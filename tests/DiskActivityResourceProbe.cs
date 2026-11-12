using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DiskAtlas;

// Controlled local resource observation only. No scan, synthetic data, UI,
// diagnostic logging, or output file is created. Loading the executable and its
// runtime is naturally part of starting the four independent child processes.
internal static class DiskActivityResourceProbe
{
    private sealed record Result(int Schema, string Mode, string Status, string Code,
        double? WallMilliseconds, double? CpuMilliseconds, double? CpuSingleCorePercent,
        long? LifetimePeakWorkingSetBytes, long? WindowEndWorkingSetBytes,
        int WindowSamples, int WindowValidSamples);

    private const string SkippedMarker = "DISK_ACTIVITY_RESOURCE_SKIPPED", FailedMarker = "DISK_ACTIVITY_RESOURCE_FAILED";
    private static readonly string[] Fields = ["Schema", "Mode", "Status", "Code", "WallMilliseconds", "CpuMilliseconds",
        "CpuSingleCorePercent", "LifetimePeakWorkingSetBytes", "WindowEndWorkingSetBytes", "WindowSamples", "WindowValidSamples"];

    internal static int Run()
    {
        int failure = 0;
        string[] modes = ["off", "on", "on", "off"];
        for (int i = 0; i < modes.Length; i++)
        {
            Result result = RunChild(modes[i]);
            // Rebuild the printable object: never relay raw child output or errors.
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result.Schema, Round = i + 1, result.Mode, result.Status, result.Code,
                CpuBasis = "single-core-percent-not-divided-by-logical-processors",
                PeakScope = "child-process-lifetime-through-report-including-runtime-and-initialization",
                result.WallMilliseconds, result.CpuMilliseconds, result.CpuSingleCorePercent,
                result.LifetimePeakWorkingSetBytes, result.WindowEndWorkingSetBytes,
                result.WindowSamples, result.WindowValidSamples
            }));
            if (result.Status == "failed") failure = 1;
            else if (result.Status == "skipped" && failure == 0) failure = 2;
        }
        return failure;
    }

    internal static int Child(string mode)
    {
        Result result;
        DiskActivityMonitor? monitor = null;
        try
        {
            if (mode is not ("off" or "on"))
                return Emit(Failure("invalid", "InvalidMode"));
            string path = Environment.CurrentDirectory;
            var target = DiskActivityResolver.Resolve(path);
            if (target == null) return Emit(Empty(mode, "skipped", "UnsupportedTarget"));

            object sampleGate = new();
            bool measuring = false;
            int samples = 0, valid = 0;
            if (mode == "on")
            {
                // Use the production resolver/discovery/source and its real 1 Hz
                // schedule. The callback only counts observations; there is no UI.
                monitor = new DiskActivityMonitor(update =>
                {
                    if (update.Sample is not { } sample || sample.DiskNumber != target.DiskNumber) return;
                    lock (sampleGate)
                    {
                        if (!measuring) return;
                        samples++;
                        if (sample.Status == DiskActivityStatus.Available &&
                            (sample.ActivePercent.HasValue || sample.ReadBytesPerSecond.HasValue ||
                             sample.WriteBytesPerSecond.HasValue || sample.ResponseMilliseconds.HasValue)) valid++;
                    }
                });
                monitor.Update(path, null, true);
            }
            Pause(TimeSpan.FromMilliseconds(1500));
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            TimeSpan before = process.TotalProcessorTime;
            long started = Stopwatch.GetTimestamp();
            lock (sampleGate) measuring = true;
            Pause(TimeSpan.FromSeconds(5));
            lock (sampleGate) measuring = false;
            long finished = Stopwatch.GetTimestamp();
            process.Refresh();
            double cpu = (process.TotalProcessorTime - before).TotalMilliseconds;
            double wall = (finished - started) * 1000.0 / Stopwatch.Frequency;
            long windowWorkingSet = process.WorkingSet64;
            if (monitor != null)
            {
                var stopping = monitor;
                monitor = null;
                stopping.Dispose();
                if (!stopping.Completion.Wait(TimeSpan.FromSeconds(2)))
                    return Emit(Failure(mode, "ShutdownTimeout"));
            }
            process.Refresh();
            // This OS-maintained peak is since child startup through this query,
            // including initialization and shutdown; it is NOT a five-second peak.
            long lifetimePeak = process.PeakWorkingSet64;
            bool unavailable = mode == "on" && valid == 0;
            result = new(1, mode, unavailable ? "skipped" : "passed", unavailable ? "CounterUnavailable" : "None",
                wall, cpu, cpu / wall * 100.0, lifetimePeak, windowWorkingSet, samples, valid);
            if (!Validate(result, mode)) result = Failure(mode, "ChildFailed");
        }
        catch
        {
            // Native exception messages, stack traces, and discovery details may
            // contain private information. Only fixed codes leave this process.
            result = Failure(mode is "on" or "off" ? mode : "invalid", "ChildFailed");
        }
        finally
        {
            if (monitor != null)
            {
                try { monitor.Dispose(); monitor.Completion.Wait(TimeSpan.FromSeconds(2)); }
                catch { }
            }
        }
        return Emit(result);
    }

    private static void Pause(TimeSpan duration)
    {
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < duration) Thread.Sleep(50);
    }

    private static int Emit(Result result)
    {
        Console.WriteLine(JsonSerializer.Serialize(result));
        if (result.Status == "skipped") { Console.Error.WriteLine(SkippedMarker); return 2; }
        if (result.Status == "failed") { Console.Error.WriteLine(FailedMarker); return 1; }
        return 0;
    }

    private static Result Empty(string mode, string status, string code) => new(1, mode, status, code, null, null, null, null, null, 0, 0);
    private static Result Failure(string mode, string code) => Empty(mode, "failed", code);

    private static Result RunChild(string mode)
    {
        Process? child = null;
        try
        {
            string assembly = Assembly.GetExecutingAssembly().Location;
            string? currentHost = Environment.ProcessPath;
            string host = string.Equals(Path.GetFileNameWithoutExtension(currentHost), "dotnet", StringComparison.OrdinalIgnoreCase)
                ? currentHost! : "dotnet";
            var start = new ProcessStartInfo(host)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Environment.CurrentDirectory
            };
            start.ArgumentList.Add(assembly);
            start.ArgumentList.Add("--disk-activity-resource-child");
            start.ArgumentList.Add(mode);
            child = Process.Start(start);
            if (child == null) return Failure(mode, "ChildStartFailed");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var output = ReadBounded(child.StandardOutput.BaseStream, 16384, deadline.Token);
            var error = ReadBounded(child.StandardError.BaseStream, 256, deadline.Token);
            try
            {
                Task.WhenAll(output, error, child.WaitForExitAsync(deadline.Token)).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                StopChild(child);
                return Failure(mode, "ChildTimeout");
            }
            catch
            {
                StopChild(child);
                return Failure(mode, "ChildOutputInvalid");
            }
            try
            {
                var encoding = new UTF8Encoding(false, true);
                using var document = JsonDocument.Parse(encoding.GetString(output.Result));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Failure(mode, "ChildOutputInvalid");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject())
                    if (!names.Add(property.Name) || !Fields.Contains(property.Name, StringComparer.Ordinal))
                        return Failure(mode, "ChildOutputInvalid");
                if (names.Count != Fields.Length) return Failure(mode, "ChildOutputInvalid");
                double? Number(string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : root.GetProperty(name).GetDouble();
                long? Integer(string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : root.GetProperty(name).GetInt64();
                var result = new Result(root.GetProperty("Schema").GetInt32(),
                    root.GetProperty("Mode").GetString() ?? throw new FormatException(),
                    root.GetProperty("Status").GetString() ?? throw new FormatException(),
                    root.GetProperty("Code").GetString() ?? throw new FormatException(),
                    Number("WallMilliseconds"), Number("CpuMilliseconds"), Number("CpuSingleCorePercent"),
                    Integer("LifetimePeakWorkingSetBytes"), Integer("WindowEndWorkingSetBytes"),
                    root.GetProperty("WindowSamples").GetInt32(), root.GetProperty("WindowValidSamples").GetInt32());
                if (!Validate(result, mode)) return Failure(mode, "ChildOutputInvalid");
                string stderr = encoding.GetString(error.Result).Trim();
                int expectedExit = result.Status == "passed" ? 0 : result.Status == "skipped" ? 2 : 1;
                string expectedError = result.Status == "passed" ? "" : result.Status == "skipped" ? SkippedMarker : FailedMarker;
                return child.ExitCode == expectedExit && stderr == expectedError ? result : Failure(mode, "ChildOutputInvalid");
            }
            catch { return Failure(mode, "ChildOutputInvalid"); }
        }
        catch { if (child != null) StopChild(child); return Failure(mode, "ChildStartFailed"); }
        finally { child?.Dispose(); }
    }

    private static async Task<byte[]> ReadBounded(Stream stream, int maximum, CancellationToken token)
    {
        using var data = new MemoryStream();
        byte[] buffer = new byte[512];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return data.ToArray();
            if (data.Length + count > maximum) throw new InvalidDataException("ResourceChildOutputLimit");
            data.Write(buffer, 0, count);
        }
    }

    private static void StopChild(Process child)
    {
        try { if (!child.HasExited) child.Kill(true); } catch { }
        try { child.WaitForExit(2000); } catch { }
    }

    private static bool Validate(Result result, string mode)
    {
        if (result.Schema != 1 || result.Mode != mode || result.Status is not ("passed" or "skipped" or "failed") ||
            result.WindowSamples is < 0 or > 64 || result.WindowValidSamples < 0 || result.WindowValidSamples > result.WindowSamples ||
            (mode == "off" && (result.WindowSamples != 0 || result.WindowValidSamples != 0))) return false;
        if (result.Status == "failed")
            return result.Code is "ChildFailed" or "ShutdownTimeout" or "InvalidMode" &&
                result.WallMilliseconds == null && result.CpuMilliseconds == null && result.CpuSingleCorePercent == null &&
                result.LifetimePeakWorkingSetBytes == null && result.WindowEndWorkingSetBytes == null && result.WindowSamples == 0;
        if (result.Status == "skipped" && result.Code == "UnsupportedTarget")
            return result.WallMilliseconds == null && result.CpuMilliseconds == null && result.CpuSingleCorePercent == null &&
                result.LifetimePeakWorkingSetBytes == null && result.WindowEndWorkingSetBytes == null && result.WindowSamples == 0;
        if (result.Status == "passed" ? result.Code != "None" : result.Code != "CounterUnavailable" || mode != "on" || result.WindowValidSamples != 0)
            return false;
        static bool Finite(double? number) => number.HasValue && double.IsFinite(number.Value) && number.Value >= 0;
        if (!Finite(result.WallMilliseconds) || result.WallMilliseconds is < 5000 or > 12000 || !Finite(result.CpuMilliseconds) ||
            !Finite(result.CpuSingleCorePercent) || result.LifetimePeakWorkingSetBytes is not > 0 || result.WindowEndWorkingSetBytes is not > 0 ||
            result.LifetimePeakWorkingSetBytes < result.WindowEndWorkingSetBytes ||
            (result.Status == "passed" && mode == "on" && result.WindowValidSamples == 0)) return false;
        double expected = result.CpuMilliseconds!.Value / result.WallMilliseconds!.Value * 100.0;
        return Math.Abs(result.CpuSingleCorePercent!.Value - expected) <= 0.000001 * Math.Max(1, expected);
    }
}
