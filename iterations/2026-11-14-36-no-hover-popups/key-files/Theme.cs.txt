using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace DiskAtlas;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(252, 246, 242);
    public static readonly Color Sidebar = Color.FromArgb(250, 239, 232);
    public static readonly Color Surface = Color.FromArgb(255, 252, 249);
    public static Color Border { get; private set; } = Color.FromArgb(228, 205, 195);
    public static readonly Color Text = Color.FromArgb(62, 41, 40);
    public static readonly Color Muted = Color.FromArgb(90, 67, 62);
    public static readonly Color DefaultAccent = Color.FromArgb(165, 68, 62);
    public static Color Accent { get; private set; } = DefaultAccent;
    public static Color Selection { get; private set; } = Color.FromArgb(248, 222, 212);

    // Point sizes are explicit so each layout can reserve enough space at its DPI.
    internal const float BodyFontSize = 10.5f, CaptionFontSize = 9.5f, ControlFontSize = 10;
    internal const float SmallRadius = 4, ControlRadius = 10, CardRadius = 14;
    internal static Color ControlBorder => Mix(Muted, Surface, .16f);

    private static bool customAccent;
    internal static (Color Start, Color End) ProgressColors(Color? accent) => accent is { } value
        ? (ReadableAccent(value), Mix(ReadableAccent(value), Surface, .32f))
        : (Color.FromArgb(166, 59, 61), Color.FromArgb(237, 156, 104));
    internal static (Color Start, Color End) ProgressPalette => ProgressColors(customAccent ? Accent : null);

    public static void SetAccent(Color? color)
    {
        customAccent = color.HasValue;
        Accent = color.HasValue ? ReadableAccent(color.Value) : DefaultAccent;
        Selection = color.HasValue ? Mix(Accent, Surface, .83f) : Color.FromArgb(248, 222, 212);
        Border = color.HasValue ? Mix(Accent, Surface, .68f) : Color.FromArgb(228, 205, 195);
    }

    internal static Color Mix(Color from, Color to, float weight) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * weight),
        (int)(from.G + (to.G - from.G) * weight),
        (int)(from.B + (to.B - from.B) * weight));

    internal static double Luminance(Color color)
    {
        static double Linear(byte value)
        {
            double channel = value / 255d;
            return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    internal static Color ReadableAccent(Color color)
    {
        color = Color.FromArgb(color.R, color.G, color.B);
        // Keep accent labels legible on both neutral and tinted backgrounds.
        while (Luminance(color) > .13) color = Mix(color, Color.Black, .1f);
        return color;
    }
    public static readonly (string Name, Color Color)[] Categories =
    [
        ("文件夹", Color.FromArgb(235, 177, 153)),
        ("视频", Color.FromArgb(232, 160, 156)),
        ("图片", Color.FromArgb(239, 207, 154)),
        ("音频", Color.FromArgb(222, 177, 194)),
        ("压缩包", Color.FromArgb(201, 184, 213)),
        ("文档", Color.FromArgb(171, 199, 193)),
        ("代码", Color.FromArgb(178, 196, 216)),
        ("其他", Color.FromArgb(207, 198, 187))
    ];
    public static ContextMenuStrip CreateContextMenu(Func<bool>? isHighContrast = null)
    {
        isHighContrast ??= () => SystemInformation.HighContrast;
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowItemToolTips = false };
        var themedRenderer = new MenuRenderer { RoundedEdges = false };

        void ApplyPalette()
        {
            bool highContrast = isHighContrast();
            if (highContrast) menu.RenderMode = ToolStripRenderMode.System;
            else menu.Renderer = themedRenderer;
            menu.BackColor = highContrast ? SystemColors.Menu : Surface;
            menu.ForeColor = highContrast ? SystemColors.MenuText : Text;
        }

        ApplyPalette();
        menu.Opening += (_, _) => ApplyPalette();
        return menu;
    }

    private sealed class MenuRenderer() : ToolStripProfessionalRenderer(new MenuColors())
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
            {
                base.OnRenderMenuItemBackground(e);
                return;
            }
            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            bounds.Inflate(-1, -1);
            using var brush = new SolidBrush(Selection);
            e.Graphics.FillRectangle(brush, bounds);
        }
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
    }

    public static string Category(string extension) => FileKinds.Category(extension);
    public static Color CategoryColor(string category) => Categories.FirstOrDefault(c => c.Name == category, Categories[^1]).Color;
    public static Font Font(float size, FontStyle style = FontStyle.Regular) => new("Microsoft YaHei UI", size, style);
    public static void TextAt(Graphics g, string text, Font font, Color color, Rectangle rect, TextFormatFlags flags = TextFormatFlags.Left)
        => TextRenderer.DrawText(g, text, font, rect, color, flags | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
    public static GraphicsPath Round(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        if (rect.Width <= 0 || rect.Height <= 0) return path;
        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ActionButton : Button
{
    private bool primary, selected;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary { get => primary; set { if (primary == value) return; primary = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; Invalidate(); } }
    private bool hovered, mousePressed, spacePressed, pointerFocus;
    private bool motionEnabled = true, subtle;
    private float pressVisual, pressFrom, pressTarget;
    private System.Windows.Forms.Timer? motionTimer;
    private System.Diagnostics.Stopwatch? motionClock;
    private const int MotionMilliseconds = 210;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool MotionEnabled
    {
        get => motionEnabled;
        set
        {
            if (motionEnabled == value) return;
            motionEnabled = value;
            if (!value) StopMotion(Pressed ? 1 : 0);
        }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Subtle { get => subtle; set { if (subtle == value) return; subtle = value; Invalidate(); } }
    internal bool IsMotionRunning => motionTimer?.Enabled == true;
    internal float PressVisual => pressVisual;
    internal bool KeyboardFocusVisible => Focused && ShowFocusCues && !pointerFocus;
    private bool Pressed => spacePressed || mousePressed && hovered;
    public ActionButton(string text, bool primary = false)
    {
        Text = text; Primary = primary; FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = primary ? 0 : 1; Cursor = Cursors.Hand; BackColor = Theme.Surface;
        Font = Theme.Font(Theme.ControlFontSize, FontStyle.Bold); Height = 40;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 4 || Height < 4) return;
        using var path = Theme.Round(new RectangleF(1, 1, Width - 3, Height - 3), Theme.ControlRadius * DeviceDpi / 96f);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
        Invalidate();
    }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); OnSizeChanged(EventArgs.Empty); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; UpdatePressVisual(); Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; UpdatePressVisual(); Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) pointerFocus = true;
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { mousePressed = true; UpdatePressVisual(); Invalidate(); }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        mousePressed = false;
        UpdatePressVisual();
        Invalidate();
        base.OnMouseUp(e);
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture && mousePressed) { mousePressed = false; StopMotion(0); }
        Invalidate();
        base.OnMouseCaptureChanged(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        pointerFocus = false;
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space) { spacePressed = true; UpdatePressVisual(); }
        Invalidate();
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { spacePressed = false; UpdatePressVisual(); Invalidate(); }
        base.OnKeyUp(e);
    }
    protected override void OnLostFocus(EventArgs e)
    {
        mousePressed = spacePressed = false; pointerFocus = false;
        StopMotion(0);
        Invalidate();
        base.OnLostFocus(e);
    }
    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) { mousePressed = spacePressed = false; hovered = false; StopMotion(0); }
        Invalidate();
        base.OnEnabledChanged(e);
    }
    protected override void OnVisibleChanged(EventArgs e)
    {
        if (!Visible) { mousePressed = spacePressed = false; hovered = false; StopMotion(0); }
        base.OnVisibleChanged(e);
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnChangeUICues(UICuesEventArgs e) { base.OnChangeUICues(e); Invalidate(); }

    private void UpdatePressVisual()
    {
        float target = Pressed ? 1 : 0;
        if (!motionEnabled || SystemInformation.HighContrast || !Visible || !Enabled || !IsHandleCreated)
        { StopMotion(target); return; }
        if (pressTarget == target && (IsMotionRunning || Math.Abs(pressVisual - target) < .001f)) return;
        if (target > pressVisual) pressVisual = Math.Max(pressVisual, .3f);
        pressFrom = pressVisual; pressTarget = target;
        motionClock ??= new System.Diagnostics.Stopwatch();
        if (motionTimer == null)
        {
            motionTimer = new System.Windows.Forms.Timer { Interval = 16 };
            motionTimer.Tick += (_, _) => AdvanceMotion();
        }
        motionClock.Restart(); motionTimer.Start(); Invalidate();
    }
    private void AdvanceMotion()
    {
        if (!motionEnabled || SystemInformation.HighContrast || !Visible || !Enabled || FindForm()?.WindowState == FormWindowState.Minimized)
        { mousePressed = spacePressed = false; StopMotion(0); return; }
        float t = Math.Clamp((motionClock?.ElapsedMilliseconds ?? MotionMilliseconds) / (float)MotionMilliseconds, 0, 1);
        if (t >= 1) { StopMotion(pressTarget); return; }
        float remainder = (1 - t) * (1 - t);
        pressVisual = pressTarget > 0
            ? pressTarget + (pressFrom - pressTarget) * remainder
            : pressFrom * remainder * (float)Math.Cos(Math.PI * 1.5 * t);
        Invalidate();
    }
    private void StopMotion(float value)
    {
        motionTimer?.Stop(); motionClock?.Reset();
        pressFrom = pressTarget = pressVisual = value;
        Invalidate();
    }
    // ButtonBase may skip background painting for WM_PRINTCLIENT. Restore
    // the complete local surface in OnPaint, including quiet buttons.
    protected override void OnPaintBackground(PaintEventArgs e) { }
    private void PaintSurface(Graphics graphics)
    {
        if (SystemInformation.HighContrast) graphics.Clear(SystemColors.Control);
        else Wallpaper.Paint(this, graphics, 100, Subtle ? WallpaperSurface.Chrome : WallpaperSurface.Ambient);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        PaintSurface(e.Graphics);
        if (Width < 4 || Height < 4) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        var rect = new Rectangle(1, 1, Width - 3, Height - 3);
        using var path = Theme.Round(rect, Theme.ControlRadius * s);
        bool pressed = Pressed;
        bool highContrast = SystemInformation.HighContrast;
        Color normal = Primary ? hovered ? Theme.Mix(Theme.Accent, Color.Black, .06f) : Theme.Accent
            : Selected ? Theme.Selection : hovered ? Theme.Mix(Theme.Selection, Theme.Surface, Subtle ? .68f : .52f) : BackColor;
        Color down = Primary ? Theme.Mix(Theme.Accent, Color.Black, .16f) : Theme.Mix(Theme.Selection, Theme.Accent, Subtle ? .06f : .1f);
        Color fill = highContrast
            ? !Enabled ? SystemColors.Control : Primary || Selected ? SystemColors.Highlight : pressed ? SystemColors.ControlDark : hovered ? SystemColors.ControlLight : SystemColors.Control
            : !Enabled ? Theme.Mix(Theme.Surface, Theme.Sidebar, .7f) : Theme.Mix(normal, down, Math.Clamp(pressVisual, 0, 1));
        if (Subtle && !Primary && !Selected && !highContrast)
        {
            float pressure = Math.Clamp(pressVisual, 0, 1);
            int alpha = !Enabled ? 112 : hovered ? 144 : 128;
            fill = Color.FromArgb((int)Math.Round(alpha + (188 - alpha) * pressure),
                Theme.Mix(hovered ? Theme.Selection : Theme.Sidebar, down, pressure));
        }
        Color text = highContrast
            ? !Enabled ? SystemColors.GrayText : Primary || Selected ? SystemColors.HighlightText : SystemColors.ControlText
            : !Enabled ? Theme.Muted : Primary ? Color.White : Selected ? Theme.Accent : Theme.Text;
        using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
        if (!Primary && (Subtle || FlatAppearance.BorderSize > 0) || highContrast)
        {
            using var pen = new Pen(highContrast ? SystemColors.ControlText : Theme.Border, Math.Max(1, s));
            e.Graphics.DrawPath(pen, path);
        }
        if (!highContrast && Enabled && Math.Abs(pressVisual) > .01f)
        {
            float inset = (4 + 1.5f * pressVisual) * s;
            var sheenBounds = RectangleF.Inflate(rect, -inset, -inset);
            if (sheenBounds.Width > 0 && sheenBounds.Height > 0)
            {
                using var sheen = new LinearGradientBrush(sheenBounds, Color.FromArgb(18, Color.White), Color.FromArgb(0, Color.White), LinearGradientMode.Vertical);
                using var inner = Theme.Round(sheenBounds, Theme.SmallRadius * s);
                e.Graphics.FillPath(sheen, inner);
            }
        }
        if (Selected && !Primary && TextAlign == ContentAlignment.MiddleLeft)
        {
            using var marker = new SolidBrush(highContrast ? text : Theme.Accent);
            var markerRect = new RectangleF(rect.X + 7 * s, rect.Y + 11 * s, 3 * s, Math.Max(2, rect.Height - 22 * s));
            using var markerPath = Theme.Round(markerRect, 1.5f * s);
            e.Graphics.FillPath(marker, markerPath);
        }
        var textRect = TextAlign == ContentAlignment.MiddleLeft ? new Rectangle(rect.X + (int)(18 * s), rect.Y, Math.Max(0, rect.Width - (int)(27 * s)), rect.Height) : rect;
        if (!highContrast) textRect.Offset(0, (int)Math.Round(Math.Clamp(pressVisual, -.2f, 1) * s));
        Theme.TextAt(e.Graphics, Text, Font, text, textRect, TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter);
        if (KeyboardFocusVisible && Enabled && rect.Width > 12 && rect.Height > 12)
        {
            int inset = Math.Max(3, (int)(4 * s));
            var focusBounds = Rectangle.Inflate(rect, -inset, -inset);
            if (focusBounds.Width > 0 && focusBounds.Height > 0)
            {
                using var focus = Theme.Round(focusBounds, Theme.SmallRadius * s);
                using var pen = new Pen(highContrast ? text : Primary ? Color.White : Theme.Accent, Math.Max(1, s));
                e.Graphics.DrawPath(pen, focus);
            }
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopMotion(0);
            motionTimer?.Dispose(); motionTimer = null; motionClock = null;
        }
        base.Dispose(disposing);
    }
}

// Keep native CheckBox state, keyboard activation and accessibility semantics;
// only the switch and its local surface are drawn here.
internal sealed class RoundedToggle : CheckBox
{
    private bool hovered, pointerFocus;
    public RoundedToggle()
    {
        Text = "NTFS 快扫"; AutoSize = false; Height = 38;
        Font = Theme.Font(Theme.ControlFontSize); Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
        AccessibleName = "NTFS 快扫（实验）";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    private void UpdateRoundRegion()
    {
        if (Width < 2 || Height < 2) return;
        using var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Theme.ControlRadius * DeviceDpi / 96f);
        var previous = Region; Region = new Region(path); previous?.Dispose();
    }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateRoundRegion(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateRoundRegion(); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; base.OnMouseLeave(e); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { pointerFocus = true; base.OnMouseDown(e); Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e) { pointerFocus = false; base.OnKeyDown(e); Invalidate(); }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { pointerFocus = false; base.OnLostFocus(e); Invalidate(); }
    protected override void OnChangeUICues(UICuesEventArgs e) { base.OnChangeUICues(e); Invalidate(); }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    internal (Rectangle Label, Rectangle Badge, RectangleF Track, RectangleF Thumb) LayoutParts(Graphics graphics)
    {
        float s = DeviceDpi / 96f;
        var track = new RectangleF(Width - 36 * s, Height / 2f - 8 * s, 28 * s, 16 * s);
        var thumb = new RectangleF(track.X + 2 * s + (Checked ? 12 * s : 0), track.Y + 2 * s, 12 * s, 12 * s);
        using var badgeFont = Theme.Font(Theme.CaptionFontSize);
        int badgeWidth = TextRenderer.MeasureText(graphics, "实验", badgeFont, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + (int)(4 * s);
        int left = (int)(10 * s), gap = (int)(4 * s);
        int labelWidth = Math.Max(0, (int)(track.Left - 6 * s) - left - badgeWidth - gap);
        int desired = TextRenderer.MeasureText(graphics, Text, Font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        var label = new Rectangle(left, 0, Math.Min(desired, labelWidth), Height);
        var badge = new Rectangle(label.Right + gap, (int)(Height / 2f - 10 * s), badgeWidth, (int)(20 * s));
        return (label, badge, track, thumb);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        bool contrast = SystemInformation.HighContrast;
        if (contrast) e.Graphics.Clear(SystemColors.Window);
        else Wallpaper.Paint(this, e.Graphics, 42, WallpaperSurface.Chrome);
        if (Width < 4 || Height < 4) return;
        float s = DeviceDpi / 96f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = Theme.Round(new RectangleF(.5f, .5f, Width - 2, Height - 2), Theme.ControlRadius * s);
        if (hovered && Enabled && !contrast)
        { using var tint = new SolidBrush(Color.FromArgb(9, Theme.Accent)); e.Graphics.FillPath(tint, shape); }
        using var edge = new Pen(contrast ? SystemColors.WindowText : Theme.Border, Math.Max(1, s));
        e.Graphics.DrawPath(edge, shape);
        var parts = LayoutParts(e.Graphics);
        Color text = contrast ? Enabled ? SystemColors.WindowText : SystemColors.GrayText : Enabled ? Theme.Text : Theme.Muted;
        Theme.TextAt(e.Graphics, Text, Font, text, parts.Label, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        using var badgeFont = Theme.Font(Theme.CaptionFontSize);
        using (var badgeShape = Theme.Round(parts.Badge, Theme.SmallRadius * s))
        using (var badgeFill = new SolidBrush(contrast ? SystemColors.Control : Color.FromArgb(30, Theme.Accent)))
            e.Graphics.FillPath(badgeFill, badgeShape);
        Theme.TextAt(e.Graphics, "实验", badgeFont, contrast ? text : Theme.Muted, parts.Badge, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        using var trackShape = Theme.Round(parts.Track, parts.Track.Height / 2);
        using var trackFill = new SolidBrush(contrast ? Checked ? SystemColors.Highlight : SystemColors.ControlDark
            : Checked ? Theme.Accent : Theme.Mix(Theme.Muted, Theme.Surface, .7f));
        e.Graphics.FillPath(trackFill, trackShape);
        using var thumb = new SolidBrush(contrast ? SystemColors.HighlightText : Theme.Surface);
        e.Graphics.FillEllipse(thumb, parts.Thumb);
        if (Focused && ShowFocusCues && !pointerFocus)
        {
            using var focus = Theme.Round(new RectangleF(3 * s, 3 * s, Width - 6 * s, Height - 6 * s), Theme.SmallRadius * s);
            using var focusPen = new Pen(contrast ? SystemColors.WindowText : Theme.Accent, Math.Max(1, s));
            e.Graphics.DrawPath(focusPen, focus);
        }
    }
}

// Keep the input native so keyboard, IME, selection and dropdown behavior are preserved.
internal sealed class InputFrame : Panel
{
    private readonly Control input;
    private readonly string caption;
    private int captionWidth;

    public InputFrame(Control input, string caption = "")
    {
        this.input = input;
        this.caption = caption;
        if (caption.Length > 0 && string.IsNullOrEmpty(input.AccessibleName)) input.AccessibleName = caption;
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : Theme.Surface;
        Dock = DockStyle.Fill;
        TabStop = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        input.Dock = DockStyle.None;
        Controls.Add(input);
        input.GotFocus += (_, _) => Invalidate();
        input.LostFocus += (_, _) => Invalidate();
        input.EnabledChanged += (_, _) =>
        {
            ApplyColors();
            Invalidate();
        };
        ApplyColors();
    }

    private void ApplyColors()
    {
        bool highContrast = SystemInformation.HighContrast;
        Color color = highContrast ? input.Enabled ? SystemColors.Window : SystemColors.Control
            : input is TextBoxBase ? Theme.Surface
            : Wallpaper.SampleSurfaceColor(this, input.Bounds.IsEmpty ? ClientRectangle : input.Bounds, WallpaperSurface.Chrome);
        if (BackColor != color) BackColor = color;
        if (input.BackColor != color) input.BackColor = color;
        Color foreground = highContrast ? input.Enabled ? SystemColors.WindowText : SystemColors.GrayText : input.Enabled ? Theme.Text : Theme.Muted;
        if (input.ForeColor != foreground) input.ForeColor = foreground;
    }
    protected override void OnSystemColorsChanged(EventArgs e) { base.OnSystemColorsChanged(e); if (input != null) ApplyColors(); Invalidate(); }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (input == null) return;
        int inset = (int)Math.Round(10 * DeviceDpi / 96f);
        captionWidth = string.IsNullOrEmpty(caption) ? 0 : TextRenderer.MeasureText(caption, input.Font,
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        int leading = captionWidth > 0 ? captionWidth + (int)Math.Round(12 * DeviceDpi / 96f) : 0;
        input.SetBounds(inset + leading, Math.Max(1, (ClientSize.Height - input.PreferredSize.Height) / 2),
            Math.Max(0, ClientSize.Width - 2 * inset - leading), input.PreferredSize.Height);
        ApplyColors();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    private void UpdateRoundRegion()
    {
        if (Width < 2 || Height < 2) return;
        using var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Theme.ControlRadius * DeviceDpi / 96f);
        var previous = Region; Region = new Region(path); previous?.Dispose();
    }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateRoundRegion(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateRoundRegion(); PerformLayout(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) e.Graphics.Clear(input.Enabled ? SystemColors.Window : SystemColors.Control);
        else if (input is TextBoxBase) e.Graphics.Clear(Theme.Surface);
        else Wallpaper.Paint(this, e.Graphics, 42, WallpaperSurface.Chrome);
        ApplyColors();
        base.OnPaint(e);
        if (Width < 3 || Height < 3) return;
        float s = DeviceDpi / 96f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color border = SystemInformation.HighContrast ? input.Enabled ? SystemColors.WindowText : SystemColors.GrayText
            : input.Focused ? Theme.Accent : Theme.Border;
        float stroke = Math.Max(1, s) * (input.Focused ? 2 : 1);
        using var pen = new Pen(border, stroke);
        float inset = stroke / 2;
        var bounds = new RectangleF(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        // Match the clipping region's corner centres. Insetting the stroke
        // without reducing its radius clips the outer side of focused corners.
        using var path = Theme.Round(bounds, Math.Max(.5f, Theme.ControlRadius * s - inset));
        e.Graphics.DrawPath(pen, path);
        if (captionWidth > 0)
            Theme.TextAt(e.Graphics, caption, input.Font, input.ForeColor,
                new Rectangle((int)Math.Round(10 * s), 1, captionWidth, Height - 2), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}

internal sealed class StatCard : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value { get; set; } = "—";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Caption { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Highlight { get; set; }
    private bool showCaption = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool ShowCaption { get => showCaption; set { if (showCaption == value) return; showCaption = value; Invalidate(); } }
    public StatCard() { DoubleBuffered = true; BackColor = Theme.Background; }
    internal const float TitleFontSize = 10;
    internal (Rectangle Title, Rectangle Value, Rectangle Caption) TextBands
    {
        get
        {
            float scale = DeviceDpi / 96f;
            int x = (int)(18 * scale), width = Math.Max(0, Width - x * 2);
            Rectangle Band(float top, float height) => new(x, (int)(top * scale), width, (int)(height * scale));
            if (!ShowCaption)
            {
                int valueTop = (int)(30 * scale), bottom = (int)(8 * scale);
                return (Band(8, 20), new Rectangle(x, valueTop, width, Math.Max(0, Height - valueTop - bottom)), Rectangle.Empty);
            }
            return (Band(10, 20), Band(31, 42), Band(77, 22));
        }
    }
    internal float ValueFontSize(Graphics graphics)
    {
        var bounds = TextBands.Value;
        for (float points = 24; points > 12; points--)
        {
            using var font = Theme.Font(points, FontStyle.Bold);
            var size = TextRenderer.MeasureText(graphics, Value, font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (size.Width <= bounds.Width && size.Height <= bounds.Height) return points;
        }
        return 12;
    }
    private void UpdateRoundRegion()
    {
        if (Width < 2 || Height < 2) return;
        using var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Theme.CardRadius * DeviceDpi / 96f);
        var previous = Region; Region = new Region(path); previous?.Dispose();
    }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateRoundRegion(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateRoundRegion(); }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e)
    {
        bool highContrast = SystemInformation.HighContrast;
        if (highContrast) e.Graphics.Clear(SystemColors.Window);
        else Wallpaper.Paint(this, e.Graphics, 42, WallpaperSurface.Card);
        if (Width < 2 || Height < 2) return;
        float s = DeviceDpi / 96f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float edgeWidth = Math.Max(1, s), inset = edgeWidth / 2;
        using var path = Theme.Round(new RectangleF(inset, inset, Width - 1 - edgeWidth, Height - 1 - edgeWidth),
            Math.Max(0, Theme.CardRadius * s - inset));
        using var pen = new Pen(highContrast ? SystemColors.WindowText : Theme.Border, edgeWidth);
        e.Graphics.DrawPath(pen, path);
        using var titleFont = Theme.Font(TitleFontSize, FontStyle.Bold);
        using var valueFont = Theme.Font(ValueFontSize(e.Graphics), FontStyle.Bold);
        var bands = TextBands;
        Theme.TextAt(e.Graphics, Title, titleFont, highContrast ? SystemColors.WindowText : Theme.Text, bands.Title);
        Theme.TextAt(e.Graphics, Value, valueFont, highContrast ? SystemColors.WindowText : Highlight ? Theme.Accent : Theme.Text, bands.Value, TextFormatFlags.SingleLine);
        if (ShowCaption)
        {
            using var captionFont = Theme.Font(Theme.CaptionFontSize);
            Theme.TextAt(e.Graphics, Caption, captionFont, highContrast ? SystemColors.WindowText : Theme.Muted, bands.Caption);
        }
    }
}

internal sealed class NavigationPanel : Control
{
    private ActionButton[] buttons = [];
    private int selectedIndex;
    private Control? activityContent;
    private Func<bool>? activityExpanded;
    private bool layoutReady, arranging;
    internal Control? ActivityContent => activityContent;
    internal Rectangle LegendBounds => R(25, 465, Math.Max(0, Width * 96f / DeviceDpi - 42), 140);
    internal Rectangle ActivityBounds => activityContent?.Bounds ?? Rectangle.Empty;

    internal void AttachActivity(Control content, Func<bool> isExpanded)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(isExpanded);
        if (content.IsDisposed || ReferenceEquals(content, this) || content.Contains(this))
            throw new ArgumentException("The activity content must be an available child control.", nameof(content));
        if (activityContent != null && !ReferenceEquals(activityContent, content)) Controls.Remove(activityContent);
        activityContent = content; activityExpanded = isExpanded;
        content.Dock = DockStyle.None; content.Margin = Padding.Empty;
        if (!ReferenceEquals(content.Parent, this)) Controls.Add(content);
        PerformLayout();
        Invalidate();
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            selectedIndex = value;
            for (int i = 0; i < buttons.Length; i++) { buttons[i].Selected = i == value; buttons[i].Invalidate(); }
            Invalidate();
        }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string RootName { get; set; } = "尚未选择文件夹";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string RootPath { get; set; } = "从一个文件夹开始探索";
    public event Action<int>? Navigate;
    public NavigationPanel()
    {
        DoubleBuffered = true; BackColor = Theme.Sidebar; TabStop = false;
        string[] names = ["▦    空间分布", "↗    大文件", "≡    文件类型"];
        buttons = new ActionButton[3];
        for (int i = 0; i < 3; i++)
        {
            int index = i;
            buttons[i] = new ActionButton(names[i]) { TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Sidebar, AccessibleName = names[i].Substring(5), TabIndex = i };
            buttons[i].FlatAppearance.BorderSize = 0;
            buttons[i].Click += (_, _) => Navigate?.Invoke(index);
            Controls.Add(buttons[i]);
        }
        SelectedIndex = 0; layoutReady = true; LayoutContents();
    }
    private Rectangle R(float x, float y, float width, float height)
    {
        float scale = DeviceDpi / 96f;
        return new Rectangle((int)(x * scale), (int)(y * scale), (int)(width * scale), (int)(height * scale));
    }
    private void LayoutContents()
    {
        if (!layoutReady || arranging) return;
        arranging = true;
        try
        {
            float s = DeviceDpi / 96f;
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] is { } button) button.Bounds = new Rectangle((int)(14 * s), (int)((166 + i * 52) * s), Math.Max(0, Width - (int)(28 * s)), (int)(44 * s));
            if (activityContent is not { IsDisposed: false } content) return;
            int inset = (int)(14 * s), bottom = (int)(16 * s);
            int available = Height - bottom - LegendBounds.Bottom - (int)(12 * s);
            int height = Math.Min((int)((activityExpanded?.Invoke() == true ? 160 : 44) * s), available);
            int width = Width - inset * 2;
            if (height <= 0 || width <= 0)
            {
                content.Visible = false;
                if (content.Bounds != Rectangle.Empty) content.Bounds = Rectangle.Empty;
                return;
            }
            var bounds = new Rectangle(inset, Height - bottom - height, width, height);
            if (content.Bounds != bounds) content.Bounds = bounds;
            content.Visible = true;
        }
        finally { arranging = false; }
    }
    protected override void OnPaintBackground(PaintEventArgs e) => Wallpaper.Paint(this, e.Graphics, 225);
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); LayoutContents(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutContents(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); LayoutContents(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = DeviceDpi / 96f;
        using var logoFont = Theme.Font(16, FontStyle.Bold);
        using var smallFont = Theme.Font(Theme.CaptionFontSize);
        using var navFont = Theme.Font(11, FontStyle.Bold);
        using var labelFont = Theme.Font(Theme.CaptionFontSize);
        using var accentBrush = new SolidBrush(Theme.Accent);
        g.FillRectangle(accentBrush, R(24, 31, 12, 12));
        g.FillRectangle(accentBrush, R(39, 31, 7, 22));
        g.FillRectangle(accentBrush, R(24, 46, 12, 7));
        Theme.TextAt(g, "AsukaLens", logoFont, Theme.Text, R(57, 26, 160, 32));
        Theme.TextAt(g, "磁 盘 空 间 可 视 化", smallFont, Theme.Muted, R(25, 67, 185, 22));
        using var borderPen = new Pen(Theme.Border);
        g.DrawLine(borderPen, 24 * s, 113 * s, Width - 24 * s, 113 * s);
        Theme.TextAt(g, "工作空间", smallFont, Theme.Muted, R(25, 132, 180, 22));
        Theme.TextAt(g, "当前扫描", smallFont, Theme.Muted, R(25, 350, 180, 22));
        Theme.TextAt(g, RootName, navFont, Theme.Text, R(25, 380, 180, 26));
        Theme.TextAt(g, RootPath, smallFont, Theme.Muted, R(25, 409, 180, 40), TextFormatFlags.WordBreak);
        Theme.TextAt(g, "颜色索引", smallFont, Theme.Muted, R(25, 465, 180, 22));
        for (int i = 0; i < Theme.Categories.Length; i++)
        {
            int column = i % 2, row = i / 2;
            using var brush = new SolidBrush(Theme.Categories[i].Color);
            g.FillEllipse(brush, R(26 + column * 99, 505 + row * 28, 8, 8));
            Theme.TextAt(g, Theme.Categories[i].Name, labelFont, Theme.Muted, R(42 + column * 99, 497 + row * 28, 74, 24));
        }
    }
}
