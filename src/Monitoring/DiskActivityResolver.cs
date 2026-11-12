using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas;

internal sealed record DiskActivityTarget(int DiskNumber, string Label);

// Resolves Windows physical-disk counter numbers without enumerating any files.
// Unsupported/ambiguous mappings remain unavailable instead of choosing a disk.
internal static class DiskActivityResolver
{
    private const uint VolumeDiskExtents = 0x00560000, StorageQueryProperty = 0x002d1400;
    private const int SingleExtentBytes = 32, DescriptorPrefixBytes = 36, MaximumDescriptorBytes = 65536;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNameW(string path, StringBuilder mountPoint, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    internal static DiskActivityTarget? Resolve(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !IsLocalAbsolutePath(path)) return null;
        try
        {
            string absolute = Path.GetFullPath(path);
            var mountPoint = new StringBuilder(32768);
            bool mapped = GetVolumePathNameW(absolute, mountPoint, (uint)mountPoint.Capacity);
            token.ThrowIfCancellationRequested();
            if (!mapped || !IsLocalAbsolutePath(mountPoint.ToString())) return null;
            string mount = mountPoint.ToString();
            uint driveType = GetDriveTypeW(mount);
            token.ThrowIfCancellationRequested();
            if (driveType != 3) return null; // DRIVE_FIXED only; never UNC/removable.
            var volumeName = new StringBuilder(64);
            bool named = GetVolumeNameForVolumeMountPointW(mount, volumeName, (uint)volumeName.Capacity);
            token.ThrowIfCancellationRequested();
            if (!named || !IsVolumeGuidPath(volumeName.ToString())) return null;

            using var volume = NativeStorage.CreateFileW(volumeName.ToString()[..^1], 0, NativeStorage.ShareAll,
                IntPtr.Zero, 3, 0, IntPtr.Zero);
            token.ThrowIfCancellationRequested();
            if (volume.IsInvalid) return null;
            var extents = new byte[SingleExtentBytes];
            bool located = NativeStorage.DeviceIoControl(volume, VolumeDiskExtents, null, 0, extents, extents.Length,
                out int returned, IntPtr.Zero);
            token.ThrowIfCancellationRequested();
            if (!located || !TryReadSingleExtent(extents, returned, out int diskNumber)) return null;

            using var disk = NativeStorage.CreateFileW(@"\\.\PhysicalDrive" + diskNumber, 0, NativeStorage.ShareAll,
                IntPtr.Zero, 3, 0, IntPtr.Zero);
            token.ThrowIfCancellationRequested();
            if (disk.IsInvalid || !ReadSupportedDevice(disk, token)) return null;
            token.ThrowIfCancellationRequested();
            return new(diskNumber, "磁盘 " + diskNumber);
        }
        catch (Exception error) when (IsExpectedReadFailure(error)) { return null; }
    }

    internal static IReadOnlyList<DiskActivityTarget> Discover(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Array.Empty<DiskActivityTarget>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception error) when (IsExpectedReadFailure(error)) { return Array.Empty<DiskActivityTarget>(); }
        var targets = new SortedDictionary<int, DiskActivityTarget>();
        foreach (var drive in drives)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                var target = Resolve(drive.RootDirectory.FullName, token);
                if (target != null) targets.TryAdd(target.DiskNumber, target);
            }
            catch (Exception error) when (IsExpectedReadFailure(error)) { }
        }
        token.ThrowIfCancellationRequested();
        return Array.AsReadOnly(targets.Values.ToArray());
    }

    private static bool ReadSupportedDevice(SafeFileHandle disk, CancellationToken token)
    {
        byte[] query = new byte[12]; // StorageDeviceProperty=0, PropertyStandardQuery=0.
        var header = new byte[8];
        bool received = NativeStorage.DeviceIoControl(disk, StorageQueryProperty, query, query.Length, header, header.Length,
            out int returned, IntPtr.Zero);
        token.ThrowIfCancellationRequested();
        if (!received || !TryReadDescriptorSize(header, returned, out int size)) return false;
        var descriptor = new byte[size];
        received = NativeStorage.DeviceIoControl(disk, StorageQueryProperty, query, query.Length, descriptor, descriptor.Length,
            out returned, IntPtr.Zero);
        token.ThrowIfCancellationRequested();
        return received && IsSupportedDeviceDescriptor(descriptor, returned);
    }

    // The native VOLUME_DISK_EXTENTS layout aligns DISK_EXTENT at offset 8;
    // DiskNumber is at 8, StartingOffset at 16, and ExtentLength at 24.
    internal static bool TryReadSingleExtent(ReadOnlySpan<byte> buffer, int returned, out int diskNumber)
    {
        diskNumber = -1;
        if (returned < SingleExtentBytes || returned > buffer.Length) return false;
        var bytes = buffer[..returned];
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 1) return false;
        uint number = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        long start = BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]);
        long length = BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]);
        if (number > int.MaxValue || start < 0 || length <= 0 || start > long.MaxValue - length) return false;
        diskNumber = (int)number;
        return true;
    }

    internal static bool TryReadDescriptorSize(ReadOnlySpan<byte> header, int returned, out int size)
    {
        size = 0;
        if (returned < 8 || returned > header.Length) return false;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (version < DescriptorPrefixBytes || version > declared || declared < DescriptorPrefixBytes || declared > MaximumDescriptorBytes)
            return false;
        size = (int)declared;
        return true;
    }

    internal static bool IsSupportedDeviceDescriptor(ReadOnlySpan<byte> buffer, int returned)
    {
        if (!TryReadDescriptorSize(buffer, returned, out int size) || size > returned) return false;
        // SCSI direct-access block device, non-removable. Never decode identifying strings.
        if (buffer[8] != 0 || buffer[10] != 0) return false;
        uint rawLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer[32..]);
        if (rawLength > size - DescriptorPrefixBytes) return false;
        return IsSupportedBusType(BinaryPrimitives.ReadUInt32LittleEndian(buffer[28..]));
    }

    internal static bool IsSupportedBusType(uint bus) => bus is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 10 or 11 or 12 or 13 or 17 or 18 or 19;

    internal static bool IsLocalAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0) return false;
        ReadOnlySpan<char> local = path.AsSpan();
        if (local.StartsWith(@"\\?\", StringComparison.Ordinal)) local = local[4..];
        return local.Length >= 3 && char.IsAsciiLetter(local[0]) && local[1] == ':' &&
            (local[2] == '\\' || local[2] == '/');
    }

    internal static bool IsVolumeGuidPath(string value)
    {
        const string prefix = @"\\?\Volume{";
        return value.Length == prefix.Length + 38 && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            value.EndsWith(@"}\", StringComparison.Ordinal) && Guid.TryParseExact(value.AsSpan(prefix.Length, 36), "D", out _);
    }

    internal static bool IsExpectedReadFailure(Exception error) => error is IOException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or SecurityException or DllNotFoundException or EntryPointNotFoundException;
}
