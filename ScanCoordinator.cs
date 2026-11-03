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
    private readonly Func<string, JournalOpenResult> openJournal;
    public ScanCoordinator() => openJournal = NtfsChangeJournal.Open;
    internal ScanCoordinator(Func<string, IChangeJournal?> openJournal) => this.openJournal = path =>
    {
        var journal = openJournal(path);
        return new(journal, new(journal == null ? JournalState.Unavailable : JournalState.Available));
    };

    public ScanResult Scan(string path, ScanResult? previous, ScanOptions options, IProgress<ScanProgress>? progress,
        CancellationToken token, LiveScanSession? live = null, bool forceFull = false)
    {
        var watch = Stopwatch.StartNew(); token.ThrowIfCancellationRequested(); path = Path.GetFullPath(path);
        JournalOpenResult opening;
        try { opening = openJournal(path); }
        catch (UnauthorizedAccessException) { opening = new(null, new(JournalState.AccessDenied)); }
        catch (IOException) { opening = new(null, new(JournalState.Unavailable)); }
        using var journal = opening.Journal;
        var journalStatus = opening.Status;
        JournalCheckpoint? start = opening.InitialCheckpoint;
        if (start == null && journal != null)
        {
            try { start = journal.Capture(); }
            catch (JournalException ex) { journalStatus = ex.Status; }
            catch (IOException) { journalStatus = new(JournalState.QueryFailed); }
            catch (UnauthorizedAccessException) { journalStatus = new(JournalState.AccessDenied); }
        }
        token.ThrowIfCancellationRequested();
        void Announce(RefreshMethod? method, string message)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new(0, 0, 0, "检查刷新方式", ScanPhase.Preparing, Method: method, Message: message));
        }
        // Capture the start cursor before callbacks: writes during planning must be replayable.
        Announce(null, journalStatus.Description);
        RefreshPlan? plan = null;
        var method = RefreshMethod.FullScan;
        long verifiedDirectories = 0, verifiedFiles = 0, records = 0;
        int peakVerificationWorkers = 0;
        bool readingJournal = false;
        string reason = forceFull ? "已强制完整扫描" : previous == null ? "首次完整扫描" : "扫描目录已切换，采用完整扫描";
        try
        {
            if (!forceFull && previous != null && previous.Root.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                if (previous.Skipped > 0 || previous.Root.Incomplete || previous.Root.UnknownAllocationFiles > 0)
                    throw new IOException("旧结果不完整");
                if (!NativeStorage.TryRead(path, true, FileAttributes.Directory, out var currentRoot, out _) || previous.Root.Storage?.Identity != currentRoot!.Identity)
                    throw new IOException("根目录身份已变化或无法核实");
                if (start?.VolumeSerial is ulong volume && currentRoot.Identity.Volume != volume)
                { journalStatus = new(JournalState.VolumeMismatch); start = null; }
                if (previous.Checkpoint != null && start != null && journal != null)
                {
                    readingJournal = true;
                    Announce(RefreshMethod.ChangeJournal, "检查两次扫描之间的日志连续性");
                    var changes = journal.Read(previous.Checkpoint, start, token); records = changes.Count;
                    readingJournal = false;
                    plan = RefreshPlan.Build(previous.Root, changes, token);
                    method = RefreshMethod.ChangeJournal;
                    reason = "NTFS 变化日志连续，优先重扫变化目录";
                }
                else
                {
                    string unavailable = start != null && previous.Checkpoint == null ? "上次扫描没有有效日志游标" : journalStatus.Description;
                    Announce(RefreshMethod.MetadataVerification, unavailable + "；将核验目录及文件元信息");
                    var verified = MetadataVerifier.Verify(previous.Root, options, progress, token);
                    plan = verified.Plan; verifiedDirectories = verified.Directories; verifiedFiles = verified.Files; peakVerificationWorkers = verified.PeakWorkers;
                    method = RefreshMethod.MetadataVerification;
                    reason = unavailable + "；已核验目录及文件元信息，复用未变部分";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException || ex is AggregateException aggregate &&
            aggregate.InnerExceptions.All(inner => inner is IOException or ArgumentException or UnauthorizedAccessException))
        {
            token.ThrowIfCancellationRequested();
            if (ex is JournalException native) journalStatus = native.Status;
            else if (readingJournal && journalStatus.State == JournalState.Available)
                journalStatus = new(JournalState.ReadFailed);
            string detail = ex is JournalException ? journalStatus.Description :
                ex is IOException && ex.Message is "旧结果不完整" or "根目录身份已变化或无法核实" or "旧快照中存在无法识别的文件或目录" ? ex.Message : "刷新准备或核验未能完成";
            reason = detail + "；已回退完整扫描"; plan = null; method = RefreshMethod.FullScan;
        }
        Announce(method, reason);
        double preparation = watch.Elapsed.TotalMilliseconds;
        var result = new DiskScanner().Scan(path, progress, token, live, options, plan);
        token.ThrowIfCancellationRequested();
        if (start?.VolumeSerial is ulong serial && result.Root.Storage?.Identity.Volume != serial)
        {
            journalStatus = new(JournalState.VolumeMismatch); start = null;
            if (method == RefreshMethod.ChangeJournal)
            {
                reason = journalStatus.Description + "；已回退完整扫描"; method = RefreshMethod.FullScan;
                Announce(method, reason);
                result = new DiskScanner().Scan(path, progress, token, live, options);
            }
        }
        token.ThrowIfCancellationRequested();
        // No persistence: only a completed snapshot in this process can become a baseline.
        return result with { Elapsed = watch.Elapsed, Checkpoint = start, Statistics = result.Statistics! with
        {
            Reason = reason, Method = method, Journal = journalStatus, JournalRecords = records,
            VerifiedDirectories = verifiedDirectories, VerifiedFiles = verifiedFiles, PeakVerificationWorkers = peakVerificationWorkers,
            PreparationMilliseconds = preparation, ScanMilliseconds = Math.Max(0, watch.Elapsed.TotalMilliseconds - preparation)
        } };
    }
}
