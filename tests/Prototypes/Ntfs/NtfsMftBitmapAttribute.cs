using System.Buffers.Binary;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsMftBitmapNonresidentMetadata(long LogicalBytes, long InitializedBytes, long AllocatedBytes);

// Internal-only parsed data. Payload is accessible solely as a fresh copy, not
// a serializable property; no raw record, source token or read address is kept.
internal sealed class NtfsMftBitmapAttributeResult
{
    private readonly byte[] payload;
    internal string Revision => "synthetic-mft-bitmap-attribute-v1";
    internal string EvidenceKind => "Synthetic";
    internal string Status { get; }
    internal string Code { get; }
    // Internal disambiguation when the shared payload budget code is returned.
    // This is an observed attribute form, never a readable mapping or authority.
    internal string Form { get; }
    internal int ExpectedSlots { get; }
    internal int CoveredSlots { get; }
    internal int PayloadBytes => payload.Length;
    internal NtfsMftBitmapNonresidentMetadata? NonresidentMetadata { get; }
    internal byte[] CopyPayload() => (byte[])payload.Clone();
    internal NtfsMftBitmapAttributeResult(string status, string code, int expectedSlots, int coveredSlots = 0,
        byte[]? privatePayload = null, NtfsMftBitmapNonresidentMetadata? metadata = null, string form = "Unknown")
    { Status = status; Code = code; Form = form; ExpectedSlots = expectedSlots; CoveredSlots = coveredSlots; payload = privatePayload ?? []; NonresidentMetadata = metadata; }
}

// Pure attribute location only. Synthetic bytes/geometry do not establish a
// Native source. There is no I/O, nonresident mapping, model integration or
// fallback from a strict raw USA rejection to the kernel representation.
internal static class NtfsMftBitmapAttribute
{
    internal const int MaximumRecordBytes = 1024, MaximumPayloadBytes = 512, MaximumSlots = 4096;
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
    private static bool Fits(int start, long length, int end) => start >= 0 && length >= 0 && start <= end && length <= end - start;

    internal static NtfsMftBitmapAttributeResult Parse(ReadOnlySpan<byte> rawRecord0, NtfsMftGeometry geometry,
        int expectedSlots, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        NtfsMftBitmapAttributeResult Result(string status, string code, string form = "Unknown")
        { token.ThrowIfCancellationRequested(); return new(status, code, expectedSlots, form: form); }
        if (rawRecord0.Length != MaximumRecordBytes) return Result("Invalid", "BitmapInput");
        if (geometry == null || geometry is not { SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024 })
            return Result("Invalid", "BitmapGeometry");
        if (expectedSlots is < 1 or > MaximumSlots || geometry.MftValidDataLength != (long)expectedSlots * 1024)
            return Result("Invalid", "BitmapSlotBudget");
        // Copy exactly once before consulting any parser, so all later reads
        // refer to this same private source rather than the mutable caller span.
        byte[] owned = rawRecord0.ToArray();
        token.ThrowIfCancellationRequested();
        var seed = NtfsMftMap.Parse(owned, geometry, token);
        if (seed.Status == "Unsupported") return Result("Deferred", "BitmapSeedUnsupported");
        if (seed.Status != "Mapped" || seed.Map == null) return Result("Invalid", "BitmapSeedInvalid");
        try
        {
            int usa = U16(owned, 4), usaCount = U16(owned, 6);
            for (int i = 1; i < usaCount; i++)
            {
                token.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteUInt16LittleEndian(owned.AsSpan(i * 512 - 2), U16(owned, usa + i * 2));
            }
            int used = checked((int)U32(owned, 24)), found = -1, foundLength = 0, bitmapCount = 0;
            bool ended = false;
            for (int offset = U16(owned, 20), count = 0; offset < used;)
            {
                token.ThrowIfCancellationRequested();
                if (!Fits(offset, 4, used)) return Result("Invalid", "BitmapAttributeBounds");
                uint type = U32(owned, offset);
                if (type == uint.MaxValue) { ended = true; break; }
                if (++count > 64 || !Fits(offset, 24, used)) return Result("Invalid", "BitmapAttributeBounds");
                uint length = U32(owned, offset + 4);
                if (length < 24 || length % 8 != 0 || !Fits(offset, length, used)) return Result("Invalid", "BitmapAttributeBounds");
                if (type == 0x20) return Result("Deferred", "BitmapExtent");
                if (type == 0xb0) { bitmapCount++; found = offset; foundLength = checked((int)length); }
                offset = checked(offset + (int)length);
            }
            if (!ended) return Result("Invalid", "BitmapAttributeBounds");
            if (bitmapCount == 0) return Result("Missing", "BitmapMissing");
            if (bitmapCount != 1) return Result("Deferred", "BitmapMultiple");
            if (owned[found + 9] != 0) return Result("Deferred", "BitmapNamed");
            if (U16(owned, found + 12) != 0) return Result("Deferred", "BitmapFlags");
            if (owned[found + 8] == 0)
            {
                uint size = U32(owned, found + 16); int value = U16(owned, found + 20);
                if (value < 24 || !Fits(value, size, foundLength)) return Result("Invalid", "BitmapAttributeBounds");
                if (size > MaximumPayloadBytes) return Result("Deferred", "BitmapPayloadBudget", "Resident");
                byte[] payload = owned.AsSpan(found + value, checked((int)size)).ToArray();
                int covered = Math.Min(expectedSlots, checked(payload.Length * 8));
                token.ThrowIfCancellationRequested();
                return new(covered == expectedSlots ? "ResidentComplete" : "ResidentPartial",
                    covered == expectedSlots ? "BitmapResidentComplete" : "BitmapResidentPartial", expectedSlots, covered, payload);
            }
            if (owned[found + 8] != 1 || foundLength < 64) return Result("Invalid", "BitmapAttributeBounds");
            if (U16(owned, found + 34) != 0) return Result("Deferred", "BitmapFlags");
            if (I64(owned, found + 16) != 0) return Result("Deferred", "BitmapExtent");
            long allocated = I64(owned, found + 40), logical = I64(owned, found + 48), initialized = I64(owned, found + 56);
            if (logical < 0 || initialized < 0 || initialized > logical || allocated < logical || allocated < 0 ||
                allocated % geometry.ClusterBytes != 0 || allocated > geometry.VolumeBytes)
                return Result("Invalid", "BitmapNonresidentBounds");
            if (logical > MaximumPayloadBytes) return Result("Deferred", "BitmapPayloadBudget", "Nonresident");
            token.ThrowIfCancellationRequested();
            // Metadata is bounded; this does not validate its physical mapping
            // or authorize reading. No offsets, LCNs or mapping bytes escape.
            return new("Deferred", "BitmapNonresident", expectedSlots, metadata: new(logical, initialized, allocated));
        }
        catch (OverflowException) { return Result("Invalid", "BitmapAttributeBounds"); }
    }
}
