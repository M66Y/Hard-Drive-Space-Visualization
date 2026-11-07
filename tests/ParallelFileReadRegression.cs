using DiskAtlas;
using System.Runtime.InteropServices;

internal static class ParallelFileReadRegression
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr reserved);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Parallel file read invariant failed"); }
    private static FileReadResult Native(string path, FileAttributes attributes)
    { NativeStorage.TryRead(path, false, attributes, out var storage, out var error); return new(storage, error); }
    private sealed class Inline(Action<ScanProgress> action) : IProgress<ScanProgress>
    { public void Report(ScanProgress value) => action(value); }

    internal static int Run()
    {
        string workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string root = Path.Combine(workspace, "artifacts", "parallel-file-tests-" + Guid.NewGuid().ToString("N"));
        int passed = 0;
        void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
        void Equal(DiskEntry a, DiskEntry b)
        {
            var x = a.Descendants().Prepend(a).OrderBy(e => e.Path).ToArray();
            var y = b.Descendants().Prepend(b).OrderBy(e => e.Path).ToArray(); Check(x.Length == y.Length);
            for (int i = 0; i < x.Length; i++)
                Check(x[i].Path == y[i].Path && x[i].IsDirectory == y[i].IsDirectory && x[i].Size == y[i].Size &&
                    x[i].AllocatedSize == y[i].AllocatedSize && x[i].Modified == y[i].Modified && x[i].FileCount == y[i].FileCount &&
                    x[i].FolderCount == y[i].FolderCount && x[i].UnknownAllocationFiles == y[i].UnknownAllocationFiles &&
                    x[i].Incomplete == y[i].Incomplete && x[i].SharedAllocation == y[i].SharedAllocation && x[i].Storage == y[i].Storage);
        }
        void Create(string directory, int count)
        {
            Directory.CreateDirectory(directory);
            for (int i = 0; i < count; i++) File.WriteAllBytes(Path.Combine(directory, $"sample-{i:0000}.bin"), new byte[1024 + i % 23]);
            Directory.SetLastWriteTimeUtc(directory, new DateTime(2020, 1, 1));
        }
        ScanResult Full(string directory, ScanMode mode = ScanMode.HighSpeed) => new DiskScanner().Scan(directory, null, CancellationToken.None, options: new(mode));
        try
        {
            Test("shared read budget caps competing workers, cancels waiters and releases permits", () =>
            {
                using var entered = new CountdownEvent(8); using var release = new ManualResetEventSlim();
                using var cancel = new CancellationTokenSource(); int calls = 0;
                using var budget = new FileReadBudget((_, _) =>
                {
                    if (Interlocked.Increment(ref calls) <= 8) entered.Signal();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                    return new(null, "synthetic");
                });
                var workers = Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(
                    () => budget.ReadAsync("synthetic", 0, cancel.Token).AsTask(), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToArray();
                try { Check(entered.Wait(TimeSpan.FromSeconds(8))); Check(calls == 8 && budget.PeakReaders == 8); }
                finally { cancel.Cancel(); release.Set(); }
                try { Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                Check(workers.All(t => t.IsCompleted) && calls == 8);
                budget.ReadAsync("synthetic", 0, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                Check(calls == 9);
            });
            Test("native read exceptions release all permits", () =>
            {
                int calls = 0;
                using var budget = new FileReadBudget((_, _) =>
                { if (Interlocked.Increment(ref calls) <= 16) throw new IOException("synthetic"); return new(null, "synthetic"); });
                for (int i = 0; i < 16; i++)
                    try { budget.ReadAsync("synthetic", 0, CancellationToken.None).GetAwaiter().GetResult(); throw new InvalidOperationException(); }
                    catch (IOException) { }
                budget.ReadAsync("synthetic", 0, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                Check(calls == 17 && budget.PeakReaders == 1);
            });
            string flat = Path.Combine(root, "flat"); Create(flat, 1200);
            Test("flat full scan matches serial metadata including hard-link ownership", () =>
            {
                Check(CreateHardLinkW(Path.Combine(flat, "alias.bin"), Path.Combine(flat, "sample-0000.bin"), IntPtr.Zero));
                Directory.SetLastWriteTimeUtc(flat, new DateTime(2020, 1, 1));
                var result = Full(flat); var serial = new DiskScanner(1).Scan(flat, null, CancellationToken.None);
                Equal(result.Root, serial.Root);
                Check(result.Skipped == 0 && result.Root.UnknownAllocationFiles == 0 && result.Root.Descendants().Any(e => e.SharedAllocation));
                Check(result.Statistics is { FileReaderLimit: 8, ParallelFileReads: 689, PeakFileReaders: > 0 and <= 8, PeakBufferedFiles: 256 });
                Check(RefreshPresentation.Details(result.Statistics!).Contains("文件读取调用峰值"));
            });
            Test("other modes and metadata refresh retain the prior strategy", () =>
            {
                var full = Full(flat);
                foreach (var mode in new[] { ScanMode.Automatic, ScanMode.LowImpact })
                { var other = Full(flat, mode); Equal(full.Root, other.Root); Check(other.Statistics is { FileReaderLimit: 0, ParallelFileReads: 0 }); }
                var refreshed = new ScanCoordinator(_ => null).Scan(flat, full, new(ScanMode.HighSpeed), null, CancellationToken.None);
                Equal(full.Root, refreshed.Root);
                Check(refreshed.Statistics is { Method: RefreshMethod.MetadataVerification, FileReaderLimit: 0, ParallelFileReads: 0, VerifiedFiles: 1201 });
            });
            Test("large directories share one I/O budget and bounded file buffers", () =>
            {
                string wide = Path.Combine(root, "wide"); Directory.CreateDirectory(wide);
                for (int d = 0; d < 12; d++) Create(Path.Combine(wide, $"folder-{d}"), 801);
                Directory.SetLastWriteTimeUtc(wide, new DateTime(2020, 1, 1));
                int active = 0, peak = 0;
                var scanner = new DiskScanner((path, attributes) =>
                {
                    int count = Interlocked.Increment(ref active), seen;
                    while (count > (seen = Volatile.Read(ref peak))) if (Interlocked.CompareExchange(ref peak, count, seen) == seen) break;
                    try { return Native(path, attributes); } finally { Interlocked.Decrement(ref active); }
                });
                var result = scanner.Scan(wide, null, CancellationToken.None, options: new(ScanMode.HighSpeed));
                Equal(result.Root, new DiskScanner(1).Scan(wide, null, CancellationToken.None).Root);
                Check(active == 0 && peak is > 1 and <= 8 && result.Statistics!.PeakFileReaders == peak &&
                    result.Statistics.PeakBufferedFiles is > 256 and <= 2048 && result.Statistics.ParallelFileReads == 12 * 289);
            });
            Test("cancellation waits for active batch reads and stops publication", () =>
            {
                using var cancel = new CancellationTokenSource(); using var reached = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                int calls = 0, active = 0; var live = new LiveScanSession();
                var scanner = new DiskScanner((path, attributes) =>
                {
                    Interlocked.Increment(ref active);
                    try
                    {
                        if (Interlocked.Increment(ref calls) > 512) { reached.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
                        return Native(path, attributes);
                    }
                    finally { Interlocked.Decrement(ref active); }
                });
                var task = Task.Run(() => scanner.Scan(flat, null, cancel.Token, live, new(ScanMode.HighSpeed)));
                try { Check(reached.Wait(TimeSpan.FromSeconds(8))); cancel.Cancel(); }
                finally { release.Set(); }
                try { task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); throw new InvalidOperationException(); }
                catch (OperationCanceledException) { }
                Check(active == 0 && calls <= 520);
                Equal(Full(flat).Root, new DiskScanner(1).Scan(flat, null, CancellationToken.None).Root);
            });
            Test("batch read failures remain explicit and unknown allocation is not zero", () =>
            {
                int calls = 0;
                var scanner = new DiskScanner((path, attributes) => Interlocked.Increment(ref calls) switch
                { 513 => throw new UnauthorizedAccessException("synthetic"), 514 => new(null, "synthetic unknown"), _ => Native(path, attributes) });
                var result = scanner.Scan(flat, null, CancellationToken.None, options: new(ScanMode.HighSpeed));
                Check(result.Skipped == 1 && result.Root.Incomplete && result.Root.FileCount == 1200 &&
                    result.Root.UnknownAllocationFiles == 1 && result.AllocationIssues.Count == 1 && result.Issues.Count == 1);
            });
            Test("replacement after enumeration uses fresh native size and identity", () =>
            {
                int calls = 0; string? changed = null;
                var scanner = new DiskScanner((path, attributes) =>
                {
                    if (Interlocked.Increment(ref calls) == 513)
                    { changed = path; File.Delete(path); File.WriteAllBytes(path, new byte[32771]); Directory.SetLastWriteTimeUtc(flat, new DateTime(2020, 1, 1)); }
                    return Native(path, attributes);
                });
                var result = scanner.Scan(flat, null, CancellationToken.None, options: new(ScanMode.HighSpeed));
                Check(changed != null && result.Root.Descendants().Single(e => e.Path == changed).Size == 32771);
                Equal(result.Root, new DiskScanner(1).Scan(flat, null, CancellationToken.None).Root);
            });
            Test("missing files stay unknown and open writers remain tracked", () =>
            {
                int calls = 0; string? deleted = null, lockedPath = null;
                var scanner = new DiskScanner((path, attributes) =>
                {
                    int call = Interlocked.Increment(ref calls);
                    if (call == 513) { deleted = path; File.Delete(path); }
                    if (call == 514)
                    { lockedPath = path; using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return Native(path, attributes); }
                    return Native(path, attributes);
                });
                var result = scanner.Scan(flat, null, CancellationToken.None, options: new(ScanMode.HighSpeed));
                Check(result.Root.UnknownAllocationFiles == 1 && result.AllocationIssues.Count == 1 && result.Root.FileCount == 1201);
                Check(lockedPath != null && result.Root.Descendants().Single(e => e.Path == lockedPath).Storage is { WritableHandleOpen: true });
                Check(deleted != null); File.WriteAllBytes(deleted!, new byte[1024]);
                File.WriteAllText(Path.Combine(flat, "new-after-scan.txt"), "synthetic new file");
                Directory.SetLastWriteTimeUtc(flat, new DateTime(2020, 1, 1));
                var refresh = new ScanCoordinator(_ => null).Scan(flat, result, new(ScanMode.HighSpeed), null, CancellationToken.None);
                Check(refresh.Statistics is { Method: RefreshMethod.FullScan } && refresh.Root.UnknownAllocationFiles == 0 && refresh.Root.FileCount == 1202);
                Equal(refresh.Root, new DiskScanner(1).Scan(flat, null, CancellationToken.None).Root);
            });
            Test("unexpected batch failures unwind and release active readers", () =>
            {
                int calls = 0, active = 0;
                var scanner = new DiskScanner((path, attributes) =>
                {
                    Interlocked.Increment(ref active);
                    try { if (Interlocked.Increment(ref calls) == 513) throw new InvalidOperationException("synthetic"); return Native(path, attributes); }
                    finally { Interlocked.Decrement(ref active); }
                });
                var task = Task.Run(() => scanner.Scan(flat, null, CancellationToken.None, options: new(ScanMode.HighSpeed)));
                try { task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); throw new Exception("Unexpected success"); }
                catch (InvalidOperationException) { }
                Check(active == 0 && calls <= 768);
                Equal(Full(flat).Root, new DiskScanner(1).Scan(flat, null, CancellationToken.None).Root);
            });
            Console.WriteLine($"All {passed} parallel file read checks passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL parallel file read checks: " + ex.GetType().Name); return 1; }
        finally
        {
            if (Directory.Exists(root))
            {
                Check(Path.GetFullPath(root).StartsWith(Path.Combine(workspace, "artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                Check(!Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0));
                Directory.Delete(root, true);
            }
        }
    }
}
