namespace DiskAtlas;

// Pure commands, in-memory trees and hashes. No pipe, worker, source or disk I/O.
internal static class NtfsPersonalTimingDriverTests
{
    private const string Root = @"B:\Synthetic-Timing";
    private static readonly DateTime Modified = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static FileIdentity Id(ulong slot) => new(41, 3UL << 48 | slot, 0);
    private static ScanResult Tree(string directoryName = "folder")
    {
        var root = new DiskEntry(Root, true) { Storage = new(Id(5), 0, 1, false, -1, Modified), Modified = Modified, Size = 20, FileCount = 2, FolderCount = 1 };
        var folder = DiskEntry.FromName(directoryName, true, root, "文件夹");
        folder.Storage = new(Id(6), 0, 1, false, -1, Modified); folder.Modified = Modified; folder.Size = 10; folder.FileCount = 1;
        root.MutableChildren.Add(folder);
        foreach (var pair in new[] { (Parent: root, Name: "a.bin"), (Parent: folder, Name: "alias.bin") })
        {
            var file = DiskEntry.FromName(pair.Name, false, pair.Parent, ".bin");
            file.Storage = new(Id(7), 4096, 2, false, 10, Modified); file.Modified = Modified; file.Size = 10; file.FileCount = 1;
            pair.Parent.MutableChildren.Add(file);
        }
        StorageMetrics.Calculate(root, default);
        return new(root, TimeSpan.Zero, 0, []);
    }
    private static ScanResult Link(ScanResult result, bool snapshot = false, string leaf = "link")
    {
        result.Root.Incomplete = true;
        return result with { Skipped = 1, Issues = [new(Path.Combine(Root, leaf), snapshot ? NtfsPrototype.NtfsReparsePolicy.Reason : "跳过符号链接、联接点或其他重解析点")],
            Source = snapshot ? new(DateTimeOffset.FromUnixTimeSeconds(1700000000), 1) : null };
    }
    internal static int Run()
    {
        int passed = 0;
        void Check(bool value, string code)
        {
            if (!value) { Console.WriteLine("FAIL personal timing " + code); throw new InvalidOperationException("TimingCheck"); }
            passed++; Console.WriteLine("PASS personal timing " + code);
        }
        void InvalidInput(string input)
        {
            bool rejected = false;
            try { _ = NtfsPersonalTimingDriver.ReadInput(new StringReader(input)); }
            catch (InvalidDataException error) { rejected = error.Message == "TimingInput" && error.InnerException == null; }
            Check(rejected, "invalid bounded stdin is sanitized");
        }
        Check(FastScanBridge.IsTimingHost("DiskAtlas.Tests.exe", "DiskAtlas.Tests") &&
            FastScanBridge.IsTimingHost(@"B:\synthetic\DiskAtlas.Tests.exe", "DiskAtlas.Tests"), "timing entry requires the tests apphost and matching assembly");
        foreach (var pair in new[] { ("DiskAtlas.exe", "DiskAtlas"), ("AsukaLens.exe", "DiskAtlas.Tests"), ("dotnet.exe", "DiskAtlas.Tests"),
            ("DiskAtlas.Tests.exe", "Other"), ("DiskAtlas.Tests.exe.bak", "DiskAtlas.Tests") })
            Check(!FastScanBridge.IsTimingHost(pair.Item1, pair.Item2), "production and mismatched hosts cannot select the timing worker");
        Check(FastScanBridge.IsWorkerRequest([FastScanBridge.WorkerArgument]) && !FastScanBridge.IsWorkerRequest([FastScanBridge.TimingWorkerArgument]) &&
            FastScanBridge.IsTimingWorkerRequest([FastScanBridge.TimingWorkerArgument]) && !FastScanBridge.IsTimingWorkerRequest([FastScanBridge.WorkerArgument]),
            "acceptance and timing worker verbs remain distinct");
        Check(FastScanBridge.RunTimingWorker([]) == 2 && FastScanBridge.RunTimingWorker([FastScanBridge.TimingWorkerArgument]) == 2 &&
            FastScanBridge.RunTimingWorker([FastScanBridge.WorkerArgument, "bad", "1", "1"]) == 2, "malformed timing worker invocations return before native work");
        Check(NtfsPersonalTimingDriver.ValidInvocation([NtfsPersonalTimingDriver.Argument]) &&
            !NtfsPersonalTimingDriver.ValidInvocation([NtfsPersonalTimingDriver.Argument, Root]) &&
            !NtfsPersonalTimingDriver.ValidInvocation([]), "scope is never accepted as an argv timing argument");
        foreach (string order in new[] { "FO", "OF", "FOOF", "" })
        {
            var request = NtfsPersonalTimingDriver.ReadInput(new StringReader(order + "\r\n" + Root + "\r\n"));
            Check(request.Order == (order.Length == 0 ? "FO" : order) && request.Scope == Root, "fixed serial order and normalized private stdin scope");
        }
        foreach (string invalid in new[] { "F\n" + Root, "O\n" + Root, "OOFF\n" + Root, "FOOFO\n" + Root, "FO\n", "FO\nB:relative", "FO\n" + new string('x', 32768), "FO\n\\\\server\\share" })
            InvalidInput(invalid);
        {
            int calls = 0; var returned = Tree() with { Source = new(new DateTimeOffset(Modified)) };
            var measured = NtfsPersonalTimingDriver.MeasureFast(Root, default, (path, progress, token) =>
            { calls++; Check(path == Root && progress == null && !token.IsCancellationRequested, "cost capture invokes only the selected fast operation"); return returned; });
            Check(calls == 1 && ReferenceEquals(measured.Result.Root, returned.Root) && measured.Result.Source == returned.Source &&
                measured.Result.FastScanFallback == null && measured.Result.Checkpoint == null && measured.Bridge >= TimeSpan.Zero && measured.PublicationValidation >= TimeSpan.Zero,
                "coordinated fast measurement retains source and captures bridge and publication costs without native work");
            var invalid = Tree() with { Source = new(new DateTimeOffset(Modified)) }; invalid.Root.Incomplete = true; bool rejected = false;
            try { _ = NtfsPersonalTimingDriver.MeasureFast(Root, default, (_, _, _) => invalid); }
            catch (FastScanUnavailableException error) { rejected = error.Code == FastScanWireError.ProtocolError && !error.CleanupConfirmed; }
            Check(rejected, "cost capture really runs coordinator publication validation");
        }
        foreach (var code in new[] { FastScanWireError.Unsupported, FastScanWireError.PermissionDenied, FastScanWireError.LimitExceeded })
        {
            int calls = 0; bool failed = false; var context = new FastScanFailureContext(FastScanWireStage.Reading, FastScanFailureDetail.MftPageLimit);
            try
            {
                _ = NtfsPersonalTimingDriver.MeasureFast(Root, default, (_, _, _) =>
                { calls++; throw new FastScanUnavailableException(code, true, context); });
            }
            catch (FastScanUnavailableException error) { failed = error.Code == code && error.CleanupConfirmed && error.Context == context; }
            Check(failed && calls == 1, "closed fast failure is returned with its fixed category instead of performing ordinary fallback");
        }
        {
            int calls = 0; bool failed = false;
            try { _ = NtfsPersonalTimingDriver.MeasureFast(Root, default, (_, _, _) => { calls++; throw new FastScanUnavailableException(FastScanWireError.CleanupUnconfirmed, false); }); }
            catch (FastScanUnavailableException error) { failed = !error.CleanupConfirmed && error.Code == FastScanWireError.CleanupUnconfirmed; }
            Check(failed && calls == 1, "unknown source cleanup never becomes a measured sample or fallback");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool stopped = false;
            try { _ = NtfsPersonalTimingDriver.MeasureFast(Root, cancellation.Token, (_, _, _) => { calls++; return Tree(); }); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && calls == 1, "pre-cancel avoids even the injected fast operation");
        }
        var original = NtfsTimingFingerprint.Capture(Tree());
        Check(original.Compare(NtfsTimingFingerprint.Capture(Tree())).AllEqual, "identical included scope and metadata match privately");
        {
            var value = Tree();
            foreach (var directory in value.Root.Descendants().Prepend(value.Root).Where(entry => entry.IsDirectory))
                directory.Storage = directory.Storage! with { LogicalBytes = 123, AllocatedBytes = 4096, LinkCount = 9 };
            Check(original.Compare(NtfsTimingFingerprint.Capture(value)).AllEqual,
                "directory storage representation does not alter file data or namespace equality");
        }
        {
            var value = Tree(); value.Root.Children[0].Storage = value.Root.Children[0].Storage! with { Identity = Id(19) };
            Check(!original.Compare(NtfsTimingFingerprint.Capture(value)).Included,
                "directory identity changes remain observable after storage normalization");
        }
        {
            var value = Tree(); value.Root.Children[0].Storage = null;
            var comparison = original.Compare(NtfsTimingFingerprint.Capture(value));
            Check(!comparison.AllocationKnown && !comparison.AllEqual,
                "unknown ordinary directory identity cannot be normalized into a matching scope");
        }
        {
            var value = Tree(); value.Root.MutableChildren.Reverse();
            Check(original.Compare(NtfsTimingFingerprint.Capture(value)).AllEqual, "fingerprint ignores enumeration order");
        }
        {
            var value = Tree(); var files = value.Root.Descendants().Where(entry => !entry.IsDirectory).ToArray();
            foreach (var file in files) { file.AllocatedSize = file.AllocatedSize == 0 ? 4096 : 0; file.SharedAllocation = !file.SharedAllocation; }
            Check(original.Compare(NtfsTimingFingerprint.Capture(value)).AllEqual, "hardlink per-entry allocation owner does not alter raw object allocation equality");
        }
        {
            var renamed = original.Compare(NtfsTimingFingerprint.Capture(Tree("renamed")));
            Check(!renamed.Included && renamed.Added && renamed.Removed && !renamed.AllEqual,
                "relative parent changes expose added and removed hash keys without storing path text");
        }
        int mutation = 0;
        foreach (var mutate in new Action<DiskEntry>[]
        {
            file => file.Storage = file.Storage! with { Identity = Id(9) },
            file => file.Storage = file.Storage! with { AllocatedBytes = 8192 },
            file => file.Storage = file.Storage! with { LogicalBytes = 11 },
            file => file.Storage = file.Storage! with { LinkCount = 3 },
            file => file.Size++
        })
        {
            var value = Tree(); mutate(value.Root.Children[1]);
            var comparison = original.Compare(NtfsTimingFingerprint.Capture(value));
            Check(!comparison.Included && !comparison.Added && !comparison.Removed && !comparison.AllEqual &&
                (mutation == 0 ? comparison.IdentityChanged : mutation == 1 ? comparison.FileAllocationChanged :
                    mutation == 3 ? comparison.FileLinksChanged : comparison.FileLogicalChanged),
                "file metadata differences keep their fixed identity size allocation or link category");
            mutation++;
        }
        {
            var added = Tree(); var file = DiskEntry.FromName("new.bin", false, added.Root, ".bin");
            file.Storage = new(Id(10), 0, 1, false, 0, Modified); file.Modified = Modified; file.FileCount = 1;
            added.Root.MutableChildren.Add(file); added.Root.FileCount++;
            var comparison = original.Compare(NtfsTimingFingerprint.Capture(added));
            Check(comparison.Added && !comparison.Removed && !comparison.AllEqual, "added included path is a category rather than a reported filename or count");
            var removed = Tree(); removed.Root.MutableChildren.RemoveAt(1); removed.Root.Size -= 10; removed.Root.FileCount--; StorageMetrics.Calculate(removed.Root, default);
            comparison = original.Compare(NtfsTimingFingerprint.Capture(removed));
            Check(!comparison.Added && comparison.Removed && !comparison.AllEqual, "removed included path is separately observable");
            var changed = Tree(); var directory = DiskEntry.FromName("a.bin", true, changed.Root, "文件夹");
            directory.Storage = new(Id(11), 0, 1, false, -1, Modified); directory.Modified = Modified; changed.Root.MutableChildren[1] = directory;
            comparison = original.Compare(NtfsTimingFingerprint.Capture(changed));
            Check(comparison.TypeChanged && !comparison.AllEqual, "same relative key changing entry type cannot be hidden by root totals");
        }
        {
            var snapshot = Tree() with { Source = new(new DateTimeOffset(Modified.AddSeconds(5))) };
            var baseline = NtfsTimingFingerprint.Capture(snapshot);
            var late = Tree(); var file = late.Root.Children[1]; file.Size++;
            file.Storage = file.Storage! with { LogicalBytes = file.Size, Modified = Modified.AddSeconds(10) }; file.Modified = Modified.AddSeconds(10);
            var comparison = baseline.Compare(NtfsTimingFingerprint.Capture(late));
            Check(comparison.FileLogicalChanged && comparison.SnapshotTimeAvailable && comparison.ChangedMtimeAfterSnapshot && !comparison.AllEqual,
                "changed file with mtime after the snapshot is only an observed timestamp relation");
            file.Storage = file.Storage! with { Modified = Modified }; file.Modified = Modified;
            comparison = baseline.Compare(NtfsTimingFingerprint.Capture(late));
            Check(comparison.FileLogicalChanged && comparison.SnapshotTimeAvailable && !comparison.ChangedMtimeAfterSnapshot && !comparison.AllEqual,
                "preserved old timestamp cannot excuse an unresolved logical size difference");
            comparison = original.Compare(NtfsTimingFingerprint.Capture(late));
            Check(!comparison.SnapshotTimeAvailable && !comparison.ChangedMtimeAfterSnapshot && !comparison.AllEqual,
                "ordinary-only comparison does not invent a snapshot time");
            var timeOnly = Tree(); file = timeOnly.Root.Children[1]; file.Storage = file.Storage! with { Modified = Modified.AddSeconds(10) }; file.Modified = Modified.AddSeconds(10);
            comparison = baseline.Compare(NtfsTimingFingerprint.Capture(timeOnly));
            Check(comparison.AllEqual && !comparison.ChangedMtimeAfterSnapshot,
                "timestamp alone is excluded from disk-size equality and is not classified as a changed-statistics item");
        }
        {
            var value = Tree(); var directory = DiskEntry.FromName("new", true, value.Root, "文件夹");
            directory.Storage = new(Id(9), 0, 1, false, -1, Modified); value.Root.MutableChildren.Add(directory);
            Check(!original.Compare(NtfsTimingFingerprint.Capture(value)).Included, "included namespace entry changes are detected");
        }
        foreach (var change in new Action<DiskEntry>[] { root => root.Size++, root => root.AllocatedSize++ })
        {
            var value = Tree(); change(value.Root); var comparison = original.Compare(NtfsTimingFingerprint.Capture(value));
            Check(comparison.Included && !comparison.RootMetrics && !comparison.AllEqual, "root size metrics are compared independently");
        }
        {
            var ordinary = NtfsTimingFingerprint.Capture(Link(Tree())); var fast = NtfsTimingFingerprint.Capture(Link(Tree(), true));
            Check(ordinary.Compare(fast).AllEqual, "ordinary and snapshot fixed reparse reasons describe the same excluded scope");
            Check(!ordinary.Compare(NtfsTimingFingerprint.Capture(Link(Tree(), true, "another-link"))).Exclusions, "excluded boundary changes are detected");
            var other = Link(Tree()) with { Issues = [new(Path.Combine(Root, "link"), "read failed")] };
            Check(!ordinary.Compare(NtfsTimingFingerprint.Capture(other)).NoOtherIssues, "unrelated read failures never become a matching excluded scope");
            var truncated = Link(Tree()) with { Skipped = 2 };
            var comparison = NtfsTimingFingerprint.Capture(truncated).Compare(NtfsTimingFingerprint.Capture(truncated));
            Check(!comparison.ExclusionsComparable && !comparison.Exclusions && !comparison.AllEqual, "truncated exclusion details do not prove complete exclusion equality");
        }
        {
            var value = Tree(); value.Root.Children[1].Storage = null; value.Root.UnknownAllocationFiles = 1;
            Check(!original.Compare(NtfsTimingFingerprint.Capture(value)).AllocationKnown, "unknown allocation prevents a performance equality conclusion");
        }
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool cancelled = false;
            try { _ = NtfsTimingFingerprint.Capture(Tree(), cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "fingerprint supports real cancellation before its bounded walk");
            cancelled = false;
            try { _ = original.Compare(original, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "compact difference walk also honors cancellation without exposing partial diagnostics");
        }
        Console.WriteLine("Personal timing synthetic checks complete."); return passed;
    }
}
