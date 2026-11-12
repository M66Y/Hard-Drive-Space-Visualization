using System.Buffers.Binary;
using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// Bounded synthetic record bytes only. No bitmap extraction from a real file,
// volume handle, native operation or readable nonresident address is involved.
internal static class NtfsMftBitmapAttributeAdversarialTests
{
    private static NtfsMftGeometry Geometry(int slots) => new(512, 4096, 1024,
        256L * 1024 * 1024, 65536, 10, slots * 1024L);

    private static byte[] Seed(int slots, params byte[][] extras)
    {
        long valid = slots * 1024L;
        int clusters = checked((int)((valid + 4095) / 4096));
        byte[] data = new byte[72];
        NtfsRecordFixtures.U32(data, 0, 0x80); NtfsRecordFixtures.U32(data, 4, 72); data[8] = 1;
        NtfsRecordFixtures.U64(data, 24, (ulong)(clusters - 1)); NtfsRecordFixtures.U16(data, 32, 64);
        NtfsRecordFixtures.U64(data, 40, (ulong)clusters * 4096); NtfsRecordFixtures.U64(data, 48, (ulong)valid);
        NtfsRecordFixtures.U64(data, 56, (ulong)valid);
        data[64] = 0x12; NtfsRecordFixtures.U16(data, 65, (ushort)clusters); data[67] = 10;
        var attributes = new List<byte[]> { NtfsRecordFixtures.Name("$MFT"), data };
        attributes.AddRange(extras);
        return NtfsRecordFixtures.Record(attributes);
    }

    private static byte[] NonresidentBitmap(long logical = 512, long initialized = 512, long allocated = 4096)
    {
        var attribute = new byte[72];
        NtfsRecordFixtures.U32(attribute, 0, 0xb0); NtfsRecordFixtures.U32(attribute, 4, 72); attribute[8] = 1;
        NtfsRecordFixtures.U64(attribute, 24, 0); NtfsRecordFixtures.U16(attribute, 32, 64);
        NtfsRecordFixtures.U64(attribute, 40, unchecked((ulong)allocated)); NtfsRecordFixtures.U64(attribute, 48, unchecked((ulong)logical));
        NtfsRecordFixtures.U64(attribute, 56, unchecked((ulong)initialized));
        attribute[64] = 0x11; attribute[65] = 1; attribute[66] = 10;
        return attribute;
    }

    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("MFT bitmap attribute adversarial check failed: " + name);
            passed++; Console.WriteLine("PASS MFT bitmap attribute adversarial " + name);
        }
        void SafeCode(NtfsMftBitmapAttributeResult result) => Check(result.Revision == "synthetic-mft-bitmap-attribute-v1" &&
            result.EvidenceKind == "Synthetic" && result.Code.Length is > 0 and <= 80 && result.Code.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'),
            "every result stays synthetic with a bounded fixed code rather than arbitrary input text");

        byte[] payload = Enumerable.Range(0, 512).Select(index => (byte)index).ToArray();
        byte[] crossing = Seed(4096, NtfsRecordFixtures.Resident(0xb0, payload)); var before = crossing.ToArray();
        Check(BinaryPrimitives.ReadUInt16LittleEndian(crossing.AsSpan(510)) == 0xa55a &&
            BinaryPrimitives.ReadUInt16LittleEndian(crossing.AsSpan(50)) == 0xfffe,
            "independent fixture places bitmap payload bytes across the first protected sector tail");
        var result = NtfsMftBitmapAttribute.Parse(crossing, Geometry(4096), 4096);
        Check(result.Status == "ResidentComplete" && result.Code == "BitmapResidentComplete" && result.ExpectedSlots == 4096 &&
            result.CoveredSlots == 4096 && result.PayloadBytes == 512 && result.CopyPayload().SequenceEqual(payload),
            "private USA restoration returns all original payload bytes including the protected tail word");
        Check(crossing.SequenceEqual(before), "strict seed validation and payload extraction never rewrite caller record bytes");
        byte[] firstCopy = result.CopyPayload(); firstCopy[0] ^= 0xff; crossing[256] ^= 0xff;
        Check(result.CopyPayload().SequenceEqual(payload) && !ReferenceEquals(firstCopy, result.CopyPayload()),
            "payload copies remain independent of both caller raw bytes and earlier returned arrays");
        SafeCode(result);
        string json = JsonSerializer.Serialize(result);
        Check(!json.Contains(Convert.ToBase64String(payload), StringComparison.Ordinal) &&
            !result.GetType().GetProperties().Any(property => property.Name is "Payload" or "RawRecord" or "RawBytes" or "Runs" or "VolumeOffset"),
            "default result serialization exposes no bitmap body raw record or readable addresses");

        foreach (string shape in new[] { "Damaged", "FirstRestored", "AllRestored" })
        {
            byte[] raw = before.ToArray();
            if (shape == "Damaged") raw[510] ^= 1;
            else
            {
                NtfsRecordFixtures.U16(raw, 510, BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(50)));
                if (shape == "AllRestored") NtfsRecordFixtures.U16(raw, 1022, BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(52)));
            }
            var snapshot = raw.ToArray();
            var rejected = NtfsMftBitmapAttribute.Parse(raw, Geometry(4096), 4096);
            Check(rejected.Status == "Invalid" && rejected.Code == "BitmapSeedInvalid" && rejected.CopyPayload().Length == 0 && raw.SequenceEqual(snapshot),
                "damaged mixed-restored and fully restored tails cannot enter the strict raw-record locator");
            SafeCode(rejected);
        }
        foreach ((int slots, int length, string status, int covered) in new[] {
            (1,0,"ResidentPartial",0), (9,1,"ResidentPartial",8), (9,2,"ResidentComplete",9), (4096,511,"ResidentPartial",4088),
            (4096,512,"ResidentComplete",4096), (1,512,"ResidentComplete",1) })
        {
            byte[] body = Enumerable.Repeat((byte)0x5c, length).ToArray();
            byte[] raw = Seed(slots, NtfsRecordFixtures.Resident(0xb0, body));
            var parsed = NtfsMftBitmapAttribute.Parse(raw, Geometry(slots), slots);
            Check(parsed.Status == status && parsed.ExpectedSlots == slots && parsed.CoveredSlots == covered && parsed.PayloadBytes == length &&
                parsed.CopyPayload().SequenceEqual(body), "resident coverage distinguishes empty partial exact and extra-capacity bodies without zero substitution");
        }
        var absent = NtfsMftBitmapAttribute.Parse(Seed(12), Geometry(12), 12);
        Check(absent.Status == "Missing" && absent.Code == "BitmapMissing" && absent.CoveredSlots == 0 && absent.PayloadBytes == 0 && absent.CopyPayload().Length == 0,
            "missing bitmap has no payload or inferred zero allocation coverage");
        SafeCode(absent);
        var tooLarge = NtfsMftBitmapAttribute.Parse(Seed(4096, NtfsRecordFixtures.Resident(0xb0, new byte[513])), Geometry(4096), 4096);
        Check(tooLarge.Code == "BitmapPayloadBudget" && tooLarge.Status == "Deferred" && tooLarge.CopyPayload().Length == 0 && tooLarge.CoveredSlots == 0,
            "resident byte 513 cannot escape the payload ceiling or publish truncated coverage");

        foreach (byte[][] extras in new byte[][][] {
            [NtfsRecordFixtures.Resident(0xb0,new byte[2],"named")],
            [NtfsRecordFixtures.Resident(0xb0,new byte[2]),NtfsRecordFixtures.Resident(0xb0,new byte[2])],
            [NtfsRecordFixtures.Resident(0xb0,new byte[2]),NtfsRecordFixtures.Resident(0xb0,new byte[2],"named")],
            [NtfsRecordFixtures.Resident(0x20,[]),NtfsRecordFixtures.Resident(0xb0,new byte[2])] })
        {
            var deferred = NtfsMftBitmapAttribute.Parse(Seed(12, extras), Geometry(12), 12);
            Check(deferred.Status == "Deferred" && deferred.CopyPayload().Length == 0 && deferred.CoveredSlots == 0,
                "named duplicate mixed or attribute-list forms stay deferred without opportunistic payload selection");
            SafeCode(deferred);
        }
        foreach (ushort flags in new ushort[] { 1, 0x8000, 0x4000, 0x8001 })
        {
            var deferred = NtfsMftBitmapAttribute.Parse(Seed(12, NtfsRecordFixtures.Resident(0xb0, new byte[2], flags: flags)), Geometry(12), 12);
            Check(deferred.Status == "Deferred" && deferred.CopyPayload().Length == 0 && deferred.CoveredSlots == 0,
                "special bitmap flags never leak a resident payload into the supported ordinary path");
            SafeCode(deferred);
        }
        {
            byte[] raw = Seed(12, NtfsRecordFixtures.Resident(0xb0, new byte[2])); NtfsRecordFixtures.U64(raw, 32, NtfsRecordFixtures.Reference(9, 3));
            var deferred = NtfsMftBitmapAttribute.Parse(raw, Geometry(12), 12);
            Check(deferred.Status == "Deferred" && deferred.CopyPayload().Length == 0, "an extension record cannot masquerade as a base record-zero bitmap");
        }
        var nonresident = NtfsMftBitmapAttribute.Parse(Seed(4096, NonresidentBitmap()), Geometry(4096), 4096);
        Check(nonresident.Status == "Deferred" && nonresident.Code == "BitmapNonresident" && nonresident.CoveredSlots == 0 && nonresident.CopyPayload().Length == 0 &&
            nonresident.NonresidentMetadata is { LogicalBytes: 512, InitializedBytes: 512, AllocatedBytes: 4096 },
            "nonresident metadata remains bounded descriptive information without payload coverage or run reading");
        Check(!nonresident.NonresidentMetadata!.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic).Any(property => property.Name.Contains("Offset", StringComparison.Ordinal) ||
            property.Name.Contains("Lcn", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Run", StringComparison.Ordinal)),
            "nonresident result offers no physical offset or readable-run addressing contract");
        SafeCode(nonresident);

        foreach (string malformed in new[] { "ValueOffset", "ValueLength", "AttributeLength", "NameOffset", "MappingOffset" })
        {
            byte[] attribute = malformed == "MappingOffset" ? NonresidentBitmap() :
                NtfsRecordFixtures.Resident(0xb0, new byte[2], malformed == "NameOffset" ? "n" : "");
            if (malformed == "ValueOffset") NtfsRecordFixtures.U16(attribute, 20, 1);
            if (malformed == "ValueLength") NtfsRecordFixtures.U32(attribute, 16, uint.MaxValue);
            if (malformed == "AttributeLength") NtfsRecordFixtures.U32(attribute, 4, 25);
            if (malformed == "NameOffset") NtfsRecordFixtures.U16(attribute, 10, ushort.MaxValue);
            if (malformed == "MappingOffset") NtfsRecordFixtures.U16(attribute, 32, ushort.MaxValue);
            byte[] raw = Seed(12, attribute); var snapshot = raw.ToArray();
            var rejected = NtfsMftBitmapAttribute.Parse(raw, Geometry(12), 12);
            Check(rejected.Status == "Invalid" && rejected.CopyPayload().Length == 0 && rejected.CoveredSlots == 0 && raw.SequenceEqual(snapshot),
                "malformed resident name attribute and mapping bounds reject before any payload publication");
            SafeCode(rejected);
        }
        foreach (int slots in new[] { -1, 0, 4097, int.MaxValue })
        {
            var rejected = NtfsMftBitmapAttribute.Parse(Seed(12), Geometry(12), slots);
            Check(rejected.Status == "Invalid" && rejected.Code == "BitmapSlotBudget" && rejected.CopyPayload().Length == 0,
                "out-of-contract expected slot counts cannot inflate coverage or allocation");
        }
        var wrongExpected = NtfsMftBitmapAttribute.Parse(Seed(12), Geometry(12), 11);
        Check(wrongExpected.Status == "Invalid" && wrongExpected.CopyPayload().Length == 0, "expected slots must bind the seed map valid-data length");
        foreach (byte[] raw in new[] { new byte[1023], new byte[1025], Array.Empty<byte>() })
            Check(NtfsMftBitmapAttribute.Parse(raw, Geometry(12), 12).Status == "Invalid", "input remains exactly one bounded record rather than an arbitrary byte source");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        byte[] cancelRaw = Seed(12, NtfsRecordFixtures.Resident(0xb0, new byte[2])); var cancelBefore = cancelRaw.ToArray();
        NtfsMftBitmapAttributeResult? partial = null; bool canceled = false;
        try { partial = NtfsMftBitmapAttribute.Parse(cancelRaw, Geometry(12), 12, cancellation.Token); }
        catch (OperationCanceledException error) { canceled = error.CancellationToken == cancellation.Token; }
        Check(canceled && partial == null && cancelRaw.SequenceEqual(cancelBefore), "cancellation publishes no partial locator result and preserves the input record");
        return passed;
    }
}
