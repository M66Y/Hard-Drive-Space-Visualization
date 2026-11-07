using DiskAtlas;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Opt-in measurement of the existing production pipeline; it does not change scan policy.
internal static class ScanResponseBenchmark
{
    private sealed record UiSample(string Shape, string Mode, int Run, double ScanMs, double SettledMs,
        double? FirstPreviewMs, double CpuMs, long AllocatedBytes, long SampledWorkingSetBytes,
        double TickGapP95Ms, double TickGapMaxMs, double SearchRoundTripMs,
        double? CancelDispatchMs, double? CancelCompletionMs, int DirectoryWorkers,
        int FileReaderLimit, int PeakFileReaders, int PeakBufferedFiles, long ParallelFileReads);
    private sealed record ReadSample(string Shape, int Workers, int Run, double WallMs, double CpuMs, long AllocatedBytes);
    private static T Field<T>(object target, string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static Task Scan(MainForm form, string path)
    {
        Set(form, "forceFullNext", true);
        return (Task)typeof(MainForm).GetMethod("ScanFolder", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [path])!;
    }
    private static async Task Wait(Func<bool> ready)
    {
        var deadline = Stopwatch.StartNew();
        while (!ready()) { if (deadline.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(); await Task.Delay(10); }
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Benchmark invariant failed"); }

    public static int Run()
    {
        string workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string artifacts = Path.Combine(workspace, "artifacts");
        string fixture = Path.Combine(artifacts, "scan-response-" + Guid.NewGuid().ToString("N"));
        string report = Path.Combine(artifacts, "scan-response-results.json");
        var samples = new List<UiSample>(); var reads = new List<ReadSample>();
        var datasets = new List<(string Shape, string Path, ScanResult Baseline)>();
        Exception? failure = null;
        Thread? uiThread = null;
        try
        {
            if (File.Exists(report)) throw new IOException("Output already exists");
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            foreach (string shape in new[] { "flat", "wide" })
            {
                string path = Path.Combine(fixture, shape); Directory.CreateDirectory(path);
                int directories = shape == "flat" ? 1 : 256, count = shape == "flat" ? 32000 : 125;
                for (int d = 0; d < directories; d++)
                {
                    string dir = directories == 1 ? path : Path.Combine(path, $"folder-{d:000}"); Directory.CreateDirectory(dir);
                    for (int f = 0; f < count; f++)
                    {
                        limit.Token.ThrowIfCancellationRequested();
                        File.WriteAllBytes(Path.Combine(dir, $"file-{f:00000}.bin"), new byte[(f * 31 + d) % 512]);
                    }
                }
                foreach (string dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).Append(path))
                    Directory.SetLastWriteTimeUtc(dir, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                var baseline = new DiskScanner().Scan(path, null, limit.Token);
                Check(baseline.Skipped == 0 && baseline.Root.UnknownAllocationFiles == 0 && baseline.Root.FileCount == 32000);
                datasets.Add((shape, path, baseline));
                Console.WriteLine($"DATA {shape}: 32000 files; device {NativeStorage.DetectDisk(path)}");
            }

            // Read-only primitive experiment: pre-enumerated paths, no publication, graph building or UI.
            // Its timings are NOT a prediction of a future parallel scanner's end-to-end speed.
            using var process = Process.GetCurrentProcess();
            foreach (var dataset in datasets)
            {
                var files = dataset.Baseline.Root.Descendants().Where(e => !e.IsDirectory)
                    .Select(e => (Entry: e, Attributes: File.GetAttributes(e.Path))).ToArray();
                foreach (int workers in new[] { 1, 4, 8 }) ReadAll(workers);
                for (int run = 0; run < 6; run++)
                {
                    int[][] orders = [[1, 4, 8], [4, 8, 1], [8, 1, 4], [8, 4, 1], [1, 8, 4], [4, 1, 8]];
                    foreach (int workers in orders[run])
                    {
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        long allocation = GC.GetTotalAllocatedBytes(true); var cpu = process.TotalProcessorTime; var watch = Stopwatch.StartNew();
                        ReadAll(workers); watch.Stop();
                        reads.Add(new(dataset.Shape, workers, run + 1, watch.Elapsed.TotalMilliseconds,
                            (process.TotalProcessorTime - cpu).TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - allocation));
                    }
                }
                foreach (var group in reads.Where(r => r.Shape == dataset.Shape).GroupBy(r => r.Workers))
                    Console.WriteLine($"READ {dataset.Shape} {group.Key} workers: {Median(group.Select(r => r.WallMs)):F2} ms median");
                void ReadAll(int workers) => Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = limit.Token }, item =>
                {
                    Check(NativeStorage.TryReadSnapshot(item.Entry.Path, false, item.Attributes, out var read, out _) && read.Matches(item.Entry.Storage));
                });
            }

            var thread = uiThread = new Thread(() =>
            {
                try
                {
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    using var diagnostics = new DiagnosticService(Path.Combine(fixture, "app-data"));
                    using var form = new MainForm(null, diagnostics: diagnostics) { Text = "AsukaLens · 性能测量（合成数据）" };
                    form.Shown += async (_, _) =>
                    {
                        try
                        {
                            await diagnostics.Ready;
                            foreach (var dataset in datasets)
                            {
                                string expected = Fingerprint(dataset.Baseline.Root);
                                foreach (int mode in new[] { 0, 1, 2 })
                                {
                                    Field<ComboBox>(form, "strategyCombo").SelectedIndex = mode;
                                    await Scan(form, dataset.Path); await Wait(() => Field<DataGridView>(form, "grid").Enabled && Field<CancellationTokenSource?>(form, "viewCancellation") == null);
                                }
                                for (int run = 0; run < 6; run++)
                                {
                                    int[][] orders = [[0, 1, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0], [0, 2, 1], [1, 0, 2]];
                                    foreach (int mode in orders[run])
                                    {
                                        limit.Token.ThrowIfCancellationRequested();
                                        Field<ComboBox>(form, "strategyCombo").SelectedIndex = mode;
                                        // Prepare folder view and idle frames outside the measurement interval.
                                        typeof(MainForm).GetMethod("SetMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [0]);
                                        await Wait(() => Field<DataGridView>(form, "grid").Enabled && Field<CancellationTokenSource?>(form, "viewCancellation") == null);
                                        await diagnostics.FlushAsync(); await Task.Delay(120);
                                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                                        var clock = Stopwatch.StartNew(); double? first = null; var gaps = new List<double>(); double last = 0; long workingSet = 0;
                                        using var tick = new System.Windows.Forms.Timer { Interval = 20 };
                                        tick.Tick += (_, _) =>
                                        {
                                            double now = clock.Elapsed.TotalMilliseconds; gaps.Add(now - last); last = now;
                                            if (first == null && Field<bool>(form, "showingPreview") && Field<DataGridView>(form, "grid").Rows.Count > 0) first = now;
                                            process.Refresh(); workingSet = Math.Max(workingSet, process.WorkingSet64);
                                        };
                                        long allocation = GC.GetTotalAllocatedBytes(true); var cpu = process.TotalProcessorTime;
                                        tick.Start(); await Scan(form, dataset.Path); double scanMs = clock.Elapsed.TotalMilliseconds;
                                        await Wait(() => Field<DataGridView>(form, "grid").Enabled && Field<CancellationTokenSource?>(form, "viewCancellation") == null);
                                        double settled = clock.Elapsed.TotalMilliseconds; tick.Stop();
                                        double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds; long allocated = GC.GetTotalAllocatedBytes(true) - allocation;
                                        var completed = Field<ScanResult>(form, "scan"); Check(Fingerprint(completed.Root) == expected && completed.Skipped == 0);
                                        typeof(MainForm).GetMethod("SetMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [1]);
                                        await Wait(() => Field<DataGridView>(form, "grid").Enabled && Field<CancellationTokenSource?>(form, "viewCancellation") == null);
                                        var searchClock = Stopwatch.StartNew(); Field<TextBox>(form, "search").Text = "file-00001";
                                        int matches = dataset.Shape == "flat" ? 1 : 256;
                                        await Wait(() => Field<List<MapItem>>(form, "sortedRows").Count == matches && Field<DataGridView>(form, "grid").Enabled && Field<CancellationTokenSource?>(form, "viewCancellation") == null);
                                        double searchMs = searchClock.Elapsed.TotalMilliseconds;
                                        Field<TextBox>(form, "search").Clear();
                                        await Wait(() => Field<List<MapItem>>(form, "sortedRows").Count == 500 && Field<DataGridView>(form, "grid").Enabled && Field<CancellationTokenSource?>(form, "viewCancellation") == null);
                                        // Cancellation is requested through the actual UI control after 75 ms.
                                        var cancelClock = Stopwatch.StartNew(); double? requested = null;
                                        using var cancelTimer = new System.Windows.Forms.Timer { Interval = 15 };
                                        cancelTimer.Tick += (_, _) =>
                                        {
                                            if (cancelClock.ElapsedMilliseconds < 75 || !Field<ActionButton>(form, "cancel").Visible) return;
                                            requested = cancelClock.Elapsed.TotalMilliseconds; cancelTimer.Stop(); Field<ActionButton>(form, "cancel").PerformClick();
                                        };
                                        cancelTimer.Start(); await Scan(form, dataset.Path); cancelTimer.Stop();
                                        double? cancelMs = requested.HasValue ? cancelClock.Elapsed.TotalMilliseconds - requested : null;
                                        if (requested.HasValue) Check(ReferenceEquals(completed, Field<ScanResult>(form, "scan")) && Field<ScanProgressControl>(form, "progressBar").State == ScanDisplayState.Cancelled);
                                        await Wait(() => Field<DataGridView>(form, "grid").Enabled && Field<CancellationTokenSource?>(form, "viewCancellation") == null);
                                        samples.Add(new(dataset.Shape, ((ScanMode)mode).ToString(), run + 1, scanMs, settled, first, cpuMs, allocated, workingSet,
                                            Percentile(gaps, .95), gaps.DefaultIfEmpty(0).Max(), searchMs, requested, cancelMs, completed.Statistics!.PeakDirectoryWorkers,
                                            completed.Statistics.FileReaderLimit, completed.Statistics.PeakFileReaders, completed.Statistics.PeakBufferedFiles, completed.Statistics.ParallelFileReads));
                                    }
                                }
                                foreach (var group in samples.Where(s => s.Shape == dataset.Shape).GroupBy(s => s.Mode))
                                    Console.WriteLine($"UI {dataset.Shape} {group.Key}: scan {Median(group.Select(s => s.ScanMs)):F2} ms; settled {Median(group.Select(s => s.SettledMs)):F2} ms; preview observations {group.Count(s => s.FirstPreviewMs.HasValue)}/6; tick max median {Median(group.Select(s => s.TickGapMaxMs)):F2} ms");
                            }
                            File.WriteAllText(report, JsonSerializer.Serialize(new
                            {
                                Schema = 2, Source = "Production pipeline; implementation revision is recorded in the enclosing iteration", FilesPerShape = 32000, RunsPerMode = 6, Dpi = form.DeviceDpi,
                                Cache = "Warm synthetic metadata; fixture creation, warmup, explicit GC and equality checks outside timed scans. OS cache not flushed. No controlled external workload.",
                                Ui = "Real MainForm with isolated enabled diagnostics. ScanMs ends at ScanFolder return; SettledMs includes completion of final list query. FirstPreviewMs is first 20ms UI timer observation of a nonempty partial list, not first physical painted frame; null means no interim preview observed.",
                                Response = "Tick gaps are UI timer dispatch intervals, not FPS/input latency. Search includes existing 220ms debounce. CancelDispatchMs is actual UI request time after a 75ms target; CancelCompletionMs is request to scan unwind, not arbitrary native I/O cancellation guarantee.",
                                Memory = "20ms UI-thread samples of whole-process working set maximum within each scan; sampling can miss blocked intervals. AllocatedBytes is total managed allocation, not retained memory. CPU is whole-process time and coarse for short runs.",
                                NativeRead = "Pre-enumerated synthetic files; existing NativeStorage.TryReadSnapshot with 1/4/8 workers. Every snapshot matches baseline identity, sizes, links, open-write state and timestamps. Excludes enumeration, graph construction, publication and UI; not an end-to-end speedup claim.",
                                Correctness = "All timed completed UI scans match every baseline entry. Requested cancellation preserves the prior completed ScanResult. No user files/configuration used.",
                                Samples = samples, NativeReads = reads
                            }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { form.Close(); }
                    };
                    Application.Run(form);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!thread.Join(TimeSpan.FromMinutes(4))) throw new TimeoutException();
            if (failure != null) throw failure;
            Console.WriteLine("PASS scan response baseline, entry equality and cancellation isolation; artifacts/scan-response-results.json"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL response benchmark: " + ex.GetType().Name); return 1; }
        finally
        {
            string prefix = Path.GetFullPath(artifacts) + Path.DirectorySeparatorChar + "scan-response-";
            if (!Path.GetFullPath(fixture).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup");
            if (uiThread?.IsAlive == true) Console.Error.WriteLine("Measurement timed out; owned fixture retained until process exits.");
            else if (Directory.Exists(fixture))
            {
                if (Directory.EnumerateFileSystemEntries(fixture, "*", SearchOption.AllDirectories).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) throw new InvalidOperationException("Redirected fixture");
                Directory.Delete(fixture, true);
            }
        }
    }

    private static double Median(IEnumerable<double> source)
    { var values = source.Order().ToArray(); return values.Length == 0 ? 0 : (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2; }
    private static double Percentile(List<double> source, double fraction)
    { var values = source.Order().ToArray(); return values.Length == 0 ? 0 : values[Math.Clamp((int)Math.Ceiling(fraction * values.Length) - 1, 0, values.Length - 1)]; }
    private static string Fingerprint(DiskEntry root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in root.Descendants().Prepend(root).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            hash.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(root.Path, entry.Path)}\t{entry.IsDirectory}\t{entry.Size}\t{entry.AllocatedSize}\t{entry.FileCount}\t{entry.FolderCount}\t{entry.Modified.Ticks}\t{entry.UnknownAllocationFiles}\t{entry.SharedAllocation}\t{entry.Incomplete}\n"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
