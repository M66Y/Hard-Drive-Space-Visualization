using DiskAtlas.NtfsPrototype;

namespace DiskAtlas;

// In-memory trees and fake Native observations only. Never opens a source.
internal static class NtfsSameSnapshotComparisonTests
{
    private const string RootPath = @"B:\Synthetic-SameSnapshot";
    private static readonly DateTime Modified = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static FileIdentity Id(ulong slot) => new(41, 3UL << 48 | slot, 0);
    private static ScanResult Tree(string rootPath = RootPath)
    {
        var root = new DiskEntry(rootPath, true) { Storage = new(Id(5), 0, 1, false, -1, Modified), Size = 20, FileCount = 2, FolderCount = 1 };
        var folder = DiskEntry.FromName("folder", true, root, "文件夹"); folder.Storage = new(Id(6), 0, 1, false, -1, Modified);
        folder.Size = 10; folder.FileCount = 1; root.MutableChildren.Add(folder);
        foreach (var item in new[] { (root, "a.bin"), (folder, "alias.bin") })
        {
            var file = DiskEntry.FromName(item.Item2, false, item.Item1, ".bin");
            file.Storage = new(Id(7), 4096, 2, false, 10, Modified); file.Size = 10; file.FileCount = 1; item.Item1.MutableChildren.Add(file);
        }
        StorageMetrics.Calculate(root, default); return new(root, TimeSpan.Zero, 0, []);
    }
    private static Func<string, bool, CancellationToken, NtfsSameSnapshotNativeObservation> Reader(ScanResult directory, Action? called = null,
        bool compressed = false, bool sparse = false, long? standard = null, bool consistent = true)
    {
        var map = directory.Root.Descendants().Prepend(directory.Root).ToDictionary(entry => ReferenceEquals(entry, directory.Root) ? "" :
            Path.GetRelativePath(directory.Root.Path, entry.Path).Replace('\\', '/'));
        return (relative, kind, token) =>
        {
            token.ThrowIfCancellationRequested(); called?.Invoke(); var node = map[relative]; var value = node.Storage!;
            return new(value.Identity, node.IsDirectory, node.IsDirectory ? -1 : value.LogicalBytes, value.AllocatedBytes,
                standard ?? value.AllocatedBytes, value.LinkCount, !node.IsDirectory && compressed, !node.IsDirectory && sparse, consistent && node.IsDirectory == kind);
        };
    }
    private static void MutateFiles(ScanResult result, Func<FileStorageInfo, FileStorageInfo> change)
    {
        foreach (var node in result.Root.Descendants().Where(entry => !entry.IsDirectory)) { node.Storage = change(node.Storage!); node.Size = node.Storage.LogicalBytes; }
        result.Root.Size = result.Root.Descendants().Where(entry => !entry.IsDirectory).Sum(entry => entry.Size);
        result.Root.Children[0].Size = result.Root.Children[0].Children.Sum(entry => entry.Size);
        StorageMetrics.Calculate(result.Root, default);
    }
    internal static int Run()
    {
        int passed = 0;
        void Check(bool value, string code)
        { if (!value) { Console.WriteLine("FAIL same snapshot " + code); throw new InvalidOperationException("SameSnapshotCheck"); }
          passed++; Console.WriteLine("PASS same snapshot " + code); }
        NtfsSameSnapshotReport Compare(ScanResult a, ScanResult b,
            Func<string, bool, CancellationToken, NtfsSameSnapshotNativeObservation>? native = null, long count = 0, IReadOnlyList<NtfsReparseBoundary>? details = null) =>
            NtfsSameSnapshotComparison.Compare(a.Root, count, details ?? [], b, native);
        {
            var a = Tree(); var b = Tree(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy9\scope");
            var result = Compare(a, b);
            Check(result.Outcome == NtfsSameSnapshotOutcome.Matched && result.Code == NtfsSameSnapshotCode.None && result.ComparisonComplete &&
                result.IncludedScopeEqual == true && result.AllocationKnown == true && result.FastRootLogicalValid == true && result.FastRootAllocationValid == true &&
                result.DirectoryRootLogicalValid == true && result.DirectoryRootAllocationValid == true && result.FastRootCountsValid == true && result.DirectoryRootCountsValid == true,
                "same relative namespace and independent totals match across different private root prefixes");
            Check(!result.CleanupConfirmed && !result.SameSourceVerified && !result.NativeProbeRequired && !result.NativeProbeAttempted &&
                !result.NativeProbeComplete && result.ProbeBranch == NtfsSameSnapshotProbeBranch.None && result.FastMatchesNativeLogical == null,
                "pure model does not manufacture native source cleanup or a probe");
            a.Root.MutableChildren.Reverse();
            foreach (var file in a.Root.Descendants().Where(entry => !entry.IsDirectory)) { file.AllocatedSize = file.AllocatedSize == 0 ? 4096 : 0; file.SharedAllocation = !file.SharedAllocation; }
            Check(Compare(a, b).Outcome == NtfsSameSnapshotOutcome.Matched, "order and per-alias allocation owner do not change object accounting");
        }
        {
            var a = Tree(); var b = Tree(); int calls = 0; MutateFiles(a, value => value with { AllocatedBytes = 8192 });
            var result = Compare(a, b, Reader(b, () => calls++));
            Check(result.Outcome == NtfsSameSnapshotOutcome.Different && result.ComparisonComplete && result.FileAllocationEqual == false &&
                result.NativeProbeRequired && result.NativeProbeAttempted && result.NativeProbeComplete && result.FastMatchesNativeAllocation == false &&
                result.DirectoryMatchesNativeAllocation == true && result.NativeMetadataConsistent == true && result.BudgetRespected,
                "raw allocation difference is independently compared with the bound native observation");
            Check(calls == 1, "hardlink aliases consume one native object query");
        }
        foreach (var mutation in new Func<FileStorageInfo, FileStorageInfo>[]
        { value => value with { LogicalBytes = 12 }, value => value with { LinkCount = 3 } })
        {
            var a = Tree(); var b = Tree(); MutateFiles(a, mutation); var result = Compare(a, b, Reader(b));
            Check(result.Outcome == NtfsSameSnapshotOutcome.Different && result.NativeProbeComplete &&
                (result.FileLogicalEqual == false && result.FastMatchesNativeLogical == false && result.DirectoryMatchesNativeLogical == true ||
                 result.FileLinksEqual == false && result.FastMatchesNativeLinks == false && result.DirectoryMatchesNativeLinks == true),
                "file logical and link differences retain their independent category");
        }
        {
            var a = Tree(); var b = Tree(); var file = a.Root.Children[1]; file.Storage = file.Storage! with { Identity = Id(8) };
            StorageMetrics.Calculate(a.Root, default); var result = Compare(a, b, Reader(b));
            Check(result.IdentitiesEqual == false && result.FastMatchesNativeIdentity == false && result.DirectoryMatchesNativeIdentity == true &&
                result.Outcome == NtfsSameSnapshotOutcome.Different, "full file identity differences survive independent root accounting");
        }
        foreach (var mutation in new Action<DiskEntry>[] { root => root.Size++, root => root.AllocatedSize++, root => root.FileCount++, root => root.FolderCount++ })
        {
            var a = Tree(); var b = Tree(); mutation(a.Root); var result = Compare(a, b, Reader(b));
            Check(result.Outcome == NtfsSameSnapshotOutcome.Different && result.NativeProbeComplete && result.ProbeBranch == NtfsSameSnapshotProbeBranch.Directory &&
                (result.FastRootLogicalValid == false || result.FastRootAllocationValid == false || result.FastRootCountsValid == false) &&
                result.DirectoryRootLogicalValid == true && result.DirectoryRootAllocationValid == true && result.DirectoryRootCountsValid == true,
                "root totals and counts are recomputed rather than trusting matching entries");
        }
        {
            var a = Tree(); var b = Tree(); a.Root.Children[1].Storage = null;
            var result = Compare(a, b, Reader(b));
            Check(result.Outcome == NtfsSameSnapshotOutcome.Incomplete && result.AllocationKnown == false && !result.ComparisonComplete,
                "unknown object metadata cannot be repaired into a matching report by diagnostic lookup");
            var changed = DiskEntry.FromName("a.bin", true, a.Root, "文件夹"); changed.Storage = new(Id(9), 0, 1, false, -1, Modified);
            a.Root.MutableChildren[1] = changed; a.Root.Size = 10; a.Root.FileCount = 1; a.Root.FolderCount = 2; StorageMetrics.Calculate(a.Root, default);
            result = Compare(a, b, Reader(b));
            Check(result.TypesEqual == false && result.FileLogicalEqual == false && result.Outcome == NtfsSameSnapshotOutcome.Different,
                "entry type changes do not masquerade as equal file metadata");
        }
        {
            var a = Tree(); var b = Tree(); MutateFiles(a, value => value with { AllocatedBytes = 8192 });
            var result = Compare(a, b, Reader(b, compressed: true, standard: 8192));
            Check(result.ProbeBranch == NtfsSameSnapshotProbeBranch.Compressed && result.NativeCompressionObserved == true &&
                result.FastMatchesNativeStandardAllocation == true && result.DirectoryMatchesNativeStandardAllocation == false &&
                result.FastMatchesNativeAllocation == false && result.DirectoryMatchesNativeAllocation == true,
                "compression final allocation and raw standard allocation are separate observations");
            result = Compare(a, b, Reader(b, sparse: true));
            Check(result.ProbeBranch == NtfsSameSnapshotProbeBranch.Sparse, "sparse branch is a fixed category without inferring residency");
            result = Compare(a, b, Reader(b, consistent: false));
            Check(result.Outcome == NtfsSameSnapshotOutcome.Incomplete && !result.NativeProbeComplete && result.NativeMetadataConsistent == false,
                "inconsistent native query observations remain incomplete");
            result = Compare(a, b, (_, _, _) => throw new IOException("PRIVATE_PATH_AND_ID"));
            Check(result.Outcome == NtfsSameSnapshotOutcome.Incomplete && result.Code == NtfsSameSnapshotCode.NativeProbeIncomplete && !result.BudgetRespected &&
                result.FastMatchesNativeAllocation == null && !result.NativeProbeComplete, "foreign probe failures lose all private exception text and do not prove budgets");
        }
        {
            var a = Tree(); var b = Tree(); b.Root.Incomplete = true;
            b = b with { Skipped = 1, Issues = [new(Path.Combine(b.Root.Path, "link"), "跳过符号链接、联接点或其他重解析点")] };
            var details = new[] { new NtfsReparseBoundary(Id(5), "link", true) };
            var result = Compare(a, b, count: 1, details: details);
            Check(result.Outcome == NtfsSameSnapshotOutcome.Matched && result.ExclusionsEqual == true && result.ExclusionDetailsComplete == true,
                "explicit fast exclusions match the ordinary excluded path set");
            result = Compare(a, b);
            Check(result.ExclusionsEqual == false && result.Outcome == NtfsSameSnapshotOutcome.Incomplete,
                "ordinary skipped entries never substitute for absent fast exclusion evidence");
            result = Compare(a, b, count: 1, details: [new(Id(99), "link", true)]);
            Check(result.Outcome == NtfsSameSnapshotOutcome.Incomplete && result.ExclusionDetailsComplete == false,
                "fast exclusion parent must belong to the included directory identity set");
            b = b with { Issues = [new(Path.Combine(b.Root.Path, "link"), "read failure")] };
            result = Compare(a, b, count: 1, details: details);
            Check(result.Outcome == NtfsSameSnapshotOutcome.Incomplete && result.Code == NtfsSameSnapshotCode.ComparisonIncomplete,
                "unrelated directory read failures do not become reparse exclusions");
        }
        {
            ScanResult Many(long allocation)
            {
                var root = new DiskEntry(RootPath, true) { Storage = new(Id(5), 0, 1, false, -1, Modified), FileCount = 1025, Size = 1025 };
                for (int i = 0; i < 1025; i++)
                { var node = DiskEntry.FromName("f" + i, false, root, "无扩展名"); node.Storage = new(Id((ulong)i + 10), allocation, 1, false, 1, Modified);
                  node.Size = 1; node.FileCount = 1; root.MutableChildren.Add(node); }
                StorageMetrics.Calculate(root, default); return new(root, TimeSpan.Zero, 0, []);
            }
            var a = Many(2); var b = Many(1); int calls = 0; var result = Compare(a, b, Reader(b, () => calls++));
            Check(result.Outcome == NtfsSameSnapshotOutcome.Incomplete && result.Code == NtfsSameSnapshotCode.BudgetExceeded && !result.BudgetRespected &&
                result.NativeProbeAttempted && !result.NativeProbeComplete && calls == 1024 && result.FastMatchesNativeAllocation == null,
                "native object cap stops at its exact bound without reporting a partial probe as complete");
        }
        {
            var a = Tree(); var b = Tree(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var result = NtfsSameSnapshotComparison.Compare(a.Root, 0, [], b, token: cancellation.Token);
            Check(result.Outcome == NtfsSameSnapshotOutcome.Cancelled && !result.SameSourceVerified && !result.CleanupConfirmed && result.FileLogicalEqual == null,
                "synthetic cancellation cannot manufacture real cleanup evidence");
            using var during = new CancellationTokenSource(); MutateFiles(a, value => value with { AllocatedBytes = 8192 });
            result = NtfsSameSnapshotComparison.Compare(a.Root, 0, [], b, (_, _, _) => { during.Cancel(); throw new IOException("PRIVATE"); }, during.Token);
            Check(result.Outcome == NtfsSameSnapshotOutcome.Cancelled && result.Code == NtfsSameSnapshotCode.Cancelled,
                "real cancellation wins over an arbitrary probe failure");
        }
        Check(typeof(NtfsSameSnapshotReport).GetProperties().All(property => property.PropertyType == typeof(bool) || property.PropertyType == typeof(bool?) || property.PropertyType.IsEnum),
            "report schema has no carrier for private names identities hashes counts sizes or timings");
        return passed;
    }
}
