using System.Diagnostics;
using DiskAtlas.NtfsPrototype;

namespace DiskAtlas;

internal sealed record NtfsPersonalScanResult(DiskEntry Root, TimeSpan Elapsed, DateTimeOffset CapturedAt)
{
    internal long ExcludedReparseEntries { get; init; }
    internal IReadOnlyList<NtfsReparseBoundary> Exclusions { get; init; } = [];
}

internal static class NtfsPersonalScan
{
    // Synchronous on the dedicated MTA owner thread. Only bounded Native lookups
    // use worker tasks; no COM owner crosses an await boundary.
    internal static NtfsPersonalScanResult Run(string path, Action<FastScanWireStage> stage, CancellationToken token,
        Action<NtfsPersonalSnapshotSource, DiskEntry, CancellationToken>? beforeCleanupValidation = null)
    {
        var watch = Stopwatch.StartNew();
        NtfsPersonalSnapshotSource? source = null;
        DiskEntry? tree = null;
        DateTimeOffset captured = default;
        NtfsReparseScope? boundary = null;
        Exception? failure = null;
        bool cleaned = true;
        FastScanWireStage lastStage = FastScanWireStage.Preparing;
        FastScanWireStage failedStage = FastScanWireStage.Preparing;
        FastScanFailureContext? rawFailure = null;
        var progressGate = new object();
        void Stage(FastScanWireStage value)
        {
            lock (progressGate)
            {
                if (lastStage == value) return;
                lastStage = value; stage(value);
            }
        }
        try
        {
            token.ThrowIfCancellationRequested(); Stage(FastScanWireStage.Snapshot);
            source = NtfsPersonalSnapshotSource.Create(path, token, NtfsScanLimits.Large);
            captured = new DateTimeOffset(DateTime.SpecifyKind(source.SnapshotCreatedUtc, DateTimeKind.Utc));
            Stage(FastScanWireStage.Reading);
            var reader = source.OpenReader(token);
            FastScanFailureDetail readPhase = FastScanFailureDetail.MftMetadataRead;
            int PhysicalRead(long offset, Memory<byte> destination, CancellationToken cancellation)
            {
                // Capture only the trusted reader's fixed category and Win32 code
                // before parser callback boundaries deliberately sanitize exceptions.
                try { return reader.ReadInto(offset, destination, cancellation); }
                catch (NtfsVssProbeException error)
                {
                    var detail = ClassifyDetail(error);
                    rawFailure = new(FastScanWireStage.Reading,
                        detail == FastScanFailureDetail.None ? readPhase : detail,
                        error.Error == 0 ? null : error.Error);
                    throw;
                }
            }
            var geometry = reader.Facts(token).Geometry;
            if (geometry is not { SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024 })
                throw new FastScanUnavailableException(FastScanWireError.Unsupported, false);
            byte[] bootstrap = new byte[1024];
            try
            {
                if (PhysicalRead(checked(geometry.MftStartLcn * geometry.ClusterBytes), bootstrap, token) != bootstrap.Length)
                    throw new FastScanUnavailableException(FastScanWireError.ReadFailed, false);
                var parsed = NtfsMftMetadataResolver.Resolve(bootstrap, geometry, PhysicalRead, token);
                if (parsed.Metadata == null || parsed.Status != "Resolved")
                {
                    var detail = parsed.Status == "LimitExceeded" ? FastScanFailureDetail.MftMetadataBudget :
                        parsed.Status == "Unsupported" ? FastScanFailureDetail.MftMappingUnsupported :
                        parsed.Status == "ReadFailed" ? FastScanFailureDetail.MftMetadataRead : FastScanFailureDetail.MftMetadataUnsupported;
                    var code = parsed.Status switch { "Unsupported" => FastScanWireError.Unsupported, "LimitExceeded" => FastScanWireError.LimitExceeded,
                        "ReadFailed" => FastScanWireError.ReadFailed, _ => FastScanWireError.SourceChanged };
                    throw new FastScanUnavailableException(code, false,
                        new(FastScanWireStage.Reading, detail));
                }
                var metadata = parsed.Metadata;
                var map = metadata.Map;
                // Validate the volume's bootstrap before enumerating a potentially
                // large namespace. An unsupported MFT layout should fail promptly.
                Stage(FastScanWireStage.Discovering);
                var observedScope = NtfsSnapshotNamespace.Read(source.CreateNamespaceBackend(), token, source.Limits);
                if (!observedScope.Report.CompleteWithinBoundary || observedScope.RootIdentity != source.RootStorage.Identity)
                    throw new FastScanUnavailableException(FastScanWireError.SourceChanged, false);
                boundary = NtfsReparsePolicy.Filter(observedScope, token);
                var scope = boundary.Included;
                Stage(FastScanWireStage.Reading);
                readPhase = FastScanFailureDetail.MftBitmapRead;
                var bits = NtfsMftBitmapSelection.Parse(metadata,
                    scope.Entries.Select(e => e.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber).Distinct().ToArray(), token, source.Limits)
                    .Read(PhysicalRead, token);
                readPhase = FastScanFailureDetail.MftDataRead;
                int Read(long offset, Memory<byte> destination, CancellationToken cancellation)
                {
                    int copied = 0;
                    foreach (var slice in map.PlanRange(offset, destination.Length, cancellation))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (slice.RecordOffset != copied || slice.Length <= 0 || slice.Length > destination.Length - copied ||
                            PhysicalRead(slice.VolumeOffset, destination.Slice(copied, slice.Length), cancellation) != slice.Length)
                            throw new FastScanUnavailableException(FastScanWireError.ReadFailed, false);
                        copied += slice.Length;
                    }
                    if (copied != destination.Length) throw new FastScanUnavailableException(FastScanWireError.ReadFailed, false);
                    return copied;
                }
                NtfsSnapshotNativeSize Lookup(NtfsSnapshotSizeRequest request, CancellationToken cancellation)
                {
                    Stage(FastScanWireStage.Supplementing);
                    return source.LookupSize(scope, request, cancellation);
                }
                var index = NtfsSnapshotSizeIndex.BuildAsync(scope, map, Read, bits.IsAllocated, Lookup, token).GetAwaiter().GetResult();
                Stage(FastScanWireStage.Building);
                tree = NtfsSnapshotSizeIndex.Materialize(index, path, token, requireModified: true);
                tree.Storage = source.RootStorage;
                tree.Modified = source.RootStorage.Modified ?? throw new FastScanUnavailableException(FastScanWireError.SourceChanged, false);
                // Optional internal acceptance observer. It can add validation or fail
                // the attempt, never replace its native source or bypass strict reads.
                beforeCleanupValidation?.Invoke(source, tree, token);
                NtfsReparsePolicy.ApplyIncomplete(tree, boundary.IncompleteDirectories, token);
            }
            finally { Array.Clear(bootstrap); }
            token.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            failure = ex;
            failedStage = lastStage;
            if (ex is NtfsPersonalSnapshotOpenException open) cleaned = open.CleanupConfirmed;
        }
        finally
        {
            if (source != null)
            {
                try { Stage(FastScanWireStage.Cleanup); } catch { }
                try { cleaned = source.Cleanup(); } catch { cleaned = false; }
                try { source.Dispose(); } catch { cleaned = false; }
            }
        }
        var context = rawFailure ?? new FastScanFailureContext(failedStage, ClassifyDetail(failure), failure switch
        {
            NtfsPersonalSnapshotOpenException value when value.NativeError != 0 => value.NativeError,
            NtfsVssProbeException value when value.Error != 0 => value.Error,
            _ => null
        });
        if (!cleaned) throw new FastScanUnavailableException(FastScanWireError.CleanupUnconfirmed, false, context);
        token.ThrowIfCancellationRequested();
        if (failure != null)
        {
            if (failure is FastScanUnavailableException available) throw new FastScanUnavailableException(
                context.Detail == FastScanFailureDetail.RawRequestLimit ? FastScanWireError.LimitExceeded : available.Code,
                true, rawFailure ?? available.Context ?? context);
            if (failure is NtfsPersonalSnapshotOpenException open)
                throw new FastScanUnavailableException(open.Code switch
                {
                    "VssPersonalElevation" => FastScanWireError.PermissionDenied,
                    "VssPersonalPath" or "VssPersonalVolume" or "VssRawGeometry" => FastScanWireError.Unsupported,
                    "VssCancelled" => FastScanWireError.Cancelled,
                    _ => FastScanWireError.SnapshotFailed
                }, true, context);
            throw new FastScanUnavailableException(ClassifyFailure(failure, context), true, context);
        }
        if (tree == null) throw new FastScanUnavailableException(FastScanWireError.InternalError, true);
        return new(tree, watch.Elapsed, captured)
        {
            ExcludedReparseEntries = boundary?.ExcludedCount ?? 0,
            Exclusions = boundary?.Details ?? []
        };
    }

    internal static FastScanWireError ClassifyFailure(Exception error, FastScanFailureContext? context = null)
    {
        if (context?.Detail == FastScanFailureDetail.RawRequestLimit) return FastScanWireError.LimitExceeded;
        string code = error switch
        {
            NtfsSnapshotNamespaceException value => value.Code,
            NtfsSnapshotSizeException value => value.Code,
            NtfsMftBitmapSelectionException value => value.Code,
            NtfsVssProbeException value => value.Code,
            _ => ""
        };
        return code switch
        {
            "NamespaceEntryBudget" or "NamespaceNameBudget" or "NamespacePathBudget" or "NamespaceQueryBudget" or
            "SnapshotSizeRawBudget" or "SnapshotSizeNativeBudget" or "SnapshotSizeNativeLimit" or "SnapshotSizeProjectionBudget" or
            "BitmapSelectionPageBudget" or "BitmapSelectionSlotBudget" or "BitmapSelectionRunBudget" or "VssRawBudget" => FastScanWireError.LimitExceeded,
            "SourceConsistencyFailed" => FastScanWireError.SourceChanged,
            _ => FastScanWireError.ReadFailed
        };
    }

    internal static FastScanFailureDetail ClassifyDetail(Exception? error)
    {
        string? code = error switch
        {
            NtfsSnapshotNamespaceException value => value.Code,
            NtfsSnapshotSizeException value => value.Code,
            NtfsMftBitmapSelectionException value => value.Code,
            NtfsVssProbeException value => value.Code,
            NtfsPersonalSnapshotOpenException value => value.Code,
            _ => null
        };
        return code switch
        {
            "NamespaceEntryBudget" => FastScanFailureDetail.NamespaceEntryLimit,
            "NamespaceNameBudget" => FastScanFailureDetail.NamespaceNameLimit,
            "NamespacePathBudget" => FastScanFailureDetail.NamespacePathLimit,
            "SnapshotSizeProjectionBudget" or "SnapshotSizeNamespaceBudget" => FastScanFailureDetail.NamespacePathLimit,
            "NamespaceQueryBudget" => FastScanFailureDetail.NamespaceQueryLimit,
            "SnapshotSizeRawBudget" => FastScanFailureDetail.MftPageLimit,
            "SnapshotSizeNativeBudget" or "SnapshotSizeNativeLimit" => FastScanFailureDetail.NativeObjectLimit,
            "BitmapSelectionPageBudget" or "BitmapSelectionSlotBudget" or "BitmapSelectionRunBudget" => FastScanFailureDetail.BitmapPageLimit,
            "VssRawBudget" => FastScanFailureDetail.RawRequestLimit,
            "VssRawGeometry" => FastScanFailureDetail.GeometryUnsupported,
            "SnapshotSizeRead" or "SnapshotSizeShortRead" => FastScanFailureDetail.MftDataRead,
            "BitmapSelectionRead" or "BitmapSelectionShortRead" => FastScanFailureDetail.MftBitmapRead,
            "SnapshotSizeInput" => FastScanFailureDetail.IndexInput,
            "SnapshotSizeNamespaceIncomplete" => FastScanFailureDetail.IndexNamespaceIncomplete,
            "SnapshotSizeMap" => FastScanFailureDetail.IndexMap,
            "SnapshotSizeIdentity" => FastScanFailureDetail.IndexIdentity,
            "SnapshotSizeSlotOutsideMap" => FastScanFailureDetail.IndexSlotOutsideMap,
            "SnapshotSizeDirectoryAlias" => FastScanFailureDetail.IndexDirectoryAlias,
            "SnapshotSizeSlotReused" => FastScanFailureDetail.IndexSlotReused,
            "SnapshotSizeAllocationRead" => FastScanFailureDetail.IndexAllocationRead,
            "SnapshotSizeBoundary" => FastScanFailureDetail.IndexBoundary,
            "SnapshotSizeOverflow" => FastScanFailureDetail.IndexOverflow,
            "SnapshotSizeDecode" => FastScanFailureDetail.IndexDecode,
            "SnapshotSizeCoverage" => FastScanFailureDetail.IndexCoverage,
            "SnapshotSizeNegative" => FastScanFailureDetail.IndexNegative,
            _ => error switch
            {
                NtfsMftBitmapSelectionException => FastScanFailureDetail.MftBitmapValidation,
                NtfsSnapshotSizeException => FastScanFailureDetail.MftIndexValidation,
                NtfsSnapshotNamespaceException => FastScanFailureDetail.NamespaceValidation,
                NtfsVssProbeException => FastScanFailureDetail.None,
                null => FastScanFailureDetail.None,
                _ => FastScanFailureDetail.UnexpectedPipelineFailure
            }
        };
    }
}
