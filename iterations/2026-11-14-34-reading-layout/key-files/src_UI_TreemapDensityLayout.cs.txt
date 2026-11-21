namespace DiskAtlas;

// Presentation only: callers pass the bounded ViewQuery map (at most 240
// concrete entries plus its remainder), never the scanned tree or full list.
internal static class TreemapDensityLayout
{
    internal const float MinimumWidthDip = 52;
    internal const float MinimumHeightDip = 36;

    internal sealed record Result(List<MapItem> Items, List<MapTile> Tiles, Dictionary<MapItem, MapTile> TileLookup);

    internal static Result Build(IReadOnlyList<MapItem> source, RectangleF bounds, int dpi)
    {
        float scale = dpi > 0 ? dpi / 96f : 1;
        var original = source.ToArray();
        var displayed = original.ToList();
        var merged = new HashSet<MapItem>(ReferenceEqualityComparer.Instance);
        MapItem? remainder = null;
        while (true)
        {
            var tiles = TreemapLayout.Build(displayed, bounds);
            var small = tiles.Where(tile => !tile.Item.IsAggregate &&
                (tile.Bounds.Width < MinimumWidthDip * scale || tile.Bounds.Height < MinimumHeightDip * scale))
                .Select(tile => tile.Item).ToArray();
            if (small.Length == 0 || tiles.Count <= 1)
            {
                var lookup = new Dictionary<MapItem, MapTile>();
                foreach (var tile in tiles) lookup[tile.Item] = tile;
                if (remainder != null && lookup.TryGetValue(remainder, out var remainderTile))
                    foreach (var item in original)
                        if (merged.Contains(item)) lookup[item] = remainderTile;
                return new(displayed, tiles, lookup);
            }

            // Each pass removes at least one original positive item. An existing
            // query remainder joins the same group, with its complete metadata;
            // the generated remainder itself is never a candidate for removal.
            foreach (var item in small) merged.Add(item);
            foreach (var item in original)
                if (item.IsAggregate || item.Size == 0) merged.Add(item);
            long size = 0, count = 0, unknown = 0;
            foreach (var item in original)
            {
                if (!merged.Contains(item)) continue;
                size = checked(size + item.Size);
                count = checked(count + item.Count);
                unknown = checked(unknown + item.UnknownFiles);
            }
            remainder = new MapItem($"其余 {SizeFormat.Number(count)} 项", size, null, "其他", count, true, unknown);
            displayed = original.Where(item => !merged.Contains(item)).ToList();
            displayed.Add(remainder);
            // Re-layout uses the actual combined bytes, with no minimum weight
            // or enlarged area for the remainder. Repeated resizes start over
            // from source, so merged entries can become individual tiles again.
        }
    }
}
