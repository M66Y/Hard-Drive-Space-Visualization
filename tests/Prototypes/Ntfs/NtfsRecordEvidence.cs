using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsReplayReport(int Records, int Matching, int Mismatching);

// Local-only, bounded reproduction evidence. Payloads are FILE record bodies,
// never the IOCTL output header. Schema 2 binds the checked header/native context
// separately. Offline replay proves self-consistency, not past live stability.
internal sealed class NtfsRecordEvidence
{
    private const int MaximumBytes = 512 * 1024, MaximumManifest = 64 * 1024;
    private const string Prefix = "ntfs-record-evidence-";
    private readonly string nonce;
    private readonly object gate = new();
    internal string Root { get; }
    private sealed record KernelBinding(ulong ReturnedReference, int ReturnedLength, int ReturnedBytes,
        long LogicalBytes, long AllocatedBytes, uint LinkCount, long ModifiedFileTimeUtc, ulong IdentityHigh,
        bool WritableHandleOpen);
    private sealed record Entry(string CaseId, string Payload, int Length, string Sha256,
        ulong Volume, ulong Reference, int SectorSize, int ClusterSize, ushort Major, ushort Minor,
        string ExpectedCode, string ExpectedStatus, string Source, KernelBinding? Kernel);
    private sealed record Manifest(int Schema, string Nonce, IReadOnlyList<Entry> Records);

    private NtfsRecordEvidence(string root, string value) { Root = root; nonce = value; }
    private static InvalidDataException Invalid() => new("EvidenceInvalid");
    private static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (InvalidDataException) { throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
        catch (ArgumentException) { throw Invalid(); }
        catch (NotSupportedException) { throw Invalid(); }
        catch (UnauthorizedAccessException) { throw new IOException("EvidenceIO"); }
        catch (IOException) { throw new IOException("EvidenceIO"); }
    }
    private static bool ShortId(string value, int limit) => value != null &&
        Regex.IsMatch(value, @"\A[A-Za-z][A-Za-z0-9-]{0," + (limit - 1) + @"}\z", RegexOptions.CultureInvariant);
    private static string Full(string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) throw Invalid();
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    private static void NoReparse(string path)
    {
        string? current = path;
        while (current != null)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid();
            current = Path.GetDirectoryName(current);
        }
    }
    private static string Session(string directory)
    {
        string root = Full(directory), leaf = Path.GetFileName(root);
        if (!Regex.IsMatch(leaf, @"\Antfs-record-evidence-[0-9a-f]{32}\z", RegexOptions.CultureInvariant) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(root)), "artifacts", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(root)) throw Invalid();
        NoReparse(root);
        return root;
    }
    internal static NtfsRecordEvidence Create(string artifactsRoot) => Guard(() =>
    {
        string parent = Full(artifactsRoot);
        if (!string.Equals(Path.GetFileName(parent), "artifacts", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(parent)) throw Invalid();
        NoReparse(parent);
        string nonce = Guid.NewGuid().ToString("N"), root = Path.Combine(parent, Prefix + nonce);
        if (Directory.Exists(root) || File.Exists(root)) throw Invalid();
        Directory.CreateDirectory(root);
        bool complete = false;
        var created = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            NoReparse(root);
            string marker = Path.Combine(root, "marker.json"), manifest = Path.Combine(root, "manifest.json");
            WriteNew(marker, JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, Nonce = nonce }), () => created.Add(marker));
            WriteNew(manifest, JsonSerializer.SerializeToUtf8Bytes(new Manifest(2, nonce, [])), () => created.Add(manifest));
            complete = true;
            return new NtfsRecordEvidence(root, nonce);
        }
        finally
        {
            if (!complete)
            {
                NoReparse(root);
                var actual = Directory.EnumerateFileSystemEntries(root).Take(3).ToArray();
                if (actual.All(created.Contains))
                {
                    foreach (string path in actual) RemoveNew(root, path);
                    Directory.Delete(root, false);
                }
            }
        }
    });

    internal string Capture(string caseId, byte[] payload, ulong volume, ulong reference, int sectorSize,
        int clusterSize, ushort major, ushort minor, string expectedCode) =>
        CaptureCore(caseId, payload, volume, reference, sectorSize, clusterSize, major, minor, expectedCode, null);

    internal string CaptureKernel(string caseId, byte[] ioctlOutput, int returnedBytes, FileStorageInfo before,
        FileStorageInfo after, int sectorSize, int clusterSize, ushort major, ushort minor, string expectedCode) => Guard(() =>
    {
        if (ioctlOutput == null || returnedBytes < 12 || returnedBytes > ioctlOutput.Length || returnedBytes > 65536 + 12 ||
            before == null || after == null || before.Identity != after.Identity || before.Identity.High != 0 ||
            before.LogicalBytes < 0 || before.AllocatedBytes < 0 || before.LinkCount == 0 ||
            before.LogicalBytes != after.LogicalBytes || before.AllocatedBytes != after.AllocatedBytes ||
            before.LinkCount != after.LinkCount || before.WritableHandleOpen || after.WritableHandleOpen ||
            !before.Modified.HasValue || !after.Modified.HasValue ||
            before.Modified.Value.ToFileTimeUtc() != after.Modified.Value.ToFileTimeUtc()) throw Invalid();
        // Copy exactly the declared body after validating the entire returned boundary.
        byte[] observed = ioctlOutput.AsSpan(0, returnedBytes).ToArray();
        ulong returnedReference = BinaryPrimitives.ReadUInt64LittleEndian(observed);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(observed.AsSpan(8));
        if (length is < 1024 or > 65536 || length % 512 != 0 || returnedBytes < 12 + length ||
            (returnedReference & NtfsRecordDecoder.MaximumRecordNumber) !=
            (before.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber)) throw Invalid();
        var kernel = new KernelBinding(returnedReference, (int)length, returnedBytes, before.LogicalBytes,
            before.AllocatedBytes, before.LinkCount, before.Modified.Value.ToFileTimeUtc(), before.Identity.High, false);
        return CaptureCore(caseId, observed.AsSpan(12, (int)length).ToArray(), before.Identity.Volume,
            before.Identity.Low, sectorSize, clusterSize, major, minor, expectedCode, kernel);
    });

    private string CaptureCore(string caseId, byte[] payload, ulong volume, ulong reference, int sectorSize,
        int clusterSize, ushort major, ushort minor, string expectedCode, KernelBinding? kernel) => Guard(() =>
    {
        lock (gate)
        {
            string root = Session(Root);
            var entries = ReadManifest(root, nonce);
            if (payload == null || payload.Length is < 1024 or > 65536 || payload.Length % 512 != 0 ||
                !ShortId(caseId, 40) || !ShortId(expectedCode, 80) || !Geometry(sectorSize) || !Geometry(clusterSize)) throw Invalid();
            if (entries.Count >= 6) return "LimitReached";
            if (entries.Any(item => string.Equals(item.CaseId, caseId, StringComparison.OrdinalIgnoreCase))) throw Invalid();
            // Own an immutable copy even if the caller reuses its input buffer.
            byte[] owned = (byte[])payload.Clone();
            var decoded = NtfsRecordDecoder.DecodeKernelReturned(owned, sectorSize,
                reference & NtfsRecordDecoder.MaximumRecordNumber, clusterSize, major, minor);
            string name = "record-" + (entries.Count + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".bin";
            var added = new Entry(caseId, name, owned.Length, Convert.ToHexString(SHA256.HashData(owned)), volume,
                reference, sectorSize, clusterSize, major, minor, expectedCode, decoded.Status.ToString(),
                kernel == null ? "SyntheticPayload" : "KernelIoctl", kernel);
            var next = entries.Append(added).ToArray();
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new Manifest(2, nonce, next));
            if (json.Length > MaximumManifest || owned.Length + json.Length + entries.Sum(item => item.Length) + 1024 > MaximumBytes)
                return "LimitReached";
            string pending = Path.Combine(root, "manifest.pending"), body = Path.Combine(root, name);
            bool bodyCreated = false, pendingCreated = false, committed = false;
            try
            {
                // Verify ownership again before creating any file. CreateNew never overwrites.
                _ = ReadManifest(root, nonce);
                WriteNew(body, owned, () => bodyCreated = true);
                WriteNew(pending, json, () => pendingCreated = true);
                NoReparse(root); NoReparse(body); NoReparse(pending); NoReparse(Path.Combine(root, "manifest.json"));
                File.Move(pending, Path.Combine(root, "manifest.json"), true);
                pendingCreated = false; committed = true;
                return "Saved";
            }
            finally
            {
                if (!committed)
                {
                    // Only this attempt's exact new files are eligible for cleanup.
                    try { if (pendingCreated) RemoveNew(root, pending); }
                    finally { if (bodyCreated) RemoveNew(root, body); }
                }
            }
        }
    });

    internal static NtfsReplayReport Replay(string sessionDirectory) => Guard(() =>
    {
        string root = Session(sessionDirectory);
        var entries = ReadManifest(root, Path.GetFileName(root)[Prefix.Length..]);
        int matching = 0;
        foreach (var entry in entries)
        {
            byte[] bytes = ReadBounded(Path.Combine(root, entry.Payload), 65536);
            if (bytes.Length != entry.Length || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), entry.Sha256, StringComparison.Ordinal)) throw Invalid();
            var decoded = NtfsRecordDecoder.DecodeKernelReturned(bytes, entry.SectorSize,
                entry.Reference & NtfsRecordDecoder.MaximumRecordNumber, entry.ClusterSize, entry.Major, entry.Minor);
            bool identity = decoded.Record == null || decoded.Record.Reference == entry.Reference;
            if (decoded.Code == entry.ExpectedCode && decoded.Status.ToString() == entry.ExpectedStatus && identity) matching++;
        }
        return new NtfsReplayReport(entries.Count, matching, entries.Count - matching);
    });

    internal void DeleteOwned() => Guard(() =>
    {
        lock (gate)
        {
            string root = Session(Root);
            var entries = ReadManifest(root, nonce);
            // Never recurse: an unknown file, directory or reparse point prevents deletion.
            foreach (var entry in entries) RemoveNew(root, Path.Combine(root, entry.Payload));
            RemoveNew(root, Path.Combine(root, "manifest.json"));
            RemoveNew(root, Path.Combine(root, "marker.json"));
            NoReparse(root);
            Directory.Delete(root, false);
            return true;
        }
    });

    private static bool Geometry(int value) => value is >= 512 and <= 65536 && (value & (value - 1)) == 0;
    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!names.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)) throw Invalid();
        if (seen.Count != names.Length) throw Invalid();
    }
    private static string Text(JsonElement value, string name)
    {
        var item = value.GetProperty(name);
        if (item.ValueKind != JsonValueKind.String) throw Invalid();
        return item.GetString() ?? throw Invalid();
    }
    private static int Integer(JsonElement value, string name)
    {
        if (!value.GetProperty(name).TryGetInt32(out int number)) throw Invalid();
        return number;
    }
    private static ulong Unsigned(JsonElement value, string name)
    {
        if (!value.GetProperty(name).TryGetUInt64(out ulong number)) throw Invalid();
        return number;
    }
    private static List<Entry> ReadManifest(string root, string nonce)
    {
        root = Session(root);
        if (Path.GetFileName(root) != Prefix + nonce) throw Invalid();
        using (var marker = JsonDocument.Parse(ReadBounded(Path.Combine(root, "marker.json"), 1024)))
        {
            Fields(marker.RootElement, "Schema", "Nonce");
            if (Integer(marker.RootElement, "Schema") != 1 || Text(marker.RootElement, "Nonce") != nonce) throw Invalid();
        }
        using var document = JsonDocument.Parse(ReadBounded(Path.Combine(root, "manifest.json"), MaximumManifest));
        var manifest = document.RootElement;
        Fields(manifest, "Schema", "Nonce", "Records");
        int schema = Integer(manifest, "Schema");
        if (schema is not (1 or 2) || Text(manifest, "Nonce") != nonce) throw Invalid();
        var records = manifest.GetProperty("Records");
        if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() > 6) throw Invalid();
        var entries = new List<Entry>(6);
        var cases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in records.EnumerateArray())
        {
            string[] common = ["CaseId", "Payload", "Length", "Sha256", "Volume", "Reference", "SectorSize", "ClusterSize", "Major", "Minor", "ExpectedCode", "ExpectedStatus"];
            Fields(value, schema == 1 ? common : [.. common, "Source", "Kernel"]);
            string caseId = Text(value, "CaseId"), payload = Text(value, "Payload"), hash = Text(value, "Sha256"), code = Text(value, "ExpectedCode"), status = Text(value, "ExpectedStatus");
            int length = Integer(value, "Length"), sector = Integer(value, "SectorSize"), cluster = Integer(value, "ClusterSize"), major = Integer(value, "Major"), minor = Integer(value, "Minor");
            string expectedName = "record-" + (entries.Count + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".bin";
            if (!ShortId(caseId, 40) || !cases.Add(caseId) || payload != expectedName || !ShortId(code, 80) ||
                !Regex.IsMatch(hash, @"\A[0-9A-F]{64}\z", RegexOptions.CultureInvariant) || length is < 1024 or > 65536 || length % 512 != 0 ||
                !Geometry(sector) || !Geometry(cluster) || major is < 0 or > ushort.MaxValue || minor is < 0 or > ushort.MaxValue ||
                status is not ("Decoded" or "Free" or "Invalid" or "Unsupported")) throw Invalid();
            string source = schema == 1 ? "SyntheticPayload" : Text(value, "Source");
            KernelBinding? kernel = null;
            if (schema == 2)
            {
                var binding = value.GetProperty("Kernel");
                if (source == "SyntheticPayload")
                {
                    if (binding.ValueKind != JsonValueKind.Null) throw Invalid();
                }
                else if (source == "KernelIoctl")
                {
                    Fields(binding, "ReturnedReference", "ReturnedLength", "ReturnedBytes", "LogicalBytes", "AllocatedBytes", "LinkCount", "ModifiedFileTimeUtc", "IdentityHigh", "WritableHandleOpen");
                    ulong links = Unsigned(binding, "LinkCount");
                    long Logical(string name)
                    {
                        if (!binding.GetProperty(name).TryGetInt64(out long number) || number < 0) throw Invalid();
                        return number;
                    }
                    var writable = binding.GetProperty("WritableHandleOpen");
                    long modified = Logical("ModifiedFileTimeUtc");
                    if (links is < 1 or > uint.MaxValue || writable.ValueKind != JsonValueKind.False ||
                        Unsigned(binding, "IdentityHigh") != 0) throw Invalid();
                    _ = DateTime.FromFileTimeUtc(modified);
                    kernel = new(Unsigned(binding, "ReturnedReference"), Integer(binding, "ReturnedLength"), Integer(binding, "ReturnedBytes"),
                        Logical("LogicalBytes"), Logical("AllocatedBytes"), (uint)links, modified, 0, false);
                    if (kernel.ReturnedLength != length || kernel.ReturnedBytes < 12 + length || kernel.ReturnedBytes > 65536 + 12 ||
                        (kernel.ReturnedReference & NtfsRecordDecoder.MaximumRecordNumber) !=
                        (Unsigned(value, "Reference") & NtfsRecordDecoder.MaximumRecordNumber)) throw Invalid();
                }
                else throw Invalid();
            }
            entries.Add(new(caseId, payload, length, hash, Unsigned(value, "Volume"), Unsigned(value, "Reference"), sector, cluster,
                (ushort)major, (ushort)minor, code, status, source, kernel));
        }
        var allowed = new HashSet<string>(entries.Select(item => item.Payload), StringComparer.Ordinal) { "marker.json", "manifest.json" };
        var files = Directory.EnumerateFileSystemEntries(root).Take(9).ToArray();
        if (files.Length != allowed.Count) throw Invalid();
        long bytes = 0;
        foreach (string path in files)
        {
            NoReparse(path);
            if (!allowed.Remove(Path.GetFileName(path)) || (File.GetAttributes(path) & FileAttributes.Directory) != 0) throw Invalid();
            long length = new FileInfo(path).Length;
            if (length < 0 || length > MaximumManifest || (bytes += length) > MaximumBytes) throw Invalid();
        }
        foreach (var entry in entries)
        {
            byte[] payload = ReadBounded(Path.Combine(root, entry.Payload), 65536);
            if (payload.Length != entry.Length || Convert.ToHexString(SHA256.HashData(payload)) != entry.Sha256) throw Invalid();
        }
        return entries;
    }
    private static byte[] ReadBounded(string path, int maximum)
    {
        NoReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 1 || stream.Length > maximum) throw Invalid();
        byte[] result = new byte[(int)stream.Length];
        stream.ReadExactly(result);
        if (stream.Position != stream.Length) throw Invalid();
        NoReparse(path);
        return result;
    }
    private static void WriteNew(string path, byte[] value, Action? created = null)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        created?.Invoke();
        stream.Write(value);
        stream.Flush(true);
    }
    private static void RemoveNew(string root, string path)
    {
        if (Path.GetDirectoryName(path) != root) throw Invalid();
        NoReparse(root); NoReparse(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw Invalid();
        File.Delete(path);
    }
}
