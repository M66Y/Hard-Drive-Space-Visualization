using System.Buffers.Binary;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsMftGeometry(int SectorBytes, int ClusterBytes, int RecordBytes,
    long VolumeBytes, long TotalClusters, long MftStartLcn, long MftValidDataLength);
internal sealed record NtfsMftRun(long FirstVcn, long ClusterCount, long FirstLcn);
internal sealed record NtfsRawSlice(long VolumeOffset, int Length, int RecordOffset);
internal sealed record NtfsMftMapResult(string Status, string Code, NtfsMftMap? Map);

// Pure bounded addressing only. The caller must establish the volume/image and
// locking provenance; a successful map is not an active-volume snapshot.
internal sealed class NtfsMftMap
{
    internal const int MaxRuns = 128;
    internal NtfsMftGeometry Geometry { get; }
    internal IReadOnlyList<NtfsMftRun> Runs { get; }
    internal long DataBytes { get; }
    internal long InitializedBytes { get; }
    internal long AllocatedBytes { get; }

    private NtfsMftMap(NtfsMftGeometry geometry, IEnumerable<NtfsMftRun> runs,
        long data, long initialized, long allocated)
    {
        Geometry = geometry;
        Runs = Array.AsReadOnly(runs.ToArray());
        DataBytes = data; InitializedBytes = initialized; AllocatedBytes = allocated;
    }

    private static NtfsMftMapResult Invalid(string code) => new("Invalid", code, null);
    private static NtfsMftMapResult Unsupported(string code) => new("Unsupported", code, null);
    private static bool Fits(int offset, long count, int length) => offset >= 0 && count >= 0 && offset <= length && count <= length - offset;
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static long I64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]);

    internal static NtfsMftMapResult Parse(ReadOnlySpan<byte> rawRecord0, NtfsMftGeometry geometry,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (geometry == null) return Invalid("MftGeometry");
        if (geometry.SectorBytes != 512) return Unsupported("MftFixupStride");
        if (geometry.ClusterBytes is < 512 or > 65536 || (geometry.ClusterBytes & (geometry.ClusterBytes - 1)) != 0 ||
            geometry.RecordBytes is < 1024 or > 65536 || geometry.RecordBytes % 512 != 0 ||
            geometry.VolumeBytes <= 0 || geometry.VolumeBytes % geometry.SectorBytes != 0 || geometry.TotalClusters <= 0 ||
            geometry.TotalClusters > geometry.VolumeBytes / geometry.ClusterBytes ||
            geometry.MftStartLcn < 0 || geometry.MftStartLcn >= geometry.TotalClusters ||
            geometry.MftValidDataLength < geometry.RecordBytes || geometry.MftValidDataLength % geometry.RecordBytes != 0)
            return Invalid("MftGeometry");
        if (rawRecord0.Length != geometry.RecordBytes || rawRecord0.Length > NtfsRecordDecoder.MaximumRecordBytes)
            return Invalid("MftRecordLength");
        try
        {
            long bootstrap = checked(geometry.MftStartLcn * geometry.ClusterBytes);
            if (bootstrap > geometry.VolumeBytes - geometry.RecordBytes) return Invalid("MftBootstrapBounds");
            // Decode and inspect the same private source, never a caller-owned buffer twice.
            byte[] owned = rawRecord0.ToArray();
            var decoded = NtfsRecordDecoder.Decode(owned, geometry.SectorBytes, 3, 1, token, 0, geometry.ClusterBytes);
            if (decoded.Status == NtfsDecodeStatus.Unsupported) return Unsupported("MftRecord-" + decoded.Code);
            if (decoded.Status != NtfsDecodeStatus.Decoded || decoded.Record == null) return Invalid("MftRecord-" + decoded.Code);
            var record = decoded.Record;
            if (record.IsDirectory || record.BaseReference != 0 || (record.Reference & NtfsRecordDecoder.MaximumRecordNumber) != 0)
                return Unsupported("MftBaseRecord");
            if (record.LookupReasons != NtfsLookupReason.None) return Unsupported("MftLookupRequired");
            if (!record.Names.Any(name => name.Namespace != 2 && string.Equals(name.Name, "$MFT", StringComparison.OrdinalIgnoreCase)))
                return Invalid("MftName");
            // Strict Decode above has verified every USA access and every protected tail.
            int usa = U16(owned, 4), usaCount = U16(owned, 6);
            for (int i = 1; i < usaCount; i++)
            {
                token.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteUInt16LittleEndian(owned.AsSpan(i * 512 - 2), U16(owned, usa + i * 2));
            }
            int used = checked((int)U32(owned, 24));
            int dataOffset = -1, dataLength = 0;
            bool ended = false;
            for (int offset = U16(owned, 20), attributes = 0; offset < used;)
            {
                token.ThrowIfCancellationRequested();
                if (!Fits(offset, 4, used)) return Invalid("MftAttributeBounds");
                uint type = U32(owned, offset);
                if (type == uint.MaxValue) { ended = true; break; }
                if (++attributes > 1024 || !Fits(offset, 24, used)) return Invalid("MftAttributeBounds");
                uint length = U32(owned, offset + 4);
                if (length < 24 || length % 8 != 0 || !Fits(offset, length, used)) return Invalid("MftAttributeBounds");
                if (type == 0x20) return Unsupported("MftAttributeList");
                if (type == 0x80)
                {
                    if (owned[offset + 9] != 0) return Unsupported("MftNamedData");
                    if (dataOffset != -1) return Unsupported("MftMultipleData");
                    if (owned[offset + 8] != 1 || U16(owned, offset + 12) != 0) return Unsupported("MftDataForm");
                    if (length < 64 || U16(owned, offset + 34) != 0) return Unsupported("MftDataForm");
                    dataOffset = offset; dataLength = (int)length;
                }
                offset = checked(offset + (int)length);
            }
            if (!ended || dataOffset < 0) return Invalid("MftMissingData");
            var data = owned.AsSpan(dataOffset, dataLength);
            ulong low = U64(data, 16), high = U64(data, 24);
            if (low != 0) return Unsupported("MftExtentContinuation");
            if (high >= long.MaxValue) return Invalid("MftVcnBounds");
            long allocated = I64(data, 40), size = I64(data, 48), initialized = I64(data, 56);
            if (allocated <= 0 || allocated % geometry.ClusterBytes != 0 || size < geometry.RecordBytes ||
                size % geometry.RecordBytes != 0 || initialized < geometry.RecordBytes || initialized % geometry.RecordBytes != 0 ||
                initialized > size || size > allocated || geometry.MftValidDataLength > initialized ||
                record.LogicalBytes != size || record.AllocatedBytes != allocated)
                return Invalid("MftDataLengths");
            int mapping = U16(data, 32);
            if (mapping < 64 || mapping >= data.Length) return Invalid("MftMappingBounds");
            var runs = new List<NtfsMftRun>(MaxRuns);
            long nextVcn = 0, lcn = 0;
            bool terminated = false;
            for (int p = mapping; p < data.Length;)
            {
                token.ThrowIfCancellationRequested();
                byte header = data[p++];
                if (header == 0) { terminated = true; break; }
                if (runs.Count >= MaxRuns) return Unsupported("MftRunLimit");
                int lengthBytes = header & 15, offsetBytes = header >> 4;
                if (lengthBytes is < 1 or > 8 || offsetBytes is < 1 or > 8 || !Fits(p, lengthBytes + offsetBytes, data.Length))
                    return Invalid("MftRunEncoding");
                ulong rawLength = 0, rawDelta = 0;
                for (int i = 0; i < lengthBytes; i++) rawLength |= (ulong)data[p++] << (8 * i);
                for (int i = 0; i < offsetBytes; i++) rawDelta |= (ulong)data[p++] << (8 * i);
                if (rawLength == 0 || rawLength > long.MaxValue) return Invalid("MftRunLength");
                if (offsetBytes < 8 && (rawDelta & (1UL << (8 * offsetBytes - 1))) != 0)
                    rawDelta |= ulong.MaxValue << (8 * offsetBytes);
                lcn = checked(lcn + unchecked((long)rawDelta));
                long length = (long)rawLength;
                long endLcn = checked(lcn + length), endVcn = checked(nextVcn + length);
                if (lcn < 0 || endLcn > geometry.TotalClusters) return Invalid("MftRunVolumeBounds");
                if (endVcn > (long)high + 1) return Invalid("MftRunCoverage");
                long volumeOffset = checked(lcn * geometry.ClusterBytes), volumeEnd = checked(endLcn * geometry.ClusterBytes);
                if (volumeOffset < 0 || volumeEnd > geometry.VolumeBytes) return Invalid("MftRunVolumeBounds");
                foreach (var previous in runs)
                    if (lcn < previous.FirstLcn + previous.ClusterCount && previous.FirstLcn < endLcn)
                        return Invalid("MftRunPhysicalOverlap");
                runs.Add(new(nextVcn, length, lcn));
                nextVcn = endVcn;
            }
            if (!terminated || runs.Count == 0 || nextVcn != (long)high + 1 ||
                checked(nextVcn * geometry.ClusterBytes) != allocated)
                return Invalid("MftRunCoverage");
            if (runs[0].FirstLcn != geometry.MftStartLcn) return Invalid("MftBootstrapLcn");
            var map = new NtfsMftMap(geometry, runs, size, initialized, allocated);
            var first = map.PlanRecord(0, token);
            if (first.Count != 1 || first[0].VolumeOffset != bootstrap || first[0].RecordOffset != 0 || first[0].Length != geometry.RecordBytes)
                return Unsupported("MftBootstrapFragmented");
            return new("Mapped", "Mapped", map);
        }
        catch (OverflowException) { return Invalid("MftArithmeticOverflow"); }
    }

    internal IReadOnlyList<NtfsRawSlice> PlanRecord(ulong recordNumber, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (recordNumber > NtfsRecordDecoder.MaximumRecordNumber) throw new InvalidDataException("MftRecordNumber");
        try
        {
            long start = checked((long)recordNumber * Geometry.RecordBytes);
            long end = checked(start + Geometry.RecordBytes);
            if (end > Geometry.MftValidDataLength || end > InitializedBytes || end > DataBytes || end > AllocatedBytes)
                throw new InvalidDataException("MftRecordBounds");
            var slices = new List<NtfsRawSlice>();
            long cursor = start;
            int destination = 0;
            foreach (var run in Runs)
            {
                token.ThrowIfCancellationRequested();
                long runStart = checked(run.FirstVcn * Geometry.ClusterBytes);
                long runEnd = checked((run.FirstVcn + run.ClusterCount) * Geometry.ClusterBytes);
                if (cursor < runStart || cursor >= runEnd) continue;
                int count = checked((int)Math.Min(end - cursor, runEnd - cursor));
                long volumeOffset = checked(run.FirstLcn * Geometry.ClusterBytes + cursor - runStart);
                if (count <= 0 || volumeOffset < 0 || volumeOffset > Geometry.VolumeBytes - count ||
                    volumeOffset % Geometry.SectorBytes != 0 || count % Geometry.SectorBytes != 0)
                    throw new InvalidDataException("MftSliceBounds");
                slices.Add(new(volumeOffset, count, destination));
                cursor = checked(cursor + count); destination = checked(destination + count);
                if (cursor == end) break;
            }
            if (cursor != end || destination != Geometry.RecordBytes || slices.Count == 0 || slices.Count > MaxRuns)
                throw new InvalidDataException("MftRecordCoverage");
            return Array.AsReadOnly(slices.ToArray());
        }
        catch (OverflowException) { throw new InvalidDataException("MftArithmeticOverflow"); }
    }
}
