using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DiskAtlas;

internal static class DiskActivityScanProbe
{
    private const string Prefix = "AsukaLens-monitor-scan-", Marker = ".asuka-monitor-owner";
    private const string FixtureVariable = "ASUKALENS_MONITOR_SCAN_FIXTURE", OwnerVariable = "ASUKALENS_MONITOR_SCAN_OWNER";
    private const int DirectoryCount = 60, FilesPerDirectory = 500;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal sealed record Sample(string Mode, long Files, long Directories, long LogicalBytes, long AllocatedBytes,
        long UnknownAllocation, bool Incomplete, long Skipped, int AllocationIssues, double WallMs, double CpuMs,
        long PeakWorkingSetBytes, string PeakScope, int MonitorAvailableSamples, int MonitorAvailableDuringScan);

    // Four fresh processes share the same owned fixture: off/on/on/off. The
    // ownership preflight enumerates it before timing, so this is not a cold-cache test.
    internal static int Run()
    {
        string root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), Prefix + Guid.NewGuid().ToString("N"));
        string owner = Guid.NewGuid().ToString("N");
        var children = new List<Process>();
        var samples = new List<Sample>();
        int result = 1;
        string? error = null;
        bool statisticsMatch = false, monitoringObserved = false, owned = false;
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, Marker), MarkerText(root, owner), new UTF8Encoding(false));
            owned = true;
            long expectedLogical = new FileInfo(Path.Combine(root, Marker)).Length;
            for (int d = 0; d < DirectoryCount; d++)
            {
                string directory = Path.Combine(root, $"folder-{d:00}");
                Directory.CreateDirectory(directory);
                for (int f = 0; f < FilesPerDirectory; f++)
                {
                    byte[] bytes = new byte[f % 2 == 0 ? 17 : 1024];
                    File.WriteAllBytes(Path.Combine(directory, $"file-{f:000}.bin"), bytes);
                    expectedLogical += bytes.Length;
                }
            }
            ValidateOwned(root, owner, complete: true);
            foreach (string mode in new[] { "off", "on", "on", "off" })
                samples.Add(RunChild(root, owner, mode, children));
            var baseline = samples[0];
            statisticsMatch = samples.All(sample => sample.Files == DirectoryCount * FilesPerDirectory + 1 && sample.Directories == DirectoryCount &&
                sample.LogicalBytes == expectedLogical && sample.AllocatedBytes == baseline.AllocatedBytes &&
                sample.UnknownAllocation == 0 && !sample.Incomplete && sample.Skipped == 0 && sample.AllocationIssues == 0);
            monitoringObserved = samples.Where(sample => sample.Mode == "on").All(sample => sample.MonitorAvailableSamples > 0) &&
                samples.Where(sample => sample.Mode == "off").All(sample => sample.MonitorAvailableSamples == 0);
            if (!statisticsMatch) error = "MONITOR_SCAN_PROBE_STATISTICS_MISMATCH";
            else if (!monitoringObserved) error = "MONITOR_SCAN_PROBE_MONITORING_NOT_OBSERVED";
            else result = 0;
        }
        catch (OperationCanceledException) { error = "MONITOR_SCAN_PROBE_CHILD_TIMEOUT"; }
        catch { error = "MONITOR_SCAN_PROBE_FAILED"; }
        finally
        {
            bool stopped = true;
            foreach (var child in children)
            {
                try
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    if (!child.WaitForExit(5000)) stopped = false;
                }
                catch { stopped = false; }
            }
            if (owned)
            {
                try
                {
                    if (!stopped) throw new InvalidOperationException();
                    ValidateOwned(root, owner, complete: false);
                    Directory.Delete(root, true);
                }
                catch { result = 1; error = "MONITOR_SCAN_PROBE_CLEANUP_REFUSED"; }
            }
            foreach (var child in children) child.Dispose();
        }
        // Preserve every completed aggregate sample, including inconclusive/failing runs.
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Kind = "disk-activity-ui-scan-probe", Order = "off/on/on/off", FixtureFiles = DirectoryCount * FilesPerDirectory,
            OwnershipMarkerFiles = 1, FixtureDirectories = DirectoryCount, PrewaitMilliseconds = 1600,
            TimingScope = "ScanFolder plus final UI query and refresh; excludes preparation and shutdown",
            PeakScope = "whole child process lifetime through scan report; not scan-phase peak",
            CacheScope = "not controlled; own-tree ownership validation precedes each scan",
            StatisticsMatch = statisticsMatch, MonitoringObserved = monitoringObserved, Success = result == 0,
            MonitoringDuringEveryOnScanObserved = samples.Where(sample => sample.Mode == "on").Any() &&
                samples.Where(sample => sample.Mode == "on").All(sample => sample.MonitorAvailableDuringScan > 0),
            Samples = samples
        }));
        if (error != null) Console.Error.WriteLine(error);
        return result;
    }

    private static Sample RunChild(string root, string owner, string mode, List<Process> children)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(DiskActivityScanProbe).Assembly.Location);
        start.ArgumentList.Add("--disk-activity-scan-child");
        start.ArgumentList.Add(mode);
        start.Environment[FixtureVariable] = root;
        start.Environment[OwnerVariable] = owner;
        start.Environment["ASUKALENS_DATA_DIR"] = Path.Combine(root, ".isolated-state");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new InvalidOperationException(); }
        catch { process.Dispose(); throw; }
        children.Add(process);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = ReadBounded(process.StandardOutput.BaseStream, 16384, timeout.Token);
        var errors = ReadBounded(process.StandardError.BaseStream, 4096, timeout.Token);
        Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, errors).GetAwaiter().GetResult();
        if (process.ExitCode != 0 || errors.Result.Length != 0) throw new InvalidOperationException();
        var sample = JsonSerializer.Deserialize<Sample>(output.Result) ?? throw new InvalidOperationException();
        if (sample.Mode != mode || !double.IsFinite(sample.WallMs) || sample.WallMs <= 0 ||
            !double.IsFinite(sample.CpuMs) || sample.CpuMs < 0 || sample.PeakWorkingSetBytes <= 0 ||
            sample.Files < 0 || sample.Directories < 0 || sample.LogicalBytes < 0 || sample.AllocatedBytes < 0 ||
            sample.UnknownAllocation < 0 || sample.Skipped < 0 || sample.AllocationIssues < 0 ||
            sample.MonitorAvailableSamples < 0 || sample.MonitorAvailableDuringScan < 0 ||
            sample.PeakScope != "process-start-through-scan-report") throw new InvalidOperationException();
        return sample;
    }

    private static async Task<byte[]> ReadBounded(Stream stream, int maximum, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[1024];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, token);
            if (count == 0) break;
            if (bytes.Length + count > maximum) throw new InvalidOperationException();
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    // Called before the ordinary test runner's output/initialization by the root entry point.
    internal static int Child(string mode)
    {
        if (mode is not ("off" or "on")) { Console.Error.WriteLine("MONITOR_SCAN_CHILD_MODE"); return 1; }
        Sample? sample = null;
        bool failed = false;
        try
        {
            string root = Environment.GetEnvironmentVariable(FixtureVariable) ?? throw new InvalidOperationException();
            string owner = Environment.GetEnvironmentVariable(OwnerVariable) ?? throw new InvalidOperationException();
            ValidateOwned(root, owner, complete: true);
            var thread = new Thread(() =>
            {
                try
                {
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using var form = new MainForm(null, loadSavedBackground: false,
                        backgroundSettingsPath: Path.Combine(root, ".unused-background.dat"), diagnostics: null)
                    { Text = "AsukaLens - synthetic monitoring scan probe" };
                    form.Shown += async (_, _) =>
                    {
                        try
                        {
                            var panel = Field<DiskActivityPanel>(form, "activityPanel");
                            typeof(MainForm).GetField("activityPath", Private)!.SetValue(form, root);
                            if (mode == "on") Field<Button>(panel, "toggle").PerformClick();
                            await Task.Delay(1600);
                            if (panel.Expanded != (mode == "on")) throw new InvalidOperationException();
                            int before = AvailableSamples(panel);
                            using var process = Process.GetCurrentProcess();
                            var cpu = process.TotalProcessorTime;
                            var clock = Stopwatch.StartNew();
                            var scanTask = (Task?)typeof(MainForm).GetMethod("ScanFolder", Private)!.Invoke(form, [root]);
                            if (scanTask == null) throw new InvalidOperationException();
                            await scanTask;
                            while (Field<CancellationTokenSource?>(form, "viewCancellation") != null ||
                                !Field<DataGridView>(form, "grid").Enabled) await Task.Delay(10);
                            form.Refresh();
                            clock.Stop();
                            double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                            var scan = Field<ScanResult?>(form, "scan") ?? throw new InvalidOperationException();
                            if (Field<DataGridView>(form, "grid").RowCount == 0) throw new InvalidOperationException();
                            int after = AvailableSamples(panel);
                            process.Refresh();
                            sample = new(mode, scan.Root.FileCount, scan.Root.FolderCount, scan.Root.Size, scan.Root.AllocatedSize,
                                scan.Root.UnknownAllocationFiles, scan.Root.Incomplete, scan.Skipped, scan.AllocationIssues.Count,
                                clock.Elapsed.TotalMilliseconds, cpuMs, process.PeakWorkingSet64,
                                "process-start-through-scan-report", after, Math.Max(0, after - before));
                        }
                        catch { failed = true; }
                        finally { form.Close(); }
                    };
                    Application.Run(form);
                }
                catch { failed = true; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(26))) { Console.Error.WriteLine("MONITOR_SCAN_CHILD_TIMEOUT"); return 1; }
        }
        catch { failed = true; }
        if (failed || sample == null) { Console.Error.WriteLine("MONITOR_SCAN_CHILD_FAILED"); return 1; }
        Console.WriteLine(JsonSerializer.Serialize(sample));
        return 0;
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static int AvailableSamples(DiskActivityPanel panel) => Field<DiskActivitySample?[]>(panel, "history")
        .Count(sample => sample?.Status == DiskActivityStatus.Available);
    private static string MarkerText(string root, string owner) => "ASUKALENS_MONITOR_SCAN_V1\n" + Path.GetFileName(root)[Prefix.Length..] + "\n" + owner;

    private static void ValidateOwned(string root, string owner, bool complete)
    {
        string resolved = Path.GetFullPath(root);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string name = Path.GetFileName(resolved);
        if (!Path.IsPathFullyQualified(root) || !string.Equals(resolved, root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(resolved), temp, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name.AsSpan(Prefix.Length), "N", out _) ||
            !Guid.TryParseExact(owner, "N", out _) || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException();
        string marker = Path.Combine(resolved, Marker);
        if ((File.GetAttributes(marker) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
            new FileInfo(marker).Length > 256 || File.ReadAllText(marker) != MarkerText(resolved, owner))
            throw new InvalidOperationException();
        var expectedDirectories = Enumerable.Range(0, DirectoryCount).Select(d => $"folder-{d:00}").ToHashSet(StringComparer.Ordinal);
        int directoryCount = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(resolved, "*", SearchOption.TopDirectoryOnly))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
            if (Path.GetFileName(entry) == Marker && (attributes & FileAttributes.Directory) == 0) continue;
            if ((attributes & FileAttributes.Directory) == 0 || !expectedDirectories.Remove(Path.GetFileName(entry))) throw new InvalidOperationException();
            directoryCount++;
            var expectedFiles = Enumerable.Range(0, FilesPerDirectory).Select(f => $"file-{f:000}.bin").ToHashSet(StringComparer.Ordinal);
            int fileCount = 0;
            foreach (string file in Directory.EnumerateFileSystemEntries(entry, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
                    !expectedFiles.Remove(Path.GetFileName(file))) throw new InvalidOperationException();
                fileCount++;
            }
            if (complete && fileCount != FilesPerDirectory) throw new InvalidOperationException();
        }
        if (complete && directoryCount != DirectoryCount) throw new InvalidOperationException();
    }
}
