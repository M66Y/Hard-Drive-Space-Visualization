using System.Runtime.InteropServices;
using DiskAtlas;

namespace DiskAtlas.NtfsPrototype;

// Only this invocation's new, flat TEMP directory is written or queried.
// No personal directory, volume device, raw record or VSS source is opened.
internal static class NtfsSnapshotMetadataTests
{
    private const string Prefix = "AsukaLens-snapshot-metadata-";
    private const uint SetCompression = 0x0009C040, SetSparse = 0x000900C4;

    internal static int Run()
    {
        int passed = 0, skipped = 0;
        void Check(bool value, string code)
        {
            if (!value) throw new InvalidOperationException("Snapshot metadata check failed: " + code);
            passed++; Console.WriteLine("PASS snapshot metadata " + code);
        }
        void Skip(string code, int nativeError = 0)
        { skipped++; Console.WriteLine("SKIP snapshot metadata " + code + " native=" + nativeError); }
        Check(NtfsSnapshotMetadata.MetadataReadAccess == 0x80, "lookup uses read attributes access");
        Check((NtfsSnapshotMetadata.MetadataReadAccess & 1) == 0, "lookup does not request file data access");
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string root = Directory.CreateTempSubdirectory(Prefix).FullName;
        string[] names = ["ordinary.bin", "empty.bin", "compressed.bin", "sparse.bin"];
        var paths = names.Select(name => Path.Combine(root, name)).ToArray();
        try
        {
            Check(Path.GetDirectoryName(Path.GetFullPath(root))!.Equals(temp, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith(Prefix, StringComparison.Ordinal) &&
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "new fixture is bound to the expected temporary parent");
            if (!new DriveInfo(Path.GetPathRoot(root)!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                Skip("NTFS temporary-file checks unavailable");
                return passed;
            }
            File.WriteAllBytes(paths[0], Enumerable.Repeat((byte)0x35, 8192).ToArray());
            File.WriteAllBytes(paths[1], []);
            var modified = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(paths[0], modified); File.SetLastWriteTimeUtc(paths[1], modified);

            void Compare(string path, bool directory, string code)
            {
                var attributes = File.GetAttributes(path);
                Check(NativeStorage.TryRead(path, directory, attributes, out var expected, out _) && expected != null,
                    code + " independent native baseline available");
                var actual = NtfsSnapshotMetadata.Read(path, directory, expected!.Identity.Volume, default);
                Check(actual.Directory == directory && !actual.DeletePending && actual.Storage.Identity == expected.Identity,
                    code + " complete identity and type match");
                Check(actual.Storage.LogicalBytes == expected.LogicalBytes, code + " logical length matches");
                Check(actual.Storage.AllocatedBytes == expected.AllocatedBytes, code + " allocated bytes match");
                Check(actual.Storage.LinkCount == expected.LinkCount, code + " hard link count matches");
                Check(actual.Storage.Modified.HasValue && actual.Storage.Modified == expected.Modified, code + " modification time matches");
            }
            Compare(paths[0], false, "ordinary");
            Compare(paths[1], false, "empty");
            Compare(root, true, "directory");

            bool Special(string path, bool compressed)
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                byte[]? input = compressed ? [1, 0] : null;
                if (!NativeStorage.DeviceIoControl(file.SafeFileHandle, compressed ? SetCompression : SetSparse,
                    input, input?.Length ?? 0, [], 0, out _, IntPtr.Zero))
                {
                    Skip(compressed ? "compression capability unavailable" : "sparse capability unavailable", Marshal.GetLastWin32Error());
                    return false;
                }
                if (compressed) file.Write(new byte[65536]);
                else { file.SetLength(1024 * 1024); file.Position = 128 * 1024; file.Write(new byte[4096]); }
                file.Flush(true); return true;
            }
            if (Special(paths[2], true))
            {
                File.SetLastWriteTimeUtc(paths[2], modified);
                Check((File.GetAttributes(paths[2]) & FileAttributes.Compressed) != 0, "compressed fixture reports its special attribute");
                Compare(paths[2], false, "compressed read attributes compression info");
            }
            if (Special(paths[3], false))
            {
                File.SetLastWriteTimeUtc(paths[3], modified);
                Check((File.GetAttributes(paths[3]) & FileAttributes.SparseFile) != 0, "sparse fixture reports its special attribute");
                Compare(paths[3], false, "sparse read attributes compression info");
            }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool observedCancellation = false;
            try { _ = NtfsSnapshotMetadata.Read(paths[0], false, 0, cancelled.Token); }
            catch (OperationCanceledException) { observedCancellation = true; }
            Check(observedCancellation, "cancelled query returns no metadata result");
            Console.WriteLine($"Snapshot metadata checks: {passed}; capability skips: {skipped}.");
            return passed;
        }
        finally
        {
            // Do not recursively delete. Only the four known files created here
            // are eligible; unexpected contents or a changed root stop cleanup.
            string absolute = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(absolute), temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(absolute).StartsWith(Prefix, StringComparison.Ordinal) ||
                (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Snapshot metadata fixture cleanup boundary");
            foreach (string path in paths)
            {
                if (!File.Exists(path)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Snapshot metadata fixture cleanup boundary");
                File.Delete(path);
            }
            Directory.Delete(absolute, recursive: false);
        }
    }
}
