using System.Diagnostics;
using System.Globalization;
using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Threading.Channels;

namespace DiskAtlas;

public sealed class DiskEntry
{
    private readonly string? storedPath;
    private List<DiskEntry>? children;
    // Directories retain their shared prefix; file paths are composed only when needed.
    public string Path => storedPath ?? System.IO.Path.Join(Parent!.Path, Name);
    public string Name { get; }
    public bool IsDirectory { get; }
    public DiskEntry? Parent { get; }
    public IReadOnlyList<DiskEntry> Children => children ?? (IReadOnlyList<DiskEntry>)Array.Empty<DiskEntry>();
    internal List<DiskEntry> MutableChildren => children ??= [];
    public long Size { get; set; }
    public long FileCount { get; set; }
    public long FolderCount { get; set; }
    public DateTime Modified { get; set; }
    public bool Incomplete { get; set; }
    public string Extension { get; }
    public FileStorageInfo? Storage { get; internal set; }
    public long AllocatedSize { get; internal set; }
    public long UnknownAllocationFiles { get; internal set; }
    public bool SharedAllocation { get; internal set; }
    public long Bytes(SizeMetric metric) => metric == SizeMetric.Allocated ? AllocatedSize : Size;

    public DiskEntry(string path, bool isDirectory, DiskEntry? parent = null)
        : this(System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path,
            isDirectory, parent, path,
            isDirectory ? "文件夹" : ExtensionOf(path)) { }

    private DiskEntry(string name, bool isDirectory, DiskEntry? parent, string? storedPath, string extension)
    {
        Name = name; IsDirectory = isDirectory; Parent = parent;
        this.storedPath = storedPath; Extension = extension;
    }

    internal static string ExtensionOf(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() is { Length: > 0 } ext ? ext : "无扩展名";

    internal static DiskEntry FromName(string name, bool isDirectory, DiskEntry parent, string extension) =>
        new(name, isDirectory, parent, isDirectory ? System.IO.Path.Join(parent.Path, name) : null, extension);

    internal DiskEntry CopyMetadata(DiskEntry? parent)
    {
        lock (this)
            return new DiskEntry(Name, IsDirectory, parent, storedPath, Extension)
            {
                Size = Size, FileCount = FileCount, FolderCount = FolderCount,
                Modified = Modified, Incomplete = Incomplete, Storage = Storage,
                AllocatedSize = AllocatedSize, UnknownAllocationFiles = UnknownAllocationFiles, SharedAllocation = SharedAllocation
            };
    }

    public static int ComparePath(DiskEntry a, DiskEntry b)
    {
        if (a.storedPath != null && b.storedPath != null)
            return StringComparer.OrdinalIgnoreCase.Compare(a.storedPath, b.storedPath);
        if (a.storedPath == null && b.storedPath == null && ReferenceEquals(a.Parent, b.Parent))
            return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        var left = new PathCursor(a); var right = new PathCursor(b);
        while (true)
        {
            var x = left.Remaining; var y = right.Remaining;
            int count = Math.Min(x.Length, y.Length);
            if (count == 0) return x.Length.CompareTo(y.Length);
            int compared = x[..count].CompareTo(y[..count], StringComparison.OrdinalIgnoreCase);
            if (compared != 0) return compared;
            left.Consume(count); right.Consume(count);
        }
    }

    private ref struct PathCursor
    {
        private ReadOnlySpan<char> remaining;
        private readonly ReadOnlySpan<char> name;
        private readonly bool needsSeparator;
        private int phase;
        public PathCursor(DiskEntry entry)
        {
            remaining = entry.storedPath ?? entry.Parent!.Path;
            name = entry.storedPath != null ? default : entry.Name;
            needsSeparator = !System.IO.Path.EndsInDirectorySeparator(remaining);
            phase = entry.storedPath != null ? 2 : 0;
        }
        public ReadOnlySpan<char> Remaining
        {
            get
            {
                while (remaining.IsEmpty && phase < 2)
                {
                    if (phase++ == 0 && needsSeparator) remaining = "\\";
                    else { phase = 2; remaining = name; }
                }
                return remaining;
            }
        }
        public void Consume(int count) => remaining = remaining[count..];
    }

    public IEnumerable<DiskEntry> Descendants()
    {
        var stack = new Stack<DiskEntry>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var entry = stack.Pop();
            foreach (var child in entry.Children)
            {
                yield return child;
                // Only directories need to remain on the traversal stack.
                if (child.IsDirectory) stack.Push(child);
            }
        }
    }

    public DiskEntry? FindDirectory(string path)
    {
        string relative = System.IO.Path.GetRelativePath(Path, path);
        if (relative == ".") return this;
        if (System.IO.Path.IsPathRooted(relative)) return null;
        var parts = relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p == "..")) return null;
        DiskEntry? found = this;
        foreach (var part in parts)
        {
            found = found.Children.FirstOrDefault(c => c.IsDirectory && c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (found == null) break;
        }
        return found;
    }
}

public record ScanProgress(long Files, long Folders, long Bytes, string CurrentPath,
    ScanPhase Phase = ScanPhase.Scanning, int WorkerLimit = 0);
public record ScanIssue(string Path, string Reason);
public record ScanResult(DiskEntry Root, TimeSpan Elapsed, long Skipped, IReadOnlyList<ScanIssue> Issues)
{
    public IReadOnlyList<ScanIssue> AllocationIssues { get; init; } = [];
    public JournalCheckpoint? Checkpoint { get; init; }
    public ScanStatistics? Statistics { get; init; }
}

public sealed class DiskScanner
{
    private readonly int maxConcurrency;

    // A small worker pool overlaps SSD directory reads without opening a task per folder.
    public DiskScanner(int maxConcurrency = 8)
    {
        if (maxConcurrency is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        this.maxConcurrency = maxConcurrency;
    }

    private readonly record struct ScanEntry(string Name, FileAttributes Attributes, long Length, DateTime Modified);

    private static readonly EnumerationOptions Enumeration = new()
    {
        IgnoreInaccessible = false, AttributesToSkip = 0,
        RecurseSubdirectories = false, ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024
    };

    public ScanResult Scan(string path, IProgress<ScanProgress>? progress, CancellationToken token, LiveScanSession? live = null,
        ScanOptions? options = null, RefreshPlan? refresh = null)
    {
        token.ThrowIfCancellationRequested();
        path = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("文件夹不存在或当前账户无法访问。" );
        var rootInfo = new DirectoryInfo(path);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("所选目录是链接或联接点，请选择其实际所在的文件夹。" );
        var root = new DiskEntry(rootInfo.FullName, true) { Modified = rootInfo.LastWriteTime };
        var profile = options == null ? new ScanProfile(maxConcurrency, 0, "默认") : ScanPolicy.Resolve(options.Mode, NativeStorage.DetectDisk(path));
        NativeStorage.TryRead(path, true, rootInfo.Attributes, out var rootStorage, out _); root.Storage = rootStorage;
        live?.Attach(root);
        var extensions = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var pending = Channel.CreateUnbounded<DiskEntry>(new UnboundedChannelOptions
        {
            SingleReader = profile.Workers == 1, SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        var issues = new List<ScanIssue>();
        var allocationIssues = new List<ScanIssue>();
        var reportGate = new object();
        var timer = Stopwatch.StartNew();
        long files = 0, folders = 0, bytes = 0, skipped = 0, lastReport = -100;
        long outstanding = 1;
        long readDirectories = 0, reusedFiles = 0;
        var pacer = new ScanWorkPacer(profile);
        var concurrency = new ScanConcurrencyMeter();

        void Issue(string itemPath, string reason, DiskEntry container)
        {
            Interlocked.Increment(ref skipped);
            for (DiskEntry? ancestor = container; ancestor != null; ancestor = ancestor.Parent)
                lock (ancestor) ancestor.Incomplete = true;
            live?.RecordSkip();
            lock (issues)
                if (issues.Count < 2000) issues.Add(new ScanIssue(itemPath, reason));
        }
        void Report(string current, bool force = false, ScanPhase phase = ScanPhase.Scanning)
        {
            if (progress == null || (!force && timer.ElapsedMilliseconds - Volatile.Read(ref lastReport) < 100)) return;
            lock (reportGate)
            {
                if (!force && timer.ElapsedMilliseconds - lastReport < 100) return;
                Volatile.Write(ref lastReport, timer.ElapsedMilliseconds);
                progress.Report(new ScanProgress(Interlocked.Read(ref files), Interlocked.Read(ref folders), Interlocked.Read(ref bytes), current, phase, profile.Workers));
            }
        }

        void ScanDirectory(DiskEntry current, CancellationToken workToken)
        {
            workToken.ThrowIfCancellationRequested();
            pacer.Worked(workToken);
            var phase = refresh != null && refresh.PreviousDirectories.ContainsKey(current.Path) && !refresh.ScanDirectories.Contains(current.Path)
                ? ScanPhase.Reusing : ScanPhase.Scanning;
            Report(current.Path, phase: phase);
            workToken.ThrowIfCancellationRequested();
            long localFiles = 0, localFolders = 0, localBytes = 0;
            int batch = 0;
            void Flush()
            {
                if (localFiles != 0 || localFolders != 0 || localBytes != 0)
                {
                    // Publish subtree totals in small batches; preview readers lock only one node at a time.
                    for (DiskEntry? ancestor = current; ancestor != null; ancestor = ancestor.Parent)
                        lock (ancestor)
                        {
                            ancestor.Size += localBytes; ancestor.FileCount += localFiles; ancestor.FolderCount += localFolders;
                        }
                }
                Interlocked.Add(ref files, localFiles); Interlocked.Add(ref folders, localFolders); Interlocked.Add(ref bytes, localBytes);
                localFiles = localFolders = localBytes = 0;
                Report(current.Path, phase: phase);
            }
            void Schedule(DiskEntry entry)
            {
                Interlocked.Increment(ref outstanding); pending.Writer.TryWrite(entry);
            }
            void Publish(DiskEntry entry)
            {
                lock (current) current.MutableChildren.Add(entry);
                if (entry.IsDirectory) { localFolders++; Schedule(entry); }
                else { localFiles++; localBytes += entry.Size; }
                if (++batch == 256) { Flush(); batch = 0; }
            }
            // Keep enumeration exceptions inside this directory so other folders still finish.
            try
            {
                var directory = new DirectoryInfo(current.Path);
                // A queued folder can become a junction while a scan is running.
                directory.Refresh();
                if (!directory.Exists) throw new DirectoryNotFoundException("目录已移动或删除。");
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (current == root) throw new IOException("所选目录已变为链接，请选择实际文件夹后重新扫描。");
                    Issue(current.Path, "扫描期间目录变为重解析点，已跳过", current);
                    return;
                }
                if (refresh != null && refresh.PreviousDirectories.TryGetValue(current.Path, out var cached) && !refresh.ScanDirectories.Contains(current.Path))
                {
                    current.Storage = cached.Storage; current.Modified = cached.Modified;
                    if (!refresh.VisitDirectories.Contains(current.Path))
                    {
                        var stack = new Stack<(DiskEntry Source, DiskEntry Target)>(); stack.Push((cached, current));
                        while (stack.TryPop(out var pair))
                        foreach (var child in pair.Source.Children)
                        {
                            workToken.ThrowIfCancellationRequested();
                            var clone = child.CopyMetadata(pair.Target);
                            lock (pair.Target) pair.Target.MutableChildren.Add(clone);
                            if (child.IsDirectory) stack.Push((child, clone));
                        }
                        localFiles = cached.FileCount; localFolders = cached.FolderCount; localBytes = cached.Size;
                        Interlocked.Add(ref reusedFiles, cached.FileCount);
                    }
                    else
                    {
                        foreach (var child in cached.Children)
                        {
                            workToken.ThrowIfCancellationRequested();
                            var clone = child.CopyMetadata(current);
                            if (clone.IsDirectory) { clone.Size = clone.FileCount = clone.FolderCount = 0; clone.AllocatedSize = clone.UnknownAllocationFiles = 0; }
                            else Interlocked.Increment(ref reusedFiles);
                            Publish(clone);
                        }
                    }
                    return;
                }
                if (current != root) { NativeStorage.TryRead(current.Path, true, directory.Attributes, out var directoryStorage, out _); current.Storage = directoryStorage; }
                Interlocked.Increment(ref readDirectories);
                var verifiedFiles = refresh?.MetadataValidated == true && refresh.VerifiedDirectoryPaths.Contains(current.Path) && refresh.PreviousDirectories.TryGetValue(current.Path, out var verifiedDirectory)
                    ? verifiedDirectory.Children.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase) : null;
                // Read metadata directly from enumeration; avoid a FileInfo object for every file.
                var entries = new FileSystemEnumerable<ScanEntry>(current.Path, (ref FileSystemEntry entry) =>
                    new ScanEntry(entry.FileName.ToString(), entry.Attributes, entry.Length, entry.LastWriteTimeUtc.LocalDateTime), Enumeration);
                foreach (var info in entries)
                {
                    workToken.ThrowIfCancellationRequested();
                    pacer.Worked(workToken);
                    try
                    {
                        var attributes = info.Attributes;
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            Issue(System.IO.Path.Join(current.Path, info.Name), "跳过符号链接、联接点或其他重解析点", current);
                            continue;
                        }
                        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                        string extension = isDirectory ? "文件夹" : extensions.GetOrAdd(DiskEntry.ExtensionOf(info.Name), static key => key);
                        var entry = DiskEntry.FromName(info.Name, isDirectory, current, extension);
                        entry.Modified = info.Modified;
                        if (!isDirectory)
                        {
                            entry.Size = info.Length; entry.FileCount = 1;
                            string filePath = entry.Path;
                            FileStorageInfo? storage = null; string? storageError = null;
                            if (refresh?.MetadataValidated == true && refresh.VerifiedChanges.TryGetValue(filePath, out var changedStorage)) storage = changedStorage;
                            else if (verifiedFiles != null && verifiedFiles.TryGetValue(entry.Name, out var oldFile) && oldFile.Storage != null &&
                                !refresh!.UnverifiedFiles.Contains(filePath) && oldFile.Size == info.Length && oldFile.Modified == info.Modified)
                            { storage = oldFile.Storage; Interlocked.Increment(ref reusedFiles); }
                            else NativeStorage.TryRead(filePath, false, attributes, out storage, out storageError);
                            if (storage != null)
                            {
                                entry.Storage = storage;
                                entry.Size = storage.LogicalBytes;
                                entry.Modified = storage.Modified ?? entry.Modified;
                            }
                            else lock (allocationIssues) if (allocationIssues.Count < 2000) allocationIssues.Add(new(entry.Path, "占用空间未知：" + storageError));
                        }
                        Publish(entry);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        Issue(System.IO.Path.Join(current.Path, info.Name), ex.Message, current);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (current == root) throw new IOException("无法读取所选文件夹，请确认目录仍然存在且具有访问权限。", ex);
                Issue(current.Path, ex.Message, current);
            }
            finally { Flush(); }
        }

        // Newly discovered folders are available immediately, even while the root is still enumerating.
        pending.Writer.TryWrite(root);
        Parallel.ForEachAsync(pending.Reader.ReadAllAsync(token), new ParallelOptions
        {
            MaxDegreeOfParallelism = profile.Workers, CancellationToken = token
        }, (current, workToken) =>
        {
            concurrency.Enter();
            try
            {
                ScanDirectory(current, workToken);
                if (Interlocked.Decrement(ref outstanding) == 0) pending.Writer.TryComplete();
            }
            finally { concurrency.Exit(); }
            return ValueTask.CompletedTask;
        }).GetAwaiter().GetResult();

        Report(root.Path, true, ScanPhase.Finalizing);
        StorageMetrics.Calculate(root, token);

        var toSort = new Stack<DiskEntry>();
        toSort.Push(root);
        while (toSort.TryPop(out var dir))
        {
            token.ThrowIfCancellationRequested();
            lock (dir)
                if (dir.Children.Count > 1) dir.MutableChildren.Sort((a, b) => b.Size.CompareTo(a.Size));
            int checkedChildren = 0;
            foreach (var child in dir.Children)
            {
                if ((++checkedChildren & 255) == 0) token.ThrowIfCancellationRequested();
                if (child.IsDirectory) toSort.Push(child);
            }
        }
        Report(root.Path, true, ScanPhase.Finalizing);
        token.ThrowIfCancellationRequested();
        return new ScanResult(root, timer.Elapsed, skipped, issues)
        {
            AllocationIssues = allocationIssues,
            Statistics = new(options?.Mode ?? ScanMode.HighSpeed, profile.Workers, refresh != null, readDirectories, reusedFiles, refresh == null ? "完整扫描" : "NTFS 日志增量刷新", PeakDirectoryWorkers: concurrency.Peak)
        };
    }
}

public static class SizeFormat
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:0.##} {units[unit]}";
    }
    public static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
    public static string Percent(long part, long total) => total == 0 ? "0%" : $"{(double)part / total:P1}";
}

public record MapItem(string Name, long Size, DiskEntry? Entry, string Category, long Count = 1, bool IsAggregate = false, long UnknownFiles = 0);
public record MapTile(MapItem Item, RectangleF Bounds);

public static class TreemapLayout
{
    // Squarified layout: each row minimizes the worst rectangle aspect ratio.
    public static List<MapTile> Build(IReadOnlyList<MapItem> items, RectangleF bounds)
    {
        var sorted = items.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList();
        var output = new List<MapTile>();
        if (sorted.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0 ||
            !float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Top) ||
            !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom)) return output;
        double total = sorted.Sum(i => (double)i.Size);
        double scale = (double)bounds.Width * bounds.Height / total;
        var row = new List<MapItem>();
        var remaining = bounds;
        int index = 0;
        while (index < sorted.Count)
        {
            double shortSide = Math.Min(remaining.Width, remaining.Height);
            var next = sorted[index];
            if (row.Count == 0 || Worst(row.Append(next), shortSide, scale) <= Worst(row, shortSide, scale))
            {
                row.Add(next);
                index++;
            }
            else
            {
                remaining = Place(row, remaining, scale, output);
                row.Clear();
            }
        }
        if (row.Count > 0) Place(row, remaining, scale, output);
        return output;
    }

    private static double Worst(IEnumerable<MapItem> row, double side, double scale)
    {
        if (side <= 0) return double.PositiveInfinity;
        double min = double.MaxValue, max = 0, sum = 0;
        foreach (var item in row)
        {
            double area = item.Size * scale;
            min = Math.Min(min, area); max = Math.Max(max, area); sum += area;
        }
        return Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
    }

    private static RectangleF Place(List<MapItem> row, RectangleF bounds, double scale, List<MapTile> output)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            foreach (var item in row) output.Add(new MapTile(item, new RectangleF(bounds.X, bounds.Y, 0, 0)));
            return bounds;
        }
        bool vertical = bounds.Width >= bounds.Height;
        double area = row.Sum(i => i.Size * scale);
        float strip = (float)(area / (vertical ? bounds.Height : bounds.Width));
        strip = Math.Min(strip, vertical ? bounds.Width : bounds.Height);
        float cursor = vertical ? bounds.Y : bounds.X;
        for (int i = 0; i < row.Count; i++)
        {
            float length = strip > 0 ? (float)(row[i].Size * scale / strip) : 0;
            if (i == row.Count - 1) length = Math.Max(0, (vertical ? bounds.Bottom : bounds.Right) - cursor);
            var rect = vertical ? new RectangleF(bounds.X, cursor, strip, length) : new RectangleF(cursor, bounds.Y, length, strip);
            output.Add(new MapTile(row[i], rect));
            cursor += length;
        }
        return vertical ? new RectangleF(bounds.X + strip, bounds.Y, Math.Max(0, bounds.Width - strip), bounds.Height)
            : new RectangleF(bounds.X, bounds.Y + strip, bounds.Width, Math.Max(0, bounds.Height - strip));
    }
}
