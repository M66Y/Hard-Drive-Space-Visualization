using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace DiskAtlas.NtfsPrototype;

// The offset belongs to the validated MFT data stream, never to a volume. The
// real source supplies a private token-bound callback; tests supply memory.
internal delegate int NtfsMftReadInto(long mftOffset, Memory<byte> destination, CancellationToken token);
internal sealed class NtfsMftEnumerationException(string code) : IOException(code)
{
    internal string Code { get; } = code;
}
internal sealed record NtfsMftCodeCount(string Status, string Code, int Count);

// Private parsed targets can contain fixture names and references. This object
// must not be serialized: the source publishes a separate safe summary only
// after unlocking and rechecking its Native context.
internal sealed record NtfsMftEnumerationResult(long ValidBytes, int ExpectedSlots, int VisitedSlots,
    int Decoded, int Free, int Unsupported, int Invalid, int LookupRequired, int UnknownLogical,
    int UnknownAllocated, int Pages, bool CompletedCursor, IReadOnlyDictionary<ulong, NtfsRecord> TargetRecords,
    int AllZeroInvalid, IReadOnlyList<NtfsMftCodeCount> CodeCounts);

// Pure, serial, bounded traversal. No file/device opening, source authority,
// name tokens, tree building, allocation totals or raw-record retention.
internal static class NtfsMftEnumerator
{
    internal const int MaximumSlots = 4096;
    internal const int PageBytes = 65536;
    internal const int MaximumTargets = 21;
    internal const int MaximumCodeCounts = 128;

    private static void Require([DoesNotReturnIf(false)] bool condition, string code)
    { if (!condition) throw new NtfsMftEnumerationException(code); }

    // This whitelist is deliberately independent of arbitrary decoder messages.
    // A future decoder status/code must be explicitly reviewed before it can be
    // exported. Status/code pairs are not interchangeable across categories.
    internal static bool IsKnownCode(NtfsDecodeStatus status, string? code) => status switch
    {
        NtfsDecodeStatus.Decoded => code == "Decoded",
        NtfsDecodeStatus.Free => code == "FreeRecord",
        NtfsDecodeStatus.Invalid => code is "AllocationGeometry" or "AttributeHeader" or "AttributeLength" or "AttributeName" or
            "DataSize" or "DuplicateAttributeId" or "FileName" or "FixupMismatch" or "MappingBounds" or "MappingPairs" or
            "MissingEndMarker" or "RecordHeader" or "RecordLength" or "RecordNumber" or "ResidentBounds" or "Sequence" or
            "Signature" or "SparseMappingFlags" or "StandardInformation",
        NtfsDecodeStatus.Unsupported => code is "AttributeFlags" or "AttributeForm" or "AttributeLimit" or "AttributeType" or
            "ClusterGeometry" or "FixupStride" or "MetadataFlags" or "MultipleUnnamedData" or "NameLimit" or "NtfsVersion" or "RecordFlags",
        _ => false
    };

    internal static NtfsMftEnumerationResult Enumerate(NtfsMftMap map, IReadOnlyList<ulong> targetReferences,
        NtfsMftReadInto read, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Require(map != null && targetReferences != null && read != null, "MftEnumerationInput");
        var geometry = map!.Geometry;
        Require(geometry is { SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024 }, "MftEnumerationGeometry");
        long validBytes = geometry.MftValidDataLength;
        Require(validBytes >= geometry.RecordBytes && validBytes % geometry.RecordBytes == 0 &&
            validBytes / geometry.RecordBytes <= MaximumSlots && validBytes <= map.InitializedBytes &&
            validBytes <= map.DataBytes && validBytes <= map.AllocatedBytes, "MftEnumerationSlotBudget");
        int expected = checked((int)(validBytes / geometry.RecordBytes));
        int targetCount = targetReferences!.Count;
        Require(targetCount is >= 0 and <= MaximumTargets, "MftEnumerationTargetBudget");
        var expectedReferences = new Dictionary<ulong, ulong>(targetCount);
        for (int i = 0; i < targetCount; i++)
        {
            token.ThrowIfCancellationRequested();
            ulong reference = targetReferences[i], slot = reference & NtfsRecordDecoder.MaximumRecordNumber;
            Require(reference >> 48 != 0 && slot < (ulong)expected && expectedReferences.TryAdd(slot, reference),
                "MftEnumerationTargetIdentity");
        }

        // Exactly one managed page is reused, including for the final short
        // page. Strict Decode still owns its bounded per-record private copy.
        byte[] page = new byte[PageBytes];
        var retained = new Dictionary<ulong, NtfsRecord>(targetCount);
        long cursor = 0;
        int visited = 0, decoded = 0, free = 0, unsupported = 0, invalid = 0;
        int lookup = 0, unknownLogical = 0, unknownAllocated = 0, pages = 0;
        int allZeroInvalid = 0;
        var codeCounts = new Dictionary<(NtfsDecodeStatus Status, string Code), int>();
        while (cursor < validBytes)
        {
            token.ThrowIfCancellationRequested();
            int length = checked((int)Math.Min(PageBytes, validBytes - cursor));
            Require(length > 0 && length % geometry.RecordBytes == 0, "MftEnumerationPageBounds");
            int received;
            try { received = read!(cursor, page.AsMemory(0, length), token); }
            catch (OperationCanceledException) { throw; }
            catch (NtfsMftEnumerationException) { throw; }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
            { throw new NtfsMftEnumerationException("MftEnumerationReadFailed"); }
            token.ThrowIfCancellationRequested();
            Require(received == length, "MftEnumerationShortRead");
            pages++;
            for (int offset = 0; offset < length; offset += geometry.RecordBytes)
            {
                token.ThrowIfCancellationRequested();
                ulong slot = checked((ulong)((cursor + offset) / geometry.RecordBytes));
                Require(slot == (ulong)visited, "MftEnumerationCursor");
                var result = NtfsRecordDecoder.Decode(page.AsSpan(offset, geometry.RecordBytes), geometry.SectorBytes,
                    token: token, recordNumber: slot, clusterSize: geometry.ClusterBytes);
                Require(IsKnownCode(result.Status, result.Code), "MftEnumerationDecodeCode");
                var key = (result.Status, result.Code);
                if (codeCounts.TryGetValue(key, out int previous)) codeCounts[key] = checked(previous + 1);
                else
                {
                    Require(codeCounts.Count < MaximumCodeCounts, "MftEnumerationCodeBudget");
                    codeCounts.Add(key, 1);
                }
                switch (result.Status)
                {
                    case NtfsDecodeStatus.Decoded:
                        Require(result.Record != null, "MftEnumerationDecodedRecord");
                        var record = result.Record!;
                        decoded++;
                        if (record.LookupReasons != NtfsLookupReason.None) lookup++;
                        if (!record.LogicalBytes.HasValue) unknownLogical++;
                        if (!record.AllocatedBytes.HasValue) unknownAllocated++;
                        if (expectedReferences.TryGetValue(slot, out ulong reference))
                        {
                            Require(record.Reference == reference && retained.TryAdd(reference, record),
                                "MftEnumerationTargetIdentity");
                        }
                        break;
                    case NtfsDecodeStatus.Free: free++; break;
                    case NtfsDecodeStatus.Unsupported: unsupported++; break;
                    case NtfsDecodeStatus.Invalid:
                        invalid++;
                        // An observation of raw bytes, not an allocation bitmap
                        // or a reason to classify this record as free.
                        if (page.AsSpan(offset, geometry.RecordBytes).IndexOfAnyExcept((byte)0) < 0) allZeroInvalid++;
                        break;
                    default: throw new NtfsMftEnumerationException("MftEnumerationDecodeStatus");
                }
                visited++;
            }
            cursor = checked(cursor + length);
        }
        token.ThrowIfCancellationRequested();
        Require(cursor == validBytes && visited == expected && visited == decoded + free + unsupported + invalid,
            "MftEnumerationCoverage");
        Require(retained.Count == targetCount, "MftEnumerationTargetsMissing");
        Require(codeCounts.Values.Sum() == visited && allZeroInvalid <= invalid &&
            allZeroInvalid <= codeCounts.GetValueOrDefault((NtfsDecodeStatus.Invalid, "Signature")), "MftEnumerationCodeCoverage");
        var reasons = codeCounts.OrderBy(item => (int)item.Key.Status).ThenBy(item => item.Key.Code, StringComparer.Ordinal)
            .Select(item => new NtfsMftCodeCount(item.Key.Status.ToString(), item.Key.Code, item.Value)).ToArray();
        token.ThrowIfCancellationRequested();
        return new(validBytes, expected, visited, decoded, free, unsupported, invalid, lookup,
            unknownLogical, unknownAllocated, pages, true, new ReadOnlyDictionary<ulong, NtfsRecord>(retained), allZeroInvalid, Array.AsReadOnly(reasons));
    }
}
