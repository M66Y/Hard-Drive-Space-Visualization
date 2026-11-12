using System.Buffers.Binary;
using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// Pure synthetic protected records and address assertions. No Native, file or
// volume operations; a successful plan remains Synthetic until a real owner
// privately binds and executes it under its separate guards and reading lease.
internal static class NtfsMftBitmapPrefixTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("BITMAP prefix check failed: " + name);
            passed++; Console.WriteLine("PASS BITMAP prefix " + name);
        }
        var geometry = Geometry();
        NtfsMftBitmapPrefixResult Parse(byte[] source, NtfsMftGeometry? facts = null, int slots = 64) =>
            NtfsMftBitmapPrefix.Parse(source, facts ?? geometry, slots);
        void Rejected(byte[] source, string name, string? status = null, string? code = null)
        {
            byte[] before = source.ToArray(); var result = Parse(source);
            Check(result.Status is "Invalid" or "Deferred" or "Missing" && (status == null || result.Status == status) &&
                (code == null || result.Code == code) && result.ReadPlan.Count == 0 && result.ReadBytes == 0 && result.CopyPrefix().Length == 0,
                name);
            Check(result.EvidenceKind == "Synthetic" && result.Code.All(char.IsAsciiLetterOrDigit) && source.SequenceEqual(before),
                "rejected input publishes no address or arbitrary message and leaves caller bytes unchanged");
        }
        var residentBytes = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
        byte[] resident = Seed([NtfsRecordFixtures.Resident(0xb0, residentBytes)]);
        byte[] original = resident.ToArray(); var direct = Parse(resident);
        Check(direct is { Status: "Resident", Form: "Resident", LogicalPrefixBytes: 8, LogicalBytes: 512, InitializedBytes: 512,
            AllocatedBytes: null, ReadBytes: 0 } && direct.CopyPrefix().SequenceEqual(residentBytes.Take(8)) && direct.ReadPlan.Count == 0,
            "resident extraction keeps only the needed logical prefix and schedules no I O");
        Check(resident.SequenceEqual(original) && JsonSerializer.Serialize(direct) == "{}",
            "resident extraction restores private USA bytes and does not serialize payload or source details");
        byte[] firstCopy = direct.CopyPrefix(); firstCopy[0] ^= 1; resident.AsSpan().Clear();
        Check(direct.CopyPrefix().SequenceEqual(residentBytes.Take(8)), "published resident prefix owns its bytes and every returned copy is independent");
        byte[] maximumResident = Seed([NtfsRecordFixtures.Resident(0xb0, residentBytes)], 4096);
        var residentFull = Parse(maximumResident, Geometry(4096), 4096);
        Check(residentFull.Status == "Resident" && residentFull.LogicalPrefixBytes == 512 && residentFull.CopyPrefix().SequenceEqual(residentBytes) &&
            BinaryPrimitives.ReadUInt16LittleEndian(maximumResident.AsSpan(510)) == 0xa55a,
            "the maximum resident prefix restores the protected sector tail without changing the raw source");
        Rejected(Seed([NtfsRecordFixtures.Resident(0xb0, new byte[513])]), "resident payload budget is not silently raised", "Deferred", "BitmapPrefixPayloadBudget");
        Rejected(Seed([NtfsRecordFixtures.Resident(0xb0, new byte[7])]), "resident short coverage never assumes implicit zero bits", "Deferred", "BitmapPrefixCoverage");
        Rejected(Seed(), "missing bitmap is not treated as known zero allocation", "Missing", "BitmapPrefixMissing");
        Rejected(Seed([NtfsRecordFixtures.Resident(0xb0, new byte[8], "named")]), "named bitmap cannot substitute for the unnamed stream", "Deferred", "BitmapPrefixNamed");
        Rejected(Seed([NtfsRecordFixtures.Resident(0xb0, new byte[8]), NtfsRecordFixtures.Resident(0xb0, new byte[8])]),
            "multiple bitmaps cannot publish the first usable candidate", "Deferred", "BitmapPrefixMultiple");
        foreach (ushort flags in new ushort[] { 1, 0x8000, 0x4000 })
            Rejected(Seed([Bitmap([0x11, 1, 100, 0], 0, 4096, 8, 8, flags)]), "special bitmap flags stay deferred without addresses", "Deferred");
        Rejected(Seed([NtfsRecordFixtures.Resident(0x20, [])]), "attribute extensions do not fall back to local bitmap candidates", "Deferred");

        byte[] simple = Seed([Bitmap([0x11, 1, 100, 0], 0, 4096, 8, 8)]);
        byte[] simpleBefore = simple.ToArray(); var planned = Parse(simple);
        Check(planned is { Status: "Planned", Form: "Nonresident", LogicalPrefixBytes: 8, LogicalBytes: 8, InitializedBytes: 8,
            AllocatedBytes: 4096, ReadBytes: 512 } && planned.ReadPlan.SequenceEqual(new[] { new NtfsRawSlice(409600, 512, 0) }) &&
            planned.CopyPrefix().Length == 0,
            "a small initialized stream produces exactly one aligned sector while interpreting only needed bytes");
        Check(simple.SequenceEqual(simpleBefore) && planned.EvidenceKind == "Synthetic" && JsonSerializer.Serialize(planned) == "{}",
            "validated physical plan remains private Synthetic metadata and preserves input");
        bool immutable = false;
        try { ((IList<NtfsRawSlice>)planned.ReadPlan).Clear(); } catch (NotSupportedException) { immutable = true; }
        Check(immutable, "validated address slices cannot be edited after parsing");
        var fragmented = Parse(Seed([Bitmap([0x11, 1, 100, 0x11, 1, 246, 0], 1, 8192, 6000, 6000)]));
        Check(fragmented.Status == "Planned" && fragmented.ReadPlan.Single().VolumeOffset == 409600 && fragmented.ReadBytes == 512,
            "valid negative LCN fragmentation is fully checked without reading later runs");
        foreach (var vector in new (long First, long Delta, int Width)[] { (1000, -300, 2), (2000000, -100000, 4), (5000000000, -3000000000, 8) })
        {
            byte[] runs = Run(1, vector.First, 8).Concat(Run(1, vector.Delta, vector.Width)).Append((byte)0).ToArray();
            var far = geometry with { VolumeBytes = 32768000000000, TotalClusters = 8000000000 };
            var result = Parse(Seed([Bitmap(runs, 1, 8192, 6000, 6000)]), far);
            Check(result.Status == "Planned" && result.ReadPlan.Single().VolumeOffset == vector.First * 4096 && result.ReadPlan.Single().Length == 512,
                "signed two four and eight byte deltas retain validated sixty-four bit physical addresses");
        }
        var large = Parse(Seed([Bitmap(Run(256, 2000, 8, 2).Append((byte)0).ToArray(), 255, 1048576, 1048576, 1048576)], 4096), Geometry(4096), 4096);
        Check(large is { Status: "Planned", LogicalPrefixBytes: 512, LogicalBytes: 1048576, AllocatedBytes: 1048576, ReadBytes: 512 } &&
            large.ReadPlan.Single() == new NtfsRawSlice(8192000, 512, 0),
            "one MiB metadata is validated while the maximum slot range still requests only a five-hundred-twelve byte prefix");
        var many = Parse(Seed([Bitmap(CompactRuns(128), 127, 128L * 4096, 128L * 4096, 128L * 4096)]));
        Check(many.Status == "Planned" && many.ReadPlan.Single().Length == 512, "the full one-hundred-twenty-eight run ceiling is validated before producing a prefix plan");
        Rejected(Seed([Bitmap(CompactRuns(129), 128, 129L * 4096, 129L * 4096, 129L * 4096)]),
            "excess run count cannot become a truncated physical map", "Deferred", "BitmapPrefixRunBudget");
        Rejected(Seed([Bitmap([0x11, 2, 100, 0x11, 2, 1, 0], 3, 16384, 8192, 8192)]),
            "physical overlap between bitmap extents is rejected", "Invalid", "BitmapPrefixPhysicalOverlap");
        Rejected(Seed([Bitmap([0x11, 1, 10, 0], 0, 4096, 8, 8)]),
            "bitmap extents cannot overlap the mapped MFT DATA stream", "Invalid", "BitmapPrefixMftOverlap");
        Rejected(Seed([Bitmap([0x01, 1, 0], 0, 4096, 8, 8)]),
            "implicit sparse bitmap runs stay deferred even when no sparse attribute flag was set", "Deferred", "BitmapPrefixSparse");
        Rejected(Seed([Bitmap([0x11, 1, 0, 0], 0, 4096, 8, 8)]),
            "an explicit zero LCN cannot turn an unallocated mapping into a readable boot-sector address", "Deferred", "BitmapPrefixSparse");
        Rejected(Seed([Bitmap([0x21, 1, 0xff, 0xff, 0], 0, 4096, 8, 8)]), "negative physical addresses are rejected by strict validation", "Invalid");
        Rejected(Seed([Bitmap(Run(1, 65536, 8).Append((byte)0).ToArray(), 0, 4096, 8, 8)]),
            "physical extents outside the bound volume cannot become read plans", "Invalid", "BitmapPrefixPhysicalBounds");
        Rejected(Seed([Bitmap([0x11, 1, 100, 0], 1, 8192, 8, 8)]), "VCN undercoverage remains strict rejection", "Invalid");
        Rejected(Seed([Bitmap([0x11, 2, 100, 0], 0, 4096, 8, 8)]), "VCN overcoverage remains strict rejection", "Invalid");
        Rejected(Seed([Bitmap([0x11, 1, 100, 0], 0, 8192, 8, 8)]),
            "allocated length must match the complete VCN map", "Invalid", "BitmapPrefixVcn");
        Rejected(Seed([Bitmap([0x11, 1, 100, 0], 1, 4096, 8, 8, low: 1)]), "continuation extents never stand in for the base bitmap", "Deferred");
        foreach (var sizes in new (long Logical, long Initialized, long Allocated)[] {
            (7,7,4096), (8,7,4096), (8,9,4096), (-1,0,4096), (8,-1,4096), (8,8,4097), (5000,5000,4096), (8,8,0) })
            Rejected(Seed([Bitmap([0x11, 1, 100, 0], 0, sizes.Allocated, sizes.Logical, sizes.Initialized)]),
                "logical EOF initialization allocation and alignment must all cover the needed prefix", "Invalid");
        Rejected(Seed([Bitmap(Run(512, 2000, 8, 2).Append((byte)0).ToArray(), 511, 2097152, 1048577, 1048577)]),
            "larger metadata cannot expand the fixed one MiB parsing budget", "Deferred", "BitmapPrefixMetadataBudget");
        var badMapping = Bitmap([0x11, 1, 100, 0], 0, 4096, 8, 8); NtfsRecordFixtures.U16(badMapping, 32, ushort.MaxValue);
        Rejected(Seed([badMapping]), "mapping offsets outside the attribute fail before address extraction", "Invalid");
        var badResident = NtfsRecordFixtures.Resident(0xb0, new byte[8]); NtfsRecordFixtures.U16(badResident, 20, 1);
        Rejected(Seed([badResident]), "resident offsets outside the attribute fail before prefix copying", "Invalid");
        byte[] damaged = simpleBefore.ToArray(); damaged[510] ^= 1;
        Rejected(damaged, "strict USA damage cannot be repaired into a prefix plan", "Invalid", "BitmapPrefixSeedInvalid");
        byte[] logicalCopy = Restore(simpleBefore);
        Rejected(logicalCopy, "already restored logical bytes cannot enter the raw prefix parser", "Invalid", "BitmapPrefixSeedInvalid");
        foreach (int length in new[] { 0, 1023, 1025, 65536 }) Rejected(new byte[length], "input is exactly one bounded record", "Invalid", "BitmapPrefixInput");
        foreach (int slots in new[] { -1, 0, 4097, int.MaxValue })
        {
            var result = NtfsMftBitmapPrefix.Parse(simpleBefore, geometry, slots);
            Check(result.Status == "Invalid" && result.Code == "BitmapPrefixSlotBudget" && result.ReadPlan.Count == 0,
                "slot budget rejects before creating a physical plan");
        }
        Check(NtfsMftBitmapPrefix.Parse(simpleBefore, geometry, 63).Code == "BitmapPrefixSlotBudget",
            "the expected range binds exactly to MFT valid length");
        Check(NtfsMftBitmapPrefix.Parse(simpleBefore, geometry with { ClusterBytes = 512 }, 64).Code == "BitmapPrefixGeometry",
            "other geometry cannot silently choose another prefix alignment");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool canceled = false;
        try { NtfsMftBitmapPrefix.Parse(simpleBefore, geometry, 64, cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
        Check(canceled && simpleBefore.SequenceEqual(Seed([Bitmap([0x11, 1, 100, 0], 0, 4096, 8, 8)])),
            "cancellation produces no partial plan and never changes caller bytes");
        return passed;
    }

    private static NtfsMftGeometry Geometry(int slots = 64) => new(512, 4096, 1024, 256L * 1024 * 1024, 65536, 10, (long)slots * 1024);
    private static byte[] Seed(byte[][]? attributes = null, int slots = 64)
    {
        int clusters = (slots + 3) / 4;
        var items = new List<byte[]> { NtfsRecordFixtures.Name("$MFT"),
            Attribute(0x80, Run(clusters, 10, 8, 2).Append((byte)0).ToArray(), (ulong)(clusters - 1), clusters * 4096L, slots * 1024L, slots * 1024L) };
        if (attributes != null) items.AddRange(attributes);
        return NtfsRecordFixtures.Record(items);
    }
    private static byte[] Bitmap(byte[] runs, ulong high, long allocated, long logical, long initialized, ushort flags = 0, ulong low = 0) =>
        Attribute(0xb0, runs, high, allocated, logical, initialized, flags, low);
    private static byte[] Attribute(uint type, byte[] runs, ulong high, long allocated, long logical, long initialized, ushort flags = 0, ulong low = 0)
    {
        int header = (flags & 0x8001) != 0 ? 72 : 64;
        var bytes = new byte[NtfsRecordFixtures.Align(header + runs.Length)];
        NtfsRecordFixtures.U32(bytes, 0, type); NtfsRecordFixtures.U32(bytes, 4, (uint)bytes.Length); bytes[8] = 1;
        NtfsRecordFixtures.U16(bytes, 12, flags); NtfsRecordFixtures.U64(bytes, 16, low); NtfsRecordFixtures.U64(bytes, 24, high);
        NtfsRecordFixtures.U16(bytes, 32, (ushort)header); NtfsRecordFixtures.U16(bytes, 34, (flags & 1) != 0 ? (ushort)4 : (ushort)0);
        NtfsRecordFixtures.U64(bytes, 40, unchecked((ulong)allocated)); NtfsRecordFixtures.U64(bytes, 48, unchecked((ulong)logical));
        NtfsRecordFixtures.U64(bytes, 56, unchecked((ulong)initialized));
        if (header == 72) NtfsRecordFixtures.U64(bytes, 64, unchecked((ulong)allocated));
        runs.CopyTo(bytes, header); return bytes;
    }
    private static byte[] Run(long count, long delta, int width, int lengthWidth = 1)
    {
        var bytes = new byte[1 + lengthWidth + width]; bytes[0] = (byte)(width * 16 + lengthWidth);
        for (int i = 0; i < lengthWidth; i++) bytes[1 + i] = (byte)((ulong)count >> (8 * i));
        for (int i = 0; i < width; i++) bytes[1 + lengthWidth + i] = (byte)((ulong)delta >> (8 * i));
        return bytes;
    }
    private static byte[] CompactRuns(int count)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < count; i++) bytes.AddRange(new byte[] { 0x11, 1, (byte)(i == 0 ? 100 : 2) });
        bytes.Add(0); return bytes.ToArray();
    }
    private static byte[] Restore(byte[] source)
    {
        var result = source.ToArray(); int usa = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(4));
        for (int i = 1; i < 3; i++) NtfsRecordFixtures.U16(result, i * 512 - 2, BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(usa + i * 2)));
        return result;
    }
}
