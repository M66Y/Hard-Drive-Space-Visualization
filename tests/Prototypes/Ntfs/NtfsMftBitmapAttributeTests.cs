using System.Buffers.Binary;
using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// Synthetic protected record0 bytes only. No file/volume/native/source use.
internal static class NtfsMftBitmapAttributeTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("BITMAP attribute check failed: " + name);
            passed++; Console.WriteLine("PASS BITMAP attribute " + name);
        }
        NtfsMftBitmapAttributeResult Parse(byte[] record, int slots = 4) => NtfsMftBitmapAttribute.Parse(record, Geometry(slots), slots);
        void Rejected(byte[] record, string status, string code, string name, int slots = 4)
        {
            var result = Parse(record, slots);
            Check(result.Status == status && result.Code == code && result.EvidenceKind == "Synthetic" &&
                result.CopyPayload().Length == 0 && result.CoveredSlots == 0 &&
                result.Code.All(char.IsAsciiLetterOrDigit), name);
        }
        byte[] payload = [0xa5, 0x53];
        byte[] source = Seed(NtfsRecordFixtures.Resident(0xb0, payload));
        byte[] before = source.ToArray();
        var complete = Parse(source);
        Check(complete is { Revision: "synthetic-mft-bitmap-attribute-v1", EvidenceKind: "Synthetic", Status: "ResidentComplete",
            Code: "BitmapResidentComplete", ExpectedSlots: 4, CoveredSlots: 4, PayloadBytes: 2, NonresidentMetadata: null } &&
            complete.CopyPayload().SequenceEqual(payload), "unique ordinary unnamed resident bitmap yields only a copied synthetic payload");
        Check(source.SequenceEqual(before), "strict seed parsing and USA restoration never modify caller record bytes");
        byte[] returned = complete.CopyPayload(); returned[0] ^= 0xff; source.AsSpan().Clear();
        Check(complete.CopyPayload().SequenceEqual(payload), "caller record and returned payload changes cannot alter the owned payload");
        Check(JsonSerializer.Serialize(complete) == "{}", "default serialization exposes neither resident payload nor record or source details");
        var partial = Parse(Seed(NtfsRecordFixtures.Resident(0xb0, new byte[] { 0x81 }), 16), 16);
        Check(partial is { Status: "ResidentPartial", Code: "BitmapResidentPartial", ExpectedSlots: 16, CoveredSlots: 8, PayloadBytes: 1 },
            "a shorter valid payload reports partial coverage rather than implied zero remainder");
        var empty = Parse(Seed(NtfsRecordFixtures.Resident(0xb0, [])));
        Check(empty is { Status: "ResidentPartial", CoveredSlots: 0, PayloadBytes: 0 },
            "an empty resident value supplies no allocation coverage");
        byte[] maximumPayload = Enumerable.Range(0, 512).Select(index => (byte)index).ToArray();
        byte[] crossing = Seed(NtfsRecordFixtures.Resident(0xb0, maximumPayload), 4096);
        byte[] crossingBefore = crossing.ToArray();
        var maximum = Parse(crossing, 4096);
        Check(maximum is { Status: "ResidentComplete", CoveredSlots: 4096, PayloadBytes: 512 } &&
            maximum.CopyPayload().SequenceEqual(maximumPayload) && crossing.SequenceEqual(crossingBefore),
            "exact record slot and payload ceilings preserve bytes across a protected sector tail");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(crossing.AsSpan(510)) == 0xa55a &&
            maximum.CopyPayload().SequenceEqual(maximumPayload), "resident bytes at the USA tail are restored solely in a private copy");
        Rejected(Seed(NtfsRecordFixtures.Resident(0xb0, new byte[513])), "Deferred", "BitmapPayloadBudget",
            "oversized but well-formed resident payload stays deferred rather than treated as corruption");
        Rejected(Seed(), "Missing", "BitmapMissing", "missing bitmap is explicit and never synthesized as zero payload");
        Rejected(Seed(NtfsRecordFixtures.Resident(0xb0, [1], "named")), "Deferred", "BitmapNamed",
            "named bitmaps do not silently substitute for the unnamed MFT bitmap");
        byte[] ordinaryData = NtfsRecordFixtures.Nonresident(4096, 8192);
        byte[] multiple = NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("$MFT"), ordinaryData,
            NtfsRecordFixtures.Resident(0xb0, [1]), NtfsRecordFixtures.Resident(0xb0, [2])]);
        Rejected(multiple, "Deferred", "BitmapMultiple", "multiple bitmap attributes cannot publish the first candidate");
        foreach (ushort flags in new ushort[] { 1, 0x8000, 0x4000 })
            Rejected(Seed(NtfsRecordFixtures.Resident(0xb0, [1], flags: flags)), "Deferred", "BitmapFlags",
                "compressed sparse or encrypted bitmap flags remain outside this extraction scope");
        Rejected(Seed(NtfsRecordFixtures.Resident(0x20, [])), "Deferred", "BitmapSeedUnsupported",
            "attribute extensions are deferred by the unchanged strict seed contract");
        byte[] bitmapNonresident = NonresidentBitmap(8, 4096, 8);
        var deferred = Parse(Seed(bitmapNonresident));
        Check(deferred is { Status: "Deferred", Code: "BitmapNonresident", CoveredSlots: 0, PayloadBytes: 0 } &&
            deferred.NonresidentMetadata == new NtfsMftBitmapNonresidentMetadata(8, 8, 4096) && deferred.CopyPayload().Length == 0,
            "a nonresident form exposes bounded lengths but no payload or readable run address");
        string[] metadataFields = typeof(NtfsMftBitmapNonresidentMetadata).GetProperties().Select(property => property.Name).ToArray();
        Check(metadataFields.OrderBy(name => name).SequenceEqual(new[] { "AllocatedBytes", "InitializedBytes", "LogicalBytes" }),
            "deferred metadata has no offsets LCN run list or authority fields");
        var uninitialized = Parse(Seed(NonresidentBitmap(8, 4096, 0)));
        Check(uninitialized.Status == "Deferred" && uninitialized.NonresidentMetadata?.InitializedBytes == 0 && uninitialized.CoveredSlots == 0,
            "uninitialized nonresident bytes are not exposed as initialized zero bitmap data");
        Rejected(Seed(NonresidentBitmap(513, 4096, 513)), "Deferred", "BitmapPayloadBudget",
            "nonresident logical size beyond the payload budget is deferred without a descriptor");
        var compressionUnit = NonresidentBitmap(8, 4096, 8); NtfsRecordFixtures.U16(compressionUnit, 34, 1);
        Rejected(Seed(compressionUnit), "Deferred", "BitmapFlags", "a nonzero compression unit cannot imply ordinary bitmap support");
        foreach (var lengths in new (long Logical, long Allocated, long Initialized)[] {
            (-1, 4096, 0), (8, 4096, -1), (8, 4096, 9), (8, 7, 8), (8, 4097, 8), (8, 512 * 1024 * 1024, 8) })
            Rejected(Seed(NonresidentBitmap(lengths.Logical, lengths.Allocated, lengths.Initialized)), "Invalid", "BitmapNonresidentBounds",
                "invalid nonresident lengths cannot become a usable deferred descriptor");
        var continuation = NonresidentBitmap(8, 4096, 8); NtfsRecordFixtures.U64(continuation, 16, 1); NtfsRecordFixtures.U64(continuation, 24, 1);
        Rejected(Seed(continuation), "Deferred", "BitmapExtent", "continuation extents cannot impersonate a complete bitmap attribute");
        var malformed = NtfsRecordFixtures.Resident(0xb0, [1]); NtfsRecordFixtures.U16(malformed, 20, 1);
        Rejected(Seed(malformed), "Invalid", "BitmapSeedInvalid", "malformed resident offsets stay strict rejection before locating payload");
        var hugeLength = NtfsRecordFixtures.Resident(0xb0, [1]); NtfsRecordFixtures.U32(hugeLength, 16, uint.MaxValue);
        Rejected(Seed(hugeLength), "Invalid", "BitmapSeedInvalid", "overflowing resident value lengths never reach a copy");
        byte[] damaged = Seed(NtfsRecordFixtures.Resident(0xb0, [1])); damaged[510] ^= 1;
        Rejected(damaged, "Invalid", "BitmapSeedInvalid", "USA damage has no restored-byte or kernel fallback");
        byte[] restored = Restore(Seed(NtfsRecordFixtures.Resident(0xb0, [1])));
        Rejected(restored, "Invalid", "BitmapSeedInvalid", "already restored logical bytes cannot enter the strict raw locator");
        foreach (int length in new[] { 0, 1023, 1025, 65536 })
            Rejected(new byte[length], "Invalid", "BitmapInput", "non-exact bounded record input is rejected before copying");
        foreach (int slots in new[] { 0, -1, 4097, int.MaxValue })
        {
            var invalid = NtfsMftBitmapAttribute.Parse(before, Geometry(4), slots);
            Check(invalid.Status == "Invalid" && invalid.Code == "BitmapSlotBudget" && invalid.PayloadBytes == 0,
                "invalid expected slot budgets never publish a resident candidate");
        }
        var wrongRange = NtfsMftBitmapAttribute.Parse(before, Geometry(4), 8);
        Check(wrongRange.Status == "Invalid" && wrongRange.Code == "BitmapSlotBudget", "expected slots must bind the declared native valid range");
        var wrongGeometry = NtfsMftBitmapAttribute.Parse(before, Geometry(4) with { ClusterBytes = 512 }, 4);
        Check(wrongGeometry.Status == "Invalid" && wrongGeometry.Code == "BitmapGeometry", "other geometry is not silently accepted by this fixture-only locator");
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); bool cancellationSeen = false;
        try { NtfsMftBitmapAttribute.Parse(before, Geometry(4), 4, canceled.Token); } catch (OperationCanceledException) { cancellationSeen = true; }
        Check(cancellationSeen && before.SequenceEqual(Seed(NtfsRecordFixtures.Resident(0xb0, payload))),
            "cancellation throws without publishing a partial result or changing input");
        return passed;
    }

    private static NtfsMftGeometry Geometry(int slots) => new(512, 4096, 1024, 256L * 1024 * 1024, 65536, 10, (long)slots * 1024);
    private static byte[] Seed(byte[]? extra = null, int slots = 4)
    {
        int clusters = Math.Max(1, (slots + 3) / 4);
        return NtfsMftMapTests.Record0([0x12, (byte)clusters, (byte)(clusters >> 8), 10, 0], (ulong)(clusters - 1),
            (long)clusters * 4096, (long)slots * 1024, (long)slots * 1024, extra: extra);
    }
    private static byte[] NonresidentBitmap(long logical, long allocated, long initialized)
    {
        var attribute = NtfsRecordFixtures.Nonresident(logical, allocated); NtfsRecordFixtures.U32(attribute, 0, 0xb0);
        NtfsRecordFixtures.U64(attribute, 24, 0); attribute[65] = 1;
        NtfsRecordFixtures.U64(attribute, 56, unchecked((ulong)initialized)); return attribute;
    }
    private static byte[] Restore(byte[] source)
    {
        var result = source.ToArray(); int usa = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(4));
        for (int i = 1; i < 3; i++)
            NtfsRecordFixtures.U16(result, i * 512 - 2, BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(usa + i * 2)));
        return result;
    }
}
