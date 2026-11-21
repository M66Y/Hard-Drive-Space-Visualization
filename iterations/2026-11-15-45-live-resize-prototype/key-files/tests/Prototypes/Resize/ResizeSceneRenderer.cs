using System.Drawing;
using System.Drawing.Drawing2D;

namespace DiskAtlas.ResizePrototype;

// No Controls, HWND captures, filesystem access, or persistent configuration.
// The host owns frame buffers; this renderer owns only fonts and two edge contours.
internal sealed class ResizeSceneRenderer : IDisposable
{
    private readonly Font body = Theme.Font(Theme.BodyFontSize);
    private readonly Font caption = Theme.Font(Theme.CaptionFontSize);
    private readonly Font button = Theme.Font(Theme.ControlFontSize, FontStyle.Bold);
    private readonly Font heading = Theme.Font(11, FontStyle.Bold);
    private readonly Font logo = Theme.Font(16, FontStyle.Bold);
    private readonly Font value = Theme.Font(24, FontStyle.Bold);
    private readonly Dictionary<int, EdgeContour> contours = [];
    private bool disposed;

    internal void Render(Graphics graphics, ResizeSceneLayout layout, ResizeSceneState state,
        Image background, Image? icon, double seconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(background);
        // Deliberately render the captured state, never a newer live selection.
        _ = state;
        var snapshot = layout.State;
        var saved = graphics.Save();
        try
        {
            graphics.SetClip(new Rectangle(Point.Empty, layout.PixelSize), CombineMode.Intersect);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            using (var opaque = new SolidBrush(Theme.Background))
                graphics.FillRectangle(opaque, new Rectangle(Point.Empty, layout.PixelSize));
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(background, Wallpaper.CoverBounds(background, layout.PixelSize, Point.Empty));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float s = layout.Dpi / 96f;
            DrawSidebar(graphics, layout, s);
            string[] labels = ["＋  选择文件夹", snapshot.Progress < 1 ? "取消扫描" : "重新扫描", "背景设置", "诊断"];
            for (int i = 0; i < layout.Toolbar.Length; i++)
                DrawButton(graphics, layout.Toolbar[i], labels[i], s, i == 0);
            DrawInput(graphics, layout.Strategy, "扫描策略", "自动", true, s);
            DrawInput(graphics, layout.Metric, "统计口径", snapshot.Metric == 0 ? "文件大小" : "实际占用", true, s);
            DrawToggle(graphics, layout.FastToggle, s);
            DrawStats(graphics, layout, s);
            DrawButton(graphics, layout.Up, "↑  上一级", s);
            DrawButton(graphics, layout.Home, "扫描根目录", s);
            DrawInput(graphics, layout.Path, "", @"X:\AsukaLens-Demo", false, s);
            DrawInput(graphics, layout.Search, "", snapshot.Search.Length == 0 ? "搜索当前层级名称…" : snapshot.Search, false, s,
                snapshot.Search.Length == 0 ? Theme.Muted : Theme.Text);
            DrawMap(graphics, layout, s, seconds);
            DrawList(graphics, layout, s);
            DrawDetails(graphics, layout, s, icon);
        }
        finally { graphics.Restore(saved); }
    }

    private void DrawSidebar(Graphics g, ResizeSceneLayout layout, float s)
    {
        using (var wash = new SolidBrush(Color.FromArgb(225, Theme.Surface))) g.FillRectangle(wash, layout.Sidebar);
        Rectangle R(float x, float y, float w, float h) => new((int)(x * s), (int)(y * s), (int)(w * s), (int)(h * s));
        using (var ink = new SolidBrush(Theme.Accent))
        {
            g.FillRectangle(ink, R(24, 31, 12, 12)); g.FillRectangle(ink, R(39, 31, 7, 22)); g.FillRectangle(ink, R(24, 46, 12, 7));
        }
        Text(g, "AsukaLens", logo, Theme.Text, R(57, 26, 160, 32));
        Text(g, "磁 盘 空 间 可 视 化", caption, Theme.Muted, R(25, 67, 185, 22));
        using (var pen = new Pen(Theme.Border, Math.Max(1, s))) g.DrawLine(pen, 24 * s, 113 * s, layout.Sidebar.Right - 24 * s, 113 * s);
        Text(g, "工作空间", caption, Theme.Muted, R(25, 132, 180, 22));
        string[] navigation = ["▦    空间分布", "↗    大文件", "≡    文件类型"];
        for (int i = 0; i < navigation.Length; i++)
        {
            bool selected = i == layout.State.View;
            DrawButton(g, layout.Navigation[i], navigation[i], s, false, selected, true);
        }
        Text(g, "当前扫描", caption, Theme.Muted, R(25, 350, 180, 22));
        Text(g, "AsukaLens-Demo", heading, Theme.Text, R(25, 380, 180, 26));
        Text(g, @"X:\AsukaLens-Demo", caption, Theme.Muted, R(25, 409, 180, 40));
        Text(g, "颜色索引", caption, Theme.Muted, R(25, 465, 180, 22));
        for (int i = 0; i < Theme.Categories.Length; i++)
        {
            int column = i % 2, row = i / 2;
            using var dot = new SolidBrush(Theme.Categories[i].Color);
            g.FillEllipse(dot, R(26 + column * 99, 505 + row * 28, 8, 8));
            Text(g, Theme.Categories[i].Name, caption, Theme.Muted, R(42 + column * 99, 497 + row * 28, 74, 24));
        }
        if (!layout.Activity.IsEmpty)
        {
            Panel(g, layout.Activity, s, 90);
            var content = Inset(layout.Activity, 12 * s, 0);
            Text(g, "磁盘活动", button, Theme.Text, new(content.X, content.Y, content.Width - (int)(44 * s), content.Height));
            var track = new RectangleF(layout.Activity.Right - 49 * s, layout.Activity.Y + (layout.Activity.Height - 18 * s) / 2, 33 * s, 18 * s);
            using var path = Theme.Round(track, 9 * s);
            using var trackFill = new SolidBrush(Color.FromArgb(55, Theme.Muted)); g.FillPath(trackFill, path);
            using var knob = new SolidBrush(Theme.Surface); g.FillEllipse(knob, track.X + 3 * s, track.Y + 3 * s, 12 * s, 12 * s);
        }
    }

    private void DrawStats(Graphics g, ResizeSceneLayout layout, float s)
    {
        string[] titles = ["当前目录大小", "文件数量", "子文件夹", "扫描用时"];
        string[] values = [SizeFormat.Bytes(layout.Total), "75", "5", "0.03 s"];
        for (int i = 0; i < layout.Stats.Length; i++)
        {
            var card = layout.Stats[i]; Panel(g, card, s, 122);
            var title = new Rectangle(card.X + (int)(18 * s), card.Y + (int)(8 * s), Math.Max(0, card.Width - (int)(36 * s)), (int)(20 * s));
            var number = new Rectangle(title.X, card.Y + (int)(30 * s), title.Width, Math.Max(0, card.Height - (int)(38 * s)));
            Text(g, titles[i], button, Theme.Text, title);
            // Measure with the same normal glyph padding and single-line flags
            // as Text(). A NoPadding estimate could choose a font whose actual
            // rendering ellipsized the unit in the minimum-size window.
            Font font = caption;
            const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping;
            for (int candidateIndex = 0; candidateIndex < 5; candidateIndex++)
            {
                Font candidate = candidateIndex switch { 0 => value, 1 => logo, 2 => heading, 3 => body, _ => caption };
                var measured = TextRenderer.MeasureText(g, values[i], candidate,
                    new Size(int.MaxValue, int.MaxValue), flags);
                if (measured.Width > number.Width || measured.Height > number.Height) continue;
                font = candidate;
                break;
            }
            Text(g, values[i], font, i == 0 ? Theme.Accent : Theme.Text, number);
        }
    }

    private void DrawMap(Graphics g, ResizeSceneLayout layout, float s, double seconds)
    {
        Panel(g, layout.MapPanel, s, 44);
        string[] titles = ["空间分布", "大文件分布", "文件类型分布"];
        var mapTitle = Inset(layout.MapHeader, 16 * s, 0);
        mapTitle.Width = Math.Max(0, mapTitle.Width - (int)(108 * s));
        Text(g, titles[layout.State.View], heading, Theme.Text, mapTitle);
        var reduce = new Rectangle(layout.MapHeader.Right - (int)(115 * s), layout.MapHeader.Top, (int)(108 * s), layout.MapHeader.Height);
        Text(g, "□  减弱动效", caption, Theme.Muted, reduce);
        var saved = g.Save();
        try
        {
            using var clip = Theme.Round(Inset(layout.MapPanel, 1 * s, 1 * s), Math.Max(1, Theme.CardRadius * s - s));
            g.SetClip(clip, CombineMode.Intersect);
            g.SetClip(layout.MapBody, CombineMode.Intersect);
            foreach (var tile in layout.Tiles)
            {
                var fillBounds = RectangleF.Inflate(tile.Bounds, -2 * s, -2 * s);
                if (fillBounds.Width < 1 || fillBounds.Height < 1) continue;
                using var shape = Theme.Round(fillBounds, Math.Min(8 * s, Math.Min(fillBounds.Width, fillBounds.Height) / 4));
                var category = Theme.CategoryColor(tile.Item.Category);
                using (var brush = new SolidBrush(Color.FromArgb(184, category))) g.FillPath(brush, shape);
                int index = 0;
                foreach (var nested in tile.Nested)
                {
                    var inner = RectangleF.Inflate(nested.Bounds, -s, -s);
                    if (inner.Width < 2 || inner.Height < 2) continue;
                    int shade = 8 + index++ % 3 * 3;
                    var dark = Color.FromArgb(Math.Max(0, category.R - shade), Math.Max(0, category.G - shade), Math.Max(0, category.B - shade));
                    using var brush = new SolidBrush(Color.FromArgb(184, dark));
                    using var innerShape = Theme.Round(inner, Math.Min(4 * s, Math.Min(inner.Width, inner.Height) / 4));
                    g.FillPath(brush, innerShape);
                    var label = Rectangle.Round(RectangleF.Inflate(inner, -5 * s, -2 * s));
                    if (label.Height >= caption.GetHeight(g)) Text(g, nested.Item.Name, caption, Theme.Text, label);
                }
                var name = new Rectangle((int)(fillBounds.X + 11 * s), (int)(fillBounds.Y + 8 * s), Math.Max(0, (int)(fillBounds.Width - 22 * s)), (int)(24 * s));
                var size = new Rectangle(name.X, name.Bottom + (int)(3 * s), name.Width, (int)(21 * s));
                if (name.Bottom <= fillBounds.Bottom - 4 * s) Text(g, tile.Item.Name, heading, Theme.Text, name);
                if (size.Bottom <= fillBounds.Bottom - 4 * s) Text(g, SizeFormat.Bytes(tile.Item.Size), caption, Theme.Text, size);
            }
            DrawEdges(g, layout, s, seconds);
            if (layout.Tiles.Count == 0) Text(g, "没有匹配的结果", heading, Theme.Muted, layout.MapBody, TextFormatFlags.HorizontalCenter);
        }
        finally { g.Restore(saved); }
    }

    private void DrawEdges(Graphics g, ResizeSceneLayout layout, float s, double seconds)
    {
        var active = layout.Tiles.Where(tile => tile.RowIndex >= 0 &&
            (tile.RowIndex == layout.State.Selected || tile.RowIndex == layout.State.Hover)).Take(2).ToArray();
        foreach (int id in contours.Keys.Where(id => active.All(tile => tile.RowIndex != id)).ToArray())
        { contours[id].Dispose(); contours.Remove(id); }
        foreach (var tile in active)
        {
            var bounds = RectangleF.Inflate(tile.Bounds, -2 * s, -2 * s);
            if (bounds.Width < 1 || bounds.Height < 1) continue;
            if (!contours.TryGetValue(tile.RowIndex, out var contour) || contour.Bounds != bounds || contour.Scale != s)
            {
                contour?.Dispose(); contour = new EdgeContour(bounds, s); contours[tile.RowIndex] = contour;
            }
            var saved = g.Save();
            try
            {
                g.SetClip(tile.Bounds, CombineMode.Intersect);
                var body = RectangleF.Inflate(tile.Bounds, -8 * s, -8 * s);
                if (body.Width > 0 && body.Height > 0) g.SetClip(body, CombineMode.Exclude);
                contour.Draw(g, (float)((Math.Max(0, seconds) % (2.8 / 1.15)) / (2.8 / 1.15)));
            }
            finally { g.Restore(saved); }
        }
    }

    private void DrawList(Graphics g, ResizeSceneLayout layout, float s)
    {
        Panel(g, layout.ListPanel, s, 122);
        var title = new Rectangle(layout.ListPanel.X + (int)(16 * s), layout.ListPanel.Y, layout.ListPanel.Width - (int)(32 * s), (int)(44 * s));
        Text(g, layout.State.View == 2 ? "文件类型" : "文件与文件夹", heading, Theme.Text, title);
        var saved = g.Save();
        try
        {
            using var clip = Theme.Round(Inset(layout.ListPanel, s, s), Math.Max(1, Theme.CardRadius * s - s));
            g.SetClip(clip, CombineMode.Intersect);
            using (var header = new SolidBrush(Color.FromArgb(190, Theme.Sidebar))) g.FillRectangle(header, layout.ListHeader);
            string[] headers = ["名称", "大小 ↓", "占比"];
            int columnX = layout.ListHeader.X;
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = new Rectangle(columnX + (int)(12 * s), layout.ListHeader.Y, Math.Max(0, layout.ColumnWidths[i] - (int)(24 * s)), layout.ListHeader.Height);
                Text(g, headers[i], button, Theme.Text, cell, i == 0 ? TextFormatFlags.Left : TextFormatFlags.Right);
                columnX += layout.ColumnWidths[i];
            }
            g.SetClip(layout.ListBody, CombineMode.Intersect);
            var byId = layout.Data.ToDictionary(row => row.Id);
            int rowOrder = layout.State.Scroll;
            foreach (var placed in layout.Rows)
            {
                var row = byId[placed.RowIndex]; var bounds = placed.Bounds;
                if (row.Id == layout.State.Selected)
                { using var selected = new SolidBrush(Color.FromArgb(205, Theme.Selection)); g.FillRectangle(selected, bounds); }
                else if (rowOrder % 2 != 0)
                { using var alternate = new SolidBrush(Color.FromArgb(38, Theme.Sidebar)); g.FillRectangle(alternate, bounds); }
                rowOrder++;
                using (var line = new Pen(Color.FromArgb(110, Theme.Border), Math.Max(1, s)))
                    g.DrawLine(line, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
                var first = new Rectangle(bounds.X, bounds.Y, layout.ColumnWidths[0], bounds.Height);
                var bands = FileListPresentation.Bands(first, layout.Dpi, layout.State.View == 1);
                using (var dot = new SolidBrush(Theme.CategoryColor(row.Category))) g.FillRectangle(dot, bands.Dot);
                Text(g, row.Name, body, Theme.Text, bands.Name);
                if (!bands.Path.IsEmpty) Text(g, row.Path, caption, Theme.Muted, bands.Path);
                var size = new Rectangle(bounds.X + layout.ColumnWidths[0] + (int)(6 * s), bounds.Y, Math.Max(0, layout.ColumnWidths[1] - (int)(18 * s)), bounds.Height);
                var percent = new Rectangle(size.Right + (int)(12 * s), bounds.Y, Math.Max(0, layout.ColumnWidths[2] - (int)(18 * s)), bounds.Height);
                Text(g, SizeFormat.Bytes(row.Bytes(layout.State.Metric)), body, Theme.Text, size, TextFormatFlags.Right);
                Text(g, SizeFormat.Percent(row.Bytes(layout.State.Metric), layout.Total), body, Theme.Text, percent, TextFormatFlags.Right);
            }
            if (layout.MaxScroll > 0)
            {
                int track = layout.ListBody.Height, thumb = Math.Max((int)(24 * s), track * Math.Min(layout.Rows.Count, layout.Data.Count) / Math.Max(1, layout.Data.Count));
                int top = layout.ListBody.Top + (track - thumb) * layout.State.Scroll / layout.MaxScroll;
                var bar = new RectangleF(layout.ListBody.Right - 6 * s, top, 3 * s, thumb);
                using var shape = Theme.Round(bar, 1.5f * s);
                using var ink = new SolidBrush(Color.FromArgb(92, Theme.Muted)); g.FillPath(ink, shape);
            }
        }
        finally { g.Restore(saved); }
    }

    private void DrawDetails(Graphics g, ResizeSceneLayout layout, float s, Image? icon)
    {
        Panel(g, layout.Details, s, 142);
        var selected = layout.Data.FirstOrDefault(row => row.Id == layout.State.Selected);
        var title = new Rectangle(layout.Details.X + (int)(14 * s), layout.Details.Y + (int)(8 * s), Math.Max(0, layout.Details.Width - (int)(215 * s)), (int)(25 * s));
        var path = new Rectangle(title.X, title.Bottom, title.Width, (int)(21 * s));
        Text(g, selected?.Name ?? "选择图块或列表项目，查看详情", button, Theme.Text, title);
        Text(g, selected?.Path ?? "合成结果 · 仅验证实时缩放与绘制", caption, Theme.Muted, path);
        DrawButton(g, new Rectangle(layout.Details.Right - (int)(192 * s), layout.Details.Y + (int)(11 * s), (int)(180 * s), (int)(34 * s)), "在资源管理器中定位", s);
        var progress = layout.ProgressBounds;
        if (progress.Width <= 0 || progress.Height <= 0) return;
        var label = new Rectangle(progress.X, progress.Y, progress.Width, (int)(20 * s));
        Text(g, layout.State.Progress < 1 ? "合成进度更新" : "扫描完成", caption, Theme.Text, label);
        Text(g, $"{layout.State.Progress:P0}", caption, Theme.Accent, label, TextFormatFlags.Right);
        var track = new RectangleF(progress.X + 10 * s, progress.Y + 29 * s, Math.Max(1, progress.Width - 20 * s), 6 * s);
        using (var border = new Pen(Color.FromArgb(140, Theme.Border), Math.Max(1, s)))
        using (var shape = Theme.Round(track, 3 * s)) g.DrawPath(border, shape);
        var done = track; done.Width = Math.Max(0, track.Width * (float)layout.State.Progress);
        if (done.Width > 0)
        {
            using var pathShape = Theme.Round(done, Math.Min(3 * s, done.Width / 2));
            var palette = Theme.ProgressPalette;
            using var brush = new LinearGradientBrush(track, palette.Start, palette.End, LinearGradientMode.Horizontal);
            g.FillPath(brush, pathShape);
        }
        if (icon != null)
        {
            float cx = track.X + done.Width, cy = track.Y + track.Height / 2;
            for (int i = 5; i > 0; i--)
            {
                float radius = (10 + i) * s;
                using var glow = new SolidBrush(Color.FromArgb(6, Color.FromArgb(255, 213, 154)));
                g.FillEllipse(glow, cx - radius, cy - radius, radius * 2, radius * 2);
            }
            g.DrawImage(icon, new RectangleF(cx - 11 * s, cy - 11 * s, 22 * s, 22 * s));
        }
    }

    private void DrawInput(Graphics g, Rectangle bounds, string label, string content, bool arrow, float s, Color? text = null)
    {
        Panel(g, bounds, s, 142, Theme.ControlBorder);
        var area = Inset(bounds, 12 * s, 0);
        if (label.Length > 0)
        {
            int labelWidth = (int)(74 * s);
            Text(g, label, caption, Theme.Muted, new(area.X, area.Y, labelWidth, area.Height));
            area.X += labelWidth; area.Width = Math.Max(0, area.Width - labelWidth);
        }
        if (arrow) area.Width = Math.Max(0, area.Width - (int)(20 * s));
        Text(g, content, body, text ?? Theme.Text, area);
        if (arrow) Text(g, "⌄", caption, Theme.Muted, new(bounds.Right - (int)(26 * s), bounds.Y, (int)(18 * s), bounds.Height), TextFormatFlags.HorizontalCenter);
    }

    private void DrawToggle(Graphics g, Rectangle bounds, float s)
    {
        var track = new RectangleF(bounds.X + 2 * s, bounds.Y + (bounds.Height - 18 * s) / 2, 32 * s, 18 * s);
        using (var shape = Theme.Round(track, 9 * s))
        using (var ink = new SolidBrush(Color.FromArgb(50, Theme.Muted))) g.FillPath(ink, shape);
        using (var ink = new SolidBrush(Theme.Surface)) g.FillEllipse(ink, track.X + 3 * s, track.Y + 3 * s, 12 * s, 12 * s);
        Text(g, "NTFS 快扫", button, Theme.Text, new(bounds.X + (int)(43 * s), bounds.Y, Math.Max(0, bounds.Width - (int)(43 * s)), bounds.Height));
    }

    private void DrawButton(Graphics g, Rectangle bounds, string text, float s, bool primary = false, bool selected = false, bool left = false)
    {
        if (bounds.Width <= 2 || bounds.Height <= 2) return;
        var rect = Inset(bounds, s, s);
        using var shape = Theme.Round(rect, Theme.ControlRadius * s);
        using (var brush = new SolidBrush(primary ? Theme.Accent : selected ? Color.FromArgb(205, Theme.Selection) : Color.FromArgb(128, Theme.Sidebar))) g.FillPath(brush, shape);
        if (!primary && !left) { using var border = new Pen(Theme.Border, Math.Max(1, s)); g.DrawPath(border, shape); }
        if (selected)
        {
            using var mark = new SolidBrush(Theme.Accent);
            using var marker = Theme.Round(new RectangleF(rect.X + 7 * s, rect.Y + 11 * s, 3 * s, Math.Max(2, rect.Height - 22 * s)), 1.5f * s);
            g.FillPath(mark, marker);
        }
        Text(g, text, button, primary ? Color.White : selected ? Theme.Accent : Theme.Text,
            Inset(bounds, left ? 18 * s : 5 * s, 0), left ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter);
    }

    private static void Panel(Graphics g, Rectangle bounds, float s, int alpha, Color? edge = null)
    {
        if (bounds.Width <= 2 || bounds.Height <= 2) return;
        float inset = Math.Max(1, s) / 2;
        var rect = RectangleF.Inflate(bounds, -inset, -inset);
        using var shape = Theme.Round(rect, Math.Max(1, (edge.HasValue ? Theme.ControlRadius : Theme.CardRadius) * s - inset));
        using var brush = new SolidBrush(Color.FromArgb(alpha, Theme.Surface)); g.FillPath(brush, shape);
        using var pen = new Pen(edge ?? Theme.Border, Math.Max(1, s)); g.DrawPath(pen, shape);
    }

    private static Rectangle Inset(Rectangle bounds, float x, float y) => new(bounds.X + (int)x, bounds.Y + (int)y,
        Math.Max(0, bounds.Width - (int)(x * 2)), Math.Max(0, bounds.Height - (int)(y * 2)));

    private static void Text(Graphics g, string text, Font font, Color color, Rectangle bounds, TextFormatFlags align = TextFormatFlags.Left)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || !g.IsVisible(bounds)) return;
        var saved = g.Save();
        try
        {
            g.SetClip(bounds, CombineMode.Intersect);
            TextRenderer.DrawText(g, text, font, bounds, color, align | TextFormatFlags.EndEllipsis |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping);
        }
        finally { g.Restore(saved); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        body.Dispose(); caption.Dispose(); button.Dispose(); heading.Dispose(); logo.Dispose(); value.Dispose();
        foreach (var contour in contours.Values) contour.Dispose();
        contours.Clear();
    }

    private sealed class EdgeContour : IDisposable
    {
        internal RectangleF Bounds { get; }
        internal float Scale { get; }
        private readonly GraphicsPath path;
        private readonly Pen baseLine, travel;
        private readonly Pen[] halo;
        private readonly float perimeter;
        internal EdgeContour(RectangleF bounds, float scale)
        {
            Bounds = bounds; Scale = scale;
            float inset = Math.Min(.75f * scale, Math.Min(bounds.Width, bounds.Height) / 4);
            var line = RectangleF.Inflate(bounds, -inset, -inset);
            float radius = Math.Max(.01f, Math.Min(8 * scale, Math.Min(bounds.Width, bounds.Height) / 4) - inset);
            path = Theme.Round(line, radius);
            perimeter = Math.Max(1, 2 * (line.Width + line.Height - 4 * radius) + 2 * MathF.PI * radius);
            baseLine = Stroke(1.5f * scale, Color.FromArgb(117, 255, 233, 201));
            travel = Stroke(2 * scale, Color.FromArgb(242, 255, 249, 232), .24f);
            float[] widths = [9, 7, 5, 3, 1];
            float[] coverage = [.0668f, .3085f, .6915f, .9330f, .9876f];
            halo = new Pen[widths.Length];
            float previous = 0;
            for (int i = 0; i < halo.Length; i++)
            {
                float target = .60f * coverage[i];
                int alpha = (int)Math.Round(255 * (target - previous) / (1 - previous));
                halo[i] = Stroke(widths[i] * scale, Color.FromArgb(alpha, 255, 219, 178), .29f);
                previous = target;
            }
        }
        private Pen Stroke(float width, Color color, float fraction = 0)
        {
            var pen = new Pen(color, width) { DashCap = DashCap.Round, LineJoin = LineJoin.Round };
            if (fraction > 0) pen.DashPattern = [fraction * perimeter / width, (1 - fraction) * perimeter / width];
            return pen;
        }
        internal void Draw(Graphics g, float phase)
        {
            g.DrawPath(baseLine, path);
            foreach (var pen in halo) { pen.DashOffset = -phase * perimeter / pen.Width; g.DrawPath(pen, path); }
            travel.DashOffset = -phase * perimeter / travel.Width; g.DrawPath(travel, path);
        }
        public void Dispose()
        {
            path.Dispose(); baseLine.Dispose(); travel.Dispose(); foreach (var pen in halo) pen.Dispose();
        }
    }
}
