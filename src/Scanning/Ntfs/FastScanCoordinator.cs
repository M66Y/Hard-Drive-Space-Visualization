using System.Diagnostics;
using DiskAtlas.NtfsPrototype;

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
            ValidateResult(result, path, token);
            return result with { Elapsed = timer.Elapsed };
        }
        catch (FastScanUnavailableException ex) when (ex.CleanupConfirmed)
        {
            token.ThrowIfCancellationRequested();
            if (ex.Code == FastScanWireError.Cancelled) throw new OperationCanceledException();
            progress?.Report(new(0, 0, 0, "切换到普通目录扫描", ScanPhase.Preparing,
                Message: "NTFS 快扫未启用：" + Reason(ex.Code, ex.Context) + "。本次使用普通扫描。") { FastScanFallback = ex.Code, FastScanFallbackContext = ex.Context });
            // A failed fast attempt has not attached any partial tree to this session.
            // Snapshot metadata never acts as a live USN checkpoint or refresh cache.
            var baseline = previous?.Source == null ? previous : null;
            var fallback = ordinary != null ? ordinary(baseline) : new ScanCoordinator().Scan(path,
                baseline, options, progress, token, session, forceFull);
            token.ThrowIfCancellationRequested();
            return fallback with { Elapsed = timer.Elapsed, FastScanFallback = ex.Code, FastScanFallbackContext = ex.Context };
        }
    }

    // The only accepted partial success is a declared, bounded reparse boundary.
    // It does not authorize following the link or claim metadata for its target.
    private static void ValidateResult(ScanResult result, string path, CancellationToken token)
    {
        try
        {
            FastScanWire.Require(result != null && result.Source != null && result.Root != null && result.Root.IsDirectory && result.Root.Parent == null &&
                string.Equals(FastScanWire.NormalizeRoot(result.Root.Path), FastScanWire.NormalizeRoot(path), StringComparison.OrdinalIgnoreCase) &&
                result.Checkpoint == null && result.AllocationIssues.Count == 0);
            long excluded = result.Source.ExcludedReparseEntries;
            FastScanWire.Require(excluded is >= 0 and <= FastScanWire.MaximumNodes && result.Skipped == excluded &&
                result.Issues.Count == Math.Min(excluded, FastScanWire.MaximumExclusionDetails));
            var nodes = new List<DiskEntry>(); var seen = new HashSet<DiskEntry>(ReferenceEqualityComparer.Instance);
            var directories = new Dictionary<string, DiskEntry>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<DiskEntry>(); pending.Enqueue(result.Root);
            while (pending.TryDequeue(out var entry))
            {
                token.ThrowIfCancellationRequested();
                FastScanWire.Require(entry != null && seen.Add(entry) && nodes.Count < FastScanWire.MaximumNodes &&
                    entry.UnknownAllocationFiles == 0 && (entry.IsDirectory || !entry.Incomplete && entry.Children.Count == 0));
                FastScanWire.Require(entry.Children.Count <= FastScanWire.MaximumNodes - nodes.Count - pending.Count - 1);
                nodes.Add(entry);
                if (entry.IsDirectory) FastScanWire.Require(directories.TryAdd(entry.Path, entry));
                foreach (var child in entry.Children)
                {
                    FastScanWire.Require(child != null && ReferenceEquals(child.Parent, entry) && FastScanWire.ValidName(child.Name) && child.HasExpectedParentPath());
                    pending.Enqueue(child);
                }
            }
            FastScanWire.Require(excluded <= FastScanWire.MaximumNodes - nodes.Count);
            var incomplete = new HashSet<DiskEntry>(ReferenceEqualityComparer.Instance);
            var names = new Dictionary<DiskEntry, HashSet<string>>(ReferenceEqualityComparer.Instance);
            long issueCharacters = 0;
            for (int i = 0; i < result.Issues.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var issue = result.Issues[i];
                FastScanWire.Require(issue != null && issue.Reason == NtfsReparsePolicy.Reason && issue.Path.Length <= 32767 &&
                    string.Equals(Path.GetFullPath(issue.Path), issue.Path, StringComparison.OrdinalIgnoreCase));
                string leaf = Path.GetFileName(issue.Path), parentPath = Path.GetDirectoryName(issue.Path) ?? "";
                FastScanWire.Require(FastScanWire.ValidName(leaf));
                FastScanWire.Require(directories.TryGetValue(parentPath, out var parent));
                issueCharacters = checked(issueCharacters + issue.Path.Length);
                FastScanWire.Require(issueCharacters <= FastScanWire.MaximumNameCharacters);
                if (!names.TryGetValue(parent, out var siblingNames)) names.Add(parent, siblingNames = new(StringComparer.OrdinalIgnoreCase));
                FastScanWire.Require(siblingNames.Add(leaf));
                if (excluded <= FastScanWire.MaximumExclusionDetails)
                    for (DiskEntry? ancestor = parent; ancestor != null; ancestor = ancestor.Parent) incomplete.Add(ancestor);
            }
            foreach (var entry in nodes)
            {
                token.ThrowIfCancellationRequested();
                bool expected = entry.IsDirectory && (excluded > FastScanWire.MaximumExclusionDetails || incomplete.Contains(entry));
                FastScanWire.Require(entry.Incomplete == expected && (entry.Parent == null ||
                    !names.TryGetValue(entry.Parent, out var siblingNames) || !siblingNames.Contains(entry.Name)));
            }
            FastScanWire.Require(excluded == 0 || result.Root.Incomplete);
        }
        catch
        {
            token.ThrowIfCancellationRequested();
            throw new FastScanUnavailableException(FastScanWireError.ProtocolError, false);
        }
    }

    internal static string Reason(FastScanWireError code, FastScanFailureContext? context = null) => context?.Detail switch
    {
        FastScanFailureDetail.NamespaceEntryLimit => "目录项超过本版快扫上限（1,048,576 项）",
        FastScanFailureDetail.NamespaceNameLimit => "文件名总量超过快扫内存上限",
        FastScanFailureDetail.NamespacePathLimit => "目录路径总量超过快扫内存上限",
        FastScanFailureDetail.NamespaceQueryLimit => "目录核验超过快扫查询上限",
        FastScanFailureDetail.MftPageLimit or FastScanFailureDetail.RawRequestLimit => "所选范围超过快扫读取上限",
        FastScanFailureDetail.BitmapPageLimit => "所需位图范围超过快扫读取上限",
        FastScanFailureDetail.NativeObjectLimit => "需要逐项补查的对象超过快扫上限",
        FastScanFailureDetail.MftExtensionsUnsupported or FastScanFailureDetail.MftMappingUnsupported or
            FastScanFailureDetail.MftAttributeListUnsupported or FastScanFailureDetail.MftMetadataUnsupported => "此卷的 NTFS 扩展布局暂不支持快扫",
        FastScanFailureDetail.GeometryUnsupported => "当前卷的扇区或记录格式暂不支持快扫",
        FastScanFailureDetail.MftMetadataBudget => "此卷的扩展元信息超过当前解析上限",
        _ => GenericReason(code) + (context?.NativeError is { } error && error != 0 ? $"（系统代码 0x{unchecked((uint)error):X8}）" : "")
    };

    private static string GenericReason(FastScanWireError code) => code switch
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
