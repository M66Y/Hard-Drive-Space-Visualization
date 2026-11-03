using DiskAtlas;
using System.Diagnostics;
using System.Text.Json;

// Compares equal, warm, unchanged synthetic snapshots. A simulated journal is
// explicitly separate from the native route; it proves skipped work, not native availability.
internal static class RefreshBenchmark
{
    private sealed record Sample(string Route, string ActualMethod, double WallMs, double CpuMs, long AllocatedBytes,
        long VerifiedDirectories, long VerifiedFiles, long ReadDirectories, long ReusedFiles, string JournalState, int NativeError, int ReadOnlyOpenError);
    public static int Run()
    {
        string parent = Path.GetFullPath("artifacts"), report = Path.Combine(parent, "refresh-benchmark.json");
        if (File.Exists(report)) throw new IOException("Archive the previous report before running.");
        string root = Path.Combine(parent, "refresh-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (int d = 0; d < 60; d++)
            {
                string dir = Path.Combine(root, $"folder-{d:00}"); Directory.CreateDirectory(dir);
                for (int f = 0; f < 40; f++) File.WriteAllBytes(Path.Combine(dir, $"sample-{f:00}.dat"), new byte[64 + f]);
                Directory.SetLastWriteTimeUtc(dir, new DateTime(2020, 1, 1));
            }
            Directory.SetLastWriteTimeUtc(root, new DateTime(2020, 1, 1));
            var native = new ScanCoordinator(); var metadata = new ScanCoordinator(_ => null);
            var simulated = new ScanCoordinator(_ => new JournalRegression.ControlledJournal());
            var options = new ScanOptions(ScanMode.HighSpeed);
            var baseline = native.Scan(root, null, options, null, CancellationToken.None);
            var simulatedBaseline = baseline with { Checkpoint = new(9, 20, 0) };
            var expected = Entries(baseline.Root);
            var samples = new List<Sample>();
            ScanResult Run(int route) => route switch
            {
                0 => native.Scan(root, baseline, options, null, CancellationToken.None, forceFull: true),
                1 => metadata.Scan(root, baseline, options, null, CancellationToken.None),
                2 => simulated.Scan(root, simulatedBaseline, options, null, CancellationToken.None),
                _ => native.Scan(root, baseline, options, null, CancellationToken.None)
            };
            void Equal(ScanResult result)
            {
                if (!expected.SequenceEqual(Entries(result.Root)) || result.Skipped != 0) throw new InvalidOperationException("Refresh differs from full scan.");
            }
            for (int route = 0; route < 4; route++) Equal(Run(route));
            using var process = Process.GetCurrentProcess();
            for (int round = 0; round < 8; round++)
            {
                var order = Enumerable.Range(0, 4).Select(i => (i + round % 4) % 4);
                if (round >= 4) order = order.Reverse();
                foreach (int route in order)
                {
                    process.Refresh(); var cpu = process.TotalProcessorTime;
                    long allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
                    var result = Run(route); clock.Stop(); process.Refresh();
                    var s = result.Statistics!;
                    samples.Add(new(new[] { "forced-full", "metadata-verification", "simulated-continuous-journal", "actual-native" }[route],
                        s.Method.ToString(), clock.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpu).TotalMilliseconds,
                        GC.GetTotalAllocatedBytes(true) - allocated, s.VerifiedDirectories, s.VerifiedFiles, s.ReadDirectories, s.ReusedFiles,
                        s.Journal?.State.ToString() ?? "Unavailable", s.Journal?.NativeError ?? 0, s.Journal?.ReadOnlyOpenError ?? 0));
                    Equal(result);
                    if (route == 2 && (s.VerifiedFiles != 0 || s.ReadDirectories != 0)) throw new InvalidOperationException("Simulated no-change journal did filesystem enumeration.");
                }
            }
            double Median(IEnumerable<double> values) { var b = values.Order().ToArray(); return (b[3] + b[4]) / 2; }
            var summaries = samples.GroupBy(s => s.Route).Select(g => new
            {
                route = g.Key, actual_method = g.First().ActualMethod, median_ms = Median(g.Select(s => s.WallMs)),
                median_cpu_ms = Median(g.Select(s => s.CpuMs)), median_allocated_bytes = Median(g.Select(s => (double)s.AllocatedBytes)),
                verified_directories = g.First().VerifiedDirectories, verified_files = g.First().VerifiedFiles,
                read_directories = g.First().ReadDirectories, reused_files = g.First().ReusedFiles,
                journal_state = g.First().JournalState, native_error = g.First().NativeError, read_only_error = g.First().ReadOnlyOpenError
            }).ToArray();
            foreach (var summary in summaries) Console.WriteLine(JsonSerializer.Serialize(summary));
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                schema_version = 1, files = 2400, directories = 61, runs_per_route = 8,
                method = "Warm metadata cache; four cyclic orders and reversals. Actual coordinator, HighSpeed mode. No OS cache flush or external-load control. Fixture creation and equality checks excluded.",
                limits = "Simulated journal is not a native speed claim. CPU time can be coarse; allocation is managed bytes, not peak memory or physical I/O.",
                correctness = "Every entry compared by relative path, logical/allocated size, modification time, counts, unknown/incomplete/shared state.",
                summaries, samples
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL refresh benchmark: " + ex.GetType().Name); return 1; }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar + "refresh-benchmark-", StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe benchmark cleanup.");
            Directory.Delete(root, true);
        }
    }
    private static string[] Entries(DiskEntry root) => root.Descendants().Prepend(root).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
        .Select(e => $"{Path.GetRelativePath(root.Path, e.Path)}|{e.IsDirectory}|{e.Size}|{e.AllocatedSize}|{e.Modified.Ticks}|{e.FileCount}|{e.FolderCount}|{e.UnknownAllocationFiles}|{e.Incomplete}|{e.SharedAllocation}").ToArray();
}
