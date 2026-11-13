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
        ScanResult Ordinary(ScanResult? baseline) { ordinaryCalls++; seenBaseline = baseline; return Result(false); }
        ScanResult Call(Func<string, IProgress<ScanProgress>?, CancellationToken, ScanResult> fast,
            ScanResult? previous = null, CancellationToken token = default, Progress? progress = null) =>
            FastScanCoordinator.Scan(PathValue, previous, new(), progress, token, new(), false, fast, Ordinary);
        ScanResult Fast(string path, IProgress<ScanProgress>? progress, CancellationToken token)
        { fastCalls++; return Result(); }
        var complete = Call(Fast);
        Check(complete.Source != null && complete.Checkpoint == null && complete.FastScanFallback == null && ordinaryCalls == 0 && fastCalls == 1, "complete snapshot published once");
        Check(complete.Root.Path == PathValue && complete.Elapsed >= TimeSpan.Zero, "complete source path and elapsed");
        foreach (var code in Enum.GetValues<FastScanWireError>().Where(code => code != FastScanWireError.Cancelled && code != FastScanWireError.CleanupUnconfirmed))
        {
            int before = ordinaryCalls; var progress = new Progress();
            var fallback = Call((_, _, _) => throw new FastScanUnavailableException(code, true), progress: progress);
            Check(ordinaryCalls == before + 1 && fallback.Source == null && fallback.FastScanFallback == code && fallback.Checkpoint == null, "confirmed closed failure falls back " + code);
            Check(progress.Values.Count == 1 && progress.Values[0].Phase == ScanPhase.Preparing && progress.Values[0].Message?.Contains("本次使用普通扫描", StringComparison.Ordinal) == true,
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
