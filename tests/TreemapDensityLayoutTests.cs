using DiskAtlas;
using System.Drawing;

// No windows, file-system access, scanning, or configuration are involved.
internal static class TreemapDensityLayoutTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Treemap density: " + message);
            passed++;
        }
        void Conserves(IReadOnlyList<MapItem> source, TreemapDensityLayout.Result result)
        {
            Check(source.Sum(item => item.Size) == result.Items.Sum(item => item.Size), "displayed bytes are conserved");
            Check(source.Sum(item => item.Count) == result.Items.Sum(item => item.Count), "displayed entry counts are conserved");
            Check(source.Sum(item => item.UnknownFiles) == result.Items.Sum(item => item.UnknownFiles), "unknown counts are conserved");
        }
        void Areas(TreemapDensityLayout.Result result, RectangleF bounds)
        {
            double totalArea = (double)bounds.Width * bounds.Height;
            double totalSize = result.Items.Sum(item => (double)Math.Max(0, item.Size));
            double paintedArea = 0;
            bool contained = true, weighted = true, disjoint = true;
            for (int i = 0; i < result.Tiles.Count; i++)
            {
                var tile = result.Tiles[i];
                double area = (double)tile.Bounds.Width * tile.Bounds.Height;
                paintedArea += area;
                contained &= tile.Bounds.Width > 0 && tile.Bounds.Height > 0 &&
                    tile.Bounds.Left >= bounds.Left - .01f && tile.Bounds.Top >= bounds.Top - .01f &&
                    tile.Bounds.Right <= bounds.Right + .01f && tile.Bounds.Bottom <= bounds.Bottom + .01f;
                weighted &= Math.Abs(area - totalArea * tile.Item.Size / totalSize) <= Math.Max(.15, totalArea * .000002);
                for (int j = 0; j < i; j++)
                {
                    var overlap = RectangleF.Intersect(tile.Bounds, result.Tiles[j].Bounds);
                    disjoint &= overlap.Width <= .01f || overlap.Height <= .01f;
                }
            }
            Check(contained, "all tiles stay inside the available rectangle");
            Check(weighted, "each tile area follows its actual byte weight");
            Check(disjoint, "displayed tiles do not overlap");
            Check(Math.Abs(paintedArea - totalArea) <= Math.Max(.2, totalArea * .000002), "positive tiles cover the available area");
        }

        var readable = new[] { new MapItem("large", 700, null, "其他"),
            new MapItem("medium", 200, null, "图片"), new MapItem("small", 100, null, "文档") };
        var readableBounds = new RectangleF(10, 12, 1200, 800);
        var readableLayout = TreemapDensityLayout.Build(readable, readableBounds, 96);
        Check(readableLayout.Items.Count == readable.Length && readableLayout.Items.All(item => !item.IsAggregate),
            "readable items stay individual without a fixed tile-count target");
        Check(readable.All(item => ReferenceEquals(readableLayout.TileLookup[item].Item, item)), "individual item identities are retained");
        Conserves(readable, readableLayout); Areas(readableLayout, readableBounds);

        // Exercise the actual query contract: 500 complete list rows, at most
        // 240 concrete map items, and the pre-existing query remainder.
        var directory = new DiskEntry("synthetic-density", true);
        for (int i = 0; i < 500; i++)
        {
            var entry = DiskEntry.FromName($"sample-{i:000}.dat", false, directory, ".dat");
            entry.Size = i < 7 ? 70_000 - i * 5_000 : 100;
            entry.FileCount = 1;
            directory.MutableChildren.Add(entry); directory.Size += entry.Size; directory.FileCount++;
        }
        var query = ViewQuery.Build(new(directory, ViewMode.LargestFiles), CancellationToken.None);
        var originalRows = query.Rows.ToArray(); var originalMap = query.MapItems.ToArray();
        Check(query.Rows.Count == 500 && query.MapItems.Count == ViewQuery.MapLimit + 1,
            "fixture reaches the complete-list and bounded-map contract");
        var compactBounds = new RectangleF(0, 0, 600, 400);
        var compact = TreemapDensityLayout.Build(query.MapItems, compactBounds, 96);
        Check(compact.Tiles.Count < 20 && compact.Tiles.Count > 1, "a heavy head and many small files avoid a mosaic");
        var remainder = compact.Items.Single(item => item.IsAggregate);
        Check(remainder.Count == 493 && remainder.Size == 49_300 && remainder.Name.Contains(SizeFormat.Number(493)),
            "query remainder counts as all of its members when merged");
        Check(query.Rows.Count == 500 && originalRows.Zip(query.Rows).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
            originalMap.Zip(query.MapItems).All(pair => ReferenceEquals(pair.First, pair.Second)),
            "presentation does not mutate the source map or complete list");
        var mergedOriginal = originalMap.First(item => !item.IsAggregate && item.Size == 100);
        Check(ReferenceEquals(compact.TileLookup[mergedOriginal].Item, remainder), "a merged original selection resolves to the remainder outline");
        Check(ReferenceEquals(compact.TileLookup[originalMap.Single(item => item.IsAggregate)].Item, remainder),
            "the old remainder selection resolves to the same new outline");
        Check(compact.TileLookup[mergedOriginal] == compact.TileLookup[remainder], "hover and an original selection share one displayed tile");
        Check(!compact.Items.Contains(mergedOriginal) && compact.TileLookup.ContainsKey(mergedOriginal) && !mergedOriginal.IsAggregate,
            "an original selection stays addressable after its individual tile is hidden");
        Conserves(originalMap, compact); Areas(compact, compactBounds);

        var uniform = Enumerable.Range(0, 240).Select(i => new MapItem($"equal-{i:000}", 100, null, "图片")).ToArray();
        var smallBounds = new RectangleF(0, 0, 420, 280); var largeBounds = new RectangleF(0, 0, 2400, 1600);
        var small = TreemapDensityLayout.Build(uniform, smallBounds, 96);
        var large = TreemapDensityLayout.Build(uniform, largeBounds, 96);
        Check(small.Tiles.Count < large.Tiles.Count && large.Tiles.Count == uniform.Length, "larger space restores readable original tiles");
        Check(large.Items.All(item => !item.IsAggregate) && large.TileLookup[uniform[10]].Item == uniform[10],
            "resize recovery uses original entries instead of the previous aggregate");
        var smallAgain = TreemapDensityLayout.Build(uniform, smallBounds, 96);
        Check(small.Tiles.Select(tile => (tile.Item, tile.Bounds)).SequenceEqual(smallAgain.Tiles.Select(tile => (tile.Item, tile.Bounds))),
            "small-large-small resizing is deterministic with no cumulative loss");
        Conserves(uniform, small); Conserves(uniform, large); Areas(small, smallBounds); Areas(large, largeBounds);

        var dpiItems = uniform.Take(80).ToArray(); var dpiBounds = new RectangleF(6, 8, 720, 480);
        var at96 = TreemapDensityLayout.Build(dpiItems, dpiBounds, 96);
        var at192SamePixels = TreemapDensityLayout.Build(dpiItems, dpiBounds, 192);
        var scaledBounds = new RectangleF(12, 16, 1440, 960);
        var at192SameDips = TreemapDensityLayout.Build(dpiItems, scaledBounds, 192);
        Check(at96.Tiles.Count > at192SamePixels.Tiles.Count, "higher DPI uses larger physical readability thresholds");
        Check(at96.Tiles.Select(tile => tile.Item).SequenceEqual(at192SameDips.Tiles.Select(tile => tile.Item)),
            "the same DIP area retains the same individual or grouped entries");
        Check(at96.Tiles.Zip(at192SameDips.Tiles).All(pair => Math.Abs(pair.First.Bounds.Width * 2 - pair.Second.Bounds.Width) < .02f &&
            Math.Abs(pair.First.Bounds.Height * 2 - pair.Second.Bounds.Height) < .02f), "DPI scaling preserves geometry proportions");
        Conserves(dpiItems, at192SamePixels); Areas(at96, dpiBounds); Areas(at192SamePixels, dpiBounds); Areas(at192SameDips, scaledBounds);

        var metadata = new[] { new MapItem("known", 9_000, null, "图片"),
            new MapItem("partly-known", 1, null, "其他", 3, UnknownFiles: 2),
            new MapItem("zero-unknown", 0, null, "其他", 5, UnknownFiles: 4),
            new MapItem("old-remainder", 9, null, "其他", 70, true, 7) };
        var metadataBounds = new RectangleF(0, 0, 500, 350);
        var metadataLayout = TreemapDensityLayout.Build(metadata, metadataBounds, 96);
        var metadataRemainder = metadataLayout.Items.Single(item => item.IsAggregate);
        Check(metadataRemainder.Size == 10 && metadataRemainder.Count == 78 && metadataRemainder.UnknownFiles == 13,
            "zero bytes and unknown metadata are not discarded or treated as one old aggregate");
        Check(metadataLayout.TileLookup[metadata[2]].Item == metadataRemainder, "zero-byte members can map to an existing positive remainder without gaining weight");
        Check(metadataLayout.TileLookup[metadata[0]].Item == metadata[0], "a readable original keeps its own selection outline");
        Check(!metadataLayout.TileLookup.ContainsKey(new MapItem("outside-input", 1, null, "其他")),
            "unrelated or unavailable upstream members are not guessed into an aggregate");
        Conserves(metadata, metadataLayout); Areas(metadataLayout, metadataBounds);

        var allZero = new[] { new MapItem("empty", 0, null, "其他"), new MapItem("unknown", 0, null, "其他", 7, UnknownFiles: 3) };
        var zeroLayout = TreemapDensityLayout.Build(allZero, smallBounds, 96);
        Check(zeroLayout.Tiles.Count == 0 && zeroLayout.TileLookup.Count == 0, "zero and unknown-only input never gets fabricated area");
        Conserves(allZero, zeroLayout);
        var unavailable = TreemapDensityLayout.Build(readable, RectangleF.Empty, 96);
        Check(unavailable.Tiles.Count == 0 && unavailable.Items.SequenceEqual(readable), "zero available area retains the input for later layout");
        var invalidBounds = TreemapDensityLayout.Build(readable, new RectangleF(float.NaN, 0, 500, 300), 96);
        Check(invalidBounds.Tiles.Count == 0, "non-finite bounds do not create tiles");
        var single = TreemapDensityLayout.Build([readable[0]], new RectangleF(0, 0, 10, 10), 96);
        Check(single.Tiles.Count == 1 && ReferenceEquals(single.Tiles[0].Item, readable[0]), "one item retains its identity even in a tiny viewport");
        var empty = TreemapDensityLayout.Build([], smallBounds, 96);
        Check(empty.Items.Count == 0 && empty.Tiles.Count == 0 && empty.TileLookup.Count == 0, "empty input remains empty");
        return passed;
    }
}
