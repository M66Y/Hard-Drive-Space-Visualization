using System.Diagnostics.CodeAnalysis;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsScaleDirectoryIndexReport(string Revision, string Scope, int DecodedRecords,
    int FileObjects, int DirectoryObjects, int FileEntries, int DosAliasesIgnored, long KnownLogicalBytes,
    long KnownAllocatedBytes, int UnknownLogicalObjects, int UnknownAllocatedObjects, int RecordKnownAllocatedObjects,
    int NativeFallbackAllocatedObjects, int LookupObjects, int IncompleteObjects, bool CompleteWithinObservedRecords);
internal sealed record NtfsScaleScannerComparison(string Revision, bool ScannerComplete, bool EntriesMatched,
    bool CountsMatched, bool LogicalMatched, bool AllocatedMatched, bool Matched, long ScannerFileEntries,
    long ScannerDirectories, long ScannerLogicalBytes, long ScannerAllocatedBytes);
internal sealed record NtfsScaleIndexEntry(string RelativePath, bool Directory, ulong Reference, int ParentIndex,
    long? LogicalBytes, long? AllocatedBytes, FileStorageInfo? Storage, string AllocationSource);
internal sealed class NtfsScaleDirectoryIndexResult(NtfsScaleDirectoryIndexReport report, IReadOnlyList<NtfsScaleIndexEntry> entries,
    FileStorageInfo rootStorage)
{
    internal NtfsScaleDirectoryIndexReport Report { get; } = report;
    internal IReadOnlyList<NtfsScaleIndexEntry> Entries { get; } = entries;
    internal FileStorageInfo RootStorage { get; } = rootStorage;
}
internal sealed class NtfsScaleDirectoryIndexException(string code) : IOException(code)
{ internal string Code { get; } = code; }

// Source-free bounded graph calculation. Native fallback values are only input
// declarations: the owner must verify them before/after the locked source epoch.
// CompleteWithinObservedRecords does not prove unsupported slots are outside the
// root. Only a separate controlled scanner/creation-manifest comparison can do so.
internal static class NtfsScaleDirectoryIndex
{
    internal const int MaximumRecords = 262144, MaximumEntries = 1048576;
    internal const int MaximumNameCharacters = 32 * 1024 * 1024, MaximumFallbackObjects = 32768;
    private sealed record Edge(ulong Reference, string Name);
    private static void Require([DoesNotReturnIf(false)] bool condition, string code)
    { if (!condition) throw new NtfsScaleDirectoryIndexException(code); }
    private static T External<T>(Func<T> access, CancellationToken token)
    {
        T value;
        try { value = access(); }
        catch (Exception) { token.ThrowIfCancellationRequested(); throw new NtfsScaleDirectoryIndexException("ScaleIndexInput"); }
        token.ThrowIfCancellationRequested(); return value;
    }
    private static bool FullReference(ulong reference) => reference >> 48 != 0 &&
        (reference & NtfsRecordDecoder.MaximumRecordNumber) < MaximumRecords;
    private static bool NativeContext(FileStorageInfo? value, ulong volume, bool directory = false) => value != null &&
        value.Identity.Volume == volume && value.Identity.High == 0 && FullReference(value.Identity.Low) &&
        (value.LogicalBytes >= 0 || directory && value.LogicalBytes == -1) && value.AllocatedBytes >= 0 &&
        value.LinkCount > 0 && value.Modified.HasValue && !value.WritableHandleOpen;
    private static bool SafeName(string name) => name is not ("." or "..") && name.Length is >= 1 and <= 255 &&
        !name.Any(character => char.IsControl(character) || character is '/' or '\\' or ':');
    private static long Add(long total, long value)
    { Require(value >= 0 && value <= long.MaxValue - total, "ScaleIndexOverflow"); return total + value; }

    private static Dictionary<ulong, T> Snapshot<T>(IReadOnlyDictionary<ulong, T> input, int maximum,
        Func<ulong, T, T> copy, CancellationToken token)
    {
        int count = External(() => input.Count, token);
        Require(count >= 0 && count <= maximum, "ScaleIndexRecordBudget");
        var result = new Dictionary<ulong, T>(count);
        var enumerator = External(() => input.GetEnumerator(), token);
        Require(enumerator != null, "ScaleIndexInput");
        try
        {
            while (External(() => enumerator.MoveNext(), token))
            {
                Require(result.Count < count, "ScaleIndexRecordBudget");
                var pair = External(() => enumerator.Current, token);
                Require(result.TryAdd(pair.Key, copy(pair.Key, pair.Value)), "ScaleIndexDuplicateRecord");
            }
        }
        finally { External(() => { enumerator.Dispose(); return true; }, token); }
        Require(result.Count == count, "ScaleIndexRecordBudget");
        return result;
    }

    internal static NtfsScaleDirectoryIndexResult Build(IReadOnlyDictionary<ulong, NtfsRecord> records,
        Func<ulong, bool> allocatedSlot, FileStorageInfo dataRootStorage,
        IReadOnlyDictionary<ulong, FileStorageInfo>? verifiedFallback = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Require(records != null && allocatedSlot != null && dataRootStorage != null, "ScaleIndexInput");
        ulong volume = dataRootStorage.Identity.Volume, rootReference = dataRootStorage.Identity.Low;
        Require(NativeContext(dataRootStorage, volume, true), "ScaleIndexNativeContext");
        long names = 0, characters = 0;
        var slots = new HashSet<ulong>();
        var owned = Snapshot(records, MaximumRecords, (reference, record) =>
        {
            Require(record != null && record.Names != null && reference == record.Reference && FullReference(reference), "ScaleIndexRecord");
            Require(slots.Add(reference & NtfsRecordDecoder.MaximumRecordNumber), "ScaleIndexSlotReused");
            int length = External(() => record.Names.Count, token);
            Require(length is >= 0 and <= 256 && names <= MaximumEntries - length, "ScaleIndexEntryBudget");
            names += length;
            var copy = new NtfsName[length];
            for (int i = 0; i < length; i++)
            {
                var name = External(() => record.Names[i], token);
                Require(name != null && name.Name != null && name.Name.Length is >= 1 and <= 255 && name.Namespace <= 3,
                    "ScaleIndexName");
                Require(characters <= MaximumNameCharacters - name.Name.Length, "ScaleIndexNameBudget");
                characters += name.Name.Length; copy[i] = name;
            }
            Require(External(() => allocatedSlot(reference & NtfsRecordDecoder.MaximumRecordNumber), token), "ScaleIndexUnallocatedRecord");
            return record with { Names = Array.AsReadOnly(copy) };
        }, token);
        Require(owned.TryGetValue(rootReference, out var root) && root.IsDirectory && root.BaseReference == 0, "ScaleIndexRootMissing");
        Require(root.LookupReasons == NtfsLookupReason.None, "ScaleIndexDirectoryUnsupported");
        var fallback = verifiedFallback == null ? new Dictionary<ulong, FileStorageInfo>() :
            Snapshot(verifiedFallback, MaximumFallbackObjects, (reference, info) =>
            {
                Require(NativeContext(info, volume) && reference == info.Identity.Low, "ScaleIndexNativeContext");
                return info;
            }, token);
        var children = new Dictionary<ulong, List<Edge>>();
        foreach (var record in owned.Values)
            foreach (var name in record.Names)
            {
                token.ThrowIfCancellationRequested();
                if (name.Namespace == 2) continue;
                if (!children.TryGetValue(name.ParentReference, out var list)) children.Add(name.ParentReference, list = []);
                list.Add(new(record.Reference, name.Name));
            }
        var resultEntries = new List<NtfsScaleIndexEntry>();
        var queue = new Queue<(ulong Reference, int Index, string Prefix)>(); queue.Enqueue((rootReference, -1, ""));
        var seenDirectories = new HashSet<ulong> { rootReference };
        var objects = new Dictionary<ulong, (long? Logical, long? Allocated, FileStorageInfo? Storage, string Source)>();
        int files = 0, directories = 0, fileEntries = 0, dos = 0, unknownLogical = 0, unknownAllocated = 0;
        int recordKnown = 0, nativeFallback = 0, lookup = 0, incomplete = 0;
        long logicalTotal = 0, allocatedTotal = 0, pathCharacters = 0;
        const NtfsLookupReason unresolved = NtfsLookupReason.AttributeList | NtfsLookupReason.ExtensionRecord | NtfsLookupReason.ReparsePoint |
            NtfsLookupReason.Encrypted | NtfsLookupReason.MissingData | NtfsLookupReason.MissingStandardInformation | NtfsLookupReason.LinkNamesIncomplete;
        while (queue.TryDequeue(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if (!children.TryGetValue(directory.Reference, out var edges)) continue;
            var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var edge in edges)
            {
                token.ThrowIfCancellationRequested();
                Require(SafeName(edge.Name) && entryNames.Add(edge.Name), "ScaleIndexEntryCollision");
                var record = owned[edge.Reference];
                Require(record.BaseReference == 0, "ScaleIndexExtension");
                Require(resultEntries.Count < MaximumEntries, "ScaleIndexEntryBudget");
                Require(pathCharacters <= MaximumNameCharacters - directory.Prefix.Length - edge.Name.Length, "ScaleIndexPathBudget");
                string relative = directory.Prefix + edge.Name; pathCharacters += relative.Length;
                if (record.IsDirectory)
                {
                    Require(seenDirectories.Add(record.Reference), "ScaleIndexDirectoryCycle");
                    Require(record.Names.Count(name => name.Namespace != 2) == 1, "ScaleIndexDirectoryParents");
                    Require((record.LookupReasons & unresolved) == 0, "ScaleIndexDirectoryUnsupported");
                    Require(record.Names.Select(name => (name.ParentReference, name.Name, name.Namespace)).Distinct().Count() == record.Names.Count,
                        "ScaleIndexDuplicateName");
                    directories++; dos += record.Names.Count(name => name.Namespace == 2);
                    int index = resultEntries.Count;
                    var directoryStorage = new FileStorageInfo(new(volume, record.Reference, 0), 0, 1, false, -1);
                    resultEntries.Add(new(relative, true, record.Reference, directory.Index, 0, 0, directoryStorage, "Record"));
                    queue.Enqueue((record.Reference, index, relative + "/"));
                    continue;
                }
                if (!objects.TryGetValue(record.Reference, out var value))
                {
                    Require(record.Names.Select(name => (name.ParentReference, name.Name, name.Namespace)).Distinct().Count() == record.Names.Count,
                        "ScaleIndexDuplicateName");
                    files++;
                    Require(!record.LogicalBytes.HasValue || record.LogicalBytes >= 0, "ScaleIndexLogical");
                    Require(!record.AllocatedBytes.HasValue || record.AllocatedBytes >= 0, "ScaleIndexAllocated");
                    long? occupied = record.AllocatedBytes;
                    string source = occupied.HasValue ? "Record" : "Unknown";
                    fallback.TryGetValue(record.Reference, out var supplement);
                    if (supplement != null)
                    {
                        Require(!record.LogicalBytes.HasValue || record.LogicalBytes == supplement.LogicalBytes, "ScaleIndexNativeLogicalMismatch");
                        Require(!occupied.HasValue || occupied == supplement.AllocatedBytes, "ScaleIndexNativeAllocationMismatch");
                        Require(supplement.LinkCount == record.Names.Count(name => name.Namespace != 2), "ScaleIndexNativeLinksMismatch");
                        if (!occupied.HasValue) { occupied = supplement.AllocatedBytes; source = "NativeFallback"; nativeFallback++; }
                    }
                    if (record.AllocatedBytes.HasValue) recordKnown++;
                    if (!record.LogicalBytes.HasValue) unknownLogical++;
                    if (!occupied.HasValue) unknownAllocated++;
                    if ((record.LookupReasons & unresolved) != 0 || record.LinkCount != record.Names.Count) incomplete++;
                    if (record.LookupReasons != NtfsLookupReason.None) lookup++;
                    dos += record.Names.Count(name => name.Namespace == 2);
                    if (occupied.HasValue) allocatedTotal = Add(allocatedTotal, occupied.Value);
                    uint links = (uint)record.Names.Count(name => name.Namespace != 2);
                    FileStorageInfo? storage = occupied.HasValue && record.LogicalBytes.HasValue ?
                        supplement ?? new(new(volume, record.Reference, 0), occupied.Value, links, false, record.LogicalBytes.Value) : null;
                    value = (record.LogicalBytes, occupied, storage, source); objects.Add(record.Reference, value);
                }
                fileEntries++;
                if (value.Logical.HasValue) logicalTotal = Add(logicalTotal, value.Logical.Value);
                resultEntries.Add(new(relative, false, record.Reference, directory.Index, value.Logical, value.Allocated, value.Storage, value.Source));
            }
        }
        token.ThrowIfCancellationRequested();
        var report = new NtfsScaleDirectoryIndexReport("scale-directory-index-v1", "ObservedDataRoot", owned.Count, files, directories,
            fileEntries, dos, logicalTotal, allocatedTotal, unknownLogical, unknownAllocated, recordKnown, nativeFallback, lookup,
            incomplete, unknownLogical == 0 && unknownAllocated == 0 && incomplete == 0);
        return new(report, resultEntries.AsReadOnly(), dataRootStorage);
    }

    // A pure projection; incomplete values are never converted into zero-sized
    // product entries. Root path is display context only, never read or opened.
    internal static DiskEntry Materialize(NtfsScaleDirectoryIndexResult index, string rootPath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Require(index != null && rootPath != null && index.Report.CompleteWithinObservedRecords, "ScaleIndexIncomplete");
        var root = new DiskEntry(rootPath, true) { Storage = index.RootStorage };
        var nodes = new DiskEntry[index.Entries.Count];
        for (int i = 0; i < nodes.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var item = index.Entries[i];
            Require(item.ParentIndex >= -1 && item.ParentIndex < i, "ScaleIndexProjection");
            var parent = item.ParentIndex < 0 ? root : nodes[item.ParentIndex];
            string name = item.RelativePath[(item.RelativePath.LastIndexOf('/') + 1)..];
            var node = DiskEntry.FromName(name, item.Directory, parent, item.Directory ? "文件夹" : DiskEntry.ExtensionOf(name));
            node.Storage = item.Storage;
            if (!item.Directory) { node.Size = item.LogicalBytes!.Value; node.FileCount = 1; }
            parent.MutableChildren.Add(node); nodes[i] = node;
        }
        for (int i = nodes.Length - 1; i >= 0; i--)
        {
            token.ThrowIfCancellationRequested(); var node = nodes[i];
            var parent = node.Parent!; parent.Size = Add(parent.Size, node.Size); parent.FileCount = Add(parent.FileCount, node.FileCount);
            parent.FolderCount = Add(parent.FolderCount, node.FolderCount + (node.IsDirectory ? 1 : 0));
        }
        StorageMetrics.Calculate(root, token);
        token.ThrowIfCancellationRequested(); return root;
    }

    internal static NtfsScaleScannerComparison Compare(NtfsScaleDirectoryIndexResult index, ScanResult scan, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Require(index != null && scan != null && scan.Root != null && scan.Issues != null && scan.AllocationIssues != null, "ScaleIndexComparisonInput");
        var root = scan.Root; ulong volume = index.RootStorage.Identity.Volume;
        bool complete = !root.Incomplete && root.UnknownAllocationFiles == 0 && scan.Skipped == 0 &&
            External(() => scan.Issues.Count, token) == 0 && External(() => scan.AllocationIssues.Count, token) == 0;
        bool matched = root.IsDirectory && root.Storage?.Identity == index.RootStorage.Identity;
        var expected = index.Entries.ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNodes = new HashSet<DiskEntry>(ReferenceEqualityComparer.Instance) { root };
        var queue = new Queue<(DiskEntry Entry, string Prefix)>(); queue.Enqueue((root, ""));
        int count = 0, files = 0, directories = 0; long pathCharacters = 0;
        while (queue.TryDequeue(out var parent))
        {
            token.ThrowIfCancellationRequested(); var children = parent.Entry.Children;
            int length = External(() => children.Count, token);
            Require(length is >= 0 and <= MaximumEntries && count <= MaximumEntries - length, "ScaleIndexComparisonBudget");
            for (int i = 0; i < length; i++)
            {
                var child = External(() => children[i], token);
                Require(child != null && seenNodes.Add(child) && SafeName(child.Name), "ScaleIndexComparisonTree");
                Require(pathCharacters <= MaximumNameCharacters - parent.Prefix.Length - child.Name.Length, "ScaleIndexComparisonBudget");
                string relative = parent.Prefix + child.Name; pathCharacters += relative.Length;
                Require(seen.Add(relative), "ScaleIndexComparisonTree"); count++;
                bool found = expected.TryGetValue(relative, out var item);
                matched &= found && item!.Directory == child.IsDirectory && child.Storage?.Identity == new FileIdentity(volume, item.Reference, 0);
                complete &= !child.Incomplete && child.UnknownAllocationFiles == 0;
                if (child.IsDirectory) { directories++; queue.Enqueue((child, relative + "/")); }
                else { files++; matched &= found && item!.LogicalBytes.HasValue && child.Size == item.LogicalBytes; }
            }
        }
        matched &= count == expected.Count;
        bool counts = root.FileCount == files && root.FolderCount == directories && files == index.Report.FileEntries && directories == index.Report.DirectoryObjects;
        bool logical = index.Report.UnknownLogicalObjects == 0 && root.Size == index.Report.KnownLogicalBytes;
        bool allocated = index.Report.UnknownAllocatedObjects == 0 && root.AllocatedSize == index.Report.KnownAllocatedBytes;
        token.ThrowIfCancellationRequested();
        return new("scale-scanner-comparison-v1", complete, matched, counts, logical, allocated,
            complete && matched && counts && logical && allocated && index.Report.CompleteWithinObservedRecords,
            root.FileCount, root.FolderCount, root.Size, root.AllocatedSize);
    }
}
