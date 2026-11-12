namespace DiskAtlas.NtfsPrototype;

internal static class NtfsPrototypeTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            passed++; Console.WriteLine("PASS NTFS prototype " + name);
        }
        NtfsDecodeResult Decode(byte[] record, ulong slot = 42) => NtfsRecordDecoder.Decode(record, 512, recordNumber: slot);
        NtfsRecord Read(byte[] record)
        {
            var result = Decode(record);
            if (result.Status != NtfsDecodeStatus.Decoded || result.Record is null) throw new InvalidOperationException("valid synthetic record");
            return result.Record;
        }
        void Rejected(byte[] record, string name) => Check(Decode(record).Status is NtfsDecodeStatus.Invalid or NtfsDecodeStatus.Unsupported, name);
        void Mutation(Action<byte[]> mutate, string name)
        {
            var record = NtfsRecordFixtures.File(); mutate(record); Rejected(record, name);
        }
        try
        {
            var bytes = NtfsRecordFixtures.File(); var unchanged = bytes.ToArray();
            var resident = Read(bytes);
            Check(resident.Reference == NtfsRecordFixtures.Reference(42, 3), "full reference uses external slot and sequence");
            Check(resident.LogicalBytes == 17 && resident.AllocatedBytes is null, "resident default DATA allocation needs native-size lookup");
            Check(bytes.SequenceEqual(unchanged), "fixup does not mutate caller record");
            var kernelRaw = NtfsRecordDecoder.DecodeKernelReturned(bytes, 512, 42, 4096, 3, 1);
            Check(kernelRaw.Status == NtfsDecodeStatus.Decoded, "kernel-returned entry also accepts all raw fixup markers");
            var restored = bytes.ToArray();
            NtfsRecordFixtures.U16(restored, 510, BitConverter.ToUInt16(restored, 50));
            NtfsRecordFixtures.U16(restored, 1022, BitConverter.ToUInt16(restored, 52));
            Check(Decode(restored).Status == NtfsDecodeStatus.Invalid, "raw decoder strictly rejects already-restored sector tails");
            var restoredBefore = restored.ToArray();
            var kernelRestored = NtfsRecordDecoder.DecodeKernelReturned(restored, 512, 42, 4096, 3, 1);
            Check(kernelRestored.Status == NtfsDecodeStatus.Decoded && kernelRestored.Record?.Reference == resident.Reference &&
                kernelRestored.Record.LogicalBytes == resident.LogicalBytes, "kernel-returned entry accepts uniformly restored tails");
            Check(restored.SequenceEqual(restoredBefore), "kernel-returned decoding preserves caller bytes");
            var mixedTails = bytes.ToArray(); NtfsRecordFixtures.U16(mixedTails, 510, BitConverter.ToUInt16(mixedTails, 50));
            Check(NtfsRecordDecoder.DecodeKernelReturned(mixedTails, 512, 42, 4096, 3, 1).Status == NtfsDecodeStatus.Invalid,
                "kernel-returned entry rejects mixed raw and restored tails");
            var badKernelTail = restored.ToArray(); NtfsRecordFixtures.U16(badKernelTail, 510, 0x1234);
            Check(NtfsRecordDecoder.DecodeKernelReturned(badKernelTail, 512, 42, 4096, 3, 1).Status == NtfsDecodeStatus.Invalid,
                "kernel-returned entry rejects unmatched tail values");
            Check(resident.Names.Count == 1 && resident.Names[0].Name == "demo.bin" && resident.Names[0].ParentReference == NtfsRecordFixtures.Parent, "name and parent identity decoded");
            Check(resident.LookupReasons == NtfsLookupReason.ResidentAllocation, "resident allocation is not misrepresented as known zero");
            var zero = Read(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), NtfsRecordFixtures.Data(0)));
            Check(zero.LogicalBytes == 0 && zero.AllocatedBytes == 0, "empty DATA remains known zero");
            var nonresident = Read(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), NtfsRecordFixtures.Nonresident()));
            Check(nonresident.LogicalBytes == 6000 && nonresident.AllocatedBytes == 8192, "nonresident size comes from DATA rather than FILE_NAME copies");
            Rejected(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), NtfsRecordFixtures.Nonresident(logical: 1, allocated: 1)), "ordinary allocation must match physical runs and cluster geometry");
            var largerClusters = NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), NtfsRecordFixtures.Nonresident(allocated: 16384));
            var largerGeometry = NtfsRecordDecoder.Decode(largerClusters, 512, recordNumber: 42, clusterSize: 8192);
            Check(largerGeometry.Status == NtfsDecodeStatus.Decoded && largerGeometry.Record?.AllocatedBytes == 16384, "explicit larger cluster geometry yields matching allocation");
            Rejected(largerClusters, "allocation valid for another geometry is rejected with default geometry");
            Check(NtfsRecordDecoder.Decode(bytes, 512, clusterSize: 4095).Status == NtfsDecodeStatus.Unsupported, "non-power-of-two cluster geometry rejected");
            var metadataFlags = NtfsRecordFixtures.Name(); NtfsRecordFixtures.U16(metadataFlags, 12, 1);
            Check(Decode(NtfsRecordFixtures.File(metadataFlags, NtfsRecordFixtures.Data())).Status == NtfsDecodeStatus.Unsupported, "flagged FILE_NAME cannot silently supply trusted metadata");
            metadataFlags = NtfsRecordFixtures.Resident(0x10, new byte[48], flags: 1);
            Check(Decode(NtfsRecordFixtures.Record([NtfsRecordFixtures.Name(), NtfsRecordFixtures.Data(), metadataFlags], addStandard: false)).Status == NtfsDecodeStatus.Unsupported, "flagged STANDARD_INFORMATION cannot silently supply trusted metadata");
            var unicode = Read(NtfsRecordFixtures.File(NtfsRecordFixtures.Name("演示 🗂.txt"), NtfsRecordFixtures.Data()));
            Check(unicode.Names[0].Name == "演示 🗂.txt", "Unicode UTF16 name roundtrip");
            var links = Read(NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("first.bin"), NtfsRecordFixtures.Name("second.bin", 1, NtfsRecordFixtures.Reference(9, 2)), NtfsRecordFixtures.Name("FIRST~1.BIN", 2), NtfsRecordFixtures.Data()], links: 3));
            Check(links.LinkCount == 3 && links.Names.Count == 3 && links.Names.Count(n => n.Namespace == 2) == 1, "hard-link names and DOS alias preserved separately");
            var combined = Read(NtfsRecordFixtures.File(NtfsRecordFixtures.Name("both.txt", 3), NtfsRecordFixtures.Data()));
            Check(combined.Names[0].Namespace == 3, "combined Win32 DOS namespace supported");
            var named = Read(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), NtfsRecordFixtures.Data(4), NtfsRecordFixtures.Data(9, "meta")));
            Check(named.LogicalBytes == 4 && named.LookupReasons.HasFlag(NtfsLookupReason.NamedData), "named DATA never overwrites default stream size");
            var missing = Read(NtfsRecordFixtures.File(NtfsRecordFixtures.Name()));
            Check(missing.LogicalBytes is null && missing.AllocatedBytes is null && missing.LookupReasons.HasFlag(NtfsLookupReason.MissingData), "missing DATA is unknown rather than empty");
            var noStandard = Read(NtfsRecordFixtures.Record([NtfsRecordFixtures.Name(), NtfsRecordFixtures.Data()], addStandard: false));
            Check(noStandard.LogicalBytes is null && noStandard.AllocatedBytes is null && noStandard.LookupReasons.HasFlag(NtfsLookupReason.MissingStandardInformation), "missing standard metadata does not imply ordinary file semantics");
            foreach (var pair in new[] { (Flag: (ushort)1, Reason: NtfsLookupReason.Compressed), (Flag: (ushort)0x8000, Reason: NtfsLookupReason.Sparse) })
            {
                var item = Read(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), NtfsRecordFixtures.Nonresident(flags: pair.Flag)));
                Check(item.LogicalBytes == 6000 && item.AllocatedBytes is null && item.LookupReasons.HasFlag(pair.Reason), pair.Reason + " allocation requests lookup");
            }
            var encrypted = Read(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), NtfsRecordFixtures.Nonresident(flags: 0x4000)));
            Check(encrypted.LookupReasons.HasFlag(NtfsLookupReason.Encrypted), "encrypted DATA explicitly requests lookup");
            var reparse = Read(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), NtfsRecordFixtures.Resident(0xc0, [0, 0, 0, 0]), NtfsRecordFixtures.Data()));
            Check(reparse.LookupReasons.HasFlag(NtfsLookupReason.ReparsePoint), "reparse data explicitly requests lookup");
            var list = Read(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), NtfsRecordFixtures.Resident(0x20, new byte[32]), NtfsRecordFixtures.Data()));
            Check(list.LogicalBytes is null && list.AllocatedBytes is null && list.LookupReasons.HasFlag(NtfsLookupReason.AttributeList), "attribute list cannot claim complete stream sizes");
            var extension = Read(NtfsRecordFixtures.Record([NtfsRecordFixtures.Nonresident()], baseReference: NtfsRecordFixtures.Reference(77, 9)));
            Check(extension.BaseReference == NtfsRecordFixtures.Reference(77, 9) && extension.LogicalBytes is null && extension.AllocatedBytes is null && extension.LookupReasons.HasFlag(NtfsLookupReason.ExtensionRecord), "extension record cannot become a standalone file total");
            Check(Decode(NtfsRecordFixtures.Record([], inUse: false)).Status == NtfsDecodeStatus.Free, "unused record excluded");
            var sequence = NtfsRecordFixtures.File(); NtfsRecordFixtures.U16(sequence, 16, 4);
            Check(Read(sequence).Reference != resident.Reference, "reused slot has a distinct full reference");
            Check(NtfsRecordDecoder.Decode(bytes, 512, ntfsMajor: 3, ntfsMinor: 0).Status == NtfsDecodeStatus.Unsupported, "unvalidated NTFS version rejected");
            Check(NtfsRecordDecoder.Decode(bytes, 4096).Status == NtfsDecodeStatus.Unsupported, "unvalidated fixup stride rejected");
            Check(NtfsRecordDecoder.Decode(bytes, 512, recordNumber: 1UL << 48).Status == NtfsDecodeStatus.Invalid, "out-of-range record slot rejected");
            Rejected(bytes[..700], "truncated record rejected");
            Rejected(new byte[65537], "oversized record rejected before allocation");
            Mutation(r => r[0] = (byte)'B', "bad record signature rejected");
            Mutation(r => r[510] ^= 1, "torn first-sector fixup rejected");
            Mutation(r => r[1022] ^= 1, "torn final-sector fixup rejected");
            Mutation(r => NtfsRecordFixtures.U16(r, 4, 1022), "out-of-bounds USA rejected");
            Mutation(r => NtfsRecordFixtures.U16(r, 6, 2), "inconsistent USA count rejected");
            Mutation(r => NtfsRecordFixtures.U16(r, 20, 48), "attribute overlap with USA rejected");
            Mutation(r => NtfsRecordFixtures.U32(r, 24, 1025), "used length beyond record rejected");
            Mutation(r => NtfsRecordFixtures.U32(r, 28, 2048), "allocated record length mismatch rejected");
            Mutation(r => NtfsRecordFixtures.U32(r, 60, 0), "zero attribute length rejected without looping");
            Mutation(r => NtfsRecordFixtures.U32(r, 60, uint.MaxValue), "overflowing attribute length rejected");
            Mutation(r => NtfsRecordFixtures.U16(r, 76, 1023), "resident value offset outside attribute rejected");
            Mutation(r => NtfsRecordFixtures.U32(r, 72, uint.MaxValue), "resident value length overflow rejected");
            Mutation(r => r[56 + 24 + 65] = 4, "unknown filename namespace rejected");
            Mutation(r => NtfsRecordFixtures.U16(r, 56 + 24 + 66, 0xd800), "unpaired filename surrogate rejected");
            var negative = NtfsRecordFixtures.Nonresident(); NtfsRecordFixtures.U64(negative, 48, ulong.MaxValue);
            Rejected(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), negative), "negative or overflowing logical size rejected");
            var allocation = NtfsRecordFixtures.Nonresident(); NtfsRecordFixtures.U64(allocation, 40, ulong.MaxValue);
            Rejected(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), allocation), "negative or overflowing allocation rejected");
            var map = NtfsRecordFixtures.Nonresident(); NtfsRecordFixtures.U16(map, 32, (ushort)map.Length);
            Rejected(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), map), "mapping offset outside attribute rejected");
            var run = NtfsRecordFixtures.Nonresident(); run[64] = 0x19;
            Rejected(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), run), "mapping width outside supported integer range rejected");
            var unknownAttribute = Decode(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), NtfsRecordFixtures.Data(), NtfsRecordFixtures.Resident(0x1234, [1, 2, 3])));
            Check(unknownAttribute.Status == NtfsDecodeStatus.Unsupported, "unknown attribute type cannot silently complete a record");
            run = NtfsRecordFixtures.Nonresident(); NtfsRecordFixtures.U64(run, 16, 1UL << 63); NtfsRecordFixtures.U64(run, 24, (1UL << 63) + 1);
            Rejected(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), run), "negative signed starting VCN rejected");
            run = NtfsRecordFixtures.Nonresident(); NtfsRecordFixtures.U64(run, 24, 1UL << 63);
            Rejected(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), run), "negative signed ending VCN rejected");
            run = NtfsRecordFixtures.Nonresident(); NtfsRecordFixtures.U64(run, 16, 1); NtfsRecordFixtures.U64(run, 24, ulong.MaxValue);
            Rejected(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), run), "empty-stream VCN sentinel cannot hide nonzero starting VCN");
            var overflowingRun = new byte[80];
            NtfsRecordFixtures.Nonresident().AsSpan(0, 64).CopyTo(overflowingRun);
            NtfsRecordFixtures.U32(overflowingRun, 4, (uint)overflowingRun.Length);
            overflowingRun[64] = 0x81; overflowingRun[65] = 2;
            NtfsRecordFixtures.U64(overflowingRun, 66, long.MaxValue);
            Rejected(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), overflowingRun), "physical run ending beyond signed cluster range rejected");
            run = NtfsRecordFixtures.Nonresident(); run[64] = 0x01; run[65] = 2; run[66] = 0;
            Rejected(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), run), "unflagged sparse mapping cannot claim ordinary allocation");
            run = NtfsRecordFixtures.Nonresident(); run[65] = 0;
            Rejected(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), run), "zero-length mapping run rejected");
            run = NtfsRecordFixtures.Nonresident(); run[66] = 0xff;
            Rejected(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), run), "negative first physical cluster rejected");
            run = NtfsRecordFixtures.Nonresident(); run[67] = 0x11;
            Rejected(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), run), "mapping without terminator rejected");
            run = NtfsRecordFixtures.Nonresident(); NtfsRecordFixtures.U64(run, 24, 2);
            Rejected(NtfsRecordFixtures.File(NtfsRecordFixtures.Name(), run), "mapping virtual-cluster extent mismatch rejected");
            var random = new Random(52733);
            for (int i = 0; i < 512; i++)
            {
                var mutated = bytes.ToArray();
                for (int j = 0; j < 3; j++) mutated[random.Next(mutated.Length)] ^= (byte)random.Next(1, 256);
                var before = mutated.ToArray();
                var result = Decode(mutated);
                if (!mutated.SequenceEqual(before) || (result.Status == NtfsDecodeStatus.Decoded) != (result.Record is not null))
                    throw new InvalidOperationException("mutation isolation");
            }
            Check(true, "bounded deterministic record mutations never escape or mutate input");
            bool cancelled = false;
            using (var source = new CancellationTokenSource())
            {
                source.Cancel();
                try { NtfsRecordDecoder.Decode(bytes, 512, token: source.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                bool kernelCancelled = false;
                try { NtfsRecordDecoder.DecodeKernelReturned(restored, 512, 42, 4096, 3, 1, source.Token); }
                catch (OperationCanceledException) { kernelCancelled = true; }
                Check(kernelCancelled, "cancelled kernel-returned decoding publishes no record");
            }
            Check(cancelled, "cancelled decode exits without publishing a record");
            passed += NtfsIndexTests.Run();
            Console.WriteLine($"PASS NTFS synthetic prototype checks: {passed}; raw volume access: none");
            return 0;
        }
        catch
        {
            Console.Error.WriteLine($"FAIL NTFS synthetic prototype after {passed} checks; no private record details emitted");
            return 1;
        }
    }
}
