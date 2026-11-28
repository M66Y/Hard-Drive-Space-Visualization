namespace DiskAtlas.NtfsPrototype;

// Exercises the real size-index decisions with one in-memory MFT page.
internal static class NtfsSupplementDiagnosticsTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS supplement diagnostics: " + name);
            passed++;
            Console.WriteLine("PASS NTFS supplement diagnostics " + name);
        }

        var time = new DateTime(2020, 1, 1);
        static FileIdentity Id(int slot) => new(11, NtfsRecordFixtures.Reference((ulong)slot, 3), 0);
        NtfsSnapshotNamespaceEntry Entry(int index, int slot, string name) =>
            new(index, 0, name, Id(slot), FileAttributes.Archive, false) { Modified = time };

        NtfsSnapshotNamespaceEntry[] entries = [
            new(0, -1, "", Id(1), FileAttributes.Directory, false),
            Entry(1, 2, "empty"), Entry(2, 3, "resident"),
            Entry(3, 4, "alias-a"), Entry(4, 4, "alias-b") with { Modified = time.AddSeconds(1) },
            Entry(5, 5, "compressed-sparse"), Entry(6, 6, "unsupported"), Entry(7, 7, "missing-data"),
            Entry(8, 8, "ordinary")
        ];
        var scope = new NtfsSnapshotNamespaceResult(new("snapshot-namespace-v1", entries.Length, 8, 0, 7, 0,
            1, 1, 2, entries.Sum(entry => entry.Name.Length), 1, true), entries, entries.Select(entry => entry.Name).ToArray(), Id(1));
        const int bytes = 16384;
        var map = NtfsMftMap.Parse(NtfsMftMapTests.Record0([0x11, 4, 10, 0], 3, bytes, bytes, bytes),
            new(512, 4096, 1024, 1024 * 1024, 256, 10, bytes)).Map!;
        var page = new byte[bytes];
        void Record(int slot, byte[] data) => data.CopyTo(page, slot * NtfsRecordFixtures.RecordLength);
        byte[] Name(string value) => NtfsRecordFixtures.Name(value, parent: Id(1).Low);
        Record(1, NtfsRecordFixtures.Record([Name("root")], directory: true));
        Record(2, NtfsRecordFixtures.Record([Name("empty"), NtfsRecordFixtures.Data(0)]));
        Record(3, NtfsRecordFixtures.Record([Name("resident"), NtfsRecordFixtures.Data(32)]));
        // Both aliases select one object, with incomplete link names and conflicting times.
        Record(4, NtfsRecordFixtures.Record([Name("alias-a"), NtfsRecordFixtures.Data(32)], links: 2));
        Record(5, NtfsRecordFixtures.Record([Name("compressed-sparse"), NtfsRecordFixtures.Nonresident(flags: 0x8001)]));
        Record(6, NtfsRecordFixtures.Record([Name("unsupported"), NtfsRecordFixtures.Resident(0x110, [])]));
        Record(7, NtfsRecordFixtures.Record([Name("missing-data")]));
        Record(8, NtfsRecordFixtures.Record([Name("ordinary"), NtfsRecordFixtures.Nonresident()]));
        int Read(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            page.AsMemory(checked((int)offset), destination.Length).CopyTo(destination);
            return destination.Length;
        }
        NtfsSnapshotNativeSize Lookup(NtfsSnapshotSizeRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return new(new(request.Identity, 32, request.Identity == Id(4) ? 2u : 1u, false,
                request.ExpectedLogicalBytes ?? 32, time), request.Directory);
        }

        int completedLookups = 0, clockReads = 0;
        var diagnostics = new NtfsScanDiagnostics(() =>
        {
            if (Volatile.Read(ref completedLookups) != 5) throw new InvalidOperationException("Finalization started before workers finished.");
            return Interlocked.Increment(ref clockReads) * 3;
        });
        NtfsSnapshotNativeSize CountedLookup(NtfsSnapshotSizeRequest request, CancellationToken token)
        {
            var size = Lookup(request, token);
            Interlocked.Increment(ref completedLookups);
            return size;
        }
        var result = NtfsSnapshotSizeIndex.BuildAsync(scope, map, Read, _ => true, CountedLookup, diagnostics: diagnostics).GetAwaiter().GetResult();
        var stats = diagnostics.Snapshot();
        Check(result.Report.Complete && result.Report.NativeCalls == 5 && stats.SupplementObjects == 5,
            "five unique objects require one lookup each");
        Check(!result.Objects[Id(2)].Native && !result.Objects[Id(8)].Native &&
            stats.ReasonCombinations.Sum() == stats.SupplementObjects && stats.ReasonCombinations[0] == 0,
            "resolved empty and nonresident objects do not contribute to any reason bucket");
        Check(stats.ReasonCombinations[(int)NtfsSupplementReason.ResidentAllocation] == 1, "plain nonempty resident allocation");
        Check(stats.ReasonCombinations[(int)(NtfsSupplementReason.ResidentAllocation | NtfsSupplementReason.IncompleteLinks |
            NtfsSupplementReason.ModifiedConflict)] == 1, "two aliases preserve all three reasons in one bucket");
        Check(stats.ReasonCombinations[(int)(NtfsSupplementReason.Compressed | NtfsSupplementReason.Sparse)] == 1,
            "two allocation flags stay associated with the same object");
        Check(stats.ReasonCombinations[(int)NtfsSupplementReason.IncompleteMetadata] == 1, "missing default data");
        Check(stats.ReasonCombinations[(int)NtfsSupplementReason.UnsupportedRecord] == 1, "strictly unsupported record");
        int final = (int)NtfsDiagnosticPhase.IndexFinalization;
        Check(clockReads == 2 && stats.PhaseMilliseconds[final] == 3, "finalization timed once, after the final lookup completed");
        var baseline = NtfsSnapshotSizeIndex.BuildAsync(scope, map, Read, _ => true, Lookup).GetAwaiter().GetResult();
        Check(result.Objects.Count == baseline.Objects.Count && result.Objects.All(pair => baseline.Objects[pair.Key] == pair.Value)
            && result.Report.LogicalBytes == baseline.Report.LogicalBytes && result.Report.AllocatedBytes == baseline.Report.AllocatedBytes,
            "enabling observations preserves every object and both totals");

        // The pre-dispatch counters describe scheduled objects; they are not successful-call counts.
        var failed = new NtfsScanDiagnostics();
        bool rejected = false;
        try
        {
            NtfsSnapshotSizeIndex.BuildAsync(scope, map, Read, _ => true, (_, _) => throw new IOException(),
                diagnostics: failed).GetAwaiter().GetResult();
        }
        catch (NtfsSnapshotSizeException error) when (error.Code == "SnapshotSizeNativeRead") { rejected = true; }
        Check(rejected && failed.Snapshot().SupplementObjects == 5 && failed.Snapshot().PhaseMilliseconds[final] == null,
            "lookup failure does not report completed index finalization");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var cancelled = new NtfsScanDiagnostics();
        bool observedCancel = false;
        try { NtfsSnapshotSizeIndex.BuildAsync(scope, map, Read, _ => true, Lookup, stop.Token, cancelled).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { observedCancel = true; }
        Check(observedCancel && cancelled.Snapshot().SupplementObjects == 0 && cancelled.Snapshot().PhaseMilliseconds[final] == null,
            "pre-cancelled index records no reasons or finalization");
        return passed;
    }
}
