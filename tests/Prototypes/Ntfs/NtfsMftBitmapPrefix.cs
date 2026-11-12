using System.Buffers.Binary;

namespace DiskAtlas.NtfsPrototype;

// A pure plan, never a Native proof. Addresses remain internal to the owning
// reader. No raw record, run list or byte payload is a serializable property.
internal sealed class NtfsMftBitmapPrefixResult
{
    private readonly byte[] prefix;
    internal string EvidenceKind => "Synthetic";
    internal string Status { get; }
    internal string Code { get; }
    internal string Form { get; }
    internal int ExpectedSlots { get; }
    internal int LogicalPrefixBytes { get; }
    internal long LogicalBytes { get; }
    internal long InitializedBytes { get; }
    internal long? AllocatedBytes { get; }
    internal int ReadBytes { get; }
    internal IReadOnlyList<NtfsRawSlice> ReadPlan { get; }
    internal byte[] CopyPrefix() => prefix.ToArray();
    internal NtfsMftBitmapPrefixResult(string status, string code, string form, int slots, int needed,
        long logical = 0, long initialized = 0, long? allocated = null, byte[]? bytes = null, NtfsRawSlice? slice = null)
    {
        Status = status; Code = code; Form = form; ExpectedSlots = slots; LogicalPrefixBytes = needed;
        LogicalBytes = logical; InitializedBytes = initialized; AllocatedBytes = allocated; prefix = bytes ?? [];
        ReadBytes = slice?.Length ?? 0;
        ReadPlan = Array.AsReadOnly(slice == null ? Array.Empty<NtfsRawSlice>() : new NtfsRawSlice[] { slice });
    }
}

internal static class NtfsMftBitmapPrefix
{
    internal const int MaximumMetadataBytes = 1024 * 1024, MaximumRuns = 128;
    private static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
    private static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
    private static ulong U64(byte[] b, int p) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p));
    private static long I64(byte[] b, int p) => BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(p));
    private static bool Fits(int p, long n, int end) => p >= 0 && n >= 0 && p <= end && n <= end - p;

    internal static NtfsMftBitmapPrefixResult Parse(ReadOnlySpan<byte> rawRecord0, NtfsMftGeometry geometry,
        int expectedSlots, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        int needed = expectedSlots is >= 1 and <= 4096 ? (expectedSlots + 7) / 8 : 0;
        NtfsMftBitmapPrefixResult Fail(string status, string code, string form = "Unknown")
        { token.ThrowIfCancellationRequested(); return new(status, code, form, expectedSlots, needed); }
        if (rawRecord0.Length != 1024) return Fail("Invalid", "BitmapPrefixInput");
        if (geometry == null || geometry is not { SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024 })
            return Fail("Invalid", "BitmapPrefixGeometry");
        if (needed == 0 || geometry.MftValidDataLength != (long)expectedSlots * 1024)
            return Fail("Invalid", "BitmapPrefixSlotBudget");
        byte[] owned = rawRecord0.ToArray();
        token.ThrowIfCancellationRequested();
        var seed = NtfsMftMap.Parse(owned, geometry, token);
        if (seed.Status == "Unsupported") return Fail("Deferred", "BitmapPrefixSeedUnsupported");
        if (seed.Status != "Mapped" || seed.Map == null) return Fail("Invalid", "BitmapPrefixSeedInvalid");
        try
        {
            int usa = U16(owned, 4), usaCount = U16(owned, 6);
            for (int i = 1; i < usaCount; i++)
            { token.ThrowIfCancellationRequested(); BinaryPrimitives.WriteUInt16LittleEndian(owned.AsSpan(i * 512 - 2), U16(owned, usa + i * 2)); }
            int used = checked((int)U32(owned, 24)), found = -1, length = 0, matches = 0;
            bool ended = false;
            for (int p = U16(owned, 20), count = 0; p < used;)
            {
                token.ThrowIfCancellationRequested();
                if (!Fits(p, 4, used)) return Fail("Invalid", "BitmapPrefixAttributeBounds");
                uint type = U32(owned, p);
                if (type == uint.MaxValue) { ended = true; break; }
                if (++count > 64 || !Fits(p, 24, used)) return Fail("Invalid", "BitmapPrefixAttributeBounds");
                uint size = U32(owned, p + 4);
                if (size < 24 || size % 8 != 0 || !Fits(p, size, used)) return Fail("Invalid", "BitmapPrefixAttributeBounds");
                if (type == 0x20) return Fail("Deferred", "BitmapPrefixExtent");
                if (type == 0xb0) { found = p; length = checked((int)size); matches++; }
                p = checked(p + (int)size);
            }
            if (!ended) return Fail("Invalid", "BitmapPrefixAttributeBounds");
            if (matches == 0) return Fail("Missing", "BitmapPrefixMissing");
            if (matches != 1) return Fail("Deferred", "BitmapPrefixMultiple");
            if (owned[found + 9] != 0) return Fail("Deferred", "BitmapPrefixNamed");
            if (U16(owned, found + 12) != 0) return Fail("Deferred", "BitmapPrefixFlags");
            if (owned[found + 8] == 0)
            {
                uint bytes = U32(owned, found + 16); int offset = U16(owned, found + 20);
                if (offset < 24 || !Fits(offset, bytes, length)) return Fail("Invalid", "BitmapPrefixResidentBounds", "Resident");
                if (bytes > 512) return Fail("Deferred", "BitmapPrefixPayloadBudget", "Resident");
                if (bytes < needed) return Fail("Deferred", "BitmapPrefixCoverage", "Resident");
                byte[] prefix = owned.AsSpan(found + offset, needed).ToArray();
                token.ThrowIfCancellationRequested();
                return new("Resident", "BitmapPrefixResident", "Resident", expectedSlots, needed, bytes, bytes, bytes: prefix);
            }
            if (owned[found + 8] != 1 || length < 64) return Fail("Invalid", "BitmapPrefixAttributeBounds");
            if (U16(owned, found + 34) != 0) return Fail("Deferred", "BitmapPrefixFlags", "Nonresident");
            if (U64(owned, found + 16) != 0) return Fail("Deferred", "BitmapPrefixExtent", "Nonresident");
            ulong high = U64(owned, found + 24);
            long allocated = I64(owned, found + 40), logical = I64(owned, found + 48), initialized = I64(owned, found + 56);
            if (allocated <= 0 || logical < needed || initialized < needed || initialized > logical || logical > allocated ||
                allocated % 4096 != 0 || allocated > geometry.VolumeBytes)
                return Fail("Invalid", "BitmapPrefixLengths", "Nonresident");
            if (allocated > MaximumMetadataBytes || logical > MaximumMetadataBytes || initialized > MaximumMetadataBytes)
                return Fail("Deferred", "BitmapPrefixMetadataBudget", "Nonresident");
            if (high >= (ulong)(MaximumMetadataBytes / 4096) || checked(((long)high + 1) * 4096) != allocated)
                return Fail("Invalid", "BitmapPrefixVcn", "Nonresident");
            int mapping = U16(owned, found + 32), end = checked(found + length);
            if (mapping < 64 || mapping >= length) return Fail("Invalid", "BitmapPrefixMappingBounds", "Nonresident");
            var runs = new List<NtfsMftRun>();
            long vcn = 0, lcn = 0;
            bool terminated = false;
            for (int p = checked(found + mapping); p < end;)
            {
                token.ThrowIfCancellationRequested();
                byte header = owned[p++];
                if (header == 0) { terminated = true; break; }
                if (runs.Count >= MaximumRuns) return Fail("Deferred", "BitmapPrefixRunBudget", "Nonresident");
                int lengthBytes = header & 15, offsetBytes = header >> 4;
                if (lengthBytes is < 1 or > 8 || offsetBytes > 8 || !Fits(p, lengthBytes + offsetBytes, end))
                    return Fail("Invalid", "BitmapPrefixRunEncoding", "Nonresident");
                if (offsetBytes == 0) return Fail("Deferred", "BitmapPrefixSparse", "Nonresident");
                ulong count = 0, delta = 0;
                for (int i = 0; i < lengthBytes; i++) count |= (ulong)owned[p++] << (i * 8);
                for (int i = 0; i < offsetBytes; i++) delta |= (ulong)owned[p++] << (i * 8);
                if (count == 0 || count > long.MaxValue) return Fail("Invalid", "BitmapPrefixRunLength", "Nonresident");
                if (offsetBytes < 8 && (delta & (1UL << (offsetBytes * 8 - 1))) != 0) delta |= ulong.MaxValue << (offsetBytes * 8);
                lcn = checked(lcn + unchecked((long)delta));
                if (lcn == 0) return Fail("Deferred", "BitmapPrefixSparse", "Nonresident");
                long lastLcn = checked(lcn + (long)count), lastVcn = checked(vcn + (long)count);
                if (lcn < 0 || lastLcn > geometry.TotalClusters || checked(lastLcn * 4096) > geometry.VolumeBytes)
                    return Fail("Invalid", "BitmapPrefixPhysicalBounds", "Nonresident");
                if (lastVcn > (long)high + 1) return Fail("Invalid", "BitmapPrefixRunCoverage", "Nonresident");
                foreach (var previous in runs)
                    if (lcn < previous.FirstLcn + previous.ClusterCount && previous.FirstLcn < lastLcn)
                        return Fail("Invalid", "BitmapPrefixPhysicalOverlap", "Nonresident");
                foreach (var data in seed.Map.Runs)
                    if (lcn < data.FirstLcn + data.ClusterCount && data.FirstLcn < lastLcn)
                        return Fail("Invalid", "BitmapPrefixMftOverlap", "Nonresident");
                runs.Add(new(vcn, (long)count, lcn)); vcn = lastVcn;
            }
            if (!terminated || runs.Count == 0 || vcn != (long)high + 1 || checked(vcn * 4096) != allocated)
                return Fail("Invalid", "BitmapPrefixRunCoverage", "Nonresident");
            long physical = checked(runs[0].FirstLcn * 4096);
            if (physical < 0 || physical > geometry.VolumeBytes - 512 || physical % 512 != 0)
                return Fail("Invalid", "BitmapPrefixPhysicalBounds", "Nonresident");
            token.ThrowIfCancellationRequested();
            return new("Planned", "BitmapPrefixPlanned", "Nonresident", expectedSlots, needed, logical, initialized, allocated,
                slice: new(physical, 512, 0));
        }
        catch (OverflowException) { return Fail("Invalid", "BitmapPrefixArithmeticOverflow"); }
    }
}
