using System.Diagnostics;

namespace DiskAtlas;

public record ScanPreview(DiskEntry Root, DiskEntry Directory, int DiscoveredChildren, bool Truncated, TimeSpan Elapsed, long Skipped);

// Pull-based snapshots keep progress callbacks small and never expose a mutable scan tree to the UI.
public sealed class LiveScanSession
{
    public const int PreviewLimit = 1000;
    private DiskEntry? root;
    private long skipped;
    private readonly Stopwatch timer = Stopwatch.StartNew();
    public string? RootPath => Volatile.Read(ref root)?.Path;

    internal void Attach(DiskEntry entry)
    {
        if (Interlocked.CompareExchange(ref root, entry, null) != null)
            throw new InvalidOperationException("每次扫描需要独立的预览会话。");
    }

    internal void RecordSkip() => Interlocked.Increment(ref skipped);

    public ScanPreview? Capture(string? path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sourceRoot = Volatile.Read(ref root);
        if (sourceRoot == null) return null;
        var source = sourceRoot;
        if (path != null)
        {
            string relative = System.IO.Path.GetRelativePath(sourceRoot.Path, path);
            if (relative != ".")
            {
                if (System.IO.Path.IsPathRooted(relative)) return null;
                foreach (string part in relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
                {
                    token.ThrowIfCancellationRequested();
                    if (part == "..") return null;
                    DiskEntry? child;
                    lock (source) child = source.Children.FirstOrDefault(c => c.IsDirectory && c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                    if (child == null) return null;
                    source = child;
                }
            }
        }
        var ancestors = new Stack<DiskEntry>();
        for (var entry = source; entry != null; entry = entry.Parent) ancestors.Push(entry);
        DiskEntry? snapshotRoot = null, directory = null;
        while (ancestors.TryPop(out var entry))
        {
            token.ThrowIfCancellationRequested();
            var clone = entry.CopyMetadata(directory);
            directory?.MutableChildren.Add(clone);
            snapshotRoot ??= clone; directory = clone;
        }
        DiskEntry[] children;
        int count;
        lock (source)
        {
            count = source.Children.Count;
            children = new DiskEntry[Math.Min(count, PreviewLimit)];
            for (int i = 0; i < children.Length; i++) children[i] = source.Children[i];
        }
        long shownBytes = 0, shownFiles = 0, shownFolders = 0;
        foreach (var child in children)
        {
            token.ThrowIfCancellationRequested();
            var clone = child.CopyMetadata(directory);
            directory!.MutableChildren.Add(clone);
            shownBytes += clone.Size; shownFiles += clone.FileCount; shownFolders += clone.FolderCount + (clone.IsDirectory ? 1 : 0);
        }
        // Counters publish in batches, while discovery may advance during this snapshot.
        directory!.Size = Math.Max(directory.Size, shownBytes);
        directory.FileCount = Math.Max(directory.FileCount, shownFiles);
        directory.FolderCount = Math.Max(directory.FolderCount, shownFolders);
        return new ScanPreview(snapshotRoot!, directory, count, count > PreviewLimit, timer.Elapsed, Interlocked.Read(ref skipped));
    }
}
