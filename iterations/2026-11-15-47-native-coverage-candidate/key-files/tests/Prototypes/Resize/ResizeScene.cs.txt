using System.Drawing;

namespace DiskAtlas.ResizePrototype;

// Bounded, public synthetic content. Nothing here traverses or opens a path.
internal sealed record ResizeSceneRow(int Id, string Name, string Path, long Size, long Allocated,
    string Category, bool IsDirectory)
{
    internal long Bytes(int metric) => metric == 1 ? Allocated : Size;
}

internal sealed class ResizeSceneState
{
    internal int View { get; set; }
    internal int Selected { get; set; } = -1;
    internal int Hover { get; set; } = -1;
    internal int Scroll { get; set; }
    internal string Search { get; set; } = "";
    internal int Metric { get; set; }
    internal double Progress { get; set; } = 1;
    internal long Revision { get; set; }

    internal ResizeSceneSnapshot Snapshot() => new(Math.Clamp(View, 0, 2), Selected, Hover,
        Math.Max(0, Scroll), Search ?? "", Metric == 1 ? 1 : 0,
        double.IsFinite(Progress) ? Math.Clamp(Progress, 0, 1) : 0, Revision);
}

internal sealed record ResizeSceneSnapshot(int View, int Selected, int Hover, int Scroll,
    string Search, int Metric, double Progress, long Revision);
internal sealed record ResizeSceneRowLayout(int RowIndex, Rectangle Bounds);
internal sealed record ResizeSceneTile(int RowIndex, RectangleF Bounds, MapItem Item,
    IReadOnlyList<MapTile> Nested);

internal sealed class ResizeSceneLayout
{
    internal Size PixelSize { get; private init; }
    internal int Dpi { get; private init; }
    internal long Revision => State.Revision;
    internal ResizeSceneSnapshot State { get; private init; } = new(0, -1, -1, 0, "", 0, 1, 0);
    internal IReadOnlyList<ResizeSceneRow> Data { get; private init; } = Array.Empty<ResizeSceneRow>();
    internal IReadOnlyList<ResizeSceneRowLayout> Rows { get; private init; } = Array.Empty<ResizeSceneRowLayout>();
    internal IReadOnlyList<ResizeSceneTile> Tiles { get; private init; } = Array.Empty<ResizeSceneTile>();
    internal Rectangle Sidebar { get; private init; }
    internal Rectangle Search { get; private init; }
    internal Rectangle Strategy { get; private init; }
    internal Rectangle Metric { get; private init; }
    internal Rectangle FastToggle { get; private init; }
    internal Rectangle Path { get; private init; }
    internal Rectangle Up { get; private init; }
    internal Rectangle Home { get; private init; }
    internal Rectangle MapPanel { get; private init; }
    internal Rectangle MapHeader { get; private init; }
    internal Rectangle MapBody { get; private init; }
    internal Rectangle ListPanel { get; private init; }
    internal Rectangle ListHeader { get; private init; }
    internal Rectangle ListBody { get; private init; }
    internal Rectangle Details { get; private init; }
    internal Rectangle ProgressBounds { get; private init; }
    internal Rectangle Activity { get; private init; }
    internal Rectangle[] Navigation { get; private init; } = new Rectangle[3];
    internal Rectangle[] Toolbar { get; private init; } = new Rectangle[4];
    internal Rectangle[] Stats { get; private init; } = new Rectangle[4];
    internal int[] ColumnWidths { get; private init; } = new int[3];
    internal int RowHeight { get; private init; }
    internal int MaxScroll { get; private init; }
    internal long Total => Data.Sum(row => row.Bytes(State.Metric));

    internal static IReadOnlyList<ResizeSceneRow> SeedData { get; } = Array.AsReadOnly(CreateData());

    internal static ResizeSceneLayout Build(Size pixels, int dpi, ResizeSceneState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (pixels.Width < 0 || pixels.Height < 0 || pixels.Width > 32768 || pixels.Height > 32768)
            throw new ArgumentOutOfRangeException(nameof(pixels), "ResizeSceneSize");
        if (dpi is < 48 or > 768) throw new ArgumentOutOfRangeException(nameof(dpi), "ResizeSceneDpi");
        var snapshot = state.Snapshot();
        float scale = dpi / 96f;
        int D(float value) => Math.Max(0, (int)MathF.Round(value * scale));
        var client = new Rectangle(Point.Empty, pixels);
        Rectangle R(int x, int y, int width, int height) => Rectangle.Intersect(client,
            new Rectangle(x, y, Math.Max(0, width), Math.Max(0, height)));
        var data = CurrentData(snapshot);
        int side = Math.Min(pixels.Width, D(232));
        int x = side + D(28), width = Math.Max(0, pixels.Width - x - D(28)), top = D(18);
        var toolbar = new Rectangle[4];
        int buttonX = x;
        int[] buttonWidths = [142, 118, 112, 80];
        for (int i = 0; i < toolbar.Length; i++)
        {
            toolbar[i] = R(buttonX, top + D(4), D(buttonWidths[i] - (i < 3 ? 8 : 0)), D(36));
            buttonX += D(buttonWidths[i]);
        }
        int settingsY = top + D(50), statsY = settingsY + D(52), pathY = statsY + D(80);
        var strategy = R(x, settingsY, Math.Min(width, D(204)), D(38));
        var metric = R(x + D(220), settingsY, Math.Min(Math.Max(0, width - D(220)), D(220)), D(38));
        var fast = R(x + D(456), settingsY, Math.Min(Math.Max(0, width - D(456)), D(180)), D(38));
        var stats = new Rectangle[4];
        for (int i = 0; i < stats.Length; i++)
        {
            int left = x + width * i / 4, right = x + width * (i + 1) / 4;
            stats[i] = R(left, statsY, right - left - D(i == 3 ? 0 : 8), D(72));
        }
        int searchWidth = Math.Min(D(230), Math.Max(0, width - D(240)));
        int searchX = x + width - searchWidth;
        var up = R(x, pathY + D(8), D(94), D(35));
        var home = R(x + D(100), pathY + D(8), D(110), D(35));
        var path = R(x + D(222), pathY + D(8), searchX - x - D(232), D(35));
        var search = R(searchX, pathY + D(8), searchWidth, D(35));
        int mapTop = pathY + D(52), footerTop = Math.Max(mapTop, pixels.Height - D(126));
        int contentHeight = Math.Max(0, footerTop - mapTop), split = Math.Max(0, (int)((width - D(14)) * .59));
        var mapPanel = R(x, mapTop, split, contentHeight);
        var listPanel = R(x + split + D(14), mapTop, Math.Max(0, width - split - D(14)), contentHeight);
        var mapHeader = R(mapPanel.X + D(1), mapPanel.Y + D(1), mapPanel.Width - D(2), D(44));
        var mapBody = R(mapPanel.X + D(8), mapHeader.Bottom + D(2), mapPanel.Width - D(16),
            mapPanel.Bottom - mapHeader.Bottom - D(10));
        var listHeader = R(listPanel.X + D(1), listPanel.Y + D(44), listPanel.Width - D(2), D(38));
        var listBody = R(listPanel.X + D(1), listHeader.Bottom, listPanel.Width - D(2), listPanel.Bottom - listHeader.Bottom - D(1));
        int rowHeight = FileListPresentation.RowHeight(dpi, snapshot.View == 1);
        int visibleFull = Math.Max(1, listBody.Height / rowHeight), maxScroll = Math.Max(0, data.Length - visibleFull);
        snapshot = snapshot with { Scroll = Math.Clamp(snapshot.Scroll, 0, maxScroll) };
        var rows = new List<ResizeSceneRowLayout>();
        for (int i = snapshot.Scroll; i < data.Length; i++)
        {
            int rowY = listBody.Y + (i - snapshot.Scroll) * rowHeight;
            if (rowY >= listBody.Bottom) break;
            rows.Add(new(data[i].Id, new Rectangle(listBody.X, rowY, listBody.Width, rowHeight)));
        }
        var mapItems = data.Take(240).Select(row => new MapItem(row.Name, row.Bytes(snapshot.Metric), null, row.Category)).ToArray();
        var ids = new Dictionary<MapItem, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < mapItems.Length; i++) ids.Add(mapItems[i], data[i].Id);
        var tileLayout = TreemapDensityLayout.Build(mapItems, mapBody, dpi);
        var tiles = tileLayout.Tiles.Select(tile =>
        {
            int id = ids.TryGetValue(tile.Item, out int found) ? found : -1;
            IReadOnlyList<MapTile> nested = Array.Empty<MapTile>();
            if (data.FirstOrDefault(row => row.Id == id) is { IsDirectory: true } &&
                tile.Bounds.Width > D(90) && tile.Bounds.Height > D(110))
            {
                var inner = new RectangleF(tile.Bounds.X + D(9), tile.Bounds.Y + D(63),
                    tile.Bounds.Width - D(18), tile.Bounds.Height - D(72));
                string[] names = id switch
                {
                    0 => ["travel-demo.mp4", "recording-demo.mp4"],
                    1 => ["app-demo.exe", "tools-demo.zip"],
                    2 => ["photo-collection.jpg", "wallpaper-demo.png"],
                    _ => ["project-backup.zip", "archive-demo.bin"]
                };
                nested = TreemapLayout.Build(names.Select((name, index) => new MapItem(name,
                    tile.Item.Size * (index == 0 ? 6 : 4) / 10, null, tile.Item.Category)).ToArray(), inner).AsReadOnly();
            }
            return new ResizeSceneTile(id, tile.Bounds, tile.Item, nested);
        }).ToArray();
        var navigation = Enumerable.Range(0, 3).Select(i => R(D(14), D(166 + i * 52), side - D(28), D(44))).ToArray();
        var details = R(x, footerTop + D(8), width, Math.Max(0, pixels.Height - D(12) - footerTop - D(8)));
        return new ResizeSceneLayout
        {
            PixelSize = pixels, Dpi = dpi, State = snapshot, Data = Array.AsReadOnly(data),
            Rows = rows.AsReadOnly(), Tiles = Array.AsReadOnly(tiles), Sidebar = R(0, 0, side, pixels.Height),
            Toolbar = toolbar, Navigation = navigation, Stats = stats, Strategy = strategy, Metric = metric, FastToggle = fast,
            Up = up, Home = home, Path = path, Search = search, MapPanel = mapPanel, MapHeader = mapHeader,
            MapBody = mapBody, ListPanel = listPanel, ListHeader = listHeader, ListBody = listBody,
            Details = details, ProgressBounds = R(x + D(14), details.Y + D(57), width - D(28), D(45)),
            Activity = R(D(14), Math.Max(D(620), pixels.Height - D(60)), side - D(28), D(44)),
            RowHeight = rowHeight, MaxScroll = maxScroll,
            ColumnWidths = [listBody.Width * 50 / 100, listBody.Width * 28 / 100, listBody.Width - listBody.Width * 50 / 100 - listBody.Width * 28 / 100]
        };
    }

    private static ResizeSceneRow[] CurrentData(ResizeSceneSnapshot state)
    {
        IEnumerable<ResizeSceneRow> source = state.View == 1 ? SeedData.Where(row => !row.IsDirectory) : SeedData;
        if (state.View == 2)
            source = SeedData.Where(row => !row.IsDirectory).GroupBy(row => row.Category).Select((group, index) =>
                new ResizeSceneRow(100 + index, group.Key, "合成文件类型", group.Sum(row => row.Size),
                    group.Sum(row => row.Allocated), group.Key, false));
        if (!string.IsNullOrEmpty(state.Search))
            source = source.Where(row => row.Name.Contains(state.Search, StringComparison.OrdinalIgnoreCase) ||
                row.Path.Contains(state.Search, StringComparison.OrdinalIgnoreCase));
        return source.OrderByDescending(row => row.Bytes(state.Metric)).ThenBy(row => row.Id).ToArray();
    }

    private static ResizeSceneRow[] CreateData()
    {
        const long mb = 1024 * 1024;
        List<ResizeSceneRow> data =
        [
            new(0, "视频 Videos", @"X:\AsukaLens-Demo\视频 Videos", 12 * mb, 12 * mb, "文件夹", true),
            new(1, "应用 Apps", @"X:\AsukaLens-Demo\应用 Apps", 6 * mb, 6 * mb, "文件夹", true),
            new(2, "图片 Pictures", @"X:\AsukaLens-Demo\图片 Pictures", 5 * mb, 5 * mb, "文件夹", true),
            new(3, "项目 Projects", @"X:\AsukaLens-Demo\项目 Projects", 3 * mb, 3 * mb, "文件夹", true),
            new(4, "文档 Documents", @"X:\AsukaLens-Demo\文档 Documents", mb + mb / 2, mb + mb / 2, "文件夹", true)
        ];
        (string Name, string Extension, string Category)[] categories =
        [("演示视频片段", ".mp4", "视频"), ("旅行照片合集", ".jpg", "图片"), ("项目备份", ".zip", "压缩包"),
         ("背景音乐", ".flac", "音频"), ("使用说明", ".pdf", "文档"), ("应用模块", ".dll", "其他"),
         ("绘图组件", ".cs", "代码")];
        for (int i = 5; i < 80; i++)
        {
            var category = categories[(i - 5) % categories.Length];
            string name = i % 9 == 0 ? $"{category.Name}-包含中英文与较长名称-Rendering-Sample-{i:00}{category.Extension}"
                : $"{category.Name}-sample-{i:00}{category.Extension}";
            long bytes = (81 - i) * 4096L + i * 137;
            data.Add(new(i, name, $@"X:\AsukaLens-Demo\公开合成数据\分组-{i % 5}\{name}", bytes,
                (bytes + 4095) / 4096 * 4096, category.Category, false));
        }
        return data.ToArray();
    }
}
