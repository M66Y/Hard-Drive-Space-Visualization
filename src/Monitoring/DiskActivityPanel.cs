using System.Drawing.Drawing2D;
using System.Globalization;
using System.ComponentModel;

namespace DiskAtlas;

// UI-thread owned. Samples stay in this fixed, in-memory ring; no timer or disk I/O.
internal sealed class DiskActivityPanel : Control
{
    private readonly DiskActivitySample?[] history = new DiskActivitySample?[60];
    private int historyStart, historyCount;
    private DiskActivitySample? current;
    private DateTimeOffset? displayTime;
    private string message = "等待采样";
    private bool changingTargets, disposed;
    private readonly ComboBox targetBox;
    private readonly Button toggle;
    private readonly Font heading = Theme.Font(9, FontStyle.Bold);
    private readonly Font small = Theme.Font(8);
    private readonly Font value = Theme.Font(13, FontStyle.Bold);

    private sealed record TargetChoice(int? Number, string Label)
    {
        public override string ToString() => Label;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Expanded { get; private set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int? SelectedDiskNumber => (targetBox.SelectedItem as TargetChoice)?.Number;
    public event EventHandler? ExpansionChanged;
    public event EventHandler? TargetChanged;

    public DiskActivityPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        AccessibleName = "磁盘活动";
        AccessibleRole = AccessibleRole.Grouping;
        targetBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Font = small,
            FlatStyle = FlatStyle.Flat, IntegralHeight = true, DropDownWidth = 220,
            AccessibleName = "磁盘活动目标", AccessibleDescription = "跟随扫描目录，或选择整块物理磁盘。",
            TabIndex = 0
        };
        targetBox.Items.Add(new TargetChoice(null, "跟随扫描目录"));
        targetBox.SelectedIndex = 0;
        targetBox.SelectedIndexChanged += (_, _) =>
        {
            if (changingTargets || disposed) return;
            ResetHistory();
            TargetChanged?.Invoke(this, EventArgs.Empty);
        };
        toggle = new Button
        {
            Text = "展开", Font = small, FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Surface, ForeColor = Theme.Accent, TabIndex = 1,
            AccessibleName = "展开磁盘活动", UseVisualStyleBackColor = false
        };
        toggle.FlatAppearance.BorderSize = 1;
        toggle.Click += (_, _) =>
        {
            if (disposed) return;
            Expanded = !Expanded;
            toggle.Text = Expanded ? "收起" : "展开";
            toggle.AccessibleName = Expanded ? "收起磁盘活动" : "展开磁盘活动";
            ExpansionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        };
        Controls.Add(targetBox);
        Controls.Add(toggle);
        UpdateDescription();
    }

    public void SetTargets(IReadOnlyList<DiskActivityTarget> targets)
    {
        if (disposed) return;
        ArgumentNullException.ThrowIfNull(targets);
        int? previous = SelectedDiskNumber;
        changingTargets = true;
        targetBox.BeginUpdate();
        try
        {
            targetBox.Items.Clear();
            targetBox.Items.Add(new TargetChoice(null, "跟随扫描目录"));
            var seen = new HashSet<int>();
            foreach (var target in targets)
                if (target.DiskNumber >= 0 && seen.Add(target.DiskNumber))
                    targetBox.Items.Add(new TargetChoice(target.DiskNumber, target.Label));
            targetBox.SelectedIndex = 0;
            for (int i = 1; i < targetBox.Items.Count; i++)
                if (((TargetChoice)targetBox.Items[i]!).Number == previous) targetBox.SelectedIndex = i;
        }
        finally { targetBox.EndUpdate(); changingTargets = false; }
        if (previous != SelectedDiskNumber)
        {
            ResetHistory();
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }
        Invalidate();
    }

    public void SetSample(DiskActivitySample sample)
    {
        if (disposed) return;
        ArgumentNullException.ThrowIfNull(sample);
        if (displayTime.HasValue && displayTime.Value - sample.Timestamp > TimeSpan.FromSeconds(4)) return;
        if (historyCount > 0 && At(historyCount - 1).DiskNumber != sample.DiskNumber) ResetHistory();
        if (current != null && current.DiskNumber != sample.DiskNumber) ResetHistory();
        if (historyCount > 0 && sample.Timestamp <= At(historyCount - 1).Timestamp) return;
        while (historyCount > 0 && sample.Timestamp - At(0).Timestamp >= TimeSpan.FromSeconds(60))
        { history[historyStart] = null; historyStart = (historyStart + 1) % history.Length; historyCount--; }
        if (historyCount == history.Length)
        { history[historyStart] = null; historyStart = (historyStart + 1) % history.Length; historyCount--; }
        history[(historyStart + historyCount) % history.Length] = sample;
        historyCount++;
        current = sample;
        if (!displayTime.HasValue || sample.Timestamp > displayTime) displayTime = sample.Timestamp;
        message = sample.Status switch
        {
            DiskActivityStatus.Available => $"磁盘 {sample.DiskNumber} · 最近 60 秒",
            DiskActivityStatus.Waiting => "等待采样",
            _ => "磁盘活动暂不可用"
        };
        UpdateDescription();
        Invalidate();
    }

    public void SetMessage(string text)
    {
        if (disposed) return;
        ResetHistory();
        message = string.IsNullOrWhiteSpace(text) ? "等待采样" : text[..Math.Min(text.Length, 160)];
        UpdateDescription();
        Invalidate();
    }

    public void ResetHistory()
    {
        if (disposed) return;
        Array.Clear(history);
        historyStart = historyCount = 0;
        current = null;
        displayTime = null;
        message = "等待采样";
        UpdateDescription();
        Invalidate();
    }

    // Called by the visible host's 1 Hz UI clock, even if a native provider stalls.
    internal void AdvanceClock(DateTimeOffset now)
    {
        if (disposed) return;
        if (displayTime.HasValue && now < displayTime.Value)
        {
            ResetHistory();
            message = "时间变化 · 等待更新";
            UpdateDescription();
            return;
        }
        displayTime = now;
        while (historyCount > 0 && now - At(0).Timestamp >= TimeSpan.FromSeconds(60))
        { history[historyStart] = null; historyStart = (historyStart + 1) % history.Length; historyCount--; }
        if (current != null && now - current.Timestamp > TimeSpan.FromSeconds(4))
        {
            current = null;
            message = "采样延迟 · 等待更新";
        }
        UpdateDescription();
        Invalidate();
    }

    private DiskActivitySample At(int index) => history[(historyStart + index) % history.Length]!;
    private int S(float logical) => (int)Math.Round(logical * DeviceDpi / 96f);
    private static double? Valid(double? number, double max = double.MaxValue) =>
        number is { } value && double.IsFinite(value) && value >= 0 && value <= max ? value : null;
    private double? Metric(Func<DiskActivitySample, double?> getter, double max = double.MaxValue) =>
        current?.Status == DiskActivityStatus.Available ? Valid(getter(current), max) : null;
    private static string Number(double? number, string suffix, bool integer = false) =>
        number is { } value ? value.ToString(integer ? "0" : "0.0", CultureInfo.CurrentCulture) + suffix : "—";
    private string[] Values() =>
    [
        Number(Metric(sample => sample.ActivePercent, 100), "%", true),
        Number(Metric(sample => sample.ReadBytesPerSecond) / (1024 * 1024), " MiB/s"),
        Number(Metric(sample => sample.WriteBytesPerSecond) / (1024 * 1024), " MiB/s"),
        Number(Metric(sample => sample.ResponseMilliseconds), " 毫秒")
    ];
    private void UpdateDescription()
    {
        var values = Values();
        AccessibleDescription = $"整块磁盘，包含其他进程；{message}；活动时间 {values[0]}；读取速度 {values[1]}；写入速度 {values[2]}；平均响应 {values[3]}";
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (targetBox == null || toggle == null) return;
        toggle.Bounds = new Rectangle(Math.Max(0, Width - S(62)), S(3), S(52), S(24));
        targetBox.Bounds = new Rectangle(Math.Max(0, toggle.Left - S(118)), S(4), S(110), S(23));
    }
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        PerformLayout();
        Invalidate();
    }

    private List<PointF[]> BuildSegments(RectangleF chart)
    {
        var result = new List<PointF[]>();
        if (historyCount == 0) return result;
        var segment = new List<PointF>(60);
        DiskActivitySample? previous = null;
        DateTimeOffset end = displayTime ?? At(historyCount - 1).Timestamp;
        void Flush() { if (segment.Count > 0) result.Add(segment.ToArray()); segment.Clear(); }
        for (int i = 0; i < historyCount; i++)
        {
            var sample = At(i);
            double? active = sample.Status == DiskActivityStatus.Available ? Valid(sample.ActivePercent, 100) : null;
            if (active == null) { Flush(); previous = null; continue; }
            if (previous != null && (sample.Timestamp - previous.Timestamp > TimeSpan.FromSeconds(2.5) ||
                sample.DiskNumber != previous.DiskNumber)) Flush();
            segment.Add(new PointF(chart.Right - chart.Width * (float)(end - sample.Timestamp).TotalSeconds / 60,
                chart.Bottom - chart.Height * (float)active.Value / 100));
            previous = sample;
        }
        Flush();
        return result;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (disposed || Width < 2 || Height < 2) return;
        base.OnPaint(e);
        bool contrast = SystemInformation.HighContrast;
        Color surface = contrast ? SystemColors.Window : Theme.Surface;
        Color text = contrast ? SystemColors.WindowText : Theme.Text;
        Color muted = contrast ? SystemColors.WindowText : Theme.Muted;
        Color accent = contrast ? SystemColors.Highlight : Theme.Accent;
        Color borderColor = contrast ? SystemColors.WindowText : Theme.Border;
        if (BackColor != surface) BackColor = surface;
        targetBox.BackColor = toggle.BackColor = surface;
        targetBox.ForeColor = text;
        toggle.ForeColor = accent;
        toggle.FlatAppearance.BorderColor = borderColor;
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(borderColor);
        using var outline = Theme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), S(9));
        graphics.DrawPath(border, outline);
        DrawText(graphics, "磁盘活动", heading, text, new Rectangle(S(14), S(3), S(82), S(24)));
        int textEnd = targetBox.Left - S(10);
        DrawText(graphics, "整块磁盘，包含其他进程", small, muted,
            new Rectangle(S(104), S(3), Math.Max(0, Math.Min(S(192), textEnd - S(104))), S(24)));
        if (textEnd > S(340))
        {
            string summary = !Expanded && current?.Status == DiskActivityStatus.Available
                ? $"{Values()[0]} · 读 {Values()[1]}" : message;
            DrawText(graphics, summary, small, muted,
                new Rectangle(S(306), S(3), Math.Max(0, textEnd - S(306)), S(24)));
        }
        if (!Expanded) return;
        float logicalWidth = Width * 96f / DeviceDpi;
        float chartRight = Width * (logicalWidth < 900 ? .34f : .44f);
        var chart = new RectangleF(S(14), S(35), Math.Max(S(50), chartRight - S(30)), S(47));
        using var grid = new Pen(contrast ? borderColor : Theme.Mix(borderColor, surface, .4f));
        for (int i = 0; i <= 4; i++)
        {
            float y = chart.Top + chart.Height * i / 4;
            graphics.DrawLine(grid, chart.Left, y, chart.Right, y);
        }
        for (int i = 0; i <= 6; i++)
        {
            float x = chart.Left + chart.Width * i / 6;
            graphics.DrawLine(grid, x, chart.Top, x, chart.Bottom);
        }
        using var line = new Pen(accent, 1.5f * DeviceDpi / 96f);
        using var fill = new SolidBrush(Color.FromArgb(contrast ? 0 : 30, accent));
        foreach (var points in BuildSegments(chart))
        {
            if (points.Length == 1)
            {
                using var dot = new SolidBrush(accent);
                graphics.FillEllipse(dot, points[0].X - S(1), points[0].Y - S(1), S(2), S(2));
                continue;
            }
            using var area = new GraphicsPath();
            area.AddLines(points);
            area.AddLine(points[^1], new PointF(points[^1].X, chart.Bottom));
            area.AddLine(new PointF(points[^1].X, chart.Bottom), new PointF(points[0].X, chart.Bottom));
            area.CloseFigure();
            graphics.FillPath(fill, area);
            graphics.DrawLines(line, points);
        }
        DrawText(graphics, current == null ? "最近 60 秒 · 活动时间" : $"磁盘 {current.DiskNumber} · 最近 60 秒", small, muted,
            new Rectangle(S(14), S(82), Math.Max(0, (int)chart.Width), S(18)));
        string[] labels = ["活动时间", "读取速度", "写入速度", "平均响应"];
        string[] values = Values();
        float start = chartRight + S(14), cell = Math.Max(0, (Width - start - S(12)) / 4);
        for (int i = 0; i < labels.Length; i++)
        {
            float x = start + cell * i;
            if (i > 0) graphics.DrawLine(grid, x - S(10), S(40), x - S(10), S(87));
            DrawText(graphics, labels[i], small, muted,
                new Rectangle((int)x, S(35), Math.Max(0, (int)cell - S(10)), S(21)));
            DrawText(graphics, values[i], value, i == 0 ? accent : text,
                new Rectangle((int)x, S(57), Math.Max(0, (int)cell - S(10)), S(30)));
        }
    }

    private static void DrawText(Graphics graphics, string text, Font font, Color color, Rectangle bounds)
    {
        if (bounds.Width > 0 && bounds.Height > 0) Theme.TextAt(graphics, text, font, color, bounds);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            Array.Clear(history);
            current = null;
            ExpansionChanged = null;
            TargetChanged = null;
            heading.Dispose(); small.Dispose(); value.Dispose();
        }
        base.Dispose(disposing);
    }
}
