using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.ComponentModel;

namespace DiskAtlas;

public record JournalCheckpoint(ulong JournalId, long NextUsn, long FirstUsn);
public readonly record struct JournalChange(ulong IdLow, ulong IdHigh, ulong ParentLow, ulong ParentHigh, uint Reason, bool Directory);
public interface IChangeJournal : IDisposable
{
    JournalCheckpoint Capture();
    IReadOnlyList<JournalChange> Read(JournalCheckpoint from, JournalCheckpoint to, CancellationToken token);
}

internal sealed class NtfsChangeJournal(SafeFileHandle handle) : IChangeJournal
{
    public static IChangeJournal? TryOpen(string path)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path))!;
            if (root.Length != 3 || root[1] != ':' || !new DriveInfo(root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return null;
            var handle = NativeStorage.CreateFileW(@"\\.\" + root[..2], 0, NativeStorage.ShareAll, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            var journal = new NtfsChangeJournal(handle);
            try { _ = journal.Capture(); return journal; } catch { journal.Dispose(); return null; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public JournalCheckpoint Capture()
    {
        byte[] output = new byte[128];
        if (!NativeStorage.DeviceIoControl(handle, 0x000900f4, null, 0, output, output.Length, out int read, IntPtr.Zero))
            throw new IOException("无法读取 NTFS 变化日志：" + new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message);
        if (read < 56) throw new IOException("变化日志头不完整");
        return new(BinaryPrimitives.ReadUInt64LittleEndian(output), BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16)),
            Math.Max(BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8)), BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(24))));
    }

    public IReadOnlyList<JournalChange> Read(JournalCheckpoint from, JournalCheckpoint to, CancellationToken token)
    {
        if (from.JournalId != to.JournalId || from.NextUsn < to.FirstUsn || from.NextUsn > to.NextUsn)
            throw new IOException("变化日志已重建、截断或游标失效");
        var result = new List<JournalChange>();
        long cursor = from.NextUsn;
        byte[] input = new byte[40], output = new byte[64 * 1024];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), from.JournalId);
        while (cursor < to.NextUsn)
        {
            token.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteInt64LittleEndian(input, cursor);
            if (!NativeStorage.DeviceIoControl(handle, 0x000903ab, input, input.Length, output, output.Length, out int read, IntPtr.Zero) &&
                !NativeStorage.DeviceIoControl(handle, 0x000900bb, input, input.Length, output, output.Length, out read, IntPtr.Zero))
                throw new IOException("无法连续读取变化日志：" + new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message);
            if (read < 8) throw new IOException("变化日志缓冲不完整");
            long next = BinaryPrimitives.ReadInt64LittleEndian(output);
            Parse(output.AsSpan(8, read - 8), to.NextUsn, result);
            if (next <= cursor) throw new IOException("变化日志游标没有前进");
            cursor = next;
            if (result.Count > 200000) throw new IOException("变化过多，改用完整扫描");
        }
        var after = Capture();
        if (after.JournalId != to.JournalId || after.FirstUsn > from.NextUsn) throw new IOException("读取期间变化日志失效");
        return result;
    }

    internal static void Parse(ReadOnlySpan<byte> buffer, long endUsn, List<JournalChange> changes)
    {
        while (!buffer.IsEmpty)
        {
            if (buffer.Length < 8) throw new IOException("变化日志记录不完整");
            int length = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            int version = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]);
            int minimum = version == 2 ? 60 : version == 3 ? 76 : throw new IOException("不支持的变化日志记录版本");
            if (length < minimum || length > buffer.Length) throw new IOException("变化日志记录长度无效");
            var record = buffer[..length];
            int usnOffset = version == 2 ? 24 : 40, reasonOffset = version == 2 ? 40 : 56, attributesOffset = version == 2 ? 52 : 68;
            long usn = BinaryPrimitives.ReadInt64LittleEndian(record[usnOffset..]);
            if (usn < endUsn)
                changes.Add(new(BinaryPrimitives.ReadUInt64LittleEndian(record[8..]), version == 2 ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(record[16..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(record[(version == 2 ? 16 : 24)..]), version == 2 ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(record[32..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(record[reasonOffset..]), (BinaryPrimitives.ReadUInt32LittleEndian(record[attributesOffset..]) & 0x10) != 0));
            buffer = buffer[length..];
        }
    }
    public void Dispose() => handle.Dispose();
}
