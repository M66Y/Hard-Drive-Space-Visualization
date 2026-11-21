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

    internal ResizeSceneState SceneState { get; } = new();
    internal ResizeSceneLayout? PresentedLayout { get; private set; }
    internal long BufferBytes => frame == null ? 0 : (long)frame.Width * frame.Height * 4;
    internal int FrameCount { get; private set; }
    internal int FrameFailures { get; private set; }
    internal ResizeNativeMode NativeMode { get; private set; }
    internal bool IsDemoRunning => sweep.Enabled;
    internal string? FrameFailure => frameFailure;
    internal bool? DragFullWindows { get; }

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
            if (SceneState.Selected >= 0 || SceneState.Hover >= 0 || simulatedProgress) Invalidate();
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
        if (nativeStrategy != null) nativeStrategy.Bounds = ComboBounds(layout.Strategy);
        if (nativeMetric != null) nativeMetric.Bounds = ComboBounds(layout.Metric);
        if (nativeGrid != null)
        {
            nativeGrid.Bounds = Rectangle.Union(layout.ListHeader, layout.ListBody);
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
        SceneState.Revision++; pendingLayout = null;
        if (ready) PrepareLayout(rebindGrid);
        Invalidate(true);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!ready || closing || WindowState == FormWindowState.Minimized || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        pendingLayout = null; PrepareLayout(); Invalidate(true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ReleaseFrame(); pendingLayout = null;
        if (ready && !closing) { PrepareLayout(); Invalidate(true); }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (closing || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        long began = Stopwatch.GetTimestamp();
        try
        {
            // Build only if a test changed SceneState directly; native bounds
            // are already settled by the normal OnResize/Changed path.
            var layout = pendingLayout;
            if (layout == null || layout.PixelSize != ClientSize || layout.Dpi != DeviceDpi || layout.Revision != SceneState.Revision)
                layout = pendingLayout = ResizeSceneLayout.Build(ClientSize, DeviceDpi, SceneState);
            EnsureFrame(ClientSize, DeviceDpi);
            frameGraphics!.ResetClip(); frameGraphics.ResetTransform();
            frameGraphics.SetClip(new Rectangle(Point.Empty, ClientSize));
            renderer.Render(frameGraphics, layout, SceneState, wallpaper, GetIconBitmap(), clock.Elapsed.TotalSeconds);
            // No scaling of a previous frame: copy matching pixels only after
            // this current-size scene has finished rendering.
            var targetState = e.Graphics.Save();
            try
            {
                e.Graphics.CompositingMode = CompositingMode.SourceCopy;
                e.Graphics.DrawImage(frame!, new Rectangle(Point.Empty, ClientSize),
                    new Rectangle(Point.Empty, ClientSize), GraphicsUnit.Pixel);
            }
            finally { e.Graphics.Restore(targetState); }
            PresentedLayout = layout; FrameCount++; frameFailure = null;
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
            paintMilliseconds[paintSamples++ % paintMilliseconds.Length] = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            paintTotal++;
        }
    }

    private Bitmap? iconBitmap;
    private Bitmap GetIconBitmap() => iconBitmap ??= appIcon.ToBitmap();

    private void EnsureFrame(Size size, int dpi)
    {
        if (size.Width <= 0 || size.Height <= 0) throw new InvalidOperationException("FrameSize");
        if (frame != null && frame.Width >= size.Width && frame.Height >= size.Height && Math.Abs(frame.HorizontalResolution - dpi) < .5) return;
        int width = checked((size.Width + 255) / 256 * 256), height = checked((size.Height + 127) / 128 * 128);
        long bytes = checked((long)width * height * 4);
        if (bytes > MaxBufferBytes) throw new InvalidOperationException("FrameBudget");
        // Drop old storage before growing. There is never an unbudgeted pair of
        // old and new full-size buffers held by this owner.
        ReleaseFrame();
        frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        frame.SetResolution(dpi, dpi);
        frameGraphics = Graphics.FromImage(frame);
    }

    private void ReleaseFrame()
    { frameGraphics?.Dispose(); frameGraphics = null; frame?.Dispose(); frame = null; }

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
            (sweep.Enabled ? "【程序驱动尺寸演示】" : "") + (FrameFailures > 0 ? "【曾有帧失败：未通过】" : "");
    }
    internal void PrintInfo()
    {
        var samples = paintMilliseconds.Take(Math.Min(paintSamples, paintMilliseconds.Length)).Order().ToArray();
        double p95 = samples.Length == 0 ? 0 : samples[(int)Math.Floor((samples.Length - 1) * .95)];
        Console.WriteLine(FormattableString.Invariant($"INFO resize-prototype mode={NativeMode}; frames={FrameCount}; client_composite_p95_ms={p95:F3}; owned_buffer_bytes={BufferBytes}; native_backdrop_bytes={nativeGrid?.BackdropBytes ?? 0}; child_controls={Controls.Count}; dpi={DeviceDpi}; initial_dpi={initialDpi}; drag_full_windows={DragFullWindows?.ToString() ?? "Unknown"}; demo_ticks={sweepTicks}; frame_failure={frameFailure ?? "None"}; frame_failures_total={FrameFailures}; native_drag_acceptance=separate; layout_native_display_cost=not_in_client_composite; display_fps=not_measured"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !closing)
        {
            closing = true; motion.Dispose(); sweep.Dispose();
            ReleaseFrame(); PresentedLayout = pendingLayout = null;
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
