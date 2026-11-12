using System.Buffers.Binary;
using System.Text;

namespace DiskAtlas.NtfsPrototype;

// Offline prototype only. No handles, disk I/O, UI, or production scanner references.
internal enum NtfsDecodeStatus { Decoded, Free, Invalid, Unsupported }
[Flags]
internal enum NtfsLookupReason
{
    None = 0, AttributeList = 1, ExtensionRecord = 2, Compressed = 4, Sparse = 8,
    ReparsePoint = 16, NamedData = 32, Encrypted = 64, MissingData = 128,
    LinkNamesIncomplete = 256, MissingStandardInformation = 512, ResidentAllocation = 1024
}
internal sealed record NtfsName(ulong ParentReference, string Name, byte Namespace);
internal sealed record NtfsRecord(ulong Reference, ulong BaseReference, bool IsDirectory, ushort LinkCount,
    IReadOnlyList<NtfsName> Names, long? LogicalBytes, long? AllocatedBytes, NtfsLookupReason LookupReasons);
internal sealed record NtfsDecodeResult(NtfsDecodeStatus Status, NtfsRecord? Record, string Code);

internal static class NtfsRecordDecoder
{
    private static readonly UnicodeEncoding Unicode = new(false, false, true);
    internal const int MaximumRecordBytes = 65536;
    internal const ulong MaximumRecordNumber = 0x0000ffffffffffff;
    private static ushort U16(ReadOnlySpan<byte> s, int p) => BinaryPrimitives.ReadUInt16LittleEndian(s[p..]);
    private static uint U32(ReadOnlySpan<byte> s, int p) => BinaryPrimitives.ReadUInt32LittleEndian(s[p..]);
    private static ulong U64(ReadOnlySpan<byte> s, int p) => BinaryPrimitives.ReadUInt64LittleEndian(s[p..]);
    private static long I64(ReadOnlySpan<byte> s, int p) => BinaryPrimitives.ReadInt64LittleEndian(s[p..]);
    private static bool Fits(int offset, long count, int length) => offset >= 0 && count >= 0 && offset <= length && count <= length - offset;
    private static NtfsDecodeResult Invalid(string code) => new(NtfsDecodeStatus.Invalid, null, code);
    private static NtfsDecodeResult Unsupported(string code) => new(NtfsDecodeStatus.Unsupported, null, code);

    internal static NtfsDecodeResult Decode(ReadOnlySpan<byte> source, int sectorSize, ushort ntfsMajor = 3,
        ushort ntfsMinor = 1, CancellationToken token = default, ulong recordNumber = 0, int clusterSize = 4096)
        => DecodeCore(source, sectorSize, ntfsMajor, ntfsMinor, token, recordNumber, clusterSize, false);

    // Only for records returned by a successful, identity-checked kernel IOCTL.
    // The public IOCTL contract does not specify USA representation. Never use
    // this as a fallback after a raw disk record fails its integrity check.
    internal static NtfsDecodeResult DecodeKernelReturned(ReadOnlySpan<byte> source, int sectorSize,
        ulong recordNumber, int clusterSize, ushort ntfsMajor, ushort ntfsMinor, CancellationToken token = default)
        => DecodeCore(source, sectorSize, ntfsMajor, ntfsMinor, token, recordNumber, clusterSize, true);

    private static NtfsDecodeResult DecodeCore(ReadOnlySpan<byte> source, int sectorSize, ushort ntfsMajor,
        ushort ntfsMinor, CancellationToken token, ulong recordNumber, int clusterSize, bool kernelReturned)
    {
        token.ThrowIfCancellationRequested();
        // The argument describes the update-sequence stride, not physical disk sector size.
        if (ntfsMajor != 3 || ntfsMinor != 1) return Unsupported("NtfsVersion");
        if (sectorSize != 512) return Unsupported("FixupStride");
        // 4096 is the synthetic fixture default. Any future reader must pass verified volume geometry.
        if (clusterSize is < 512 or > 65536 || (clusterSize & (clusterSize - 1)) != 0) return Unsupported("ClusterGeometry");
        if (recordNumber > MaximumRecordNumber) return Invalid("RecordNumber");
        if (source.Length < 1024 || source.Length > MaximumRecordBytes || source.Length % sectorSize != 0) return Invalid("RecordLength");
        if (!source[..4].SequenceEqual("FILE"u8)) return Invalid("Signature");
        int usa = U16(source, 4), usaCount = U16(source, 6), attributes = U16(source, 20);
        uint used = U32(source, 24), allocated = U32(source, 28);
        if (usaCount != source.Length / sectorSize + 1 || usa < 48 || (usa & 1) != 0 ||
            !Fits(usa, usaCount * 2, Math.Min(sectorSize - 2, source.Length)) ||
            attributes < usa + usaCount * 2 || (attributes & 7) != 0 ||
            allocated != source.Length || used > allocated || !Fits(attributes, 4, (int)used)) return Invalid("RecordHeader");
        byte[] buffer = source.ToArray(); // At most 64 KiB, never mutate caller's bytes.
        ushort marker = U16(buffer, usa);
        bool protectedTails = true, restoredTails = true;
        for (int i = 1; i < usaCount; i++)
        {
            protectedTails &= U16(buffer, i * sectorSize - 2) == marker;
            restoredTails &= U16(buffer, i * sectorSize - 2) == U16(buffer, usa + i * 2);
        }
        if (!protectedTails && (!kernelReturned || !restoredTails)) return Invalid("FixupMismatch");
        for (int i = 1; i < usaCount; i++)
        {
            token.ThrowIfCancellationRequested();
            int tail = i * sectorSize - 2;
            if (protectedTails) BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(tail), U16(buffer, usa + i * 2));
        }
        ReadOnlySpan<byte> record = buffer.AsSpan(0, (int)used);
        ushort flags = U16(record, 22), sequence = U16(record, 16), links = U16(record, 18);
        if ((flags & ~3) != 0) return Unsupported("RecordFlags");
        if ((flags & 1) == 0) return new(NtfsDecodeStatus.Free, null, "FreeRecord");
        if (sequence == 0) return Invalid("Sequence");
        bool directory = (flags & 2) != 0;
        ulong reference = recordNumber | ((ulong)sequence << 48), baseReference = U64(record, 32);
        var reasons = baseReference != 0 ? NtfsLookupReason.ExtensionRecord : NtfsLookupReason.None;
        var names = new List<NtfsName>();
        var ids = new HashSet<ushort>();
        long? logical = null, allocation = null;
        bool sawData = false, sawStandard = false, sawEnd = false, geometryMismatch = false;
        for (int offset = attributes, count = 0; offset < record.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (!Fits(offset, 4, record.Length)) return Invalid("AttributeHeader");
            uint type = U32(record, offset);
            if (type == uint.MaxValue) { sawEnd = true; break; }
            if (++count > 1024) return Unsupported("AttributeLimit");
            if (!Fits(offset, 24, record.Length)) return Invalid("AttributeHeader");
            uint size = U32(record, offset + 4);
            if (size < 24 || size % 8 != 0 || !Fits(offset, size, record.Length)) return Invalid("AttributeLength");
            ReadOnlySpan<byte> attr = record.Slice(offset, (int)size);
            byte form = attr[8], nameLength = attr[9];
            ushort nameOffset = U16(attr, 10), attrFlags = U16(attr, 12);
            if (!ids.Add(U16(attr, 14))) return Invalid("DuplicateAttributeId");
            if (form > 1) return Unsupported("AttributeForm");
            if ((attrFlags & ~0xc001) != 0) return Unsupported("AttributeFlags");
            if (type is 0x10 or 0x30 && attrFlags != 0) return Unsupported("MetadataFlags");
            int minimum = form == 0 ? 24 : (attrFlags & 0x8001) != 0 ? 72 : 64;
            if (attr.Length < minimum || (nameLength != 0 && (nameOffset < minimum || !Fits(nameOffset, nameLength * 2, attr.Length)))) return Invalid("AttributeName");
            if (nameLength != 0 && !TryText(attr.Slice(nameOffset, nameLength * 2), out _)) return Invalid("AttributeName");
            ReadOnlySpan<byte> value = default;
            if (form == 0)
            {
                int valueOffset = U16(attr, 20); uint valueLength = U32(attr, 16);
                if (valueOffset < minimum || !Fits(valueOffset, valueLength, attr.Length) ||
                    (nameLength > 0 && valueOffset < nameOffset + nameLength * 2)) return Invalid("ResidentBounds");
                value = attr.Slice(valueOffset, (int)valueLength);
            }
            else
            {
                int mappings = U16(attr, 32);
                if (mappings < minimum || mappings >= attr.Length || (nameLength > 0 && mappings < nameOffset + nameLength * 2)) return Invalid("MappingBounds");
                if (!ValidMappings(attr[mappings..], U64(attr, 16), U64(attr, 24), token, out bool sparseRun, out ulong physicalClusters)) return Invalid("MappingPairs");
                if (type == 0x80 && sparseRun && (attrFlags & 0x8001) == 0) return Invalid("SparseMappingFlags");
                if (type == 0x80 && nameLength == 0 && U64(attr, 16) == 0 && (attrFlags & 0xc001) == 0 &&
                    (physicalClusters > (ulong)(long.MaxValue / clusterSize) ||
                    I64(attr, 40) != (long)physicalClusters * clusterSize)) geometryMismatch = true;
            }
            if (type == 0x10)
            {
                if (sawStandard || form != 0 || nameLength != 0 || value.Length < 48) return Invalid("StandardInformation");
                sawStandard = true; reasons |= FileFlags(U32(value, 32));
            }
            else if (type == 0x20) reasons |= NtfsLookupReason.AttributeList;
            else if (type == 0x30)
            {
                if (form != 0 || nameLength != 0 || value.Length < 66 || value[64] == 0 || value[65] > 3 ||
                    value.Length != 66 + value[64] * 2 || !TryText(value[66..], out var name)) return Invalid("FileName");
                if (names.Count >= 256) return Unsupported("NameLimit");
                names.Add(new(U64(value, 0), name!, value[65]));
                reasons |= FileFlags(U32(value, 56));
            }
            else if (type == 0x80)
            {
                if (nameLength != 0) reasons |= NtfsLookupReason.NamedData;
                else
                {
                    if (sawData) return Unsupported("MultipleUnnamedData");
                    sawData = true;
                    if ((attrFlags & 1) != 0) reasons |= NtfsLookupReason.Compressed;
                    if ((attrFlags & 0x8000) != 0) reasons |= NtfsLookupReason.Sparse;
                    if ((attrFlags & 0x4000) != 0) reasons |= NtfsLookupReason.Encrypted;
                    if (form == 0)
                    {
                        logical = value.Length;
                        allocation = value.Length == 0 ? 0 : null;
                        if (value.Length != 0) reasons |= NtfsLookupReason.ResidentAllocation;
                    }
                    else
                    {
                        long allocatedBytes = I64(attr, 40), dataBytes = I64(attr, 48), initialized = I64(attr, 56);
                        if (allocatedBytes < 0 || dataBytes < 0 || initialized < 0 || initialized > dataBytes) return Invalid("DataSize");
                        if (U64(attr, 16) != 0) reasons |= NtfsLookupReason.AttributeList;
                        logical = dataBytes; allocation = allocatedBytes;
                        if ((attrFlags & 0xc001) == 0 && allocatedBytes < dataBytes) return Invalid("DataSize");
                    }
                }
            }
            else if (type == 0xc0) reasons |= NtfsLookupReason.ReparsePoint;
            else if (type is not (0x40 or 0x50 or 0x60 or 0x70 or 0x90 or 0xa0 or 0xb0 or 0xd0 or 0xe0 or 0xf0 or 0x100))
                return Unsupported("AttributeType");
            // Known unrelated attributes never supply unnamed file-data sizes.
            offset += (int)size;
        }
        if (!sawEnd) return Invalid("MissingEndMarker");
        if (geometryMismatch && (reasons & (NtfsLookupReason.AttributeList | NtfsLookupReason.ExtensionRecord)) == 0) return Invalid("AllocationGeometry");
        if (!sawStandard && baseReference == 0) reasons |= NtfsLookupReason.MissingStandardInformation;
        // Raw name-reference counts include separate DOS FILE_NAME attributes.
        if (!directory && baseReference == 0 && (links == 0 || names.Count != links)) reasons |= NtfsLookupReason.LinkNamesIncomplete;
        if (directory) { logical = 0; allocation = 0; }
        else if (!sawData) reasons |= NtfsLookupReason.MissingData;
        if ((reasons & (NtfsLookupReason.AttributeList | NtfsLookupReason.ExtensionRecord | NtfsLookupReason.MissingData |
            NtfsLookupReason.MissingStandardInformation | NtfsLookupReason.ReparsePoint | NtfsLookupReason.Encrypted)) != 0) logical = allocation = null;
        if ((reasons & (NtfsLookupReason.Compressed | NtfsLookupReason.Sparse)) != 0) allocation = null;
        token.ThrowIfCancellationRequested();
        return new(NtfsDecodeStatus.Decoded, new(reference, baseReference, directory, links, names.AsReadOnly(), logical, allocation, reasons), "Decoded");
    }

    private static NtfsLookupReason FileFlags(uint flags) =>
        ((flags & 0x400) != 0 ? NtfsLookupReason.ReparsePoint : 0) |
        ((flags & 0x800) != 0 ? NtfsLookupReason.Compressed : 0) |
        ((flags & 0x200) != 0 ? NtfsLookupReason.Sparse : 0) |
        ((flags & 0x4000) != 0 ? NtfsLookupReason.Encrypted : 0);

    private static bool TryText(ReadOnlySpan<byte> bytes, out string? text)
    {
        text = null;
        try { text = Unicode.GetString(bytes); }
        catch (DecoderFallbackException) { return false; }
        return text.Length > 0 && text.IndexOfAny(['\0', '\\', '/', ':']) < 0;
    }

    private static bool ValidMappings(ReadOnlySpan<byte> bytes, ulong first, ulong last, CancellationToken token,
        out bool sparseRun, out ulong physicalClusters)
    {
        sparseRun = false;
        physicalClusters = 0;
        bool empty = first == 0 && last == ulong.MaxValue;
        if (first > long.MaxValue || (!empty && (last > long.MaxValue || last < first || last - first >= long.MaxValue))) return false;
        ulong clusters = 0; long lcn = 0;
        for (int p = 0; p < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            byte header = bytes[p++];
            if (header == 0) return empty ? clusters == 0 : clusters == last - first + 1;
            int lengthBytes = header & 15, offsetBytes = header >> 4;
            if (lengthBytes is < 1 or > 8 || offsetBytes > 8 || !Fits(p, lengthBytes + offsetBytes, bytes.Length)) return false;
            ulong length = 0, rawOffset = 0;
            for (int i = 0; i < lengthBytes; i++) length |= (ulong)bytes[p++] << (i * 8);
            if (length == 0 || length > ulong.MaxValue - clusters) return false;
            clusters += length;
            if (offsetBytes != 0)
            {
                for (int i = 0; i < offsetBytes; i++) rawOffset |= (ulong)bytes[p++] << (i * 8);
                if (offsetBytes < 8 && (rawOffset & (1UL << (offsetBytes * 8 - 1))) != 0) rawOffset |= ulong.MaxValue << (offsetBytes * 8);
                long relative = unchecked((long)rawOffset);
                if ((relative > 0 && lcn > long.MaxValue - relative) || (relative < 0 && lcn < long.MinValue - relative)) return false;
                lcn += relative;
                if (lcn < 0 || length - 1 > (ulong)(long.MaxValue - lcn)) return false;
                if (length > ulong.MaxValue - physicalClusters) return false;
                physicalClusters += length;
            }
            else sparseRun = true;
        }
        return false;
    }
}
