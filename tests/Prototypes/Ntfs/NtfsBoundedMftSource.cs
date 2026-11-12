using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsBoundedMftCase(string CaseId, string Kind, int NameLength, string RawCode,
    bool IdentityMatched, bool LogicalMatched, bool LinksMatched, bool NamesMatched, string AllocationCheck);
internal sealed record NtfsBoundedMftRangeReport(long ValidBytes, int ExpectedSlots, int VisitedSlots,
    int Decoded, int Free, int Unsupported, int Invalid, int LookupRequired, int UnknownLogical,
    int UnknownAllocated, int Pages, bool CompletedCursor, int MatchedObjects, int UnknownAllocationObjects,
    int MftRuns, int ReadCalls, long RequestedBytes, int PhysicalSectorBytes, bool LockAcquired,
    bool UnlockSucceeded, bool PostNativeStable, IReadOnlyList<NtfsBoundedMftCase> Cases,
    int AllZeroInvalid, IReadOnlyList<NtfsMftCodeCount> CodeCounts);
internal sealed record NtfsBoundedMftSourceReport(string Revision, string EvidenceKind, bool GuardOwned,
    bool TokenBound, bool MapBound, bool Closed, int RejectedReads, NtfsBoundedMftRangeReport Enumeration);
internal sealed record NtfsBitmapBootstrapMetadata(long LogicalBytes, long InitializedBytes, long AllocatedBytes);
internal sealed record NtfsBitmapBootstrapObservation(string Status, string Code, int ExpectedSlots,
    int CoveredSlots, int PayloadBytes, NtfsBitmapBootstrapMetadata? NonresidentMetadata);
internal sealed record NtfsBitmapBootstrapSourceReport(string Revision, string Stage,
    NtfsBoundedMftSourceReport Range, NtfsBitmapBootstrapObservation Bitmap);
internal sealed record NtfsFixtureFastBitmapReport(string Form, int PrefixBytes, int CoveredSlots, int ReadCalls,
    long RequestedBytes, int TargetsAllocated, int ReleasedUnallocated, int ReleasedFreeHeaders, bool Corroborated);
internal sealed record NtfsFixtureFastResources(int ReadCalls, long RequestedBytes, bool Closed);
internal sealed record NtfsFixtureFastScanReport(string Revision, string Stage, NtfsBoundedMftSourceReport Range,
    NtfsFixtureFastBitmapReport Bitmap, NtfsFixtureDirectoryIndexReport Index, NtfsFixtureScannerComparison Comparison,
    NtfsFixtureFastResources Resources);

// Owns a new, verified fixture context and its one locked reading epoch. The
// only public result is a safe classification/target summary after closure;
// neither parsed records nor a caller-selected physical offset can escape.
internal sealed class NtfsBoundedMftSource : IDisposable
{
    private enum Profile { RangeOnly, BitmapObservation, FastScan }
    private sealed record CompletedRun(NtfsBoundedMftSourceReport Range, NtfsBitmapBootstrapObservation? Bitmap, NtfsFixtureFastScanReport? Fast = null);
    internal interface IFastBackend : NtfsFixtureSource.IBackend
    {
        NtfsFixtureFastContext PrepareFastContext(CancellationToken token);
        void VerifyFastContext(NtfsFixtureFastContext baseline, CancellationToken token);
        ScanResult ScanFixture(CancellationToken token);
    }
    private sealed class RealBackend(NtfsVerifiedFixtureContext context) : IFastBackend
    {
        public SafeFileHandle Volume => context.Volume;
        public NtfsRawVolumeFacts QueryFacts(bool reopen, CancellationToken token) => context.QueryFacts(reopen, token);
        public IReadOnlyList<NtfsRawTarget> CaptureFixedTargets(CancellationToken token) => context.CaptureFixedTargets(token);
        public void VerifyFixedTargets(IReadOnlyList<NtfsRawTarget> baseline, CancellationToken token) => context.VerifyFixedTargets(baseline, token);
        public void Dispose() => context.Dispose();
        public NtfsFixtureFastContext PrepareFastContext(CancellationToken token) => context.PrepareFastContext(token);
        public void VerifyFastContext(NtfsFixtureFastContext baseline, CancellationToken token) => context.VerifyFastContext(baseline, token);
        public ScanResult ScanFixture(CancellationToken token) => context.ScanFastFixture(token);
    }

    private readonly object gate = new(), sourceToken = new();
    private readonly NtfsFixtureSource.IBackend backend;
    private readonly bool nativeProvenance;
    private readonly Profile profile;
    private readonly Dictionary<string, NtfsRawTarget> targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NtfsKernelMatrixCase> descriptors = NtfsKernelMatrix.Cases.ToDictionary(item => item.CaseId, StringComparer.Ordinal);
    private NtfsRawVolumeFacts facts = null!;
    private NtfsRawVolumeLease? lease;
    private NtfsMftMap? map;
    private object? mapToken;
    private NtfsBitmapBootstrapObservation? bitmapObservation;
    private NtfsFixtureFastContext? fastContext;
    private byte[]? bitmapPrefix;
    private string prefixForm = "Unknown";
    private int prefixReadCalls;
    private long prefixRequestedBytes;
    private bool active, disposed, backendDisposed, backendDisposeSucceeded;

    private NtfsBoundedMftSource(NtfsFixtureSource.IBackend backend, bool nativeProvenance, Profile profile)
    { this.backend = backend; this.nativeProvenance = nativeProvenance; this.profile = profile; }

    internal static NtfsBoundedMftSource Open(string manifestPath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // This factory alone creates NativeOwnedFixture evidence. It accepts no
        // supplied volume, target list, validation flags or injected verifier.
        var context = NtfsVerifiedFixtureContext.CreateForBoundedMft(manifestPath, token);
        return Create(new RealBackend(context), true, Profile.RangeOnly, null, token);
    }

    internal static NtfsBoundedMftSource OpenBitmapObservation(string manifestPath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var context = NtfsVerifiedFixtureContext.CreateForBitmapObservation(manifestPath, token);
        return Create(new RealBackend(context), true, Profile.BitmapObservation, null, token);
    }

    internal static NtfsBoundedMftSource OpenFastScan(string manifestPath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Create(new RealBackend(NtfsVerifiedFixtureContext.CreateForFastScan(manifestPath, token)), true, Profile.FastScan, null, token);
    }

    internal static NtfsBoundedMftSource OpenFastScanForTests(IFastBackend backend,
        NtfsRawVolumeLease.INative native, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(backend); ArgumentNullException.ThrowIfNull(native);
        return Create(backend, false, Profile.FastScan, native, token);
    }

    internal static NtfsBoundedMftSource OpenForTests(NtfsFixtureSource.IBackend backend,
        NtfsRawVolumeLease.INative native, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(backend); ArgumentNullException.ThrowIfNull(native);
        return Create(backend, false, Profile.RangeOnly, native, token);
    }

    internal static NtfsBoundedMftSource OpenBitmapObservationForTests(NtfsFixtureSource.IBackend backend,
        NtfsRawVolumeLease.INative native, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(backend); ArgumentNullException.ThrowIfNull(native);
        return Create(backend, false, Profile.BitmapObservation, native, token);
    }

    private static NtfsBoundedMftSource Create(NtfsFixtureSource.IBackend backend, bool real, Profile profile,
        NtfsRawVolumeLease.INative? native, CancellationToken cancellation)
    {
        var source = new NtfsBoundedMftSource(backend, real, profile);
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
        if (profile == Profile.FastScan)
        {
            Require(backend is IFastBackend, "SourceFastBackendInvalid");
            fastContext = ((IFastBackend)backend).PrepareFastContext(cancellation);
            cancellation.ThrowIfCancellationRequested();
        }
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
        if (profile == Profile.FastScan)
        {
            Require(fastContext != null && fastContext.DataRootStorage.Identity.Volume == facts.Serial && fastContext.DataRootStorage.Identity.High == 0 &&
                fastContext.DataRootStorage.Identity.Low >> 48 != 0 && fastContext.DataRootStorage.LogicalBytes == -1 &&
                fastContext.DataRootStorage.Modified.HasValue && !fastContext.DataRootStorage.WritableHandleOpen && fastContext.Released.Count == 2,
                "SourceWitnessInvalid");
            var witnessSlots = new HashSet<ulong>();
            for (int i = 0; i < 2; i++)
            {
                var witness = fastContext.Released[i]; var storage = witness.Storage;
                ulong slot = storage.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber;
                Require(witness.CaseId == "Released-" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                    storage.Identity.Volume == facts.Serial && storage.Identity.High == 0 && storage.Identity.Low >> 48 is > 0 and < 65535 &&
                    storage.LogicalBytes == 0 && storage.AllocatedBytes == 0 && storage.LinkCount == 1 && storage.Modified.HasValue &&
                    !storage.WritableHandleOpen && slot < (ulong)(facts.Geometry.MftValidDataLength / 1024) && witnessSlots.Add(slot) &&
                    targets.Values.All(target => (target.Storage.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber) != slot), "SourceWitnessInvalid");
            }
        }
        cancellation.ThrowIfCancellationRequested();
        lease = profile == Profile.FastScan ? native == null ? NtfsRawVolumeLease.AcquireBitmapPrefix(backend.Volume, facts.Geometry.VolumeBytes,
            facts.Geometry.SectorBytes, facts.PhysicalSectorBytes, cancellation) : NtfsRawVolumeLease.AcquireBitmapPrefixForTests(backend.Volume,
                facts.Geometry.VolumeBytes, facts.Geometry.SectorBytes, facts.PhysicalSectorBytes, native, cancellation) :
            native == null ? NtfsRawVolumeLease.AcquireBoundedMft(backend.Volume, facts.Geometry.VolumeBytes,
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
        // Only the dedicated profile invokes the locator. It consumes this
        // same private bootstrap after reverse-map validation, and caches only
        // safe scalar metadata. No payload copy or second read is performed.
        if (profile == Profile.BitmapObservation) bitmapObservation = ObserveBitmap(raw0, cancellation);
        if (profile == Profile.FastScan) ReadBitmapPrefix(raw0, cancellation);
        cancellation.ThrowIfCancellationRequested();
        active = true;
    }

    private void ReadBitmapPrefix(byte[] raw0, CancellationToken token)
    {
        int slots = checked((int)(facts.Geometry.MftValidDataLength / 1024)), needed = (slots + 7) / 8;
        var plan = NtfsMftBitmapPrefix.Parse(raw0, facts.Geometry, slots, token);
        Require(plan.ExpectedSlots == slots && plan.LogicalPrefixBytes == needed && needed is >= 1 and <= 512 &&
            ReferenceEquals(mapToken, sourceToken), "SourceFastBitmapFailed");
        int calls = lease!.ReadCalls; long bytes = lease.RequestedBytes;
        if (plan.Status == "Resident")
        {
            Require(plan.Form == "Resident" && plan.ReadBytes == 0 && plan.ReadPlan.Count == 0, "SourceFastBitmapFailed");
            bitmapPrefix = plan.CopyPrefix(); Require(bitmapPrefix.Length == needed, "SourceFastBitmapFailed");
        }
        else if (plan.Status == "Planned")
        {
            Require(plan.Form == "Nonresident" && plan.ReadBytes == 512 && plan.ReadPlan.Count == 1 &&
                plan.ReadPlan[0] is { Length: 512, RecordOffset: 0 }, "SourceFastBitmapFailed");
            var buffer = new byte[512]; var slice = plan.ReadPlan[0];
            Require(lease.ReadInto(slice.VolumeOffset, buffer, token) == 512, "SourceReadCoverageInvalid");
            bitmapPrefix = buffer.AsSpan(0, needed).ToArray();
        }
        else throw new NtfsFixtureSourceException("SourceFastBitmapFailed", nativeCode: plan.Code);
        prefixForm = plan.Form; prefixReadCalls = lease.ReadCalls - calls; prefixRequestedBytes = lease.RequestedBytes - bytes;
        Require(prefixForm == "Resident" ? prefixReadCalls == 0 && prefixRequestedBytes == 0 :
            prefixReadCalls == 1 && prefixRequestedBytes == 512, "SourceFastBitmapFailed");
        token.ThrowIfCancellationRequested();
    }

    private NtfsBitmapBootstrapObservation ObserveBitmap(byte[] raw0, CancellationToken cancellation)
    {
        int expected = checked((int)(facts.Geometry.MftValidDataLength / 1024));
        var located = NtfsMftBitmapAttribute.Parse(raw0, facts.Geometry, expected, cancellation);
        Require(located.Status != "Invalid", "SourceBitmapInvalid");
        Require(located.ExpectedSlots == expected && located.PayloadBytes is >= 0 and <= 512 &&
            located.CoveredSlots is >= 0 && located.CoveredSlots <= expected, "SourceBitmapShapeInvalid");
        NtfsBitmapBootstrapMetadata? metadata = null;
        string status = located.Status;
        string code = located.Code;
        switch (status)
        {
            case "Missing":
                Require(located.Code == "BitmapMissing" && located.PayloadBytes == 0 && located.CoveredSlots == 0 && located.NonresidentMetadata == null,
                    "SourceBitmapShapeInvalid");
                break;
            case "ResidentComplete":
            case "ResidentPartial":
                int capacity = Math.Min(expected, checked(located.PayloadBytes * 8));
                Require(located.CoveredSlots == capacity && located.NonresidentMetadata == null &&
                    (status == "ResidentComplete" ? located.Code == "BitmapResidentComplete" && capacity == expected :
                        located.Code == "BitmapResidentPartial" && capacity < expected), "SourceBitmapShapeInvalid");
                if (status == "ResidentComplete") status = "ResidentCapacityOnly";
                break;
            case "Deferred":
                Require(located.PayloadBytes == 0 && located.CoveredSlots == 0 && located.Code is "BitmapSeedUnsupported" or "BitmapExtent" or
                    "BitmapMultiple" or "BitmapNamed" or "BitmapFlags" or "BitmapPayloadBudget" or "BitmapNonresident", "SourceBitmapShapeInvalid");
                if (located.Code == "BitmapNonresident")
                {
                    var lengths = located.NonresidentMetadata;
                    Require(lengths != null && lengths.LogicalBytes is >= 0 and <= 512 && lengths.InitializedBytes >= 0 &&
                        lengths.InitializedBytes <= lengths.LogicalBytes && lengths.AllocatedBytes >= lengths.LogicalBytes &&
                        lengths.AllocatedBytes % 4096 == 0 && lengths.AllocatedBytes <= facts.Geometry.VolumeBytes, "SourceBitmapShapeInvalid");
                    metadata = new(lengths.LogicalBytes, lengths.InitializedBytes, lengths.AllocatedBytes);
                }
                else Require(located.NonresidentMetadata == null, "SourceBitmapShapeInvalid");
                // A capacity ceiling says nothing about residency by itself.
                // Publish only the parser's explicitly observed branch, never
                // an unknown-form generic budget or a readable descriptor.
                if (located.Code == "BitmapPayloadBudget")
                {
                    code = located.Form switch
                    {
                        "Resident" => "BitmapResidentPayloadBudget",
                        "Nonresident" => "BitmapNonresidentPayloadBudget",
                        _ => throw new NtfsFixtureSourceException("SourceBitmapShapeInvalid")
                    };
                }
                break;
            default: throw new NtfsFixtureSourceException("SourceBitmapShapeInvalid");
        }
        cancellation.ThrowIfCancellationRequested();
        return new(status, code, expected, located.CoveredSlots, located.PayloadBytes, metadata);
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

    internal NtfsBoundedMftSourceReport RunEnumeration(CancellationToken cancellation = default) =>
        RunCore(Profile.RangeOnly, cancellation).Range;

    internal NtfsBitmapBootstrapSourceReport RunBitmapObservation(CancellationToken cancellation = default)
    {
        var completed = RunCore(Profile.BitmapObservation, cancellation);
        Require(completed.Bitmap != null, "SourceBitmapShapeInvalid");
        cancellation.ThrowIfCancellationRequested();
        return new("bitmap-bootstrap-source-v1", "ObservedShapeOnly", completed.Range, completed.Bitmap);
    }

    internal NtfsFixtureFastScanReport RunFastScan(CancellationToken cancellation = default)
    {
        var completed = RunCore(Profile.FastScan, cancellation);
        Require(completed.Fast != null, "SourceFastBackendInvalid"); cancellation.ThrowIfCancellationRequested(); return completed.Fast;
    }

    private bool AllocatedSlot(object binding, ulong slot)
    {
        CheckActive(binding); Require(bitmapPrefix != null && slot < (ulong)(facts.Geometry.MftValidDataLength / 1024), "SourceFastBitmapFailed");
        return (bitmapPrefix[checked((int)(slot / 8))] & (1 << (int)(slot % 8))) != 0;
    }

    private CompletedRun RunCore(Profile requestedProfile, CancellationToken cancellation)
    {
        lock (gate)
        {
            CheckActive(sourceToken);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                Require(profile == requestedProfile, "SourceProfileMismatch");
                Require(profile != Profile.BitmapObservation || bitmapObservation != null, "SourceBitmapShapeInvalid");
                var binding = sourceToken;
                Exception? callbackFailure = null;
                NtfsMftEnumerationResult result;
                NtfsMftIndexCollection? collection = null;
                try
                {
                    NtfsMftReadInto reader = (offset, destination, token) =>
                        {
                            try { return ReadRange(binding, offset, destination, token); }
                            catch (Exception error) { callbackFailure = error; throw; }
                        };
                    var references = targets.Values.Select(target => target.Storage.Identity.Low).ToArray();
                    if (profile == Profile.FastScan) { collection = NtfsMftIndexCollector.Collect(map!, references, reader, cancellation); result = collection.Range; }
                    else result = NtfsMftEnumerator.Enumerate(map!, references, reader, cancellation);
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
                NtfsFixtureDirectoryIndexResult? index = null;
                if (profile == Profile.FastScan)
                {
                    Require(collection != null && fastContext != null && bitmapPrefix != null, "SourceFastBackendInvalid");
                    foreach (var target in targets.Values) Require(AllocatedSlot(binding, target.Storage.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber), "SourceBitmapCorroborationFailed");
                    foreach (var witness in fastContext.Released)
                    {
                        ulong slot = witness.Storage.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber;
                        Require(!AllocatedSlot(binding, slot) && collection.Slots[checked((int)slot)].Status == NtfsDecodeStatus.Free &&
                            collection.Slots[checked((int)slot)].HeaderReference == witness.Storage.Identity.Low + (1UL << 48), "SourceBitmapCorroborationFailed");
                    }
                    try { index = NtfsFixtureDirectoryIndex.Build(collection.Records, slot => AllocatedSlot(binding, slot), fastContext.DataRootStorage,
                        targets.Values.ToArray(), cancellation); }
                    catch (NtfsFixtureDirectoryIndexException error) { throw new NtfsFixtureSourceException("SourceFastIndexFailed", nativeCode: error.Code); }
                }
                int runs = map.Runs.Count, totalReads = lease.ReadCalls, reads = totalReads - prefixReadCalls;
                long totalBytes = lease.RequestedBytes, bytes = totalBytes - prefixRequestedBytes;
                Require(bytes == checked(result.ValidBytes + 1024) && reads >= result.Pages + 1 && reads <= NtfsRawVolumeLease.BoundedMftMaximumReadCalls,
                    "SourceReadCoverageInvalid");
                var observation = bitmapObservation;
                int prefixBytes = bitmapPrefix?.Length ?? 0;
                string observedPrefixForm = prefixForm;
                int bitmapReads = prefixReadCalls; long bitmapBytes = prefixRequestedBytes;
                var comparison = CloseAndVerify(cancellation, index);
                var enumeration = new NtfsBoundedMftRangeReport(result.ValidBytes, result.ExpectedSlots, result.VisitedSlots, result.Decoded, result.Free,
                    result.Unsupported, result.Invalid, result.LookupRequired, result.UnknownLogical, result.UnknownAllocated, result.Pages,
                    result.CompletedCursor, 21, 14, runs, reads, bytes, facts.PhysicalSectorBytes, true, true, true, rows.AsReadOnly(),
                    result.AllZeroInvalid, result.CodeCounts);
                cancellation.ThrowIfCancellationRequested();
                var range = new NtfsBoundedMftSourceReport("bounded-mft-source-v2", nativeProvenance ? "NativeOwnedFixture" : "SyntheticFixture", nativeProvenance,
                    true, true, true, 0, enumeration);
                if (profile == Profile.FastScan)
                {
                    Require(index != null && comparison != null && comparison.Matched && totalReads <= 256 && totalBytes <= 4195840,
                        "SourceFastComparisonFailed");
                    var bitmap = new NtfsFixtureFastBitmapReport(observedPrefixForm, prefixBytes, result.ExpectedSlots, bitmapReads, bitmapBytes, 21, 2, 2, true);
                    var fast = new NtfsFixtureFastScanReport("fixture-fast-scan-v1", "ControlledFixtureCompared", range, bitmap, index.Report,
                        comparison, new(totalReads, totalBytes, true));
                    cancellation.ThrowIfCancellationRequested(); return new(range, observation, fast);
                }
                return new(range, observation);
            }
            catch (NtfsRawVolumeException error)
            { Dispose(); throw new NtfsFixtureSourceException("SourceNativeFailed", error.NativeErrorCode, error.Code); }
            catch { Dispose(); throw; }
        }
    }

    private NtfsFixtureScannerComparison? CloseAndVerify(CancellationToken cancellation, NtfsFixtureDirectoryIndexResult? index = null)
    {
        active = false;
        NtfsFixtureScannerComparison? comparison = null;
        try
        {
            lease!.Dispose();
            cancellation.ThrowIfCancellationRequested();
            Require(lease.BufferReleaseSucceeded, "SourceBufferReleaseFailed");
            Require(lease.UnlockSucceeded, "SourceUnlockFailed");
            if (profile == Profile.FastScan)
            {
                Require(backend is IFastBackend && index != null && fastContext != null, "SourceFastBackendInvalid");
                var scan = ((IFastBackend)backend).ScanFixture(cancellation);
                try { comparison = NtfsFixtureDirectoryIndex.Compare(index, scan, cancellation); }
                catch (NtfsFixtureDirectoryIndexException error) { throw new NtfsFixtureSourceException("SourceFastIndexFailed", nativeCode: error.Code); }
                Require(comparison.Matched && comparison.ScannerComplete, "SourceFastComparisonFailed");
                ((IFastBackend)backend).VerifyFastContext(fastContext, cancellation);
            }
            backend.VerifyFixedTargets(targets.Values.ToArray(), cancellation);
            cancellation.ThrowIfCancellationRequested();
            Require(backend.QueryFacts(true, cancellation) == facts, "SourceFactsChanged");
            cancellation.ThrowIfCancellationRequested();
        }
        finally { Dispose(); }
        cancellation.ThrowIfCancellationRequested();
        Require(backendDisposeSucceeded, "SourceCleanupFailed");
        return comparison;
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
                map = null; mapToken = null; bitmapObservation = null; fastContext = null;
                if (bitmapPrefix != null) Array.Clear(bitmapPrefix); bitmapPrefix = null; targets.Clear();
                prefixForm = "Unknown"; prefixReadCalls = 0; prefixRequestedBytes = 0;
                if (!backendDisposed)
                {
                    backendDisposed = true;
                    try { backend.Dispose(); backendDisposeSucceeded = true; } catch { backendDisposeSucceeded = false; }
                }
            }
        }
    }
}
