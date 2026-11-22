using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DiskAtlas;

// Test apphost only. All result trees/fingerprints remain in memory; reports
// contain fixed phase names, time/resource measurements and booleans only.
internal static partial class NtfsPersonalTimingDriver
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int productUiInitialized;
    internal static bool ValidUiInvocation(string[] args) => args.Length == 1 && args[0] == UiArgument;
    internal static NtfsTimingFingerprint RunVisibleOne(string scope, char mode, CancellationToken cancellation, Action<string> observed,
        Func<string, IProgress<ScanProgress>, CancellationToken, LiveScanSession, ScanResult>? ordinary = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(TimeSpan.FromMinutes(10));
        ScanResult? result = null; ExceptionDispatchInfo? failure = null;
        object? sample = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Interlocked.Exchange(ref productUiInitialized, 1) == 0)
                {
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                    Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F));
                }
                var rendering = DesktopRendering.Resolve([], "AsukaLens.exe");
                var clock = new Stopwatch(); var timeline = new NtfsTimingTimeline(() => clock.Elapsed.TotalMilliseconds);
                object? helper = null;
                FastMeasurement? fast = null; double? coordinatorDone = null, firstPreview = null;
                void Stage(string value)
                {
                    bool changed = timeline.Mark(value); observed(value);
                    if (changed && timeline.DroppedTransitions == 0)
                        Console.WriteLine(JsonSerializer.Serialize(new { revision = "personal-ntfs-product-timing-v1", kind = "stage", mode = mode.ToString(), stage = value }));
                }
                string isolated = Path.Combine(Path.GetTempPath(), "AsukaLens-timing-unused-" + Guid.NewGuid().ToString("N"), "background.dat");
                using var form = new MainForm(null, false, isolated, dibCandidate: rendering.Dib,
                    compositionCandidate: rendering.Composition, showCandidateLabel: false)
                {
                    Text = "AsukaLens · 单次扫描计时", FastScanConfirmation = () => true,
                    ScanRunner = (path, progress, token, session) =>
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, token);
                        try
                        {
                            if (mode == 'F')
                            {
                                NtfsTimingResources? helperProbe = null;
                                try
                                {
                                fast = MeasureFast(path, linked.Token, (selected, _, cancel) => FastScanBridge.ScanForTiming(selected, progress, cancel,
                                    stage => Stage("Fast_" + stage), (point, process) =>
                                    {
                                        Stage("Bridge_" + point);
                                        if (point == FastScanTimingPoint.LaunchReturned && process != null)
                                        { helperProbe = new NtfsTimingResources(process); helperProbe.Start(); }
                                        if (point == FastScanTimingPoint.ResultDecoded && helperProbe != null)
                                        { helperProbe.Stop(); helper = helperProbe.Result; }
                                    }));
                                }
                                finally { helperProbe?.Dispose(); }
                                result = fast.Result;
                                if (result.Source == null || result.FastScanFallback != null || result.Checkpoint != null)
                                    throw new InvalidDataException("TimingFastContract");
                            }
                            else
                            {
                                Stage("Ordinary_Start");
                                var reporter = new TimingProgress(progress, value => Stage("Ordinary_" + value.Phase));
                                result = ordinary != null ? ordinary(path, reporter, linked.Token, session) :
                                    new ScanCoordinator().Scan(path, null, new(ScanMode.Automatic), reporter, linked.Token, session, forceFull: true);
                                if (result.Source != null || result.Statistics is not { Mode: ScanMode.Automatic, Method: RefreshMethod.FullScan })
                                    throw new InvalidDataException("TimingOrdinaryContract");
                            }
                            coordinatorDone = clock.Elapsed.TotalMilliseconds; Stage("UiHandoff"); return result;
                        }
                        catch (Exception error)
                        {
                            failure = ExceptionDispatchInfo.Capture(error);
                            // Suppress only this owned probe's error dialog. Preserve and
                            // rethrow the actual category/cleanup result after it closes.
                            throw new OperationCanceledException(linked.Token);
                        }
                    }
                };
                form.NtfsFastEnabled = mode == 'F';
                bool done = false;
                form.FormClosing += (_, args) =>
                {
                    if (done) return;
                    args.Cancel = true; limit.Cancel(); form.Text = "AsukaLens · 取消计时，等待本次资源清理";
                };
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        // Initialization and image decoding precede the scan click interval.
                        using var resources = new NtfsTimingResources();
                        using var preview = new System.Windows.Forms.Timer { Interval = 100 };
                        preview.Tick += (_, _) =>
                        {
                            if (firstPreview == null && Field<bool>(form, "showingPreview") && Field<DataGridView>(form, "grid").RowCount > 0)
                                firstPreview = clock.Elapsed.TotalMilliseconds;
                        };
                        typeof(MainForm).GetField("forceFullNext", Hidden)!.SetValue(form, true);
                        timeline.Mark("UiStart"); clock.Start(); resources.Start(); preview.Start();
                        await (Task)typeof(MainForm).GetMethod("ScanFolder", Hidden)!.Invoke(form, [scope])!;
                        failure?.Throw(); limit.Token.ThrowIfCancellationRequested();
                        if (result == null) throw new InvalidDataException("TimingNoResult");
                        while (Field<CancellationTokenSource?>(form, "viewCancellation") != null || !Field<DataGridView>(form, "grid").Enabled)
                        { limit.Token.ThrowIfCancellationRequested(); await Task.Delay(10); }
                        if (Field<TreemapControl>(form, "map").LoadingView) throw new InvalidDataException("TimingViewNotReady");
                        // Publish the existing invalid regions; no extra query or scanner.
                        // This includes synchronous final paints, not DWM presentation time.
                        form.Update(); Field<TreemapControl>(form, "map").Update(); Field<DataGridView>(form, "grid").Update();
                        preview.Stop(); clock.Stop(); resources.Stop(); timeline.Finish();
                        sample = new
                        {
                            revision = "personal-ntfs-product-timing-v1", kind = "sample", mode = mode.ToString(),
                            total_ms = clock.Elapsed.TotalMilliseconds, coordinator_available_ms = coordinatorDone,
                            ui_handoff_query_and_final_paint_ms = coordinatorDone == null ? (double?)null : clock.Elapsed.TotalMilliseconds - coordinatorDone,
                            bridge_ms = fast?.Bridge.TotalMilliseconds, publication_validation_ms = fast?.PublicationValidation.TotalMilliseconds,
                            first_observed_preview_ms = firstPreview, intervals = timeline.Intervals, transitions = timeline.Transitions,
                            dropped_transition_details = timeline.DroppedTransitions,
                            ui_resources = resources.Result, helper_resources_at_result = helper,
                            helper_resource_scope = "launch returned to decoded result; excludes earlier initialization and later shutdown",
                            launch_wait_includes_consent_and_startup = mode == 'F', human_wait_separately_measurable = false,
                            phases_observed_at_client = true, cleanup_transfer_decode_combined = mode == 'F',
                            ui_drawing_included = true, synchronous_final_paint_included = true, dwm_presentation_measured = false,
                            initialization_excluded = true, audit_added = false, cleanup_confirmed = true, fallback = false,
                            physical_io_measured = false, resource_sample_interval_ms = 250, gc_between_runs = true,
                            compact_diagnostics_retained_between_runs = true
                        };
                    }
                    catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
                    finally { done = true; form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(13))) throw new FastScanUnavailableException(FastScanWireError.CleanupUnconfirmed, false);
        failure?.Throw();
        if (result == null || sample == null) throw new InvalidDataException("TimingNoResult");
        Console.WriteLine(JsonSerializer.Serialize(sample));
        observed("FingerprintOutsideTiming");
        return NtfsTimingFingerprint.Capture(result, cancellation);
    }
    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, Hidden)!.GetValue(form)!;
    private sealed class TimingProgress(IProgress<ScanProgress> destination, Action<ScanProgress> observe) : IProgress<ScanProgress>
    {
        private ScanPhase? previous;
        public void Report(ScanProgress value)
        {
            lock (this) { if (previous != value.Phase) { previous = value.Phase; observe(value); } }
            destination.Report(value);
        }
    }
}

internal sealed class NtfsTimingTimeline(Func<double> elapsed)
{
    private readonly object gate = new();
    private readonly Dictionary<string, double> intervals = new(StringComparer.Ordinal);
    private readonly List<object> transitions = [];
    private string? current; private double started;
    internal long DroppedTransitions { get; private set; }
    internal IReadOnlyDictionary<string, double> Intervals => intervals;
    internal IReadOnlyList<object> Transitions => transitions;
    internal bool Mark(string phase)
    {
        if (!Allowed(phase)) throw new InvalidDataException("TimingPhase");
        lock (gate)
        {
            if (current == phase) return false;
            double now = elapsed(); Accumulate(now); current = phase; started = now;
            if (transitions.Count < 128) transitions.Add(new { phase, at_ms = now });
            else DroppedTransitions++;
            return true;
        }
    }
    internal void Finish() { lock (gate) { Accumulate(elapsed()); current = null; } }
    private void Accumulate(double now)
    {
        if (!double.IsFinite(now) || now < started) throw new InvalidDataException("TimingClock");
        if (current != null) intervals[current] = intervals.GetValueOrDefault(current) + now - started;
    }
    internal static bool Allowed(string phase) => phase is "UiStart" or "UiHandoff" or "Ordinary_Start" ||
        Enum.GetNames<FastScanWireStage>().Any(x => phase == "Fast_" + x) ||
        Enum.GetNames<FastScanTimingPoint>().Any(x => phase == "Bridge_" + x) ||
        Enum.GetNames<ScanPhase>().Any(x => phase == "Ordinary_" + x);
}

internal sealed class NtfsTimingResources : IDisposable
{
    internal sealed record Snapshot(double? CpuMs, long? WorkingSetBytes, ulong? ReadBytes, ulong? WriteBytes);
    private readonly Process process;
    private readonly bool ownsProcess;
    private readonly CancellationTokenSource stop = new();
    private Task? sampler; private Snapshot? start, end; private long sampledPeak; private int samples, stopped;
    internal NtfsTimingResources(Process? observed = null) { process = observed ?? Process.GetCurrentProcess(); ownsProcess = observed == null; }
    internal object Result => new { cpu_ms = Delta(start?.CpuMs, end?.CpuMs), sampled_peak_working_set_bytes = samples == 0 ? (long?)null : sampledPeak,
        process_read_bytes = Delta(start?.ReadBytes, end?.ReadBytes), process_write_bytes = Delta(start?.WriteBytes, end?.WriteBytes),
        start_working_set_bytes = start?.WorkingSetBytes, end_working_set_bytes = end?.WorkingSetBytes, samples };
    private static double? Delta(double? a, double? b) => a.HasValue && b >= a ? b - a : null;
    private static ulong? Delta(ulong? a, ulong? b) => a.HasValue && b >= a ? b - a : null;
    internal void Start()
    {
        start = Read(process); Sample();
        sampler = Task.Run(async () =>
        {
            try { using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250)); while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false)) Sample(); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
    }
    private void Sample() { var value = Read(process); if (value.WorkingSetBytes is { } bytes) { sampledPeak = Math.Max(sampledPeak, bytes); samples++; } }
    internal void Stop() { if (Interlocked.Exchange(ref stopped, 1) != 0) return; stop.Cancel(); sampler?.GetAwaiter().GetResult(); Sample(); end = Read(process); }
    internal static Snapshot Read(Process process)
    {
        double? cpu = null; long? working = null; ulong? read = null, written = null;
        try { process.Refresh(); cpu = process.TotalProcessorTime.TotalMilliseconds; working = process.WorkingSet64; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or ObjectDisposedException) { }
        try { if (GetProcessIoCounters(process.Handle, out var io)) { read = io.ReadTransferCount; written = io.WriteTransferCount; } }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or ObjectDisposedException) { }
        return new(cpu, working, read, written);
    }
    public void Dispose() { Stop(); stop.Dispose(); if (ownsProcess) process.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
