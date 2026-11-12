using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsLeaseResult(bool Success, FileStorageInfo? Storage, string Code, int HandlesOpened, int SnapshotReads);

// Prototype-only size lookup. A single read handle denies data-write/delete sharing
// while two snapshots are read. This is not a volume snapshot or residency detector.
internal static class NtfsResidentLease
{
    private const uint ReadAttributesAndData = 0x81, ShareRead = 1;
    private const uint BackupAndOpenReparse = 0x02000000 | 0x00200000;
    private const uint UnsupportedAttributes = 0x10 | 0x200 | 0x400 | 0x800 | 0x1000 | 0x4000 | 0x40000 | 0x400000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IdInfo { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long Allocation, EndOfFile;
        public uint Links;
        public byte DeletePending, Directory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct LegacyInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    private sealed record Observation(FileStorageInfo Storage, uint Attributes);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool Identity(SafeFileHandle handle, int infoClass, out IdInfo value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool Standard(SafeFileHandle handle, int infoClass, out StandardInfo value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out LegacyInfo value);

    internal static NtfsLeaseResult Read(ulong volume, NtfsRecord record, string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        int opened = 0, reads = 0;
        NtfsLeaseResult Fail(string code) => new(false, null, code, opened, reads);
        if (record.IsDirectory || record.BaseReference != 0 || (record.Reference >> 48) == 0 ||
            record.LookupReasons != NtfsLookupReason.ResidentAllocation || record.LogicalBytes is not >= 0 || record.AllocatedBytes != null ||
            record.Names.Count is < 1 or > 256 || record.LinkCount != record.Names.Count ||
            record.Names.Any(name => name.Namespace > 3 || (name.ParentReference >> 48) == 0 ||
                name.Name is not { Length: > 0 and <= 255 } || name.Name.IndexOfAny(['\0', '\\', '/', ':']) >= 0))
            return Fail("UnsupportedObject");
        int visibleNames = record.Names.Where(name => name.Namespace != 2).Select(name => (name.ParentReference, name.Name)).Distinct().Count();
        if (visibleNames == 0) return Fail("MissingVisibleNames");
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path)) return Fail("InvalidPath");
        if (!record.Names.Any(name => name.Namespace != 2 && name.Name == Path.GetFileName(path))) return Fail("NameMismatch");
        if (!OperatingSystem.IsWindows()) return Fail("UnsupportedPlatform");

        using var handle = NativeStorage.CreateFileW(NativeStorage.LongPath(path), ReadAttributesAndData, ShareRead,
            IntPtr.Zero, 3, BackupAndOpenReparse, IntPtr.Zero);
        if (handle.IsInvalid) return Fail("OpenError-" + Marshal.GetLastWin32Error());
        opened = 1;
        token.ThrowIfCancellationRequested();

        Observation? Capture(out string error)
        {
            token.ThrowIfCancellationRequested();
            reads++;
            error = "NativeReadFailed";
            if (!Identity(handle, 18, out var id, (uint)Marshal.SizeOf<IdInfo>()) ||
                !Standard(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>()) ||
                !GetFileInformationByHandle(handle, out var legacy)) return null;
            if ((legacy.Attributes & UnsupportedAttributes) != 0 || standard.Directory != 0 || standard.DeletePending != 0)
            { error = "UnsupportedAttributes"; return null; }
            if (new FileIdentity(id.Volume, id.Low, id.High) != new FileIdentity(volume, record.Reference, 0))
            { error = "IdentityChanged"; return null; }
            if (standard.EndOfFile != record.LogicalBytes) { error = "LogicalChanged"; return null; }
            if (standard.Allocation < 0 || standard.Links != visibleNames || legacy.Links != standard.Links ||
                (((ulong)legacy.SizeHigh << 32) | legacy.SizeLow) != (ulong)standard.EndOfFile)
            { error = "InvalidSnapshot"; return null; }
            long write = ((long)legacy.Write.dwHighDateTime << 32) | (uint)legacy.Write.dwLowDateTime;
            DateTime modified;
            try { modified = DateTime.FromFileTimeUtc(write).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { error = "InvalidTimestamp"; return null; }
            token.ThrowIfCancellationRequested();
            error = "None";
            return new(new(new(id.Volume, id.Low, id.High), standard.Allocation, standard.Links, false, standard.EndOfFile, modified), legacy.Attributes);
        }

        var first = Capture(out string code);
        if (first == null) return Fail(code);
        var second = Capture(out code);
        if (second == null) return Fail(code);
        if (first.Storage != second.Storage || first.Attributes != second.Attributes) return Fail("SnapshotChanged");
        token.ThrowIfCancellationRequested();
        return new(true, first.Storage, "ResidentLease", opened, reads);
    }
}
