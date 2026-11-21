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
    private ScanProgress? latest;
    private bool animationsEnabled = true;
    private Bitmap? marker;
    private int markerPixels;
    private Bitmap? markerGlow;
    private int glowPixels, glowColor;
    private bool resourcesDisposed;
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
    internal bool PulseRunning => !resourcesDisposed && pulse.Enabled;
    internal long GlowBytes => markerGlow == null ? 0 : (long)markerGlow.Width * markerGlow.Height * 4;
    private string detailText = "";
    internal void SetDetailText(string value)
    {
        string next = value.Length <= 4096 ? value : value[..4095] + "…";
        if (detailText == next) return;
        detailText = next;
        UpdateAccessibility();
    }
    internal (Rectangle Stage, Rectangle Percent, RectangleF Track) ContentBands
    {
        get
        {
            float scale = DeviceDpi / 96f;
            int pad = (int)Math.Ceiling(15 * scale), right = Math.Min(Width / 3, (int)(110 * scale));
            int height = Math.Min(Height, (int)(20 * scale));
            float y = Math.Min(Height - 14 * scale, 34 * scale);
            return (new Rectangle(pad, 0, Math.Max(0, Width - 2 * pad - right - (int)(10 * scale)), height),
                new Rectangle(Math.Max(pad, Width - pad - right), 0, right, height),
                new RectangleF(pad, y - 3 * scale, Math.Max(1, Width - 2 * pad), 6 * scale));
        }
    }

    public ScanProgressControl()
    {
        DoubleBuffered = true; TabStop = false; Margin = Padding.Empty;
        SetStyle(ControlStyles.ResizeRedraw, true);
        // Generic controls expose a spurious editable numeric range for ProgressBar.
        // Publish the actual phase/estimate as read-only text, including unknown work.
        AccessibleRole = AccessibleRole.StaticText;
        pulse.Tick += (_, _) => { RefreshPulse(); if (PulseRunning) Invalidate(); };
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
        AccessibleDescription = Explanation + (detailText.Length == 0 ? "" : "\n\n" + detailText);
    }
    private void Changed() { UpdateAccessibility(); RefreshPulse(); Invalidate(); }
    private void RefreshPulse()
    {
        if (resourcesDisposed) return;
        pulse.Enabled = !IsDisposed && Visible && FindForm()?.WindowState != FormWindowState.Minimized &&
            AnimationsEnabled && State == ScanDisplayState.Running && Percent == null;
    }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); RefreshPulse(); }
    protected override void OnPaintBackground(PaintEventArgs e) => Wallpaper.Paint(this, e.Graphics, 165);
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        RefreshPulse();
        if (Width < 40 || Height < 12) return;
        float s = DeviceDpi / 96f;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = Theme.Font(Theme.CaptionFontSize);
        var bands = ContentBands;
        var textFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        Theme.TextAt(g, Description, font, Theme.Text, bands.Stage, textFlags);
        Theme.TextAt(g, PercentText, font, Theme.Accent, bands.Percent, textFlags | TextFormatFlags.Right);
        float position = AnimationsEnabled ? (float)(.5 - .5 * Math.Cos(clock.Elapsed.TotalSeconds * Math.PI)) : .5f;
        PaintTrack(g, bands.Track, s, position, SystemInformation.HighContrast);
    }

    // A fixed-position rendering entry is shared with the owned-bitmap tests;
    // it never changes the measured percentage or advances scan work.
    internal void PaintTrack(Graphics g, RectangleF track, float scale, float unknownPosition, bool highContrast)
    {
        if (resourcesDisposed || track.Width <= 0 || track.Height <= 0 || !float.IsFinite(scale) || scale <= 0 ||
            !float.IsFinite(unknownPosition)) return;
        using var path = Theme.Round(track, 3 * scale);
        using var border = new Pen(highContrast ? SystemColors.WindowText : Theme.Border, scale);
        g.DrawPath(border, path); // No fill on the unprocessed part: wallpaper stays visible.
        bool running = State == ScanDisplayState.Running;
        float end = track.Left;
        if (Percent is int percent)
        {
            float width = track.Width * percent / 100f;
            if (width > 0)
            {
                using var done = Theme.Round(new RectangleF(track.X, track.Y, width, track.Height), 3 * scale);
                using Brush fill = highContrast ? new SolidBrush(SystemColors.Highlight)
                    : new LinearGradientBrush(track, TrackStart, TrackEnd, LinearGradientMode.Horizontal);
                g.FillPath(fill, done);
            }
            end += width;
        }
        else if (running)
        {
            // Unknown work has no filled tail or implied percentage. Only the
            // artwork and its symmetric glow travel along the transparent rail.
            end = track.Left + track.Width * Math.Clamp(unknownPosition, 0, 1);
        }
        if (running || State == ScanDisplayState.Completed)
        {
            // Reuse the actual app artwork; decode/resize only when the DPI changes.
            int pixels = Math.Clamp((int)Math.Round(26 * scale), 16, 96);
            var avatar = GetMarker(pixels);
            float y = track.Top + track.Height / 2;
            if (highContrast)
            {
                using var cue = new Pen(SystemColors.Highlight, Math.Max(1, scale));
                g.DrawEllipse(cue, end - pixels / 2f - scale, y - pixels / 2f - scale,
                    pixels + 2 * scale, pixels + 2 * scale);
            }
            else
            {
                var glow = GetMarkerGlow(pixels);
                // The short progress row has less vertical room than horizontal
                // room; a symmetric soft ellipse keeps the glow within its row.
                g.DrawImage(glow, new RectangleF(end - pixels * .80f, y - pixels * .52f,
                    pixels * 1.60f, pixels * 1.04f));
            }
            g.DrawImageUnscaled(avatar, (int)Math.Round(end - pixels / 2f), (int)Math.Round(y - pixels / 2f));
        }
    }

    private Bitmap GetMarkerGlow(int pixels)
    {
        pixels = Math.Clamp(pixels, 16, 96);
        Color warm = Theme.Mix(TrackEnd, Color.FromArgb(255, 217, 169), .70f);
        if (markerGlow != null && glowPixels == pixels && glowColor == warm.ToArgb()) return markerGlow;
        int size = pixels * 2;
        var next = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            float radius = (size - 1) / 2f;
            // This tiny Gaussian-like texture is generated only on size/theme
            // changes, not for each animation tick. Its outer edge is transparent.
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - radius) / radius, dy = (y - radius) / radius;
                float distanceSquared = dx * dx + dy * dy;
                int alpha = distanceSquared >= 1 ? 0 : (int)Math.Round(168 * Math.Exp(-4.5 * distanceSquared) * (1 - distanceSquared));
                next.SetPixel(x, y, Color.FromArgb(alpha, warm));
            }
        }
        catch { next.Dispose(); throw; }
        markerGlow?.Dispose(); markerGlow = next; glowPixels = pixels; glowColor = warm.ToArgb();
        return next;
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
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            pulse.Dispose(); marker?.Dispose(); marker = null;
            markerGlow?.Dispose(); markerGlow = null;
        }
        base.Dispose(disposing);
    }
}
