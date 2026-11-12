using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed class NtfsFixtureSourceException : IOException
{
    internal string Code { get; }
    internal int NativeErrorCode { get; }
    internal string NativeCode { get; }
    internal NtfsFixtureSourceException(string code, int nativeErrorCode = 0, string nativeCode = "None") : base(code)
    { Code = code; NativeErrorCode = nativeErrorCode; NativeCode = nativeCode; }
}

// Tokens have no serializable identity. Possession alone is insufficient: every
// use checks the exact owning source instance, its current map and active state.
internal sealed class NtfsFixtureSourceToken { }
internal sealed class NtfsFixtureSourceReadResult
{
    private readonly NtfsFixtureSource owner;
    internal string CaseId { get; }
    internal NtfsFixtureSourceReadResult(NtfsFixtureSource owner, string caseId)
    { this.owner = owner; CaseId = caseId; }
    internal NtfsRecord Value => owner.GetValue(this);
}

internal sealed record NtfsFixtureSourceReport(string Revision, string EvidenceKind, bool GuardOwned,
    bool TokenBound, bool MapBound, bool Closed, int RejectedReads, NtfsRawComparisonReport Comparison);

// A source owner for the finite, locally generated fixture only. No VSS/live
// factory, arbitrary offset/path API, production scanner or simulated proof bool.
internal sealed class NtfsFixtureSource : IDisposable
{
    internal interface IBackend : IDisposable
    {
        SafeFileHandle Volume { get; }
        NtfsRawVolumeFacts QueryFacts(bool reopen, CancellationToken token);
        IReadOnlyList<NtfsRawTarget> CaptureFixedTargets(CancellationToken token);
        void VerifyFixedTargets(IReadOnlyList<NtfsRawTarget> baseline, CancellationToken token);
    }
    private sealed class RealBackend(NtfsVerifiedFixtureContext context) : IBackend
    {
        public SafeFileHandle Volume => context.Volume;
        public NtfsRawVolumeFacts QueryFacts(bool reopen, CancellationToken token) => context.QueryFacts(reopen, token);
        public IReadOnlyList<NtfsRawTarget> CaptureFixedTargets(CancellationToken token) => context.CaptureFixedTargets(token);
        public void VerifyFixedTargets(IReadOnlyList<NtfsRawTarget> baseline, CancellationToken token) => context.VerifyFixedTargets(baseline, token);
        public void Dispose() => context.Dispose();
    }

    private readonly object gate = new();
    private readonly IBackend backend;
    private readonly bool nativeProvenance;
    private readonly NtfsFixtureSourceToken token = new();
    private readonly Dictionary<string, NtfsRawTarget> targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NtfsRecord> records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NtfsFixtureSourceReadResult> results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NtfsKernelMatrixCase> descriptors = NtfsKernelMatrix.Cases.ToDictionary(item => item.CaseId, StringComparer.Ordinal);
    private NtfsRawVolumeFacts facts = null!;
    private NtfsRawVolumeLease? lease;
    private NtfsMftMap? map;
    private NtfsFixtureSourceToken? mapToken;
    private bool active, disposed, backendDisposed, backendDisposeSucceeded;
    private int rejectedReads;

    private NtfsFixtureSource(IBackend backend, bool nativeProvenance)
    { this.backend = backend; this.nativeProvenance = nativeProvenance; }
    internal NtfsFixtureSourceToken Token => token;

    internal static NtfsFixtureSource Open(string manifestPath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // This is the sole real factory. Callers supply neither a handle nor
        // target paths, snapshots, injected native operations, or a proof flag.
        var context = NtfsVerifiedFixtureContext.Create(manifestPath, token);
        return Create(new RealBackend(context), true, null, token);
    }

    // Explicitly synthetic: even perfect fake operations cannot produce the
    // NativeOwnedFixture/GuardOwned evidence accepted by the real CLI wrapper.
    internal static NtfsFixtureSource OpenForTests(IBackend backend, NtfsRawVolumeLease.INative native,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(backend); ArgumentNullException.ThrowIfNull(native);
        return Create(backend, false, native, token);
    }

    private static NtfsFixtureSource Create(IBackend backend, bool isNative, NtfsRawVolumeLease.INative? native,
        CancellationToken cancellation)
    {
        var source = new NtfsFixtureSource(backend, isNative);
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
            var target = baseline![i];
            Require(target != null && target.Storage != null && target.ParentStorage != null &&
                descriptors.ContainsKey(target.CaseId) && targets.TryAdd(target.CaseId, target), "SourceTargetScopeInvalid");
        }
        facts = backend.QueryFacts(false, cancellation);
        Require(facts != null && facts.Geometry is { SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024 }, "SourceGeometryInvalid");
        Require(targets.Values.Select(item => item.Storage.Identity).Distinct().Count() == 21, "SourceTargetScopeInvalid");
        foreach (var target in targets.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            var descriptor = descriptors[target.CaseId];
            Require(target.Storage != null && target.ParentStorage != null &&
                target.Storage.Identity.Volume == facts.Serial && target.Storage.Identity.High == 0 && target.Storage.Identity.Low >> 48 != 0 &&
                target.ParentStorage.Identity.Volume == facts.Serial && target.ParentStorage.Identity.High == 0 && target.ParentStorage.Identity.Low >> 48 != 0 &&
                target.Storage.LogicalBytes == descriptor.LogicalBytes && target.Storage.AllocatedBytes >= 0 &&
                target.Storage.LinkCount == (descriptor.HardLink ? 2u : 1u) && target.Storage.Modified.HasValue && !target.Storage.WritableHandleOpen,
                "SourceTargetContextInvalid");
        }
        cancellation.ThrowIfCancellationRequested();
        lease = native == null ? NtfsRawVolumeLease.Acquire(backend.Volume, facts.Geometry.VolumeBytes,
            facts.Geometry.SectorBytes, facts.PhysicalSectorBytes, cancellation) :
            NtfsRawVolumeLease.AcquireForTests(backend.Volume, facts.Geometry.VolumeBytes,
                facts.Geometry.SectorBytes, facts.PhysicalSectorBytes, native, cancellation);
        long start;
        try { start = checked(facts.Geometry.MftStartLcn * facts.Geometry.ClusterBytes); }
        catch (OverflowException) { throw new NtfsFixtureSourceException("SourceGeometryInvalid"); }
        byte[] raw0 = lease.Read(start, facts.Geometry.RecordBytes, cancellation);
        var parsed = NtfsMftMap.Parse(raw0, facts.Geometry, cancellation);
        Require(parsed.Status == "Mapped" && parsed.Map != null, "SourceMapInvalid");
        map = parsed.Map!;
        var bootstrap = map.PlanRecord(0, cancellation);
        Require(bootstrap.Count == 1 && bootstrap[0].VolumeOffset == start && bootstrap[0].RecordOffset == 0 &&
            bootstrap[0].Length == facts.Geometry.RecordBytes, "SourceMapBindingMismatch");
        mapToken = token;
        cancellation.ThrowIfCancellationRequested();
        active = true;
    }

    [DoesNotReturn]
    private void RejectRequest(string code)
    {
        if (active && rejectedReads < 128) rejectedReads++;
        throw new NtfsFixtureSourceException(code);
    }
    private void CheckActive(NtfsFixtureSourceToken requested)
    {
        if (!active || disposed) RejectRequest("SourceNotActive");
        if (!ReferenceEquals(requested, token)) RejectRequest("SourceTokenMismatch");
        if (map == null || !ReferenceEquals(mapToken, token)) RejectRequest("SourceMapBindingMismatch");
    }

    internal NtfsFixtureSourceReadResult ReadKnown(NtfsFixtureSourceToken requested, string caseId,
        CancellationToken cancellation = default)
    {
        lock (gate)
        {
            CheckActive(requested);
            if (caseId == null) RejectRequest("SourceCaseInvalid");
            if (!targets.TryGetValue(caseId!, out var target)) RejectRequest("SourceCaseInvalid");
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (results.TryGetValue(caseId!, out var cached)) return cached;
                var descriptor = descriptors[caseId!];
                ulong slot = target!.Storage.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber;
                var raw = new byte[facts.Geometry.RecordBytes];
                int copied = 0;
                foreach (var slice in map!.PlanRecord(slot, cancellation))
                {
                    cancellation.ThrowIfCancellationRequested();
                    Require(slice.RecordOffset == copied && slice.Length > 0 && slice.Length <= raw.Length - copied, "SourceReadCoverageInvalid");
                    byte[] bytes = lease!.Read(slice.VolumeOffset, slice.Length, cancellation);
                    bytes.CopyTo(raw, slice.RecordOffset); copied += bytes.Length;
                }
                Require(copied == raw.Length, "SourceReadCoverageInvalid");
                var decoded = NtfsRecordDecoder.Decode(raw, facts.Geometry.SectorBytes, token: cancellation,
                    recordNumber: slot, clusterSize: facts.Geometry.ClusterBytes);
                Require(decoded.Status == NtfsDecodeStatus.Decoded && decoded.Record != null, "SourceStrictDecodeFailed");
                var record = decoded.Record!;
                Require(record.Reference == target.Storage.Identity.Low && record.BaseReference == 0 && !record.IsDirectory, "SourceIdentityMismatch");
                Require(record.LogicalBytes == target.Storage.LogicalBytes, "SourceLogicalMismatch");
                Require(NtfsRawComparison.MatchesLinks(record, target.Storage.LinkCount), "SourceLinksMismatch");
                Require(NtfsRawComparison.MatchesNames(record, target.ParentStorage.Identity.Low, descriptor), "SourceNamesMismatch");
                var reason = descriptor.Kind switch { "Compressed" => NtfsLookupReason.Compressed, "Sparse" => NtfsLookupReason.Sparse, _ => NtfsLookupReason.None };
                if (descriptor.NamedStream) reason |= NtfsLookupReason.NamedData;
                Require(record.LookupReasons == reason, "SourceScopeMismatch");
                Require(descriptor.Kind == "Ordinary" ? record.AllocatedBytes == target.Storage.AllocatedBytes : record.AllocatedBytes == null,
                    "SourceAllocationMismatch");
                cancellation.ThrowIfCancellationRequested();
                var result = new NtfsFixtureSourceReadResult(this, caseId!);
                records.Add(caseId!, record); results.Add(caseId!, result);
                return result;
            }
            catch (NtfsRawVolumeException error)
            { Dispose(); throw new NtfsFixtureSourceException("SourceNativeFailed", error.NativeErrorCode, error.Code); }
            catch { Dispose(); throw; }
        }
    }

    internal void ValidateResult(NtfsFixtureSourceToken requested, NtfsFixtureSourceReadResult result)
    {
        lock (gate)
        {
            CheckActive(requested);
            if (result == null || !results.TryGetValue(result.CaseId, out var current) || !ReferenceEquals(current, result))
                RejectRequest("SourceResultMismatch");
        }
    }
    internal NtfsRecord GetValue(NtfsFixtureSourceReadResult result)
    {
        lock (gate)
        { ValidateResult(token, result); return records[result.CaseId]; }
    }

    internal NtfsFixtureSourceReport RunFixedComparison(NtfsKernelMatrixReport control, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            CheckActive(token);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var kernel = ValidateControl(control);
                var rows = new List<NtfsRawComparisonCase>(21);
                int unknown = 0;
                foreach (var descriptor in NtfsKernelMatrix.Cases)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var result = ReadKnown(token, descriptor.CaseId, cancellation);
                    ValidateResult(token, result);
                    var record = result.Value;
                    bool known = descriptor.Kind == "Ordinary";
                    if (!known) unknown++;
                    rows.Add(new(descriptor.CaseId, descriptor.Kind, descriptor.NameLength, kernel[descriptor.CaseId].Code,
                        "Decoded", true, true, true, true, known ? "Matched" : "Unknown"));
                }
                Require(records.Count == 21 && rows.Count == 21 && unknown == 14 && map != null && lease != null, "SourceCoverageInvalid");
                int runs = map!.Runs.Count, reads = lease!.ReadCalls;
                long bytes = lease.RequestedBytes;
                cancellation.ThrowIfCancellationRequested();
                CloseAndVerify(cancellation);
                var comparison = new NtfsRawComparisonReport("raw-records-v1", 21, control.Requests, control.Decoded, control.DecodeFailures,
                    22, 22, 21, 14, runs, reads, bytes, facts.PhysicalSectorBytes, true, true, true, rows.AsReadOnly());
                cancellation.ThrowIfCancellationRequested();
                return new("fixture-source-v1", nativeProvenance ? "NativeOwnedFixture" : "SyntheticFixture", nativeProvenance,
                    true, true, true, rejectedReads, comparison);
            }
            catch (NtfsRawVolumeException error)
            { Dispose(); throw new NtfsFixtureSourceException("SourceNativeFailed", error.NativeErrorCode, error.Code); }
            catch { Dispose(); throw; }
        }
    }

    private Dictionary<string, NtfsKernelMatrixRow> ValidateControl(NtfsKernelMatrixReport control)
    {
        Require(control != null && control.Revision == NtfsKernelMatrix.Revision && control.Objects == 21 && control.Requests == 63 &&
            control.Rows != null && control.Rows.Count == 63 && control.Decoded is >= 0 and <= 63 &&
            control.DecodeFailures == 63 - control.Decoded && control.Captured == 0 && control.CaptureFailures == 0 &&
            control.CaptureLimited == control.DecodeFailures, "SourceControlInvalid");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var flushed = new Dictionary<string, NtfsKernelMatrixRow>(StringComparer.Ordinal);
        int decoded = 0;
        for (int i = 0; i < 63; i++)
        {
            var row = control!.Rows[i];
            Require(row != null && NtfsKernelMatrix.ReadStages.Contains(row.Stage), "SourceControlInvalid");
            string suffix = "-" + row!.Stage;
            Require(row.CaseId != null && row.CaseId.EndsWith(suffix, StringComparison.Ordinal), "SourceControlInvalid");
            string id = row.CaseId![..^suffix.Length];
            Require(descriptors.TryGetValue(id, out var descriptor) && row.Kind == descriptor.Kind && row.NameLength == descriptor.NameLength &&
                seen.Add(row.CaseId) && ValidKernelCode(row.Status, row.Code), "SourceControlInvalid");
            if (row.Status == "Decoded") decoded++;
            if (row.Stage == "Flushed") flushed.Add(id, row);
        }
        Require(decoded == control!.Decoded && flushed.Count == 21, "SourceControlInvalid");
        return flushed;
    }
    private static bool ValidKernelCode(string status, string code) => status switch
    {
        "Decoded" => code == "Decoded", "Free" => code == "FreeRecord",
        "Invalid" => code is "RecordNumber" or "RecordLength" or "Signature" or "RecordHeader" or "FixupMismatch" or "Sequence" or
            "AttributeHeader" or "AttributeLength" or "DuplicateAttributeId" or "AttributeName" or "ResidentBounds" or "MappingBounds" or
            "MappingPairs" or "SparseMappingFlags" or "StandardInformation" or "FileName" or "DataSize" or "MissingEndMarker" or "AllocationGeometry",
        "Unsupported" => code is "NtfsVersion" or "FixupStride" or "ClusterGeometry" or "RecordFlags" or "AttributeLimit" or "AttributeForm" or
            "AttributeFlags" or "MetadataFlags" or "NameLimit" or "MultipleUnnamedData" or "AttributeType", _ => false
    };

    private void CloseAndVerify(CancellationToken cancellation)
    {
        active = false;
        try
        {
            lease!.Dispose();
            Require(lease.UnlockSucceeded, "SourceUnlockFailed");
            cancellation.ThrowIfCancellationRequested();
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
                map = null; mapToken = null; records.Clear(); results.Clear();
                if (!backendDisposed)
                {
                    backendDisposed = true;
                    try { backend.Dispose(); backendDisposeSucceeded = true; } catch { backendDisposeSucceeded = false; }
                }
            }
        }
    }
}
