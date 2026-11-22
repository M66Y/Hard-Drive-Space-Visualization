using System.Reflection;

namespace DiskAtlas;

// Only pointer tracking is owned here. The same ScrollBar and its Scroll event
// continue to own ranges, keyboard/wheel input, accessibility and grid updates.
internal sealed class ScrollbarPointerTracking : IDisposable
{
    internal enum Part { None, Thumb, SmallDecrement, SmallIncrement, LargeDecrement, LargeIncrement }
    private readonly WallpaperGrid owner;
    private readonly ScrollBar bar;
    private readonly Action repaint;
    private readonly Func<bool> fullDrag;
    private readonly System.Windows.Forms.Timer repeat = new() { Interval = 400 };
    private static readonly Action<ScrollBar, ScrollEventArgs>? raise = CreateRaise();
    private Part part;
    private int offset, pointer, preview;
    private bool active, live, disposed;
    internal bool Notifying { get; private set; }
    internal bool Active => active;
    internal bool RepeatRunning => repeat.Enabled;

    internal ScrollbarPointerTracking(WallpaperGrid owner, ScrollBar bar, Action repaint, Func<bool>? fullDrag = null)
    {
        this.owner = owner; this.bar = bar; this.repaint = repaint;
        this.fullDrag = fullDrag ?? (() => SystemInformation.DragFullWindows);
        repeat.Tick += (_, _) =>
        {
            repeat.Interval = 60;
            if (!Available || !active || !bar.Capture) { Cancel(false); return; }
            Step(); repaint();
        };
    }
    private static Action<ScrollBar, ScrollEventArgs>? CreateRaise()
    {
        try { return typeof(ScrollBar).GetMethod("OnScroll", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(ScrollEventArgs)], null)?.CreateDelegate<Action<ScrollBar, ScrollEventArgs>>(); }
        catch (Exception e) when (e is ArgumentException or MemberAccessException) { return null; }
    }
    private bool Vertical => bar is VScrollBar;
    private bool Reverse => !Vertical && bar.RightToLeft == RightToLeft.Yes;
    private bool Available => !disposed && !owner.IsDisposed && !bar.IsDisposed && bar.Visible && bar.Enabled && owner.Visible;
    internal static int Maximum(int minimum, int maximum, int page) =>
        (int)Math.Max(minimum, (long)maximum - Math.Max(0L, (long)page - 1));
    internal static int Position(int coordinate, int grabOffset, int start, int travel, int minimum, int maximum, bool reverse)
    {
        if (travel <= 0 || maximum <= minimum) return minimum;
        double fraction = Math.Clamp(((double)coordinate - grabOffset - start) / travel, 0, 1);
        if (reverse) fraction = 1 - fraction;
        return (int)Math.Clamp(minimum + Math.Round(((long)maximum - minimum) * fraction, MidpointRounding.AwayFromZero), minimum, maximum);
    }
    internal static int ThumbStart(int value, int start, int travel, int minimum, int maximum, bool reverse)
    {
        if (maximum <= minimum || travel <= 0) return start;
        double fraction = Math.Clamp(((double)value - minimum) / ((long)maximum - minimum), 0, 1);
        return start + (int)Math.Round(travel * (reverse ? 1 - fraction : fraction), MidpointRounding.AwayFromZero);
    }
    private int AxisStart(Rectangle rect) => Vertical ? rect.Top : rect.Left;
    private int AxisLength(Rectangle rect) => Vertical ? rect.Height : rect.Width;
    internal GridScrollbarSkin.Geometry Geometry(GridScrollbarSkin.Geometry native)
    {
        if (!active || part != Part.Thumb) return native;
        int start = AxisStart(native.StartArrow) + AxisLength(native.StartArrow);
        int travel = Math.Max(0, AxisStart(native.EndArrow) - start - AxisLength(native.Thumb));
        int at = ThumbStart(preview, start, travel, bar.Minimum, Maximum(bar.Minimum, bar.Maximum, bar.LargeChange), Reverse);
        var thumb = native.Thumb;
        if (Vertical) thumb.Y = at; else thumb.X = at;
        return native with { Thumb = thumb };
    }
    internal bool Handle(ref Message message)
    {
        if (disposed || raise == null) return false;
        int msg = message.Msg;
        if (msg == 0x001F || msg == 0x0215 || msg == 0x0100 && message.WParam.ToInt64() == 27)
        {
            if (!active) return false;
            Cancel(true); message.Result = IntPtr.Zero; return true;
        }
        if (msg is not (0x0200 or 0x0201 or 0x0202 or 0x0203)) return false;
        if (!Available)
        {
            if (active) Cancel(false);
            // Posted/synthetic clicks must not enter native tracking on a
            // disabled or hidden child either; normal hit testing skips it.
            return msg is 0x0201 or 0x0203;
        }
        int x = (short)(message.LParam.ToInt64() & 0xffff), y = (short)((message.LParam.ToInt64() >> 16) & 0xffff);
        pointer = Vertical ? y : x;
        try
        {
            if (msg is 0x0201 or 0x0203)
            {
                if (!GridScrollbarSkin.TryGetGeometry(bar, out var native)) return false;
                var point = new Point(x, y);
                part = Hit(native, point);
                if (part == Part.None) return true;
                active = true; live = fullDrag(); preview = bar.Value;
                offset = pointer - AxisStart(native.Thumb);
                bar.Capture = true;
                if (!bar.Capture) { Cancel(false); return false; }
                if (part != Part.Thumb) { Step(); if (active) { repeat.Interval = 400; repeat.Start(); } }
            }
            else if (!active) return false;
            else if (msg == 0x0200 && part == Part.Thumb)
            {
                if (!GridScrollbarSkin.TryGetGeometry(bar, out var native)) { Cancel(true); return true; }
                int start = AxisStart(native.StartArrow) + AxisLength(native.StartArrow);
                int travel = AxisStart(native.EndArrow) - start - AxisLength(native.Thumb);
                int target = travel <= 0 ? bar.Value : Position(pointer, offset, start, travel, bar.Minimum,
                    Maximum(bar.Minimum, bar.Maximum, bar.LargeChange), Reverse);
                if (target != preview) { preview = target; if (live) Emit(ScrollEventType.ThumbTrack, target); }
            }
            else if (msg == 0x0202)
            {
                bool commit = part == Part.Thumb;
                int target = preview;
                // Commit while the preview still owns the thumb. Otherwise
                // WM_CAPTURECHANGED can publish its old native position before
                // the deferred ThumbPosition update has reached the grid.
                if (commit && Available)
                {
                    // DataGridView consumes ThumbTrack, not ThumbPosition, to
                    // move its actual rows. Deferred dragging sends that one
                    // update only at release, before the final position event.
                    if (!live) Emit(ScrollEventType.ThumbTrack, target);
                    if (Available) Emit(ScrollEventType.ThumbPosition, bar.Value);
                }
                ReleaseCapture();
                if (Available) Emit(ScrollEventType.EndScroll, bar.Value);
            }
            repaint(); message.Result = IntPtr.Zero; return true;
        }
        catch { Cancel(false); throw; }
    }
    private Part Hit(GridScrollbarSkin.Geometry shape, Point point)
    {
        if (shape.StartArrow.Contains(point)) return (shape.StartState & 1) == 0 ? Part.SmallDecrement : Part.None;
        if (shape.EndArrow.Contains(point)) return (shape.EndState & 1) == 0 ? Part.SmallIncrement : Part.None;
        if (shape.Thumb.Contains(point)) return (shape.ThumbState & 0x8001) == 0 ? Part.Thumb : Part.None;
        if (!bar.ClientRectangle.Contains(point)) return Part.None;
        return (Vertical ? point.Y : point.X) < AxisStart(shape.Thumb) ? Part.LargeDecrement : Part.LargeIncrement;
    }
    private void Step()
    {
        if (!GridScrollbarSkin.TryGetGeometry(bar, out var shape)) { Cancel(true); return; }
        var point = Vertical ? new Point(bar.Width / 2, pointer) : new Point(pointer, bar.Height / 2);
        if (Hit(shape, point) != part) return;
        bool decrease = part is Part.SmallDecrement or Part.LargeDecrement;
        bool large = part is Part.LargeDecrement or Part.LargeIncrement;
        if (Reverse) decrease = !decrease;
        long change = large ? bar.LargeChange : bar.SmallChange;
        int value = (int)Math.Clamp(bar.Value + (decrease ? -change : change), bar.Minimum, Maximum(bar.Minimum, bar.Maximum, bar.LargeChange));
        var type = (large, decrease) switch
        { (true, true) => ScrollEventType.LargeDecrement, (true, false) => ScrollEventType.LargeIncrement,
          (false, true) => ScrollEventType.SmallDecrement, _ => ScrollEventType.SmallIncrement };
        Emit(type, value);
    }
    private void Emit(ScrollEventType type, int value)
    {
        if (!Available || raise == null) return;
        Notifying = true;
        try
        {
            var args = new ScrollEventArgs(type, bar.Value, value, Vertical ? ScrollOrientation.VerticalScroll : ScrollOrientation.HorizontalScroll);
            owner.PerformBufferedScroll(() =>
            {
                raise(bar, args);
                if (!Available) return;
                bar.Value = Math.Clamp(args.NewValue, bar.Minimum, Maximum(bar.Minimum, bar.Maximum, bar.LargeChange));
                preview = bar.Value;
            });
        }
        finally { Notifying = false; }
    }
    private void ReleaseCapture()
    {
        repeat.Stop(); active = false; part = Part.None;
        if (!bar.IsDisposed && bar.IsHandleCreated && bar.Capture) bar.Capture = false;
    }
    internal void Cancel(bool notify)
    {
        bool wasActive = active;
        ReleaseCapture();
        if (notify && wasActive && Available) Emit(ScrollEventType.EndScroll, bar.Value);
        if (wasActive) repaint();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; ReleaseCapture(); repeat.Dispose();
    }
}
