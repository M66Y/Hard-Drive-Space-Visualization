using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskAtlas;

public record JournalCheckpoint(ulong JournalId, long NextUsn, long FirstUsn, ulong? VolumeSerial = null);
public readonly record struct JournalChange(ulong IdLow, ulong IdHigh, ulong ParentLow, ulong ParentHigh, uint Reason, bool Directory);
public interface IChangeJournal : IDisposable
{
    JournalCheckpoint Capture();
    IReadOnlyList<JournalChange> Read(JournalCheckpoint from, JournalCheckpoint to, CancellationToken token);
}

internal sealed class NtfsChangeJournal : IChangeJournal
{
    internal const uint QueryControl = 0x900f4, UnprivilegedReadControl = 0x903ab, ReadControl = 0x900bb;
    internal const int ChangeLimit = 200000;
    private readonly SafeFileHandle? handle;
    private readonly JournalControl control;
    private readonly ulong? volumeSerial;
    private (uint Code, bool V1)? reader;

    private NtfsChangeJournal(SafeFileHandle handle, ulong volumeSerial)
    {
        this.handle = handle; this.volumeSerial = volumeSerial;
        control = (code, input, output) =>
        {
            bool ok = NativeStorage.DeviceIoControl(handle, code, input, input?.Length ?? 0, output, output.Length, out int bytes, IntPtr.Zero);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            return new(ok, bytes, error);
        };
    }
    // Deterministic protocol tests exercise the same decoder/negotiation without system changes.
    internal NtfsChangeJournal(JournalControl control, ulong? volumeSerial = null) { this.control = control; this.volumeSerial = volumeSerial; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNameW(string path, StringBuilder volume, int size);

    public static IChangeJournal? TryOpen(string path) => Open(path).Journal;
    internal static JournalOpenResult Open(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            string root = Path.GetPathRoot(path)!;
            if (root.Length != 3 || root[1] != ':') return new(null, new(JournalState.UnsupportedPath));
            var drive = new DriveInfo(root);
            if (drive.DriveType == DriveType.Network || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                return new(null, new(JournalState.NotNtfs));
            var mount = new StringBuilder(Math.Max(512, path.Length + 2));
            if (!GetVolumePathNameW(path, mount, mount.Capacity)) return new(null, new(JournalState.VolumeUnknown, Marshal.GetLastWin32Error()));
            // A path below another mounted volume must not consume the containing drive's journal.
            if (!root.Equals(mount.ToString(), StringComparison.OrdinalIgnoreCase)) return new(null, new(JournalState.UnsupportedPath));
            if (!NativeStorage.TryRead(root, true, FileAttributes.Directory, out var rootStorage, out _) || rootStorage == null)
                return new(null, new(JournalState.VolumeUnknown));
            JournalStatus? firstFailure = null;
            foreach (uint access in new uint[] { 0, 0x80000000 })
            {
                // Same process identity, metadata-only first, then read-only. Never request write access or elevation.
                var handle = NativeStorage.CreateFileW(@"\\.\" + root[..2], access, NativeStorage.ShareAll, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error(); handle.Dispose();
                    return new(null, firstFailure == null ? JournalStatus.FromNative(error) : firstFailure with { ReadOnlyOpenError = error });
                }
                var journal = new NtfsChangeJournal(handle, rootStorage.Identity.Volume);
                try { return new(journal, new(JournalState.Available), journal.Capture()); }
                catch (JournalException ex)
                {
                    journal.Dispose(); firstFailure ??= ex.Status;
                    if (access != 0 || ex.Status.State is JournalState.NotActive or JournalState.InvalidData)
                        return new(null, ex.Status);
                }
            }
            return new(null, firstFailure ?? new(JournalState.Unavailable));
        }
        catch (UnauthorizedAccessException) { return new(null, new(JournalState.AccessDenied)); }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.Security.SecurityException)
        { return new(null, new(JournalState.VolumeUnknown)); }
    }

    public JournalCheckpoint Capture()
    {
        byte[] output = new byte[128]; var result = control(QueryControl, null, output);
        if (!result.Success) throw new JournalException(JournalStatus.FromNative(result.Error));
        if (result.Bytes < 56 || result.Bytes > output.Length) throw InvalidData();
        long next = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16));
        long first = Math.Max(BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8)), BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(24)));
        if (first < 0 || next < first) throw InvalidData();
        return new(BinaryPrimitives.ReadUInt64LittleEndian(output), next, first, volumeSerial);
    }

    internal static byte[] CreateReadInput(long cursor, ulong journalId, bool v1)
    {
        var input = new byte[v1 ? 48 : 40];
        BinaryPrimitives.WriteInt64LittleEndian(input, cursor);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), journalId);
        if (v1)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(40), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(42), 3);
        }
        return input;
    }

    private JournalIoResult ReadChunk(long cursor, ulong journalId, byte[] output)
    {
        if (reader is { } known)
        {
            var result = control(known.Code, CreateReadInput(cursor, journalId, known.V1), output);
            if (!result.Success) throw new JournalException(JournalStatus.FromNative(result.Error, true));
            return result;
        }
        JournalIoResult last = default;
        foreach (var candidate in new[] { (UnprivilegedReadControl, true), (UnprivilegedReadControl, false), (ReadControl, true), (ReadControl, false) })
        {
            last = control(candidate.Item1, CreateReadInput(cursor, journalId, candidate.Item2), output);
            if (last.Success) { reader = candidate; return last; }
            if (last.Error is not (1 or 5 or 50 or 87)) break;
        }
        throw new JournalException(JournalStatus.FromNative(last.Error, true));
    }

    public IReadOnlyList<JournalChange> Read(JournalCheckpoint from, JournalCheckpoint to, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (from.JournalId != to.JournalId || from.NextUsn < to.FirstUsn || from.NextUsn > to.NextUsn || from.NextUsn < 0 ||
            from.VolumeSerial != to.VolumeSerial)
            throw new JournalException(new(JournalState.Discontinuous));
        var changes = new List<JournalChange>(); long cursor = from.NextUsn;
        byte[] output = new byte[64 * 1024];
        while (cursor < to.NextUsn)
        {
            token.ThrowIfCancellationRequested();
            var result = ReadChunk(cursor, from.JournalId, output);
            if (result.Bytes < 8 || result.Bytes > output.Length) throw InvalidData();
            long next = BinaryPrimitives.ReadInt64LittleEndian(output);
            if (next <= cursor) throw InvalidData();
            Parse(output.AsSpan(8, result.Bytes - 8), to.NextUsn, changes, cursor, next);
            cursor = next;
        }
        token.ThrowIfCancellationRequested();
        var after = Capture();
        if (after.JournalId != to.JournalId || after.FirstUsn > from.NextUsn || after.NextUsn < to.NextUsn || after.VolumeSerial != to.VolumeSerial)
            throw new JournalException(new(JournalState.Discontinuous));
        return changes;
    }

    private static JournalException InvalidData() => new(new(JournalState.InvalidData));
    internal static void Parse(ReadOnlySpan<byte> buffer, long endUsn, List<JournalChange> changes, long startUsn = 0, long chunkEnd = long.MaxValue)
    {
        long previousUsn = -1;
        while (!buffer.IsEmpty)
        {
            if (buffer.Length < 8) throw InvalidData();
            int length = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            int version = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]);
            int minimum = version == 2 ? 60 : version == 3 ? 76 : throw InvalidData();
            if (length < minimum || length > buffer.Length || (length & 7) != 0) throw InvalidData();
            var record = buffer[..length];
            int usnOffset = version == 2 ? 24 : 40, reasonOffset = version == 2 ? 40 : 56, attributesOffset = version == 2 ? 52 : 68;
            long usn = BinaryPrimitives.ReadInt64LittleEndian(record[usnOffset..]);
            if (usn < 0 || usn < previousUsn || usn >= chunkEnd) throw InvalidData();
            previousUsn = usn;
            if (usn >= startUsn && usn < endUsn)
            {
                if (changes.Count >= ChangeLimit) throw new JournalException(new(JournalState.TooManyChanges));
                changes.Add(new(BinaryPrimitives.ReadUInt64LittleEndian(record[8..]), version == 2 ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(record[16..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(record[(version == 2 ? 16 : 24)..]), version == 2 ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(record[32..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(record[reasonOffset..]), (BinaryPrimitives.ReadUInt32LittleEndian(record[attributesOffset..]) & 0x10) != 0));
            }
            buffer = buffer[length..];
        }
    }
    public void Dispose() => handle?.Dispose();
}
