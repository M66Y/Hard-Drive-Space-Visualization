using System.Diagnostics.CodeAnalysis;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsFixtureDirectoryIndexReport(string Revision, string Scope, int DecodedRecords,
    int FileObjects, int DirectoryObjects, int FileEntries, int DosAliasesIgnored, long KnownLogicalBytes,
    long KnownAllocatedBytes, int UnknownLogicalObjects, int UnknownAllocatedObjects, int RecordKnownAllocatedObjects,
    int NativeFallbackAllocatedObjects, int LookupObjects, bool CompleteWithinScope);
internal sealed record NtfsFixtureScannerComparison(string Revision, bool ScannerComplete, bool EntriesMatched,
    bool CountsMatched, bool LogicalMatched, bool AllocatedMatched, bool Matched, long ScannerFileEntries,
    long ScannerDirectories, long ScannerLogicalBytes, long ScannerAllocatedBytes);
internal sealed record NtfsFixtureIndexEntry(string RelativePath, bool Directory, ulong Reference, long? LogicalBytes);

// Only the safe report is projected by the source. Full references and relative
// fixture names are internal in-memory data, never serializable properties.
internal sealed class NtfsFixtureDirectoryIndexResult(NtfsFixtureDirectoryIndexReport report,
    IReadOnlyList<NtfsFixtureIndexEntry> entries, ulong volume, ulong rootReference)
{
    internal NtfsFixtureDirectoryIndexReport Report { get; } = report;
    internal IReadOnlyList<NtfsFixtureIndexEntry> Entries { get; } = entries;
    internal ulong Volume { get; } = volume;
    internal ulong RootReference { get; } = rootReference;
}
internal sealed class NtfsFixtureDirectoryIndexException(string code) : IOException(code)
{ internal string Code { get; } = code; }

// Pure, fixed-fixture reconciliation. Input Native values and allocation bits
// are declarations here; only the private source can verify their provenance
// and final stability. No I/O, source minting or whole-volume completeness.
internal static class NtfsFixtureDirectoryIndex
{
    internal const int MaximumRecords = 4096, MaximumEntries = 16384, MaximumNameCharacters = 1048576;
    private static void Require([DoesNotReturnIf(false)] bool condition, string code)
    { if (!condition) throw new NtfsFixtureDirectoryIndexException(code); }
    private static T External<T>(Func<T> access, CancellationToken token)
    {
        T result;
        try { result = access(); }
        catch (Exception) { token.ThrowIfCancellationRequested(); throw new NtfsFixtureDirectoryIndexException("FixtureIndexInput"); }
        token.ThrowIfCancellationRequested(); return result;
    }
    private static bool FullReference(ulong reference) => reference >> 48 != 0 &&
        (reference & NtfsRecordDecoder.MaximumRecordNumber) < MaximumRecords;
    private static bool NativeContext(FileStorageInfo? value, ulong volume, bool directory = false) => value != null && value.Identity.Volume == volume &&
        value.Identity.High == 0 && FullReference(value.Identity.Low) && (value.LogicalBytes >= 0 || directory && value.LogicalBytes == -1) && value.AllocatedBytes >= 0 &&
        value.LinkCount > 0 && value.Modified.HasValue && !value.WritableHandleOpen;
    private static bool SafeName(string name) => name is not ("." or "..") && name.Length is >= 1 and <= 255 &&
        !name.Any(character => char.IsControl(character) || character is '/' or '\\' or ':');

    internal static NtfsFixtureDirectoryIndexResult Build(IReadOnlyDictionary<ulong, NtfsRecord> records,
        Func<ulong, bool> allocatedSlot, FileStorageInfo dataRootStorage, IReadOnlyList<NtfsRawTarget> fixedTargets,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Require(records != null && allocatedSlot != null && dataRootStorage != null && fixedTargets != null, "FixtureIndexInput");
        int count = External(() => records!.Count, token);
        Require(count is >= 43 and <= MaximumRecords, "FixtureIndexRecordBudget");
        Require(External(() => fixedTargets!.Count, token) == 21, "FixtureIndexTargetBudget");
        ulong volume = dataRootStorage!.Identity.Volume, rootReference = dataRootStorage.Identity.Low;
        Require(NativeContext(dataRootStorage, volume, true), "FixtureIndexNativeContext");
        var owned = new Dictionary<ulong, NtfsRecord>(count);
        var slots = new HashSet<ulong>();
        long names = 0, characters = 0;
        var enumerator = External(() => records!.GetEnumerator(), token);
        Require(enumerator != null, "FixtureIndexInput");
        try
        {
            while (External(() => enumerator!.MoveNext(), token))
            {
                token.ThrowIfCancellationRequested();
                Require(owned.Count < count && owned.Count < MaximumRecords, "FixtureIndexRecordBudget");
                var pair = External(() => enumerator!.Current, token);
                var record = pair.Value;
                Require(record != null && record.Names != null && pair.Key == record.Reference && FullReference(record.Reference), "FixtureIndexRecord");
                Require(slots.Add(record!.Reference & NtfsRecordDecoder.MaximumRecordNumber), "FixtureIndexSlotReused");
                int nameCount = External(() => record.Names.Count, token);
                Require(nameCount is >= 0 and <= 256, "FixtureIndexNameBudget");
                names = checked(names + nameCount);
                Require(names <= MaximumEntries, "FixtureIndexEntryBudget");
                var copiedNames = new NtfsName[nameCount];
                for (int i = 0; i < nameCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var name = External(() => record.Names[i], token);
                    Require(name != null && name.Name != null && name.Name.Length is >= 1 and <= 255 && name.Namespace <= 3, "FixtureIndexName");
                    characters = checked(characters + name!.Name.Length);
                    Require(characters <= MaximumNameCharacters, "FixtureIndexNameBudget");
                    copiedNames[i] = name;
                }
                Require(External(() => allocatedSlot!(record.Reference & NtfsRecordDecoder.MaximumRecordNumber), token), "FixtureIndexUnallocatedRecord");
                var copy = record with { Names = Array.AsReadOnly(copiedNames) };
                Require(owned.TryAdd(copy.Reference, copy), "FixtureIndexDuplicateRecord");
            }
        }
        finally { External(() => { enumerator!.Dispose(); return true; }, token); }
        Require(owned.Count == count, "FixtureIndexRecordBudget");
        Require(owned.TryGetValue(rootReference, out var root) && root.IsDirectory && root.BaseReference == 0, "FixtureIndexRootMissing");
        var descriptors = NtfsKernelMatrix.Cases.ToDictionary(item => item.CaseId, StringComparer.Ordinal);
        var targets = new Dictionary<string, NtfsRawTarget>(StringComparer.Ordinal);
        var scope = new HashSet<ulong> { rootReference };
        var files = new HashSet<ulong>();
        var expectedEntries = new Dictionary<(ulong Parent, string Name), ulong>();
        var entries = new List<NtfsFixtureIndexEntry>(48);
        int dos = 0, unknownLogical = 0, unknownAllocated = 0, knownAllocated = 0, fallback = 0, lookup = 0, fileEntries = 0;
        long logical = 0, allocated = 0;
        for (int i = 0; i < 21; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = External(() => fixedTargets![i], token);
            Require(target != null && target.CaseId != null && descriptors.ContainsKey(target.CaseId) && targets.TryAdd(target.CaseId, target), "FixtureIndexTarget");
            var descriptor = descriptors[target!.CaseId];
            Require(NativeContext(target.Storage, volume) && NativeContext(target.ParentStorage, volume, true) &&
                target.Storage.Identity.Low != rootReference && target.ParentStorage.Identity.Low != rootReference &&
                files.Add(target.Storage.Identity.Low) && scope.Add(target.ParentStorage.Identity.Low), "FixtureIndexNativeContext");
            ulong parentReference = target.ParentStorage.Identity.Low;
            Require(owned.TryGetValue(parentReference, out var parent) && parent.IsDirectory && parent.BaseReference == 0, "FixtureIndexParentMissing");
            var parentNames = parent!.Names.Where(name => name.Namespace != 2).ToArray();
            Require(parentNames.Length == 1 && parentNames[0].ParentReference == rootReference &&
                string.Equals(parentNames[0].Name, descriptor.CaseId, StringComparison.Ordinal), "FixtureIndexParentMismatch");
            Require(parent.Names.All(name => name.ParentReference == rootReference) &&
                parent.Names.Select(name => (name.ParentReference, name.Name, name.Namespace)).Distinct().Count() == parent.Names.Count,
                "FixtureIndexEntryCollision");
            dos += parent.Names.Count(name => name.Namespace == 2);
            Require(expectedEntries.TryAdd((rootReference, descriptor.CaseId), parentReference), "FixtureIndexEntryCollision");
            entries.Add(new(descriptor.CaseId, true, parentReference, 0));
            Require(owned.TryGetValue(target.Storage.Identity.Low, out var file) && !file.IsDirectory && file.BaseReference == 0, "FixtureIndexFileMissing");
            Require(NtfsRawComparison.MatchesLinks(file!, target.Storage.LinkCount) &&
                NtfsRawComparison.MatchesNames(file!, parentReference, descriptor), "FixtureIndexFileNames");
            Require(target.Storage.LinkCount == (descriptor.HardLink ? 2u : 1u), "FixtureIndexLinkCount");
            if (file!.LogicalBytes.HasValue) Require(file.LogicalBytes >= 0 && file.LogicalBytes == target.Storage.LogicalBytes, "FixtureIndexLogicalMismatch");
            else unknownLogical++;
            long? occupied = file.AllocatedBytes;
            if (occupied.HasValue)
            {
                Require(occupied >= 0 && occupied == target.Storage.AllocatedBytes, "FixtureIndexAllocationMismatch");
                knownAllocated++;
            }
            else
            {
                // This is reuse of declared, owner-verified snapshots, not a
                // new native read and not an assertion of MFT-only allocation.
                occupied = target.Storage.AllocatedBytes;
                fallback++;
            }
            if (occupied.HasValue) allocated = Sum(allocated, occupied.Value);
            else unknownAllocated++;
            if (file.LookupReasons != NtfsLookupReason.None) lookup++;
            dos += file.Names.Count(name => name.Namespace == 2);
            foreach (var name in file.Names.Where(name => name.Namespace != 2))
            {
                token.ThrowIfCancellationRequested();
                Require(SafeName(name.Name) && expectedEntries.TryAdd((parentReference, name.Name), file.Reference), "FixtureIndexEntryCollision");
                fileEntries++;
                if (file.LogicalBytes.HasValue) logical = Sum(logical, file.LogicalBytes.Value);
                entries.Add(new(descriptor.CaseId + "/" + name.Name, false, file.Reference, file.LogicalBytes));
            }
        }
        Require(files.Count == 21 && scope.Count == 22 && fileEntries == 27 && entries.Count == 48, "FixtureIndexScope");
        // Reject every unexpected visible or DOS entry into the fixed scope.
        // Metadata outside it does not establish or invalidate a whole-volume tree.
        foreach (var record in owned.Values)
            foreach (var name in record.Names)
            {
                token.ThrowIfCancellationRequested();
                if (!scope.Contains(name.ParentReference)) continue;
                if (name.Namespace == 2)
                    Require(scope.Contains(record.Reference) || files.Contains(record.Reference), "FixtureIndexUnexpectedEntry");
                else Require(expectedEntries.TryGetValue((name.ParentReference, name.Name), out ulong owner) && owner == record.Reference,
                    "FixtureIndexUnexpectedEntry");
            }
        token.ThrowIfCancellationRequested();
        var report = new NtfsFixtureDirectoryIndexReport("fixture-directory-index-v1", "FixedFixtureDataRoot", owned.Count,
            21, 21, fileEntries, dos, logical, allocated, unknownLogical, unknownAllocated, knownAllocated, fallback, lookup,
            unknownLogical == 0 && unknownAllocated == 0);
        return new(report, entries.AsReadOnly(), volume, rootReference);
    }

    private static long Sum(long total, long value)
    { Require(value >= 0 && value <= long.MaxValue - total, "FixtureIndexOverflow"); return total + value; }

    internal static NtfsFixtureScannerComparison Compare(NtfsFixtureDirectoryIndexResult index, ScanResult scan,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Require(index != null && scan != null && scan.Root != null && scan.Issues != null && scan.AllocationIssues != null, "FixtureIndexComparisonInput");
        var root = scan!.Root;
        bool scannerComplete = !root.Incomplete && root.UnknownAllocationFiles == 0 && scan.Skipped == 0 &&
            External(() => scan.Issues.Count, token) == 0 && External(() => scan.AllocationIssues.Count, token) == 0;
        var expected = index!.Entries.ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<DiskEntry>(ReferenceEqualityComparer.Instance) { root };
        var queue = new Queue<(DiskEntry Node, string Prefix)>(); queue.Enqueue((root, ""));
        bool entryMatch = root.IsDirectory && root.Storage?.Identity == new FileIdentity(index.Volume, index.RootReference, 0);
        int files = 0, directories = 0, count = 0;
        long characters = 0;
        while (queue.Count != 0)
        {
            token.ThrowIfCancellationRequested();
            var (node, prefix) = queue.Dequeue();
            var children = External(() => node.Children, token);
            int childCount = External(() => children.Count, token);
            Require(childCount is >= 0 and <= MaximumEntries && count <= MaximumEntries - childCount, "FixtureIndexComparisonBudget");
            for (int i = 0; i < childCount; i++)
            {
                token.ThrowIfCancellationRequested();
                var child = External(() => children[i], token);
                Require(child != null && visited.Add(child) && SafeName(child.Name), "FixtureIndexComparisonTree");
                count++; characters = checked(characters + child!.Name.Length);
                Require(characters <= MaximumNameCharacters, "FixtureIndexComparisonBudget");
                string relative = prefix + child.Name;
                Require(seen.Add(relative), "FixtureIndexComparisonTree");
                bool found = expected.TryGetValue(relative, out var item);
                entryMatch &= found && item!.Directory == child.IsDirectory && child.Storage?.Identity == new FileIdentity(index.Volume, item.Reference, 0);
                scannerComplete &= !child.Incomplete && child.UnknownAllocationFiles == 0;
                if (child.IsDirectory) { directories++; queue.Enqueue((child, relative + "/")); }
                else { files++; entryMatch &= found && item!.LogicalBytes.HasValue && item.LogicalBytes == child.Size; }
            }
        }
        entryMatch &= count == expected.Count;
        bool counts = files == index.Report.FileEntries && directories == index.Report.DirectoryObjects &&
            root.FileCount == files && root.FolderCount == directories;
        bool logical = index.Report.UnknownLogicalObjects == 0 && root.Size == index.Report.KnownLogicalBytes;
        bool allocated = index.Report.UnknownAllocatedObjects == 0 && root.AllocatedSize == index.Report.KnownAllocatedBytes;
        token.ThrowIfCancellationRequested();
        return new("fixture-scanner-comparison-v1", scannerComplete, entryMatch, counts, logical, allocated,
            scannerComplete && entryMatch && counts && logical && allocated && index.Report.CompleteWithinScope,
            root.FileCount, root.FolderCount, root.Size, root.AllocatedSize);
    }
}
