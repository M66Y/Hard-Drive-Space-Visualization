using System.Buffers.Binary;
using System.Text;

namespace DiskAtlas.NtfsPrototype;

// Deliberately tiny, deterministic test records. Never reads a volume or a file.
internal static class NtfsRecordFixtures
{
    internal const int RecordLength = 1024;
    internal const int FirstAttribute = 56;
    internal const ulong Parent = (7UL << 48) | 5;
    internal static ulong Reference(ulong slot, ushort sequence) => ((ulong)sequence << 48) | slot;
    internal static int Align(int value) => (value + 7) & ~7;

    internal static byte[] Resident(uint type, byte[] value, string streamName = "", ushort flags = 0)
    {
        byte[] name = Encoding.Unicode.GetBytes(streamName);
        int valueOffset = Align(24 + name.Length);
        var attribute = new byte[Align(valueOffset + value.Length)];
        U32(attribute, 0, type); U32(attribute, 4, (uint)attribute.Length);
        attribute[9] = checked((byte)streamName.Length);
        U16(attribute, 10, streamName.Length == 0 ? (ushort)0 : (ushort)24);
        U16(attribute, 12, flags); U32(attribute, 16, (uint)value.Length); U16(attribute, 20, (ushort)valueOffset);
        name.CopyTo(attribute, 24); value.CopyTo(attribute, valueOffset);
        return attribute;
    }

    internal static byte[] Name(string name = "demo.bin", byte nameSpace = 1, ulong parent = Parent)
    {
        var value = new byte[66 + name.Length * 2];
        U64(value, 0, parent);
        // Deliberately misleading FILE_NAME size copies: DATA must win.
        U64(value, 40, 987654321); U64(value, 48, 123456789);
        value[64] = checked((byte)name.Length); value[65] = nameSpace;
        Encoding.Unicode.GetBytes(name).CopyTo(value, 66);
        return Resident(0x30, value);
    }

    internal static byte[] Data(int size = 17, string streamName = "") => Resident(0x80, new byte[size], streamName);

    internal static byte[] Nonresident(long logical = 6000, long allocated = 8192, ushort flags = 0, string streamName = "")
    {
        int fixedLength = (flags & 0x8001) != 0 ? 72 : 64;
        var name = Encoding.Unicode.GetBytes(streamName);
        int mappings = Align(fixedLength + name.Length);
        var attribute = new byte[Align(mappings + 4)];
        U32(attribute, 0, 0x80); U32(attribute, 4, (uint)attribute.Length); attribute[8] = 1;
        attribute[9] = checked((byte)streamName.Length);
        U16(attribute, 10, streamName.Length == 0 ? (ushort)0 : (ushort)fixedLength);
        U16(attribute, 12, flags); U64(attribute, 16, 0); U64(attribute, 24, 1);
        U16(attribute, 32, (ushort)mappings); U16(attribute, 34, (flags & 1) != 0 ? (ushort)4 : (ushort)0);
        U64(attribute, 40, unchecked((ulong)allocated)); U64(attribute, 48, unchecked((ulong)logical));
        U64(attribute, 56, unchecked((ulong)logical));
        if (fixedLength == 72) U64(attribute, 64, unchecked((ulong)allocated));
        name.CopyTo(attribute, fixedLength);
        attribute[mappings] = 0x11; attribute[mappings + 1] = 2; attribute[mappings + 2] = 10;
        return attribute;
    }

    internal static byte[] Record(IEnumerable<byte[]> attributes, ushort sequence = 3, ushort links = 1,
        bool directory = false, bool inUse = true, ulong baseReference = 0, bool addStandard = true)
    {
        var record = new byte[RecordLength];
        Encoding.ASCII.GetBytes("FILE").CopyTo(record, 0);
        U16(record, 4, 48); U16(record, 6, 3); U16(record, 16, sequence); U16(record, 18, links);
        U16(record, 20, FirstAttribute); U16(record, 22, (ushort)((inUse ? 1 : 0) | (directory ? 2 : 0)));
        U32(record, 28, RecordLength); U64(record, 32, baseReference);
        int offset = FirstAttribute; ushort instance = 0;
        var items = attributes.ToList();
        if (addStandard && baseReference == 0)
            items.Add(Resident(0x10, new byte[48]));
        foreach (byte[] item in items)
        {
            if (offset + item.Length + 4 > record.Length) throw new InvalidOperationException("Synthetic fixture too large.");
            item.CopyTo(record, offset); U16(record, offset + 14, instance++); offset += item.Length;
        }
        U32(record, offset, uint.MaxValue); U32(record, 24, (uint)(offset + 4));
        Protect(record);
        return record;
    }

    internal static byte[] File(params byte[][] attributes) => Record(attributes.Length == 0 ? [Name(), Data()] : attributes);

    internal static void Protect(byte[] record)
    {
        U16(record, 48, 0xa55a);
        U16(record, 50, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)));
        U16(record, 52, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022)));
        U16(record, 510, 0xa55a); U16(record, 1022, 0xa55a);
    }
    internal static void U16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    internal static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    internal static void U64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
}
