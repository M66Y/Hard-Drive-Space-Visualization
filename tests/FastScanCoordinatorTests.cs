namespace DiskAtlas;

// Coordinator decisions are exercised with pure delegates and synthetic trees.
// No source, native handle, snapshot, pipe or elevation is created.
internal static class FastScanCoordinatorTests
{
    private const string PathValue = @"B:\AsukaLens-Synthetic";
    private static ScanResult Result(bool snapshot = true)
    {
        var root = new DiskEntry(PathValue, true) { Storage = new(new(41, 3UL << 48 | 5, 0), 0, 1, false) };
        return new(root, TimeSpan.FromMilliseconds(2), 0, [])
        { Source = snapshot ? new(DateTimeOffset.FromUnixTimeSeconds(1700000000)) : null };
    }
    private static ScanResult Partial(long count = 1)
    {
        var result = Result(); result.Root.Incomplete = true;
        return result with { Skipped = count, Source = result.Source! with { ExcludedReparseEntries = count },
            Issues = Enumerable.Range(0, (int)Math.Min(count, FastScanWire.MaximumExclusionDetails))
                .Select(index => new ScanIssue(Path.Combine(PathValue, "link-" + index), NtfsPrototype.NtfsReparsePolicy.Reason)).ToArray() };
    }
    private sealed class Progress : IProgress<ScanProgress>
    {
        internal List<ScanProgress> Values { get; } = [];
        public void Report(ScanProgress value) => Values.Add(value);
    }
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Fast scan coordinator check failed: " + name); passed++; Console.WriteLine("PASS fast scan coordinator " + name); }
        int ordinaryCalls = 0, fastCalls = 0; ScanResult? seenBaseline = null;
        Check(FastScanCoordinator.Reason(FastScanWireError.SnapshotFailed,
            new(FastScanWireStage.Snapshot, FastScanFailureDetail.None, unchecked((int)0x80010119))).Contains("0x80010119") &&
            !FastScanCoordinator.Reason(FastScanWireError.SnapshotFailed).Contains("系统代码"),
            "snapshot failure exposes only the fixed native error code when available");
        var rawBudget = new FastScanFailureContext(FastScanWireStage.Reading, FastScanFailureDetail.RawRequestLimit);
        Check(NtfsPersonalScan.ClassifyFailure(new NtfsPrototype.NtfsSnapshotSizeException("SnapshotSizeRead"), rawBudget) == FastScanWireError.LimitExceeded &&
            NtfsPersonalScan.ClassifyFailure(new NtfsPrototype.NtfsMftBitmapSelectionException("BitmapSelectionRead"), rawBudget) == FastScanWireError.LimitExceeded,
            "trusted raw budget survives both callback sanitization boundaries");
        Check(NtfsPersonalScan.ClassifyDetail(new NtfsPrototype.NtfsSnapshotSizeException("SnapshotSizeBoundary")) == FastScanFailureDetail.IndexBoundary &&
            NtfsPersonalScan.ClassifyDetail(new IOException("private text")) == FastScanFailureDetail.UnexpectedPipelineFailure,
            "failure categories preserve reparse boundary and discard arbitrary exception text");
        ScanResult Ordinary(ScanResult? baseline) { ordinaryCalls++; seenBaseline = baseline; return Result(false); }
        ScanResult Call(Func<string, IProgress<ScanProgress>?, CancellationToken, ScanResult> fast,
            ScanResult? previous = null, CancellationToken token = default, Progress? progress = null) =>
            FastScanCoordinator.Scan(PathValue, previous, new(), progress, token, new(), false, fast, Ordinary);
        ScanResult Fast(string path, IProgress<ScanProgress>? progress, CancellationToken token)
        { fastCalls++; return Result(); }
        var complete = Call(Fast);
        Check(complete.Source != null && complete.Checkpoint == null && complete.FastScanFallback == null && ordinaryCalls == 0 && fastCalls == 1, "complete snapshot published once");
        Check(complete.Root.Path == PathValue && complete.Elapsed >= TimeSpan.Zero, "complete source path and elapsed");
        {
            int before = ordinaryCalls; var expected = Partial(); var partial = Call((_, _, _) => expected);
            Check(partial.Source?.ExcludedReparseEntries == 1 && partial.Root.Incomplete && partial.Skipped == 1 &&
                partial.Issues.Single().Reason == NtfsPrototype.NtfsReparsePolicy.Reason && partial.AllocationIssues.Count == 0 &&
                partial.Root.UnknownAllocationFiles == 0 && ordinaryCalls == before && ReferenceEquals(partial.Root, expected.Root),
                "declared reparse-only partial success publishes without following a link or ordinary fallback");
            var truncated = Call((_, _, _) => Partial(2001));
            Check(truncated.Source?.ExcludedReparseEntries == 2001 && truncated.Issues.Count == 2000 && truncated.Root.Incomplete,
                "bounded truncated issues retain the complete skipped count");
            var nested = Partial(); var folder = DiskEntry.FromName("folder", true, nested.Root, "文件夹");
            folder.Incomplete = true; nested.Root.MutableChildren.Add(folder);
            nested = nested with { Issues = [new(Path.Combine(folder.Path, "link"), NtfsPrototype.NtfsReparsePolicy.Reason)] };
            Check(Call((_, _, _) => nested).Root.Children[0].Incomplete,
                "included nested directory flags are explained by the declared boundary ancestors");
        }
        {
            var context = new FastScanFailureContext(FastScanWireStage.Discovering, FastScanFailureDetail.NamespaceEntryLimit, 5);
            var progress = new Progress();
            var fallback = Call((_, _, _) => throw new FastScanUnavailableException(FastScanWireError.LimitExceeded, true, context), progress: progress);
            Check(fallback.FastScanFallbackContext == context && progress.Values.Single().FastScanFallbackContext == context &&
                FastScanCoordinator.Reason(FastScanWireError.LimitExceeded, context).Contains("1,048,576"), "fixed failure context survives fallback and selects actionable explanation");
        }
        foreach (var code in Enum.GetValues<FastScanWireError>().Where(code => code != FastScanWireError.Cancelled && code != FastScanWireError.CleanupUnconfirmed))
        {
            int before = ordinaryCalls; var progress = new Progress();
            var fallback = Call((_, _, _) => throw new FastScanUnavailableException(code, true), progress: progress);
            Check(ordinaryCalls == before + 1 && fallback.Source == null && fallback.FastScanFallback == code && fallback.Checkpoint == null, "confirmed closed failure falls back " + code);
            Check(progress.Values.Count == 1 && progress.Values[0].Phase == ScanPhase.Preparing && progress.Values[0].FastScanFallback == code &&
                progress.Values[0].Message?.Contains("本次使用普通扫描", StringComparison.Ordinal) == true,
                "fallback visibly announced " + code);
        }
        foreach (var code in Enum.GetValues<FastScanWireError>())
        {
            int before = ordinaryCalls; bool rejected = false;
            try { _ = Call((_, _, _) => throw new FastScanUnavailableException(code, false)); }
            catch (FastScanUnavailableException failure) { rejected = !failure.CleanupConfirmed && failure.Code == code; }
            Check(rejected && ordinaryCalls == before, "unconfirmed source never falls back " + code);
        }
        {
            int before = ordinaryCalls; bool cancelled = false;
            try { _ = Call((_, _, _) => throw new FastScanUnavailableException(FastScanWireError.Cancelled, true)); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && ordinaryCalls == before, "worker cancellation never falls back");
        }
        {
            int before = ordinaryCalls; bool cancelled = false;
            try { _ = Call((_, _, _) => throw new OperationCanceledException()); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && ordinaryCalls == before, "direct cancellation never falls back");
        }
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); int before = fastCalls; bool cancelled = false;
            try { _ = Call(Fast, token: cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && fastCalls == before, "pre-cancel avoids fast attempt");
        }
        {
            using var cancellation = new CancellationTokenSource(); int before = ordinaryCalls; bool cancelled = false;
            try { _ = Call((_, _, _) => { cancellation.Cancel(); throw new FastScanUnavailableException(FastScanWireError.ReadFailed, true); }, token: cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && ordinaryCalls == before, "cancel after closed failure avoids fallback");
        }
        {
            using var cancellation = new CancellationTokenSource(); int before = ordinaryCalls; bool cancelled = false;
            try { _ = Call((_, _, _) => { cancellation.Cancel(); return Result(); }, token: cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && ordinaryCalls == before, "cancel before result publication keeps old result");
        }
        var malformed = new List<(string Name, Func<ScanResult> Result)>
        {
            ("source missing", () => Result(false)),
            ("live journal checkpoint", () => Result() with { Checkpoint = new(1, 2, 0, 41) }),
            ("root incomplete", () => { var result = Result(); result.Root.Incomplete = true; return result; }),
            ("unknown allocation", () => { var result = Result(); result.Root.UnknownAllocationFiles = 1; return result; }),
            ("skipped entries", () => Result() with { Skipped = 1 }),
            ("read issues", () => Result() with { Issues = [new("synthetic", "synthetic")] }),
            ("allocation issues", () => Result() with { AllocationIssues = [new("synthetic", "synthetic")] }),
            ("root path mismatch", () => new(new(@"B:\another-synthetic", true), TimeSpan.Zero, 0, []) { Source = Result().Source }),
            ("root is file", () => new(new(PathValue, false), TimeSpan.Zero, 0, []) { Source = Result().Source })
        };
        malformed.AddRange(
        [
            ("reparse source count missing", () => Partial() with { Source = Result().Source }),
            ("reparse skipped count mismatch", () => Partial() with { Skipped = 2 }),
            ("negative reparse source count", () => Result() with { Source = Result().Source! with { ExcludedReparseEntries = -1 } }),
            ("reparse details missing", () => Partial() with { Issues = [] }),
            ("reparse detail count mismatch", () => Partial(2) with { Issues = Partial().Issues }),
            ("reparse reason is fixed", () => Partial() with { Issues = [new(Path.Combine(PathValue, "link"), "read failed")] }),
            ("reparse issue outside scope", () => Partial() with { Issues = [new(@"B:\other\link", NtfsPrototype.NtfsReparsePolicy.Reason)] }),
            ("reparse issue cannot contain traversal", () => Partial() with { Issues = [new(PathValue + @"\folder\..\link", NtfsPrototype.NtfsReparsePolicy.Reason)] }),
            ("reparse details unique", () => Partial(2) with { Issues = [Partial().Issues[0], Partial().Issues[0]] }),
            ("reparse requires incomplete root", () => { var value = Partial(); value.Root.Incomplete = false; return value; }),
            ("reparse cannot hide allocation issues", () => Partial() with { AllocationIssues = [new("synthetic", "synthetic")] }),
            ("reparse cannot hide child unknown allocation", () =>
            {
                var value = Partial(); var child = DiskEntry.FromName("file.bin", false, value.Root, ".bin");
                child.UnknownAllocationFiles = 1; value.Root.MutableChildren.Add(child); return value;
            }),
            ("reparse cannot hide incomplete files", () =>
            {
                var value = Partial(); var child = DiskEntry.FromName("file.bin", false, value.Root, ".bin");
                child.Incomplete = true; value.Root.MutableChildren.Add(child); return value;
            }),
            ("reparse cannot hide an unexplained incomplete directory", () =>
            {
                var value = Partial(); var child = DiskEntry.FromName("folder", true, value.Root, "文件夹");
                child.Incomplete = true; value.Root.MutableChildren.Add(child); return value;
            }),
            ("reparse cannot duplicate an included child", () =>
            {
                var value = Partial(); value.Root.MutableChildren.Add(DiskEntry.FromName("LINK-0", false, value.Root, ".bin")); return value;
            }),
            ("truncated reparse cannot mark a directory complete", () =>
            {
                var value = Partial(2001); value.Root.MutableChildren.Add(DiskEntry.FromName("folder", true, value.Root, "文件夹")); return value;
            })
        ]);
        foreach (var pair in malformed)
        {
            int before = ordinaryCalls; bool rejected = false;
            try { _ = Call((_, _, _) => pair.Result()); }
            catch (FastScanUnavailableException failure) { rejected = failure.Code == FastScanWireError.ProtocolError && !failure.CleanupConfirmed; }
            Check(rejected && ordinaryCalls == before, "invalid success isolated " + pair.Name);
        }
        {
            var previous = Result(); _ = Call((_, _, _) => throw new FastScanUnavailableException(FastScanWireError.Unsupported, true), previous);
            Check(seenBaseline == null, "snapshot previous is not a live refresh baseline");
            previous = Result(false); _ = Call((_, _, _) => throw new FastScanUnavailableException(FastScanWireError.Unsupported, true), previous);
            Check(ReferenceEquals(seenBaseline, previous), "ordinary previous remains eligible for ordinary verification");
        }
        Check(Enum.GetValues<FastScanWireError>().All(code => !string.IsNullOrWhiteSpace(FastScanCoordinator.Reason(code))), "fixed user reasons complete");
        Console.WriteLine($"Fast scan coordinator: {passed} checks passed."); return passed;
    }
}
