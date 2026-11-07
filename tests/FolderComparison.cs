using DiskAtlas;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

// Explicit opt-in for a user-named directory. Reports stay outside the repository.
internal static class FolderComparison
{
    private sealed record Sample(int Run, string Mode, double ScanMs, double SettledMs, double CpuMs,
        long AllocatedBytes, long Files, long Directories, long Skipped, long UnknownAllocation,
        bool MatchesFirstSnapshot, Difference? Differences, string Method, int DirectoryWorkers, int FileReaders, long BatchFiles);
    private readonly record struct EntryState(bool Directory, long Size, long Allocated, long Modified,
        long Files, long Folders, long Unknown, bool Shared, bool Incomplete)
    {
        public static EntryState Of(DiskEntry e) => new(e.IsDirectory, e.Size, e.AllocatedSize, e.Modified.Ticks,
            e.FileCount, e.FolderCount, e.UnknownAllocationFiles, e.SharedAllocation, e.Incomplete);
    }
    private sealed record Difference(int Added, int Removed, int ChangedFiles, int ChangedDirectories,
        int ModifiedTime, int SizeOrAllocation, int CountsOrState, int ChangedFilesWithinWorkspace, int FreshNativeMatches);
    private static Dictionary<string, EntryState> Capture(DiskEntry root) => root.Descendants().Prepend(root)
        .ToDictionary(e => e.Path, EntryState.Of, StringComparer.OrdinalIgnoreCase);
    private static Difference Compare(DiskEntry root, Dictionary<string, EntryState> baseline)
    {
        int added = 0, found = 0, files = 0, directories = 0, modified = 0, sizes = 0, states = 0, projectFiles = 0, freshMatches = 0;
        string workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")) + Path.DirectorySeparatorChar;
        foreach (var e in root.Descendants().Prepend(root))
        {
            string path = e.Path; var current = EntryState.Of(e);
            if (!baseline.TryGetValue(path, out var before)) { added++; continue; }
            found++; if (current == before) continue;
            if (e.IsDirectory) directories++; else files++;
            if (current.Modified != before.Modified) modified++;
            if (current.Size != before.Size || current.Allocated != before.Allocated) sizes++;
            if (current.Directory != before.Directory || current.Files != before.Files || current.Folders != before.Folders ||
                current.Unknown != before.Unknown || current.Shared != before.Shared || current.Incomplete != before.Incomplete) states++;
            if (!e.IsDirectory)
            {
                if (path.StartsWith(workspace, StringComparison.OrdinalIgnoreCase)) projectFiles++;
                // Outside timing. Confirm changed file metadata using a fresh native read,
                // without emitting its path, identity or contents in the report.
                try
                {
                    if (NativeStorage.TryRead(path, false, File.GetAttributes(path), out var fresh, out _) && fresh != null &&
                        fresh.Identity == e.Storage?.Identity && fresh.LogicalBytes == e.Size && fresh.Modified == e.Modified &&
                        fresh.AllocatedBytes == e.Storage?.AllocatedBytes) freshMatches++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        return new(added, baseline.Count - found, files, directories, modified, sizes, states, projectFiles, freshMatches);
    }
    internal static int RunChecks()
    {
        string root = Path.Combine(Path.GetTempPath(), "AsukaLens-comparison-check-" + Guid.NewGuid().ToString("N"));
        void Check(bool value) { if (!value) throw new InvalidOperationException("Comparison check failed"); }
        try
        {
            Directory.CreateDirectory(root); string item = Path.Combine(root, "synthetic.bin");
            File.WriteAllBytes(item, new byte[1024]); File.SetLastWriteTimeUtc(item, new DateTime(2020, 1, 1));
            Directory.SetLastWriteTimeUtc(root, new DateTime(2020, 1, 1));
            DiskEntry Scan() => new DiskScanner(1).Scan(root, null, CancellationToken.None).Root;
            var before = Capture(Scan()); var same = Compare(Scan(), before);
            Check(same == new Difference(0, 0, 0, 0, 0, 0, 0, 0, 0));
            File.WriteAllBytes(item, new byte[16384]); File.SetLastWriteTimeUtc(item, new DateTime(2021, 1, 1));
            var changed = Compare(Scan(), before);
            Check(changed.Added == 0 && changed.Removed == 0 && changed.ChangedFiles == 1 &&
                changed.ModifiedTime == 1 && changed.SizeOrAllocation == 2 && changed.FreshNativeMatches == 1);
            File.Move(item, Path.Combine(root, "replacement.bin")); var renamed = Compare(Scan(), before);
            Check(renamed.Added == 1 && renamed.Removed == 1);
            Console.WriteLine("PASS comparison equality, changed metadata/native confirmation, added/removed membership"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL comparison checks: " + ex.GetType().Name); return 1; }
        finally
        {
            if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(root, true);
        }
    }
    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
    private static async Task Wait(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready()) { if (watch.Elapsed > TimeSpan.FromMinutes(2)) throw new TimeoutException(); await Task.Delay(25); }
    }
    public static int Run(string directory, bool pair = false)
    {
        if (!Directory.Exists(directory)) { Console.Error.WriteLine("The explicit comparison directory is unavailable."); return 2; }
        string id = Guid.NewGuid().ToString("N"), privateRoot = Path.Combine(Path.GetTempPath(), "AsukaLens-scan-comparison-" + id);
        string scanRoot = Path.GetFullPath(directory);
        string scanPrefix = Path.EndsInDirectorySeparator(scanRoot) ? scanRoot : scanRoot + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(privateRoot).StartsWith(scanPrefix, StringComparison.OrdinalIgnoreCase))
        { Console.Error.WriteLine("Comparison output must be outside the scanned directory."); return 2; }
        Directory.CreateDirectory(privateRoot); var samples = new List<Sample>(); Exception? failure = null;
        ScanMode[] order = pair ? [ScanMode.Automatic, ScanMode.HighSpeed] : [ScanMode.Automatic, ScanMode.HighSpeed, ScanMode.HighSpeed, ScanMode.Automatic];
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using var diagnostics = new DiagnosticService(Path.Combine(privateRoot, "app-data"));
                using var process = Process.GetCurrentProcess();
                using var form = new MainForm(null, diagnostics: diagnostics) { Text = "AsukaLens · 本地扫描对比" };
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        await diagnostics.Ready; string? first = null; Dictionary<string, EntryState>? baseline = null;
                        for (int run = 0; run < order.Length; run++)
                        {
                            if (form.IsDisposed) throw new OperationCanceledException();
                            var mode = order[run]; Field<ComboBox>(form, "strategyCombo").SelectedIndex = (int)mode;
                            await diagnostics.FlushAsync(); await Task.Delay(200);
                            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                            typeof(MainForm).GetField("forceFullNext", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
                            Console.WriteLine($"START local comparison {run + 1}/{order.Length} {mode}; forced full scan");
                            long allocation = GC.GetTotalAllocatedBytes(true); var cpu = process.TotalProcessorTime; var timer = Stopwatch.StartNew();
                            using var heartbeat = new System.Windows.Forms.Timer { Interval = 30000 };
                            heartbeat.Tick += (_, _) =>
                            {
                                var live = Field<LiveScanSession?>(form, "liveSession");
                                var current = live == null ? null : (DiskEntry?)typeof(LiveScanSession).GetField("root", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(live);
                                long files = 0, directories = 0;
                                if (current != null) lock (current) { files = current.FileCount; directories = current.FolderCount; }
                                Console.WriteLine($"PROGRESS local comparison {run + 1}/{order.Length} {mode}: {timer.Elapsed.TotalSeconds:F0} s; {files} files; {directories} directories");
                            };
                            heartbeat.Start();
                            var task = (Task)typeof(MainForm).GetMethod("ScanFolder", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [directory])!;
                            try { await task.WaitAsync(TimeSpan.FromMinutes(15)); }
                            catch (TimeoutException) { Field<ActionButton>(form, "cancel").PerformClick(); throw; }
                            double scanMs = timer.Elapsed.TotalMilliseconds;
                            await Wait(() => Field<DataGridView>(form, "grid").Enabled && Field<CancellationTokenSource?>(form, "viewCancellation") == null);
                            double settled = timer.Elapsed.TotalMilliseconds;
                            heartbeat.Stop();
                            double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds; allocation = GC.GetTotalAllocatedBytes(true) - allocation;
                            var result = Field<ScanResult?>(form, "scan");
                            if (result == null || result.Statistics?.Method != RefreshMethod.FullScan ||
                                Field<ScanProgressControl>(form, "progressBar").State is ScanDisplayState.Cancelled or ScanDisplayState.Failed)
                                throw new OperationCanceledException();
                            string hash = MixedScanBenchmark.Fingerprint(result.Root); first ??= hash; var stats = result.Statistics!;
                            Difference? differences = null;
                            if (pair) { baseline ??= Capture(result.Root); differences = Compare(result.Root, baseline); }
                            samples.Add(new(run + 1, mode.ToString(), scanMs, settled, cpuMs, allocation, result.Root.FileCount,
                                result.Root.FolderCount, result.Skipped, result.Root.UnknownAllocationFiles, hash == first, differences, stats.Method.ToString(),
                                stats.PeakDirectoryWorkers, stats.PeakFileReaders, stats.ParallelFileReads));
                            Console.WriteLine($"DONE local comparison {run + 1}/{order.Length} {mode}: {scanMs / 1000:F2} s; same snapshot: {hash == first}");
                        }
                        if (!pair) foreach (var group in samples.GroupBy(s => s.Mode))
                            Console.WriteLine($"MEDIAN {group.Key}: {MixedScanBenchmark.Median(group.Select(s => s.ScanMs)) / 1000:F2} s");
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        bool ended = thread.Join(TimeSpan.FromMinutes(62));
        var completedSamples = ended ? samples.ToArray() : [];
        File.WriteAllText(Path.Combine(privateRoot, "result.json"), JsonSerializer.Serialize(new
        {
            Schema = 2, Private = true, PerformanceComparable = !pair, Completed = ended && failure == null && samples.Count == order.Length,
            Method = "Explicit local directory, production MainForm with isolated diagnostics; forced full scans ordered Automatic/HighSpeed/HighSpeed/Automatic. Optional --pair uses Automatic/HighSpeed once for correctness diagnosis and retains an extra baseline after the first run; its timings are not a performance comparison. No extra warmup or OS cache flush. Equality evaluated after timing; changing snapshots are marked, not called equal. Not a cold-cache benchmark.",
            Privacy = "No paths, names, identities or snapshot fingerprints in this report. Do not commit or publish the report or isolated runtime diagnostics.",
            TargetedPair = pair, Samples = completedSamples
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine("PRIVATE_REPORT " + id);
        if (!ended || failure != null || completedSamples.Length != order.Length) { Console.Error.WriteLine("FAIL local comparison: " + (failure?.GetType().Name ?? (ended ? "Cancelled" : "Timeout"))); return 1; }
        Console.WriteLine("PASS local comparison completed; inspect snapshot equality before attributing timing differences."); return 0;
    }
}
