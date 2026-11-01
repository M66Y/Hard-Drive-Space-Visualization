namespace DiskAtlas;

internal static class StorageMetrics
{
    public static void Calculate(DiskEntry root, CancellationToken token)
    {
        var owners = new Dictionary<FileIdentity, DiskEntry>();
        var directories = new List<DiskEntry> { root };
        foreach (var entry in root.Descendants())
        {
            token.ThrowIfCancellationRequested();
            if (entry.IsDirectory) { directories.Add(entry); continue; }
            entry.UnknownAllocationFiles = entry.Storage == null ? 1 : 0;
            entry.SharedAllocation = false;
            entry.AllocatedSize = entry.Storage?.AllocatedBytes ?? 0;
            if (entry.Storage is not { LinkCount: > 1 } storage) continue;
            if (!owners.TryGetValue(storage.Identity, out var owner)) owners[storage.Identity] = entry;
            else if (DiskEntry.ComparePath(entry, owner) < 0) { owner.AllocatedSize = 0; owner.SharedAllocation = true; owners[storage.Identity] = entry; }
            else { entry.AllocatedSize = 0; entry.SharedAllocation = true; }
        }
        for (int i = directories.Count - 1; i >= 0; i--)
        {
            token.ThrowIfCancellationRequested();
            var dir = directories[i]; long bytes = 0, unknown = 0;
            foreach (var child in dir.Children) { token.ThrowIfCancellationRequested(); bytes += child.AllocatedSize; unknown += child.UnknownAllocationFiles; }
            lock (dir) { dir.AllocatedSize = bytes; dir.UnknownAllocationFiles = unknown; }
        }
    }
}
