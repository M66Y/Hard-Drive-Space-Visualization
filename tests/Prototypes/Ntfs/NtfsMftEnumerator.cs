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

// Private parsed targets can contain fixture names and references. This object
// must not be serialized: the source publishes a separate safe summary only
// after unlocking and rechecking its Native context.
internal sealed record NtfsMftEnumerationResult(long ValidBytes, int ExpectedSlots, int VisitedSlots,
    int Decoded, int Free, int Unsupported, int Invalid, int LookupRequired, int UnknownLogical,
    int UnknownAllocated, int Pages, bool CompletedCursor, IReadOnlyDictionary<ulong, NtfsRecord> TargetRecords);

// Pure, serial, bounded traversal. No file/device opening, source authority,
// name tokens, tree building, allocation totals or raw-record retention.
internal static class NtfsMftEnumerator
{
    internal const int MaximumSlots = 4096;
    internal const int PageBytes = 65536;
    internal const int MaximumTargets = 21;

    private static void Require([DoesNotReturnIf(false)] bool condition, string code)
    { if (!condition) throw new NtfsMftEnumerationException(code); }

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
                    case NtfsDecodeStatus.Invalid: invalid++; break;
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
        return new(validBytes, expected, visited, decoded, free, unsupported, invalid, lookup,
            unknownLogical, unknownAllocated, pages, true, new ReadOnlyDictionary<ulong, NtfsRecord>(retained));
    }
}
