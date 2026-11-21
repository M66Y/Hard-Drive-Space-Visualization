using System.Runtime.InteropServices;

namespace DiskAtlas;

// Geometry changes need a presentation boundary after child HWNDs and their
// rounded clips settle. It owns one queue slot and explicitly owned local
// redraw holds during registered layout or a complete resize transaction.
internal sealed class LayoutPresentation : IDisposable
{
    private readonly Form owner;
    private readonly Func<bool> suppressed;
    private readonly Action<Control> settleClips;
    private readonly List<Root> roots = [];
    private bool queued, presenting, disposed;
    private long ticket;
    private int geometryDepth;
    private long geometryGeneration;
    private IntPtr geometryHandle;
    private bool geometryHoldRequested, finalizingGeometry;
    private bool logicalOnlyGeometry;
    internal bool IsLogicalGeometryActive => logicalOnlyGeometry && (geometryDepth > 0 || finalizingGeometry);
    private readonly List<(Root Root, IDisposable Token)> geometryHolds = [];
    internal bool HasQueuedWork => queued;
    internal int Presentations { get; private set; }
    internal int PendingRoots => roots.Count(root => !root.Dirty.IsEmpty);
    // Diagnostic comparison only; ordinary application behavior stays enabled.
    internal bool NativeLayoutHoldEnabled { get; set; } = true;

    internal LayoutPresentation(Form owner, Func<bool> suppressed, Action<Control> settleClips, params Control[] controls)
    {
        this.owner = owner; this.suppressed = suppressed; this.settleClips = settleClips;
        foreach (var control in controls) roots.Add(new Root(this, control));
        owner.HandleDestroyed += OwnerHandleDestroyed;
        owner.HandleCreated += Retry;
    }

    internal void ResizeSurfaces()
    {
        foreach (var root in roots) Request(root, root.Control.ClientRectangle);
    }
    internal void WindowResized() { foreach (var root in roots.ToArray()) root.Observe(); }
    internal IDisposable? BeginLayout(Control control)
    {
        if (presenting || finalizingGeometry || !Allowed) return null;
        return roots.FirstOrDefault(root => root.Control == control)?.HoldLayout();
    }

    // A Form WM_SIZE lays out several registered roots. Each root still owns
    // its local redraw hold, but none may synchronously present an intermediate
    // parent layout. The outer message performs one final presentation pass.
    internal IDisposable? BeginGeometry(bool holdRedraw = false, bool logicalOnly = false)
    {
        if (presenting || finalizingGeometry || !Allowed) return null;
        if (geometryDepth == 0)
        {
            geometryHandle = owner.Handle;
            geometryGeneration++;
            geometryHoldRequested = false;
            logicalOnlyGeometry = logicalOnly;
            ticket++; queued = false; // Retire a previously posted callback.
        }
        geometryDepth++;
        long generation = geometryGeneration;
        IntPtr handle = geometryHandle;
        if (holdRedraw && !logicalOnlyGeometry && NativeLayoutHoldEnabled && !geometryHoldRequested)
        {
            geometryHoldRequested = true;
            try
            {
                foreach (var root in roots.OrderBy(root => Depth(root.Control)).ToArray())
                {
                    if (!Current()) break;
                    var token = root.HoldLayout(invalidateEmpty: false);
                    if (!Current()) { token?.Dispose(); break; }
                    if (token != null) geometryHolds.Add((root, token));
                }
            }
            catch
            {
                try { EndGeometry(generation); }
                catch { /* Preserve the acquisition exception; cleanup tried all owned roots. */ }
                throw;
            }
        }
        return new GeometryHold(this, generation);

        bool Current() => !disposed && generation == geometryGeneration && Allowed && owner.Handle == handle;
    }

    private void EndGeometry(long generation)
    {
        if (disposed || generation != geometryGeneration || geometryDepth == 0) return;
        if (geometryDepth > 1) { geometryDepth--; return; }
        IntPtr handle = geometryHandle;
        var holds = geometryHolds.ToArray(); geometryHolds.Clear();
        var settled = new HashSet<Root>();
        Exception? failure = null;
        finalizingGeometry = true;
        bool Current() => !disposed && generation == geometryGeneration && Allowed && owner.Handle == handle;
        try
        {
            // Keep the owning roots native-held through their final clips.
            // A descendant blocked by our ancestor is covered, never acquired
            // as a second native owner. Externally held roots stay untouched.
            foreach (var root in roots.ToArray())
            {
                if (!Current()) break;
                if (root.Dirty.IsEmpty || root.Control.IsDisposed || root.Control.Disposing ||
                    !root.Control.IsHandleCreated || !root.Control.Visible || root.Control.FindForm() != owner ||
                    !holds.Any(held => held.Root == root || held.Root.Control.Contains(root.Control))) continue;
                settleClips(root.Control);
                if (!Current()) break;
                root.Observe(); settled.Add(root);
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            for (int index = holds.Length - 1; index >= 0; index--)
            {
                try { holds[index].Token.Dispose(); }
                catch (Exception error) { failure ??= error; }
            }
            if (generation == geometryGeneration)
            {
                geometryDepth = 0; geometryHandle = IntPtr.Zero; geometryHoldRequested = false; logicalOnlyGeometry = false;
            }
            finalizingGeometry = false;
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (!Current()) return;
        PresentPending(layoutSettled: true, settled);
    }

    private sealed class GeometryHold(LayoutPresentation presenter, long generation) : IDisposable
    {
        private LayoutPresentation? owner = presenter;
        public void Dispose()
        {
            var held = owner; owner = null;
            held?.EndGeometry(generation);
        }
    }

    private void PresentPending(bool layoutSettled = false, HashSet<Root>? settled = null)
    {
        if (geometryDepth != 0 || presenting || !Allowed || !roots.Any(root => !root.Dirty.IsEmpty && Ready(root.Control))) return;
        queued = false;
        Flush(++ticket, owner.Handle, layoutSettled, settled);
    }
    // External redraw-lock owners must call this after releasing their lock.
    // The grid's own outer scroll transaction supplies the same notification.
    internal void RetryPending() => Queue();
    private void Retry(object? sender, EventArgs args) => RetryPending();

    private bool Allowed => !disposed && !owner.IsDisposed && !owner.Disposing && owner.IsHandleCreated &&
        owner.Visible && owner.WindowState != FormWindowState.Minimized && !suppressed();

    private void Request(Root root, Rectangle rectangle)
    {
        if (!Allowed) { if (geometryDepth == 0) Clear(); return; }
        rectangle = Rectangle.Intersect(rectangle, root.Control.ClientRectangle);
        if (rectangle.Width > 0 && rectangle.Height > 0)
            root.Dirty = root.Dirty.IsEmpty ? rectangle : Rectangle.Union(root.Dirty, rectangle);
        Queue();
    }

    private void Queue()
    {
        if (geometryDepth != 0 || queued || presenting || !Allowed || !roots.Any(root => !root.Dirty.IsEmpty && Ready(root.Control))) return;
        queued = true;
        long expected = ++ticket;
        IntPtr handle = owner.Handle; // Allowed checked IsHandleCreated; never create one.
        try { owner.BeginInvoke((Action)(() => Flush(expected, handle))); }
        catch (InvalidOperationException) { queued = false; Clear(); }
    }

    private bool Ready(Control control)
    {
        if (control.IsDisposed || control.Disposing || !control.IsHandleCreated || !control.Visible || control.FindForm() != owner) return false;
        for (Control? ancestor = control; ancestor != null; ancestor = ancestor.Parent)
            if (ancestor.IsHandleCreated && GetProp(ancestor.Handle, "SysSetRedraw") != IntPtr.Zero) return false;
        return !HasPausedGrid(control);
    }

    private static bool HasPausedGrid(Control control)
    {
        if (control is WallpaperGrid grid && grid.Visible && grid.IsHandleCreated && grid.ScrollbarPaintingPaused) return true;
        foreach (Control child in control.Controls)
            if (child.Visible && HasPausedGrid(child)) return true;
        return false;
    }

    private void Flush(long expected, IntPtr handle, bool layoutSettled = false, HashSet<Root>? settled = null)
    {
        using var resizeWork = ResizeWorkProbe.Measure(ResizeWorkProbe.Work.LayoutPresent);
        if (disposed || expected != ticket) return;
        queued = false;
        if (geometryDepth != 0) return;
        if (!Allowed || owner.Handle != handle) { Clear(); return; }
        presenting = true;
        try
        {
            var currentRoots = roots.ToArray();
            bool Current() => !disposed && geometryDepth == 0 && expected == ticket && Allowed && owner.Handle == handle;
            foreach (var root in currentRoots)
            {
                if (!Current()) return;
                if (root.Dirty.IsEmpty || !Ready(root.Control)) continue;
                // A completed outer geometry message has already run the
                // framework's layout. Keep clip reconciliation and observation,
                // without replaying every root's layout merely to present it.
                // Deferred/non-geometry requests retain the original path.
                if (!layoutSettled) root.Control.PerformLayout();
                if (!Current()) return;
                if (settled?.Contains(root) != true) settleClips(root.Control);
                if (!Current()) return;
                root.Observe();
            }
            // A parent request covers its registered descendants. Keep only
            // the small list root when pagination is the sole geometry change.
            foreach (var parent in currentRoots)
            foreach (var child in currentRoots)
            {
                if (parent == child || parent.Dirty.IsEmpty || child.Dirty.IsEmpty || !parent.Control.Contains(child.Control)) continue;
                Point offset = Offset(child.Control, parent.Control);
                var area = child.Dirty; area.Offset(offset);
                parent.Dirty = Rectangle.Union(parent.Dirty, area);
                child.Dirty = Rectangle.Empty;
            }
            var batches = currentRoots.Where(root => !root.Dirty.IsEmpty && Ready(root.Control))
                .Select(root => (Root: root, Area: Rectangle.Intersect(root.Dirty, root.Control.ClientRectangle), Handle: root.Control.Handle)).ToArray();
            foreach (var batch in batches)
            {
                if (!Current()) return;
                if (!Ready(batch.Root.Control)) continue; // Keep its pending area.
                if (batch.Root.Control.Handle != batch.Handle)
                {
                    batch.Root.Dirty = batch.Root.Control.ClientRectangle;
                    continue;
                }
                var area = Rectangle.Intersect(batch.Root.Dirty.IsEmpty ? batch.Area : Rectangle.Union(batch.Area, batch.Root.Dirty), batch.Root.Control.ClientRectangle);
                batch.Root.Dirty = Rectangle.Empty;
                var rect = new NativeRect(area);
                // No Form-root or frame repaint, lock toggling, or loop here.
                // UPDATENOW also drains already pending paint on these HWNDs.
                if (RedrawWindow(batch.Handle, ref rect, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100)) Presentations++;
                else if (Current() && Ready(batch.Root.Control)) batch.Root.Control.Invalidate(area, true);
            }
        }
        finally { presenting = false; Queue(); }
    }

    private static Point Offset(Control control, Control? root)
    {
        Point point = Point.Empty;
        for (Control? node = control; node != null && node != root; node = node.Parent) point.Offset(node.Left, node.Top);
        return point;
    }

    private static int Depth(Control control)
    {
        int result = 0;
        for (Control? parent = control.Parent; parent != null; parent = parent.Parent) result++;
        return result;
    }

    private void Clear() { foreach (var root in roots) root.Dirty = Rectangle.Empty; }
    private void OwnerHandleDestroyed(object? sender, EventArgs args)
    {
        geometryGeneration++; geometryDepth = 0; geometryHandle = IntPtr.Zero;
        geometryHoldRequested = false; logicalOnlyGeometry = false; geometryHolds.Clear();
        ticket++; queued = false; Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; geometryGeneration++; geometryDepth = 0; geometryHandle = IntPtr.Zero;
        geometryHoldRequested = false; logicalOnlyGeometry = false; geometryHolds.Clear();
        ticket++; queued = false; Clear();
        owner.HandleDestroyed -= OwnerHandleDestroyed;
        owner.HandleCreated -= Retry;
        foreach (var root in roots) root.Dispose();
        roots.Clear();
    }

    private sealed class Root : IDisposable
    {
        private readonly LayoutPresentation presenter;
        internal Control Control { get; }
        internal Rectangle Dirty;
        private Size size, windowSize;
        private Point offset;
        private bool visible;
        private IntPtr heldLayoutHandle;
        private bool layoutActive;
        private bool nativeLayoutHeld;
        private long layoutGeneration;
        private readonly Dictionary<Control, (Rectangle Bounds, bool Visible)> children = [];

        internal Root(LayoutPresentation presenter, Control control)
        {
            this.presenter = presenter; Control = control;
            size = control.ClientSize; windowSize = presenter.owner.ClientSize; offset = Offset(control, presenter.owner);
            visible = control.Visible;
            control.Layout += Layout;
            control.LocationChanged += Changed; control.SizeChanged += Changed; control.VisibleChanged += Changed;
            control.ControlAdded += Added; control.ControlRemoved += Removed;
            control.HandleCreated += HandleCreated; control.HandleDestroyed += HandleDestroyed;
            foreach (Control child in control.Controls) Watch(child);
        }

        private void Watch(Control child)
        {
            children[child] = (child.Bounds, child.Visible);
            child.LocationChanged += Changed; child.SizeChanged += Changed; child.VisibleChanged += Changed;
            if (child is WallpaperGrid grid) grid.PresentationResumed += presenter.Retry;
        }
        private void Unwatch(Control child)
        {
            child.LocationChanged -= Changed; child.SizeChanged -= Changed; child.VisibleChanged -= Changed;
            if (child is WallpaperGrid grid) grid.PresentationResumed -= presenter.Retry;
            children.Remove(child);
        }
        private void Added(object? sender, ControlEventArgs args) { if (args.Control is { } child) Watch(child); presenter.Request(this, Control.ClientRectangle); }
        private void Removed(object? sender, ControlEventArgs args) { if (args.Control is { } child) Unwatch(child); presenter.Request(this, Control.ClientRectangle); }
        private void Layout(object? sender, LayoutEventArgs args) => Observe();
        private void Changed(object? sender, EventArgs args) { Observe(); if (!layoutActive) ReleaseLayout(); }
        private void HandleCreated(object? sender, EventArgs args) => presenter.Request(this, Control.ClientRectangle);
        private void HandleDestroyed(object? sender, EventArgs args)
        {
            layoutGeneration++; layoutActive = false; nativeLayoutHeld = false; heldLayoutHandle = IntPtr.Zero;
            Dirty = Rectangle.Empty; size = Size.Empty;
        }

        internal IDisposable? HoldLayout(bool invalidateEmpty = true)
        {
            if (heldLayoutHandle != IntPtr.Zero || !presenter.Ready(Control) || !IsWindowVisible(Control.Handle)) return null;
            heldLayoutHandle = Control.Handle;
            layoutActive = true;
            long generation = ++layoutGeneration;
            nativeLayoutHeld = presenter.NativeLayoutHoldEnabled && !presenter.logicalOnlyGeometry;
            IntPtr handle = heldLayoutHandle;
            try
            {
                if (nativeLayoutHeld) SendMessage(handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
                if (generation != layoutGeneration || Control.IsDisposed || !Control.IsHandleCreated || Control.Handle != handle) return null;
                return new LayoutHold(this, generation, invalidateEmpty);
            }
            catch
            {
                if (generation == layoutGeneration)
                {
                    layoutActive = false;
                    try { ReleaseLayout(invalidateEmpty); }
                    catch { /* The original acquisition exception remains authoritative. */ }
                }
                throw;
            }
        }

        private void ReleaseLayout(bool invalidateEmpty = true)
        {
            if (heldLayoutHandle == IntPtr.Zero) return;
            if (Control.IsDisposed || !Control.IsHandleCreated || Control.Handle != heldLayoutHandle)
            { heldLayoutHandle = IntPtr.Zero; return; }
            // Like the grid scroll transaction, never make a hidden control
            // visible by releasing WM_SETREDRAW. Its show event retries this.
            if (nativeLayoutHeld && !Control.Visible) return;
            IntPtr handle = heldLayoutHandle; heldLayoutHandle = IntPtr.Zero;
            bool restoreNative = nativeLayoutHeld; nativeLayoutHeld = false;
            long generation = layoutGeneration;
            try
            {
                if (restoreNative) SendMessage(handle, 0x000B, new IntPtr(1), IntPtr.Zero);
            }
            catch
            {
                // A callback may fail before native redraw was restored. Keep
                // this generation's ownership available for a later retry.
                if (restoreNative && generation == layoutGeneration && !Control.IsDisposed && Control.IsHandleCreated &&
                    Control.Handle == handle && GetProp(handle, "SysSetRedraw") != IntPtr.Zero)
                { heldLayoutHandle = handle; nativeLayoutHeld = true; }
                throw;
            }
            if (invalidateEmpty && Dirty.IsEmpty) Control.Invalidate(true);
            presenter.PresentPending();
        }

        private sealed class LayoutHold(Root root, long generation, bool invalidateEmpty) : IDisposable
        {
            private Root? owner = root;
            public void Dispose()
            {
                var held = owner; owner = null;
                if (held != null && held.layoutGeneration == generation) { held.layoutActive = false; held.ReleaseLayout(invalidateEmpty); }
            }
        }

        internal void Observe()
        {
            if (Control.IsDisposed || Control.Disposing) return;
            var nowOffset = Offset(Control, presenter.owner);
            Rectangle area = size != Control.ClientSize || windowSize != presenter.owner.ClientSize || offset != nowOffset || visible != Control.Visible
                ? Control.ClientRectangle : Rectangle.Empty;
            size = Control.ClientSize; windowSize = presenter.owner.ClientSize; offset = nowOffset;
            visible = Control.Visible;
            foreach (var child in children.Keys.ToArray())
            {
                var previous = children[child]; var current = (Bounds: child.Bounds, Visible: child.Visible);
                if (previous != current)
                {
                    Rectangle changed = Rectangle.Union(previous.Bounds, current.Bounds);
                    area = area.IsEmpty ? changed : Rectangle.Union(area, changed);
                    children[child] = current;
                }
            }
            presenter.Request(this, area);
        }

        public void Dispose()
        {
            ReleaseLayout();
            Control.Layout -= Layout;
            Control.LocationChanged -= Changed; Control.SizeChanged -= Changed; Control.VisibleChanged -= Changed;
            Control.ControlAdded -= Added; Control.ControlRemoved -= Removed;
            Control.HandleCreated -= HandleCreated; Control.HandleDestroyed -= HandleDestroyed;
            foreach (var child in children.Keys.ToArray()) Unwatch(child);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect(Rectangle rect)
    {
        private readonly int left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr window, ref NativeRect rect, IntPtr region, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr handle, string name);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);
}
