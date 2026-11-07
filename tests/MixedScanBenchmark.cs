using DiskAtlas;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Retain one owned synthetic fixture between before/after runs; no user directory is accepted.
internal static class MixedScanBenchmark
{
    private sealed record Sample(string Shape, string Mode, int Run, double WallMs, double CpuMs,
        long AllocatedBytes, int DirectoryWorkers, int FileReaders, long BatchFiles, int BufferedFiles, int Helpers);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Mixed benchmark invariant failed"); }
    internal static double Median(IEnumerable<double> source)
    { var x = source.Order().ToArray(); return (x[(x.Length - 1) / 2] + x[x.Length / 2]) / 2; }

    internal static string Fingerprint(DiskEntry root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var e in root.Descendants().Prepend(root).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            hash.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(root.Path, e.Path)}\t{e.IsDirectory}\t{e.Size}\t{e.AllocatedSize}\t{e.Modified.Ticks}\t{e.FileCount}\t{e.FolderCount}\t{e.UnknownAllocationFiles}\t{e.SharedAllocation}\t{e.Incomplete}\n"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static int Run(string phase)
    {
        if (phase is not ("before" or "after")) return 2;
        string workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string artifacts = Path.Combine(workspace, "artifacts"), marker = Path.Combine(artifacts, "mixed-scan-owned.txt");
        string report = Path.Combine(artifacts, $"mixed-scan-{phase}.json");
        var samples = new List<Sample>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15)); var token = cancellation.Token;
        try
        {
            Directory.CreateDirectory(artifacts); Check(!File.Exists(report));
            Guid id;
            if (phase == "before")
            {
                Check(!File.Exists(marker)); id = Guid.NewGuid();
                File.WriteAllText(marker, id.ToString("N"));
            }
            else Check(Guid.TryParseExact(File.ReadAllText(marker), "N", out id));
            string fixture = Path.Combine(artifacts, "mixed-scan-data-" + id.ToString("N"));
            if (phase == "before")
            {
                void Create(string directory, int count)
                {
                    Directory.CreateDirectory(directory);
                    for (int i = 0; i < count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        File.WriteAllBytes(Path.Combine(directory, $"item-{i:00000}.bin"), new byte[i % 128 == 0 ? 8192 : i % 512]);
                    }
                }
                Create(Path.Combine(fixture, "flat"), 32768);
                for (int d = 0; d < 32; d++) Create(Path.Combine(fixture, "many-large", $"large-{d:00}"), 4096);
                for (int d = 0; d < 16; d++) Create(Path.Combine(fixture, "mixed", $"large-{d:00}"), 4096);
                for (int d = 0; d < 2048; d++)
                    for (int leaf = 0; leaf < 4; leaf++) Create(Path.Combine(fixture, "mixed", "small", $"group-{d:0000}", $"leaf-{leaf}"), 8);
                foreach (string directory in Directory.EnumerateDirectories(fixture, "*", SearchOption.AllDirectories).Prepend(fixture))
                    Directory.SetLastWriteTimeUtc(directory, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            }
            Check(Directory.Exists(fixture) && (File.GetAttributes(fixture) & FileAttributes.ReparsePoint) == 0);
            using var process = Process.GetCurrentProcess();
            foreach (string shape in new[] { "flat", "many-large", "mixed" })
            {
                string path = Path.Combine(fixture, shape);
                var coordinator = new ScanCoordinator(_ => null);
                ScanResult Scan(ScanMode mode) => coordinator.Scan(path, null, new(mode), null, token, new LiveScanSession(), forceFull: true);
                var baseline = Scan(ScanMode.Automatic); string expected = Fingerprint(baseline.Root);
                Check(baseline.Skipped == 0 && baseline.Root.UnknownAllocationFiles == 0 && baseline.Root.FileCount == (shape == "flat" ? 32768 : 131072));
                _ = Scan(ScanMode.HighSpeed);
                Console.WriteLine($"DATA {shape}: {baseline.Root.FileCount} files; {baseline.Root.FolderCount} directories; {NativeStorage.DetectDisk(path)}");
                for (int run = 0; run < 6; run++)
                {
                    ScanMode[] order = run % 2 == 0 ? [ScanMode.Automatic, ScanMode.HighSpeed] : [ScanMode.HighSpeed, ScanMode.Automatic];
                    foreach (var mode in order)
                    {
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        var cpu = process.TotalProcessorTime; long allocated = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
                        var result = Scan(mode); timer.Stop();
                        double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds; long allocation = GC.GetTotalAllocatedBytes(true) - allocated;
                        Check(result.Skipped == 0 && Fingerprint(result.Root) == expected && result.Statistics!.Method == RefreshMethod.FullScan);
                        var s = result.Statistics!;
                        samples.Add(new(shape, mode.ToString(), run + 1, timer.Elapsed.TotalMilliseconds, cpuMs, allocation,
                            s.PeakDirectoryWorkers, s.PeakFileReaders, s.ParallelFileReads, s.PeakBufferedFiles, s.PeakSupplementalReaders));
                        Console.WriteLine($"RUN {shape} {run + 1}/6 {mode}: {timer.Elapsed.TotalMilliseconds:F2} ms");
                    }
                }
                foreach (var group in samples.Where(s => s.Shape == shape).GroupBy(s => s.Mode))
                    Console.WriteLine($"BENCH {shape} {group.Key}: {Median(group.Select(s => s.WallMs)):F2} ms; CPU {Median(group.Select(s => s.CpuMs)):F2} ms; allocation {Median(group.Select(s => (double)s.AllocatedBytes)):F0} B");
            }
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                Schema = 2, Phase = phase, RunsPerMode = 6,
                Method = "Production coordinator, forced full scans, identical retained synthetic fixture. Warm each mode then alternate AB/BA six times. Equality checks, fixture creation and explicit GC outside timing. No UI rendering or physical I/O measurement.",
                Data = "flat:32768 files; many-large:32x4096; mixed:16x4096 plus 2048x4x8. Every 128th file is 8192 bytes, others 0-511 bytes. No OS cache flush or controlled external workload.",
                Memory = "Total managed allocations, not retained or peak process memory. Process CPU includes all managed/native threads.",
                Correctness = "Every timed result matches all baseline entries by relative position, type, sizes, counts, modified time, sharing, unknown and incomplete state. No user paths, names, logs or identities in this report.",
                Samples = samples
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            Console.WriteLine("PASS mixed benchmark; owned fixture retained for same-data comparison and explicit final cleanup."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL mixed benchmark: " + ex.GetType().Name); return 1; }
    }
}
