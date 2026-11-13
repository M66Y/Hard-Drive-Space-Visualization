using System.Diagnostics;
using DiskAtlas.NtfsPrototype;

namespace DiskAtlas;

internal sealed record NtfsPersonalScanResult(DiskEntry Root, TimeSpan Elapsed, DateTimeOffset CapturedAt);

internal static class NtfsPersonalScan
{
    // Synchronous on the dedicated MTA owner thread. Only bounded Native lookups
    // use worker tasks; no COM owner crosses an await boundary.
    internal static NtfsPersonalScanResult Run(string path, Action<FastScanWireStage> stage, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        NtfsPersonalSnapshotSource? source = null;
        DiskEntry? tree = null;
        DateTimeOffset captured = default;
        Exception? failure = null;
        bool cleaned = true;
        FastScanWireStage lastStage = FastScanWireStage.Preparing;
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
            source = NtfsPersonalSnapshotSource.Create(path, token);
            captured = new DateTimeOffset(DateTime.SpecifyKind(source.SnapshotCreatedUtc, DateTimeKind.Utc));
            Stage(FastScanWireStage.Discovering);
            var scope = NtfsSnapshotNamespace.Read(source.CreateNamespaceBackend(), token);
            if (!scope.Report.CompleteWithinBoundary || scope.RootIdentity != source.RootStorage.Identity)
                throw new FastScanUnavailableException(FastScanWireError.SourceChanged, false);
            Stage(FastScanWireStage.Reading);
            var reader = source.OpenReader(token);
            var geometry = reader.Facts(token).Geometry;
            if (geometry is not { SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024 })
                throw new FastScanUnavailableException(FastScanWireError.Unsupported, false);
            byte[] bootstrap = new byte[1024];
            try
            {
                if (reader.ReadInto(checked(geometry.MftStartLcn * geometry.ClusterBytes), bootstrap, token) != bootstrap.Length)
                    throw new FastScanUnavailableException(FastScanWireError.ReadFailed, false);
                var parsed = NtfsMftMap.Parse(bootstrap, geometry, token);
                if (parsed.Map == null || parsed.Status != "Mapped")
                    throw new FastScanUnavailableException(parsed.Status == "Unsupported" ? FastScanWireError.Unsupported : FastScanWireError.SourceChanged, false);
                var map = parsed.Map;
                var bits = NtfsMftBitmapSelection.Parse(bootstrap, geometry,
                    scope.Entries.Select(e => e.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber).Distinct().ToArray(), token)
                    .Read(reader.ReadInto, token);
                int Read(long offset, Memory<byte> destination, CancellationToken cancellation)
                {
                    int copied = 0;
                    foreach (var slice in map.PlanRange(offset, destination.Length, cancellation))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (slice.RecordOffset != copied || slice.Length <= 0 || slice.Length > destination.Length - copied ||
                            reader.ReadInto(slice.VolumeOffset, destination.Slice(copied, slice.Length), cancellation) != slice.Length)
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
            }
            finally { Array.Clear(bootstrap); }
            token.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            failure = ex;
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
        if (!cleaned) throw new FastScanUnavailableException(FastScanWireError.CleanupUnconfirmed, false);
        token.ThrowIfCancellationRequested();
        if (failure != null)
        {
            if (failure is FastScanUnavailableException available) throw new FastScanUnavailableException(available.Code, true);
            if (failure is NtfsPersonalSnapshotOpenException open)
                throw new FastScanUnavailableException(open.Code switch
                {
                    "VssPersonalElevation" => FastScanWireError.PermissionDenied,
                    "VssPersonalPath" or "VssPersonalVolume" or "VssRawGeometry" => FastScanWireError.Unsupported,
                    "VssCancelled" => FastScanWireError.Cancelled,
                    _ => FastScanWireError.SnapshotFailed
                }, true);
            throw new FastScanUnavailableException(ClassifyFailure(failure), true);
        }
        if (tree == null) throw new FastScanUnavailableException(FastScanWireError.InternalError, true);
        return new(tree, watch.Elapsed, captured);
    }

    internal static FastScanWireError ClassifyFailure(Exception error)
    {
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
            "SnapshotSizeRawBudget" or "SnapshotSizeNativeBudget" or "SnapshotSizeNativeLimit" or
            "BitmapSelectionPageBudget" or "BitmapSelectionSlotBudget" or "BitmapSelectionRunBudget" or "VssRawBudget" => FastScanWireError.LimitExceeded,
            "SourceConsistencyFailed" => FastScanWireError.SourceChanged,
            _ => FastScanWireError.ReadFailed
        };
    }
}
