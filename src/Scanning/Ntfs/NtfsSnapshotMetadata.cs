using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal static class NtfsSnapshotMetadata
{
    // These calls query metadata only. Directory enumeration has its own
    // FILE_LIST_DIRECTORY handle; size lookup never requests FILE_READ_DATA.
    internal const uint MetadataReadAccess = 0x80;
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { internal long Creation, Access, Write, Change; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo { internal long Allocation, End; internal uint Links; internal byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] private struct IdInfo { internal ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct CompressionInfo
    { internal long Size; internal ushort Format; internal byte Unit, Chunk, Cluster, Reserved1, Reserved2, Reserved3; }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool Basic(SafeFileHandle handle, int kind, out BasicInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool Standard(SafeFileHandle handle, int kind, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool Identity(SafeFileHandle handle, int kind, out IdInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool Compression(SafeFileHandle handle, int kind, out CompressionInfo info, uint size);
    internal static NtfsSnapshotNativeSize Read(string path, bool directory, ulong serial, CancellationToken token)
    {
        using var handle = NativeStorage.CreateFileW(path, MetadataReadAccess, NativeStorage.ShareAll, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        NtfsVssNativeSession.Require(!handle.IsInvalid, "VssSnapshotNative", Marshal.GetLastWin32Error()); token.ThrowIfCancellationRequested();
        return ReadHandle(handle, directory, serial, token);
    }
    internal static NtfsSnapshotNativeSize ReadHandle(SafeFileHandle handle, bool directory, ulong? serial, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        BasicInfo basic = default; StandardInfo standard = default; IdInfo identity = default;
        NtfsVssNativeSession.Require(Basic(handle, 0, out basic, (uint)Marshal.SizeOf<BasicInfo>()) && Standard(handle, 1, out standard, (uint)Marshal.SizeOf<StandardInfo>()) &&
            Identity(handle, 18, out identity, (uint)Marshal.SizeOf<IdInfo>()), "VssSnapshotNative", Marshal.GetLastWin32Error());
        NtfsVssNativeSession.Require((basic.Attributes & (uint)FileAttributes.ReparsePoint) == 0 && ((basic.Attributes & (uint)FileAttributes.Directory) != 0) == directory &&
            (standard.Directory != 0) == directory && standard.DeletePending == 0 && (!serial.HasValue || identity.Volume == serial.Value) && identity.High == 0 && identity.Low >> 48 != 0 && standard.Links > 0,
            "VssSnapshotNative");
        long allocated = directory ? 0 : standard.Allocation;
        if (!directory && ((FileAttributes)basic.Attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
        {
            NtfsVssNativeSession.Require(Compression(handle, 8, out var compression, (uint)Marshal.SizeOf<CompressionInfo>()), "VssSnapshotNative", Marshal.GetLastWin32Error());
            allocated = compression.Size;
        }
        NtfsVssNativeSession.Require(allocated >= 0 && (directory || standard.End >= 0), "VssSnapshotNative");
        var storage = new FileStorageInfo(new(identity.Volume, identity.Low, identity.High), allocated, standard.Links, false,
            directory ? -1 : standard.End, DateTime.FromFileTimeUtc(basic.Write).ToLocalTime());
        token.ThrowIfCancellationRequested(); return new(storage, directory, standard.DeletePending != 0);
    }
}
