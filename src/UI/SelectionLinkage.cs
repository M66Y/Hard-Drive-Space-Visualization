namespace DiskAtlas;

// All matches stay inside one published result tree. Names, sizes and native
// file IDs alone cannot distinguish two paths of the same hard-linked file.
internal static class SelectionLinkage
{
    internal static int FindRow(IReadOnlyList<MapItem> rows, MapItem? item)
    {
        if (item == null) return -1;
        for (int i = 0; i < rows.Count; i++)
        {
            var candidate = rows[i];
            if (item.Entry != null ? ReferenceEquals(candidate.Entry, item.Entry) :
                item.IsAggregate ? ReferenceEquals(candidate, item) :
                candidate.Entry == null && !candidate.IsAggregate &&
                StringComparer.OrdinalIgnoreCase.Equals(candidate.Name, item.Name) && candidate.Category == item.Category)
                return i;
        }
        return -1;
    }

    internal static int ContainingRow(IReadOnlyList<MapItem> rows, MapItem? item, DiskEntry? directory)
    {
        if (item?.Entry == null || directory == null) return -1;
        var ancestor = item.Entry.Parent;
        if (ReferenceEquals(ancestor, directory)) return -1;
        while (ancestor?.Parent != null && !ReferenceEquals(ancestor.Parent, directory)) ancestor = ancestor.Parent;
        return ancestor != null && ReferenceEquals(ancestor.Parent, directory)
            ? FindRow(rows, new(ancestor.Name, 0, ancestor, "")) : -1;
    }

    internal static MapItem? Rebind(IReadOnlyList<MapItem> rows, MapItem? previous, DiskEntry directory, SizeMetric metric, int mode)
    {
        int index = FindRow(rows, previous);
        if (index >= 0) return rows[index];
        // Only a still-visible ancestor can justify preserving an internal
        // selection. Never clear filters or traverse the result tree to guess.
        if (mode != 0 || previous?.Entry is not { } entry || ContainingRow(rows, previous, directory) < 0) return null;
        return new(entry.Name, entry.Bytes(metric), entry, FileKinds.Category(entry.Extension),
            UnknownFiles: metric == SizeMetric.Allocated ? entry.UnknownAllocationFiles : 0);
    }

    internal static int ScrollTarget(int first, int fullyVisibleRows, int target)
    {
        if (first < 0) return target;
        int count = Math.Max(1, fullyVisibleRows);
        return target < first ? target : target >= first + count ? Math.Max(0, target - count + 1) : first;
    }
}

internal enum SelectionAction { None, EnterDirectory, RevealFile, ShowType, ShowList }

internal sealed record SelectionDetails(string Name, string Kind, string LogicalSize, string Allocation,
    string Path, string Modified, string Note, string Scope, SelectionAction Action)
{
    internal static SelectionDetails Create(MapItem item, SizeMetric metric, int mode, string scope)
    {
        if (item.Entry is not { } entry)
        {
            bool type = !item.IsAggregate && mode == 2;
            string value = item.UnknownFiles > 0 ? item.Size > 0 ? SizeFormat.Bytes(item.Size) + " + 未知" : "未知" : SizeFormat.Bytes(item.Size);
            return new(item.Name, type ? "文件类型汇总" : "合并图块",
                metric == SizeMetric.Logical ? value : "未在当前汇总中计算",
                metric == SizeMetric.Allocated ? value : "未在当前汇总中计算", "", "—",
                type ? $"{item.Count:N0} 个文件（含子目录）。查看明细后可选择具体文件。" :
                    $"合并展示 {item.Count:N0} 项；这不是单个文件。请在当前列表选择单项，不能据名称猜测成员。",
                scope, type ? SelectionAction.ShowType : SelectionAction.ShowList);
        }
        long nativeAllocation = entry.IsDirectory ? entry.AllocatedSize : entry.Storage?.AllocatedBytes ?? entry.AllocatedSize;
        string allocation = entry.UnknownAllocationFiles > 0
            ? entry.IsDirectory && nativeAllocation > 0 ? SizeFormat.Bytes(nativeAllocation) + " + 未知" : "未知"
            : SizeFormat.Bytes(nativeAllocation);
        var notes = new List<string>();
        if (entry.UnknownAllocationFiles > 0) notes.Add("实际占用包含未知项；未知不按零计算。");
        if (entry.Storage is { LinkCount: > 1 } storage)
            notes.Add($"{storage.LinkCount:N0} 个硬链接。" + (entry.SharedAllocation
                ? "此路径共享同一对象，本路径未重复计入范围占用。" : "范围占用已按文件身份去重。"));
        if (entry.Incomplete) notes.Add("部分内容未读取，目录统计并不完整。");
        notes.Add("实际占用不等于删除后一定释放的空间；当前统计仅覆盖支持的文件数据分配。");
        return new(entry.Name, entry.IsDirectory ? "文件夹" : "文件", SizeFormat.Bytes(entry.Size), allocation,
            entry.Path, entry.Modified == default ? "未知" : entry.Modified.ToString("yyyy-MM-dd HH:mm:ss"),
            string.Join("\r\n", notes), scope, entry.IsDirectory ? SelectionAction.EnterDirectory : SelectionAction.RevealFile);
    }

    internal string Summary => Kind is "文件" or "文件夹"
        ? $"{Name} · 文件大小 {LogicalSize} · 实际占用 {Allocation}"
        : $"{Name} · {Kind}";
    internal string ActionText => Action switch
    {
        SelectionAction.EnterDirectory => "进入目录", SelectionAction.RevealFile => "定位文件",
        SelectionAction.ShowType => "查看明细", SelectionAction.ShowList => "查看当前列表", _ => "定位文件"
    };
}
