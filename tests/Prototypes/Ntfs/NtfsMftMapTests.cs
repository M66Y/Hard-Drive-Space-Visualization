using System.Buffers.Binary;

namespace DiskAtlas.NtfsPrototype;

// Pure deterministic bytes and address plans only. No files, handles or native I/O.
internal static class NtfsMftMapTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS MFT map check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS MFT map " + name);
        }
        void Rejected(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, name);
        }
        void Failed(byte[] bytes, NtfsMftGeometry geometry, string name, string? status = null)
        {
            var result = NtfsMftMap.Parse(bytes, geometry);
            Check(result.Map == null && (result.Status is "Invalid" or "Unsupported") &&
                (status == null || result.Status == status), name);
            Check(result.Code.Length is > 0 and <= 80 && result.Code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'),
                "failure uses only a bounded controlled code");
        }
        NtfsMftMap Mapped(byte[] bytes, NtfsMftGeometry geometry, string name)
        {
            var result = NtfsMftMap.Parse(bytes, geometry);
            Check(result.Status == "Mapped" && result.Code == "Mapped" && result.Map != null, name);
            return result.Map!;
        }
        var geometry = new NtfsMftGeometry(512, 4096, 1024, 1024 * 1024, 256, 10, 4096);
        byte[] bytes = Record0();
        byte[] original = (byte[])bytes.Clone();
        var map = Mapped(bytes, geometry, "strict protected record zero creates a map");
        Check(bytes.SequenceEqual(original), "parse leaves caller bytes unchanged");
        Check(map.DataBytes == 4096 && map.InitializedBytes == 4096 && map.AllocatedBytes == 16384,
            "logical initialized and allocated lengths stay distinct");
        Check(map.Runs.SequenceEqual(new[] { new NtfsMftRun(0, 4, 10) }), "run representation preserves VCN length and LCN");
        Check(map.PlanRecord(0).SequenceEqual(new[] { new NtfsRawSlice(40960, 1024, 0) }), "bootstrap slot maps to its verified native LCN");
        Check(map.PlanRecord(3).SequenceEqual(new[] { new NtfsRawSlice(44032, 1024, 0) }), "final initialized record is addressable");
        Rejected(() => map.PlanRecord(4), "record beyond native valid data length is rejected");
        Rejected(() => map.PlanRecord(NtfsRecordDecoder.MaximumRecordNumber + 1), "full references cannot masquerade as slot numbers");
        bool immutable = false;
        try { ((IList<NtfsMftRun>)map.Runs)[0] = new(0, 4, 20); } catch (NotSupportedException) { immutable = true; }
        Check(immutable, "run list cannot be edited by a consumer");
        bytes[0] ^= 1;
        Check(map.PlanRecord(0)[0].VolumeOffset == 40960, "map retains no caller buffer dependency");
        Mapped(Record0(name: "$mFt"), geometry, "MFT name is compared ordinally without case");
        Failed(Record0(name: "ordinary.bin"), geometry, "a different file cannot seed the MFT map", "Invalid");
        Failed(Record0(nameSpace: 2), geometry, "a DOS-only MFT name cannot establish the seed", "Invalid");

        var fragmented = Mapped(Record0([0x11, 2, 10, 0x11, 2, 250, 0], 3, 16384, 16384, 16384),
            geometry with { MftValidDataLength = 16384 }, "negative LCN delta supports ordinary fragmentation");
        Check(fragmented.Runs[1] == new NtfsMftRun(2, 2, 4), "negative LCN delta is sign extended");
        Check(fragmented.PlanRecord(8).SequenceEqual(new[] { new NtfsRawSlice(16384, 1024, 0) }),
            "slot addressing follows the MFT data stream instead of assumed contiguous storage");
        foreach (long startLcn in new[] { (1L << 31) / 4096 + 1, (1L << 32) / 4096 + 1 })
        {
            var farGeometry = geometry with { VolumeBytes = 1L << 34, TotalClusters = (1L << 34) / 4096, MftStartLcn = startLcn };
            byte[] farSource = Record0(WideInitialRun(startLcn, 4));
            byte[] before = (byte[])farSource.Clone();
            var farMap = Mapped(farSource, farGeometry, "physical addressing beyond signed and unsigned 32-bit offsets stays valid");
            Check(farMap.PlanRecord(0).SequenceEqual(new[] { new NtfsRawSlice(startLcn * 4096, 1024, 0) }) &&
                farMap.PlanRecord(3).SequenceEqual(new[] { new NtfsRawSlice(startLcn * 4096 + 3072, 1024, 0) }),
                "far physical offset and intra-extent displacement retain all 64-bit address bits");
            Check(farMap.PlanRange(1024, 2048).SequenceEqual(new[] { new NtfsRawSlice(startLcn * 4096 + 1024, 2048, 0) }),
                "range planning also preserves physical offsets above both 32-bit boundaries");
            Check(farSource.SequenceEqual(before), "far addressing preserves the caller protected bytes");
            Rejected(() => farMap.PlanRecord(4), "far physical address does not bypass native valid-length bounds");
        }
        var signedVectors = new (long Start, long End, byte[] EncodedDelta)[]
        {
            (1000, 700, [0x21, 2, 0xd4, 0xfe]), // -300, signed 16-bit.
            (2_000_000, 1_900_000, [0x41, 2, 0x60, 0x79, 0xfe, 0xff]), // -100000, signed 32-bit.
            (5_000_000_000, 2_000_000_000, [0x81, 2, 0x00, 0xa2, 0x2f, 0x4d, 0xff, 0xff, 0xff, 0xff]) // -3000000000, signed 64-bit.
        };
        foreach (var vector in signedVectors)
        {
            byte[] encoded = WideInitialRun(vector.Start, 2, vector.EncodedDelta);
            byte[] signedSource = Record0(encoded, 3, 16384, 16384, 16384);
            byte[] before = (byte[])signedSource.Clone();
            var signedGeometry = geometry with { VolumeBytes = 32_768_000_000_000, TotalClusters = 8_000_000_000,
                MftStartLcn = vector.Start, MftValidDataLength = 16384 };
            var signedMap = Mapped(signedSource, signedGeometry, "multi-byte signed LCN deltas remain valid without overflow");
            Check(signedMap.Runs.SequenceEqual(new[] { new NtfsMftRun(0, 2, vector.Start), new NtfsMftRun(2, 2, vector.End) }),
                "signed delta maps to the independently specified backward physical extent");
            Check(signedMap.PlanRecord(8).SequenceEqual(new[] { new NtfsRawSlice(vector.End * 4096, 1024, 0) }) &&
                signedMap.PlanRecord(15).SequenceEqual(new[] { new NtfsRawSlice(vector.End * 4096 + 7168, 1024, 0) }),
                "backward extent addresses and final record preserve exact 64-bit offsets");
            Check(signedMap.PlanRange(7168, 2048).SequenceEqual(new[] {
                new NtfsRawSlice(vector.Start * 4096 + 7168, 1024, 0), new NtfsRawSlice(vector.End * 4096, 1024, 1024) }),
                "range planning preserves every signed-width backward transition");
            Check(signedSource.SequenceEqual(before), "signed fragmented planning leaves caller bytes unchanged");
            Rejected(() => signedMap.PlanRecord(16), "signed fragmented address does not bypass initialized-length bounds");
        }
        var adjacent = Mapped(Record0([0x11, 2, 10, 0x11, 2, 2, 0], 3), geometry,
            "adjacent physical extents are permitted without overlap");
        Check(adjacent.Runs.Count == 2, "validated extent identities are not silently merged");
        var splitGeometry = geometry with { ClusterBytes = 512, TotalClusters = 2048 };
        var split = Mapped(Record0([0x11, 3, 10, 0x11, 5, 250, 0], 7, 4096), splitGeometry,
            "synthetic geometry permits a record spanning two extents");
        var splitPlan = split.PlanRecord(1);
        Check(splitPlan.SequenceEqual(new[] { new NtfsRawSlice(6144, 512, 0), new NtfsRawSlice(2048, 512, 512) }),
            "split reads preserve logical order and exact destination offsets");
        Check(splitPlan.Sum(x => x.Length) == splitGeometry.RecordBytes && splitPlan.All(x => x.Length > 0 && x.Length % 512 == 0),
            "split plan fills exactly one sector-aligned record");
        Check(split.PlanRange(1024, 2048).SequenceEqual(new[] {
            new NtfsRawSlice(6144, 512, 0), new NtfsRawSlice(2048, 1536, 512) }),
            "a range preserves destination continuity through a backwards physical run");
        Check(map.PlanRange(0, 4096).SequenceEqual(new[] { new NtfsRawSlice(40960, 4096, 0) }),
            "a complete small valid range has exact coverage");
        Check(map.PlanRange(3072, 1024).SequenceEqual(map.PlanRecord(3)), "EOF range and final record share exact addresses");
        foreach (var range in new (long Offset, int Length)[] {
            (-1024, 1024), (1, 1024), (0, 0), (0, -1024), (0, 512), (0, 66560),
            (4096, 1024), (3072, 2048), (long.MaxValue - 1023, 1024) })
            Rejected(() => map.PlanRange(range.Offset, range.Length), "invalid range never returns a truncated prefix plan");
        const int pageVdl = 65536 + 2048;
        var pageGeometry = geometry with { MftValidDataLength = pageVdl };
        byte[] pageSource = Record0([0x11, 16, 10, 0x11, 16, 32, 0], 31, 131072, pageVdl, pageVdl);
        byte[] pageBefore = (byte[])pageSource.Clone();
        var pageMap = Mapped(pageSource, pageGeometry, "page map covers an exact final short page");
        Check(pageMap.PlanRange(0, 65536).SequenceEqual(new[] { new NtfsRawSlice(40960, 65536, 0) }),
            "exact 64 KiB range endpoint remains bounded");
        Check(pageMap.PlanRange(65536, 2048).SequenceEqual(new[] { new NtfsRawSlice(172032, 2048, 0) }),
            "final short page ends exactly at native valid data length");
        Check(pageMap.PlanRange(1024, 65536).SequenceEqual(new[] {
            new NtfsRawSlice(41984, 64512, 0), new NtfsRawSlice(172032, 1024, 64512) }),
            "one page may span runs without gaps overlaps or reordered destination bytes");
        var backwardsPage = Mapped(Record0([0x11, 16, 40, 0x11, 16, 224, 0], 31, 131072, pageVdl, pageVdl),
            pageGeometry with { MftStartLcn = 40 }, "range map permits a negative run delta");
        Check(backwardsPage.PlanRange(64512, 3072).SequenceEqual(new[] {
            new NtfsRawSlice(228352, 1024, 0), new NtfsRawSlice(32768, 2048, 1024) }),
            "range physical addresses retain a valid negative LCN transition");
        Check(pageSource.SequenceEqual(pageBefore), "range planning does not modify protected caller bytes");
        bool rangeImmutable = false;
        try { ((IList<NtfsRawSlice>)pageMap.PlanRange(0, 1024))[0] = new(0, 1024, 0); }
        catch (NotSupportedException) { rangeImmutable = true; }
        Check(rangeImmutable, "range slices cannot be edited by a consumer");
        Failed(Record0([0x11, 1, 10, 0x11, 7, 10, 0], 7, 4096), splitGeometry,
            "fragmentation inside bootstrap record zero is explicitly unsupported", "Unsupported");
        Failed(Record0([0x11, 2, 10, 0x11, 2, 255, 0], 3), geometry,
            "physical MFT runs cannot overlap", "Invalid");
        Failed(Record0([0x11, 4, 11, 0], 3), geometry, "first run must match native MFT start", "Invalid");
        Failed(Record0([0x11, 4, 254, 0], 3), geometry, "negative first physical address is rejected", "Invalid");
        Failed(Record0([0x21, 4, 254, 0, 0], 3), geometry, "run ending beyond the volume is rejected", "Invalid");
        Failed(Record0([0x11, 0, 10, 0], 3), geometry, "zero-length run is rejected", "Invalid");
        Failed(Record0([0x01, 4, 0], 3), geometry, "a sparse run cannot supply physical MFT addresses", "Invalid");
        Failed(Record0([0], 3), geometry, "empty mapping is rejected", "Invalid");
        Failed(Record0([0x88, 1, 2, 3, 4, 5, 6, 7], 3), geometry, "truncated run integer is rejected", "Invalid");
        Failed(Record0([0x11, 1, 10, 0x11, 1, 1, 0x11, 1], 3), geometry, "missing run terminator is rejected", "Invalid");
        Failed(Record0([0x11, 3, 10, 0], 3), geometry, "VCN undercoverage is rejected", "Invalid");
        Failed(Record0([0x11, 5, 10, 0], 3), geometry, "VCN overcoverage is rejected", "Invalid");
        Failed(Record0([0x11, 4, 10, 0], 3, 8192), geometry, "allocated length must match the complete map", "Invalid");
        Failed(Record0([0x81, 1, 255, 255, 255, 255, 255, 255, 255, 127, 0], 0, 4096, 1024, 1024),
            geometry with { MftValidDataLength = 1024 }, "LCN end arithmetic overflow is controlled", "Invalid");

        var many = new List<byte>();
        for (int i = 0; i < NtfsMftMap.MaxRuns; i++) many.AddRange(new byte[] { 0x11, 1, (byte)(i == 0 ? 10 : 1) });
        many.Add(0);
        Check(Mapped(Record0(many.ToArray(), 127, 128L * 4096), geometry, "exact maximum run count is accepted").Runs.Count == 128,
            "all maximum-budget extents are retained");
        many.RemoveAt(many.Count - 1); many.AddRange(new byte[] { 0x11, 1, 1, 0 });
        Failed(Record0(many.ToArray(), 128, 129L * 4096), geometry, "excess run count is unsupported rather than truncated", "Unsupported");
        var lengthMap = Mapped(Record0(size: 8192, initialized: 6144), geometry, "native initialized data may be shorter than EOF and allocation");
        Check(lengthMap.DataBytes == 8192 && lengthMap.InitializedBytes == 6144 && lengthMap.PlanRecord(3).Count == 1,
            "all three length bounds are preserved");
        Rejected(() => lengthMap.PlanRecord(4), "native initialized boundary limits otherwise covered records");
        Failed(Record0(initialized: 2048), geometry, "native valid length cannot exceed DATA initialization", "Invalid");
        Failed(Record0(size: 4096, initialized: 5120), geometry, "initialization beyond EOF is rejected", "Invalid");
        Failed(Record0(size: 4608), geometry, "partial MFT record EOF is rejected", "Invalid");
        Failed(Record0(initialized: 3584), geometry with { MftValidDataLength = 3072 }, "partial initialized MFT record is rejected", "Invalid");
        Failed(Record0(size: 0, initialized: 0), geometry, "empty MFT data cannot seed record zero", "Invalid");

        var invalidGeometry = new[] {
            geometry with { ClusterBytes = 0 }, geometry with { ClusterBytes = 3072 }, geometry with { ClusterBytes = 131072 },
            geometry with { RecordBytes = 512 }, geometry with { RecordBytes = 1025 }, geometry with { RecordBytes = 66048 },
            geometry with { VolumeBytes = -1 }, geometry with { VolumeBytes = 1048577 }, geometry with { TotalClusters = 0 },
            geometry with { TotalClusters = 257 }, geometry with { TotalClusters = long.MaxValue }, geometry with { MftStartLcn = -1 },
            geometry with { MftStartLcn = 256 }, geometry with { MftValidDataLength = 0 }, geometry with { MftValidDataLength = 1025 }
        };
        foreach (var bad in invalidGeometry) Failed(Record0(), bad, "invalid geometry is rejected before addressing", "Invalid");
        Failed(Record0(), null!, "null geometry is a controlled input failure", "Invalid");
        Failed(Record0(), geometry with { VolumeBytes = 512, TotalClusters = 1, ClusterBytes = 512, MftStartLcn = 0 },
            "bootstrap record cannot extend past the volume", "Invalid");
        Failed(Record0(), geometry with { SectorBytes = 4096 }, "other fixup stride remains unsupported", "Unsupported");
        Failed(new byte[1023], geometry, "short record source is rejected", "Invalid");
        Failed(new byte[65537], geometry, "record source exceeding 64 KiB is rejected", "Invalid");
        Failed(Record0(extra: NtfsRecordFixtures.Resident(0x20, [])), geometry, "attribute lists require a separate implementation", "Unsupported");
        Failed(Record0(extra: NtfsRecordFixtures.Data(0, "meta")), geometry, "named DATA does not silently broaden supported MFT form", "Unsupported");
        Failed(Record0(extra: NtfsRecordFixtures.Nonresident(4096, 16384)), geometry, "duplicate unnamed DATA remains unsupported", "Unsupported");
        Failed(Record0(baseReference: 7), geometry, "an extension cannot seed the MFT map", "Unsupported");
        Failed(Record0(flags: 1), geometry, "compressed MFT DATA is unsupported", "Unsupported");
        Failed(Record0(flags: 0x8000), geometry, "sparse MFT DATA is unsupported", "Unsupported");
        Failed(Record0(flags: 0x4000), geometry, "encrypted MFT DATA is unsupported", "Unsupported");
        Failed(Record0(lowVcn: 1, highVcn: 4), geometry, "continuation VCN cannot masquerade as a complete map", "Unsupported");
        Failed(NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("$MFT"), NtfsRecordFixtures.Data(0)]), geometry,
            "resident DATA is outside the MFT mapping contract", "Unsupported");
        Failed(NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("$MFT")]), geometry,
            "missing DATA does not become an empty map", "Unsupported");
        var unusualUnit = Record0();
        NtfsRecordFixtures.U16(unusualUnit, NtfsRecordFixtures.FirstAttribute + NtfsRecordFixtures.Name("$MFT").Length + 34, 1);
        Failed(unusualUnit, geometry, "ordinary DATA cannot carry a compression unit", "Unsupported");
        var directory = Record0(); NtfsRecordFixtures.U16(directory, 22, 3);
        Failed(directory, geometry, "directory metadata cannot seed MFT mapping", "Unsupported");
        var free = Record0(); NtfsRecordFixtures.U16(free, 22, 0);
        Failed(free, geometry, "a free record cannot seed MFT mapping", "Invalid");
        var unknownSignature = Record0(); unknownSignature[0] = (byte)'X';
        Failed(unknownSignature, geometry, "invalid signature cannot establish a map", "Invalid");
        var damaged = Record0(); damaged[510] ^= 1;
        Failed(damaged, geometry, "strict raw USA damage remains rejected", "Invalid");
        var restored = Restore(Record0());
        Failed(restored, geometry, "already restored logical bytes cannot enter the raw parser", "Invalid");

        var largeGeometry = new NtfsMftGeometry(512, 65536, 65536, 16 * 1024 * 1024, 256, 10, 65536);
        var large = Mapped(Enlarge(Record0(allocated: 4L * 65536, size: 65536, initialized: 65536), 65536), largeGeometry,
            "exact 64 KiB source endpoint is accepted");
        Rejected(() => large.PlanRecord(NtfsRecordDecoder.MaximumRecordNumber), "slot multiplication overflow is a controlled failure");

        // DATA starts at 496: its instance ID lies exactly at the first protected tail.
        byte[] crossing = Record0(crossingInstance: true);
        var crossingRestored = Restore(crossing);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(crossingRestored.AsSpan(496)) == 0x80 &&
            BinaryPrimitives.ReadUInt16LittleEndian(crossingRestored.AsSpan(510)) == 3,
            "regression fixture puts the DATA instance ID exactly at byte 510");
        Mapped(crossing, geometry, "properly protected crossing instance remains strict raw success");
        var logical = (byte[])crossingRestored.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(logical.AsSpan(510), 4);
        Check(NtfsRecordDecoder.DecodeKernelReturned(logical, 512, 0, 4096, 3, 1).Code == "FixupMismatch",
            "independent logical ID cannot bypass the unchanged kernel USA contract");
        Failed(logical, geometry, "the MFT map does not repair mismatched kernel logic into raw evidence", "Invalid");
        byte[] logicalBefore = (byte[])logical.Clone();
        var mechanical = Restore(logical);
        Failed(mechanical, geometry, "mechanical replacement is still not a protected raw source", "Invalid");
        Check(logical.SequenceEqual(logicalBefore), "examining a replacement copy never alters the original strict failure");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool parseCanceled = false, planCanceled = false, rangeCanceled = false;
        try { NtfsMftMap.Parse(original, geometry, cancellation.Token); } catch (OperationCanceledException) { parseCanceled = true; }
        try { map.PlanRecord(0, cancellation.Token); } catch (OperationCanceledException) { planCanceled = true; }
        try { map.PlanRange(0, 1024, cancellation.Token); } catch (OperationCanceledException) { rangeCanceled = true; }
        Check(parseCanceled && planCanceled && rangeCanceled && original.SequenceEqual(Record0()), "cancellation throws without publishing partial state or changing source");
        return passed;
    }

    internal static byte[] Record0(byte[]? runs = null, ulong highVcn = 3, long allocated = 16384,
        long size = 4096, long initialized = 4096, string name = "$MFT", byte nameSpace = 1,
        byte[]? extra = null, ulong baseReference = 0, ushort flags = 0, ulong lowVcn = 0, bool crossingInstance = false)
    {
        byte[] data = Data(runs ?? [0x11, 4, 10, 0], highVcn, allocated, size, initialized, flags, lowVcn);
        var attributes = new List<byte[]> { NtfsRecordFixtures.Name(name, nameSpace) };
        if (crossingInstance)
        {
            attributes.Add(NtfsRecordFixtures.Resident(0x10, new byte[48]));
            attributes.Add(NtfsRecordFixtures.Resident(0x40, new byte[240]));
        }
        attributes.Add(data);
        if (extra != null) attributes.Add(extra);
        return NtfsRecordFixtures.Record(attributes, baseReference: baseReference, addStandard: !crossingInstance);
    }

    private static byte[] Data(byte[] runs, ulong highVcn, long allocated, long size, long initialized, ushort flags, ulong lowVcn)
    {
        int header = (flags & 0x8001) != 0 ? 72 : 64;
        var attribute = new byte[NtfsRecordFixtures.Align(header + runs.Length)];
        NtfsRecordFixtures.U32(attribute, 0, 0x80); NtfsRecordFixtures.U32(attribute, 4, (uint)attribute.Length); attribute[8] = 1;
        NtfsRecordFixtures.U16(attribute, 12, flags); NtfsRecordFixtures.U64(attribute, 16, lowVcn); NtfsRecordFixtures.U64(attribute, 24, highVcn);
        NtfsRecordFixtures.U16(attribute, 32, (ushort)header); NtfsRecordFixtures.U16(attribute, 34, (flags & 1) != 0 ? (ushort)4 : (ushort)0);
        NtfsRecordFixtures.U64(attribute, 40, unchecked((ulong)allocated)); NtfsRecordFixtures.U64(attribute, 48, unchecked((ulong)size));
        NtfsRecordFixtures.U64(attribute, 56, unchecked((ulong)initialized));
        if (header == 72) NtfsRecordFixtures.U64(attribute, 64, unchecked((ulong)allocated));
        runs.CopyTo(attribute, header);
        return attribute;
    }

    private static byte[] WideInitialRun(long firstLcn, byte clusters, byte[]? followingRun = null)
    {
        // Deliberately use the full signed 64-bit initial LCN encoding; following
        // negative encodings above are independent literal regression vectors.
        byte[] first = new byte[10]; first[0] = 0x81; first[1] = clusters;
        BinaryPrimitives.WriteInt64LittleEndian(first.AsSpan(2), firstLcn);
        return first.Concat(followingRun ?? []).Append((byte)0).ToArray();
    }

    private static byte[] Restore(byte[] source)
    {
        byte[] copy = (byte[])source.Clone();
        int usa = BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(4)), count = BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(6));
        for (int i = 1; i < count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(i * 512 - 2), BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(usa + i * 2)));
        return copy;
    }

    private static byte[] Enlarge(byte[] source, int length)
    {
        byte[] restored = Restore(source), result = new byte[length];
        restored.AsSpan(0, 48).CopyTo(result);
        int oldFirst = BinaryPrimitives.ReadUInt16LittleEndian(restored.AsSpan(20));
        int oldUsed = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(restored.AsSpan(24)));
        int count = length / 512 + 1, first = NtfsRecordFixtures.Align(48 + count * 2);
        restored.AsSpan(oldFirst, oldUsed - oldFirst).CopyTo(result.AsSpan(first));
        NtfsRecordFixtures.U16(result, 4, 48); NtfsRecordFixtures.U16(result, 6, (ushort)count); NtfsRecordFixtures.U16(result, 20, (ushort)first);
        NtfsRecordFixtures.U32(result, 24, (uint)(first + oldUsed - oldFirst)); NtfsRecordFixtures.U32(result, 28, (uint)length);
        NtfsRecordFixtures.U16(result, 48, 0xa55a);
        for (int i = 1; i < count; i++)
        {
            ushort tail = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(i * 512 - 2));
            NtfsRecordFixtures.U16(result, 48 + i * 2, tail); NtfsRecordFixtures.U16(result, i * 512 - 2, 0xa55a);
        }
        return result;
    }
}
