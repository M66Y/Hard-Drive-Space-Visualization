namespace DiskAtlas;

// Counter checks and a small, nonce-owned directory of empty synthetic files.
// No raw volume, snapshot, elevation, personal path or link creation is used.
internal static class ScanTraversalBudgetTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition)
            {
                Console.Error.WriteLine("FAIL traversal budget " + label);
                throw new InvalidOperationException("TraversalCheck:" + label);
            }
            passed++; Console.WriteLine("PASS traversal budget " + label);
        }
        void Limit(Action action, ScanTraversalLimitKind expected, string label)
        {
            try { action(); }
            catch (ScanTraversalLimitException error) { Check(error.Kind == expected, label); return; }
            throw new InvalidOperationException("TraversalLimitMissing:" + label);
        }
        static ScanTraversalBudget Budget(int entries = 512, long names = 65536, long paths = 65536, int queue = 512)
            => new(entries, names, paths, Math.Min(entries, queue));

        var rootOnly = Budget(entries: 1); rootOnly.Begin(3);
        Limit(() => rootOnly.ObserveEntry(1, 0, false), ScanTraversalLimitKind.Entries, "root consumes entry allowance");
        var skipped = Budget(entries: 3); skipped.Begin(3);
        // Observation precedes policy filtering: a subsequently skipped entry
        // still spends one entry and its UTF-16 name allowance.
        skipped.ObserveEntry(4, 0, false);
        skipped.ObserveEntry(2, 0, true);
        Limit(() => skipped.ObserveEntry(1, 0, false), ScanTraversalLimitKind.Entries, "filtered entry still counted");

        var names = Budget(names: 5); names.Begin(3);
        names.ObserveEntry(2, 0, false); names.ObserveEntry(3, 0, false);
        Check(names.Failure == ScanTraversalLimitKind.None, "name exact allowance");
        Limit(() => names.ObserveEntry(1, 0, false), ScanTraversalLimitKind.Names, "name overflow");
        Limit(() => names.EnqueueDirectory(), ScanTraversalLimitKind.Names, "first failure remains sticky");
        var unicode = Budget(names: 2); unicode.Begin(3);
        unicode.ObserveEntry("\U0001F339".Length, 0, false);
        Limit(() => unicode.ObserveEntry(1, 0, false), ScanTraversalLimitKind.Names, "UTF16 character accounting");

        var paths = Budget(paths: 6); paths.Begin(3);
        paths.ObserveEntry(10, 0, false); // Full file paths are not retained.
        paths.ObserveEntry(2, 0, true); paths.ObserveEntry(1, 2, true);
        Check(paths.Failure == ScanTraversalLimitKind.None, "directory relative path exact allowance");
        Limit(() => paths.ObserveEntry(1, 0, true), ScanTraversalLimitKind.Paths, "directory path overflow");
        var absolute = Budget(); absolute.Begin(32765); absolute.ObserveEntry(1, 0, false);
        Check(absolute.Failure == ScanTraversalLimitKind.None, "absolute path exact boundary");
        Limit(() => absolute.ObserveEntry(2, 0, false), ScanTraversalLimitKind.Paths, "absolute path overflow");
        var emptyNames = Budget(names: 0); emptyNames.Begin(3);
        Limit(() => emptyNames.ObserveEntry(1, 0, false), ScanTraversalLimitKind.Names, "zero name allowance");
        var emptyPaths = Budget(paths: 0); emptyPaths.Begin(3); emptyPaths.ObserveEntry(1, 0, false);
        Limit(() => emptyPaths.ObserveEntry(1, 0, true), ScanTraversalLimitKind.Paths, "zero directory path allowance");

        var queue = Budget(queue: 2); queue.Begin(3);
        queue.EnqueueDirectory(); queue.EnqueueDirectory(); queue.DequeueDirectory(); queue.EnqueueDirectory();
        Check(queue.Failure == ScanTraversalLimitKind.None, "queue exact capacity and slot reuse");
        Limit(queue.EnqueueDirectory, ScanTraversalLimitKind.Queue, "queue overflow");
        var underflow = Budget(); underflow.Begin(3);
        Limit(underflow.DequeueDirectory, ScanTraversalLimitKind.InvalidUse, "queue underflow");
        var inactive = Budget();
        Limit(inactive.EnqueueDirectory, ScanTraversalLimitKind.InvalidUse, "inactive budget rejected");
        var single = Budget(); single.Begin(3);
        Limit(() => single.Begin(3), ScanTraversalLimitKind.InvalidUse, "budget is single use");
        foreach (var input in new[] { (Name: 0, Parent: 0), (Name: 256, Parent: 0), (Name: 1, Parent: -1), (Name: 1, Parent: 32768) })
        {
            var invalid = Budget(); invalid.Begin(3);
            Limit(() => invalid.ObserveEntry(input.Name, input.Parent, false), ScanTraversalLimitKind.InvalidUse, "invalid observed lengths");
        }
        foreach (int length in new[] { 0, 32768 })
        {
            var invalid = Budget();
            Limit(() => invalid.Begin(length), ScanTraversalLimitKind.InvalidUse, "invalid root length");
        }
        foreach (Action invalidConstructor in new Action[]
        {
            () => _ = new ScanTraversalBudget(0), () => _ = new ScanTraversalBudget(1_048_577),
            () => _ = new ScanTraversalBudget(maxNameCharacters: -1), () => _ = new ScanTraversalBudget(maxNameCharacters: 64L * 1024 * 1024 + 1),
            () => _ = new ScanTraversalBudget(maxPathCharacters: -1), () => _ = new ScanTraversalBudget(maxPathCharacters: 64L * 1024 * 1024 + 1),
            () => _ = new ScanTraversalBudget(maxQueuedDirectories: 0), () => _ = new ScanTraversalBudget(2, maxQueuedDirectories: 3)
        })
        {
            bool rejected = false;
            try { invalidConstructor(); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "invalid constructor allowance");
        }

        var concurrent = Budget(entries: 65); concurrent.Begin(3);
        int accepted = 0, rejectedEntries = 0;
        Parallel.For(0, 128, _ =>
        {
            try { concurrent.ObserveEntry(1, 0, false); Interlocked.Increment(ref accepted); }
            catch (ScanTraversalLimitException error) when (error.Kind == ScanTraversalLimitKind.Entries) { Interlocked.Increment(ref rejectedEntries); }
        });
        Check(accepted == 64 && rejectedEntries == 64, "concurrent entries never overrun");
        var concurrentNames = Budget(names: 32); concurrentNames.Begin(3);
        int acceptedNames = 0, rejectedNames = 0;
        Parallel.For(0, 64, _ =>
        {
            try { concurrentNames.ObserveEntry(1, 0, false); Interlocked.Increment(ref acceptedNames); }
            catch (ScanTraversalLimitException error) when (error.Kind == ScanTraversalLimitKind.Names) { Interlocked.Increment(ref rejectedNames); }
        });
        Check(acceptedNames == 32 && rejectedNames == 32, "concurrent names never overrun");

        var fixture = new Fixture();
        try
        {
            fixture.Create();
            var ordinary = ScanBounded(fixture, token => new DiskScanner(8).Scan(fixture.Scope, null, token));
            int entries = ordinary.Root.Descendants().Count() + 1;
            var exact = Budget(entries: entries);
            var guarded = ScanBounded(fixture, token => new DiskScanner(8, exact).Scan(fixture.Scope, null, token, options: null));
            Check(SameTree(ordinary, guarded), "guarded result matches ordinary scanner");
            Check(!guarded.Root.Incomplete && guarded.Skipped == 0 && guarded.Root.FileCount == 129 && guarded.Root.FolderCount == 16,
                "empty synthetic fixture remains complete");
            Limit(() => new DiskScanner(8, exact).Scan(fixture.Scope, null, default, options: null),
                ScanTraversalLimitKind.InvalidUse, "scanner cannot reuse completed budget");
            Limit(() => ScanBounded(fixture, token => new DiskScanner(1, Budget(queue: 1)).Scan(fixture.Scope, null, token, options: null)),
                ScanTraversalLimitKind.Queue, "single worker wide root exits on queue limit");
            Limit(() => ScanBounded(fixture, token => new DiskScanner(8, Budget(entries: 24)).Scan(fixture.Scope, null, token, options: null)),
                ScanTraversalLimitKind.Entries, "eight workers exit on entry limit");
            Limit(() => ScanBounded(fixture, token => new DiskScanner(8, Budget(names: 0)).Scan(fixture.Scope, null, token, options: null)),
                ScanTraversalLimitKind.Names, "name limit escapes instead of partial result");
            Limit(() => ScanBounded(fixture, token => new DiskScanner(8, Budget(paths: 0)).Scan(fixture.Scope, null, token, options: null)),
                ScanTraversalLimitKind.Paths, "path limit escapes instead of partial result");

            using var preCancelled = new CancellationTokenSource(); preCancelled.Cancel();
            var preBudget = Budget(); bool preStopped = false;
            try { new DiskScanner(8, preBudget).Scan(fixture.Scope, null, preCancelled.Token); }
            catch (OperationCanceledException error) { preStopped = error.CancellationToken == preCancelled.Token; }
            Check(preStopped && preBudget.Failure == ScanTraversalLimitKind.None, "pre cancellation retains caller token");
            Check(SameTree(ordinary, ScanBounded(fixture, token => new DiskScanner(8, preBudget).Scan(fixture.Scope, null, token))),
                "pre cancellation does not consume budget");

            bool duringStopped = false;
            try
            {
                ScanBounded(fixture, token =>
                {
                    using var during = CancellationTokenSource.CreateLinkedTokenSource(token);
                    return new DiskScanner(8, Budget()).Scan(fixture.Scope, new InlineProgress(_ => during.Cancel()), during.Token);
                });
            }
            catch (OperationCanceledException) { duringStopped = true; }
            Check(duringStopped, "inline progress cancellation exits bounded workers");
            Check(SameTree(ordinary, ScanBounded(fixture, token => new DiskScanner(8).Scan(fixture.Scope, null, token))),
                "failed and cancelled scans leave ordinary scan unchanged");
        }
        finally { fixture.Cleanup(); }
        Console.WriteLine("PASS traversal budget checks=" + passed);
        return passed;
    }

    private sealed class InlineProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    { public void Report(ScanProgress value) => report(value); }

    private static ScanResult ScanBounded(Fixture fixture, Func<CancellationToken, ScanResult> scan)
    {
        var cancellation = new CancellationTokenSource();
        Task<ScanResult> task = Task.Run(() => scan(cancellation.Token));
        try { return task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
        catch (TimeoutException)
        {
            cancellation.Cancel();
            try { task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            catch (TimeoutException)
            {
                // Never delete a fixture underneath a worker that failed to stop.
                fixture.AllowCleanup = false;
                _ = task.ContinueWith(completed => { _ = completed.Exception; cancellation.Dispose(); }, TaskScheduler.Default);
            }
            catch { /* The worker settled after defensive cancellation. */ }
            throw new InvalidOperationException("TraversalScanExceededTimeout");
        }
        finally { if (task.IsCompleted) cancellation.Dispose(); }
    }

    private static bool SameTree(ScanResult left, ScanResult right)
    {
        if (left.Skipped != right.Skipped || left.Issues.Count != right.Issues.Count || left.AllocationIssues.Count != right.AllocationIssues.Count)
            return false;
        var a = left.Root.Descendants().Prepend(left.Root).OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        var b = right.Root.Descendants().Prepend(right.Root).OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        return a.Length == b.Length && a.Zip(b).All(pair => pair.First.Path == pair.Second.Path && pair.First.Name == pair.Second.Name &&
            pair.First.IsDirectory == pair.Second.IsDirectory && pair.First.Size == pair.Second.Size && pair.First.FileCount == pair.Second.FileCount &&
            pair.First.FolderCount == pair.Second.FolderCount && pair.First.Incomplete == pair.Second.Incomplete && pair.First.Modified == pair.Second.Modified &&
            pair.First.Storage == pair.Second.Storage && pair.First.AllocatedSize == pair.Second.AllocatedSize &&
            pair.First.UnknownAllocationFiles == pair.Second.UnknownAllocationFiles && pair.First.SharedAllocation == pair.Second.SharedAllocation);
    }

    private sealed class Fixture
    {
        private const string Prefix = "AsukaLens-traversal-";
        private readonly string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private readonly string nonce = Guid.NewGuid().ToString("N");
        private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        private bool owned;
        internal bool AllowCleanup { get; set; } = true;
        private string Root => Path.GetFullPath(Path.Combine(temp, Prefix + nonce));
        internal string Scope => Path.Combine(Root, "scope");
        internal void Create()
        {
            if (Directory.Exists(Root) || File.Exists(Root)) throw new InvalidOperationException("TraversalFixtureCollision");
            Directory.CreateDirectory(Root); owned = true;
            Empty(".owner-" + nonce); Directory.CreateDirectory(Scope); directories.Add("scope");
            Empty(Path.Combine("scope", "root.bin"));
            for (int directory = 0; directory < 16; directory++)
            {
                string relative = Path.Combine("scope", "d" + directory.ToString("D2"));
                Directory.CreateDirectory(Path.Combine(Root, relative)); directories.Add(relative);
                for (int file = 0; file < 8; file++) Empty(Path.Combine(relative, "f" + file.ToString("D2") + ".bin"));
            }
            // Newly created directory enumeration timestamps can settle after
            // file handles close. Pin this immutable fixture before comparing
            // the two independent scans, including their Modified metadata.
            var modified = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            foreach (string relative in files) File.SetLastWriteTimeUtc(Path.Combine(Root, relative), modified);
            foreach (string relative in directories.OrderByDescending(value => value.Length))
                Directory.SetLastWriteTimeUtc(Path.Combine(Root, relative), modified);
        }
        private void Empty(string relative)
        {
            using var stream = new FileStream(Path.Combine(Root, relative), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            files.Add(relative);
        }
        internal void Cleanup()
        {
            if (!owned) return;
            if (!AllowCleanup) throw new InvalidOperationException("TraversalFixtureCleanupWorkerActive");
            if (!string.Equals(Path.GetDirectoryName(Root), temp, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(Root) != Prefix + nonce ||
                (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0 || !files.Contains(".owner-" + nonce))
                throw new InvalidOperationException("TraversalFixtureCleanupRefused");
            var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(); pending.Push(Root);
            while (pending.TryPop(out string? current))
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                string relative = Path.GetRelativePath(Root, entry);
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("TraversalFixtureCleanupLink");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!directories.Contains(relative)) throw new InvalidOperationException("TraversalFixtureCleanupUnknownDirectory");
                    seenDirectories.Add(relative); pending.Push(entry);
                }
                else
                {
                    if (!files.Contains(relative) || new FileInfo(entry).Length != 0) throw new InvalidOperationException("TraversalFixtureCleanupUnknownFile");
                    seenFiles.Add(relative);
                }
            }
            if (!seenDirectories.SetEquals(directories) || !seenFiles.SetEquals(files)) throw new InvalidOperationException("TraversalFixtureCleanupChanged");
            foreach (string relative in files)
            {
                string path = Path.Combine(Root, relative);
                if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || new FileInfo(path).Length != 0)
                    throw new InvalidOperationException("TraversalFixtureCleanupChanged");
                File.Delete(path);
            }
            foreach (string relative in directories.OrderByDescending(value => value.Length))
            {
                string path = Path.Combine(Root, relative);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("TraversalFixtureCleanupLink");
                Directory.Delete(path, recursive: false);
            }
            Directory.Delete(Root, recursive: false); owned = false;
            Console.WriteLine("CLEANUP traversal budget completed=True");
        }
    }
}
