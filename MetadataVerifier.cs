using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace DiskAtlas;

internal static class MetadataVerifier
{
    private readonly record struct Entry(string Name, FileAttributes Attributes);
    public static (RefreshPlan Plan, long Directories, long Files, int PeakWorkers) Verify(DiskEntry root, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var directories = root.Descendants().Prepend(root).Where(e => e.IsDirectory).ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        if (directories.Values.Any(d => d.Storage == null)) throw new IOException("旧快照的目录身份不完整");
        var dirty = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var changedIds = new ConcurrentDictionary<FileIdentity, byte>();
        var changedStorage = new ConcurrentDictionary<string, FileStorageInfo>(StringComparer.OrdinalIgnoreCase);
        var verifiedPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var unverifiedFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var profile = ScanPolicy.Resolve(options.Mode, NativeStorage.DetectDisk(root.Path));
        var pacer = new ScanWorkPacer(profile);
        var concurrency = new ScanConcurrencyMeter();
        long checkedDirs = 0, checkedFiles = 0, lastReport = -100;
        var clock = Stopwatch.StartNew(); var gate = new object();
        void Report(string path, bool force = false)
        {
            if (progress == null || (!force && clock.ElapsedMilliseconds - Volatile.Read(ref lastReport) < 100)) return;
            lock (gate)
            {
                if (!force && clock.ElapsedMilliseconds - lastReport < 100) return;
                Volatile.Write(ref lastReport, clock.ElapsedMilliseconds);
                long completed = Interlocked.Read(ref checkedDirs);
                progress.Report(new(Interlocked.Read(ref checkedFiles), completed, 0, "核验缓存：" + path,
                    ScanPhase.Verifying, profile.Workers, completed, directories.Count));
            }
        }
        Parallel.ForEach(directories.Values, new ParallelOptions { MaxDegreeOfParallelism = profile.Workers, CancellationToken = token }, directory =>
        {
            token.ThrowIfCancellationRequested();
            Report(directory.Path);
            concurrency.Enter();
            try
            {
                pacer.Worked(token);
                var info = new DirectoryInfo(directory.Path); info.Refresh();
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    !NativeStorage.TryReadSnapshot(directory.Path, true, info.Attributes, out var identity, out _) || identity.Identity != directory.Storage!.Identity)
                {
                    dirty[directory.Path] = 0; if (directory.Parent != null) dirty[directory.Parent.Path] = 0; return;
                }
                if (info.LastWriteTime != directory.Modified) dirty[directory.Path] = 0;
                var old = directory.Children.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
                int seen = 0;
                int batch = 0;
                var entries = new FileSystemEnumerable<Entry>(directory.Path, (ref FileSystemEntry entry) => new(entry.FileName.ToString(), entry.Attributes),
                    new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, BufferSize = 64 * 1024 });
                foreach (var item in entries)
                {
                    token.ThrowIfCancellationRequested();
                    pacer.Worked(token);
                    if (++batch == 256) { Report(directory.Path); batch = 0; }
                    bool isDirectory = (item.Attributes & FileAttributes.Directory) != 0;
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || !old.TryGetValue(item.Name, out var cached) || cached.Name != item.Name || cached.IsDirectory != isDirectory)
                    { dirty[directory.Path] = 0; continue; }
                    seen++;
                    if (!isDirectory)
                    {
                        Interlocked.Increment(ref checkedFiles);
                        bool read = NativeStorage.TryReadSnapshot(cached.Path, false, item.Attributes, out var storage, out _);
                        if (!read || !storage.Matches(cached.Storage))
                        {
                            dirty[directory.Path] = 0;
                            if (cached.Storage != null) changedIds[cached.Storage.Identity] = 0;
                            if (read) changedStorage[cached.Path] = storage.ToInfo();
                            else unverifiedFiles[cached.Path] = 0;
                        }
                    }
                }
                if (seen != directory.Children.Count) dirty[directory.Path] = 0;
                verifiedPaths[directory.Path] = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { dirty[directory.Path] = 0; if (directory.Parent != null) dirty[directory.Parent.Path] = 0; }
            finally { concurrency.Exit(); Interlocked.Increment(ref checkedDirs); Report(directory.Path); }
        });
        Report(root.Path, true);
        // A write through any alias invalidates every known in-scope hard-link path.
        foreach (var entry in root.Descendants())
        {
            token.ThrowIfCancellationRequested();
            if (!entry.IsDirectory && entry.Storage != null && changedIds.ContainsKey(entry.Storage.Identity)) dirty[entry.Parent!.Path] = 0;
        }
        return (RefreshPlan.FromDirty(root, directories, dirty.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)) with
            { MetadataValidated = true, VerifiedChanges = changedStorage, VerifiedDirectoryPaths = verifiedPaths.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                UnverifiedFiles = unverifiedFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) }, checkedDirs, checkedFiles, concurrency.Peak);
    }
}
