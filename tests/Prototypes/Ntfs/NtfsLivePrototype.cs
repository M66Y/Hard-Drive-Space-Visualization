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
            string mode = Field("mode");
            Check(mode is "comparison" or "kernel-diagnostics" or "raw-records" or "fixture-source" or "mft-range", "KnownFixtureMode");
            if (mode is "fixture-source" or "mft-range")
            {
                // Reject wrong backing volumes or parent chains before the
                // legacy preparation path touches any marker or volume data.
                string preparationSummary = Path.Combine(workspace, "live-summary.json");
                Check(!File.Exists(preparationSummary), "FreshSummary");
                summaryPath = preparationSummary;
                if (mode == "mft-range") NtfsVerifiedFixtureContext.VerifyPreparationForBoundedMft(manifestFile.FullName);
                else NtfsVerifiedFixtureContext.VerifyPreparation(manifestFile.FullName);
            }
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
            foreach (string field in new[] { "nonce", "image_path", "volume_guid", "workspace", "mode" })
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
            Check(OpenVirtualDisk(ref storageType, image, 0xc0000, mode is "fixture-source" or "mft-range" ? 1u : 0u, ref open, out var virtualDisk) == 0, "OpenOwnedVirtualDiskInfo");
            long ownedPartitionBytes = 0;
            uint ownedPhysicalNumber = 0;
            using (virtualDisk)
            {
                var physical = new StringBuilder(128); uint physicalBytes = 256;
                Check(GetVirtualDiskPhysicalPath(virtualDisk, ref physicalBytes, physical) == 0, "OwnedVirtualDiskDevice");
                const string prefix = @"\\.\PhysicalDrive";
                string device = physical.ToString();
                Check(device.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    uint.TryParse(device[prefix.Length..], out _), "VirtualDeviceSyntax");
                uint number = uint.Parse(device[prefix.Length..]);
                ownedPhysicalNumber = number;
                var extents = new byte[128];
                Check(NativeStorage.DeviceIoControl(volume, 0x560000, null, 0, extents, extents.Length, out int extentBytes, IntPtr.Zero) &&
                    extentBytes >= 32 && BinaryPrimitives.ReadUInt32LittleEndian(extents) == 1 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(extents.AsSpan(8)) == number, "VolumeBackedByOwnedImage");
                long start = BinaryPrimitives.ReadInt64LittleEndian(extents.AsSpan(16));
                long length = BinaryPrimitives.ReadInt64LittleEndian(extents.AsSpan(24));
                Check(start >= 0 && length > 0 && start <= 256L * 1024 * 1024 - length, "VirtualPartitionBounds");
                ownedPartitionBytes = length;
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
            if (mode is "kernel-diagnostics" or "raw-records" or "fixture-source" or "mft-range")
            {
                var rawTargets = new Dictionary<string, NtfsRawTarget>(StringComparer.Ordinal);
                NtfsKernelRead ReadKernel(string path)
                {
                    Check(NativeStorage.TryRead(path, false, File.GetAttributes(path), out var before, out _) && before != null,
                        "MatrixNativeBefore");
                    Check(before!.Identity.High == 0 && before.Identity.Volume == serial && (before.Identity.Low >> 48) != 0 &&
                        !before.WritableHandleOpen, "MatrixOwnedFileIdentity");
                    ulong slot = before.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber;
                    var input = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(input, slot);
                    var output = new byte[recordSize + 32];
                    Check(NativeStorage.DeviceIoControl(volume, GetRecord, input, input.Length, output, output.Length,
                        out int count, IntPtr.Zero), "MatrixReadKernelRecord");
                    Check(count >= recordSize + 12 && count <= output.Length &&
                        (BinaryPrimitives.ReadUInt64LittleEndian(output) & NtfsRecordDecoder.MaximumRecordNumber) == slot &&
                        BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(8)) == recordSize &&
                        BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(28)) == (ushort)(before.Identity.Low >> 48),
                        "MatrixReturnedRecordBinding");
                    bool afterRead = NativeStorage.TryRead(path, false, File.GetAttributes(path), out var after, out _);
                    int changed = !afterRead || after == null ? 1 :
                        (after.Identity != before.Identity ? 2 : 0) |
                        (after.LogicalBytes != before.LogicalBytes ? 4 : 0) |
                        (after.AllocatedBytes != before.AllocatedBytes ? 8 : 0) |
                        (after.LinkCount != before.LinkCount ? 16 : 0) |
                        (after.Modified != before.Modified ? 32 : 0) |
                        (after.WritableHandleOpen ? 64 : 0);
                    string observedCase = NtfsKernelMatrix.Cases.FirstOrDefault(item =>
                        item.CaseId == Path.GetFileName(Path.GetDirectoryName(path)))?.CaseId ?? "UnknownCase";
                    Check(changed == 0, "MatrixNativeAfterStable-" + observedCase + "-M" + changed);
                    if (mode == "raw-records")
                    {
                        string parentPath = Path.GetDirectoryName(path) ?? throw new FixtureFailure("RawParentPath");
                        string caseId = Path.GetFileName(parentPath);
                        Check(NtfsKernelMatrix.Cases.Any(item => item.CaseId == caseId) &&
                            (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                            (File.GetAttributes(parentPath) & FileAttributes.ReparsePoint) == 0,
                            "RawKnownGeneratedPath");
                        Check(NativeStorage.TryRead(parentPath, true, File.GetAttributes(parentPath), out var parent, out _) &&
                            parent != null && parent.Identity.Volume == serial && parent.Identity.High == 0,
                            "RawNativeParentContext");
                        rawTargets[caseId] = new(caseId, path, parentPath, after!, parent!, File.GetAttributes(path));
                    }
                    return new(output, count, before, after!, sector, cluster, 3, 1);
                }
                if (mode == "mft-range")
                {
                    _ = NtfsKernelMatrix.Run(data, ReadKernel, null, Check, retainFailures: false);
                    volume.Dispose();
                    using var rangeSource = NtfsBoundedMftSource.Open(manifestFile.FullName);
                    var rangeReport = rangeSource.RunEnumeration();
                    Check(rangeReport.EvidenceKind == "NativeOwnedFixture" && rangeReport.GuardOwned &&
                        rangeReport.TokenBound && rangeReport.MapBound && rangeReport.Closed,
                        "RangeOwnedClosureVerified");
                    Check(rangeReport.Enumeration.MatchedObjects == 21 && rangeReport.Enumeration.UnknownAllocationObjects == 14,
                        "RangeFixedTargetsVerified");
                    File.WriteAllText(summaryPath, JsonSerializer.Serialize(new { schema = 1, status = "range_classified", checks,
                        failures = 0, code = "None", mft_range = rangeReport }));
                    Console.WriteLine("CLASSIFIED bounded MFT range; usage completeness is not asserted");
                    return 0;
                }
                if (mode == "fixture-source")
                {
                    var control = NtfsKernelMatrix.Run(data, ReadKernel, null, Check, retainFailures: false);
                    // The new source owns a freshly verified handle; the preparation
                    // handle must not survive into its locking/read lifetime.
                    volume.Dispose();
                    using var source = NtfsFixtureSource.Open(manifestFile.FullName);
                    var sourceReport = source.RunFixedComparison(control);
                    Check(sourceReport.EvidenceKind == "NativeOwnedFixture" && sourceReport.GuardOwned &&
                        sourceReport.TokenBound && sourceReport.MapBound && sourceReport.Closed,
                        "SourceOwnedClosureVerified");
                    Check(sourceReport.Comparison.RawDecoded == 22 && sourceReport.Comparison.MatchedObjects == 21 &&
                        sourceReport.Comparison.UnknownAllocationObjects == 14, "SourceFixedComparisonVerified");
                    File.WriteAllText(summaryPath, JsonSerializer.Serialize(new { schema = 1, status = "source_verified", checks,
                        failures = 0, code = "None", source_adapter = sourceReport }));
                    Console.WriteLine("VERIFIED fixture source: known records only; source and closure bound");
                    return 0;
                }
                if (mode == "raw-records")
                {
                    int factsObservation = 0;
                    NtfsRawVolumeFacts Facts(bool reopen = false)
                    {
                        factsObservation++;
                        using var reopened = reopen ? NativeStorage.CreateFileW(volumeGuid.TrimEnd('\\'), 0x80000000,
                            NativeStorage.ShareAll, IntPtr.Zero, 3, 0, IntPtr.Zero) : null;
                        var queryVolume = reopened ?? volume;
                        Check(!queryVolume.IsInvalid, "RawMetadataVolumeOpen");
                        var extents = new byte[128];
                        Check(NativeStorage.DeviceIoControl(queryVolume, 0x560000, null, 0, extents, extents.Length,
                            out int extentBytes, IntPtr.Zero) && extentBytes >= 32 &&
                            BinaryPrimitives.ReadUInt32LittleEndian(extents) == 1 &&
                            BinaryPrimitives.ReadUInt32LittleEndian(extents.AsSpan(8)) == ownedPhysicalNumber &&
                            BinaryPrimitives.ReadInt64LittleEndian(extents.AsSpan(24)) == ownedPartitionBytes,
                            "RawMetadataVolumeBinding");
                        var fresh = new byte[128];
                        bool volumeRead = NativeStorage.DeviceIoControl(queryVolume, VolumeData, null, 0, fresh, fresh.Length,
                            out int bytes, IntPtr.Zero);
                        int volumeError = volumeRead ? 0 : Marshal.GetLastWin32Error();
                        Check(volumeRead && bytes >= 104, "RawFreshVolumeData-F" + factsObservation + "-E" + volumeError + "-B" + bytes);
                        int sectorBytes = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(fresh.AsSpan(40)));
                        int clusterBytes = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(fresh.AsSpan(44)));
                        int recordBytes = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(fresh.AsSpan(48)));
                        long sectors = BinaryPrimitives.ReadInt64LittleEndian(fresh.AsSpan(8));
                        long clusters = BinaryPrimitives.ReadInt64LittleEndian(fresh.AsSpan(16));
                        Check(BinaryPrimitives.ReadUInt64LittleEndian(fresh) == serial && sectorBytes == 512 &&
                            clusterBytes == 4096 && recordBytes == 1024 && sectors > 0 && sectors <= ownedPartitionBytes / 512 &&
                            clusters > 0 && clusters <= sectors / 8 &&
                            BinaryPrimitives.ReadUInt16LittleEndian(fresh.AsSpan(100)) == 3 &&
                            BinaryPrimitives.ReadUInt16LittleEndian(fresh.AsSpan(102)) == 1, "RawFreshGeometry");
                        var query = new byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(query, 6);
                        var alignment = new byte[64];
                        Check(NativeStorage.DeviceIoControl(queryVolume, 0x2d1400, query, query.Length, alignment, alignment.Length,
                            out int alignmentBytes, IntPtr.Zero) && alignmentBytes >= 28 &&
                            BinaryPrimitives.ReadUInt32LittleEndian(alignment) >= 28 &&
                            BinaryPrimitives.ReadUInt32LittleEndian(alignment.AsSpan(4)) >= 28 &&
                            BinaryPrimitives.ReadUInt32LittleEndian(alignment.AsSpan(4)) <= alignmentBytes &&
                            BinaryPrimitives.ReadUInt32LittleEndian(alignment.AsSpan(16)) == 512,
                            "RawPhysicalAlignmentDescriptor");
                        int physical = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(alignment.AsSpan(20)));
                        Check(physical is >= 512 and <= 65536 && (physical & (physical - 1)) == 0,
                            "RawPhysicalSectorBound");
                        var facts = new NtfsMftGeometry(sectorBytes, clusterBytes, recordBytes, checked(sectors * sectorBytes),
                            clusters, BinaryPrimitives.ReadInt64LittleEndian(fresh.AsSpan(64)),
                            BinaryPrimitives.ReadInt64LittleEndian(fresh.AsSpan(56)));
                        return new(facts, serial, physical);
                    }
                    var control = NtfsKernelMatrix.Run(data, ReadKernel, null, Check, retainFailures: false);
                    var facts = Facts();
                    var raw = NtfsRawComparison.Run(volume, rawTargets.Values.ToArray(), control, facts, () => Facts(reopen: true), Check);
                    File.WriteAllText(summaryPath, JsonSerializer.Serialize(new { schema = 1, status = "verified", checks,
                        failures = 0, code = "None", raw_comparison = raw }));
                    Console.WriteLine($"VERIFIED raw records: {raw.RawDecoded}; matched objects: {raw.MatchedObjects}");
                    return 0;
                }
                var evidence = NtfsRecordEvidence.Create(artifacts.TrimEnd(Path.DirectorySeparatorChar));
                var matrix = NtfsKernelMatrix.Run(data, ReadKernel, evidence, Check);
                string captureCleanup = NtfsKernelMatrix.CleanupCapture(matrix.Captured, evidence.DeleteOwned);
                File.WriteAllText(summaryPath, JsonSerializer.Serialize(new { schema = 1, status = "collected", checks,
                    failures = 0, code = "None", kernel_matrix = matrix, capture_cleanup_status = captureCleanup }));
                Console.WriteLine($"COLLECTED NTFS matrix: requests={matrix.Requests}; decode_failures={matrix.DecodeFailures}; captured={matrix.Captured}");
                return 0;
            }
            Directory.CreateDirectory(data);
            var files = new List<(string Path, NtfsLookupReason? Expected)>();
            int aliasCount = 2; // Existing resident and ordinary aliases below.
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
            string compressedZero = files[^1].Path;
            var randomCompressedBytes = new byte[131072]; new Random(913).NextBytes(randomCompressedBytes);
            Make("compressed-random.bin", randomCompressedBytes, NtfsLookupReason.Compressed);
            string compressedRandom = files[^1].Path;
            using (var file = File.OpenHandle(compressedRandom, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                Check(NativeStorage.DeviceIoControl(file, 0x9c040, [2, 0], 2, [], 0, out _, IntPtr.Zero), "SetRandomSyntheticCompression");
            Make("sparse.bin", [], NtfsLookupReason.Sparse);
            using (var file = new FileStream(files[^1].Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                Check(NativeStorage.DeviceIoControl(file.SafeFileHandle, 0x900c4, null, 0, [], 0, out _, IntPtr.Zero), "SetSyntheticSparse");
                file.SetLength(2 * 1024 * 1024); file.Flush(true);
            }
            string sparseHole = files[^1].Path;
            Make("sparse-written.bin", [], NtfsLookupReason.Sparse);
            string sparseWritten = files[^1].Path;
            using (var file = new FileStream(sparseWritten, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                Check(NativeStorage.DeviceIoControl(file.SafeFileHandle, 0x900c4, null, 0, [], 0, out _, IntPtr.Zero), "SetWrittenSyntheticSparse");
                file.SetLength(2 * 1024 * 1024);
                file.Write(random.AsSpan(0, 4096)); file.Position = file.Length - 4096; file.Write(random.AsSpan(4096, 4096));
                file.Flush(true);
            }
            Make("with-stream.bin", "default"u8.ToArray(), NtfsLookupReason.NamedData);
            File.WriteAllText(files[^1].Path + ":meta", "synthetic named stream");
            string ordinaryNamed = files[^1].Path;
            // Short fixture names isolate the reason-combination policy from the
            // separately retained kernel USA representation failures on larger records.
            Make("c.a", new byte[131072], NtfsLookupReason.Compressed | NtfsLookupReason.NamedData);
            string compressedNamed = files[^1].Path;
            using (var file = File.OpenHandle(compressedNamed, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                Check(NativeStorage.DeviceIoControl(file, 0x9c040, [2, 0], 2, [], 0, out _, IntPtr.Zero), "SetNamedSyntheticCompression");
            File.WriteAllText(compressedNamed + ":meta", "synthetic named stream");
            Make("s.a", [], NtfsLookupReason.Sparse | NtfsLookupReason.NamedData);
            string sparseNamed = files[^1].Path;
            using (var file = new FileStream(sparseNamed, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                Check(NativeStorage.DeviceIoControl(file.SafeFileHandle, 0x900c4, null, 0, [], 0, out _, IntPtr.Zero), "SetNamedSyntheticSparse");
                file.SetLength(2 * 1024 * 1024); file.Flush(true);
            }
            File.WriteAllText(sparseNamed + ":meta", "synthetic named stream");
            foreach (int length in new[] { 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 255, 256, 400, 512, 768, 1024, 4096 })
                Make("size-" + length + ".bin", new byte[length], expected: null);

            // Fixture preparation: settle our own link/stream/compression metadata
            // before read-only record comparison. No writable volume is opened.
            foreach (string named in new[] { ordinaryNamed, compressedNamed, sparseNamed })
            {
                using var stream = new FileStream(named + ":meta", FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                stream.Flush(flushToDisk: true);
                Check(true, "FlushSyntheticNamedStream");
            }
            foreach (var item in files)
            {
                using var prepared = new FileStream(item.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                prepared.Flush(flushToDisk: true);
                Check(true, "FlushSyntheticFileMetadata");
            }

            int recordOrdinal = 0;
            NtfsRecord ReadRecord(string path, bool directory)
            {
                recordOrdinal++;
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
                string decodeCode = "DecodeKernelRecord-" + decoded.Code;
                if (decoded.Code == "FixupMismatch")
                {
                    var bytes = output.AsSpan(12, recordSize);
                    int usa = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
                    int tailCount = recordSize / sector, protectedCount = 0, restoredCount = 0;
                    ushort mark = BinaryPrimitives.ReadUInt16LittleEndian(bytes[usa..]);
                    for (int i = 1; i <= tailCount; i++)
                    {
                        ushort tail = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * sector - 2)..]);
                        if (tail == mark) protectedCount++;
                        if (tail == BinaryPrimitives.ReadUInt16LittleEndian(bytes[(usa + i * 2)..])) restoredCount++;
                    }
                    uint usedBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
                    decodeCode += $"-Case{recordOrdinal}-P{protectedCount}-R{restoredCount}-S{tailCount}-U{usedBytes}";
                }
                Check(decoded.Status == NtfsDecodeStatus.Decoded && decoded.Record != null, decodeCode);
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
            Check(decodedFiles.Count == files.Count - aliasCount, "HardLinkObjectDeduplication");
            var dir = ReadRecord(data, true);
            Check(dir.Names.All(n => n.Namespace == 2 || n.ParentReference != dir.Reference), "SyntheticDirectoryParent");
            var lookupItems = decodedFiles.Values.Select(item => new NtfsLookupItem(serial, item, objectPaths[item.Reference])).ToArray();
            var experiment = NtfsLookupExperiment.Run(lookupItems, Check);
            var leaseExperiment = NtfsLeaseExperiment.Run(lookupItems, Check);
            NtfsLookupItem ItemAt(string path) => lookupItems.Single(item => item.Path == path);
            var special = NtfsSpecialEvidence.Run([
                ("CompressedZero", ItemAt(compressedZero)), ("CompressedRandom", ItemAt(compressedRandom)),
                ("SparseHole", ItemAt(sparseHole)), ("SparseWritten", ItemAt(sparseWritten))],
                [ItemAt(ordinaryNamed), ItemAt(compressedNamed), ItemAt(sparseNamed)], Check);
            var specialCost = NtfsSpecialCostExperiment.Run(lookupItems, Check);
            WriteSummary(summaryPath, "passed", checks, 0, "None", experiment, leaseExperiment, special, specialCost);
            Console.WriteLine("PASS NTFS live checks: " + checks + "; known synthetic files only");
            return 0;
        }
        catch (Exception error)
        {
            if (error is FixtureFailure specific) failureCode = specific.Code;
            else if (error is NtfsFixtureSourceException sourceFailure) failureCode = sourceFailure.Code;
            else if (error is NtfsFixtureGuardException guardFailure) failureCode = guardFailure.Code;
            else failureCode = error switch
            {
                UnauthorizedAccessException => "AccessDenied", IOException => "FixtureIO",
                JsonException => "ManifestJson", _ => "FixtureFailure"
            };
            if (summaryPath != null) try
            {
                if (error is NtfsFixtureSourceException or NtfsFixtureGuardException)
                {
                    int nativeError = error switch
                    {
                        NtfsFixtureSourceException sourceError when sourceError.NativeErrorCode >= 0 => sourceError.NativeErrorCode,
                        NtfsFixtureGuardException guardError when guardError.NativeErrorCode >= 0 => guardError.NativeErrorCode,
                        _ => 0
                    };
                    string nativeOperation = error is NtfsFixtureSourceException sourceErrorWithOperation &&
                        sourceErrorWithOperation.NativeCode is "LockFailed" or "SeekFailed" or "ReadFailed" or "ShortRead" or "AllocationFailed" or "BufferReleaseFailed"
                        ? sourceErrorWithOperation.NativeCode : "None";
                    File.WriteAllText(summaryPath, JsonSerializer.Serialize(new { schema = 1, status = "failed", checks,
                        failures = 1, code = failureCode, native_error = nativeError, native_operation = nativeOperation }));
                }
                else WriteSummary(summaryPath, "failed", checks, 1, failureCode);
            } catch { }
            Console.Error.WriteLine("FAIL NTFS live " + failureCode + "; checks=" + checks);
            return 1;
        }
    }

    private static void WriteSummary(string path, string status, int checks, int failures, string code,
        NtfsLookupExperimentReport? experiment = null, NtfsLeaseExperimentReport? leaseExperiment = null,
        NtfsSpecialLookupReport? special = null, NtfsSpecialCostReport? specialCost = null) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new { schema = 1, status, checks, failures, code, lookup_experiment = experiment,
            lease_experiment = leaseExperiment, special_lookup = special, special_cost = specialCost }));
    private sealed class FixtureFailure(string code) : Exception { internal string Code { get; } = code; }
}
