using System.Diagnostics;

namespace DiskAtlas;

public sealed record RefreshPlan(DiskEntry PreviousRoot, Dictionary<string, DiskEntry> PreviousDirectories,
    HashSet<string> ScanDirectories, HashSet<string> VisitDirectories)
{
    internal bool MetadataValidated { get; init; }
    internal IReadOnlyDictionary<string, FileStorageInfo> VerifiedChanges { get; init; } = new Dictionary<string, FileStorageInfo>();
    internal IReadOnlySet<string> VerifiedDirectoryPaths { get; init; } = new HashSet<string>();
    internal IReadOnlySet<string> UnverifiedFiles { get; init; } = new HashSet<string>();
    public static RefreshPlan Build(DiskEntry root, IReadOnlyList<JournalChange> changes, CancellationToken token)
    {
        var changed = changes.Select(c => (c.IdLow, c.IdHigh)).ToHashSet();
        var parents = changes.Select(c => (c.ParentLow, c.ParentHigh)).ToHashSet();
        var directories = new Dictionary<string, DiskEntry>(StringComparer.OrdinalIgnoreCase);
        var dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in root.Descendants().Prepend(root))
        {
            token.ThrowIfCancellationRequested();
            if (entry.Storage == null) throw new IOException("旧快照中存在无法识别的文件或目录");
            var key = (entry.Storage.Identity.Low, entry.Storage.Identity.High);
            if (entry.IsDirectory)
            {
                directories.Add(entry.Path, entry);
                if (changed.Contains(key) || parents.Contains(key)) dirty.Add(entry.Path);
                if (changed.Contains(key) && entry.Parent != null) dirty.Add(entry.Parent.Path);
            }
            else if (changed.Contains(key) || entry.Storage.WritableHandleOpen) dirty.Add(entry.Parent!.Path);
        }
        return FromDirty(root, directories, dirty);
    }

    internal static RefreshPlan FromDirty(DiskEntry root, Dictionary<string, DiskEntry> directories, HashSet<string> dirty)
    {
        var visit = new HashSet<string>(dirty, StringComparer.OrdinalIgnoreCase);
        foreach (string path in dirty)
        {
            var entry = directories.GetValueOrDefault(path)?.Parent;
            for (; entry != null; entry = entry.Parent) visit.Add(entry.Path);
        }
        return new(root, directories, dirty, visit);
    }
}

public sealed class ScanCoordinator
{
    private readonly Func<string, IChangeJournal?> openJournal;
    public ScanCoordinator() : this(NtfsChangeJournal.TryOpen) { }
    internal ScanCoordinator(Func<string, IChangeJournal?> openJournal) => this.openJournal = openJournal;

    public ScanResult Scan(string path, ScanResult? previous, ScanOptions options, IProgress<ScanProgress>? progress,
        CancellationToken token, LiveScanSession? live = null, bool forceFull = false)
    {
        var watch = Stopwatch.StartNew(); token.ThrowIfCancellationRequested(); path = Path.GetFullPath(path);
        using var journal = openJournal(path);
        JournalCheckpoint? start = null;
        RefreshPlan? plan = null;
        long verifiedDirectories = 0, verifiedFiles = 0;
        int peakVerificationWorkers = 0;
        string reason = forceFull ? "已强制完整扫描" : previous == null ? "首次完整扫描" : "变化日志不可用，完整扫描";
        try
        {
            start = journal?.Capture();
            if (!forceFull && previous != null && previous.Root.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                if (previous.Skipped > 0 || previous.Root.UnknownAllocationFiles > 0) throw new IOException("旧结果不完整，重新完整扫描");
                if (!NativeStorage.TryRead(path, true, FileAttributes.Directory, out var currentRoot, out _) || previous.Root.Storage?.Identity != currentRoot!.Identity)
                    throw new IOException("根目录身份已变化");
                if (previous.Checkpoint != null && start != null && journal != null)
                {
                    var changes = journal.Read(previous.Checkpoint, start, token);
                    plan = RefreshPlan.Build(previous.Root, changes, token);
                    reason = "NTFS 变化日志连续，优先重扫变化目录";
                }
                else
                {
                    var verified = MetadataVerifier.Verify(previous.Root, options, progress, token);
                    plan = verified.Plan; verifiedDirectories = verified.Directories; verifiedFiles = verified.Files; peakVerificationWorkers = verified.PeakWorkers;
                    reason = "无可用日志：已核验目录及文件元信息，复用未变部分";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException || ex is AggregateException aggregate &&
            aggregate.InnerExceptions.All(inner => inner is IOException or ArgumentException or UnauthorizedAccessException))
        {
            token.ThrowIfCancellationRequested();
            reason = ex.Message + "；已回退完整扫描"; plan = null;
        }
        var result = new DiskScanner().Scan(path, progress, token, live, options, plan);
        token.ThrowIfCancellationRequested();
        // Keep the START cursor: changes during this scan are replayed on the next refresh.
        return result with { Elapsed = watch.Elapsed, Checkpoint = start, Statistics = result.Statistics! with { Reason = reason, VerifiedDirectories = verifiedDirectories, VerifiedFiles = verifiedFiles, PeakVerificationWorkers = peakVerificationWorkers } };
    }
}
