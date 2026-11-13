using System.Runtime.InteropServices;
using DiskAtlas.NtfsPrototype;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas;

// Test-only, READ_ATTRIBUTES handle. Values stay in memory and are never part
// of the wire report. The caller first validates this path through its owner.
internal sealed record NtfsSameSnapshotNativeObservation(FileIdentity Identity, bool Directory, long Logical,
    long Allocated, long StandardAllocation, uint Links, bool Compressed, bool Sparse, bool Consistent)
{
    internal NtfsSameSnapshotProbeBranch Branch => Directory ? NtfsSameSnapshotProbeBranch.Directory :
        Compressed && Sparse ? NtfsSameSnapshotProbeBranch.CompressedAndSparse : Compressed ? NtfsSameSnapshotProbeBranch.Compressed :
        Sparse ? NtfsSameSnapshotProbeBranch.Sparse : NtfsSameSnapshotProbeBranch.Normal;
}

internal static class NtfsSameSnapshotNativeProbe
{
    [StructLayout(LayoutKind.Sequential)] private struct Basic { internal long Creation, Access, Write, Change; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct Standard { internal long Allocation, End; internal uint Links; internal byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] private struct Identity { internal ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct Compression { internal long Size; internal ushort Format; internal byte Unit, Chunk, Cluster, R1, R2, R3; }
    [StructLayout(LayoutKind.Sequential)] private struct Legacy
    {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadBasic(SafeFileHandle handle, int kind, out Basic info, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadStandard(SafeFileHandle handle, int kind, out Standard info, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadIdentity(SafeFileHandle handle, int kind, out Identity info, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadCompression(SafeFileHandle handle, int kind, out Compression info, uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    private static extern bool ReadLegacy(SafeFileHandle handle, out Legacy info);

    internal static NtfsSameSnapshotNativeObservation Read(string path, bool directory, FileStorageInfo bound, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var handle = NativeStorage.CreateFileW(path, 0x80, NativeStorage.ShareAll, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new InvalidDataException("SameSnapshotProbe");
        if (!ReadBasic(handle, 0, out var basic, (uint)Marshal.SizeOf<Basic>()) ||
            !ReadStandard(handle, 1, out var standard, (uint)Marshal.SizeOf<Standard>()) ||
            !ReadIdentity(handle, 18, out var identity, (uint)Marshal.SizeOf<Identity>()) || !ReadLegacy(handle, out var legacy))
            throw new InvalidDataException("SameSnapshotProbe");
        token.ThrowIfCancellationRequested();
        var attributes = (FileAttributes)basic.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory ||
            (standard.Directory != 0) != directory || standard.DeletePending != 0 || standard.Links == 0 || identity.High != 0 || identity.Low >> 48 == 0)
            throw new InvalidDataException("SameSnapshotProbe");
        bool compressed = !directory && (attributes & FileAttributes.Compressed) != 0;
        bool sparse = !directory && (attributes & FileAttributes.SparseFile) != 0;
        long allocated = directory ? 0 : standard.Allocation;
        if (compressed || sparse)
        {
            if (!ReadCompression(handle, 8, out var compression, (uint)Marshal.SizeOf<Compression>())) throw new InvalidDataException("SameSnapshotProbe");
            allocated = compression.Size;
        }
        if (allocated < 0 || !directory && (standard.End < 0 || standard.Allocation < 0)) throw new InvalidDataException("SameSnapshotProbe");
        var id = new FileIdentity(identity.Volume, identity.Low, identity.High);
        ulong legacySize = (ulong)legacy.SizeHigh << 32 | legacy.SizeLow;
        const uint relevant = (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.SparseFile);
        bool consistent = id == bound.Identity && (legacy.Attributes & relevant) == (basic.Attributes & relevant) &&
            ((ulong)legacy.IndexHigh << 32 | legacy.IndexLow) == identity.Low && legacy.Links == standard.Links &&
            (directory || legacySize == (ulong)standard.End) && bound.LogicalBytes == (directory ? -1 : standard.End) &&
            bound.AllocatedBytes == allocated && bound.LinkCount == standard.Links;
        token.ThrowIfCancellationRequested();
        return new(id, directory, directory ? -1 : standard.End, allocated, standard.Allocation, standard.Links, compressed, sparse, consistent);
    }
}
