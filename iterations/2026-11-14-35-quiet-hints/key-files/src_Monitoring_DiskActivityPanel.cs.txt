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
    private Button? details;
    private ToolTip? sidebarTip;
    private bool sidebarMode;
    private readonly bool viewOnly;
    private bool mirroredEnabled;
    private readonly Font heading = Theme.Font(Theme.ControlFontSize, FontStyle.Bold);
    private readonly Font small = Theme.Font(Theme.CaptionFontSize);
    private readonly Font value = Theme.Font(14, FontStyle.Bold);

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
    public event EventHandler? DetailsRequested;
    public event EventHandler? StateChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool SidebarMode
    {
        get => sidebarMode;
        set
        {
            if (sidebarMode == value) return;
            if (IsHandleCreated || viewOnly) throw new InvalidOperationException("Choose the sidebar presentation before creating its handle.");
            sidebarMode = value;
            targetBox.Visible = !value;
            if (value && details == null)
            {
                details = new Button { Text = "详情", Font = small, FlatStyle = FlatStyle.Flat, TabIndex = 2,
                    BackColor = Theme.Surface, ForeColor = Theme.Accent, UseVisualStyleBackColor = false,
                    AccessibleName = "磁盘活动详情" };
                details.Click += (_, _) => { if (!disposed) DetailsRequested?.Invoke(this, EventArgs.Empty); };
                Controls.Add(details);
                sidebarTip = AppToolTips.Create();
                sidebarTip.SetToolTip(details, "查看同一份磁盘活动采样的完整曲线和指标。");
            }
            if (details != null) details.Visible = value;
            UpdateToggle(); UpdateDescription(); PerformLayout(); Invalidate();
        }
    }
    internal Button ToggleButton => toggle;
    internal Button? DetailsButton => details;
    internal ComboBox TargetSelector => targetBox;

    public DiskActivityPanel() : this(false) { }
    internal DiskActivityPanel(bool viewOnly)
    {
        this.viewOnly = viewOnly;
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
            if (disposed || viewOnly) return;
            Expanded = !Expanded;
            UpdateToggle();
            ExpansionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
            NotifyStateChanged();
        };
        Controls.Add(targetBox);
        Controls.Add(toggle);
        if (viewOnly) { Expanded = true; toggle.Visible = false; }
        UpdateDescription();
    }

    private void UpdateToggle()
    {
        toggle.Text = sidebarMode ? Expanded ? "暂停" : "启用" : Expanded ? "收起" : "展开";
        toggle.AccessibleName = sidebarMode ? Expanded ? "暂停磁盘活动" : "启用磁盘活动" : Expanded ? "收起磁盘活动" : "展开磁盘活动";
    }
    private void NotifyStateChanged() { if (!disposed && !viewOnly) StateChanged?.Invoke(this, EventArgs.Empty); }

    internal void SelectDisk(int? number)
    {
        if (disposed) return;
        int index = 0;
        if (number.HasValue)
            for (int i = 1; i < targetBox.Items.Count; i++)
                if (((TargetChoice)targetBox.Items[i]!).Number == number) { index = i; break; }
        targetBox.SelectedIndex = index;
    }

    internal sealed record ViewSnapshot(IReadOnlyList<DiskActivitySample> History, DiskActivitySample? Current,
        DateTimeOffset? DisplayTime, string Message, IReadOnlyList<DiskActivityTarget> Targets, int? SelectedDiskNumber, bool Enabled);

    internal ViewSnapshot GetViewSnapshot()
    {
        var samples = new DiskActivitySample[historyCount];
        for (int i = 0; i < samples.Length; i++) samples[i] = At(i);
        var targets = new List<DiskActivityTarget>(Math.Min(64, targetBox.Items.Count));
        DiskActivityTarget Target(TargetChoice choice) => new(choice.Number!.Value, choice.Label.Length > 160 ? choice.Label[..160] : choice.Label);
        for (int i = 1; i < targetBox.Items.Count && targets.Count < 64; i++) targets.Add(Target((TargetChoice)targetBox.Items[i]!));
        if (targetBox.SelectedItem is TargetChoice { Number: not null } selected && targets.All(item => item.DiskNumber != selected.Number))
        {
            if (targets.Count == 64) targets.RemoveAt(63);
            targets.Add(Target(selected));
        }
        return new(Array.AsReadOnly(samples), current, displayTime, message, targets.AsReadOnly(), SelectedDiskNumber,
            viewOnly ? mirroredEnabled : Expanded);
    }

    internal void ApplyViewSnapshot(ViewSnapshot snapshot)
    {
        if (!viewOnly) throw new InvalidOperationException("Only an activity detail view accepts a mirrored snapshot.");
        if (disposed) return;
        if (snapshot.History.Count > 60 || snapshot.Targets.Count > 64 || snapshot.Message.Length > 160 ||
            snapshot.Targets.Any(target => target.DiskNumber < 0 || target.Label.Length > 160))
            throw new ArgumentException("Invalid activity view snapshot.", nameof(snapshot));
        changingTargets = true;
        try
        {
            bool sameTargets = targetBox.Items.Count == snapshot.Targets.Count + 1;
            for (int i = 0; sameTargets && i < snapshot.Targets.Count; i++)
            {
                var old = (TargetChoice)targetBox.Items[i + 1]!;
                var next = snapshot.Targets[i];
                sameTargets = old.Number == next.DiskNumber && old.Label == next.Label;
            }
            if (!sameTargets)
            {
                targetBox.BeginUpdate();
                try
                {
                    targetBox.Items.Clear(); targetBox.Items.Add(new TargetChoice(null, "跟随扫描目录"));
                    foreach (var target in snapshot.Targets) targetBox.Items.Add(new TargetChoice(target.DiskNumber, target.Label));
                }
                finally { targetBox.EndUpdate(); }
            }
            if (targetBox.SelectedIndex < 0 || SelectedDiskNumber != snapshot.SelectedDiskNumber) SelectDisk(snapshot.SelectedDiskNumber);
        }
        finally { changingTargets = false; }
        Array.Clear(history); historyStart = 0; historyCount = snapshot.History.Count;
        for (int i = 0; i < historyCount; i++) history[i] = snapshot.History[i];
        current = snapshot.Current; displayTime = snapshot.DisplayTime; message = snapshot.Message; mirroredEnabled = snapshot.Enabled;
        UpdateDescription(); Invalidate();
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
        NotifyStateChanged();
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
        NotifyStateChanged();
    }

    public void SetMessage(string text)
    {
        if (disposed) return;
        ResetHistory();
        message = string.IsNullOrWhiteSpace(text) ? "等待采样" : text[..Math.Min(text.Length, 160)];
        UpdateDescription();
        Invalidate();
        NotifyStateChanged();
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
        NotifyStateChanged();
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
            NotifyStateChanged();
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
        NotifyStateChanged();
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
        if (sidebarMode)
        {
            int top = S(1), height = Math.Min(S(24), Height);
            if (details != null) details.Bounds = new Rectangle(Math.Max(0, Width - S(50)), top, S(42), height);
            toggle.Bounds = new Rectangle(Math.Max(0, Width - S(96)), top, S(42), height);
            return;
        }
        int headerHeight = Math.Min(Height, S(30));
        int buttonHeight = Math.Min(headerHeight, S(24));
        toggle.Bounds = new Rectangle(Math.Max(0, Width - S(62)), Math.Max(0, (headerHeight - buttonHeight) / 2), S(52), buttonHeight);
        // A native DropDownList chooses its own height from its font and border.
        // Center that actual preferred height instead of forcing a 23 px box
        // whose final native height can cross the 30 px row at 96 DPI.
        int targetHeight = targetBox.PreferredHeight;
        targetBox.Bounds = new Rectangle(Math.Max(0, toggle.Left - S(118)), Math.Max(0, (headerHeight - targetHeight) / 2), S(110), targetHeight);
    }
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        PerformLayout();
        Invalidate();
    }

    internal readonly record struct MetricBounds(Rectangle Label, Rectangle Value);
    internal sealed record ExpandedLayout(RectangleF Chart, Rectangle Caption, Rectangle Body,
        Rectangle MetricArea, IReadOnlyList<MetricBounds> Metrics, bool TwoRows);

    // The paint path and UI checks share the actual bounds. Fonts and sampling
    // remain independent of whether the host can offer the full expanded height.
    internal ExpandedLayout ExpandedGeometry()
    {
        int top = S(36);
        bool twoRows = Height >= S(172);
        float logicalWidth = Width * 96f / DeviceDpi;
        int split = (int)Math.Round(Width * (twoRows ? .54f : logicalWidth < 900 ? .34f : .44f));
        int chartBottom = Math.Max(top, Height - S(32));
        var chart = new RectangleF(S(14), top, Math.Max(0, split - S(30)), Math.Max(0, chartBottom - top));
        var body = new Rectangle(S(14), top, Math.Max(0, Width - S(28)), Math.Max(0, Height - S(6) - top));
        int captionTop = chartBottom + S(4);
        var caption = new Rectangle(S(14), captionTop, (int)chart.Width,
            Math.Max(0, Math.Min(S(22), Height - S(6) - captionTop)));
        int metricLeft = split + S(14);
        var metricArea = new Rectangle(metricLeft, top, Math.Max(0, Width - S(12) - metricLeft),
            Math.Max(0, Height - S(12) - top));
        int columns = twoRows ? 2 : 4, rows = twoRows ? 2 : 1;
        var metrics = new MetricBounds[4];
        int labelHeight = S(20), gap = S(4), valueHeight = S(28);
        for (int i = 0; i < metrics.Length; i++)
        {
            int column = i % columns, row = i / columns;
            int left = metricArea.Left + metricArea.Width * column / columns;
            int right = metricArea.Left + metricArea.Width * (column + 1) / columns;
            int rowTop = metricArea.Top + metricArea.Height * row / rows;
            int rowBottom = metricArea.Top + metricArea.Height * (row + 1) / rows;
            int blockTop = rowTop + Math.Max(0, (rowBottom - rowTop - labelHeight - gap - valueHeight) / 2);
            int width = Math.Max(0, right - left - S(10));
            var label = new Rectangle(left, blockTop, width, Math.Min(labelHeight, Math.Max(0, rowBottom - blockTop)));
            int valueTop = Math.Min(rowBottom, label.Bottom + gap);
            var number = new Rectangle(left, valueTop, width, Math.Min(valueHeight, Math.Max(0, rowBottom - valueTop)));
            metrics[i] = new(label, number);
        }
        return new(chart, caption, body, metricArea, metrics, twoRows);
    }

    internal readonly record struct SidebarLayout(RectangleF Chart, Rectangle ActivityLabel, Rectangle ActivityValue,
        Rectangle Read, Rectangle Write, bool ShowsCurve);
    internal SidebarLayout SidebarGeometry()
    {
        bool show = Expanded && Height >= S(100);
        if (!show) return new(RectangleF.Empty, Rectangle.Empty, Rectangle.Empty, Rectangle.Empty, Rectangle.Empty, false);
        var chart = new RectangleF(S(8), S(32), Math.Max(0, Width * .58f - S(16)), Math.Max(0, Height - S(72)));
        int left = (int)Math.Ceiling(chart.Right) + S(6), width = Math.Max(0, Width - S(8) - left);
        int labelHeight = Height >= S(132) ? S(20) : 0;
        int blockHeight = labelHeight + (labelHeight > 0 ? S(2) : 0) + S(28);
        int top = (int)chart.Top + Math.Max(0, ((int)chart.Height - blockHeight) / 2);
        var label = new Rectangle(left, top, width, labelHeight);
        var number = new Rectangle(left, top + labelHeight + (labelHeight > 0 ? S(2) : 0), width, S(28));
        return new(chart, label, number, new Rectangle(S(8), Height - S(40), Math.Max(0, Width - S(16)), S(19)),
            new Rectangle(S(8), Height - S(20), Math.Max(0, Width - S(16)), S(19)), true);
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
        if (details != null)
        {
            details.BackColor = surface; details.ForeColor = accent; details.FlatAppearance.BorderColor = borderColor;
        }
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (sidebarMode)
        {
            PaintSidebar(graphics, text, muted, accent, borderColor, contrast, surface);
            return;
        }
        using var border = new Pen(borderColor);
        if (Expanded)
        {
            using var outline = Theme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), S(9));
            graphics.DrawPath(border, outline);
        }
        else graphics.DrawLine(border, S(14), Height - 1, Math.Max(S(14), Width - S(14)), Height - 1);
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
        var layout = ExpandedGeometry();
        var chart = layout.Chart;
        using var grid = new Pen(contrast ? borderColor : Theme.Mix(borderColor, surface, .4f));
        DrawCurve(graphics, chart, accent, grid.Color, contrast);
        DrawText(graphics, current == null ? "最近 60 秒 · 活动时间" : $"磁盘 {current.DiskNumber} · 最近 60 秒", small, muted,
            layout.Caption);
        string[] labels = ["活动时间", "读取速度", "写入速度", "平均响应"];
        string[] values = Values();
        for (int i = 0; i < labels.Length; i++)
        {
            var bounds = layout.Metrics[i];
            if (i > 0 && (!layout.TwoRows || i % 2 == 1))
                graphics.DrawLine(grid, bounds.Label.Left - S(10), bounds.Label.Top + S(4),
                    bounds.Label.Left - S(10), bounds.Value.Bottom - S(4));
            DrawText(graphics, labels[i], small, muted, bounds.Label);
            DrawText(graphics, values[i], value, i == 0 ? accent : text, bounds.Value);
        }
    }

    private void PaintSidebar(Graphics graphics, Color text, Color muted, Color accent, Color borderColor, bool contrast, Color surface)
    {
        using var border = new Pen(borderColor);
        using var outline = Theme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), S(7));
        graphics.DrawPath(border, outline);
        DrawText(graphics, "磁盘活动", heading, text,
            new Rectangle(S(8), S(1), Math.Max(0, toggle.Left - S(12)), S(24)));
        var layout = SidebarGeometry();
        if (!layout.ShowsCurve)
        {
            string summary = !Expanded ? "整块磁盘 · 已暂停" : current?.Status == DiskActivityStatus.Available ? $"整盘活动 {Values()[0]}" : message;
            DrawText(graphics, summary, small, muted,
                new Rectangle(S(8), S(24), Math.Max(0, Width - S(16)), Math.Max(0, Math.Min(S(20), Height - S(24)))));
            return;
        }
        DrawCurve(graphics, layout.Chart, accent, contrast ? borderColor : Theme.Mix(borderColor, surface, .4f), contrast);
        string[] values = Values();
        DrawText(graphics, "活动时间", small, muted, layout.ActivityLabel);
        DrawText(graphics, values[0], value, accent, layout.ActivityValue);
        DrawText(graphics, "整盘读 " + values[1], small, text, layout.Read);
        DrawText(graphics, "整盘写 " + values[2], small, text, layout.Write);
    }

    private void DrawCurve(Graphics graphics, RectangleF chart, Color accent, Color gridColor, bool contrast)
    {
        if (chart.Width <= 0 || chart.Height <= 0) return;
        using var grid = new Pen(gridColor);
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
            DetailsRequested = null;
            StateChanged = null;
            sidebarTip?.Dispose();
            heading.Dispose(); small.Dispose(); value.Dispose();
        }
        base.Dispose(disposing);
    }
}
