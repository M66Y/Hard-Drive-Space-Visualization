using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsVolumePreflightReport(string Revision, string Status, string Code, int NativeError,
    string FileSystem, bool OpenedWithZeroAccess, int? SectorBytes = null, int? ClusterBytes = null,
    int? RecordBytes = null, long? VolumeBytes = null, long? MftValidDataBytes = null, long? MftSlots = null);

// Only filesystem/NTFS volume metadata, never MFT records, file names/content,
// snapshots, directory enumeration or storage configuration. The result remains
// local; volume identifiers and MFT physical locations are deliberately omitted.
internal static class NtfsVolumePreflight
{
    internal readonly record struct FormatResult(bool Success, string Format, int Error);
    internal readonly record struct OpenResult(SafeFileHandle Handle, int Error);
    internal readonly record struct MetadataResult(bool Success, int BytesReturned, int Error);
    internal interface INative
    {
        FormatResult FileSystem(string root);
        OpenResult OpenMetadataRoot(string root, bool zeroAccess);
        MetadataResult Metadata(SafeFileHandle handle, byte[] output);
    }
    private const string Revision = "volume-preflight-v1";
    internal static int Run(string selectedRoot, bool readMetadataHandle = false, CancellationToken token = default)
    {
        NtfsVolumePreflightReport report;
        try { report = InspectForTests(selectedRoot, Native.Instance, token, readMetadataHandle); }
        catch (OperationCanceledException) { report = Failure("Unavailable", "PreflightCancelled", "Unknown"); }
        Console.WriteLine(JsonSerializer.Serialize(report));
        return report.Status == "MetadataAvailable" ? 0 : report.Status is "Unavailable" or "Unsupported" ? 2 : 1;
    }

    internal static NtfsVolumePreflightReport InspectForTests(string selectedRoot, INative adapter, CancellationToken token = default, bool readMetadataHandle = false)
    {
        token.ThrowIfCancellationRequested();
        if (selectedRoot == null || selectedRoot.Length != 3 || !char.IsAsciiLetter(selectedRoot[0]) || selectedRoot[1] != ':' || selectedRoot[2] != '\\' || adapter == null)
            return Failure("Invalid", "PreflightRoot", "Unknown");
        string root = char.ToUpperInvariant(selectedRoot[0]) + ":\\", format = "Unknown"; bool opened = false, zeroAccess = !readMetadataHandle;
        try
        {
            FormatResult detected;
            try { detected = adapter.FileSystem(root); }
            catch { token.ThrowIfCancellationRequested(); return Failure("Unavailable", "PreflightNative", format); }
            token.ThrowIfCancellationRequested();
            if (!detected.Success) return Failure("Unavailable", "PreflightFormat", format, detected.Error);
            format = SafeFormat(detected.Format);
            if (format != "NTFS") return Failure("Unsupported", "PreflightNotNtfs", format);
            OpenResult openedVolume;
            try { openedVolume = adapter.OpenMetadataRoot(root, zeroAccess); }
            catch { token.ThrowIfCancellationRequested(); return Failure("Unavailable", "PreflightNative", format); }
            using (var handle = openedVolume.Handle)
            {
                token.ThrowIfCancellationRequested();
                if (handle == null || handle.IsInvalid || handle.IsClosed) return Failure("Unavailable", "PreflightOpen", format, openedVolume.Error);
                opened = zeroAccess;
                var bytes = new byte[128]; MetadataResult result;
                try { result = adapter.Metadata(handle, bytes); }
                catch { token.ThrowIfCancellationRequested(); return Failure("Unavailable", "PreflightNative", format, opened: opened); }
                token.ThrowIfCancellationRequested();
                if (!result.Success) return Failure("Unavailable", "PreflightQuery", format, result.Error, opened);
                return ParseNative(bytes, result.BytesReturned) with { OpenedWithZeroAccess = zeroAccess };
            }
        }
        catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); return Failure("Unavailable", "PreflightNative", format, opened: opened); }
        catch { token.ThrowIfCancellationRequested(); return Failure("Unavailable", "PreflightNative", format, opened: opened); }
    }

    internal static NtfsVolumePreflightReport ParseNative(ReadOnlySpan<byte> bytes, int used)
    {
        if (bytes.Length != 128 || used is < 96 or > 128) return Failure("Invalid", "PreflightPayload", "NTFS", opened: true);
        // Only these documented metadata fields enter the safe report. In
        // particular bytes 0..7(serial), 64..87(LCNs) are never copied or exposed.
        long sectors = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]), clusters = BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]);
        int sector = BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]), cluster = BinaryPrimitives.ReadInt32LittleEndian(bytes[44..]), record = BinaryPrimitives.ReadInt32LittleEndian(bytes[48..]);
        long valid = BinaryPrimitives.ReadInt64LittleEndian(bytes[56..]);
        bool PowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
        if (!PowerOfTwo(sector) || sector is < 512 or > 65536 || !PowerOfTwo(cluster) || cluster < sector || cluster > 2 * 1024 * 1024 ||
            cluster % sector != 0 || !PowerOfTwo(record) || record < sector || record > 65536 || record % sector != 0 ||
            sectors <= 0 || sectors > long.MaxValue / sector || clusters <= 0 || clusters > long.MaxValue / cluster)
            return Failure("Invalid", "PreflightGeometry", "NTFS", opened: true);
        long volume = sectors * sector;
        if (clusters * cluster > volume) return Failure("Invalid", "PreflightGeometry", "NTFS", opened: true);
        if (valid <= 0 || valid > volume || valid % record != 0) return Failure("Invalid", "PreflightMftLength", "NTFS", opened: true);
        return new(Revision, "MetadataAvailable", "None", 0, "NTFS", true, sector, cluster, record, volume, valid, valid / record);
    }
    private static string SafeFormat(string? value) => (value is { Length: <= 16 } ? value.ToUpperInvariant() : null) switch
    { "NTFS" => "NTFS", "REFS" => "ReFS", "FAT" => "FAT", "FAT32" => "FAT32", "EXFAT" => "exFAT", "UDF" => "UDF", "CDFS" => "CDFS", _ => "Unknown" };
    private static NtfsVolumePreflightReport Failure(string status, string code, string format, int error = 0, bool opened = false) =>
        new(Revision, status, code, error, format, opened);

    private sealed class Native : INative
    {
        internal static readonly Native Instance = new();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformationW(string root, IntPtr label, uint labelSize, IntPtr serial,
            IntPtr maximumComponentLength, IntPtr flags, StringBuilder fileSystem, uint fileSystemSize);
        public FormatResult FileSystem(string root)
        {
            var format = new StringBuilder(32);
            bool success = GetVolumeInformationW(root, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, format, 32);
            return new(success, success ? format.ToString() : "Unknown", success ? 0 : Marshal.GetLastWin32Error());
        }
        public OpenResult OpenMetadataRoot(string root, bool zeroAccess)
        {
            // An explicit metadata-only retry may request read permission for
            // this same FSCTL. This type has no ReadFile or raw-record path.
            var handle = NativeStorage.CreateFileW(@"\\.\" + root[..2], zeroAccess ? 0 : 0x80000000, NativeStorage.ShareAll, IntPtr.Zero, 3, 0, IntPtr.Zero);
            return new(handle, handle.IsInvalid ? Marshal.GetLastWin32Error() : 0);
        }
        public MetadataResult Metadata(SafeFileHandle handle, byte[] output)
        {
            bool success = NativeStorage.DeviceIoControl(handle, 0x90064, null, 0, output, output.Length, out int used, IntPtr.Zero);
            return new(success, used, success ? 0 : Marshal.GetLastWin32Error());
        }
    }
}
