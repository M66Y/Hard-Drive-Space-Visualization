using System.Diagnostics;

namespace DiskAtlas;

internal static class FastScanCoordinator
{
    internal static ScanResult Scan(string path, ScanResult? previous, ScanOptions options,
        IProgress<ScanProgress>? progress, CancellationToken token, LiveScanSession session, bool forceFull,
        Func<string, IProgress<ScanProgress>?, CancellationToken, ScanResult>? fast = null,
        Func<ScanResult?, ScanResult>? ordinary = null)
    {
        token.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        try
        {
            var result = (fast ?? FastScanBridge.Scan)(path, progress, token);
            token.ThrowIfCancellationRequested();
            if (result.Source == null || !result.Root.IsDirectory || result.Root.Parent != null ||
                !string.Equals(FastScanWire.NormalizeRoot(result.Root.Path), FastScanWire.NormalizeRoot(path), StringComparison.OrdinalIgnoreCase) ||
                result.Checkpoint != null || result.Root.Incomplete ||
                result.Skipped != 0 || result.Root.UnknownAllocationFiles != 0 || result.Issues.Count != 0 || result.AllocationIssues.Count != 0)
                throw new FastScanUnavailableException(FastScanWireError.ProtocolError, false);
            return result with { Elapsed = timer.Elapsed };
        }
        catch (FastScanUnavailableException ex) when (ex.CleanupConfirmed)
        {
            token.ThrowIfCancellationRequested();
            if (ex.Code == FastScanWireError.Cancelled) throw new OperationCanceledException();
            progress?.Report(new(0, 0, 0, "切换到普通目录扫描", ScanPhase.Preparing,
                Message: "NTFS 快扫未启用：" + Reason(ex.Code) + "。本次使用普通扫描。"));
            // A failed fast attempt has not attached any partial tree to this session.
            // Snapshot metadata never acts as a live USN checkpoint or refresh cache.
            var baseline = previous?.Source == null ? previous : null;
            var fallback = ordinary != null ? ordinary(baseline) : new ScanCoordinator().Scan(path,
                baseline, options, progress, token, session, forceFull);
            token.ThrowIfCancellationRequested();
            return fallback with { Elapsed = timer.Elapsed, FastScanFallback = ex.Code };
        }
    }

    internal static string Reason(FastScanWireError code) => code switch
    {
        FastScanWireError.PermissionDenied => "未取得本次管理员授权",
        FastScanWireError.Unsupported => "当前目录、卷或记录格式暂不支持",
        FastScanWireError.LimitExceeded => "超过本版快速扫描的资源上限",
        FastScanWireError.SourceChanged => "无法确认读取来源一致",
        FastScanWireError.SnapshotFailed => "无法建立只读快照",
        FastScanWireError.ReadFailed => "元信息读取未完成",
        FastScanWireError.CleanupUnconfirmed => "尚未确认本次快照清理，已停止扫描并保留旧结果；请关闭软件后重新启动再试",
        _ => "快速扫描未能完成核验"
    };
}
