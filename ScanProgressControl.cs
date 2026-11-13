using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace DiskAtlas;

internal enum ScanDisplayState { Idle, Running, Completed, Incomplete, Cancelled, Failed }

// A bounded UI-only pulse denotes unknown work; it never changes the estimate.
internal sealed class ScanProgressControl : Control
{
    private readonly System.Windows.Forms.Timer pulse = new() { Interval = 50 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ToolTip tip = new() { AutoPopDelay = 20000 };
    private ScanProgress? latest;
    private bool animationsEnabled = true;
    private Bitmap? marker;
    private int markerPixels;
    internal static Color TrackStart => Theme.ProgressPalette.Start;
    internal static Color TrackEnd => Theme.ProgressPalette.End;
    internal const string Explanation = "当前阶段估算 = 已处理目录任务 / 当前已发现目录任务；包含根目录。\n新目录出现时估算可能下降；核验与读取/复用分别计数，整理阶段沿用读取计数。\n不代表文件、字节或剩余时间比例。核验以旧快照目录数为分母，整个缓存子树复用算一个任务。\n统计整理结束前最多 99%；总量未知时显示往返指示。取消、失败或结果不完整不显示成功的 100%。";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AnimationsEnabled
    {
        get => animationsEnabled;
        set { animationsEnabled = value; RefreshPulse(); Invalidate(); }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ScanDisplayState State { get; private set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int? Percent { get; private set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string StageText { get; private set; } = "选择文件夹开始扫描";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PercentText => State == ScanDisplayState.Completed ? "100%" : State == ScanDisplayState.Running
        ? Percent is int p ? $"估算 {p}%" : "总量未知" : "—";
    internal bool PulseRunning => pulse.Enabled;

    public ScanProgressControl()
    {
        DoubleBuffered = true; TabStop = false; Margin = Padding.Empty;
        SetStyle(ControlStyles.ResizeRedraw, true);
        // Generic controls expose a spurious editable numeric range for ProgressBar.
        // Publish the actual phase/estimate as read-only text, including unknown work.
        AccessibleRole = AccessibleRole.StaticText;
        tip.SetToolTip(this, Explanation);
        pulse.Tick += (_, _) => Invalidate();
        UpdateAccessibility();
    }
    public void BeginScan()
    {
        latest = null; State = ScanDisplayState.Running; Percent = null;
        StageText = "准备扫描"; Changed();
    }
    public void Report(ScanProgress value)
    {
        // Queued callbacks are also guarded by the form's scan identity.
        if (State != ScanDisplayState.Running) return;
        latest = value; Percent = value.EstimatedPercent;
        StageText = PhaseName(value.Phase); Changed();
    }
    public void Finish(ScanDisplayState state)
    {
        if (state is ScanDisplayState.Running or ScanDisplayState.Idle) throw new ArgumentOutOfRangeException(nameof(state));
        State = state; Percent = state == ScanDisplayState.Completed ? 100 : null;
        StageText = state switch
        {
            ScanDisplayState.Completed => "扫描完成",
            ScanDisplayState.Incomplete => "扫描结束 · 结果不完整，请查看未读取项",
            ScanDisplayState.Cancelled => "扫描已取消",
            _ => "扫描失败 · 未完成"
        };
        Changed();
    }
    internal static string PhaseName(ScanPhase phase) => phase switch
    {
        ScanPhase.Preparing => "检查刷新条件",
        ScanPhase.Verifying => "核验元信息", ScanPhase.Reusing => "读取与复用缓存",
        ScanPhase.Finalizing => "统计与整理", _ => "读取目录"
    };
    private string Description => State == ScanDisplayState.Running && latest is { DiscoveredDirectories: > 0 } p && Percent != null
        ? $"{StageText} · 当前阶段 {p.CompletedDirectories:N0} / {p.DiscoveredDirectories:N0} 个目录任务"
        : StageText;
    private void UpdateAccessibility()
    {
        AccessibleName = $"{Description} · {PercentText}";
        AccessibleDescription = Explanation;
    }
    private void Changed() { UpdateAccessibility(); RefreshPulse(); Invalidate(); }
    private void RefreshPulse() => pulse.Enabled = !IsDisposed && Visible && AnimationsEnabled && State == ScanDisplayState.Running && Percent == null;
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); RefreshPulse(); }
    protected override void OnPaintBackground(PaintEventArgs e) => Wallpaper.Paint(this, e.Graphics, 165);
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 40 || Height < 12) return;
        float s = DeviceDpi / 96f;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = Theme.Font(Theme.CaptionFontSize);
        int rightWidth = Math.Min(Width / 3, (int)(110 * s));
        int textHeight = Math.Min(Height, (int)(20 * s));
        Theme.TextAt(g, Description, font, Theme.Text, new Rectangle(0, 0, Math.Max(0, Width - rightWidth - (int)(10 * s)), textHeight));
        Theme.TextAt(g, PercentText, font, Theme.Accent, new Rectangle(Width - rightWidth, 0, rightWidth, textHeight), TextFormatFlags.Right);
        float pad = 15 * s, y = Math.Min(Height - 14 * s, 34 * s);
        var track = new RectangleF(pad, y - 3 * s, Math.Max(1, Width - 2 * pad), 6 * s);
        using var path = Theme.Round(track, 3 * s);
        using var border = new Pen(Theme.Border, s);
        g.DrawPath(border, path); // No fill on the unprocessed part: wallpaper stays visible.
        bool running = State == ScanDisplayState.Running;
        float end = track.Left;
        using var fill = new LinearGradientBrush(track, TrackStart, TrackEnd, LinearGradientMode.Horizontal);
        if (Percent is int percent)
        {
            float width = track.Width * percent / 100f;
            if (width > 0) { using var done = Theme.Round(new RectangleF(track.X, track.Y, width, track.Height), 3 * s); g.FillPath(fill, done); }
            end += width;
        }
        else if (running)
        {
            // Short travelling segment, never a filled percentage.
            float t = AnimationsEnabled ? (float)(.5 - .5 * Math.Cos(clock.Elapsed.TotalSeconds * Math.PI)) : .5f;
            float width = Math.Min(track.Width, 45 * s);
            float start = track.Left + (track.Width - width) * t;
            using var segment = Theme.Round(new RectangleF(start, track.Y, width, track.Height), 3 * s);
            g.FillPath(fill, segment); end = start + width;
        }
        if (running || State == ScanDisplayState.Completed)
        {
            // Reuse the actual app artwork; decode/resize only when the DPI changes.
            int pixels = Math.Clamp((int)Math.Round(26 * s), 16, 96);
            var avatar = GetMarker(pixels);
            using var halo = new SolidBrush(Color.FromArgb(215, Theme.Surface));
            g.FillEllipse(halo, end - pixels / 2f, y - pixels / 2f, pixels, pixels);
            g.DrawImageUnscaled(avatar, (int)Math.Round(end - pixels / 2f), (int)Math.Round(y - pixels / 2f));
        }
    }
    private Bitmap GetMarker(int pixels)
    {
        if (marker != null && markerPixels == pixels) return marker;
        using var source = typeof(ScanProgressControl).Assembly.GetManifestResourceStream("DiskAtlas.AppIcon")
            ?? throw new InvalidOperationException("Missing app icon resource.");
        using var icon = new Icon(source, pixels, pixels);
        using var bitmap = icon.ToBitmap();
        var next = new Bitmap(pixels, pixels, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(next))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bitmap, new Rectangle(0, 0, pixels, pixels));
        }
        marker?.Dispose(); marker = next; markerPixels = pixels; return next;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { pulse.Dispose(); tip.Dispose(); marker?.Dispose(); }
        base.Dispose(disposing);
    }
}
