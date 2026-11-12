using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsBoundedMftCase(string CaseId, string Kind, int NameLength, string RawCode,
    bool IdentityMatched, bool LogicalMatched, bool LinksMatched, bool NamesMatched, string AllocationCheck);
internal sealed record NtfsBoundedMftRangeReport(long ValidBytes, int ExpectedSlots, int VisitedSlots,
    int Decoded, int Free, int Unsupported, int Invalid, int LookupRequired, int UnknownLogical,
    int UnknownAllocated, int Pages, bool CompletedCursor, int MatchedObjects, int UnknownAllocationObjects,
    int MftRuns, int ReadCalls, long RequestedBytes, int PhysicalSectorBytes, bool LockAcquired,
    bool UnlockSucceeded, bool PostNativeStable, IReadOnlyList<NtfsBoundedMftCase> Cases);
internal sealed record NtfsBoundedMftSourceReport(string Revision, string EvidenceKind, bool GuardOwned,
    bool TokenBound, bool MapBound, bool Closed, int RejectedReads, NtfsBoundedMftRangeReport Enumeration);

// Owns a new, verified fixture context and its one locked reading epoch. The
// only public result is a safe classification/target summary after closure;
// neither parsed records nor a caller-selected physical offset can escape.
internal sealed class NtfsBoundedMftSource : IDisposable
{
    private sealed class RealBackend(NtfsVerifiedFixtureContext context) : NtfsFixtureSource.IBackend
    {
        public SafeFileHandle Volume => context.Volume;
        public NtfsRawVolumeFacts QueryFacts(bool reopen, CancellationToken token) => context.QueryFacts(reopen, token);
        public IReadOnlyList<NtfsRawTarget> CaptureFixedTargets(CancellationToken token) => context.CaptureFixedTargets(token);
        public void VerifyFixedTargets(IReadOnlyList<NtfsRawTarget> baseline, CancellationToken token) => context.VerifyFixedTargets(baseline, token);
        public void Dispose() => context.Dispose();
    }

    private readonly object gate = new(), sourceToken = new();
    private readonly NtfsFixtureSource.IBackend backend;
    private readonly bool nativeProvenance;
    private readonly Dictionary<string, NtfsRawTarget> targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NtfsKernelMatrixCase> descriptors = NtfsKernelMatrix.Cases.ToDictionary(item => item.CaseId, StringComparer.Ordinal);
    private NtfsRawVolumeFacts facts = null!;
    private NtfsRawVolumeLease? lease;
    private NtfsMftMap? map;
    private object? mapToken;
    private bool active, disposed, backendDisposed, backendDisposeSucceeded;

    private NtfsBoundedMftSource(NtfsFixtureSource.IBackend backend, bool nativeProvenance)
    { this.backend = backend; this.nativeProvenance = nativeProvenance; }

    internal static NtfsBoundedMftSource Open(string manifestPath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // This factory alone creates NativeOwnedFixture evidence. It accepts no
        // supplied volume, target list, validation flags or injected verifier.
        var context = NtfsVerifiedFixtureContext.CreateForBoundedMft(manifestPath, token);
        return Create(new RealBackend(context), true, null, token);
    }

    internal static NtfsBoundedMftSource OpenForTests(NtfsFixtureSource.IBackend backend,
        NtfsRawVolumeLease.INative native, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(backend); ArgumentNullException.ThrowIfNull(native);
        return Create(backend, false, native, token);
    }

    private static NtfsBoundedMftSource Create(NtfsFixtureSource.IBackend backend, bool real,
        NtfsRawVolumeLease.INative? native, CancellationToken cancellation)
    {
        var source = new NtfsBoundedMftSource(backend, real);
        try { source.Initialize(native, cancellation); return source; }
        catch (NtfsRawVolumeException error)
        { source.Dispose(); throw new NtfsFixtureSourceException("SourceNativeFailed", error.NativeErrorCode, error.Code); }
        catch { source.Dispose(); throw; }
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string code)
    { if (!condition) throw new NtfsFixtureSourceException(code); }

    private void Initialize(NtfsRawVolumeLease.INative? native, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var baseline = backend.CaptureFixedTargets(cancellation);
        Require(baseline != null && baseline.Count == 21, "SourceTargetScopeInvalid");
        for (int i = 0; i < 21; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var target = baseline[i];
            Require(target != null && target.Storage != null && target.ParentStorage != null &&
                descriptors.ContainsKey(target.CaseId) && targets.TryAdd(target.CaseId, target), "SourceTargetScopeInvalid");
        }
        facts = backend.QueryFacts(false, cancellation);
        Require(facts != null && facts.Geometry is { SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024 }, "SourceGeometryInvalid");
        // Reject the entire oversized stream before locking or reading even its
        // bootstrap. An accepted prefix could never establish full coverage.
        Require(facts.Geometry.MftValidDataLength >= 22 * 1024 && facts.Geometry.MftValidDataLength % 1024 == 0 &&
            facts.Geometry.MftValidDataLength / 1024 <= NtfsMftEnumerator.MaximumSlots, "SourceRangeBudget");
        Require(targets.Values.Select(item => item.Storage.Identity).Distinct().Count() == 21, "SourceTargetScopeInvalid");
        foreach (var target in targets.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            var descriptor = descriptors[target.CaseId];
            Require(target.Storage.Identity.Volume == facts.Serial && target.Storage.Identity.High == 0 && target.Storage.Identity.Low >> 48 != 0 &&
                target.ParentStorage.Identity.Volume == facts.Serial && target.ParentStorage.Identity.High == 0 && target.ParentStorage.Identity.Low >> 48 != 0 &&
                (target.Storage.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber) < (ulong)(facts.Geometry.MftValidDataLength / 1024) &&
                target.Storage.LogicalBytes == descriptor.LogicalBytes && target.Storage.AllocatedBytes >= 0 &&
                target.Storage.LinkCount == (descriptor.HardLink ? 2u : 1u) && target.Storage.Modified.HasValue && !target.Storage.WritableHandleOpen,
                "SourceTargetContextInvalid");
        }
        cancellation.ThrowIfCancellationRequested();
        lease = native == null ? NtfsRawVolumeLease.AcquireBoundedMft(backend.Volume, facts.Geometry.VolumeBytes,
            facts.Geometry.SectorBytes, facts.PhysicalSectorBytes, cancellation) :
            NtfsRawVolumeLease.AcquireBoundedMftForTests(backend.Volume, facts.Geometry.VolumeBytes,
                facts.Geometry.SectorBytes, facts.PhysicalSectorBytes, native, cancellation);
        long start;
        try { start = checked(facts.Geometry.MftStartLcn * facts.Geometry.ClusterBytes); }
        catch (OverflowException) { throw new NtfsFixtureSourceException("SourceGeometryInvalid"); }
        // A small bootstrap buffer is discarded before the enumerator allocates
        // its single managed 64-KiB page. The native 64-KiB buffer is reused.
        var raw0 = new byte[1024];
        Require(lease.ReadInto(start, raw0, cancellation) == 1024, "SourceReadCoverageInvalid");
        var parsed = NtfsMftMap.Parse(raw0, facts.Geometry, cancellation);
        Require(parsed.Status == "Mapped" && parsed.Map != null, "SourceMapInvalid");
        map = parsed.Map;
        var bootstrap = map.PlanRecord(0, cancellation);
        Require(bootstrap.Count == 1 && bootstrap[0].VolumeOffset == start && bootstrap[0].RecordOffset == 0 && bootstrap[0].Length == 1024,
            "SourceMapBindingMismatch");
        mapToken = sourceToken;
        cancellation.ThrowIfCancellationRequested();
        active = true;
    }

    private void CheckActive(object requested)
    {
        Require(active && !disposed, "SourceNotActive");
        Require(ReferenceEquals(requested, sourceToken) && ReferenceEquals(mapToken, sourceToken) && map != null && lease != null,
            "SourceMapBindingMismatch");
    }

    private int ReadRange(object requested, long offset, Memory<byte> destination, CancellationToken cancellation)
    {
        CheckActive(requested);
        cancellation.ThrowIfCancellationRequested();
        int copied = 0;
        foreach (var slice in map!.PlanRange(offset, destination.Length, cancellation))
        {
            CheckActive(requested);
            cancellation.ThrowIfCancellationRequested();
            Require(slice.RecordOffset == copied && slice.Length > 0 && slice.Length <= destination.Length - copied, "SourceReadCoverageInvalid");
            int received = lease!.ReadInto(slice.VolumeOffset, destination.Slice(copied, slice.Length), cancellation);
            Require(received == slice.Length, "SourceReadCoverageInvalid");
            copied = checked(copied + received);
        }
        Require(copied == destination.Length, "SourceReadCoverageInvalid");
        cancellation.ThrowIfCancellationRequested();
        CheckActive(requested);
        return copied;
    }

    internal NtfsBoundedMftSourceReport RunEnumeration(CancellationToken cancellation = default)
    {
        lock (gate)
        {
            CheckActive(sourceToken);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var binding = sourceToken;
                Exception? callbackFailure = null;
                NtfsMftEnumerationResult result;
                try
                {
                    result = NtfsMftEnumerator.Enumerate(map!, targets.Values.Select(target => target.Storage.Identity.Low).ToArray(),
                        (offset, destination, token) =>
                        {
                            try { return ReadRange(binding, offset, destination, token); }
                            catch (Exception error) { callbackFailure = error; throw; }
                        }, cancellation);
                }
                catch (NtfsMftEnumerationException error)
                {
                    if (callbackFailure is NtfsRawVolumeException raw) throw new NtfsFixtureSourceException("SourceNativeFailed", raw.NativeErrorCode, raw.Code);
                    if (callbackFailure is NtfsFixtureSourceException source) throw source;
                    throw new NtfsFixtureSourceException("SourceEnumerationFailed", nativeCode: error.Code);
                }
                CheckActive(binding);
                var rows = new List<NtfsBoundedMftCase>(21);
                int unknown = 0;
                foreach (var descriptor in NtfsKernelMatrix.Cases)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var target = targets[descriptor.CaseId];
                    Require(result.TargetRecords.TryGetValue(target.Storage.Identity.Low, out var record), "SourceCoverageInvalid");
                    Require(record.Reference == target.Storage.Identity.Low && record.BaseReference == 0 && !record.IsDirectory, "SourceIdentityMismatch");
                    Require(record.LogicalBytes == target.Storage.LogicalBytes, "SourceLogicalMismatch");
                    Require(NtfsRawComparison.MatchesLinks(record, target.Storage.LinkCount), "SourceLinksMismatch");
                    Require(NtfsRawComparison.MatchesNames(record, target.ParentStorage.Identity.Low, descriptor), "SourceNamesMismatch");
                    var reasons = descriptor.Kind switch { "Compressed" => NtfsLookupReason.Compressed, "Sparse" => NtfsLookupReason.Sparse, _ => NtfsLookupReason.None };
                    if (descriptor.NamedStream) reasons |= NtfsLookupReason.NamedData;
                    Require(record.LookupReasons == reasons, "SourceScopeMismatch");
                    bool known = descriptor.Kind == "Ordinary";
                    Require(known ? record.AllocatedBytes == target.Storage.AllocatedBytes : record.AllocatedBytes == null, "SourceAllocationMismatch");
                    if (!known) unknown++;
                    rows.Add(new(descriptor.CaseId, descriptor.Kind, descriptor.NameLength, "Decoded", true, true, true, true, known ? "Matched" : "Unknown"));
                }
                Require(result.CompletedCursor && result.ValidBytes == facts.Geometry.MftValidDataLength && result.TargetRecords.Count == 21 &&
                    result.VisitedSlots == result.ExpectedSlots && result.VisitedSlots == result.Decoded + result.Free + result.Unsupported + result.Invalid &&
                    rows.Count == 21 && unknown == 14 && map != null && lease != null, "SourceCoverageInvalid");
                int runs = map.Runs.Count, reads = lease.ReadCalls;
                long bytes = lease.RequestedBytes;
                Require(bytes == checked(result.ValidBytes + 1024) && reads >= result.Pages + 1 && reads <= NtfsRawVolumeLease.BoundedMftMaximumReadCalls,
                    "SourceReadCoverageInvalid");
                CloseAndVerify(cancellation);
                var enumeration = new NtfsBoundedMftRangeReport(result.ValidBytes, result.ExpectedSlots, result.VisitedSlots, result.Decoded, result.Free,
                    result.Unsupported, result.Invalid, result.LookupRequired, result.UnknownLogical, result.UnknownAllocated, result.Pages,
                    result.CompletedCursor, 21, 14, runs, reads, bytes, facts.PhysicalSectorBytes, true, true, true, rows.AsReadOnly());
                cancellation.ThrowIfCancellationRequested();
                return new("bounded-mft-source-v1", nativeProvenance ? "NativeOwnedFixture" : "SyntheticFixture", nativeProvenance,
                    true, true, true, 0, enumeration);
            }
            catch (NtfsRawVolumeException error)
            { Dispose(); throw new NtfsFixtureSourceException("SourceNativeFailed", error.NativeErrorCode, error.Code); }
            catch { Dispose(); throw; }
        }
    }

    private void CloseAndVerify(CancellationToken cancellation)
    {
        active = false;
        try
        {
            lease!.Dispose();
            cancellation.ThrowIfCancellationRequested();
            Require(lease.BufferReleaseSucceeded, "SourceBufferReleaseFailed");
            Require(lease.UnlockSucceeded, "SourceUnlockFailed");
            backend.VerifyFixedTargets(targets.Values.ToArray(), cancellation);
            cancellation.ThrowIfCancellationRequested();
            Require(backend.QueryFacts(true, cancellation) == facts, "SourceFactsChanged");
            cancellation.ThrowIfCancellationRequested();
        }
        finally { Dispose(); }
        cancellation.ThrowIfCancellationRequested();
        Require(backendDisposeSucceeded, "SourceCleanupFailed");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            active = false; disposed = true;
            try { lease?.Dispose(); }
            finally
            {
                map = null; mapToken = null; targets.Clear();
                if (!backendDisposed)
                {
                    backendDisposed = true;
                    try { backend.Dispose(); backendDisposeSucceeded = true; } catch { backendDisposeSucceeded = false; }
                }
            }
        }
    }
}
