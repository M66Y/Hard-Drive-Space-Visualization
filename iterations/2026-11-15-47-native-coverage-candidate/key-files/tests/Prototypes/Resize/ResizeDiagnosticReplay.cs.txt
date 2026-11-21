using System.Diagnostics;

namespace DiskAtlas.ResizePrototype;

// A diagnostic stimulus, not a resize workaround. Both cadences request the
// exact same bounded Size sequence; the UI pump determines actual tick times.
internal sealed class ResizeDiagnosticReplay : IDisposable
{
    internal const int StepLimit = 12;
    private readonly Form owner;
    private readonly System.Windows.Forms.Timer timer;
    private readonly Func<ResizeDiagnosticTrace?> trace;
    private readonly Action completed;
    private readonly Size[] sizes;
    private readonly long[] requestedAt = new long[StepLimit];
    private int step;
    internal int Steps => step;
    internal int RequestedInterval { get; }
    internal bool Completed => step == sizes.Length;

    internal ResizeDiagnosticReplay(Form owner, int interval, Func<ResizeDiagnosticTrace?> trace, Action completed, Size? common = null)
    {
        if (interval is not (30 or 100)) throw new ArgumentOutOfRangeException(nameof(interval));
        this.owner = owner; this.trace = trace; this.completed = completed;
        RequestedInterval = interval;
        sizes = Build(common ?? owner.Size, owner.MinimumSize);
        timer = new() { Interval = interval };
        timer.Tick += (_, _) => Tick();
    }

    internal static Size[] Build(Size common, Size minimum)
    {
        if (common.Width <= 0 || common.Height <= 0 || minimum.Width <= 0 || minimum.Height <= 0 ||
            minimum.Width > common.Width || minimum.Height > common.Height)
            throw new ArgumentOutOfRangeException(nameof(common));
        var result = new Size[StepLimit];
        for (int i = 0; i < result.Length; i++)
        {
            // Put newly exposed right/bottom regions in the first few steps.
            // The earlier 160-step smooth sweep exhausted normal-form trace
            // capacity while it was still shrinking, before the useful edge.
            result[i] = i % 2 == 0 ? common : minimum;
        }
        return result;
    }

    internal void Start()
    {
        if (owner.IsDisposed || step != 0) return;
        trace()?.Mark("ReplayStarted");
        timer.Start();
    }

    private void Tick()
    {
        if (owner.IsDisposed) { timer.Stop(); return; }
        if (step >= sizes.Length) { timer.Stop(); return; }
        requestedAt[step] = Stopwatch.GetTimestamp();
        trace()?.Mark("ReplayStep");
        owner.Size = sizes[step++];
        if (step < sizes.Length) return;
        timer.Stop(); trace()?.Mark("ReplayFinished");
        // The callback can queue close after the final natural Paint. It must
        // never call Refresh/Update or block the observed UI thread.
        completed();
    }

    internal object Summary()
    {
        double[] gaps = requestedAt.Take(step).Zip(requestedAt.Skip(1).Take(Math.Max(0, step - 1)),
            (first, second) => Stopwatch.GetElapsedTime(first, second).TotalMilliseconds).Order().ToArray();
        return new { revision = 2, sequence_kind = "alternating-common-minimum", requested_interval_ms = RequestedInterval, requested_steps = step,
            completed = Completed, fixed_sequence_steps = sizes.Length,
            actual_gap_p50_ms = gaps.Length == 0 ? 0 : gaps[gaps.Length / 2],
            actual_gap_p95_ms = gaps.Length == 0 ? 0 : gaps[(int)Math.Floor((gaps.Length - 1) * .95)],
            size_sequence = sizes.Take(step).Select(size => new { width = size.Width, height = size.Height }).ToArray(),
            program_driven = true, native_mouse_drag = false };
    }

    public void Dispose() => timer.Dispose();
}
