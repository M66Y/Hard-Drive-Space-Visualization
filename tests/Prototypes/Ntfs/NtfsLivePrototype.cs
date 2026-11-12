using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using DiskAtlas;

namespace DiskAtlas.NtfsPrototype;

// Test-only: reads records for a finite list of files created by this invocation.
// Never enumerates a host volume or accepts a disk number/device path from the CLI.
internal static class NtfsLivePrototype
{
    private const uint VolumeData = 0x90064, GetRecord = 0x90068;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder volume, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string name, string existing, IntPtr reserved);
    [StructLayout(LayoutKind.Sequential)]
    private struct StorageType { public uint DeviceId; public Guid Vendor; }
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenParameters { public uint Version, Depth; }
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint OpenVirtualDisk(ref StorageType type, string path, uint access, uint flags,
        ref OpenParameters parameters, out SafeFileHandle handle);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetVirtualDiskPhysicalPath(SafeFileHandle handle, ref uint size, StringBuilder path);

    internal static int Run(string manifestPath)
    {
        string? summaryPath = null;
        int checks = 0;
        string failureCode = "FixtureGuard";
        void Check(bool condition, string code)
        {
            if (!condition) throw new FixtureFailure(code);
            checks++; Console.WriteLine("PASS NTFS live " + code);
        }
        try
        {
            var manifestFile = new FileInfo(Path.GetFullPath(manifestPath));
            string artifacts = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
            string workspace = manifestFile.DirectoryName ?? throw new FixtureFailure("Workspace");
            string suffix = Path.GetFileName(workspace);
            Check(workspace.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) &&
                suffix.StartsWith("ntfs-live-", StringComparison.Ordinal) &&
                Guid.TryParseExact(suffix[10..], "N", out _) && manifestFile.Name == "fixture.json", "OwnedWorkspace");
            for (var current = new DirectoryInfo(workspace); current != null; current = current.Parent)
                Check((current.Attributes & FileAttributes.ReparsePoint) == 0, "NoReparseParent");
            Check(manifestFile.Length is > 0 and < 16384 && (manifestFile.Attributes & FileAttributes.ReparsePoint) == 0, "ManifestBounds");
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestFile.FullName));
            var json = document.RootElement;
            string Field(string name) => json.GetProperty(name).GetString() ?? throw new FixtureFailure("ManifestField");
            string nonce = Field("nonce"), root = Field("drive_root"), volumeGuid = Field("volume_guid");
            string image = Field("image_path");
            Check(json.GetProperty("schema").GetInt32() == 1 && Guid.TryParseExact(nonce, "N", out _) &&
                suffix == "ntfs-live-" + nonce &&
                Path.GetFullPath(Field("workspace")).Equals(workspace, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFullPath(image).Equals(Path.Combine(workspace, "fixture.vhd"), StringComparison.OrdinalIgnoreCase), "ManifestIdentity");
            Check(root.Length == 3 && char.IsAsciiLetter(root[0]) && root[1..] == ":\\" &&
                volumeGuid.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && volumeGuid.EndsWith(@"}\", StringComparison.Ordinal), "VolumeSyntax");
            Check(File.Exists(image) && new FileInfo(image).Length is >= 256 * 1024 * 1024 and <= 257 * 1024 * 1024 &&
                (File.GetAttributes(image) & FileAttributes.ReparsePoint) == 0, "OwnedImage");
            string marker = Path.Combine(volumeGuid, ".asukalens-ntfs-fixture.json");
            var markerFile = new FileInfo(marker);
            Check(markerFile.Exists && markerFile.Length is > 0 and < 16384 &&
                (markerFile.Attributes & FileAttributes.ReparsePoint) == 0, "VolumeMarker");
            using var markerJson = JsonDocument.Parse(File.ReadAllBytes(marker));
            foreach (string field in new[] { "nonce", "image_path", "volume_guid", "workspace" })
                Check(markerJson.RootElement.GetProperty(field).GetString() == Field(field), "MarkerBinding");
            var actualGuid = new StringBuilder(128);
            Check(GetVolumeNameForVolumeMountPointW(root, actualGuid, actualGuid.Capacity) &&
                actualGuid.ToString().Equals(volumeGuid, StringComparison.OrdinalIgnoreCase), "MountedVolumeIdentity");
            Check(new DriveInfo(root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase), "NtfsVolume");
            string targetSummary = Path.Combine(workspace, "live-summary.json");
            Check(!File.Exists(targetSummary), "FreshSummary");
            summaryPath = targetSummary;

            using var volume = NativeStorage.CreateFileW(@"\\.\" + root[..2], 0x80000000, NativeStorage.ShareAll,
                IntPtr.Zero, 3, 0, IntPtr.Zero);
            Check(!volume.IsInvalid, "ReadOnlyVolumeOpen");
            var storageType = new StorageType { DeviceId = 2, Vendor = new Guid("ec984aec-a0f9-47e9-901f-71415a66345b") };
            var open = new OpenParameters { Version = 1, Depth = 1 };
            // GET_INFO plus DETACH access for a permanently mounted VHD; no detach API is called here.
            Check(OpenVirtualDisk(ref storageType, image, 0xc0000, 0, ref open, out var virtualDisk) == 0, "OpenOwnedVirtualDiskInfo");
            using (virtualDisk)
            {
                var physical = new StringBuilder(128); uint physicalBytes = 256;
                Check(GetVirtualDiskPhysicalPath(virtualDisk, ref physicalBytes, physical) == 0, "OwnedVirtualDiskDevice");
                const string prefix = @"\\.\PhysicalDrive";
                string device = physical.ToString();
                Check(device.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    uint.TryParse(device[prefix.Length..], out _), "VirtualDeviceSyntax");
                uint number = uint.Parse(device[prefix.Length..]);
                var extents = new byte[128];
                Check(NativeStorage.DeviceIoControl(volume, 0x560000, null, 0, extents, extents.Length, out int extentBytes, IntPtr.Zero) &&
                    extentBytes >= 32 && BinaryPrimitives.ReadUInt32LittleEndian(extents) == 1 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(extents.AsSpan(8)) == number, "VolumeBackedByOwnedImage");
                long start = BinaryPrimitives.ReadInt64LittleEndian(extents.AsSpan(16));
                long length = BinaryPrimitives.ReadInt64LittleEndian(extents.AsSpan(24));
                Check(start >= 0 && length > 0 && start <= 256L * 1024 * 1024 - length, "VirtualPartitionBounds");
            }
            var geometry = new byte[128];
            Check(NativeStorage.DeviceIoControl(volume, VolumeData, null, 0, geometry, geometry.Length, out int returned, IntPtr.Zero) && returned >= 96,
                "VolumeGeometry");
            int sector = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(geometry.AsSpan(40)));
            int cluster = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(geometry.AsSpan(44)));
            int recordSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(geometry.AsSpan(48)));
            Check(sector == 512 && cluster == 4096 && recordSize is >= 1024 and <= 65536 &&
                recordSize % 512 == 0 && returned >= 104 &&
                BinaryPrimitives.ReadUInt16LittleEndian(geometry.AsSpan(100)) == 3 &&
                BinaryPrimitives.ReadUInt16LittleEndian(geometry.AsSpan(102)) == 1, "SupportedGeometry");
            ulong serial = BinaryPrimitives.ReadUInt64LittleEndian(geometry);

            string data = Path.Combine(volumeGuid, "AsukaLensFixture-" + nonce);
            Check(!Directory.Exists(data), "FreshSyntheticDirectory");
            Directory.CreateDirectory(data);
            var files = new List<(string Path, NtfsLookupReason? Expected)>();
            void Make(string name, byte[] bytes, NtfsLookupReason? expected = NtfsLookupReason.None)
            {
                string path = Path.Combine(data, name);
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes); output.Flush(true); }
                files.Add((path, expected));
            }
            Make("empty.bin", []);
            Make("resident.bin", "synthetic resident"u8.ToArray(), NtfsLookupReason.ResidentAllocation);
            string residentAlias = Path.Combine(data, "resident-link.bin");
            Check(CreateHardLinkW(residentAlias, files[^1].Path, IntPtr.Zero), "CreateResidentHardLink");
            files.Add((residentAlias, NtfsLookupReason.ResidentAllocation));
            var random = new byte[65536]; new Random(417).NextBytes(random); Make("ordinary.bin", random);
            string alias = Path.Combine(data, "ordinary-link.bin");
            Check(CreateHardLinkW(alias, files[^1].Path, IntPtr.Zero), "CreateSyntheticHardLink");
            files.Add((alias, NtfsLookupReason.None));
            Make("compressed.bin", new byte[131072], NtfsLookupReason.Compressed);
            using (var file = File.OpenHandle(files[^1].Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                Check(NativeStorage.DeviceIoControl(file, 0x9c040, [1, 0], 2, [], 0, out _, IntPtr.Zero), "SetSyntheticCompression");
            Make("sparse.bin", [], NtfsLookupReason.Sparse);
            using (var file = new FileStream(files[^1].Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                Check(NativeStorage.DeviceIoControl(file.SafeFileHandle, 0x900c4, null, 0, [], 0, out _, IntPtr.Zero), "SetSyntheticSparse");
                file.SetLength(2 * 1024 * 1024); file.Flush(true);
            }
            Make("with-stream.bin", "default"u8.ToArray(), NtfsLookupReason.NamedData);
            File.WriteAllText(files[^1].Path + ":meta", "synthetic named stream");
            foreach (int length in new[] { 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 255, 256, 400, 512, 768, 1024, 4096 })
                Make("size-" + length + ".bin", new byte[length], expected: null);

            NtfsRecord ReadRecord(string path, bool directory)
            {
                Check(NativeStorage.TryRead(path, directory, File.GetAttributes(path), out var before, out _) && before != null, "NativeStorageBefore");
                Check(before!.Identity.High == 0 && before.Identity.Volume == serial, "FileVolumeIdentity");
                ulong slot = before.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber;
                var input = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(input, slot);
                var output = new byte[recordSize + 16];
                Check(NativeStorage.DeviceIoControl(volume, GetRecord, input, input.Length, output, output.Length, out int count, IntPtr.Zero) && count >= 12,
                    "ReadTargetRecord");
                Check((BinaryPrimitives.ReadUInt64LittleEndian(output) & NtfsRecordDecoder.MaximumRecordNumber) == slot &&
                    BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(8)) == recordSize && count >= recordSize + 12, "ReturnedRecordBoundsAndSlot");
                var decoded = NtfsRecordDecoder.DecodeKernelReturned(output.AsSpan(12, recordSize), sector, slot, cluster, 3, 1);
                Check(decoded.Status == NtfsDecodeStatus.Decoded && decoded.Record != null, "DecodeKernelRecord-" + decoded.Code);
                var result = decoded.Record!;
                Check(result.Reference == before.Identity.Low && result.BaseReference == 0, "RecordSequenceAndBase");
                Check(NativeStorage.TryRead(path, directory, File.GetAttributes(path), out var after, out _) && after != null &&
                    before.Identity == after.Identity && before.LogicalBytes == after.LogicalBytes && before.AllocatedBytes == after.AllocatedBytes &&
                    before.LinkCount == after.LinkCount && before.Modified == after.Modified, "StableFileIdentityAndSizes");
                Check(result.IsDirectory == directory, "RecordTypeMatchesNative");
                Check(result.LinkCount == result.Names.Count, "MftNameReferenceCount");
                int visibleLinks = result.Names.Where(n => n.Namespace != 2).Select(n => (n.ParentReference, n.Name)).Distinct().Count();
                Check(visibleLinks == before.LinkCount, "VisibleLinksMatchNative");
                Check(result.Names.Any(n => n.Namespace != 2 && n.Name == Path.GetFileName(path)), "SelectedNamePresent");
                if (!directory)
                {
                    Check(result.LogicalBytes == before.LogicalBytes, "LogicalSizeMatchesNative");
                    if (result.AllocatedBytes is { } allocatedBytes) Check(allocatedBytes == before.AllocatedBytes, "AllocationMatchesNative");
                }
                return result;
            }

            var decodedFiles = new Dictionary<ulong, NtfsRecord>();
            var objectPaths = new Dictionary<ulong, string>();
            foreach (var item in files)
            {
                var decoded = ReadRecord(item.Path, false);
                if (item.Expected is { } expected)
                {
                    if (expected != NtfsLookupReason.None) Check(decoded.LookupReasons.HasFlag(expected), "SpecialTypeRequestsLookup");
                    else Check(decoded.LookupReasons == NtfsLookupReason.None, "OrdinaryTypeHasCompleteMetadata");
                }
                else Check(decoded.LookupReasons is NtfsLookupReason.None or NtfsLookupReason.ResidentAllocation, "SizeCaseHasBoundedLookupPolicy");
                if (item.Expected is NtfsLookupReason.Compressed or NtfsLookupReason.Sparse or NtfsLookupReason.ResidentAllocation)
                    Check(decoded.AllocatedBytes == null, "SpecialAllocationRemainsUnknown");
                decodedFiles.TryAdd(decoded.Reference, decoded);
                objectPaths.TryAdd(decoded.Reference, item.Path);
            }
            Check(decodedFiles.Count == files.Count - 2, "HardLinkObjectDeduplication");
            var dir = ReadRecord(data, true);
            Check(dir.Names.All(n => n.Namespace == 2 || n.ParentReference != dir.Reference), "SyntheticDirectoryParent");
            var lookupItems = decodedFiles.Values.Select(item => new NtfsLookupItem(serial, item, objectPaths[item.Reference])).ToArray();
            var experiment = NtfsLookupExperiment.Run(lookupItems, Check);
            WriteSummary(summaryPath, "passed", checks, 0, "None", experiment);
            Console.WriteLine("PASS NTFS live checks: " + checks + "; known synthetic files only");
            return 0;
        }
        catch (Exception error)
        {
            if (error is FixtureFailure specific) failureCode = specific.Code;
            else failureCode = error switch
            {
                UnauthorizedAccessException => "AccessDenied", IOException => "FixtureIO",
                JsonException => "ManifestJson", _ => "FixtureFailure"
            };
            if (summaryPath != null) try { WriteSummary(summaryPath, "failed", checks, 1, failureCode); } catch { }
            Console.Error.WriteLine("FAIL NTFS live " + failureCode + "; checks=" + checks);
            return 1;
        }
    }

    private static void WriteSummary(string path, string status, int checks, int failures, string code,
        NtfsLookupExperimentReport? experiment = null) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new { schema = 1, status, checks, failures, code, lookup_experiment = experiment }));
    private sealed class FixtureFailure(string code) : Exception { internal string Code { get; } = code; }
}
