using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DiskAtlas;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsKernelRead(byte[] Output, int ReturnedBytes, FileStorageInfo Before,
    FileStorageInfo After, int Sector, int Cluster, ushort Major, ushort Minor);
internal sealed record NtfsKernelMatrixCase(string CaseId, string Stage, string Kind, int NameLength,
    bool HardLink, bool NamedStream, long LogicalBytes);
internal sealed record NtfsKernelMatrixRow(string CaseId, string Stage, string Kind, int NameLength,
    string Status, string Code, int ProtectedTails, int RestoredTails, int Sectors, int UsedBytes, int VisibleNames);
internal sealed record NtfsKernelMatrixReport(string Revision, int Objects, int Requests, int Decoded,
    int DecodeFailures, int Captured, int CaptureFailures, int CaptureLimited, IReadOnlyList<NtfsKernelMatrixRow> Rows);

// This finite generator is called only after the live entry point has bound a fresh
// VHD to its nonce and geometry. It never enumerates a volume or accepts a device.
internal static class NtfsKernelMatrix
{
    internal const string Revision = "kernel-matrix-v1";
    internal const int MaximumObjects = 24, MaximumRequests = 96;
    internal static IReadOnlyList<string> ReadStages { get; } = Array.AsReadOnly(new[] { "Initial", "Reopened", "Flushed" });
    internal static IReadOnlyList<NtfsKernelMatrixCase> Cases { get; } = BuildCases();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string name, string existing, IntPtr reserved);

    private static IReadOnlyList<NtfsKernelMatrixCase> BuildCases()
    {
        var result = new List<NtfsKernelMatrixCase>(21);
        foreach (string phase in new[] { "A", "B", "C" })
            foreach (string kind in new[] { "Ordinary", "Compressed", "Sparse" })
                foreach (int length in phase == "A" ? new[] { 8, 24, 64 } : new[] { 8, 64 })
                    result.Add(new(phase + "-" + kind + "-" + length, phase, kind, length,
                        phase == "B", phase == "C", kind == "Sparse" ? 2 * 1024 * 1024 : 131072));
        return result.AsReadOnly();
    }

    internal static string FileName(int length)
    {
        if (length is not (8 or 24 or 64)) throw new InvalidDataException("MatrixNameLength");
        return "f" + new string('x', length - 5) + ".bin";
    }

    internal static string CleanupCapture(int captured, Action cleanup)
    {
        if (captured is < 0 or > 6) throw new InvalidDataException("MatrixCaptureCount");
        if (captured > 0) return "Retained";
        try { cleanup(); return "EmptyRemoved"; }
        catch (IOException) { return "CleanupFailed"; }
        catch (UnauthorizedAccessException) { return "CleanupFailed"; }
    }

    internal static NtfsKernelMatrixReport Run(string dataDirectory, Func<string, NtfsKernelRead> read,
        NtfsRecordEvidence evidence, Action<bool, string> check)
    {
        string root = Path.GetFullPath(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory) || Directory.Exists(root) || File.Exists(root))
            throw new InvalidDataException("MatrixFreshDirectory");
        string parent = Path.GetDirectoryName(root) ?? throw new InvalidDataException("MatrixDirectory");
        NoReparse(parent);
        Directory.CreateDirectory(root);
        NoReparse(root);
        var rows = new List<NtfsKernelMatrixRow>(63);
        int decoded = 0, failures = 0, captured = 0, captureFailures = 0, limited = 0;
        check(Cases.Count == 21 && Cases.Count <= MaximumObjects && Cases.Count * ReadStages.Count <= MaximumRequests,
            "MatrixFixedBudget");
        foreach (var item in Cases)
        {
            string directory = Path.Combine(root, item.CaseId);
            if (Directory.Exists(directory) || File.Exists(directory)) throw new InvalidDataException("MatrixCaseFresh");
            Directory.CreateDirectory(directory); NoReparse(directory);
            string path = Path.Combine(directory, FileName(item.NameLength));
            Prepare(item, path, check);
            foreach (string stage in ReadStages)
            {
                if (rows.Count >= MaximumRequests) throw new InvalidDataException("MatrixRequestBudget");
                if (stage == "Reopened")
                {
                    using var reopened = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    check(reopened.Length == item.LogicalBytes, "MatrixReopenedLength");
                }
                if (stage == "Flushed")
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) stream.Flush(true);
                    if (item.NamedStream)
                    {
                        using var stream = new FileStream(path + ":meta", FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                        stream.Flush(true);
                    }
                }
                // Any I/O/identity/geometry failure is infrastructure failure and propagates.
                NtfsKernelRead observation = read(path);
                Require(observation.Before.LogicalBytes == item.LogicalBytes &&
                    observation.Before.LinkCount == (item.HardLink ? 2u : 1u), "MatrixPreparedNativeShape", check);
                var row = Observe(item, stage, observation);
                rows.Add(row);
                if (row.Status == "Decoded") { decoded++; continue; }
                failures++;
                if (captured >= 6) { limited++; continue; }
                try
                {
                    string outcome = evidence.CaptureKernel(row.CaseId, observation.Output, observation.ReturnedBytes,
                        observation.Before, observation.After, observation.Sector, observation.Cluster,
                        observation.Major, observation.Minor, row.Code);
                    if (outcome == "Saved") captured++;
                    else if (outcome == "LimitReached") limited++;
                    else captureFailures++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { captureFailures++; }
            }
        }
        return new(Revision, Cases.Count, rows.Count, decoded, failures, captured, captureFailures, limited, rows.AsReadOnly());
    }

    internal static NtfsKernelMatrixRow Observe(NtfsKernelMatrixCase item, string stage, NtfsKernelRead observation)
    {
        if (!Cases.Contains(item) || !ReadStages.Contains(stage)) throw new InvalidDataException("MatrixCase");
        byte[] output = observation.Output;
        if (output == null || observation.ReturnedBytes < 12 || observation.ReturnedBytes > output.Length ||
            observation.Sector != 512 || observation.Cluster != 4096 || observation.Major != 3 || observation.Minor != 1)
            throw new InvalidDataException("MatrixReadBounds");
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(8));
        if (declared is < 1024 or > 65536 || declared % 512 != 0 || declared > observation.ReturnedBytes - 12)
            throw new InvalidDataException("MatrixRecordBounds");
        ulong reference = observation.Before.Identity.Low;
        if (observation.Before.Identity != observation.After.Identity || observation.Before.Identity.High != 0 ||
            reference >> 48 == 0 || observation.Before.WritableHandleOpen || observation.After.WritableHandleOpen ||
            observation.Before.LogicalBytes < 0 || observation.After.LogicalBytes < 0 ||
            observation.Before.AllocatedBytes < 0 || observation.After.AllocatedBytes < 0 ||
            observation.Before.LinkCount == 0 || observation.After.LinkCount == 0 ||
            observation.Before.Modified == null || observation.After.Modified == null ||
            observation.Before.LogicalBytes != observation.After.LogicalBytes ||
            observation.Before.AllocatedBytes != observation.After.AllocatedBytes ||
            observation.Before.LinkCount != observation.After.LinkCount || observation.Before.Modified != observation.After.Modified ||
            (BinaryPrimitives.ReadUInt64LittleEndian(output) & NtfsRecordDecoder.MaximumRecordNumber) !=
            (reference & NtfsRecordDecoder.MaximumRecordNumber)) throw new InvalidDataException("MatrixReadIdentity");
        ReadOnlySpan<byte> payload = output.AsSpan(12, (int)declared);
        var result = NtfsRecordDecoder.DecodeKernelReturned(payload, observation.Sector,
            reference & NtfsRecordDecoder.MaximumRecordNumber, observation.Cluster, observation.Major, observation.Minor);
        if (result.Record != null && result.Record.Reference != reference) throw new InvalidDataException("MatrixDecodedIdentity");
        int sectors = payload.Length / 512, protectedTails = 0, restoredTails = 0;
        int usa = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
        if (usa >= 48 && (usa & 1) == 0 && count == sectors + 1 && usa + count * 2 <= 510)
        {
            ushort marker = BinaryPrimitives.ReadUInt16LittleEndian(payload[usa..]);
            for (int i = 1; i < count; i++)
            {
                ushort tail = BinaryPrimitives.ReadUInt16LittleEndian(payload[(i * 512 - 2)..]);
                if (tail == marker) protectedTails++;
                if (tail == BinaryPrimitives.ReadUInt16LittleEndian(payload[(usa + i * 2)..])) restoredTails++;
            }
        }
        uint used = BinaryPrimitives.ReadUInt32LittleEndian(payload[24..]);
        return new(item.CaseId + "-" + stage, stage, item.Kind, item.NameLength, result.Status.ToString(), result.Code,
            protectedTails, restoredTails, sectors, used <= payload.Length ? (int)used : -1,
            result.Status == NtfsDecodeStatus.Decoded && result.Record != null ? result.Record.Names.Count(name => name.Namespace != 2) : -1);
    }

    private static void Prepare(NtfsKernelMatrixCase item, string path, Action<bool, string> check)
    {
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            if (item.Kind == "Compressed")
                Require(NativeStorage.DeviceIoControl(file.SafeFileHandle, 0x9c040, [2, 0], 2, [], 0, out _, IntPtr.Zero), "MatrixCompressionCapability", check);
            if (item.Kind == "Sparse")
            {
                Require(NativeStorage.DeviceIoControl(file.SafeFileHandle, 0x900c4, null, 0, [], 0, out _, IntPtr.Zero), "MatrixSparseCapability", check);
                file.SetLength(item.LogicalBytes);
            }
            else
            {
                var content = new byte[131072];
                if (item.Kind == "Ordinary") new Random(417).NextBytes(content);
                file.Write(content);
            }
        }
        if (item.HardLink)
            Require(CreateHardLinkW(Path.Combine(Path.GetDirectoryName(path)!, "link.bin"), path, IntPtr.Zero), "MatrixHardLinkCapability", check);
        if (item.NamedStream)
        {
            using var stream = new FileStream(path + ":meta", FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write("synthetic named stream"u8);
        }
        FileAttributes actual = File.GetAttributes(path) & (FileAttributes.Compressed | FileAttributes.SparseFile);
        FileAttributes expected = item.Kind switch
        {
            "Compressed" => FileAttributes.Compressed,
            "Sparse" => FileAttributes.SparseFile,
            _ => 0
        };
        Require(actual == expected, "MatrixPreparedAttributes", check);
    }

    private static void Require(bool condition, string code, Action<bool, string> check)
    {
        check(condition, code);
        if (!condition) throw new IOException(code);
    }

    private static void NoReparse(string directory)
    {
        string? current = directory;
        while (current != null)
        {
            if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("MatrixDirectoryBoundary");
            current = Path.GetDirectoryName(current);
        }
    }
}
