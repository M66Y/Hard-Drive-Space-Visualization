using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DiskAtlas;

// Keep the original scrollbar, range and event contract. Only pointer tracking
// avoids the native modal loop that paints a system rail outside WM_PAINT.
internal sealed class GridScrollbarSkin : IDisposable
{
    private readonly WallpaperGrid owner;
    private readonly Dictionary<ScrollBar, BarWindow> bars = [];
    private bool disposed;
    internal int Count => bars.Count;

    internal GridScrollbarSkin(WallpaperGrid owner)
    {
        this.owner = owner;
        owner.ControlAdded += Added;
        owner.ControlRemoved += Removed;
        owner.Invalidated += OwnerInvalidated;
        owner.PresentationResumed += Resumed;
        foreach (var bar in owner.Controls.OfType<ScrollBar>()) Attach(bar);
    }
    private void Added(object? sender, ControlEventArgs e) { if (e.Control is ScrollBar bar) Attach(bar); }
    private void Removed(object? sender, ControlEventArgs e)
    {
        if (e.Control is ScrollBar bar && bars.Remove(bar, out var skin)) skin.Dispose();
    }
    private void Attach(ScrollBar bar)
    {
        if (!disposed && !bars.ContainsKey(bar)) bars.Add(bar, new BarWindow(owner, bar));
    }
    private void OwnerInvalidated(object? sender, InvalidateEventArgs e)
    {
        foreach (var bar in bars.Keys)
            if (!bar.IsDisposed && bar.Visible && bar.Bounds.IntersectsWith(e.InvalidRect)) bar.Invalidate();
    }
    private void Resumed(object? sender, EventArgs e)
    {
        foreach (var skin in bars.Values) skin.PaintCurrent();
    }
    internal long BufferedBytes => bars.Values.Sum(bar => bar.BufferedBytes);
    internal void Pause()
    {
        foreach (var skin in bars.Values) skin.Pause();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        owner.ControlAdded -= Added; owner.ControlRemoved -= Removed; owner.Invalidated -= OwnerInvalidated;
        owner.PresentationResumed -= Resumed;
        foreach (var skin in bars.Values) skin.Dispose();
        bars.Clear();
    }

    internal readonly record struct Geometry(Rectangle StartArrow, Rectangle EndArrow, Rectangle Thumb,
        uint StartState, uint EndState, uint ThumbState);
    internal static bool TryGetGeometry(ScrollBar bar, out Geometry geometry)
    {
        geometry = default;
        if (bar.IsDisposed || !bar.IsHandleCreated || bar.Width < 1 || bar.Height < 1) return false;
        var info = ScrollbarInfo.Create();
        if (!GetScrollBarInfo(bar.Handle, -4, ref info) || info.xyThumbTop < 0 ||
            info.xyThumbBottom < info.xyThumbTop || info.dxyLineButton <= 0) return false;
        bool vertical = bar is VScrollBar;
        int length = vertical ? bar.ClientSize.Height : bar.ClientSize.Width;
        int cross = vertical ? bar.ClientSize.Width : bar.ClientSize.Height;
        if (length <= 0 || cross <= 0) return false;
        int arrow = Math.Clamp(info.dxyLineButton, 1, Math.Max(1, length / 2));
        int start = Math.Clamp(info.xyThumbTop, arrow, Math.Max(arrow, length - arrow));
        int end = Math.Clamp(info.xyThumbBottom, start, Math.Max(start, length - arrow));
        Rectangle Axis(int at, int extent) => vertical ? new(0, at, cross, extent) : new(at, 0, extent, cross);
        geometry = new(Axis(0, arrow), Axis(length - arrow, arrow), Axis(start, end - start),
            info.states[1], info.states[5], info.states[3]);
        return true;
    }

    internal static bool PaintBar(WallpaperGrid owner, ScrollBar bar, Graphics graphics, bool highContrast, Geometry? pointerGeometry = null)
    {
        if (highContrast || owner.IsDisposed || bar.IsDisposed || bar.Parent != owner ||
            !TryGetGeometry(bar, out var geometry)) return false;
        geometry = pointerGeometry ?? geometry;
        var saved = graphics.Save();
        try
        {
            graphics.SetClip(bar.ClientRectangle, CombineMode.Intersect);
            owner.PaintScrollbarBackdrop(bar, graphics);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = owner.DeviceDpi / 96f;
            bool vertical = bar is VScrollBar;
            var thumb = (RectangleF)geometry.Thumb;
            if (thumb.Width > 0 && thumb.Height > 0 && bar.Enabled && (geometry.ThumbState & 0x8001) == 0)
            {
                float inset = Math.Max(2, 4 * scale);
                if (vertical) thumb.Inflate(-Math.Min(inset, thumb.Width / 3), -scale);
                else thumb.Inflate(-scale, -Math.Min(inset, thumb.Height / 3));
                if (thumb.Width > 0 && thumb.Height > 0)
                {
                    using var shape = Theme.Round(thumb, Math.Min(thumb.Width, thumb.Height) / 2);
                    using var fill = new SolidBrush(Color.FromArgb(bar.Capture ? 185 : 125, Theme.Muted));
                    graphics.FillPath(fill, shape);
                }
            }
            DrawArrow(geometry.StartArrow, -1, geometry.StartState);
            DrawArrow(geometry.EndArrow, 1, geometry.EndState);
            return true;

            void DrawArrow(Rectangle bounds, int direction, uint state)
            {
                if (bounds.Width < 3 || bounds.Height < 3) return;
                float x = bounds.Left + bounds.Width / 2f, y = bounds.Top + bounds.Height / 2f;
                float wing = Math.Min(3 * scale, Math.Min(bounds.Width, bounds.Height) / 4f);
                PointF[] points = vertical
                    ? [new(x - wing, y - direction * wing / 2), new(x, y + direction * wing / 2), new(x + wing, y - direction * wing / 2)]
                    : [new(x - direction * wing / 2, y - wing), new(x + direction * wing / 2, y), new(x - direction * wing / 2, y + wing)];
                using var line = new Pen(Color.FromArgb(bar.Enabled && (state & 1) == 0 ? 145 : 65, Theme.Muted), Math.Max(1, scale))
                    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                graphics.DrawLines(line, points);
            }
        }
        catch (Exception error) when (error is OutOfMemoryException or ExternalException) { return false; }
        finally { graphics.Restore(saved); }
    }

    private sealed class BarWindow : NativeWindow, IDisposable
    {
        private readonly WallpaperGrid owner;
        private readonly ScrollBar bar;
        private bool painting, disposed;
        private readonly ScrollbarPointerTracking pointer;
        private PixelSurface? frame;
        private const long MaxFramePixels = 262_144;
        internal long BufferedBytes => frame == null ? 0 : (long)frame.Bitmap.Width * frame.Bitmap.Height * 4;
        internal BarWindow(WallpaperGrid owner, ScrollBar bar)
        {
            this.owner = owner; this.bar = bar;
            pointer = new(owner, bar, PaintCurrent);
            bar.HandleCreated += Created; bar.HandleDestroyed += Destroyed;
            bar.LocationChanged += Changed; bar.SizeChanged += Changed; bar.EnabledChanged += Changed; bar.VisibleChanged += Changed;
            if (bar.IsHandleCreated) AssignHandle(bar.Handle);
        }
        private void Created(object? sender, EventArgs e) { if (!disposed && Handle == IntPtr.Zero) AssignHandle(bar.Handle); }
        private void Destroyed(object? sender, EventArgs e) { pointer.Cancel(false); ReleaseFrame(); if (Handle != IntPtr.Zero) ReleaseHandle(); }
        private void Changed(object? sender, EventArgs e)
        {
            if (disposed || bar.IsDisposed) return;
            if (!bar.Visible || !bar.Enabled || frame?.Bitmap.Size != bar.ClientSize) pointer.Cancel(false);
            if (!bar.Visible || frame?.Bitmap.Size != bar.ClientSize || frame?.Bitmap.HorizontalResolution != bar.DeviceDpi) ReleaseFrame();
            bar.Invalidate();
        }
        protected override void WndProc(ref Message message)
        {
            if (disposed || painting || owner.IsDisposed)
            { base.WndProc(ref message); return; }
            // Neither the themed renderer nor the native high-contrast
            // fallback may print across an ancestor's presentation boundary.
            if (message.Msg is 0x0317 or 0x0318 && message.WParam != IntPtr.Zero && owner.ScrollbarPaintingPaused)
            { message.Result = IntPtr.Zero; return; }
            if (SystemInformation.HighContrast) { base.WndProc(ref message); return; }
            if (pointer.Handle(ref message)) return;
            if (message.Msg == 0x0014) { message.Result = new IntPtr(1); return; } // WM_ERASEBKGND
            if (message.Msg == 0x000F)
            {
                if (!owner.ScrollbarPaintingPaused && !TryGetGeometry(bar, out _))
                { base.WndProc(ref message); return; }
                IntPtr dc = BeginPaint(Handle, out var paint);
                if (dc == IntPtr.Zero) { base.WndProc(ref message); return; }
                try
                {
                    if (dc != IntPtr.Zero && !owner.ScrollbarPaintingPaused)
                    {
                        using var graphics = Graphics.FromHdc(dc);
                        if (!Draw(graphics))
                        {
                            var print = Message.Create(Handle, 0x0318, dc, new IntPtr(0x0004));
                            base.WndProc(ref print);
                        }
                    }
                }
                finally { if (dc != IntPtr.Zero) EndPaint(Handle, ref paint); }
                message.Result = IntPtr.Zero; return;
            }
            if (message.Msg is 0x0317 or 0x0318 && message.WParam != IntPtr.Zero)
            {
                using var graphics = Graphics.FromHdc(message.WParam);
                if (Draw(graphics)) { message.Result = IntPtr.Zero; return; }
            }
            int msg = message.Msg;
            var original = message;
            bool suppressed = false;
            // SetScrollInfo/SetScrollPos may paint the system rail immediately,
            // bypassing WM_PAINT. Keep their state/range updates, but let this
            // skin own publication. Parent scrolling may defer it until resume.
            if (msg is 0x00E9 or 0x00E0 or 0x00E6 && TryGetGeometry(bar, out _))
            {
                if (msg == 0x00E9) { suppressed = message.WParam != IntPtr.Zero; message.WParam = IntPtr.Zero; }
                else if (msg == 0x00E0) { suppressed = message.LParam != IntPtr.Zero; message.LParam = IntPtr.Zero; }
                else if (msg == 0x00E6) { suppressed = true; message.Msg = 0x00E2; }
            }
            base.WndProc(ref message);
            message.Msg = msg;
            if (msg is 0x00E0 or 0x00E2 or 0x00E6 or 0x00E9)
            {
                // Native thumb rectangles can remain cached when redraw=false.
                // Ask the native control to refresh them into OUR offscreen DC,
                // then cover that temporary native image with the themed frame.
                // No native pixels are published to the child window here.
                if (!RefreshGeometry() && suppressed)
                {
                    // Geometry correctness wins if the bounded frame cannot be
                    // allocated: preserve the original native fallback contract.
                    base.WndProc(ref original); message.Result = original.Result;
                }
            }
            // Native range/tracking messages can draw directly without posting
            // WM_PAINT. Reapply only this tiny child surface, not the grid.
            if (msg is 0x00E0 or 0x00E2 or 0x00E4 or 0x00E6 or 0x00E9 or 0x0200 or 0x0201 or 0x0202 or 0x02A3 or 0x0113 or 0x0007 or 0x0008 or 0x2114 or 0x2115 or 0x0215)
                PaintCurrent();
        }
        private bool Draw(Graphics graphics)
        {
            if (painting) return false;
            painting = true;
            try
            {
                EnsureFrame();
                Geometry? pointerGeometry = TryGetGeometry(bar, out var native) ? pointer.Geometry(native) : null;
                if (frame == null) return PaintBar(owner, bar, graphics, SystemInformation.HighContrast, pointerGeometry);
                using (var buffer = Graphics.FromImage(frame.Bitmap))
                {
                    if (!PaintBar(owner, bar, buffer, SystemInformation.HighContrast, pointerGeometry)) return false;
                }
                // Publish the fixed backdrop and thumb together. Never expose
                // the erased rail between background and foreground drawing.
                frame.Paint(graphics, new Rectangle(Point.Empty, frame.Bitmap.Size), Point.Empty, bar);
                return true;
            }
            catch (Exception error) when (error is OutOfMemoryException or ExternalException)
            { ReleaseFrame(); return false; }
            finally { painting = false; }
        }
        private bool EnsureFrame()
        {
            if (frame?.Bitmap.Size != bar.ClientSize || frame?.Bitmap.HorizontalResolution != bar.DeviceDpi) ReleaseFrame();
            if (frame == null && (long)bar.Width * bar.Height <= MaxFramePixels)
                frame = PixelSurface.Create(bar.ClientSize, bar.DeviceDpi, (owner.FindForm() as MainForm)?.Rendering);
            return frame != null;
        }
        private bool RefreshGeometry()
        {
            if (painting || disposed || bar.IsDisposed || !bar.IsHandleCreated || !EnsureFrame()) return false;
            painting = true;
            try
            {
                using var graphics = Graphics.FromImage(frame!.Bitmap);
                IntPtr dc = graphics.GetHdc();
                try
                {
                    var print = Message.Create(Handle, 0x0318, dc, new IntPtr(0x0004));
                    base.WndProc(ref print);
                }
                finally { graphics.ReleaseHdc(dc); }
                return true;
            }
            catch (Exception error) when (error is OutOfMemoryException or ExternalException) { return false; }
            finally { painting = false; }
        }
        internal void PaintCurrent()
        {
            if (disposed || painting || pointer.Notifying || bar.IsDisposed || !bar.IsHandleCreated || !bar.Visible || owner.ScrollbarPaintingPaused) return;
            using var graphics = Graphics.FromHwnd(bar.Handle);
            Draw(graphics);
        }
        private void ReleaseFrame() { frame?.Dispose(); frame = null; }
        internal void Pause() { pointer.Cancel(false); ReleaseFrame(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            pointer.Dispose();
            bar.HandleCreated -= Created; bar.HandleDestroyed -= Destroyed;
            bar.LocationChanged -= Changed; bar.SizeChanged -= Changed; bar.EnabledChanged -= Changed; bar.VisibleChanged -= Changed;
            ReleaseFrame();
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct ScrollbarInfo
    {
        public uint cbSize; public NativeRect rcScrollBar;
        public int dxyLineButton, xyThumbTop, xyThumbBottom, reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public uint[] states;
        internal static ScrollbarInfo Create() => new() { cbSize = (uint)Marshal.SizeOf<ScrollbarInfo>(), states = new uint[6] };
    }
    [StructLayout(LayoutKind.Sequential)] private struct PaintStruct
    {
        public IntPtr hdc;
        public int erase; public NativeRect paint; public int restore, update;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] reserved;
    }
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetScrollBarInfo(IntPtr window, int objectId, ref ScrollbarInfo info);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr window, out PaintStruct paint);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr window, ref PaintStruct paint);
}
