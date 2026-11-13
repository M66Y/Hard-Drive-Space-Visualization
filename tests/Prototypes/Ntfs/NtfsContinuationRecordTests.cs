namespace DiskAtlas.NtfsPrototype;

// Fixed synthetic records only. No volume, file handle or source authority.
internal static class NtfsContinuationRecordTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Continuation record " + name);
            passed++; Console.WriteLine("PASS continuation record " + name);
        }
        byte[] Extent(ulong first = 4, ulong last = 5, long allocated = 0, long logical = 0, long initialized = 0)
        {
            var attr = NtfsRecordFixtures.Nonresident();
            NtfsRecordFixtures.U64(attr, 16, first); NtfsRecordFixtures.U64(attr, 24, last);
            NtfsRecordFixtures.U64(attr, 40, unchecked((ulong)allocated));
            NtfsRecordFixtures.U64(attr, 48, unchecked((ulong)logical));
            NtfsRecordFixtures.U64(attr, 56, unchecked((ulong)initialized));
            return attr;
        }
        const ulong slot = 77;
        ulong parentReference = NtfsRecordFixtures.Reference(0, 9);
        byte[] Record(byte[] attr, bool extension = true) => extension
            ? NtfsRecordFixtures.Record([attr], sequence: 7, baseReference: parentReference, addStandard: false)
            : NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), attr);
        NtfsDecodeResult Decode(byte[] record) => NtfsRecordDecoder.Decode(record, 512, recordNumber: slot);
        void Unknown(byte[] record, bool extension, string name)
        {
            byte[] original = record.ToArray(); var result = Decode(record);
            Check(result is { Status: NtfsDecodeStatus.Decoded, Record: not null } &&
                result.Record.LogicalBytes == null && result.Record.AllocatedBytes == null &&
                result.Record.LookupReasons.HasFlag(NtfsLookupReason.AttributeList) &&
                (!extension || result.Record.LookupReasons.HasFlag(NtfsLookupReason.ExtensionRecord)), name);
            Check(record.SequenceEqual(original), name + " preserves protected input");
            Check(result.Record!.Reference == NtfsRecordFixtures.Reference(slot, extension ? (ushort)7 : (ushort)3) &&
                result.Record.BaseReference == (extension ? parentReference : 0), name + " preserves complete references for resolver binding");
        }
        void Invalid(byte[] record, string name)
        {
            var result = Decode(record);
            Check(result.Status == NtfsDecodeStatus.Invalid && result.Record == null, name);
        }

        foreach (var sizes in new (long Allocated, long Logical, long Initialized)[]
        {
            (0, 0, 0), (0, long.MaxValue, long.MinValue),
            (long.MinValue, -1, long.MaxValue), (1, 999999, 1000000),
            (long.MaxValue, long.MinValue, -999), (4096, 123, 0)
        })
        {
            var attr = Extent(allocated: sizes.Allocated, logical: sizes.Logical, initialized: sizes.Initialized);
            Unknown(Record(attr), true, "undefined total size fields cannot reject a structurally valid extension");
            Unknown(Record(attr, false), false, "nonzero LowestVCN stays unknown even in a base segment");
        }

        var validBase = Decode(Record(Extent(0, 1, 8192, 6000, 6000), false));
        Check(validBase is { Status: NtfsDecodeStatus.Decoded, Record: { LogicalBytes: 6000, AllocatedBytes: 8192 } },
            "LowestVCN zero retains authoritative complete sizes");
        foreach (var sizes in new (long Allocated, long Logical, long Initialized)[]
        { (-1, 0, 0), (8192, -1, 0), (8192, 1, -1), (8192, 1, 2), (4096, 6000, 6000) })
            Invalid(Record(Extent(0, 1, sizes.Allocated, sizes.Logical, sizes.Initialized), false),
                "LowestVCN zero still rejects invalid total size metadata");

        var bad = Record(Extent()); bad[0] ^= 1; Invalid(bad, "signature remains strict");
        bad = Record(Extent()); NtfsRecordFixtures.U16(bad, 510, 0x1234); Invalid(bad, "raw USA mismatch remains strict");
        bad = Record(Extent()); NtfsRecordFixtures.U16(bad, 16, 0); Invalid(bad, "zero sequence remains invalid");
        bad = Record(Extent()); NtfsRecordFixtures.U16(bad, 22, 0);
        Check(Decode(bad).Status == NtfsDecodeStatus.Free, "free extension is not decoded as an in-use extent");
        bad = Record(Extent()); NtfsRecordFixtures.U16(bad, 22, 5);
        Check(Decode(bad).Status == NtfsDecodeStatus.Unsupported, "unknown record flags remain unsupported");
        Invalid(Record(Extent(5, 4)), "backwards VCN extent remains invalid");
        Invalid(Record(Extent(4, 6)), "runlist must cover exactly the stated extent");
        Invalid(Record(Extent(1UL << 63, (1UL << 63) + 1)), "signed-negative starting VCN remains invalid");
        Invalid(Record(Extent(4, ulong.MaxValue)), "empty sentinel cannot hide nonzero LowestVCN");
        var mapping = Extent(); mapping[64] = 0x19; Invalid(Record(mapping), "invalid run integer width remains invalid");
        mapping = Extent(); mapping[65] = 0; Invalid(Record(mapping), "zero run length remains invalid");
        mapping = Extent(); mapping[66] = 0xff; Invalid(Record(mapping), "negative first physical cluster remains invalid");
        mapping = Extent(); mapping[67] = 0x11; Invalid(Record(mapping), "runlist terminator remains mandatory");
        mapping = Extent(); NtfsRecordFixtures.U16(mapping, 32, (ushort)mapping.Length);
        Invalid(Record(mapping), "mapping offset cannot escape the continuation attribute");
        mapping = Extent(); mapping[64] = 0x01; mapping[65] = 2; mapping[66] = 0;
        Invalid(Record(mapping), "unflagged sparse mappings remain invalid");

        var protectedRecord = Record(Extent(allocated: -1, logical: -1, initialized: -1));
        var restored = protectedRecord.ToArray();
        NtfsRecordFixtures.U16(restored, 510, BitConverter.ToUInt16(restored, 50));
        NtfsRecordFixtures.U16(restored, 1022, BitConverter.ToUInt16(restored, 52));
        Invalid(restored, "raw continuation does not accept kernel-restored USA tails");
        Check(NtfsRecordDecoder.DecodeKernelReturned(restored, 512, slot, 4096, 3, 1) is
            { Status: NtfsDecodeStatus.Decoded, Record: { LogicalBytes: null, AllocatedBytes: null } },
            "separate kernel-returned entry keeps its existing uniform restored-tail contract");
        var mixed = protectedRecord.ToArray(); NtfsRecordFixtures.U16(mixed, 510, BitConverter.ToUInt16(mixed, 50));
        Check(NtfsRecordDecoder.DecodeKernelReturned(mixed, 512, slot, 4096, 3, 1).Status == NtfsDecodeStatus.Invalid,
            "kernel-returned continuation still rejects mixed protected and restored tails");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool canceled = false;
        try { _ = NtfsRecordDecoder.Decode(protectedRecord, 512, token: cancellation.Token, recordNumber: slot); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "pre-cancel publishes no continuation metadata");
        return passed;
    }
}
