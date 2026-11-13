using DiskAtlas;

namespace DiskAtlas.NtfsPrototype;

internal static class NtfsPersonalAcceptanceTests
{
    private static readonly DateTime Modified = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static FileIdentity Id(ulong slot, ushort sequence = 1) => new(31, ((ulong)sequence << 48) | slot, 0);
    private static FileStorageInfo Info(ulong slot, long logical, long allocation, uint links = 1) => new(Id(slot), allocation, links, false, logical, Modified);
    private static DiskEntry File(DiskEntry parent, string name, ulong slot, long logical, long allocation, uint links = 1) =>
        new(Path.Combine(parent.Path, name), false, parent) { Storage = Info(slot, logical, allocation, links), Modified = Modified,
            Size = logical, FileCount = 1 };
    private static DiskEntry Fixture()
    {
        var root = new DiskEntry(@"X:\Synthetic-Acceptance", true) { Storage = Info(5, -1, 0), Modified = Modified, Size = 50, FileCount = 3, FolderCount = 1 };
        var child = new DiskEntry(Path.Combine(root.Path, "cache"), true, root) { Storage = Info(6, -1, 0), Modified = Modified, Size = 30, FileCount = 2 };
        root.MutableChildren.Add(File(root, "a.bin", 20, 20, 32, 2)); root.MutableChildren.Add(child);
        child.MutableChildren.Add(File(child, "alias.bin", 20, 20, 32, 2)); child.MutableChildren.Add(File(child, "b.bin", 21, 10, 16));
        StorageMetrics.Calculate(root, default); return root;
    }
    private static DiskEntry CompactFixture()
    {
        var root = new DiskEntry(@"X:\Synthetic-Acceptance", true) { Storage = Info(5, -1, 0), Modified = Modified, Size = 50, FileCount = 3, FolderCount = 1 };
        var child = DiskEntry.FromName("cache", true, root, "文件夹");
        child.Storage = Info(6, -1, 0); child.Modified = Modified; child.Size = 30; child.FileCount = 2;
        DiskEntry Add(DiskEntry parent, string name, ulong slot, long logical, long allocation, uint links = 1)
        {
            var file = DiskEntry.FromName(name, false, parent, ".bin");
            file.Storage = Info(slot, logical, allocation, links); file.Modified = Modified;
            file.Size = logical; file.FileCount = 1; parent.MutableChildren.Add(file); return file;
        }
        Add(root, "a.bin", 20, 20, 32, 2); root.MutableChildren.Add(child);
        Add(child, "alias.bin", 20, 20, 32, 2); Add(child, "b.bin", 21, 10, 16);
        StorageMetrics.Calculate(root, default); return root;
    }
    private static Func<string, bool, CancellationToken, FileStorageInfo> Reader(DiskEntry root, Action<string, bool>? seen = null)
    {
        var entries = root.Descendants().Prepend(root).ToDictionary(entry => entry == root ? "" : Path.GetRelativePath(root.Path, entry.Path).Replace('\\', '/'));
        var observed = entries.ToDictionary(pair => pair.Key, pair => pair.Value.Storage!);
        return (relative, directory, token) =>
        {
            token.ThrowIfCancellationRequested(); seen?.Invoke(relative, directory);
            if (!entries.TryGetValue(relative, out var entry) || entry.IsDirectory != directory) throw new IOException("Synthetic callback failed");
            return observed[relative];
        };
    }
    internal static int Run()
    {
        int passed = 0;
        void Check(bool value, string code)
        {
            if (!value) { Console.WriteLine("FAIL personal acceptance " + code); throw new InvalidOperationException("Personal acceptance check failed: " + code); }
            passed++; Console.WriteLine("PASS personal acceptance " + code);
        }
        void Reject(DiskEntry root, NtfsPersonalAcceptanceCode code, Func<string, bool, CancellationToken, FileStorageInfo>? reader = null,
            NtfsScanLimits? limits = null)
        {
            try { _ = NtfsPersonalAcceptance.Verify(root, reader ?? ((_, _, _) => throw new IOException("private callback")), limits: limits); }
            catch (NtfsPersonalAcceptanceException error)
            {
                if (error.Code != code) Console.WriteLine("FAIL personal acceptance expected " + code + "; actual " + error.Code);
                Check(error.Code == code && error.Message == code.ToString() && error.InnerException == null, "controlled " + code); return;
            }
            Console.WriteLine("FAIL personal acceptance missing controlled " + code);
            throw new InvalidOperationException("Expected controlled audit failure");
        }
        var valid = Fixture(); var calls = new List<(string, bool)>();
        var result = NtfsPersonalAcceptance.Verify(valid, Reader(valid, (path, directory) => calls.Add((path, directory))));
        Check(result is (true, true), "complete private tree and all object metadata match");
        Check(calls.Count == 4 && calls.Count(item => item.Item2) == 2 && calls.Count(item => !item.Item2) == 2,
            "every unique full identity is queried once including selected root");
        Check(calls[0] == ("", true) && calls.All(item => !item.Item1.Contains('\\') && !Path.IsPathRooted(item.Item1)),
            "relative private paths are constructed only inside the tree");
        Check(valid.Size == 50 && valid.AllocatedSize == 48 && valid.Children[1].AllocatedSize == 16,
            "logical aliases and canonical object allocation remain distinct");
        foreach (var change in new Action<DiskEntry>[]
        {
            root => root.Size++, root => root.FileCount++, root => root.FolderCount++, root => root.AllocatedSize++,
            root => root.Children[1].AllocatedSize++, root => root.Children[0].SharedAllocation = true
        }) { var root = Fixture(); var read = Reader(root); change(root); Reject(root, NtfsPersonalAcceptanceCode.Totals, read); }
        { var root = Fixture(); root.Incomplete = true; Reject(root, NtfsPersonalAcceptanceCode.TreeIncomplete); }
        { var root = Fixture(); root.Children[0].UnknownAllocationFiles = 1; Reject(root, NtfsPersonalAcceptanceCode.TreeIncomplete); }
        { var root = Fixture(); root.Children[0].Storage = null; Reject(root, NtfsPersonalAcceptanceCode.Metadata); }
        { var root = Fixture(); root.Children[0].Modified = DateTime.MinValue; Reject(root, NtfsPersonalAcceptanceCode.Metadata); }
        { var root = Fixture(); root.Children[0].Storage = root.Children[0].Storage! with { LinkCount = 0 }; Reject(root, NtfsPersonalAcceptanceCode.Metadata); }
        { var root = Fixture(); root.Children[1].Storage = root.Storage; Reject(root, NtfsPersonalAcceptanceCode.Identity); }
        { var root = Fixture(); root.Children[0].Storage = root.Children[0].Storage! with { Identity = Id(5) }; Reject(root, NtfsPersonalAcceptanceCode.Identity); }
        { var root = Fixture(); root.Children[0].Storage = root.Children[0].Storage! with { Identity = Id(5, 2) }; Reject(root, NtfsPersonalAcceptanceCode.Identity); }
        { var root = Fixture(); root.Children[0].Storage = root.Children[0].Storage! with { Identity = new(32, Id(20).Low, 0) }; Reject(root, NtfsPersonalAcceptanceCode.Identity); }
        { var root = Fixture(); root.MutableChildren.Add(root.Children[0]); Reject(root, NtfsPersonalAcceptanceCode.TreeShape); }
        { var root = Fixture(); root.MutableChildren.Add(File(new DiskEntry(@"X:\Other", true), "outside.bin", 30, 1, 1)); Reject(root, NtfsPersonalAcceptanceCode.TreeShape); }
        { var root = Fixture(); root.MutableChildren.Add(new DiskEntry(@"X:\Other\outside.bin", false, root) { Storage = Info(30, 1, 1), Modified = Modified, Size = 1, FileCount = 1 }); Reject(root, NtfsPersonalAcceptanceCode.TreePath); }
        { var root = Fixture(); root.MutableChildren.Add(File(root, "..", 30, 1, 1)); Reject(root, NtfsPersonalAcceptanceCode.TreePath); }
        { var root = Fixture(); root.Children[1].MutableChildren[0].Storage = root.Children[0].Storage! with { AllocatedBytes = 33 }; Reject(root, NtfsPersonalAcceptanceCode.Identity); }
        { var root = Fixture(); root.Children[0].Storage = root.Children[0].Storage! with { LinkCount = 1 }; root.Children[1].MutableChildren[0].Storage = root.Children[0].Storage; Reject(root, NtfsPersonalAcceptanceCode.Identity); }
        {
            var root = Fixture(); var read = Reader(root);
            foreach (var change in new Func<FileStorageInfo, FileStorageInfo>[]
            {
                info => info with { Identity = Id(99) }, info => info with { LogicalBytes = 99 },
                info => info with { AllocatedBytes = 99 }, info => info with { LinkCount = 9 },
                info => info with { Modified = Modified.AddTicks(1) }
            }) Reject(root, NtfsPersonalAcceptanceCode.NativeMismatch, (path, directory, token) => directory ? read(path, directory, token) : change(read(path, directory, token)));
        }
        {
            var root = Fixture(); Reject(root, NtfsPersonalAcceptanceCode.NativeFailure, (_, _, _) => throw new IOException("Private path and identity"));
            Reject(root, NtfsPersonalAcceptanceCode.NativeFailure, (_, _, _) => throw new NtfsPersonalAcceptanceException(NtfsPersonalAcceptanceCode.Totals));
            Reject(root, NtfsPersonalAcceptanceCode.NativeFailure, (_, _, _) => throw new OperationCanceledException());
        }
        {
            var root = Fixture(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); int attempts = 0;
            bool didCancel = false;
            try { _ = NtfsPersonalAcceptance.Verify(root, (_, _, _) => { attempts++; throw new IOException(); }, cancelled.Token); }
            catch (OperationCanceledException) { didCancel = true; }
            Check(didCancel && attempts == 0, "pre cancellation performs no native query");
        }
        {
            var root = Fixture(); using var cancelled = new CancellationTokenSource(); bool didCancel = false;
            try { _ = NtfsPersonalAcceptance.Verify(root, (_, _, _) => { cancelled.Cancel(); throw new IOException("Private failure"); }, cancelled.Token); }
            catch (OperationCanceledException) { didCancel = true; }
            Check(didCancel, "real cancellation takes priority over callback failure");
        }
        {
            var root = Fixture(); var read = Reader(root);
            Reject(root, NtfsPersonalAcceptanceCode.SourceMutated, (path, directory, token) => { root.Size = 500; return read(path, directory, token); });
        }
        {
            var root = Fixture(); var file = root.Children[0]; root.MutableChildren.Clear();
            root.MutableChildren.AddRange(Enumerable.Repeat(file, NtfsPersonalAcceptance.MaximumEntries));
            Reject(root, NtfsPersonalAcceptanceCode.Budget);
        }
        {
            var root = new DiskEntry(@"X:\Deep", true) { Storage = Info(5, -1, 0), Modified = Modified };
            var parent = root;
            for (int i = 0; i <= NtfsPersonalAcceptance.MaximumDepth; i++)
            {
                var child = new DiskEntry(Path.Combine(parent.Path, "d"), true, parent) { Storage = Info((ulong)i + 10, -1, 0), Modified = Modified };
                parent.MutableChildren.Add(child); parent = child;
            }
            Reject(root, NtfsPersonalAcceptanceCode.Budget);
        }
        {
            var root = new DiskEntry(@"X:\Overflow", true) { Storage = Info(5, -1, 0), Modified = Modified, Size = long.MaxValue, FileCount = 2 };
            root.MutableChildren.AddRange([File(root, "a.bin", 10, long.MaxValue, 0), File(root, "b.bin", 11, 1, 0)]);
            Reject(root, NtfsPersonalAcceptanceCode.ArithmeticOverflow, Reader(root));
        }
        {
            var root = CompactFixture(); var largeCalls = new List<(string, bool)>();
            var largeResult = NtfsPersonalAcceptance.Verify(root,
                Reader(root, (path, directory) => largeCalls.Add((path, directory))), limits: NtfsScanLimits.Large);
            Check(largeResult is (true, true) && largeCalls.Count == 4 &&
                largeCalls.Select(item => item.Item1).Order(StringComparer.Ordinal).SequenceEqual(new[] { "", "a.bin", "cache", "cache/b.bin" }),
                "large compact tree audits every unique object with complete relative paths");
            Check(root.Descendants().Where(entry => !entry.IsDirectory).All(entry => entry.StoredPathCharacters == 0),
                "large compact file nodes retain names and shared directory prefixes");
            Check(root.Size == 50 && root.AllocatedSize == 48 && root.Children[1].AllocatedSize == 16,
                "large full audit preserves alias logical bytes and canonical allocation");
        }
        {
            var root = Fixture();
            Check(NtfsPersonalAcceptance.Verify(root, Reader(root), limits: NtfsScanLimits.Large) is (true, true),
                "large audit also validates explicit stored paths without retaining another copy");
            root.MutableChildren.Add(new DiskEntry(@"X:\Other\outside.bin", false, root)
                { Storage = Info(30, 1, 1), Modified = Modified, Size = 1, FileCount = 1 });
            Reject(root, NtfsPersonalAcceptanceCode.TreePath, limits: NtfsScanLimits.Large);
        }
        {
            Console.WriteLine("CHECK personal acceptance large child-order mutation");
            var root = CompactFixture(); var read = Reader(root); bool changed = false;
            Reject(root, NtfsPersonalAcceptanceCode.SourceMutated,
                (path, directory, token) => { if (!changed) { root.MutableChildren.Reverse(); changed = true; } return read(path, directory, token); }, NtfsScanLimits.Large);
        }
        {
            Console.WriteLine("CHECK personal acceptance large metadata mutation");
            var root = CompactFixture(); var read = Reader(root); var file = root.Children[1].Children[1];
            Reject(root, NtfsPersonalAcceptanceCode.SourceMutated,
                (path, directory, token) => { file.Storage = file.Storage! with { LinkCount = 9 }; return read(path, directory, token); }, NtfsScanLimits.Large);
            Reject(CompactFixture(), NtfsPersonalAcceptanceCode.NativeFailure,
                (_, _, _) => throw new NtfsPersonalAcceptanceException(NtfsPersonalAcceptanceCode.SourceMutated), NtfsScanLimits.Large);
        }
        {
            var root = CompactFixture(); var file = root.Children[0]; root.MutableChildren.Clear();
            root.MutableChildren.AddRange(Enumerable.Repeat(file, NtfsPersonalAcceptance.MaximumEntries));
            // The default profile stops at the old bound. Large reaches strict
            // shape validation instead; it never silently accepts duplicate nodes.
            Reject(root, NtfsPersonalAcceptanceCode.Budget);
            Reject(root, NtfsPersonalAcceptanceCode.TreeShape, limits: NtfsScanLimits.Large);
            root.MutableChildren.Clear(); root.MutableChildren.AddRange(Enumerable.Repeat(file, NtfsScanLimits.Large.MaximumEntries));
            Reject(root, NtfsPersonalAcceptanceCode.Budget, limits: NtfsScanLimits.Large);
        }
        {
            long atLimit = NtfsPersonalAcceptance.AddCompactPathCharacters(
                NtfsScanLimits.Large.MaximumCharacters - 32770L, 1, 1, 32768, NtfsScanLimits.Large);
            Check(atLimit == NtfsScanLimits.Large.MaximumCharacters,
                "large character bound includes stored absolute directory prefixes exactly");
            bool rejected = false;
            try { _ = NtfsPersonalAcceptance.AddCompactPathCharacters(atLimit, 1, 0, 0, NtfsScanLimits.Large); }
            catch (NtfsPersonalAcceptanceException error) { rejected = error.Code == NtfsPersonalAcceptanceCode.Budget; }
            Check(rejected, "large retained path characters reject the first excess character");
            Check(NtfsPersonalAcceptance.AddCompactPathCharacters(0, 5, 0, 0, NtfsScanLimits.Large) == 5 &&
                NtfsPersonalAcceptance.AddCompactPathCharacters(0, 5, 4, 30, NtfsScanLimits.Large) == 39,
                "compact file names and all directory string storage are accounted separately");
        }
        {
            var root = CompactFixture(); using var cancelled = new CancellationTokenSource(); bool didCancel = false;
            try { _ = NtfsPersonalAcceptance.Verify(root,
                (_, _, _) => { cancelled.Cancel(); throw new IOException("PRIVATE"); }, cancelled.Token, NtfsScanLimits.Large); }
            catch (OperationCanceledException) { didCancel = true; }
            Check(didCancel, "large real cancellation still takes priority over callback failure");
        }
        Console.WriteLine($"Personal acceptance audit: {passed} checks; pure synthetic callbacks only."); return passed;
    }
}
