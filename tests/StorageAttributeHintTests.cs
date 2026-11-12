using DiskAtlas;

// Uses only this run's flat, nonce-owned temporary fixture. Observation mode
// reports differences without calling them a regression pass.
internal static class StorageAttributeHintTests
{
    private const string Prefix = "AsukaLens-storage-hints-", Marker = ".owner";
    private const int MiB = 1024 * 1024;
    private enum Kind { OrdinaryZero, OrdinaryRandom, CompressedZero, CompressedRandom, SparseHole, SparseWritten, OrdinaryWithSparseStream }

    internal static int Run(bool requireConsistent = false)
    {
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string nonce = Guid.NewGuid().ToString("N");
        string root = Path.GetFullPath(Path.Combine(temp, Prefix + nonce));
        string[] names = Enumerable.Range(0, 7).Select(index => $"f{index}.bin").ToArray();
        bool owned = false;
        int compared = 0, differences = 0, skipped = 0, cases = 0, result = 1;
        try
        {
            if (!OperatingSystem.IsWindows() || !string.Equals(new DriveInfo(Path.GetPathRoot(root)!).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("SKIP STORAGE_ATTRIBUTE_HINT NTFS_WINDOWS_REQUIRED");
                return 2;
            }
            Directory.CreateDirectory(root);
            owned = true;
            File.WriteAllText(Path.Combine(root, Marker), nonce);
            byte[] random = new byte[MiB];
            new Random(0x4153554b).NextBytes(random);
            foreach (var kind in Enum.GetValues<Kind>())
            {
                string path = Path.Combine(root, names[(int)kind]);
                if (!CreateCase(path, kind, random))
                {
                    skipped++;
                    Console.WriteLine($"SKIP STORAGE_ATTRIBUTE_HINT case={kind}; code=FEATURE_UNAVAILABLE_OR_DENIED");
                    continue;
                }
                FileAttributes actual = File.GetAttributes(path);
                if (!NativeStorage.TryRead(path, false, actual, out var reference, out _) || reference == null ||
                    reference.LogicalBytes < 0 || reference.AllocatedBytes < 0 || reference.WritableHandleOpen)
                    throw new InvalidOperationException("BASELINE_UNAVAILABLE");
                cases++;
                Console.WriteLine($"BASELINE STORAGE_ATTRIBUTE_HINT case={kind}; logical={reference.LogicalBytes}; allocated={reference.AllocatedBytes}; attributes={(uint)actual}");
                (string Label, FileAttributes Value)[] hints =
                [
                    ("Normal", FileAttributes.Normal), ("Compressed", FileAttributes.Compressed),
                    ("Sparse", FileAttributes.SparseFile), ("CompressedAndSparse", FileAttributes.Compressed | FileAttributes.SparseFile)
                ];
                foreach (var hint in hints)
                {
                    bool success = NativeStorage.TryRead(path, false, hint.Value, out var sample, out _);
                    bool same = success && sample == reference;
                    compared++;
                    if (!same) differences++;
                    Console.WriteLine($"OBSERVE STORAGE_ATTRIBUTE_HINT case={kind}; hint={hint.Label}; success={success}; logical={sample?.LogicalBytes}; allocated={sample?.AllocatedBytes}; identity_match={sample?.Identity == reference.Identity}; match={same}");
                }
                if (!NativeStorage.TryRead(path, false, File.GetAttributes(path), out var after, out _) ||
                    after != reference || File.GetAttributes(path) != actual)
                    throw new InvalidOperationException("BASELINE_CHANGED");
            }
            if (cases == 0 || skipped > 0)
            {
                Console.WriteLine($"SKIP STORAGE_ATTRIBUTE_HINT incomplete_coverage=True; cases={cases}; skipped={skipped}; comparisons={compared}; differences={differences}");
                result = requireConsistent && differences > 0 ? 1 : 2;
            }
            else if (requireConsistent)
            {
                result = differences == 0 ? 0 : 1;
                Console.WriteLine($"{(result == 0 ? "PASS" : "FAIL")} STORAGE_ATTRIBUTE_HINT regression=True; cases={cases}; comparisons={compared}; differences={differences}");
            }
            else
            {
                result = 0;
                Console.WriteLine($"OBSERVED STORAGE_ATTRIBUTE_HINT regression=False; cases={cases}; comparisons={compared}; differences={differences}");
            }
        }
        catch
        {
            Console.Error.WriteLine("FAIL STORAGE_ATTRIBUTE_HINT FIXTURE_OR_READ_FAILED");
            result = 1;
        }
        finally
        {
            if (owned)
            {
                try
                {
                    string full = Path.GetFullPath(root);
                    if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(full) != Prefix + nonce ||
                        (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException();
                    string marker = Path.Combine(full, Marker);
                    if ((File.GetAttributes(marker) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                        new FileInfo(marker).Length != nonce.Length || File.ReadAllText(marker) != nonce)
                        throw new InvalidOperationException();
                    var expected = names.Append(Marker).ToHashSet(StringComparer.Ordinal);
                    foreach (string entry in Directory.EnumerateFileSystemEntries(full))
                        if (!expected.Remove(Path.GetFileName(entry)) ||
                            (File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                            throw new InvalidOperationException();
                    Directory.Delete(full, recursive: true);
                    Console.WriteLine("CLEANUP STORAGE_ATTRIBUTE_HINT completed=True");
                }
                catch
                {
                    Console.Error.WriteLine("FAIL STORAGE_ATTRIBUTE_HINT CLEANUP_REFUSED");
                    result = 1;
                }
            }
        }
        return result;
    }

    private static bool Compression(FileStream file, bool enabled)
    {
        byte[] format = enabled ? [2, 0] : [0, 0];
        return NativeStorage.DeviceIoControl(file.SafeFileHandle, 0x0009c040, format, format.Length, [], 0, out _, IntPtr.Zero);
    }

    private static bool CreateCase(string path, Kind kind, byte[] random)
    {
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            // Avoid inherited directory compression changing an ordinary fixture's type.
            if (!Compression(file, false) && (File.GetAttributes(path) & FileAttributes.Compressed) != 0) return false;
            bool compressed = kind is Kind.CompressedZero or Kind.CompressedRandom;
            bool sparse = kind is Kind.SparseHole or Kind.SparseWritten;
            if (compressed && !Compression(file, true)) return false;
            if (sparse && !NativeStorage.DeviceIoControl(file.SafeFileHandle, 0x000900c4, null, 0, [], 0, out _, IntPtr.Zero)) return false;
            if (sparse)
            {
                file.SetLength(8L * MiB);
                if (kind == Kind.SparseWritten)
                {
                    file.Write(random, 0, 4096);
                    file.Position = file.Length - 4096;
                    file.Write(random, 4096, 4096);
                }
            }
            else if (kind == Kind.OrdinaryWithSparseStream) file.Write(random, 0, 4096);
            else file.Write(kind is Kind.OrdinaryRandom or Kind.CompressedRandom ? random : new byte[MiB]);
            file.Flush(flushToDisk: true);
        }
        FileAttributes actual = File.GetAttributes(path);
        if (kind is Kind.CompressedZero or Kind.CompressedRandom && (actual & FileAttributes.Compressed) == 0) return false;
        if (kind is Kind.SparseHole or Kind.SparseWritten && (actual & FileAttributes.SparseFile) == 0) return false;
        if (kind == Kind.OrdinaryWithSparseStream)
        {
            using var stream = new FileStream(path + ":fixture", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            if (!Compression(stream, false)) return false;
            if (!NativeStorage.DeviceIoControl(stream.SafeFileHandle, 0x000900c4, null, 0, [], 0, out _, IntPtr.Zero)) return false;
            stream.SetLength(8L * MiB);
            stream.Position = stream.Length - 4096;
            stream.Write(random, 0, 4096);
            stream.Flush(flushToDisk: true);
        }
        return true;
    }
}
