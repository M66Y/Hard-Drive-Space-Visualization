using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace DiskAtlas;

internal sealed class TreemapControl : Control
{
    private IReadOnlyList<MapItem> items = [];
    private List<MapTile> tiles = [];
    private readonly Dictionary<MapItem, List<MapTile>> nestedLayouts = [];
    private MapItem? hovered;
    private MapItem? selected;
    private readonly ToolTip tip = new() { InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 20000 };
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public long TotalSize { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowNested { get; set; } = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SizeMetric Metric { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Scanning { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool LoadingView { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyTitle { get; set; } = "空间，都有迹可循";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyCaption { get; set; } = "选择电脑上的文件夹，查看文件大小与空间分布";
    public event Action<MapItem?>? ItemSelected;
    public event Action<MapItem>? ItemActivated;
    public TreemapControl()
    {
        DoubleBuffered = true; BackColor = Theme.Surface;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }
    public void SetItems(IReadOnlyList<MapItem> value)
    {
        items = value; hovered = selected = null; tip.SetToolTip(this, null);
        LayoutTiles(); Invalidate();
    }
    public void SelectItem(MapItem? value) { selected = value; Invalidate(); }
    protected override void OnPaintBackground(PaintEventArgs e) => Wallpaper.Paint(this, e.Graphics, tiles.Count == 0 ? 130 : 215);
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutTiles(); }
    private void LayoutTiles()
    {
        float pad = 10 * DeviceDpi / 96f;
        tiles = TreemapLayout.Build(items, new RectangleF(pad, pad, Math.Max(0, Width - 2 * pad), Math.Max(0, Height - 2 * pad)));
        nestedLayouts.Clear();
        float s = DeviceDpi / 96f;
        if (!ShowNested) return;
        foreach (var tile in tiles)
        {
            var rect = RectangleF.Inflate(tile.Bounds, -2 * s, -2 * s);
            if (tile.Item.Entry is not { IsDirectory: true } dir || rect.Width <= 90 * s || rect.Height <= 110 * s) continue;
            var nestedBounds = new RectangleF(rect.X + 7 * s, rect.Y + 63 * s, rect.Width - 14 * s, rect.Height - 70 * s);
            var nested = dir.Children.Where(c => c.Bytes(Metric) > 0).Take(36).Select(c => new MapItem(c.Name, c.Bytes(Metric), c, Theme.Category(c.Extension))).ToList();
            long rest = dir.Bytes(Metric) - nested.Sum(i => i.Size);
            if (rest > 0) nested.Add(new MapItem("其他", rest, null, "其他", IsAggregate: true));
            nestedLayouts[tile.Item] = TreemapLayout.Build(nested, nestedBounds);
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        if (tiles.Count == 0)
        {
            int cx = Width / 2, cy = Height / 2 - (int)(60 * s);
            using var outline = new Pen(Theme.Accent, 2 * s);
            g.DrawRectangle(outline, cx - 46 * s, cy - 26 * s, 56 * s, 61 * s);
            g.DrawRectangle(outline, cx + 16 * s, cy - 26 * s, 30 * s, 29 * s);
            g.DrawRectangle(outline, cx + 16 * s, cy + 9 * s, 30 * s, 26 * s);
            using var titleFont = Theme.Font(17, FontStyle.Bold);
            using var captionFont = Theme.Font(9);
            Theme.TextAt(g, Scanning ? "正在读取你的文件夹" : LoadingView ? "正在整理空间分布" : EmptyTitle, titleFont, Theme.Text,
                new Rectangle((int)(20 * s), cy + (int)(60 * s), Width - (int)(40 * s), (int)(40 * s)), TextFormatFlags.HorizontalCenter);
            Theme.TextAt(g, Scanning ? "已读取的内容会逐步显示，无需等待整个扫描完成" : LoadingView ? "可继续搜索或切换视图，始终显示最新选择" : EmptyCaption, captionFont, Theme.Muted,
                new Rectangle((int)(30 * s), cy + (int)(108 * s), Width - (int)(60 * s), (int)(62 * s)), TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            return;
        }
        using var nameFont = Theme.Font(10, FontStyle.Bold);
        using var sizeFont = Theme.Font(9);
        using var miniFont = Theme.Font(8);
        foreach (var tile in tiles)
        {
            RectangleF rect = tile.Bounds;
            rect.Inflate(-2 * s, -2 * s);
            if (rect.Width < 1 || rect.Height < 1) continue;
            Color baseColor = Theme.CategoryColor(tile.Item.Category);
            if (tile.Item == hovered) baseColor = ControlPaint.Light(baseColor, .13f);
            using var fill = new SolidBrush(baseColor);
            using var path = Theme.Round(rect, Math.Min(5 * s, Math.Min(rect.Width, rect.Height) / 4));
            g.FillPath(fill, path);
            if (nestedLayouts.TryGetValue(tile.Item, out var nested))
            {
                int index = 0;
                foreach (var child in nested)
                {
                    var inner = child.Bounds; inner.Inflate(-1 * s, -1 * s);
                    if (inner.Width <= 1 || inner.Height <= 1) continue;
                    var dark = Color.FromArgb(Math.Max(0, baseColor.R - 16 - index % 4 * 5), Math.Max(0, baseColor.G - 16 - index % 4 * 5), Math.Max(0, baseColor.B - 16 - index % 4 * 5));
                    using var b = new SolidBrush(dark);
                    g.FillRectangle(b, inner);
                    if (inner.Width > 65 * s && inner.Height > 24 * s)
                        Theme.TextAt(g, child.Item.Name, miniFont, Theme.Text, Rectangle.Round(RectangleF.Inflate(inner, -4 * s, 0)));
                    index++;
                }
            }
            if (rect.Width > 55 * s && rect.Height > 25 * s)
            {
                int x = (int)(rect.X + 11 * s), y = (int)(rect.Y + 8 * s), w = (int)(rect.Width - 22 * s);
                Theme.TextAt(g, tile.Item.Name, nameFont, Theme.Text, new Rectangle(x, y, w, (int)(24 * s)));
                if (rect.Height > 52 * s)
                    Theme.TextAt(g, SizeFormat.Bytes(tile.Item.Size), sizeFont, Theme.Text, new Rectangle(x, y + (int)(26 * s), w, (int)(21 * s)));
            }
            if (tile.Item == selected || tile.Item == hovered)
            {
                using var pen = new Pen(tile.Item == selected ? Theme.Accent : Theme.Muted, (tile.Item == selected ? 2.5f : 1.5f) * s);
                g.DrawPath(pen, path);
            }
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var item = tiles.FirstOrDefault(t => t.Bounds.Contains(e.Location))?.Item;
        if (item == hovered) return;
        hovered = item;
        string? text = item == null ? null : $"{item.Name}\n{SizeFormat.Bytes(item.Size)}  ·  {SizeFormat.Percent(item.Size, TotalSize)}\n{item.Entry?.Path ?? (item.IsAggregate ? $"合并 {SizeFormat.Number(item.Count)} 项，请查看右侧列表" : $"共 {SizeFormat.Number(item.Count)} 个文件")}\n{(item.Entry?.IsDirectory == true ? "双击进入文件夹" : item.Entry != null ? "双击在资源管理器中定位" : item.IsAggregate ? "" : "双击查看该类型文件")} ";
        tip.SetToolTip(this, text); Invalidate();
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = null; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        selected = tiles.FirstOrDefault(t => t.Bounds.Contains(e.Location))?.Item;
        ItemSelected?.Invoke(selected); Invalidate();
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var item = tiles.FirstOrDefault(t => t.Bounds.Contains(e.Location))?.Item;
        if (item != null) ItemActivated?.Invoke(item);
    }
    protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
}
