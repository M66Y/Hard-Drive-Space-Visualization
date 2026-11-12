using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsMftCollectedSlot(NtfsDecodeStatus Status, ulong? HeaderReference);
internal sealed record NtfsMftIndexCollection(NtfsMftEnumerationResult Range,
    IReadOnlyDictionary<ulong, NtfsRecord> Records, IReadOnlyList<NtfsMftCollectedSlot> Slots);

// Independent bounded consumer for the fixed-fixture indexing profile. Older
// range enumeration continues retaining only its twenty-one requested records.
// Nothing here establishes a source or serializes names, identities or bytes.
internal static class NtfsMftIndexCollector
{
    internal const int MaximumNames = 16384, MaximumCharacters = 1024 * 1024;
    private static void Require([DoesNotReturnIf(false)] bool condition, string code)
    { if (!condition) throw new NtfsMftEnumerationException(code); }

    internal static NtfsMftIndexCollection Collect(NtfsMftMap map, IReadOnlyList<ulong> targetReferences,
        NtfsMftReadInto read, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Require(map != null && read != null && targetReferences != null, "MftCollectionInput");
        var geometry = map.Geometry;
        long valid = geometry.MftValidDataLength;
        Require(geometry is { SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024 } && valid >= 1024 && valid % 1024 == 0 &&
            valid / 1024 <= NtfsMftEnumerator.MaximumSlots && valid <= map.DataBytes && valid <= map.InitializedBytes && valid <= map.AllocatedBytes,
            "MftCollectionGeometry");
        int expected = checked((int)(valid / 1024));
        Require(targetReferences.Count is >= 0 and <= 21, "MftCollectionTargets");
        var required = new Dictionary<ulong, ulong>();
        for (int i = 0; i < targetReferences.Count; i++)
        {
            token.ThrowIfCancellationRequested(); ulong reference = targetReferences[i], slot = reference & NtfsRecordDecoder.MaximumRecordNumber;
            Require(reference >> 48 != 0 && slot < (ulong)expected && required.TryAdd(slot, reference), "MftCollectionTargets");
        }
        var records = new Dictionary<ulong, NtfsRecord>(); var targets = new Dictionary<ulong, NtfsRecord>();
        var slots = new List<NtfsMftCollectedSlot>(expected);
        var codes = new Dictionary<(NtfsDecodeStatus Status, string Code), int>();
        byte[] page = new byte[65536];
        int decoded = 0, free = 0, invalid = 0, unsupported = 0, lookup = 0, unknownLogical = 0, unknownAllocated = 0;
        int pages = 0, names = 0, characters = 0, allZero = 0;
        for (long cursor = 0; cursor < valid;)
        {
            token.ThrowIfCancellationRequested(); int length = checked((int)Math.Min(page.Length, valid - cursor));
            int received;
            try { received = read(cursor, page.AsMemory(0, length), token); }
            catch (Exception)
            {
                // A callback cannot impersonate our controlled exception or
                // caller cancellation using an arbitrary private message.
                token.ThrowIfCancellationRequested();
                throw new NtfsMftEnumerationException("MftCollectionReadFailed");
            }
            token.ThrowIfCancellationRequested(); Require(received == length, "MftCollectionShortRead"); pages++;
            for (int offset = 0; offset < length; offset += 1024)
            {
                token.ThrowIfCancellationRequested(); ulong slot = checked((ulong)((cursor + offset) / 1024));
                Require(slot == (ulong)slots.Count && slots.Count < expected, "MftCollectionCursor");
                var raw = page.AsSpan(offset, 1024);
                var result = NtfsRecordDecoder.Decode(raw, 512, token: token, recordNumber: slot, clusterSize: 4096);
                Require(NtfsMftEnumerator.IsKnownCode(result.Status, result.Code), "MftCollectionCode");
                var key = (result.Status, result.Code);
                if (codes.TryGetValue(key, out int count)) codes[key] = checked(count + 1);
                else { Require(codes.Count < 128, "MftCollectionCode"); codes.Add(key, 1); }
                ulong? headerReference = null;
                switch (result.Status)
                {
                    case NtfsDecodeStatus.Decoded:
                        Require(result.Record != null, "MftCollectionRecord"); var record = result.Record;
                        Require(record.Names.Count <= MaximumNames - names && records.Count < 4096, "MftCollectionNameBudget");
                        names += record.Names.Count;
                        foreach (var name in record.Names)
                        {
                            token.ThrowIfCancellationRequested(); Require(name.Name.Length <= MaximumCharacters - characters, "MftCollectionNameBudget");
                            characters += name.Name.Length;
                        }
                        Require(records.TryAdd(record.Reference, record), "MftCollectionRecord"); decoded++; headerReference = record.Reference;
                        if (record.LookupReasons != NtfsLookupReason.None) lookup++;
                        if (!record.LogicalBytes.HasValue) unknownLogical++;
                        if (!record.AllocatedBytes.HasValue) unknownAllocated++;
                        if (required.TryGetValue(slot, out ulong expectedReference))
                            Require(record.Reference == expectedReference && targets.TryAdd(expectedReference, record), "MftCollectionTargetIdentity");
                        break;
                    case NtfsDecodeStatus.Free:
                        free++;
                        // Strict Decode already verified the complete header
                        // and USA before declaring Free. No raw body is kept.
                        headerReference = slot | ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(raw[16..]) << 48);
                        break;
                    case NtfsDecodeStatus.Invalid:
                        invalid++; if (raw.IndexOfAnyExcept((byte)0) < 0) allZero++; break;
                    case NtfsDecodeStatus.Unsupported: unsupported++; break;
                    default: throw new NtfsMftEnumerationException("MftCollectionCode");
                }
                slots.Add(new(result.Status, headerReference));
            }
            cursor = checked(cursor + length);
        }
        Require(slots.Count == expected && decoded + free + invalid + unsupported == expected && targets.Count == required.Count &&
            codes.Values.Sum() == expected && allZero <= codes.GetValueOrDefault((NtfsDecodeStatus.Invalid, "Signature")), "MftCollectionCoverage");
        var counts = codes.OrderBy(item => (int)item.Key.Status).ThenBy(item => item.Key.Code, StringComparer.Ordinal)
            .Select(item => new NtfsMftCodeCount(item.Key.Status.ToString(), item.Key.Code, item.Value)).ToArray();
        var range = new NtfsMftEnumerationResult(valid, expected, expected, decoded, free, unsupported, invalid, lookup, unknownLogical,
            unknownAllocated, pages, true, new ReadOnlyDictionary<ulong, NtfsRecord>(targets), allZero, Array.AsReadOnly(counts));
        token.ThrowIfCancellationRequested();
        return new(range, new ReadOnlyDictionary<ulong, NtfsRecord>(records), slots.AsReadOnly());
    }
}
