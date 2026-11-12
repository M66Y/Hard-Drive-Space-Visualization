using System.Runtime.InteropServices;
using DiskAtlas;

namespace DiskAtlas.NtfsPrototype;

// Native checks open only this invocation's newly created, flat temporary tree.
// Synthetic observations are kept separate from the actual-file capability checks.
internal static class NtfsSpecialLeaseTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string name, string existing, IntPtr reserved);

    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS special lease check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS special lease " + name);
        }
        const ulong volume = 41;
        ulong parent = NtfsRecordFixtures.Reference(5, 1), reference = NtfsRecordFixtures.Reference(52, 3);
        var compressed = new NtfsRecord(reference, 0, false, 1, [new(parent, "compressed.bin", 1)],
            65536, null, NtfsLookupReason.Compressed);
        var sparse = compressed with { Names = [new(parent, "sparse.bin", 1)], LookupReasons = NtfsLookupReason.Sparse };
        var first = new NtfsSpecialObservation(new(volume, reference, 0), 65536, 4096, 1, false, false,
            (uint)(FileAttributes.Archive | FileAttributes.Compressed), 133000000000000000, 133000000000000001,
            2, 16, 12, 12);
        var sparseFirst = first with { Attributes = (uint)(FileAttributes.Archive | FileAttributes.SparseFile), CompressionFormat = 0 };
        string temp = Path.GetFullPath(Path.GetTempPath());
        const string prefix = "AsukaLens-special-lease-";
        string nonce = Guid.NewGuid().ToString("N");
        string root = Path.Combine(temp, prefix + nonce);
        string compressedPath = Path.Combine(root, "compressed.bin"), sparsePath = Path.Combine(root, "sparse.bin");
        Check(NtfsSpecialLease.LayoutSizes == (40, 24, 24, 16), "native metadata structures have the expected ABI sizes");
        Check(NtfsSpecialLease.Preflight(compressed, compressedPath) == null, "single compressed reason is eligible");
        Check(NtfsSpecialLease.Preflight(sparse, sparsePath) == null, "single sparse reason is eligible");
        Check(NtfsSpecialLease.Validate(volume, compressed, first, first) == null, "stable compressed observations are accepted");
        Check(NtfsSpecialLease.Validate(volume, sparse, sparseFirst, sparseFirst) == null, "stable sparse observations are accepted");
        var zero = sparseFirst with { AllocatedBytes = 0 };
        Check(NtfsSpecialLease.Validate(volume, sparse, zero, zero) == null, "known zero sparse allocation is distinct from unknown");

        void PreflightFails(NtfsRecord record, string name, string? path = null) =>
            Check(NtfsSpecialLease.Preflight(record, path ?? compressedPath) != null, name);
        foreach (var reason in Enum.GetValues<NtfsLookupReason>().Where(reason => reason is not (NtfsLookupReason.Compressed or NtfsLookupReason.Sparse)))
            PreflightFails(compressed with { LookupReasons = reason }, "other single reason is not silently broadened: " + reason);
        foreach (var reason in new[] { NtfsLookupReason.Compressed | NtfsLookupReason.Sparse,
            NtfsLookupReason.Compressed | NtfsLookupReason.NamedData, NtfsLookupReason.Sparse | NtfsLookupReason.NamedData,
            NtfsLookupReason.Compressed | NtfsLookupReason.ResidentAllocation, NtfsLookupReason.Sparse | NtfsLookupReason.AttributeList,
            NtfsLookupReason.Compressed | NtfsLookupReason.ExtensionRecord })
            PreflightFails(compressed with { LookupReasons = reason }, "combined reasons remain unsupported");
        PreflightFails(compressed with { IsDirectory = true }, "directory input is rejected");
        PreflightFails(compressed with { BaseReference = 1 }, "extension input is rejected");
        PreflightFails(compressed with { Reference = 52 }, "missing sequence is rejected");
        PreflightFails(compressed with { LogicalBytes = null }, "unknown EOF is rejected");
        PreflightFails(compressed with { LogicalBytes = -1 }, "negative EOF is rejected");
        PreflightFails(compressed with { AllocatedBytes = 0 }, "already-known allocation is not a lookup candidate");
        PreflightFails(compressed with { Names = [] }, "missing names are rejected");
        PreflightFails(compressed with { LinkCount = 2 }, "incomplete raw link names are rejected");
        PreflightFails(compressed with { LinkCount = 257, Names = Enumerable.Repeat(compressed.Names[0], 257).ToArray() }, "name capacity is bounded");
        PreflightFails(compressed with { Names = [new(parent, "COMPRE~1.BIN", 2)] }, "DOS-only names are rejected");
        PreflightFails(compressed with { Names = [new(5, "compressed.bin", 1)] }, "parent sequence is required");
        PreflightFails(compressed with { Names = [new(parent, "compressed.bin", 4)] }, "unknown namespace is rejected");
        foreach (string name in new[] { "", "bad/name", "bad\\name", "bad:name", "bad\0name", new string('x', 256) })
            PreflightFails(compressed with { Names = [new(parent, name, 1)] }, "invalid name is rejected");
        PreflightFails(compressed, "relative path is rejected", "compressed.bin");
        PreflightFails(compressed, "empty path is rejected", " ");
        PreflightFails(compressed, "NUL path is rejected", compressedPath + '\0');
        PreflightFails(compressed, "unrelated basename is rejected", sparsePath);
        var dos = compressed with { LinkCount = 2, Names = [compressed.Names[0], new(parent, "COMPRE~1.BIN", 2)] };
        Check(NtfsSpecialLease.Preflight(dos, compressedPath) == null && NtfsSpecialLease.Validate(volume, dos, first, first) == null,
            "DOS alias is not a second visible native link");
        var hardLinks = compressed with { LinkCount = 2, Names = [compressed.Names[0], new(parent, "alias.bin", 1)] };
        var twice = first with { Links = 2 };
        Check(NtfsSpecialLease.Validate(volume, hardLinks, twice, twice) == null, "visible native links are counted independently of DOS aliases");

        void InvalidObservation(NtfsSpecialObservation observation, string name)
        {
            Check(NtfsSpecialLease.Validate(volume, compressed, observation, observation) != null, name + " in first observation");
            Check(NtfsSpecialLease.Validate(volume, compressed, first, observation) != null, name + " in second observation");
        }
        InvalidObservation(first with { Identity = first.Identity with { Volume = volume + 1 } }, "wrong volume is rejected");
        InvalidObservation(first with { Identity = first.Identity with { Low = reference + 1 } }, "wrong record slot is rejected");
        InvalidObservation(first with { Identity = first.Identity with { Low = NtfsRecordFixtures.Reference(52, 4) } }, "reused sequence is rejected");
        InvalidObservation(first with { Identity = first.Identity with { High = 1 } }, "full identity is required");
        InvalidObservation(first with { LogicalBytes = first.LogicalBytes + 1 }, "EOF change is rejected");
        InvalidObservation(first with { AllocatedBytes = -1 }, "negative allocation is rejected");
        InvalidObservation(first with { Links = 2 }, "unexpected visible links are rejected");
        InvalidObservation(first with { DeletePending = true }, "delete-pending state is rejected");
        InvalidObservation(first with { Directory = true }, "actual directory is rejected");
        InvalidObservation(first with { Attributes = (uint)FileAttributes.Archive }, "actual special type must match the record");
        InvalidObservation(first with { Attributes = (uint)FileAttributes.SparseFile }, "sparse metadata cannot satisfy compressed evidence");
        foreach (FileAttributes flag in new[] { FileAttributes.ReparsePoint, FileAttributes.Offline, FileAttributes.Encrypted, FileAttributes.Directory, FileAttributes.Device })
            InvalidObservation(first with { Attributes = first.Attributes | (uint)flag }, "unsupported current attributes are rejected");
        foreach (uint flag in new uint[] { 0x40000, 0x400000 })
            InvalidObservation(first with { Attributes = first.Attributes | flag }, "recall attribute is rejected");
        InvalidObservation(first with { CompressionFormat = 0 }, "compressed format none is rejected");
        InvalidObservation(first with { CompressionFormat = 1 }, "default request format is not reported LZNT1");
        InvalidObservation(first with { CompressionFormat = 3 }, "unknown compression format is rejected");
        InvalidObservation(first with { UnitShift = 64 }, "unit shift is bounded");
        InvalidObservation(first with { ChunkShift = 64 }, "chunk shift is bounded");
        InvalidObservation(first with { ClusterShift = 64 }, "cluster shift is bounded");
        InvalidObservation(first with { LastWrite = -1 }, "negative last-write time is rejected");
        InvalidObservation(first with { ChangeTime = -1 }, "negative change time is rejected");
        InvalidObservation(first with { LastWrite = long.MaxValue }, "overflowing last-write time is rejected");
        InvalidObservation(first with { ChangeTime = long.MaxValue }, "overflowing change time is rejected");
        foreach (var changed in new[] { first with { AllocatedBytes = 8192 }, first with { LastWrite = first.LastWrite + 1 },
            first with { ChangeTime = first.ChangeTime + 1 }, first with { Attributes = first.Attributes | (uint)FileAttributes.Hidden },
            first with { UnitShift = 15 }, first with { ChunkShift = 11 }, first with { ClusterShift = 11 } })
            Check(NtfsSpecialLease.Validate(volume, compressed, first, changed) != null, "observable metadata change between snapshots is rejected");
        Check(NtfsSpecialLease.Validate(volume, sparse, sparseFirst with { CompressionFormat = 2 }, sparseFirst with { CompressionFormat = 2 }) != null,
            "sparse-only evidence does not accept compressed format");
        Check(NtfsSpecialLease.Validate(volume, sparse, sparseFirst with { Attributes = first.Attributes }, sparseFirst with { Attributes = first.Attributes }) != null,
            "compressed attributes do not satisfy sparse-only record");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool stopped = false;
            try { NtfsSpecialLease.Read(volume, compressed, compressedPath, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && !Directory.Exists(root), "pre-cancellation opens no synthetic file");
        }

        if (!OperatingSystem.IsWindows() || !string.Equals(new DriveInfo(Path.GetPathRoot(root)!).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("SKIP NTFS special lease native checks: Windows NTFS temporary storage required");
            return passed;
        }
        Directory.CreateDirectory(root);
        try
        {
            bool compressedSupported;
            using (var file = new FileStream(compressedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                compressedSupported = NativeStorage.DeviceIoControl(file.SafeFileHandle, 0x9c040, [2, 0], 2, [], 0, out _, IntPtr.Zero);
                if (compressedSupported) { file.Write(new byte[2 * 1024 * 1024]); file.Flush(true); }
            }
            if (compressedSupported)
            {
                NativeChecks(compressedPath, NtfsLookupReason.Compressed);
                string randomPath = Path.Combine(root, "random.bin");
                using (var file = new FileStream(randomPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    Check(NativeStorage.DeviceIoControl(file.SafeFileHandle, 0x9c040, [2, 0], 2, [], 0, out _, IntPtr.Zero), "incompressible fixture compression enabled");
                    var bytes = new byte[131072]; new Random(8371).NextBytes(bytes); file.Write(bytes); file.Flush(true);
                }
                NativeChecks(randomPath, NtfsLookupReason.Compressed, exerciseSharing: false);
            }
            else Console.WriteLine("SKIP NTFS special lease compressed native checks: feature unavailable");

            bool sparseSupported;
            using (var file = new FileStream(sparsePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                sparseSupported = NativeStorage.DeviceIoControl(file.SafeFileHandle, 0x900c4, null, 0, [], 0, out _, IntPtr.Zero);
                if (sparseSupported) { file.SetLength(4 * 1024 * 1024); file.Flush(true); }
            }
            if (sparseSupported)
            {
                var hole = NativeChecks(sparsePath, NtfsLookupReason.Sparse);
                Check(hole.AllocatedBytes == 0, "unwritten sparse fixture reports known zero allocation");
                using (var file = new FileStream(sparsePath, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    file.Position = 131072; var bytes = new byte[65536]; new Random(719).NextBytes(bytes); file.Write(bytes); file.Flush(true);
                }
                var written = NativeChecks(sparsePath, NtfsLookupReason.Sparse, exerciseSharing: false);
                Check(written.AllocatedBytes > 0 && written.AllocatedBytes < written.LogicalBytes,
                    "allocated sparse region is distinguished from a completely empty hole");
            }
            else Console.WriteLine("SKIP NTFS special lease sparse native checks: feature unavailable");
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(temp), StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(resolved) != prefix + nonce || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0 ||
                Directory.EnumerateFileSystemEntries(resolved).Any(path => (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0))
                throw new InvalidOperationException("Refused unexpected special lease fixture cleanup target");
            Directory.Delete(resolved, true);
        }
        Console.WriteLine("PASS NTFS special lease checks: " + passed + "; only owned temporary files opened");
        return passed;

        FileStorageInfo NativeChecks(string path, NtfsLookupReason reason, bool exerciseSharing = true)
        {
            Check(NativeStorage.TryRead(path, false, File.GetAttributes(path), out var observed, out _) && observed != null &&
                observed.Identity.High == 0 && (observed.Identity.Low >> 48) != 0, "native reference has full NTFS identity");
            FileStorageInfo native = observed!;
            var record = compressed with { Reference = native.Identity.Low, LogicalBytes = native.LogicalBytes,
                Names = [new(parent, Path.GetFileName(path), 1)], LookupReasons = reason };
            NtfsLeaseResult Read(NtfsRecord? candidate = null) => NtfsSpecialLease.Read(native.Identity.Volume, candidate ?? record, path);
            void Same(NtfsLeaseResult result, FileStorageInfo expected, string name) =>
                Check(result.Success && result.Storage == expected && result.HandlesOpened == 1 && result.SnapshotReads == 2, name);
            void Unknown(NtfsLeaseResult result, string name) => Check(!result.Success && result.Storage == null, name);
            Same(Read(), native, "single handle snapshots match current native reference");
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(exclusive.Length == native.LogicalBytes, "success releases its handle for an exclusive reopen");
            Unknown(Read(record with { Reference = record.Reference ^ 1 }), "wrong actual file identity remains unknown");
            Unknown(Read(record with { LogicalBytes = record.LogicalBytes + 1 }), "stale EOF remains unknown");
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(exclusive.Length == native.LogicalBytes, "failed native lookup releases its handle");
            if (!exerciseSharing) return native;
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                var blocked = Read();
                Check(!blocked.Success && blocked.Storage == null && blocked.HandlesOpened == 0 && blocked.SnapshotReads == 0,
                    "existing data writer is refused without permissive share retry");
            }
            using (var deleting = NativeStorage.CreateFileW(NativeStorage.LongPath(path), 0x10000, NativeStorage.ShareAll, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                Check(!deleting.IsInvalid, "owned fixture can open delete access");
                var blocked = Read();
                Check(!blocked.Success && blocked.Storage == null && blocked.HandlesOpened == 0 && blocked.SnapshotReads == 0,
                    "existing delete access is refused without permissive share retry");
            }
            string alias = Path.Combine(root, reason == NtfsLookupReason.Compressed ? "compressed-alias.bin" : "sparse-alias.bin");
            if (CreateHardLinkW(alias, path, IntPtr.Zero))
            {
                try
                {
                    Unknown(Read(), "new visible hard link invalidates the old name set");
                    Check(NativeStorage.TryRead(path, false, File.GetAttributes(path), out var linked, out _) && linked?.LinkCount == 2,
                        "hard link fixture has two native links");
                    var linkedRecord = record with { LinkCount = 2, Names = [record.Names[0], new(parent, Path.GetFileName(alias), 1)] };
                    Same(Read(linkedRecord), linked!, "complete hard link evidence resolves one current allocation");
                    using var writer = new FileStream(alias, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    Unknown(Read(linkedRecord), "already-open writer through another hard link is refused");
                }
                finally { File.Delete(alias); }
            }
            else Console.WriteLine("SKIP NTFS special lease hard links: native feature unavailable");
            Same(Read(), native, "sharing checks release handles and preserve native values");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool stopped = false;
            try { NtfsSpecialLease.Read(native.Identity.Volume, record, path, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "native fixture pre-cancellation propagates");
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(exclusive.Length == native.LogicalBytes, "cancelled lookup retains no native handle");
            return native;
        }
    }
}
