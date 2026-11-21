using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DiskAtlas.ResizePrototype;

internal enum ResizeNativeMode { Canvas, Text, Combo, Grid, All }

// Test-only client surface. The Canvas case has no child controls or child HWNDs.
// Native compatibility is an explicit, separately labelled experiment.
internal sealed class LiveResizePrototypeForm : Form
{
    internal const long MaxBufferBytes = 128L * 1024 * 1024;
    private readonly ResizeSceneRenderer renderer = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer motion = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer sweep = new() { Interval = 30 };
    private readonly Font inputFont = Theme.Font(Theme.BodyFontSize);
    private readonly Font headerFont = Theme.Font(Theme.CaptionFontSize, FontStyle.Bold);
    private readonly Font pathFont = Theme.Font(Theme.CaptionFontSize);
    private readonly Image wallpaper = Wallpaper.Load();
    private readonly Icon appIcon;
    private readonly double[] paintMilliseconds = new double[512];
    private Bitmap? frame;
    private Graphics? frameGraphics;
    private NativeResizeFrame? nativeFrame;
    private bool nativeDibEnabled;
    private readonly double[] publishMilliseconds = new double[512];
    private int publishSamples, nativePublishes, nativePublishFallbacks;
    private readonly Dictionary<NativeResizeFrame.CopyFailure, int> copyFailures = [];
    private int preparedSizeRejected, preparedDpiRejected, preparedRevisionRejected;
    private ResizeSceneLayout? pendingLayout;
    private TextBox? nativeSearch;
    private ComboBox? nativeStrategy, nativeMetric;
    private WallpaperGrid? nativeGrid;
    private bool updatingNative, ready, closing, simulatedProgress;
    private int paintSamples, sweepTicks;
    private long paintTotal;
    private double demoStarted;
    private Size commonSize, smallSize;
    private string? frameFailure;
    private readonly int initialDpi;
    private ResizeRootCoverageHook? coverageHook;
    private ResizeSceneBaseCache? baseCache;
    private Region? animationDirty;
    private ResizeSceneLayout? preparedLayout;
    private ResizeSceneLayout? renderedLayout;
    private bool preparedCommitted;
    private int preparationCount, preparationFailures, preparedPresents;
    private double preparationMilliseconds;
    private int printDepth, childPrintCopies, exposedCopies, exposedCopyFailures;

    internal ResizeSceneState SceneState { get; } = new();
    internal ResizeSceneLayout? PresentedLayout { get; private set; }
    private long FrameBytes => frame == null ? 0 : (long)frame.Width * frame.Height * 4;
    internal long BufferBytes => FrameBytes + (baseCache?.RetainedBytes ?? 0);
    internal int FrameCount { get; private set; }
    internal int FrameFailures { get; private set; }
    internal ResizeNativeMode NativeMode { get; private set; }
    internal bool IsDemoRunning => sweep.Enabled;
    internal string? FrameFailure => frameFailure;
    internal bool? DragFullWindows { get; }
    internal ResizeDiagnosticTrace? DiagnosticTrace { get; private set; }
    internal bool UsesNativeFrame => nativeDibEnabled;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool MappingAuditEnabled { get; set; }

    internal void EnableCoverageCandidate(bool nativeDib = false)
    {
        if (coverageHook != null) throw new InvalidOperationException("CoverageAlreadyEnabled");
        nativeDibEnabled = nativeDib;
        baseCache = new ResizeSceneBaseCache();
        coverageHook = new ResizeRootCoverageHook(this, () => null, prepareFrame: PrepareNextGeometryFrame, presentFrame: PresentPreparedFrame,
            presentBeforeNcPaint: nativeDib);
        UpdateCaption();
    }

    private void PrepareNextGeometryFrame(Size size)
    {
        if (!ready || closing || size.Width <= 0 || size.Height <= 0) return;
        long began = Stopwatch.GetTimestamp();
        preparedLayout = null; preparedCommitted = false;
        try
        {
            var layout = ResizeSceneLayout.Build(size, DeviceDpi, SceneState);
            EnsureFrame(size, DeviceDpi);
            frameGraphics!.ResetClip(); frameGraphics.ResetTransform();
            frameGraphics.SetClip(new Rectangle(Point.Empty, size));
            ComposeScene(layout);
            preparedLayout = layout; preparationCount++;
        }
        catch (Exception failure) when (failure is OutOfMemoryException or ArgumentException or ExternalException or InvalidOperationException)
        {
            preparationFailures++; FrameFailures++; frameFailure = "PreparationUnavailable";
        }
        finally { preparationMilliseconds += Stopwatch.GetElapsedTime(began).TotalMilliseconds; }
    }

    private bool PresentPreparedFrame(IntPtr dc, Size actual)
    {
        if (closing || preparedCommitted || frame == null || preparedLayout is not { } layout) return false;
        if (layout.PixelSize != actual) { preparedSizeRejected++; return false; }
        if (layout.Dpi != DeviceDpi || nativeDibEnabled && GetDpiForWindow(Handle) != DeviceDpi)
        { preparedDpiRejected++; return false; }
        if (layout.Revision != SceneState.Revision) { preparedRevisionRejected++; return false; }
        var area = new Rectangle(Point.Empty, actual);
        long publishBegan = Stopwatch.GetTimestamp();
        DiagnosticTrace?.Mark("PublishBegin", frame, area, actual);
        try
        {
            if (nativeFrame?.TryCopyTo(dc, area) == true) nativePublishes++;
            else
            {
                if (nativeDibEnabled) RecordCopyFallback();
                using var graphics = Graphics.FromHdc(dc);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImage(frame, area, area, GraphicsUnit.Pixel);
            }
        }
        finally { RecordPublish(publishBegan, area, actual); }
        PresentedLayout = layout; preparedCommitted = true; preparedPresents++; FrameCount++;
        DiagnosticTrace?.Mark("FrameCopied", frame, new Rectangle(Point.Empty, actual), actual);
        AuditRoot("PreparedCopied");
        return true;
    }

    internal void EnableDiagnostics(bool pixels)
    {
        if (DiagnosticTrace != null) throw new InvalidOperationException("DiagnosticAlreadyEnabled");
        DiagnosticTrace = new ResizeDiagnosticTrace(this, pixels, coverageHook != null);
        DiagnosticTrace.MappingAuditEnabled = MappingAuditEnabled;
        UpdateCaption();
    }

    internal LiveResizePrototypeForm(ResizeNativeMode mode = ResizeNativeMode.Canvas)
    {
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
        // Only our explicitly owned frame buffer is used. Avoid another hidden
        // full-client WinForms buffer on top of it.
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.DoubleBuffer, false);
        BackColor = Theme.Background;
        BackgroundImage = wallpaper;
        Font = inputFont;
        KeyPreview = true;
        StartPosition = FormStartPosition.CenterScreen;
        using var stream = typeof(Wallpaper).Assembly.GetManifestResourceStream("DiskAtlas.AppIcon");
        appIcon = stream == null ? (Icon)SystemIcons.Application.Clone() : new Icon(stream);
        Icon = appIcon;
        initialDpi = DeviceDpi;
        SetSizes();
        DragFullWindows = SystemParametersInfo(0x0026, 0, out bool drag, 0) ? drag : null;
        NativeMode = mode;
        UpdateCaption();
        motion.Tick += (_, _) =>
        {
            if (closing || !Visible || WindowState == FormWindowState.Minimized) return;
            if (simulatedProgress)
            {
                SceneState.Progress = (clock.Elapsed.TotalSeconds % 12) / 12;
                SceneState.Revision++;
                pendingLayout = null;
            }
            // The elapsed-time phase preserves the accepted 1.15x orbit speed;
            // timer ticks are not a display-frame-rate measurement.
            if (simulatedProgress || baseCache == null) { if (SceneState.Selected >= 0 || SceneState.Hover >= 0 || simulatedProgress) Invalidate(); }
            else InvalidateAnimatedEdges();
        };
        sweep.Tick += (_, _) => SweepTick();
        ready = true;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Handle creation can move the form to a monitor with a different DPI.
        // Re-evaluate in that final context before centering the owned preview.
        SetSizes();
        var work = Screen.FromControl(this).WorkingArea;
        Location = new Point(work.Left + Math.Max(0, (work.Width - Width) / 2),
            work.Top + Math.Max(0, (work.Height - Height) / 2));
        SetMode(NativeMode);
        motion.Start();
        DiagnosticTrace?.Mark("Shown");
    }

    private void SetSizes()
    {
        double scale = DeviceDpi / 96d;
        Rectangle work = Screen.FromControl(this).WorkingArea;
        int width = Math.Min((int)Math.Round(1440 * scale), Math.Max(640, work.Width - 48));
        int height = Math.Min((int)Math.Round(940 * scale), Math.Max(480, work.Height - 48));
        commonSize = new(width, height);
        smallSize = new(Math.Min(width, (int)Math.Round(1120 * scale)),
            Math.Min(height, (int)Math.Round(780 * scale)));
        MinimumSize = smallSize;
        Size = commonSize;
    }

    internal void SetMode(ResizeNativeMode mode)
    {
        if (closing) return;
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        updatingNative = true;
        try
        {
            foreach (Control control in Controls.Cast<Control>().ToArray())
            { Controls.Remove(control); control.Dispose(); }
            nativeSearch = null; nativeStrategy = nativeMetric = null; nativeGrid = null;
            NativeMode = mode;
            if (mode is ResizeNativeMode.Text or ResizeNativeMode.All)
            {
                nativeSearch = new TextBox
                {
                    BorderStyle = BorderStyle.None, Font = inputFont, BackColor = Theme.Surface,
                    ForeColor = Theme.Text, Text = SceneState.Search, MaxLength = 256,
                    PlaceholderText = "搜索扩展名或类型...", AccessibleName = "原型搜索", Name = "prototypeSearch"
                };
                nativeSearch.TextChanged += (_, _) =>
                {
                    if (updatingNative) return;
                    SceneState.Search = nativeSearch.Text; SceneState.Scroll = 0;
                    Changed(rebindGrid: true);
                };
                Controls.Add(nativeSearch);
            }
            if (mode is ResizeNativeMode.Combo or ResizeNativeMode.All)
            {
                nativeStrategy = Combo("扫描策略", ["自动", "低占用", "高速"], 0);
                nativeMetric = Combo("统计口径", ["文件大小", "实际占用"], SceneState.Metric);
                nativeStrategy.Name = "prototypeStrategy"; nativeMetric.Name = "prototypeMetric";
                nativeMetric.SelectedIndexChanged += (_, _) =>
                {
                    if (updatingNative) return;
                    SceneState.Metric = Math.Max(0, nativeMetric.SelectedIndex);
                    Changed(rebindGrid: true);
                };
                Controls.Add(nativeStrategy); Controls.Add(nativeMetric);
            }
            if (mode is ResizeNativeMode.Grid or ResizeNativeMode.All)
            {
                nativeGrid = new WallpaperGrid
                {
                    ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                    AllowUserToResizeRows = false, RowHeadersVisible = false,
                    BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
                    BackgroundColor = Theme.Surface, ForeColor = Theme.Text,
                    Font = inputFont, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                    CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                    ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                    GridColor = Theme.Border, ShowCellToolTips = false,
                    ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                    AccessibleName = "原型原生文件列表", Name = "prototypeGrid"
                };
                nativeGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                { BackColor = Theme.Sidebar, ForeColor = Theme.Text, SelectionBackColor = Theme.Sidebar,
                    Font = headerFont, Padding = new Padding(12, 0, 4, 0) };
                nativeGrid.DefaultCellStyle = new DataGridViewCellStyle
                { BackColor = Theme.Surface, ForeColor = Theme.Text, SelectionBackColor = Theme.Selection,
                    SelectionForeColor = Theme.Text, Font = inputFont, Padding = new Padding(12, 0, 6, 0) };
                nativeGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "名称", FillWeight = 58, MinimumWidth = 90 });
                nativeGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "size", HeaderText = "大小 ↓", FillWeight = 25, MinimumWidth = 80 });
                nativeGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "percent", HeaderText = "占比", FillWeight = 17, MinimumWidth = 64 });
                for (int i = 1; i < 3; i++)
                    nativeGrid.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                nativeGrid.CellPainting += PaintNativeName;
                nativeGrid.SelectionChanged += (_, _) =>
                {
                    if (updatingNative || nativeGrid.SelectedRows.Count == 0) return;
                    if (nativeGrid.SelectedRows[0].Tag is int id) { SceneState.Selected = id; Changed(false); }
                };
                nativeGrid.Scroll += (_, e) =>
                {
                    if (updatingNative || e.ScrollOrientation != ScrollOrientation.VerticalScroll ||
                        nativeGrid.FirstDisplayedScrollingRowIndex < 0) return;
                    SceneState.Scroll = nativeGrid.FirstDisplayedScrollingRowIndex; Changed(false);
                };
                Controls.Add(nativeGrid);
            }
            PrepareLayout(rebindGrid: true);
        }
        finally { updatingNative = false; }
        UpdateCaption();
        DiagnosticTrace?.AttachCurrentChildren();
        DiagnosticTrace?.Mark("ModeChanged");
        Invalidate(true);
    }

    private ComboBox Combo(string label, string[] values, int selected)
    {
        var result = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList,
            Font = inputFont, BackColor = Theme.Surface, ForeColor = Theme.Text,
            AccessibleName = "原型" + label, FlatStyle = FlatStyle.Flat };
        result.Items.AddRange(values); result.SelectedIndex = Math.Clamp(selected, 0, values.Length - 1);
        return result;
    }

    private void PaintNativeName(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (nativeGrid == null || e.RowIndex < 0 || e.ColumnIndex != 0 || e.Graphics == null ||
            nativeGrid.Rows[e.RowIndex].Tag is not int id) return;
        var row = (pendingLayout ?? PresentedLayout)?.Data.FirstOrDefault(value => value.Id == id);
        if (row == null) return;
        var saved = e.Graphics.Save();
        try
        {
            e.Graphics.SetClip(Rectangle.Intersect(e.CellBounds, e.ClipBounds), CombineMode.Intersect);
            nativeGrid.PaintCellSurface(e);
            var bands = FileListPresentation.Bands(e.CellBounds, DeviceDpi, SceneState.View == 1);
            using var dot = new SolidBrush(Theme.CategoryColor(row.Category));
            e.Graphics.FillRectangle(dot, bands.Dot);
            Theme.TextAt(e.Graphics, row.Name, inputFont, Theme.Text, bands.Name,
                TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping);
            if (!bands.Path.IsEmpty)
                Theme.TextAt(e.Graphics, row.Path, pathFont, Theme.Muted, bands.Path,
                    TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis | TextFormatFlags.PreserveGraphicsClipping);
            e.Handled = true;
        }
        finally { e.Graphics.Restore(saved); }
    }

    private ResizeSceneLayout PrepareLayout(bool rebindGrid = false)
    {
        if (pendingLayout == null || pendingLayout.PixelSize != ClientSize ||
            pendingLayout.Dpi != DeviceDpi || pendingLayout.Revision != SceneState.Revision)
            pendingLayout = ResizeSceneLayout.Build(ClientSize, DeviceDpi, SceneState);
        var layout = pendingLayout;
        // This is the geometry stage, outside painting. OnPaint never moves a
        // child HWND, which could re-enter rendering with half-updated bounds.
        if (nativeSearch != null) nativeSearch.Bounds = Inset(layout.Search, 12, 10);
        AuditRoot("AfterTextBounds");
        if (nativeStrategy != null) nativeStrategy.Bounds = ComboBounds(layout.Strategy);
        AuditRoot("AfterStrategyBounds");
        if (nativeMetric != null) nativeMetric.Bounds = ComboBounds(layout.Metric);
        AuditRoot("AfterMetricBounds");
        if (nativeGrid != null)
        {
            nativeGrid.Bounds = Rectangle.Union(layout.ListHeader, layout.ListBody);
            AuditRoot("AfterGridBounds");
            int rowHeight = FileListPresentation.RowHeight(DeviceDpi, SceneState.View == 1);
            nativeGrid.RowTemplate.Height = rowHeight;
            nativeGrid.ColumnHeadersHeight = Math.Max(20, layout.ListHeader.Height);
            if (rebindGrid)
            {
                bool before = updatingNative; updatingNative = true;
                try
                {
                    nativeGrid.Rows.Clear();
                    double total = Math.Max(1, layout.Data.Sum(row => (double)row.Bytes(SceneState.Metric)));
                    foreach (var row in layout.Data)
                    {
                        int index = nativeGrid.Rows.Add(row.Name, SizeFormat.Bytes(row.Bytes(SceneState.Metric)),
                            (row.Bytes(SceneState.Metric) / total).ToString("P1"));
                        nativeGrid.Rows[index].Tag = row.Id;
                        nativeGrid.Rows[index].Height = rowHeight;
                    }
                }
                finally { updatingNative = before; }
            }
            bool syncBefore = updatingNative; updatingNative = true;
            try
            {
                var selected = nativeGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => row.Tag is int id && id == SceneState.Selected);
                if (selected != null && !selected.Selected) { nativeGrid.ClearSelection(); selected.Selected = true; }
                else if (selected == null && nativeGrid.SelectedRows.Count > 0) nativeGrid.ClearSelection();
            }
            finally { updatingNative = syncBefore; }
        }
        return layout;
    }

    private Rectangle Inset(Rectangle rectangle, int x, int y)
    {
        int dx = (int)Math.Round(x * DeviceDpi / 96d), dy = (int)Math.Round(y * DeviceDpi / 96d);
        return new(rectangle.X + dx, rectangle.Y + dy, Math.Max(1, rectangle.Width - dx * 2), Math.Max(1, rectangle.Height - dy * 2));
    }

    private Rectangle ComboBounds(Rectangle frameBounds)
    {
        var inner = Inset(frameBounds, 8, 5);
        int label = (int)Math.Round(78 * DeviceDpi / 96d);
        return new(inner.Left + label, inner.Top, Math.Max(1, inner.Width - label), inner.Height);
    }

    private void Changed(bool rebindGrid)
    {
        ClearAnimationDirty();
        SceneState.Revision++; pendingLayout = null;
        if (ready) PrepareLayout(rebindGrid);
        Invalidate(true);
    }

    protected override void OnResize(EventArgs e)
    {
        ClearAnimationDirty();
        DiagnosticTrace?.Mark("GeometryBegin");
        base.OnResize(e);
        if (!ready || closing || WindowState == FormWindowState.Minimized || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        DiagnosticTrace?.Mark("GeometryBeforeChildren");
        AuditRoot("BeforeChildren");
        using var exposed = new Region();
        exposed.MakeEmpty();
        foreach (Control child in Controls)
            if (child.Visible) IncludeChildSurface(exposed, child, exclude: false);
        pendingLayout = null; PrepareLayout();
        foreach (Control child in Controls)
            if (child.Visible) IncludeChildSurface(exposed, child, exclude: true);
        exposed.Intersect(ClientRectangle);
        PublishExposedRoot(exposed);
        Size valid = PresentedLayout?.PixelSize ?? Size.Empty;
        DiagnosticTrace?.Mark("GeometryAfterChildren", frame, null, valid);
        AuditRoot("AfterChildren");
        Invalidate(true);
        DiagnosticTrace?.Mark("GeometryEnd");
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ReleaseFrame(); pendingLayout = null;
        if (ready && !closing) { PrepareLayout(); Invalidate(true); }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (nativeDibEnabled)
        {
            ClearAnimationDirty(); ReleaseFrame(); pendingLayout = null;
        }
        base.OnHandleDestroyed(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    // Native ComboBox WM_PRINTCLIENT asks its parent to paint into the combo's
    // translated DC. It is not a new root-window presentation or frame commit.
    protected override void OnPrint(PaintEventArgs e)
    {
        printDepth++;
        try { base.OnPrint(e); }
        finally { printDepth--; }
    }

    private bool TryGetOwnedPrintViewport(Graphics graphics, out Point viewport)
    {
        viewport = Point.Empty;
        if (printDepth == 0) return false;
        IntPtr dc = graphics.GetHdc();
        try
        {
            var control = Control.FromHandle(WindowFromDC(dc));
            if (control == null || control.Parent != this || !control.Visible ||
                control != nativeStrategy && control != nativeMetric) return false;
            viewport = new Point(-control.Left, -control.Top);
            return true;
        }
        finally { graphics.ReleaseHdc(dc); }
    }

    private void PublishExposedRoot(Region exposed)
    {
        if (!nativeDibEnabled || !IsHandleCreated || RecreatingHandle || frame == null ||
            nativeFrame == null || renderedLayout is not { } layout) return;
        // This is the already prepared, CURRENT geometry. An earlier root copy
        // could not reach pixels hidden by the old child HWND. Publish only the
        // vacated region after all children have moved, retaining native clips.
        if (layout.PixelSize != ClientSize || layout.Dpi != DeviceDpi ||
            layout.State != pendingLayout?.State || GetDpiForWindow(Handle) != DeviceDpi)
        { Invalidate(exposed); return; }
        IntPtr handle = Handle, dc = GetDC(handle);
        if (dc == IntPtr.Zero) { exposedCopyFailures++; Invalidate(exposed); return; }
        try
        {
            using var graphics = Graphics.FromHdc(dc);
            if (exposed.IsEmpty(graphics)) return;
            graphics.SetClip(exposed, CombineMode.Intersect);
            if (nativeFrame.TryCopyTo(graphics, ClientRectangle)) exposedCopies++;
            else
            {
                exposedCopyFailures++; RecordCopyFallback();
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImage(frame, ClientRectangle, ClientRectangle, GraphicsUnit.Pixel);
            }
        }
        catch (Exception error) when (error is ArgumentException or ExternalException or InvalidOperationException or OutOfMemoryException)
        { exposedCopyFailures++; Invalidate(exposed); }
        finally { ReleaseDC(handle, dc); }
    }

    private static void IncludeChildSurface(Region result, Control child, bool exclude)
    {
        // Region is owned by the control: clone it before intersect/translate.
        // Include rounded holes as exposed pixels instead of treating every
        // HWND as an opaque rectangle after a move or size-dependent clip.
        using var surface = child.Region?.Clone() ?? new Region(new Rectangle(Point.Empty, child.Size));
        surface.Intersect(new Rectangle(Point.Empty, child.Size));
        surface.Translate(child.Left, child.Top);
        if (exclude) result.Exclude(surface); else result.Union(surface);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (closing || ClientSize.Width <= 0 || ClientSize.Height <= 0 || nativeDibEnabled && (!IsHandleCreated || RecreatingHandle)) return;
        long began = Stopwatch.GetTimestamp();
        bool rootPresentation = printDepth == 0;
        try
        {
            if (rootPresentation) DiagnosticTrace?.Mark("FrameBegin", frame, e.ClipRectangle, PresentedLayout?.PixelSize ?? Size.Empty);
            // Build only if a test changed SceneState directly; native bounds
            // are already settled by the normal OnResize/Changed path.
            var layout = pendingLayout;
            if (layout == null || layout.PixelSize != ClientSize || layout.Dpi != DeviceDpi || layout.Revision != SceneState.Revision)
                layout = pendingLayout = ResizeSceneLayout.Build(ClientSize, DeviceDpi, SceneState);
            EnsureFrame(ClientSize, DeviceDpi);
            frameGraphics!.ResetClip(); frameGraphics.ResetTransform();
            frameGraphics.SetClip(new Rectangle(Point.Empty, ClientSize));
            if (preparedLayout?.PixelSize != layout.PixelSize || preparedLayout.Dpi != layout.Dpi || preparedLayout.State != layout.State ||
                renderedLayout?.PixelSize != layout.PixelSize || renderedLayout.Dpi != layout.Dpi || renderedLayout.State != layout.State)
                ComposeScene(layout, animationDirty);
            if (rootPresentation) DiagnosticTrace?.Mark("FrameRendered", frame, new Rectangle(Point.Empty, layout.PixelSize), layout.PixelSize);
            // No scaling of a previous frame: copy matching pixels only after
            // this current-size scene has finished rendering.
            var targetState = e.Graphics.Save();
            try
            {
                e.Graphics.CompositingMode = CompositingMode.SourceCopy;
                var area = new Rectangle(Point.Empty, ClientSize);
                long publishBegan = Stopwatch.GetTimestamp();
                if (rootPresentation) DiagnosticTrace?.Mark("PublishBegin", frame, area, ClientSize);
                try
                {
                    bool mapped = TryGetOwnedPrintViewport(e.Graphics, out var viewport);
                    bool copied = nativeFrame != null && (mapped
                        ? nativeFrame.TryCopyTo(e.Graphics, area, area.Location, viewport)
                        : nativeFrame.TryCopyTo(e.Graphics, area));
                    if (MappingAuditEnabled && nativeFrame != null && (mapped || !copied))
                        DiagnosticTrace?.AuditMapping(e.Graphics, nativeFrame.LastCopyFailure);
                    if (copied)
                    {
                        nativePublishes++;
                        if (!rootPresentation) childPrintCopies++;
                    }
                    else
                    {
                        if (nativeDibEnabled) RecordCopyFallback();
                        e.Graphics.DrawImage(frame!, area, area, GraphicsUnit.Pixel);
                    }
                }
                finally { RecordPublish(publishBegan, area, ClientSize, rootPresentation); }
            }
            finally { e.Graphics.Restore(targetState); }
            if (rootPresentation)
            {
                PresentedLayout = layout; FrameCount++; frameFailure = null;
                preparedLayout = null; preparedCommitted = false;
                DiagnosticTrace?.Mark("FrameCopied", frame, e.ClipRectangle, layout.PixelSize);
                AuditRoot("PaintCopied");
            }
        }
        catch (Exception failure) when (failure is OutOfMemoryException or ArgumentException or ExternalException or InvalidOperationException)
        {
            FrameFailures++;
            frameFailure = "FrameUnavailable";
            e.Graphics.Clear(Theme.Background);
            Theme.TextAt(e.Graphics, "原型帧绘制失败：FrameUnavailable（未通过）", inputFont, Theme.Accent, ClientRectangle);
            motion.Stop(); sweep.Stop(); UpdateCaption();
        }
        finally
        {
            if (rootPresentation)
            {
                ClearAnimationDirty();
                paintMilliseconds[paintSamples++ % paintMilliseconds.Length] = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                paintTotal++;
            }
        }
    }

    private Bitmap? iconBitmap;
    private void AuditRoot(string phase)
    {
        if (MappingAuditEnabled) DiagnosticTrace?.AuditRoot(phase, frame);
    }
    private Bitmap GetIconBitmap() => iconBitmap ??= appIcon.ToBitmap();

    private void RecordPublish(long began, Rectangle area, Size content, bool rootPresentation = true)
    {
        publishMilliseconds[publishSamples++ % publishMilliseconds.Length] = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        if (rootPresentation) DiagnosticTrace?.Mark("PublishEnd", frame, area, content);
    }
    private void RecordCopyFallback()
    {
        nativePublishFallbacks++;
        if (nativeFrame is { } owned)
            copyFailures[owned.LastCopyFailure] = copyFailures.GetValueOrDefault(owned.LastCopyFailure) + 1;
    }

    private void ComposeScene(ResizeSceneLayout layout, Region? animationClip = null)
    {
        Action<string>? stage = DiagnosticTrace == null ? null : value => DiagnosticTrace.Mark(value);
        if (baseCache != null && FrameBytes + (long)layout.PixelSize.Width * layout.PixelSize.Height * 4 <= MaxBufferBytes)
        {
            long builds = baseCache.Builds;
            if (baseCache.TryGet(renderer, layout, SceneState, wallpaper, GetIconBitmap(), out var background, stage))
            {
                if (animationClip != null && builds == baseCache.Builds &&
                    renderedLayout?.PixelSize == layout.PixelSize && renderedLayout.Dpi == layout.Dpi && renderedLayout.State == layout.State)
                    frameGraphics!.SetClip(animationClip, CombineMode.Intersect);
                frameGraphics!.CompositingMode = CompositingMode.SourceCopy;
                frameGraphics.DrawImageUnscaled(background!, Point.Empty);
                renderer.RenderEdgeAnimation(frameGraphics, layout, clock.Elapsed.TotalSeconds);
                renderedLayout = layout;
                return;
            }
        }
        else baseCache?.Invalidate();
        renderer.Render(frameGraphics!, layout, SceneState, wallpaper, GetIconBitmap(), clock.Elapsed.TotalSeconds, stage);
        renderedLayout = layout;
    }

    private void InvalidateAnimatedEdges()
    {
        if (PresentedLayout is not { } layout || layout.PixelSize != ClientSize) return;
        using var dirty = new Region(); dirty.MakeEmpty();
        float inset = 8 * DeviceDpi / 96f;
        bool active = false;
        foreach (var tile in layout.Tiles.Where(tile => tile.RowIndex >= 0 &&
            (tile.RowIndex == SceneState.Selected || tile.RowIndex == SceneState.Hover)).Take(2))
        {
            using var edge = new Region(tile.Bounds);
            var inner = RectangleF.Inflate(tile.Bounds, -inset, -inset);
            if (inner.Width > 0 && inner.Height > 0) edge.Exclude(inner);
            dirty.Union(edge); active = true;
        }
        if (!active) return;
        if (animationDirty == null) animationDirty = dirty.Clone(); else animationDirty.Union(dirty);
        Invalidate(dirty, false);
    }
    private void ClearAnimationDirty() { animationDirty?.Dispose(); animationDirty = null; }

    private void EnsureFrame(Size size, int dpi)
    {
        if (size.Width <= 0 || size.Height <= 0) throw new InvalidOperationException("FrameSize");
        if (frame != null && frame.Width >= size.Width && frame.Height >= size.Height && Math.Abs(frame.HorizontalResolution - dpi) < .5) return;
        int width = checked((size.Width + 255) / 256 * 256), height = checked((size.Height + 127) / 128 * 128);
        long bytes = checked((long)width * height * 4);
        if (baseCache != null && bytes + baseCache.RetainedBytes > MaxBufferBytes) baseCache.Invalidate();
        if (bytes > MaxBufferBytes) throw new InvalidOperationException("FrameBudget");
        // Drop old storage before growing. There is never an unbudgeted pair of
        // old and new full-size buffers held by this owner.
        ReleaseFrame();
        if (nativeDibEnabled)
        {
            nativeFrame = new NativeResizeFrame(new Size(width, height), dpi, MaxBufferBytes - (baseCache?.RetainedBytes ?? 0));
            frame = nativeFrame.Bitmap; frameGraphics = nativeFrame.Graphics;
        }
        else
        {
            frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            frame.SetResolution(dpi, dpi);
            frameGraphics = Graphics.FromImage(frame);
        }
    }

    private void ReleaseFrame()
    {
        if (nativeFrame != null) { nativeFrame.Dispose(); nativeFrame = null; }
        else { frameGraphics?.Dispose(); frame?.Dispose(); }
        frameGraphics = null; frame = null;
        preparedLayout = null; preparedCommitted = false;
        renderedLayout = null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int next = PresentedLayout?.Tiles.FirstOrDefault(tile => tile.Bounds.Contains(e.Location))?.RowIndex ?? -1;
        if (SceneState.Hover == next) return;
        SceneState.Hover = next; Changed(false);
    }
    protected override void OnMouseLeave(EventArgs e)
    { base.OnMouseLeave(e); if (SceneState.Hover != -1) { SceneState.Hover = -1; Changed(false); } }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || PresentedLayout is not { } layout) return;
        for (int i = 0; i < layout.Navigation.Length; i++)
            if (layout.Navigation[i].Contains(e.Location)) { SceneState.View = i; SceneState.Scroll = 0; Changed(true); return; }
        var tile = layout.Tiles.FirstOrDefault(value => value.Bounds.Contains(e.Location));
        if (tile != null && tile.RowIndex >= 0) { SceneState.Selected = tile.RowIndex; Changed(false); return; }
        var row = layout.ListBody.Contains(e.Location)
            ? layout.Rows.FirstOrDefault(value => value.Bounds.Contains(e.Location)) : null;
        if (row != null) { SceneState.Selected = row.RowIndex; Changed(false); }
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (PresentedLayout is not { } layout || !layout.ListPanel.Contains(e.Location) || nativeGrid != null) return;
        SceneState.Scroll = Math.Clamp(SceneState.Scroll - Math.Sign(e.Delta) * 3, 0, layout.MaxScroll);
        Changed(false);
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F2) { SetMode((ResizeNativeMode)(((int)NativeMode + 1) % 5)); return true; }
        if (keyData == Keys.F6) { ToggleDemo(); return true; }
        if (keyData == Keys.F7) { simulatedProgress = !simulatedProgress; UpdateCaption(); Invalidate(); return true; }
        if (keyData == Keys.Escape && sweep.Enabled) { sweep.Stop(); UpdateCaption(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    internal void SetWindowSize(bool minimum) { sweep.Stop(); Size = minimum ? smallSize : commonSize; UpdateCaption(); }
    internal void ToggleDemo()
    {
        if (sweep.Enabled) sweep.Stop();
        else { WindowState = FormWindowState.Normal; demoStarted = clock.Elapsed.TotalSeconds; sweepTicks = 0; sweep.Start(); }
        UpdateCaption();
    }
    private void SweepTick()
    {
        double elapsed = clock.Elapsed.TotalSeconds - demoStarted;
        if (elapsed >= 30) { sweep.Stop(); UpdateCaption(); PrintInfo(); return; }
        double phase = (.5 - .5 * Math.Cos(elapsed * Math.PI / 3));
        int width = smallSize.Width + (int)((commonSize.Width - smallSize.Width) * phase);
        int height = smallSize.Height + (int)((commonSize.Height - smallSize.Height) * (.5 - .5 * Math.Cos(elapsed * Math.PI / 4)));
        Size = new(width, height); sweepTicks++;
    }
    private void UpdateCaption()
    {
        string mode = NativeMode switch { ResizeNativeMode.Canvas => "A 纯画布", ResizeNativeMode.Text => "B 搜索框", ResizeNativeMode.Combo => "B 下拉框", ResizeNativeMode.Grid => "B 原生列表", _ => "B 原生组合" };
        Text = $"AsukaLens · 实时缩放原型 · {mode} · 合成数据 · F2切换 / F6尺寸演示" +
            (DiagnosticTrace != null ? "【诊断采样】" : "") +
            (coverageHook != null ? "【铺底候选】" : "") +
            (nativeDibEnabled ? "【DIB缓冲】" : "") +
            (sweep.Enabled ? "【程序驱动尺寸演示】" : "") + (FrameFailures > 0 ? "【曾有帧失败：未通过】" : "");
    }
    internal void PrintInfo()
    {
        var samples = paintMilliseconds.Take(Math.Min(paintSamples, paintMilliseconds.Length)).Order().ToArray();
        double p95 = samples.Length == 0 ? 0 : samples[(int)Math.Floor((samples.Length - 1) * .95)];
        Console.WriteLine(FormattableString.Invariant($"INFO resize-prototype mode={NativeMode}; frames={FrameCount}; client_composite_p95_ms={p95:F3}; owned_buffer_bytes={BufferBytes}; native_backdrop_bytes={nativeGrid?.BackdropBytes ?? 0}; child_controls={Controls.Count}; dpi={DeviceDpi}; initial_dpi={initialDpi}; drag_full_windows={DragFullWindows?.ToString() ?? "Unknown"}; demo_ticks={sweepTicks}; frame_failure={frameFailure ?? "None"}; frame_failures_total={FrameFailures}; native_drag_acceptance=separate; layout_native_display_cost=not_in_client_composite; display_fps=not_measured"));
        if (coverageHook != null)
        {
            Console.WriteLine($"COVERAGE root paints={coverageHook.PaintCount}; failures={coverageHook.Failures}; extra_owned_bytes=0");
            Console.WriteLine(FormattableString.Invariant($"PREPARE frames={preparationCount}; copied={preparedPresents}; failures={preparationFailures}; total_ms={preparationMilliseconds:F3}; timing=before-native-size-not-client-composite"));
            if (baseCache != null) Console.WriteLine($"BASE cache hits={baseCache.Hits}; builds={baseCache.Builds}; rejected={baseCache.Rejections}; bytes={baseCache.RetainedBytes}");
            var copies = publishMilliseconds.Take(Math.Min(publishSamples, publishMilliseconds.Length)).Order().ToArray();
            Console.WriteLine(FormattableString.Invariant($"PUBLISH dib={nativeDibEnabled}; samples={publishSamples}; native={nativePublishes}; fallback={nativePublishFallbacks}; p50_ms={(copies.Length == 0 ? 0 : copies[copies.Length / 2]):F3}; p95_ms={(copies.Length == 0 ? 0 : copies[(int)Math.Floor((copies.Length - 1) * .95)]):F3}; rejected_size={preparedSizeRejected}; rejected_dpi={preparedDpiRejected}; rejected_revision={preparedRevisionRejected}; scope=copy-path-not-total-frame"));
            foreach (var failure in copyFailures.OrderBy(pair => pair.Key)) Console.WriteLine($"COPY fallback_code={failure.Key}; count={failure.Value}");
            Console.WriteLine($"NATIVE_REPAIR child_print_copies={childPrintCopies}; exposed_copies={exposedCopies}; exposed_failures={exposedCopyFailures}; child_print_commits_root=false");
        }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromDC(IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr handle);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr handle, IntPtr dc);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !closing)
        {
            closing = true; motion.Dispose(); sweep.Dispose();
            DiagnosticTrace?.Mark("Closing"); DiagnosticTrace?.Dispose();
            coverageHook?.Dispose(); coverageHook = null;
            ClearAnimationDirty(); baseCache?.Dispose(); baseCache = null;
            ReleaseFrame(); PresentedLayout = pendingLayout = preparedLayout = null;
            renderer.Dispose(); iconBitmap?.Dispose(); iconBitmap = null;
            // Child controls reference the owned fonts until base.Dispose.
            base.Dispose(true);
            inputFont.Dispose(); headerFont.Dispose(); pathFont.Dispose();
            BackgroundImage = null; wallpaper.Dispose(); appIcon.Dispose();
            return;
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint update);
}
