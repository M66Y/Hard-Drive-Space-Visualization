using System.Runtime.InteropServices;

namespace DiskAtlas.NtfsPrototype;

// Pure memory streams only. A successful synthetic traversal is not a real
// fixture/source/lock proof and never opens files, devices or Native handles.
internal static class NtfsMftEnumeratorTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("MFT enumeration check failed: " + name);
            passed++; Console.WriteLine("PASS MFT enumeration " + name);
        }
        void Rejected(Action action, string code, string name)
        {
            bool rejected = false;
            try { action(); }
            catch (NtfsMftEnumerationException error)
            { rejected = error.Code == code && error.Message == code && error.InnerException == null; }
            Check(rejected, name);
        }
        byte[] ordinary = NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), NtfsRecordFixtures.Nonresident());
        byte[] free = NtfsRecordFixtures.Record([], inUse: false);
        byte[] unsupported = (byte[])ordinary.Clone(); NtfsRecordFixtures.U16(unsupported, 22, 5);
        byte[] invalid = (byte[])ordinary.Clone(); invalid[510] ^= 1;
        byte[] unknown = NtfsRecordFixtures.File(NtfsRecordFixtures.Name());
        byte[] special = NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), NtfsRecordFixtures.Nonresident(flags: 1));
        var map = Map(70);
        var original = ordinary.ToArray();
        int reads = 0;
        bool inspectBuffer = true;
        byte[]? reused = null;
        var windows = new List<(long Offset, int Length)>();
        int ReadMixed(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); reads++; windows.Add((offset, destination.Length));
            if (inspectBuffer)
            {
                Check(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination, out var segment) &&
                    segment.Array != null && segment.Array.Length == NtfsMftEnumerator.PageBytes && segment.Offset == 0,
                    "every callback receives a slice of one bounded managed page");
                if (reused == null) reused = segment.Array;
                else Check(ReferenceEquals(reused, segment.Array), "the last page reuses the same managed buffer");
            }
            for (int local = 0; local < destination.Length; local += 1024)
            {
                ulong slot = (ulong)((offset + local) / 1024);
                byte[] record = slot switch { 1 => free, 2 => unsupported, 3 => invalid, 4 => unknown, 5 => special, _ => ordinary };
                record.CopyTo(destination.Span[local..]);
            }
            return destination.Length;
        }
        var targets = new ulong[] { NtfsRecordFixtures.Reference(0, 3), NtfsRecordFixtures.Reference(63, 3),
            NtfsRecordFixtures.Reference(64, 3), NtfsRecordFixtures.Reference(69, 3) };
        var result = NtfsMftEnumerator.Enumerate(map, targets, ReadMixed);
        Check(result.CompletedCursor && result.ValidBytes == 70 * 1024 && result.ExpectedSlots == 70 && result.VisitedSlots == 70,
            "cursor covers the exact valid range through the final short page");
        Check(result.Decoded == 67 && result.Free == 1 && result.Unsupported == 1 && result.Invalid == 1 &&
            result.VisitedSlots == result.Decoded + result.Free + result.Unsupported + result.Invalid,
            "strict invalid unsupported and free remain separate classifications");
        Check(result.LookupRequired == 2 && result.UnknownLogical == 1 && result.UnknownAllocated == 2,
            "unknown logical allocation and lookup reasons are counted without zero substitution");
        Check(result.Pages == 2 && reads == 2 && windows.SequenceEqual(new[] { (0L, 65536), (65536L, 6144) }),
            "pages neither skip nor overlap and EOF is never padded or overread");
        Check(result.TargetRecords.Count == targets.Length && targets.All(reference => result.TargetRecords[reference].Reference == reference),
            "only requested full references are retained across page boundaries");
        Check(result.TargetRecords.Values.All(record => record.LogicalBytes == 6000 && record.AllocatedBytes == 8192) && ordinary.SequenceEqual(original),
            "retained records survive page reuse and caller fixture bytes remain unchanged");
        bool immutable = false;
        try { ((IDictionary<ulong, NtfsRecord>)result.TargetRecords).Clear(); } catch (NotSupportedException) { immutable = true; }
        Check(immutable, "retained target dictionary cannot be edited by consumers");
        inspectBuffer = false;
        var allTargets = Enumerable.Range(6, 21).Select(slot => NtfsRecordFixtures.Reference((ulong)slot, 3)).ToArray();
        var finiteTargets = NtfsMftEnumerator.Enumerate(map, allTargets, ReadMixed);
        Check(finiteTargets.TargetRecords.Count == 21 && finiteTargets.TargetRecords.Keys.All(allTargets.Contains),
            "the exact target retention limit stores only twenty-one selected parsed records");
        var unknownTargets = NtfsMftEnumerator.Enumerate(map,
            [NtfsRecordFixtures.Reference(4, 3), NtfsRecordFixtures.Reference(5, 3)], ReadMixed);
        Check(unknownTargets.TargetRecords.Count == 2 && unknownTargets.TargetRecords.Values.All(record => !record.AllocatedBytes.HasValue) &&
            !unknownTargets.TargetRecords[NtfsRecordFixtures.Reference(4, 3)].LogicalBytes.HasValue &&
            unknownTargets.TargetRecords[NtfsRecordFixtures.Reference(5, 3)].LogicalBytes == 6000,
            "retained unknown target sizes remain nullable and distinct from known logical data");

        int readAttempts = 0;
        int ReadOrdinary(long offset, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); readAttempts++;
            for (int i = 0; i < destination.Length; i += 1024) ordinary.CopyTo(destination.Span[i..]);
            return destination.Length;
        }
        var exactPage = NtfsMftEnumerator.Enumerate(Map(64), [], ReadOrdinary);
        Check(exactPage.Pages == 1 && exactPage.VisitedSlots == 64 && exactPage.Decoded == 64 && exactPage.TargetRecords.Count == 0,
            "an exact page endpoint has no empty trailing read");
        readAttempts = 0;
        var maximum = NtfsMftEnumerator.Enumerate(Map(4096), [], ReadOrdinary);
        Check(maximum.CompletedCursor && maximum.ExpectedSlots == 4096 && maximum.VisitedSlots == 4096 && maximum.Pages == 64 &&
            maximum.Decoded == 4096 && readAttempts == 64, "the exact maximum slot budget covers all sixty-four pages");
        readAttempts = 0;
        Rejected(() => NtfsMftEnumerator.Enumerate(Map(4097), [], ReadOrdinary), "MftEnumerationSlotBudget",
            "an oversized valid range is rejected before any prefix read");
        Check(readAttempts == 0, "slot-budget rejection performs no callback");
        Rejected(() => NtfsMftEnumerator.Enumerate(null!, [], ReadOrdinary), "MftEnumerationInput", "null map is a controlled input failure");
        Rejected(() => NtfsMftEnumerator.Enumerate(map, null!, ReadOrdinary), "MftEnumerationInput", "null targets are rejected");
        Rejected(() => NtfsMftEnumerator.Enumerate(map, [], null!), "MftEnumerationInput", "null reader is rejected");
        Rejected(() => NtfsMftEnumerator.Enumerate(Map(4, 512), [], ReadOrdinary), "MftEnumerationGeometry",
            "a valid map with another geometry cannot expand this fixture enumeration contract");
        foreach (var badTargets in new ulong[][] {
            Enumerable.Range(0, 22).Select(slot => NtfsRecordFixtures.Reference((ulong)slot, 3)).ToArray(),
            [NtfsRecordFixtures.Reference(1, 3), NtfsRecordFixtures.Reference(1, 3)],
            [NtfsRecordFixtures.Reference(1, 3), NtfsRecordFixtures.Reference(1, 4)],
            [1], [NtfsRecordFixtures.Reference(70, 3)] })
        {
            readAttempts = 0;
            Rejected(() => NtfsMftEnumerator.Enumerate(map, badTargets, ReadOrdinary),
                badTargets.Length > 21 ? "MftEnumerationTargetBudget" : "MftEnumerationTargetIdentity",
                "invalid duplicate reused unsequenced or out-of-range target references are rejected");
            Check(readAttempts == 0, "target validation is complete before the first page read");
        }
        Rejected(() => NtfsMftEnumerator.Enumerate(map, [NtfsRecordFixtures.Reference(0, 4)], ReadOrdinary),
            "MftEnumerationTargetIdentity", "target slot reuse with a different sequence cannot match an earlier object");
        Rejected(() => NtfsMftEnumerator.Enumerate(map, [NtfsRecordFixtures.Reference(1, 3)], ReadMixed),
            "MftEnumerationTargetsMissing", "a free target does not become a successful missing or zero-size object");
        foreach (int slot in new[] { 2, 3 })
            Rejected(() => NtfsMftEnumerator.Enumerate(map, [NtfsRecordFixtures.Reference((ulong)slot, 3)], ReadMixed),
                "MftEnumerationTargetsMissing", "unsupported or invalid targets prevent publication of a complete comparison input");
        foreach (int received in new[] { -1, 0, 1024, 65537 })
            Rejected(() => NtfsMftEnumerator.Enumerate(Map(64), [], (_, _, _) => received), "MftEnumerationShortRead",
                "negative short empty or oversized read counts cannot publish a partial result");
        int shortReads = 0;
        Rejected(() => NtfsMftEnumerator.Enumerate(map, [], (offset, destination, token) =>
        {
            shortReads++;
            return shortReads == 2 ? destination.Length - 1 : ReadOrdinary(offset, destination, token);
        }), "MftEnumerationShortRead", "a short final page cannot turn earlier decoded pages into successful coverage");
        Check(shortReads == 2, "a short read stops without retrying or reading another page");
        Rejected(() => NtfsMftEnumerator.Enumerate(map, [], (_, _, _) => throw new IOException("SyntheticPrivateText")),
            "MftEnumerationReadFailed", "reader errors are reduced to a fixed code without private messages");
        foreach (bool duringRead in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            if (!duringRead) cancellation.Cancel();
            readAttempts = 0; bool canceled = false;
            try
            {
                NtfsMftEnumerator.Enumerate(map, [], (offset, destination, token) =>
                {
                    int count = ReadOrdinary(offset, destination, token); cancellation.Cancel(); return count;
                }, cancellation.Token);
            }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && readAttempts == (duringRead ? 1 : 0), "cancellation before or during a page prevents publication and further reads");
        }
        int faultReads = 0;
        Rejected(() => NtfsMftEnumerator.Enumerate(map, [], (offset, destination, token) =>
        {
            faultReads++;
            if (faultReads == 2) throw new IOException("SyntheticSourceClosed");
            return ReadOrdinary(offset, destination, token);
        }), "MftEnumerationReadFailed", "source invalidation between pages cannot return a complete result");
        Check(faultReads == 2, "source invalidation stops at the first failing page");
        using var finalPageCancellation = new CancellationTokenSource();
        int finalReads = 0; bool finalCanceled = false;
        try
        {
            NtfsMftEnumerator.Enumerate(map, [], (offset, destination, token) =>
            {
                finalReads++;
                int count = ReadOrdinary(offset, destination, token);
                if (finalReads == 2) finalPageCancellation.Cancel();
                return count;
            }, finalPageCancellation.Token);
        }
        catch (OperationCanceledException) { finalCanceled = true; }
        Check(finalCanceled && finalReads == 2, "late cancellation on the final page still prevents result publication");
        return passed;
    }

    private static NtfsMftMap Map(int slots, int clusterBytes = 4096)
    {
        long validBytes = checked((long)slots * 1024);
        int clusters = checked((int)((validBytes + clusterBytes - 1) / clusterBytes));
        byte[] runs = [0x12, (byte)clusters, (byte)(clusters >> 8), 10, 0];
        byte[] raw = NtfsMftMapTests.Record0(runs, (ulong)(clusters - 1), (long)clusters * clusterBytes, validBytes, validBytes);
        var geometry = new NtfsMftGeometry(512, clusterBytes, 1024, 256L * 1024 * 1024,
            256L * 1024 * 1024 / clusterBytes, 10, validBytes);
        var parsed = NtfsMftMap.Parse(raw, geometry);
        return parsed.Map ?? throw new InvalidOperationException("Synthetic enumeration map setup failed");
    }
}
