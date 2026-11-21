using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskAtlas.ResizePrototype;

// Read-only, opt-in observations of explicitly selected title controls. A
// mismatch is a geometric observation, not proof that a glyph was cut off.
internal sealed class ResizeTitleGeometryAudit : IDisposable
{
    private const int MaxEvents = 2048, MaxIssues = 64;
    private readonly Form owner;
    private readonly List<Control[]> chains = [];
    private readonly Dictionary<Control, AuditWindow> hooks = [];
    private readonly List<Issue> issues = new(MaxIssues);
    private readonly Bitmap boundsBitmap = new(1, 1);
    private readonly Graphics boundsGraphics;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly long started = Stopwatch.GetTimestamp();
    private long paintEntries, eraseEntries, elapsedTicks;
    private int titlePaintEntries, titlePaintGeometryIssues;
    private int sampledEvents, nativeClientMismatches, nativeBoundsMismatches, regionMismatches,
        regionComparisons, managedRegionNativeUnavailable, unavailableObservations, failures, droppedIssues;
    private bool disposed, observing, observationsTruncated, detachDeferred;

    internal ResizeTitleGeometryAudit(Form owner, IEnumerable<Control> titles)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(titles);
        this.owner = owner;
        boundsGraphics = Graphics.FromImage(boundsBitmap);
        try
        {
            var selected = titles.Take(3).ToArray();
            if (selected.Length is < 1 or > 2 || selected.Any(title => title is not Label || title.FindForm() != owner) ||
                selected.Distinct().Count() != selected.Length)
                throw new ArgumentException("TitleAuditSelection");
            foreach (var title in selected)
            {
                var chain = new List<Control> { title };
                for (Control? ancestor = title.Parent; ancestor != null && chain.Count < 6; ancestor = ancestor.Parent)
                {
                    chain.Add(ancestor);
                    if (ancestor == owner) break;
                }
                chains.Add(chain.ToArray());
                foreach (var control in chain)
                {
                    if (hooks.ContainsKey(control)) continue;
                    var hook = new AuditWindow(this, control, hooks.Count + 1);
                    hooks.Add(control, hook);
                    hook.Subscribe();
                }
            }
            owner.Disposed += OwnerDisposed;
        }
        catch { Dispose(); throw; }
    }

    private void OwnerDisposed(object? sender, EventArgs args) => Dispose();

    private void Observe(Control source, uint message)
    {
        if (disposed || observing) return;
        if (message == 0x000F) paintEntries++; else eraseEntries++;
        if (Environment.CurrentManagedThreadId != thread) { failures++; return; }
        if (sampledEvents >= MaxEvents) { observationsTruncated = true; return; }
        sampledEvents++;
        long began = Stopwatch.GetTimestamp(); observing = true;
        try
        {
            for (int titleIndex = 0; titleIndex < chains.Count; titleIndex++)
            {
                var chain = chains[titleIndex];
                if (!chain.Contains(source)) continue;
                bool titlePaint = message == 0x000F && source == chain[0];
                if (titlePaint) titlePaintEntries++;
                var snapshots = new Geometry[chain.Length];
                bool issue = false;
                for (int depth = 0; depth < chain.Length; depth++)
                {
                    snapshots[depth] = Read(chain[depth], depth);
                    var snapshot = snapshots[depth];
                    if (!snapshot.Available) { unavailableObservations++; continue; }
                    if (snapshot.ClientMismatch) { nativeClientMismatches++; issue = true; }
                    if (snapshot.BoundsMismatch) { nativeBoundsMismatches++; issue = true; }
                    if (snapshot.NativeRegionBox.HasValue && snapshot.ManagedRegionBox.HasValue) regionComparisons++;
                    else if (snapshot.ManagedRegionBox.HasValue) managedRegionNativeUnavailable++;
                    if (snapshot.RegionMismatch) { regionMismatches++; issue = true; }
                }
                if (!issue) continue;
                if (titlePaint) titlePaintGeometryIssues++;
                if (issues.Count == MaxIssues) { droppedIssues++; continue; }
                issues.Add(new(sampledEvents, Stopwatch.GetTimestamp() - started,
                    titleIndex + 1, hooks[source].Id, message == 0x000F ? "PaintEnter" : "EraseEnter", snapshots));
            }
        }
        catch { failures++; }
        finally { observing = false; elapsedTicks += Stopwatch.GetTimestamp() - began; }
    }

    private Geometry Read(Control control, int depth)
    {
        var hook = hooks[control];
        if (control.IsDisposed || !control.IsHandleCreated || !owner.IsHandleCreated || hook.Handle == IntPtr.Zero)
            return Geometry.Missing(hook.Id, depth);
        IntPtr handle = hook.Handle;
        long generation = hook.Generation;
        if (handle != owner.Handle && !IsChild(owner.Handle, handle)) return Geometry.Missing(hook.Id, depth);
        if (!GetClientRect(handle, out var client) || !GetWindowRect(handle, out var window))
            return Geometry.Missing(hook.Id, depth);
        Rectangle nativeBounds, managedBounds;
        bool parentMapped = false;
        if (control == owner)
        {
            nativeBounds = new(0, 0, window.Right - window.Left, window.Bottom - window.Top);
            managedBounds = new(0, 0, owner.Width, owner.Height);
            parentMapped = true;
        }
        else
        {
            IntPtr parent = GetParent(handle);
            var origin = new NativePoint();
            parentMapped = parent != IntPtr.Zero && (parent == owner.Handle || IsChild(owner.Handle, parent)) &&
                control.Parent is { IsHandleCreated: true } logicalParent && logicalParent.Handle == parent && ClientToScreen(parent, ref origin);
            nativeBounds = parentMapped ? new(window.Left - origin.X, window.Top - origin.Y,
                window.Right - window.Left, window.Bottom - window.Top) : Rectangle.Empty;
            managedBounds = control.Bounds;
        }
        var nativeClient = new Size(client.Right - client.Left, client.Bottom - client.Top);
        Size managedClient = control.ClientSize;
        Rectangle? nativeRegion = null;
        int regionKind = 0;
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (region != IntPtr.Zero)
            {
                regionKind = GetWindowRgn(handle, region);
                if (regionKind != 0 && GetRgnBox(region, out var rectangle) != 0)
                    nativeRegion = Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
            }
        }
        finally { if (region != IntPtr.Zero) DeleteObject(region); }
        RectangleF? managedRegion = control.Region?.GetBounds(boundsGraphics);
        if (!control.IsHandleCreated || hook.Handle != handle || hook.Generation != generation)
            return Geometry.Missing(hook.Id, depth);
        bool clientMismatch = nativeClient != managedClient;
        bool boundsMismatch = parentMapped && nativeBounds != managedBounds;
        bool regionMismatch = nativeRegion is { } actual && managedRegion is { } expected &&
            (Math.Abs(actual.Left - expected.Left) > 1 || Math.Abs(actual.Top - expected.Top) > 1 ||
             Math.Abs(actual.Right - expected.Right) > 1 || Math.Abs(actual.Bottom - expected.Bottom) > 1);
        return new(hook.Id, depth, generation, true, control.DeviceDpi, nativeClient, managedClient,
            parentMapped ? nativeBounds : null, parentMapped ? managedBounds : null,
            regionKind, nativeRegion, managedRegion, clientMismatch, boundsMismatch, regionMismatch);
    }

    internal object Summary() => new
    {
        schema = "resize-title-geometry-audit-v1", enabled = true,
        title_count = chains.Count, watched_controls = hooks.Count,
        paint_entries = paintEntries, erase_entries = eraseEntries,
        title_paint_entries = titlePaintEntries, title_paint_geometry_issues = titlePaintGeometryIssues,
        sampled_events = sampledEvents, max_sampled_events = MaxEvents,
        observations_truncated = observationsTruncated,
        native_client_mismatches = nativeClientMismatches,
        native_bounds_mismatches = nativeBoundsMismatches,
        region_bbox_mismatches_beyond_one_pixel = regionMismatches,
        region_compared_observations = regionComparisons,
        managed_region_native_unavailable = managedRegionNativeUnavailable,
        unavailable_observations = unavailableObservations,
        issue_count = issues.Count, max_issues = MaxIssues,
        issue_records_truncated = droppedIssues > 0, dropped_issue_records = droppedIssues,
        failures, detach_deferred = detachDeferred, diagnostic_ticks = elapsedTicks,
        timestamp_frequency = Stopwatch.Frequency,
        attribution = "bbox-observations-only-not-glyph-clipping-proof",
        native_region_zero = "absent-or-error-unknown-not-a-mismatch",
        emitted_issue_examples = Math.Min(4, issues.Count),
        issues = issues.Take(4).Select(issue => new
        {
            issue.Event, issue.Timestamp, issue.TitleId, issue.SourceSurfaceId, issue.Message,
            chain = issue.Chain.Where(g => g.ClientMismatch || g.BoundsMismatch || g.RegionMismatch).Take(4).Select(g => new
            {
                g.SurfaceId, g.Depth, g.Dpi, g.ClientMismatch, g.BoundsMismatch, g.RegionMismatch,
                native_client = g.NativeClient is { } n ? new[] { n.Width, n.Height } : null,
                managed_client = g.ManagedClient is { } m ? new[] { m.Width, m.Height } : null,
                native_bounds = g.NativeParentBounds is { } b ? new[] { b.X, b.Y, b.Width, b.Height } : null,
                managed_bounds = g.ManagedParentBounds is { } c ? new[] { c.X, c.Y, c.Width, c.Height } : null
            }).ToArray()
        }).ToArray()
    };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        owner.Disposed -= OwnerDisposed;
        foreach (var hook in hooks.Values)
        {
            if (Environment.CurrentManagedThreadId == thread) hook.Dispose();
            else { hook.Unsubscribe(); detachDeferred = true; }
        }
        boundsGraphics.Dispose(); boundsBitmap.Dispose();
    }

    private sealed record Geometry(int SurfaceId, int Depth, long HandleGeneration, bool Available,
        int Dpi, Size? NativeClient, Size? ManagedClient, Rectangle? NativeParentBounds,
        Rectangle? ManagedParentBounds, int NativeRegionKind, Rectangle? NativeRegionBox,
        RectangleF? ManagedRegionBox, bool ClientMismatch, bool BoundsMismatch, bool RegionMismatch)
    {
        internal static Geometry Missing(int id, int depth) =>
            new(id, depth, 0, false, 0, null, null, null, null, 0, null, null, false, false, false);
    }
    private sealed record Issue(int Event, long Timestamp, int TitleId, int SourceSurfaceId,
        string Message, Geometry[] Chain);

    private sealed class AuditWindow(ResizeTitleGeometryAudit audit, Control control, int id) : NativeWindow, IDisposable
    {
        internal int Id { get; } = id;
        internal long Generation { get; private set; }
        internal void Subscribe()
        {
            control.HandleCreated += Created;
            control.HandleDestroyed += Destroyed;
            if (control.IsHandleCreated) Created(control, EventArgs.Empty);
        }
        private void Created(object? sender, EventArgs args)
        {
            if (audit.disposed || control.IsDisposed || !control.IsHandleCreated) return;
            if (Handle != IntPtr.Zero) ReleaseHandle();
            Generation++; AssignHandle(control.Handle);
        }
        private void Destroyed(object? sender, EventArgs args)
        { Generation++; if (Handle != IntPtr.Zero) ReleaseHandle(); }
        internal void Unsubscribe()
        { control.HandleCreated -= Created; control.HandleDestroyed -= Destroyed; }
        public void Dispose() { Unsubscribe(); if (Handle != IntPtr.Zero) ReleaseHandle(); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg is 0x000F or 0x0014) audit.Observe(control, (uint)message.Msg);
            base.WndProc(ref message);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr handle);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr handle, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region, out NativeRect rectangle);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
}
