using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DiskAtlas.NtfsPrototype;

// Synthetic protected records and a deterministic memory reader only.
internal static class NtfsMftMetadataResolverTests
{
    private static readonly NtfsMftGeometry Geometry = new(512, 4096, 1024, 1024 * 1024, 256, 10, 16384);
    private const ulong RootReference = 3UL << 48;
    private static ulong Ref(ulong slot, ushort sequence = 9) => ((ulong)sequence << 48) | slot;

    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS MFT metadata resolver check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS MFT metadata resolver " + name);
        }
        NtfsMftMetadataResult Resolve(byte[] root, Dictionary<long, byte[]> reads, out int calls,
            CancellationToken token = default, NtfsMftGeometry? geometry = null)
        {
            int local = 0;
            var result = NtfsMftMetadataResolver.Resolve(root, geometry ?? Geometry, (offset, buffer, ct) =>
            {
                ct.ThrowIfCancellationRequested(); local++;
                if (!reads.TryGetValue(offset, out byte[]? source) || source.Length < buffer.Length)
                    throw new InvalidOperationException("private reader text must never escape");
                source.AsMemory(0, buffer.Length).CopyTo(buffer); return buffer.Length;
            }, token);
            calls = local; return result;
        }
        void Fail(byte[] root, Dictionary<long, byte[]> reads, string name, string? code = null)
        {
            var result = Resolve(root, reads, out _);
            Check(result.Metadata == null && result.Status is "Invalid" or "Unsupported" or "ReadFailed" or "LimitExceeded" &&
                (code == null || result.Code == code), name);
            Check(result.Code.Length is > 0 and <= 100 && result.Code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'),
                "failure exposes only a fixed bounded code");
        }

        byte[] simple = Root(false, false, list: null, split: false);
        byte[] before = simple.ToArray();
        var plain = Resolve(simple, [], out int calls);
        Check(plain.Status == "Resolved" && plain.Metadata != null && calls == 0, "single-record metadata needs no further reads");
        Check(simple.SequenceEqual(before), "resolver leaves protected caller bytes unchanged");
        Check(plain.Metadata!.Map.Runs.SequenceEqual([new NtfsMftRun(0, 4, 10)]), "single-record DATA map is complete");
        Check(plain.Metadata.Bitmap.Resident && plain.Metadata.Bitmap.CopyResidentData().SequenceEqual(new byte[] { 255, 255 }),
            "resident bitmap remains separate from DATA addressing");
        byte[] copy = plain.Metadata.Bitmap.CopyResidentData(); copy[0] = 0;
        Check(plain.Metadata.Bitmap.CopyResidentData()[0] == 255, "resident bitmap access returns a defensive copy");
        Check(NtfsMftMap.Parse(simple, Geometry).Status == "Mapped", "legacy map entry still accepts its old complete form");

        byte[] list = Entries((0x80, 0, RootReference, 1), (0x80, 2, Ref(4), 0), (0xb0, 0, RootReference, 2));
        byte[] splitRoot = Root(false, false, list);
        byte[] extension = ExtensionData();
        var goodReads = new Dictionary<long, byte[]> { [11 * 4096L] = extension };
        var split = Resolve(splitRoot, goodReads, out calls);
        Check(split.Status == "Resolved" && split.Metadata != null && calls == 1, "resident list resolves a reachable DATA continuation");
        Check(split.Metadata!.Map.Runs.SequenceEqual([new NtfsMftRun(0, 2, 10), new NtfsMftRun(2, 2, 40)]),
            "each attribute extent restarts its LCN delta at zero");
        Check(split.Metadata.Map.PlanRecord(8).Single().VolumeOffset == 40 * 4096L,
            "resolved continuation addresses records through its exact mapping");
        Check(NtfsMftMap.Parse(splitRoot, Geometry).Status == "Unsupported", "legacy parser does not silently gain extension support");
        Check(extension.SequenceEqual(ExtensionData()), "extension caller bytes remain protected and unchanged");

        byte[] nonresidentRoot = Root(true, false, list);
        var listReads = new Dictionary<long, byte[]> { [80 * 4096L] = Pad(list, 512), [11 * 4096L] = extension };
        var nonresident = Resolve(nonresidentRoot, listReads, out calls);
        Check(nonresident.Status == "Resolved" && calls == 2, "bounded nonresident attribute list is read through the same callback");

        byte[] bitList = Entries((0x80, 0, RootReference, 1), (0x80, 2, Ref(4), 0), (0xb0, 0, Ref(5), 0));
        byte[] bitRoot = Root(true, true, bitList);
        var bitReads = new Dictionary<long, byte[]> { [80 * 4096L] = Pad(bitList, 512), [11 * 4096L] = extension,
            [11 * 4096L + 1024] = NtfsRecordFixtures.Record([Nonresident(0xb0, 0, 0, 90, 1, 4096, 2, 2)],
                sequence: 9, baseReference: RootReference) };
        var bitResult = Resolve(bitRoot, bitReads, out calls);
        Check(bitResult.Status == "Resolved" && bitResult.Metadata != null && calls == 3,
            "BITMAP may reside in its own verified extension record");
        Check(!bitResult.Metadata!.Bitmap.Resident && bitResult.Metadata.Bitmap.Runs.SequenceEqual([new NtfsMftRun(0, 1, 90)]) &&
            bitResult.Metadata.Bitmap.DataBytes == 2 && bitResult.Metadata.Bitmap.InitializedBytes == 2 && bitResult.Metadata.Bitmap.AllocatedBytes == 4096,
            "nonresident bitmap preserves complete runs and three lengths without reading bitmap data");

        byte[] stagedList = Entries((0x80, 0, RootReference, 1), (0x80, 2, Ref(4), 0),
            (0xb0, 0, Ref(8), 0));
        var stagedReads = new Dictionary<long, byte[]> { [11 * 4096L] = extension,
            [40 * 4096L] = NtfsRecordFixtures.Record([NtfsRecordFixtures.Resident(0xb0, [255, 255])],
                sequence: 9, baseReference: RootReference) };
        var staged = Resolve(Root(false, true, stagedList), stagedReads, out calls);
        Check(staged.Status == "Resolved" && calls == 2,
            "later extension becomes readable only after its DATA mapping is established");

        byte[] badSequence = NtfsRecordFixtures.Record([Nonresident(0x80, 2, 3, 40, 2, -7, -8, -9)], sequence: 8, baseReference: RootReference);
        Fail(splitRoot, new() { [11 * 4096L] = badSequence }, "extension sequence must match full list reference", "MftExtensionSequence");
        byte[] badBase = NtfsRecordFixtures.Record([Nonresident(0x80, 2, 3, 40, 2, -7, -8, -9)], sequence: 9, baseReference: Ref(1));
        Fail(splitRoot, new() { [11 * 4096L] = badBase }, "extension base reference must bind to record zero", "MftExtensionBase");
        byte[] damaged = extension.ToArray(); damaged[510] ^= 1;
        Fail(splitRoot, new() { [11 * 4096L] = damaged }, "damaged raw USA is rejected", "MftRecord-FixupMismatch");
        byte[] restored = Restore(extension);
        Fail(splitRoot, new() { [11 * 4096L] = restored }, "logical restored representation never replaces raw validation", "MftRecord-FixupMismatch");

        Fail(Root(false, false, Entries((0x80, 0, RootReference, 1), (0x80, 2, Ref(4), 7), (0xb0, 0, RootReference, 2))), goodReads,
            "attribute instance must match its list selector", "MftListAttributeMismatch");
        Fail(Root(false, false, Entries((0x80, 0, RootReference, 1), (0x80, 2, Ref(4), 0), (0x80, 2, Ref(5), 0), (0xb0, 0, RootReference, 2))), [],
            "duplicate VCN selector is rejected", "MftListDuplicateExtent");
        Fail(Root(false, false, Entries((0x80, 0, RootReference, 1), (0x80, 2, Ref(4), 0))), goodReads,
            "unlisted resident bitmap is not trusted", "MftAttributeListCoverage");
        byte[] unreachableRoot = Root(false, false,
            Entries((0x80, 0, RootReference, 1), (0x80, 2, Ref(12), 0), (0xb0, 0, RootReference, 2)));
        var unreachable = Resolve(unreachableRoot, [], out calls);
        Check(unreachable.Status == "Unsupported" && unreachable.Code == "MftExtensionUnreachable" && calls == 0,
            "unreachable extension causes no speculative physical read");

        byte[] hole = NtfsRecordFixtures.Record([Nonresident(0x80, 3, 3, 40, 1, 0, 0, 0)], sequence: 9, baseReference: RootReference);
        Fail(Root(false, false, Entries((0x80, 0, RootReference, 1), (0x80, 3, Ref(4), 0), (0xb0, 0, RootReference, 2))),
            new() { [11 * 4096L] = hole }, "missing VCN extent prevents a partial map", "MftStreamExtentCoverage");
        byte[] overlap = NtfsRecordFixtures.Record([Nonresident(0x80, 2, 3, 11, 2, 0, 0, 0)], sequence: 9, baseReference: RootReference);
        Fail(splitRoot, new() { [11 * 4096L] = overlap }, "physically overlapping DATA extents are rejected", "MftRunPhysicalOverlap");
        byte[] beyond = NtfsRecordFixtures.Record([Nonresident(0x80, 2, 3, 255, 2, 0, 0, 0)], sequence: 9, baseReference: RootReference);
        Fail(splitRoot, new() { [11 * 4096L] = beyond }, "extension run cannot exceed volume geometry", "MftRunVolumeBounds");
        byte[] overlapBitmap = NtfsRecordFixtures.Record([Nonresident(0xb0, 0, 0, 40, 1, 4096, 2, 2)], sequence: 9, baseReference: RootReference);
        var overlappingBitReads = new Dictionary<long, byte[]>(bitReads) { [11 * 4096L + 1024] = overlapBitmap };
        Fail(bitRoot, overlappingBitReads, "BITMAP and DATA allocation may not overlap", "MftBitmapPhysicalOverlap");
        Fail(Root(true, false, list, listLcn: 10), [], "list and known DATA allocation may not overlap before any reads", "MftListPhysicalOverlap");

        byte[] badEntryLength = list.ToArray(); NtfsRecordFixtures.U16(badEntryLength, 4, 31);
        Fail(Root(false, false, badEntryLength), [], "list entry alignment is strict", "MftListEntryBounds");
        byte[] staleReference = Entries((0x80, 0, 4UL << 48, 1), (0xb0, 0, RootReference, 2));
        Fail(Root(false, false, staleReference), [], "record zero selector sequence is exact", "MftListReference");
        byte[] outOfVdl = Entries((0x80, 0, RootReference, 1), (0x80, 2, Ref(16), 0), (0xb0, 0, RootReference, 2));
        Fail(Root(false, false, outOfVdl), [], "list record is bounded by native valid length", "MftListReference");
        byte[] oversizedListAttr = Nonresident(0x20, 0, 16, 80, 17, 17 * 4096, 65537, 65537);
        byte[] oversizedRoot = NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("$MFT"),
            Nonresident(0x80, 0, 3, 10, 4, 16384, 16384, 16384), NtfsRecordFixtures.Resident(0xb0, [255, 255]), oversizedListAttr]);
        Fail(oversizedRoot, [], "list logical length has an independent 64 KiB bound", "MftAttributeListByteLimit");
        var tooManyEntries = new List<(uint, long, ulong, ushort)> { (0x80, 0, RootReference, 1), (0xb0, 0, RootReference, 2) };
        for (int i = 1; i <= 65; i++) tooManyEntries.Add((0x80, i + 1, Ref((ulong)i), 0));
        byte[] tooManyList = Entries(tooManyEntries.ToArray());
        byte[] tooManyRoot = NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("$MFT"),
            Nonresident(0x80, 0, 1, 10, 2, 67 * 4096, 67 * 4096, 67 * 4096),
            NtfsRecordFixtures.Resident(0xb0, new byte[16]), Nonresident(0x20, 0, 0, 100, 1, 4096, tooManyList.Length, tooManyList.Length)]);
        var tooMany = Resolve(tooManyRoot, new() { [100 * 4096L] = Pad(tooManyList, (tooManyList.Length + 511) / 512 * 512) },
            out calls, geometry: Geometry with { MftValidDataLength = 131072 });
        Check(tooMany.Status == "LimitExceeded" && tooMany.Code == "MftExtensionLimit" && calls == 1,
            "65 distinct extension records stop after the bounded list read");
        var vdlResult = Resolve(simple, [], out calls, geometry: Geometry with { MftValidDataLength = 17408 });
        Check(vdlResult.Metadata == null && calls == 0, "native VDL cannot exceed initialized MFT data");

        var shortRead = NtfsMftMetadataResolver.Resolve(splitRoot, Geometry, (_, _, _) => 0);
        Check(shortRead.Status == "ReadFailed" && shortRead.Code == "MftShortRead" && shortRead.Metadata == null,
            "short callback read does not publish partial metadata");
        var exception = NtfsMftMetadataResolver.Resolve(splitRoot, Geometry, (_, _, _) => throw new IOException("D:\\private-secret"));
        Check(exception.Status == "ReadFailed" && exception.Code == "MftReadFailed", "reader exception text never escapes");
        var fakeCancellation = NtfsMftMetadataResolver.Resolve(splitRoot, Geometry, (_, _, _) => throw new OperationCanceledException("private"));
        Check(fakeCancellation.Status == "ReadFailed" && fakeCancellation.Code == "MftReadFailed", "foreign cancellation is sanitized");
        using var cancellation = new CancellationTokenSource();
        bool canceled = false;
        try
        {
            NtfsMftMetadataResolver.Resolve(splitRoot, Geometry, (_, buffer, _) =>
            { extension.AsMemory().CopyTo(buffer); cancellation.Cancel(); return buffer.Length; }, cancellation.Token);
        }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "real cancellation after a read throws before publishing the bundle");
        cancellation.Cancel(); canceled = false;
        try { Resolve(simple, [], out _, cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "pre-cancellation prevents metadata work");

        var mutableRuns = new List<NtfsMftRun> { new(0, 4, 10) };
        var factory = NtfsMftMap.FromResolvedLayout(Geometry, mutableRuns, 16384, 16384, 16384);
        mutableRuns[0] = new(0, 4, 30);
        Check(factory.Map != null && factory.Map.Runs[0].FirstLcn == 10, "safe factory takes ownership of a defensive layout copy");
        Check(NtfsMftMap.FromResolvedLayout(Geometry, [new(0, 2, 10), new(3, 2, 40)], 16384, 16384, 16384).Map == null,
            "safe factory independently rejects incomplete VCN coverage");
        var manyRuns = Enumerable.Range(0, 1024).Select(i => new NtfsMftRun(i, 1, i + 10)).ToList();
        var manyGeometry = Geometry with { VolumeBytes = 8 * 1024 * 1024, TotalClusters = 2048 };
        var manyMap = NtfsMftMap.FromResolvedLayout(manyGeometry, manyRuns, 1024 * 4096, 1024 * 4096, 1024 * 4096);
        Check(manyMap.Map != null && manyMap.Map.Runs.Count == 1024,
            "resolved factory accepts its exact 1024-run budget");
        manyRuns.Add(new(1024, 1, 1034));
        Check(NtfsMftMap.FromResolvedLayout(manyGeometry, manyRuns, 1025 * 4096, 1025 * 4096, 1025 * 4096).Map == null,
            "resolved factory never truncates excess runs");
        passed += ReaderCombinationTests();
        return passed;
    }

    private static int ReaderCombinationTests()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS MFT metadata reader combination check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS MFT metadata reader combination " + name);
        }
        byte[] list544 = FilledEntries(544);
        byte[] root544 = CompleteRootWithList(Nonresident(0x20, 0, 0, 80, 1, 4096, 544, 544));
        foreach (bool large in new[] { false, true })
        {
            var native = new ResolverMemoryNative(new() { [80 * 4096L] = PadWithSentinel(list544, 1024) });
            var reader = NtfsSnapshotVolumeReader.ForPersonalTests(native, ResolverMemoryNative.Serial, Geometry.VolumeBytes, large);
            try
            {
                var facts = reader.Facts();
                var result = NtfsMftMetadataResolver.Resolve(root544, facts.Geometry, reader.ReadInto);
                Check(result.Status == "Resolved" && result.Metadata != null && reader.IsSynthetic,
                    "544-byte list resolves through the real reader seam in " + (large ? "Large" : "Standard"));
                Check(reader.ReadCalls == 1 && reader.RequestedBytes == 1024 &&
                    native.Reads.SequenceEqual(new[] { (80 * 4096L, 1024) }),
                    "544-byte logical list charges one exact 1024-byte physical request");
                Check(native.AllBuffersAligned && native.AllRequestsAligned && native.FactsCalls == 1,
                    "reader supplies its aligned buffer and caches verified geometry");
                Check(result.Metadata!.Bitmap.CopyResidentData().SequenceEqual(new byte[] { 255, 255 }),
                    "sector padding is excluded from logical list parsing");
            }
            finally { reader.Dispose(); }
            Check(reader.BufferReleased && native.Closed && native.Allocations == 1 && native.Frees == 1,
                "combined success releases its single allocation and native seam");
        }

        byte[] list4128 = FilledEntries(4128);
        foreach (byte secondLcn in new byte[] { 70, 90 })
        {
            byte[] attr = Nonresident(0x20, 0, 1, 80, 2, 8192, 4128, 4128);
            // Two physical extents. The second delta is independently encoded
            // as signed -10 or +10, never mistaken for an absolute LCN.
            attr[64] = 0x21; attr[65] = 1; attr[66] = 80; attr[67] = 0;
            attr[68] = 0x11; attr[69] = 1; attr[70] = unchecked((byte)(secondLcn - 80)); attr[71] = 0;
            byte[] tail = list4128.AsSpan(4096).ToArray();
            var native = new ResolverMemoryNative(new()
            {
                [80 * 4096L] = list4128.AsSpan(0, 4096).ToArray(),
                [secondLcn * 4096L] = PadWithSentinel(tail, 512)
            });
            var reader = NtfsSnapshotVolumeReader.ForPersonalTests(native, ResolverMemoryNative.Serial, Geometry.VolumeBytes);
            try
            {
                var result = NtfsMftMetadataResolver.Resolve(CompleteRootWithList(attr), reader.Facts().Geometry, reader.ReadInto);
                Check(result.Status == "Resolved" && result.Metadata != null,
                    "4128-byte list crosses a nonadjacent " + (secondLcn < 80 ? "backward" : "forward") + " extent");
                Check(reader.ReadCalls == 2 && reader.RequestedBytes == 4608 && native.Reads.SequenceEqual(
                    new[] { (80 * 4096L, 4096), (secondLcn * 4096L, 512) }),
                    "cross-extent reads charge 4096 plus 512 in logical destination order");
                Check(native.AllBuffersAligned && native.AllRequestsAligned,
                    "every cross-extent request preserves sector and native-buffer alignment");
            }
            finally { reader.Dispose(); }
            Check(reader.BufferReleased && native.Closed && native.Frees == 1,
                "cross-extent reader releases memory after parsing its sliced destination");
        }

        byte[] listMaximum = FilledEntries(NtfsMftMetadataResolver.MaximumAttributeListBytes);
        var maximumNative = new ResolverMemoryNative(new() { [80 * 4096L] = listMaximum });
        var maximumReader = NtfsSnapshotVolumeReader.ForPersonalTests(maximumNative, ResolverMemoryNative.Serial, Geometry.VolumeBytes);
        try
        {
            byte[] rootMaximum = CompleteRootWithList(Nonresident(0x20, 0, 15, 80, 16, 65536, 65536, 65536));
            var result = NtfsMftMetadataResolver.Resolve(rootMaximum, maximumReader.Facts().Geometry, maximumReader.ReadInto);
            Check(result.Status == "Resolved" && result.Metadata != null && maximumReader.ReadCalls == 1 &&
                maximumReader.RequestedBytes == 65536 && maximumNative.Reads.SequenceEqual(new[] { (80 * 4096L, 65536) }),
                "exact 64-KiB list limit crosses the reader without overflow or a larger request");
        }
        finally { maximumReader.Dispose(); }
        Check(maximumReader.BufferReleased && maximumNative.Frees == 1, "maximum list buffer is released");

        foreach (string fault in new[] { "Seek", "Read", "Short", "Exception", "ForeignCancellation" })
        {
            var native = new ResolverMemoryNative(new() { [80 * 4096L] = PadWithSentinel(list544, 1024) }) { Fault = fault };
            var reader = NtfsSnapshotVolumeReader.ForPersonalTests(native, ResolverMemoryNative.Serial, Geometry.VolumeBytes);
            try
            {
                var result = NtfsMftMetadataResolver.Resolve(root544, reader.Facts().Geometry, reader.ReadInto);
                Check(result.Metadata == null && result.Status == "ReadFailed" && result.Code == "MftReadFailed",
                    fault + " from the reader exposes only a fixed resolver failure and no partial bundle");
                Check(reader.ReadCalls == (fault == "Seek" ? 0 : 1) && reader.RequestedBytes == (fault == "Seek" ? 0 : 1024),
                    fault + " retains the reader attempt-budget accounting");
            }
            finally { reader.Dispose(); }
            Check(reader.BufferReleased && native.Closed && native.Frees == 1,
                fault + " releases the allocated buffer and closes its seam");
        }

        foreach (bool byteBudget in new[] { false, true })
        {
            int requestBytes = byteBudget ? 65536 : 512;
            int attempts = byteBudget ? checked((int)(NtfsSnapshotVolumeReader.PersonalMaximumBytes / requestBytes)) :
                NtfsSnapshotVolumeReader.ScaleMaximumCalls;
            var native = new ResolverMemoryNative(new() { [0] = new byte[requestBytes], [80 * 4096L] = PadWithSentinel(list544, 1024) });
            var reader = NtfsSnapshotVolumeReader.ForPersonalTests(native, ResolverMemoryNative.Serial, Geometry.VolumeBytes);
            try
            {
                byte[] scratch = new byte[requestBytes];
                var facts = reader.Facts();
                for (int i = 0; i < attempts; i++) reader.ReadInto(0, scratch);
                int nativeReads = native.Reads.Count, nativeSeeks = native.Seeks;
                long charged = reader.RequestedBytes;
                var result = NtfsMftMetadataResolver.Resolve(root544, facts.Geometry, reader.ReadInto);
                Check(result.Metadata == null && result.Status == "ReadFailed" && result.Code == "MftReadFailed",
                    (byteBudget ? "byte" : "call") + " budget exhaustion stays sanitized without partial metadata");
                Check(reader.ReadCalls == attempts && reader.RequestedBytes == charged &&
                    native.Reads.Count == nativeReads && native.Seeks == nativeSeeks,
                    "exhausted " + (byteBudget ? "byte" : "call") + " budget rejects before any further native seek or read");
                Check(!byteBudget || charged == NtfsSnapshotVolumeReader.PersonalMaximumBytes,
                    "byte budget reaches its exact fixed boundary without wraparound");
            }
            finally { reader.Dispose(); }
            Check(reader.BufferReleased && native.Closed && native.Frees == 1,
                "budget failure releases the pre-existing reader allocation");
        }
        return passed;
    }

    private static byte[] FilledEntries(int length)
    {
        if (length < 64 || length % 32 != 0) throw new ArgumentOutOfRangeException(nameof(length));
        var entries = new List<(uint, long, ulong, ushort)> { (0x80, 0, RootReference, 1), (0xb0, 0, RootReference, 2) };
        // Repeated unrelated selectors are intentionally not consulted for the
        // DATA/BITMAP bundle. Their nonzero headers detect a missing tail copy.
        while (entries.Count < length / 32) entries.Add((0x10, 0, RootReference, 4));
        return Entries(entries.ToArray());
    }

    private static byte[] CompleteRootWithList(byte[] listAttribute) => NtfsRecordFixtures.Record([
        NtfsRecordFixtures.Name("$MFT"), Nonresident(0x80, 0, 3, 10, 4, 16384, 16384, 16384),
        NtfsRecordFixtures.Resident(0xb0, [255, 255]), listAttribute]);

    private static byte[] PadWithSentinel(byte[] source, int length)
    {
        var result = Enumerable.Repeat((byte)0xcc, length).ToArray(); source.CopyTo(result, 0); return result;
    }

    private sealed class ResolverMemoryNative(Dictionary<long, byte[]> blocks) : NtfsSnapshotVolumeReader.INative
    {
        internal const ulong Serial = 0x12345678;
        internal string Fault { get; init; } = "";
        internal readonly List<(long Offset, int Length)> Reads = [];
        internal int FactsCalls, Seeks, Allocations, Frees;
        internal bool Closed, AllBuffersAligned = true, AllRequestsAligned = true;
        private long position;

        public NtfsSnapshotVolumeReader.ControlResult Facts(byte[] output)
        {
            FactsCalls++;
            NtfsRecordFixtures.U64(output, 0, Serial); NtfsRecordFixtures.U64(output, 8, (ulong)Geometry.VolumeBytes / 512);
            NtfsRecordFixtures.U64(output, 16, (ulong)Geometry.TotalClusters);
            NtfsRecordFixtures.U32(output, 40, 512); NtfsRecordFixtures.U32(output, 44, 4096); NtfsRecordFixtures.U32(output, 48, 1024);
            NtfsRecordFixtures.U64(output, 56, (ulong)Geometry.MftValidDataLength); NtfsRecordFixtures.U64(output, 64, (ulong)Geometry.MftStartLcn);
            NtfsRecordFixtures.U16(output, 100, 3); NtfsRecordFixtures.U16(output, 102, 1);
            return new(true, 104, 0);
        }
        public NtfsSnapshotVolumeReader.Result Seek(long offset)
        {
            Seeks++; position = offset;
            return Fault == "Seek" ? new(false, 87) : new(true, 0);
        }
        public NtfsSnapshotVolumeReader.ReadResult Read(IntPtr buffer, int length)
        {
            Reads.Add((position, length));
            AllBuffersAligned &= ((nuint)buffer & (nuint)65535) == 0;
            AllRequestsAligned &= position >= 0 && position % 512 == 0 && length is > 0 and <= 65536 && length % 512 == 0;
            if (Fault == "Read") return new(false, 0, 23);
            if (Fault == "Short") return new(true, length - 512, 0);
            if (Fault == "Exception") throw new IOException("private fake native message D:\\do-not-publish");
            if (Fault == "ForeignCancellation") throw new OperationCanceledException("private fake cancellation");
            if (!blocks.TryGetValue(position, out byte[]? source) || source.Length < length)
                throw new InvalidOperationException("missing synthetic block");
            Marshal.Copy(source, 0, buffer, length); return new(true, length, 0);
        }
        public IntPtr Allocate(int bytes) { Allocations++; return Marshal.AllocHGlobal(bytes); }
        public void Free(IntPtr buffer) { Frees++; Marshal.FreeHGlobal(buffer); }
        public void Dispose() { Closed = true; }
    }

    private static byte[] Root(bool nonresidentList, bool bitmapInExtension, byte[]? list, bool split = true, byte listLcn = 80)
    {
        var attrs = new List<byte[]> { NtfsRecordFixtures.Name("$MFT"),
            Nonresident(0x80, 0, split ? 1 : 3, 10, split ? 2 : 4, 16384, 16384, 16384) };
        if (!bitmapInExtension) attrs.Add(NtfsRecordFixtures.Resident(0xb0, [255, 255]));
        if (list != null) attrs.Add(nonresidentList ? Nonresident(0x20, 0, 0, listLcn, 1, 4096, list.Length, list.Length) :
            NtfsRecordFixtures.Resident(0x20, list));
        return NtfsRecordFixtures.Record(attrs);
    }

    private static byte[] ExtensionData() => NtfsRecordFixtures.Record(
        [Nonresident(0x80, 2, 3, 40, 2, -7, -8, -9)], sequence: 9, baseReference: RootReference);

    private static byte[] Nonresident(uint type, long low, long high, byte lcn, int clusters, long allocated, long data, long initialized)
    {
        var attr = new byte[72]; NtfsRecordFixtures.U32(attr, 0, type); NtfsRecordFixtures.U32(attr, 4, 72); attr[8] = 1;
        NtfsRecordFixtures.U64(attr, 16, (ulong)low); NtfsRecordFixtures.U64(attr, 24, (ulong)high); NtfsRecordFixtures.U16(attr, 32, 64);
        NtfsRecordFixtures.U64(attr, 40, unchecked((ulong)allocated)); NtfsRecordFixtures.U64(attr, 48, unchecked((ulong)data));
        NtfsRecordFixtures.U64(attr, 56, unchecked((ulong)initialized)); attr[64] = 0x21; attr[65] = (byte)clusters;
        attr[66] = lcn; attr[67] = 0; return attr;
    }

    private static byte[] Entries(params (uint Type, long Low, ulong Reference, ushort Instance)[] entries)
    {
        var value = new byte[entries.Length * 32];
        for (int i = 0; i < entries.Length; i++)
        {
            int p = i * 32; var entry = entries[i]; NtfsRecordFixtures.U32(value, p, entry.Type);
            NtfsRecordFixtures.U16(value, p + 4, 32); NtfsRecordFixtures.U64(value, p + 8, (ulong)entry.Low);
            NtfsRecordFixtures.U64(value, p + 16, entry.Reference); NtfsRecordFixtures.U16(value, p + 24, entry.Instance);
        }
        return value;
    }
    private static byte[] Pad(byte[] source, int length) { var result = new byte[length]; source.CopyTo(result, 0); return result; }
    private static byte[] Restore(byte[] source)
    {
        byte[] result = source.ToArray(); int usa = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(4));
        for (int i = 1; i <= source.Length / 512; i++)
            NtfsRecordFixtures.U16(result, i * 512 - 2, BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(usa + i * 2)));
        return result;
    }
}
