namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsVolumeRecord(ulong Volume, NtfsRecord Record);
internal sealed record NtfsPrototypeSummary(bool Complete, string Code, int FileObjects, int DirectoryObjects,
    long DirectoryEntries, long KnownLogicalBytes, long KnownAllocatedBytes, int UnknownLogicalObjects,
    int UnknownAllocatedObjects, int LookupObjects);

// A bounded correctness model, not a production tree/cache or a live-volume snapshot.
internal static class NtfsPrototypeIndex
{
    internal const int MaximumEntries = 200_000;
    internal const int MaximumNameCharacters = 8_000_000;
    internal static NtfsPrototypeSummary Build(IReadOnlyList<NtfsVolumeRecord> records, int limit = 100_000,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (limit is < 1 or > 1_000_000 || records.Count > limit) return Failed("RecordLimit");
        var objects = new Dictionary<(ulong Volume, ulong Reference), NtfsRecord>();
        var slots = new HashSet<(ulong Volume, ulong Slot)>();
        int extensions = 0;
        long discoveredEntries = 0, nameCharacters = 0;
        foreach (var item in records)
        {
            token.ThrowIfCancellationRequested();
            var record = item.Record;
            if (record.Names.Count > 256) return Failed("NameLimit");
            discoveredEntries += record.Names.Count;
            foreach (var name in record.Names)
            {
                if (name.Name.Length is < 1 or > 255) return Failed("NameLimit");
                nameCharacters += name.Name.Length;
            }
            if (discoveredEntries > MaximumEntries || nameCharacters > MaximumNameCharacters) return Failed("EntryLimit");
            if (!objects.TryAdd((item.Volume, record.Reference), record)) return Failed("DuplicateReference");
            if (!slots.Add((item.Volume, record.Reference & NtfsRecordDecoder.MaximumRecordNumber))) return Failed("SlotReused");
            if (record.BaseReference != 0) extensions++;
        }
        long logical = 0, allocated = 0, entries = 0;
        int files = 0, directories = 0, unknownLogical = 0, unknownAllocated = 0, lookups = extensions;
        bool incomplete = extensions != 0 || records.Count == 0;
        var parentOf = new Dictionary<(ulong Volume, ulong Reference), (ulong Volume, ulong Reference)>();
        var entriesByName = new Dictionary<(ulong Volume, ulong Parent, string Name), ulong>();
        foreach (var item in records)
        {
            token.ThrowIfCancellationRequested();
            var record = item.Record;
            if (record.BaseReference != 0) continue;
            if (record.LookupReasons != NtfsLookupReason.None) { lookups++; incomplete = true; }
            var names = record.Names.Where(n => n.Namespace != 2).DistinctBy(n => (n.ParentReference, n.Name)).ToArray();
            entries += names.Length;
            if (names.Length == 0) incomplete = true;
            foreach (var name in names)
            {
                var entry = (item.Volume, name.ParentReference, name.Name);
                if (entriesByName.TryGetValue(entry, out ulong owner) && owner != record.Reference) return Failed("EntryCollision");
                entriesByName[entry] = record.Reference;
                if (!objects.TryGetValue((item.Volume, name.ParentReference), out var parent) || !parent.IsDirectory || parent.BaseReference != 0)
                    incomplete = true;
            }
            if (record.IsDirectory)
            {
                directories++;
                if (names.Length != 1) incomplete = true;
                else parentOf[(item.Volume, record.Reference)] = (item.Volume, names[0].ParentReference);
                continue; // Directory indexes and NTFS system overhead are outside this sum.
            }
            files++;
            if (record.LogicalBytes is { } size)
            {
                // Match production: each hard-link entry contributes logical bytes;
                // allocation below is charged once to the unique file object.
                if (size < 0 || (names.Length > 0 && size > (long.MaxValue - logical) / names.Length)) return Failed("TotalOverflow");
                logical += size * names.Length;
            }
            else unknownLogical++;
            if (record.AllocatedBytes is { } occupied)
            {
                if (occupied < 0 || occupied > long.MaxValue - allocated) return Failed("TotalOverflow");
                allocated += occupied;
            }
            else unknownAllocated++;
        }
        // Every directory must reach the actual root (slot 5); self-parents elsewhere
        // and stale parent sequence references must not become disconnected success.
        var resolved = new HashSet<(ulong Volume, ulong Reference)>();
        foreach (var key in parentOf.Keys)
        {
            token.ThrowIfCancellationRequested();
            var current = key;
            var chain = new HashSet<(ulong Volume, ulong Reference)>();
            while (!resolved.Contains(current))
            {
                token.ThrowIfCancellationRequested();
                if (!chain.Add(current)) return Failed("DirectoryCycle");
                if (!parentOf.TryGetValue(current, out var parent)) { incomplete = true; break; }
                if (parent == current)
                {
                    if ((current.Reference & NtfsRecordDecoder.MaximumRecordNumber) != 5) return Failed("DirectoryCycle");
                    break;
                }
                current = parent;
            }
            foreach (var ancestor in chain) resolved.Add(ancestor);
        }
        token.ThrowIfCancellationRequested();
        bool complete = !incomplete && unknownLogical == 0 && unknownAllocated == 0;
        return new(complete, complete ? "CompleteSyntheticIndex" : "NeedsLookup", files, directories, entries,
            logical, allocated, unknownLogical, unknownAllocated, lookups);
    }

    private static NtfsPrototypeSummary Failed(string code) => new(false, code, 0, 0, 0, 0, 0, 0, 0, 0);
}
