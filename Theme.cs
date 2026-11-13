using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace DiskAtlas;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(252, 246, 242);
    public static readonly Color Sidebar = Color.FromArgb(250, 239, 232);
    public static readonly Color Surface = Color.FromArgb(255, 252, 249);
    public static Color Border { get; private set; } = Color.FromArgb(228, 205, 195);
    public static readonly Color Text = Color.FromArgb(75, 54, 52);
    public static readonly Color Muted = Color.FromArgb(121, 96, 90);
    public static readonly Color DefaultAccent = Color.FromArgb(165, 68, 62);
    public static Color Accent { get; private set; } = DefaultAccent;
    public static Color Selection { get; private set; } = Color.FromArgb(248, 222, 212);

    // Point sizes are explicit so each layout can reserve enough space at its DPI.
    internal const float BodyFontSize = 10.5f, CaptionFontSize = 9.5f, ControlFontSize = 10;
    internal const float SmallRadius = 4, ControlRadius = 7, CardRadius = 10;
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
        var menu = new ContextMenuStrip { ShowImageMargin = false };
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
    private bool hovered, mousePressed, spacePressed;
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
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { mousePressed = true; Invalidate(); }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        mousePressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture) mousePressed = false;
        Invalidate();
        base.OnMouseCaptureChanged(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space) { spacePressed = true; Invalidate(); }
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { spacePressed = false; Invalidate(); }
        base.OnKeyUp(e);
    }
    protected override void OnLostFocus(EventArgs e)
    {
        spacePressed = false;
        Invalidate();
        base.OnLostFocus(e);
    }
    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) { mousePressed = spacePressed = false; hovered = false; }
        Invalidate();
        base.OnEnabledChanged(e);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) e.Graphics.Clear(SystemColors.Control);
        else Wallpaper.Paint(this, e.Graphics, 100);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 4 || Height < 4) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        var rect = new Rectangle(1, 1, Width - 3, Height - 3);
        using var path = Theme.Round(rect, Theme.ControlRadius * s);
        bool pressed = spacePressed || mousePressed && hovered;
        bool highContrast = SystemInformation.HighContrast;
        Color fill = highContrast
            ? !Enabled ? SystemColors.Control : Primary || Selected ? SystemColors.Highlight : pressed ? SystemColors.ControlDark : hovered ? SystemColors.ControlLight : SystemColors.Control
            : !Enabled ? Theme.Mix(Theme.Surface, Theme.Sidebar, .7f) : Primary
                ? pressed ? Theme.Mix(Theme.Accent, Color.Black, .25f) : hovered ? Theme.Mix(Theme.Accent, Color.Black, .12f) : Theme.Accent
                : pressed ? Theme.Mix(Theme.Selection, Theme.Accent, .13f) : Selected ? Theme.Selection : hovered ? Theme.Mix(Theme.Selection, Theme.Surface, .35f) : BackColor;
        Color text = highContrast
            ? !Enabled ? SystemColors.GrayText : Primary || Selected ? SystemColors.HighlightText : SystemColors.ControlText
            : !Enabled ? Theme.Muted : Primary ? Color.White : Selected ? Theme.Accent : Theme.Text;
        using var brush = new SolidBrush(fill);
        e.Graphics.FillPath(brush, path);
        if (!Primary && FlatAppearance.BorderSize > 0 || highContrast)
        {
            using var pen = new Pen(highContrast ? SystemColors.ControlText : Enabled ? Theme.ControlBorder : Theme.Border, Math.Max(1, s));
            e.Graphics.DrawPath(pen, path);
        }
        if (Selected && !Primary && TextAlign == ContentAlignment.MiddleLeft)
        {
            using var marker = new SolidBrush(highContrast ? text : Theme.Accent);
            var markerRect = new RectangleF(rect.X + 7 * s, rect.Y + 11 * s, 3 * s, Math.Max(2, rect.Height - 22 * s));
            using var markerPath = Theme.Round(markerRect, 1.5f * s);
            e.Graphics.FillPath(marker, markerPath);
        }
        var textRect = TextAlign == ContentAlignment.MiddleLeft ? new Rectangle(rect.X + (int)(18 * s), rect.Y, Math.Max(0, rect.Width - (int)(27 * s)), rect.Height) : rect;
        Theme.TextAt(e.Graphics, Text, Font, text, textRect, TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter);
        if (Focused && Enabled && rect.Width > 12 && rect.Height > 12)
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
}

// Keep the input native so keyboard, IME, selection and dropdown behavior are preserved.
internal sealed class InputFrame : Panel
{
    private readonly Control input;

    public InputFrame(Control input)
    {
        this.input = input;
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
        BackColor = highContrast ? input.Enabled ? SystemColors.Window : SystemColors.Control : input.Enabled ? Theme.Surface : Theme.Sidebar;
        input.BackColor = BackColor;
        input.ForeColor = highContrast ? input.Enabled ? SystemColors.WindowText : SystemColors.GrayText : input.Enabled ? Theme.Text : Theme.Muted;
    }
    protected override void OnSystemColorsChanged(EventArgs e) { base.OnSystemColorsChanged(e); if (input != null) ApplyColors(); Invalidate(); }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (input == null) return;
        int inset = (int)Math.Round(10 * DeviceDpi / 96f);
        input.SetBounds(inset, Math.Max(1, (ClientSize.Height - input.PreferredSize.Height) / 2),
            Math.Max(0, ClientSize.Width - 2 * inset), input.PreferredSize.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 3 || Height < 3) return;
        float s = DeviceDpi / 96f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color border = SystemInformation.HighContrast ? input.Enabled ? SystemColors.WindowText : SystemColors.GrayText
            : input.Focused ? Theme.Accent : input.Enabled ? Theme.ControlBorder : Theme.Border;
        using var pen = new Pen(border, Math.Max(1, s) * (input.Focused ? 2 : 1));
        float inset = input.Focused ? Math.Max(1, s) : .5f;
        var bounds = new RectangleF(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var path = Theme.Round(bounds, Theme.ControlRadius * s);
        e.Graphics.DrawPath(pen, path);
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
    public StatCard() { DoubleBuffered = true; BackColor = Theme.Background; }
    internal float ValueFontSize(Graphics graphics)
    {
        int width = Math.Max(1, Width - (int)(36 * DeviceDpi / 96f));
        for (float points = 24; points > 12; points--)
        {
            using var font = Theme.Font(points, FontStyle.Bold);
            if (TextRenderer.MeasureText(graphics, Value, font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width <= width) return points;
        }
        return 12;
    }
    protected override void OnPaintBackground(PaintEventArgs e) => Wallpaper.Paint(this, e.Graphics);
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        float s = DeviceDpi / 96f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Theme.CardRadius * s);
        using var brush = new SolidBrush(Color.FromArgb(Wallpaper.SurfaceAlpha(this, WallpaperSurface.Card), Theme.Surface));
        e.Graphics.FillPath(brush, path);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawPath(pen, path);
        using var titleFont = Theme.Font(10, FontStyle.Bold);
        using var valueFont = Theme.Font(ValueFontSize(e.Graphics), FontStyle.Bold);
        using var captionFont = Theme.Font(Theme.CaptionFontSize);
        int x = (int)(18 * s);
        Theme.TextAt(e.Graphics, Title, titleFont, Theme.Text, new Rectangle(x, (int)(13 * s), Width - x * 2, (int)(22 * s)));
        Theme.TextAt(e.Graphics, Value, valueFont, Highlight ? Theme.Accent : Theme.Text, new Rectangle(x, (int)(38 * s), Width - x * 2, (int)(42 * s)), TextFormatFlags.SingleLine);
        Theme.TextAt(e.Graphics, Caption, captionFont, Theme.Muted, new Rectangle(x, (int)(84 * s), Width - x * 2, (int)(22 * s)));
    }
}

internal sealed class NavigationPanel : Control
{
    private ActionButton[] buttons = [];
    private int selectedIndex;
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
        SelectedIndex = 0; LayoutButtons();
    }
    private void LayoutButtons()
    {
        float s = DeviceDpi / 96f;
        for (int i = 0; i < buttons.Length; i++) buttons[i].Bounds = new Rectangle((int)(14 * s), (int)((166 + i * 52) * s), Math.Max(0, Width - (int)(28 * s)), (int)(44 * s));
    }
    protected override void OnPaintBackground(PaintEventArgs e) => Wallpaper.Paint(this, e.Graphics, 225);
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutButtons(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); LayoutButtons(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = DeviceDpi / 96f;
        Rectangle R(float x, float y, float w, float h) => new((int)(x * s), (int)(y * s), (int)(w * s), (int)(h * s));
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
        Theme.TextAt(g, "颜色索引", smallFont, Theme.Muted, R(25, 479, 180, 22));
        for (int i = 0; i < Theme.Categories.Length; i++)
        {
            int column = i % 2, row = i / 2;
            using var brush = new SolidBrush(Theme.Categories[i].Color);
            g.FillEllipse(brush, R(26 + column * 99, 519 + row * 30, 8, 8));
            Theme.TextAt(g, Theme.Categories[i].Name, labelFont, Theme.Muted, R(42 + column * 99, 511 + row * 30, 74, 24));
        }
        if (Height > 740 * s)
        {
            Theme.TextAt(g, "本地扫描 文件不上传", smallFont, Theme.Muted, new Rectangle((int)(25 * s), Height - (int)(46 * s), Width - (int)(40 * s), (int)(25 * s)));
        }
    }
}
