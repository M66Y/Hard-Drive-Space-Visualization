using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiskAtlas;

public readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High);
public record FileStorageInfo(FileIdentity Identity, long AllocatedBytes, uint LinkCount, bool WritableHandleOpen, long LogicalBytes = -1, DateTime? Modified = null);
internal readonly record struct StorageSnapshot(FileIdentity Identity, long AllocatedBytes, uint LinkCount, bool WritableHandleOpen, long LogicalBytes, DateTime Modified)
{
    public FileStorageInfo ToInfo() => new(Identity, AllocatedBytes, LinkCount, WritableHandleOpen, LogicalBytes, Modified);
    public bool Matches(FileStorageInfo? info) => info != null && Identity == info.Identity && AllocatedBytes == info.AllocatedBytes &&
        LinkCount == info.LinkCount && WritableHandleOpen == info.WritableHandleOpen && LogicalBytes == info.LogicalBytes && Modified == info.Modified;
}

internal static class NativeStorage
{
    internal const uint ShareAll = 7;
    private const uint ReadAttributes = 0x80, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize, EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending, Directory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IdInfo { public ulong VolumeSerialNumber, Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CompressionInfo
    {
        public long CompressedFileSize;
        public ushort CompressionFormat;
        public byte CompressionUnitShift, ChunkShift, ClusterShift, Reserved1, Reserved2, Reserved3;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct LegacyInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool Standard(SafeFileHandle handle, int infoClass, out StandardInfo value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool Identity(SafeFileHandle handle, int infoClass, out IdInfo value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool Compression(SafeFileHandle handle, int infoClass, out CompressionInfo value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out LegacyInfo value);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    internal static string LongPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    public static bool TryRead(string path, bool directory, FileAttributes attributes, out FileStorageInfo? info, out string? error)
    {
        bool success = TryReadSnapshot(path, directory, attributes, out var snapshot, out error);
        info = success ? snapshot.ToInfo() : null; return success;
    }

    internal static bool TryReadSnapshot(string path, bool directory, FileAttributes attributes, out StorageSnapshot info, out string? error)
    {
        info = default; error = null;
        // Excluding write sharing detects already-open writers whose repeated changes may be coalesced in USN.
        bool writable = false;
        SafeFileHandle handle = CreateFileW(LongPath(path), directory ? ReadAttributes : ReadAttributes | 1u, directory ? ShareAll : 5, IntPtr.Zero, 3, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid && !directory && Marshal.GetLastWin32Error() is 32 or 5)
        {
            handle.Dispose(); writable = true;
            handle = CreateFileW(LongPath(path), ReadAttributes, ShareAll, IntPtr.Zero, 3, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        }
        using (handle)
        {
            if (handle.IsInvalid) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
            if (!GetFileInformationByHandle(handle, out var legacy)) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
            if ((legacy.Attributes & (uint)FileAttributes.ReparsePoint) != 0) { error = "读取期间项目变为重解析点"; return false; }
            FileIdentity id = Identity(handle, 18, out var identity, (uint)Marshal.SizeOf<IdInfo>())
                ? new(identity.VolumeSerialNumber, identity.Low, identity.High)
                : new(legacy.Volume, ((ulong)legacy.IndexHigh << 32) | legacy.IndexLow, 0);
            if (id.Low == 0 && id.High == 0) { error = "文件系统未提供可靠的文件标识"; return false; }
            long writeTime = ((long)legacy.Write.dwHighDateTime << 32) | (uint)legacy.Write.dwLowDateTime;
            var modified = DateTime.FromFileTimeUtc(writeTime).ToLocalTime();
            if (directory) { info = new(id, 0, legacy.Links, false, -1, modified); return true; }
            if (!Standard(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>())) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
            long allocated = standard.AllocationSize;
            if ((attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
            {
                if (!Compression(handle, 8, out var compression, (uint)Marshal.SizeOf<CompressionInfo>())) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
                allocated = compression.CompressedFileSize;
            }
            if (allocated < 0) { error = "文件系统返回的分配大小无效"; return false; }
            info = new(id, allocated, standard.NumberOfLinks, writable, standard.EndOfFile, modified);
            return true;
        }
    }

    public static DiskKind DetectDisk(string path)
    {
        try
        {
            string root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path))!;
            if (root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType == DriveType.Network) return DiskKind.Network;
            if (root.Length != 3 || root[1] != ':') return DiskKind.Unknown;
            using var handle = CreateFileW(@"\\.\" + root[..2], 0, ShareAll, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return DiskKind.Unknown;
            byte[] query = new byte[12]; BitConverter.GetBytes(7).CopyTo(query, 0);
            byte[] result = new byte[32];
            if (!DeviceIoControl(handle, 0x002d1400, query, query.Length, result, result.Length, out int read, IntPtr.Zero) || read < 9) return DiskKind.Unknown;
            return result[8] == 0 ? DiskKind.SolidState : DiskKind.Mechanical;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return DiskKind.Unknown; }
    }
}
