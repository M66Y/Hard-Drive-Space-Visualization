namespace DiskAtlas;

public enum ViewMode { Folder, LargestFiles, FileTypes }
public enum ViewSort { Size, Name, Count, Location }
public record ViewRequest(DiskEntry Directory, ViewMode Mode, string Search = "", ViewSort Sort = ViewSort.Size, bool Ascending = false, string? ExtensionFilter = null, SizeMetric Metric = SizeMetric.Logical);
public record ViewResult(List<MapItem> Rows, List<MapItem> MapItems, long VisibleBytes, long MatchedCount);

public readonly record struct PageSlice(int Index, int Pages, int Offset, int Length)
{
    public const int Size = 1000;
    public static PageSlice Create(int total, int index)
    {
        if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));
        int pages = total == 0 ? 1 : (total - 1) / Size + 1;
        int safeIndex = Math.Clamp(index, 0, pages - 1);
        int offset = safeIndex * Size;
        return new PageSlice(safeIndex, pages, offset, Math.Min(Size, total - offset));
    }
}

public static class ViewQuery
{
    public const int LargestLimit = 500;
    public const int MapLimit = 240;
    private static readonly IComparer<MapItem> Quality = Comparer<MapItem>.Create((a, b) =>
    {
        int size = a.Size.CompareTo(b.Size);
        if (size != 0) return size;
        // A lexically smaller path wins ties, independent of enumeration order.
        return a.Entry != null && b.Entry != null ? DiskEntry.ComparePath(b.Entry, a.Entry) : StringComparer.OrdinalIgnoreCase.Compare(b.Name, a.Name);
    });

    public static ViewResult Build(ViewRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rows = new List<MapItem>();
        string query = request.Search.Trim();
        long matched = 0;
        bool Matches(string name) => query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase);
        if (request.Mode == ViewMode.FileTypes)
        {
            var totals = new Dictionary<string, (long Bytes, long Count, long Unknown)>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in request.Directory.Descendants())
            {
                token.ThrowIfCancellationRequested();
                if (entry.IsDirectory) continue;
                var previous = totals.GetValueOrDefault(entry.Extension);
                totals[entry.Extension] = (previous.Bytes + entry.Bytes(request.Metric), previous.Count + 1, previous.Unknown + (request.Metric == SizeMetric.Allocated ? entry.UnknownAllocationFiles : 0));
            }
            foreach (var pair in totals)
            {
                string category = FileKinds.Category(pair.Key);
                if (!Matches(pair.Key) && !Matches(category)) continue;
                rows.Add(new MapItem(pair.Key, pair.Value.Bytes, null, category, pair.Value.Count, UnknownFiles: pair.Value.Unknown));
            }
            matched = rows.Count;
        }
        else
        {
            var top = new PriorityQueue<MapItem, MapItem>(Quality);
            var source = request.Mode == ViewMode.Folder ? request.Directory.Children : request.Directory.Descendants();
            foreach (var entry in source)
            {
                token.ThrowIfCancellationRequested();
                if (request.Mode == ViewMode.LargestFiles && entry.IsDirectory) continue;
                if (request.ExtensionFilter != null && !entry.Extension.Equals(request.ExtensionFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Matches(entry.Name)) continue;
                matched++;
                long size = entry.Bytes(request.Metric);
                if (request.Mode == ViewMode.LargestFiles && top.Count == LargestLimit)
                {
                    var smallest = top.Peek();
                    if (size < smallest.Size || size == smallest.Size && DiskEntry.ComparePath(entry, smallest.Entry!) >= 0) continue;
                }
                var item = new MapItem(entry.Name, size, entry, FileKinds.Category(entry.Extension), UnknownFiles: request.Metric == SizeMetric.Allocated ? entry.UnknownAllocationFiles : 0);
                if (request.Mode == ViewMode.Folder) rows.Add(item);
                else Keep(top, item, LargestLimit);
            }
            if (request.Mode == ViewMode.LargestFiles) rows.AddRange(top.UnorderedItems.Select(i => i.Element));
        }
        token.ThrowIfCancellationRequested();
        rows.Sort((a, b) =>
        {
            int compared = request.Sort switch
            {
                ViewSort.Name => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name),
                ViewSort.Count => a.Count.CompareTo(b.Count),
                ViewSort.Location => StringComparer.CurrentCultureIgnoreCase.Compare(a.Entry?.Parent?.Path, b.Entry?.Parent?.Path),
                _ => a.Size.CompareTo(b.Size)
            };
            if (compared != 0) return request.Ascending ? compared : -compared;
            return a.Entry != null && b.Entry != null ? DiskEntry.ComparePath(a.Entry, b.Entry) : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        token.ThrowIfCancellationRequested();
        var mapTop = new PriorityQueue<MapItem, MapItem>(Quality);
        long visible = 0, positive = 0;
        foreach (var item in rows)
        {
            token.ThrowIfCancellationRequested();
            visible += item.Size;
            if (item.Size <= 0) continue;
            positive++;
            Keep(mapTop, item, MapLimit);
        }
        var map = mapTop.UnorderedItems.Select(i => i.Element).OrderByDescending(i => i.Size).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        long remainder = visible - map.Sum(i => i.Size);
        if (remainder > 0) map.Add(new MapItem($"其余 {positive - map.Count:N0} 项", remainder, null, "其他", positive - map.Count, true));
        return new ViewResult(rows, map, visible, matched);
    }

    private static void Keep(PriorityQueue<MapItem, MapItem> queue, MapItem item, int limit)
    {
        if (queue.Count < limit) queue.Enqueue(item, item);
        else queue.EnqueueDequeue(item, item);
    }
}
