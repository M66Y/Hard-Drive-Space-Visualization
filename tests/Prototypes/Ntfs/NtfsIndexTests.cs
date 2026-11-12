namespace DiskAtlas.NtfsPrototype;

internal static class NtfsIndexTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException(name);
            passed++; Console.WriteLine("PASS NTFS index " + name);
        }
        NtfsRecord Decode(byte[] bytes, ulong slot) => NtfsRecordDecoder.Decode(bytes, 512, recordNumber: slot).Record
            ?? throw new InvalidOperationException("synthetic record decoding");
        ulong rootId = NtfsRecordFixtures.Reference(5, 7);
        var root = Decode(NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("root", parent: rootId)], sequence: 7, directory: true), 5);
        var file = Decode(NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("first.bin"),
            NtfsRecordFixtures.Name("second.bin"), NtfsRecordFixtures.Name("FIRST~1.BIN", 2),
            NtfsRecordFixtures.Nonresident()], links: 2), 42);
        NtfsVolumeRecord R(NtfsRecord entry, ulong volume = 1) => new(volume, entry);
        var result = NtfsPrototypeIndex.Build([R(root), R(file)]);
        Check(result.Complete && result.FileObjects == 1 && result.KnownLogicalBytes == 12000 && result.KnownAllocatedBytes == 8192,
            "hard-link entries contribute logical size; allocation counted once");
        Check(result.DirectoryEntries == 3, "two links and root retained; DOS alias not counted twice");
        Check(NtfsPrototypeIndex.Build([R(root), R(file), R(file)]).Code == "DuplicateReference", "duplicate records reject the batch");
        var reused = file with { Reference = NtfsRecordFixtures.Reference(42, 4) };
        Check(NtfsPrototypeIndex.Build([R(root), R(file), R(reused)]).Code == "SlotReused", "slot reuse across a read invalidates the batch");
        var replacement = file with { Reference = NtfsRecordFixtures.Reference(43, 3) };
        Check(NtfsPrototypeIndex.Build([R(root), R(file), R(replacement)]).Code == "EntryCollision", "two objects cannot own the same directory entry");
        Check(!NtfsPrototypeIndex.Build([]).Complete, "empty input cannot claim a complete snapshot");
        var twoVolumes = NtfsPrototypeIndex.Build([R(root), R(file), R(root, 2), R(file, 2)]);
        Check(twoVolumes.Complete && twoVolumes.FileObjects == 2 && twoVolumes.KnownAllocatedBytes == 16384, "file identity includes volume");
        Check(!NtfsPrototypeIndex.Build([R(file)]).Complete, "missing parent is incomplete");
        var stale = file with { Names = [new(rootId + (1UL << 48), "stale.bin", 1)] };
        Check(!NtfsPrototypeIndex.Build([R(root), R(stale)]).Complete, "parent reference sequence is validated");
        var unknown = file with { LogicalBytes = null, AllocatedBytes = null, LookupReasons = NtfsLookupReason.AttributeList };
        var partial = NtfsPrototypeIndex.Build([R(root), R(unknown)]);
        Check(!partial.Complete && partial.UnknownAllocatedObjects == 1 && partial.UnknownLogicalObjects == 1,
            "unknown sizes remain counted as unknown objects");
        var extension = file with { Reference = NtfsRecordFixtures.Reference(43, 3), BaseReference = file.Reference,
            LookupReasons = NtfsLookupReason.ExtensionRecord, Names = [], LogicalBytes = null, AllocatedBytes = null };
        var pending = NtfsPrototypeIndex.Build([R(root), R(file), R(extension)]);
        Check(!pending.Complete && pending.FileObjects == 1 && pending.KnownAllocatedBytes == 8192, "extension never becomes another file or a second allocation");
        var huge = file with { LogicalBytes = long.MaxValue, AllocatedBytes = long.MaxValue };
        var more = file with { Reference = NtfsRecordFixtures.Reference(44, 3), Names = [new(rootId, "more.bin", 1)] };
        Check(NtfsPrototypeIndex.Build([R(root), R(huge), R(more)]).Code == "TotalOverflow", "totals cannot silently overflow");
        var cycle = root with { Reference = NtfsRecordFixtures.Reference(8, 7), Names = [new(NtfsRecordFixtures.Reference(8, 7), "loop", 1)] };
        Check(NtfsPrototypeIndex.Build([R(cycle)]).Code == "DirectoryCycle", "self parent outside NTFS root is rejected");
        Check(NtfsPrototypeIndex.Build([R(root), R(file)], limit: 1).Code == "RecordLimit", "record budget is bounded before indexing");
        var manyNames = Enumerable.Range(0, 256).Select(i => new NtfsName(rootId, "entry" + i, 1)).ToArray();
        var large = Enumerable.Range(1000, 800).Select(i => R(file with
        {
            Reference = NtfsRecordFixtures.Reference((ulong)i, 3), Names = manyNames
        })).ToArray();
        Check(NtfsPrototypeIndex.Build(large).Code == "EntryLimit", "entry budget bounds hard-link index expansion");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool stopped = false;
        try { NtfsPrototypeIndex.Build([R(root), R(file)], token: cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "cancelled build publishes no summary");
        using var duringBuild = new CancellationTokenSource();
        bool interrupted = false;
        try { NtfsPrototypeIndex.Build(new CancelAfterFirst([R(root), R(file)], duringBuild), token: duringBuild.Token); }
        catch (OperationCanceledException) { interrupted = true; }
        Check(interrupted, "cancellation after indexing starts discards partial output");
        return passed;
    }

    private sealed class CancelAfterFirst(IReadOnlyList<NtfsVolumeRecord> records, CancellationTokenSource source)
        : IReadOnlyList<NtfsVolumeRecord>
    {
        public int Count => records.Count;
        public NtfsVolumeRecord this[int index] => records[index];
        public IEnumerator<NtfsVolumeRecord> GetEnumerator()
        {
            for (int i = 0; i < records.Count; i++)
            {
                if (i == 1) source.Cancel();
                yield return records[i];
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
