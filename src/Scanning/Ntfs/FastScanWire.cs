using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using DiskAtlas.NtfsPrototype;

namespace DiskAtlas;

// This codec carries metadata only over the authenticated, in-memory worker
// pipe. It does not create a source proof or authorize a disk operation.
internal enum FastScanWireStage { Preparing, Snapshot, Discovering, Reading, Supplementing, Building, Cleanup }
internal enum FastScanWireError
{
    Unsupported, PermissionDenied, LimitExceeded, SourceChanged, SnapshotFailed,
    ReadFailed, Cancelled, ProtocolError, CleanupUnconfirmed, InternalError
}
internal enum FastScanWireEventKind { Status, Progress, Completed, Failure }
internal sealed record FastScanWireResult(DiskEntry Root, TimeSpan Elapsed, DateTimeOffset CapturedAt,
    long ExcludedReparseEntries, IReadOnlyList<ScanIssue> Issues);
internal sealed record FastScanWireEvent(FastScanWireEventKind Kind, FastScanWireStage? Stage = null,
    ScanProgress? Progress = null, FastScanWireResult? Result = null,
    FastScanWireError? Error = null, bool CleanupConfirmed = false, FastScanFailureContext? Context = null);
internal sealed class FastScanWireException : IOException
{
    internal FastScanWireException() : base("NTFS 快速扫描通信数据无效。") { }
}

internal static class FastScanWire
{
    internal const int MaximumNodes = 1048576;
    internal const long MaximumNameCharacters = 64L * 1024 * 1024;
    internal const long MaximumWireBytes = 256L * 1024 * 1024;
    internal const int MaximumFrameBytes = 65536;
    internal const int MaximumDepth = 512;
    internal const int MaximumExclusionDetails = 2000;
    private const uint Magic = 0x314C5341;
    private const ushort Version = 3;
    private static readonly UnicodeEncoding TextEncoding = new(false, false, true);
    internal enum Frame : ushort { Request = 1, Status, Progress, TreeBegin, Node, Done, Failure, Exclusion }
    internal sealed record Node(int Index, int Parent, bool Directory, string Name, FileStorageInfo Storage, DateTime Modified, bool Incomplete = false);
    internal sealed record Header(int Nodes, long Files, long Directories, long Logical, long Allocated, long ElapsedTicks, long CapturedTicks,
        long ExcludedReparseEntries = 0, int ExclusionDetails = 0);

    internal static void Require([DoesNotReturnIf(false)] bool condition) { if (!condition) throw new FastScanWireException(); }
    private static bool Letter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    internal static string NormalizeRoot(string value)
    {
        try
        {
            Require(value != null && value.Length is >= 3 and <= 32767 && Letter(value[0]) && value[1] == ':' && value[2] is '\\' or '/');
            Require(!value[2..].Contains(':') && !value.Any(char.IsControl));
            string normalized = value.Replace('/', '\\');
            var parts = normalized[3..].Split('\\', StringSplitOptions.None);
            for (int i = 0; i < parts.Length; i++)
                Require(parts[i].Length == 0 && i == parts.Length - 1 || ValidName(parts[i]));
            normalized = Path.GetFullPath(normalized);
            return normalized.Length == 3 ? normalized : Path.TrimEndingDirectorySeparator(normalized);
        }
        catch (FastScanWireException) { throw; }
        catch { throw new FastScanWireException(); }
    }
    internal static bool ValidName(string value)
    {
        if (value.Length is < 1 or > 255 || value is "." or ".." || value[^1] is '.' or ' ' ||
            value.Any(c => char.IsControl(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')) return false;
        string device = value.Split('.')[0].ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" || device.Length == 4 &&
            (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && device[3] is >= '1' and <= '9') return false;
        try { _ = TextEncoding.GetByteCount(value); return true; } catch { return false; }
    }
    private static void Text(BinaryWriter writer, string value)
    {
        byte[] bytes;
        try { bytes = TextEncoding.GetBytes(value); } catch { throw new FastScanWireException(); }
        writer.Write(value.Length); writer.Write(bytes);
    }
    private static string Text(BinaryReader reader, int maximum)
    {
        int length = reader.ReadInt32(); Require(length >= 0 && length <= maximum && length <= (reader.BaseStream.Length - reader.BaseStream.Position) / 2);
        byte[] bytes = reader.ReadBytes(length * 2); Require(bytes.Length == length * 2);
        try { return TextEncoding.GetString(bytes); } catch { throw new FastScanWireException(); }
    }
    internal static void Write(Stream stream, Frame kind, Action<BinaryWriter> payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, true)) payload(writer);
        Require(body.Length <= MaximumFrameBytes);
        Span<byte> header = stackalloc byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version); BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)kind);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], (int)body.Length);
        stream.Write(header); stream.Write(body.GetBuffer(), 0, (int)body.Length); stream.Flush(); token.ThrowIfCancellationRequested();
    }
    internal static (Frame Kind, byte[] Payload) Read(Stream stream, ref long totalBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            Span<byte> header = stackalloc byte[12]; stream.ReadExactly(header); token.ThrowIfCancellationRequested();
            int size = BinaryPrimitives.ReadInt32LittleEndian(header[8..]); ushort kind = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            Require(BinaryPrimitives.ReadUInt32LittleEndian(header) == Magic && BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) == Version &&
                Enum.IsDefined(typeof(Frame), kind) && size is >= 0 and <= MaximumFrameBytes && totalBytes <= MaximumWireBytes - size - header.Length);
            totalBytes += size + header.Length;
            byte[] payload = new byte[size]; stream.ReadExactly(payload); token.ThrowIfCancellationRequested(); return ((Frame)kind, payload);
        }
        catch (FastScanWireException) { throw; }
        catch { token.ThrowIfCancellationRequested(); throw new FastScanWireException(); }
    }
    internal static T Parse<T>(byte[] payload, Func<BinaryReader, T> read)
    {
        try
        {
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream, Encoding.UTF8, false);
            T value = read(reader); Require(stream.Position == stream.Length); return value;
        }
        catch (FastScanWireException) { throw; }
        catch { throw new FastScanWireException(); }
    }
    internal static bool Boolean(BinaryReader reader) { byte value = reader.ReadByte(); Require(value <= 1); return value == 1; }
    internal static void WriteRequest(Stream stream, string root, CancellationToken token = default)
    {
        string normalized = NormalizeRoot(root);
        Write(stream, Frame.Request, writer => writer.Write(TextEncoding.GetBytes(normalized)), token);
    }
    internal static string ReadRequest(Stream stream, CancellationToken token = default)
    {
        long total = 0; var frame = Read(stream, ref total, token); Require(frame.Kind == Frame.Request);
        Require(frame.Payload.Length is >= 6 and <= 65534 && frame.Payload.Length % 2 == 0);
        try { return NormalizeRoot(TextEncoding.GetString(frame.Payload)); }
        catch (FastScanWireException) { throw; }
        catch { throw new FastScanWireException(); }
    }
    internal static void WriteStatus(Stream stream, FastScanWireStage stage, CancellationToken token = default)
    { Require(Enum.IsDefined(stage)); Write(stream, Frame.Status, writer => writer.Write((byte)stage), token); }
    internal static void WriteProgress(Stream stream, ScanProgress value, CancellationToken token = default)
    {
        ValidateProgress(value);
        Write(stream, Frame.Progress, writer =>
        {
            writer.Write(value.Files); writer.Write(value.Folders); writer.Write(value.Bytes); writer.Write((byte)value.Phase);
            writer.Write(value.WorkerLimit); writer.Write(value.CompletedDirectories); writer.Write(value.DiscoveredDirectories);
        }, token);
    }
    internal static void ValidateProgress(ScanProgress value) => Require(value != null && value.Files >= 0 && value.Folders >= 0 && value.Bytes >= 0 &&
        Enum.IsDefined(value.Phase) && value.WorkerLimit is >= 0 and <= 16 && value.CompletedDirectories >= 0 &&
        value.DiscoveredDirectories >= value.CompletedDirectories);
    internal static ScanProgress Progress(BinaryReader reader)
    {
        var value = new ScanProgress(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), "", (ScanPhase)reader.ReadByte(),
            reader.ReadInt32(), reader.ReadInt64(), reader.ReadInt64()); ValidateProgress(value); return value;
    }
    internal static void WriteFailure(Stream stream, FastScanWireError error, bool cleanupConfirmed, CancellationToken token = default,
        FastScanFailureContext? context = null)
    {
        Require(Enum.IsDefined(error) && (error != FastScanWireError.CleanupUnconfirmed || !cleanupConfirmed));
        Require(context == null || Enum.IsDefined(context.Stage) && Enum.IsDefined(context.Detail));
        Write(stream, Frame.Failure, writer =>
        {
            writer.Write((byte)error); writer.Write(cleanupConfirmed); writer.Write(context != null);
            if (context != null)
            {
                writer.Write((byte)context.Stage); writer.Write((byte)context.Detail); writer.Write(context.NativeError.HasValue);
                if (context.NativeError.HasValue) writer.Write(context.NativeError.Value);
            }
        }, token);
    }
    internal static void WriteResult(Stream stream, DiskEntry root, TimeSpan elapsed, DateTimeOffset capturedAt, CancellationToken token = default,
        long excludedCount = 0, IReadOnlyList<NtfsReparseBoundary>? boundaries = null)
    {
        try { WriteResultCore(stream, root, elapsed, capturedAt, token, excludedCount, boundaries); }
        catch (FastScanWireException) { token.ThrowIfCancellationRequested(); throw; }
        catch { token.ThrowIfCancellationRequested(); throw new FastScanWireException(); }
    }
    private static void WriteResultCore(Stream stream, DiskEntry root, TimeSpan elapsed, DateTimeOffset capturedAt, CancellationToken token,
        long excludedCount, IReadOnlyList<NtfsReparseBoundary>? boundaries)
    {
        token.ThrowIfCancellationRequested(); Require(root != null && root.IsDirectory && root.Parent == null && root.UnknownAllocationFiles == 0);
        int exclusionDetails = boundaries?.Count ?? 0;
        Require(excludedCount is >= 0 and <= MaximumNodes && exclusionDetails == Math.Min(excludedCount, MaximumExclusionDetails));
        var exclusions = new NtfsReparseBoundary[exclusionDetails];
        for (int i = 0; i < exclusions.Length; i++) { token.ThrowIfCancellationRequested(); exclusions[i] = boundaries![i]; }
        var nodes = new List<Node>(); var pending = new Queue<(DiskEntry Entry, int Parent)>();
        var seen = new HashSet<DiskEntry>(ReferenceEqualityComparer.Instance); pending.Enqueue((root, -1));
        while (pending.TryDequeue(out var work))
        {
            token.ThrowIfCancellationRequested(); var entry = work.Entry;
            Require(seen.Add(entry) && nodes.Count < MaximumNodes && (!entry.Incomplete || entry.IsDirectory && excludedCount > 0) && entry.UnknownAllocationFiles == 0 && entry.Storage != null);
            int index = nodes.Count; var modified = entry.Modified.ToUniversalTime();
            Require(entry.Storage.Modified == null || entry.Storage.Modified.Value.ToUniversalTime() == modified);
            Require(entry.IsDirectory || entry.Children.Count == 0 && entry.Size == entry.Storage.LogicalBytes && entry.FileCount == 1 && entry.FolderCount == 0);
            Require(entry.Children.Count <= MaximumNodes - nodes.Count - pending.Count - 1);
            nodes.Add(new(index, work.Parent, entry.IsDirectory, index == 0 ? "" : entry.Name, entry.Storage, modified, entry.Incomplete));
            foreach (var child in entry.Children) { Require(ReferenceEquals(child.Parent, entry)); pending.Enqueue((child, index)); }
        }
        var expected = new Header(nodes.Count, root.FileCount, root.FolderCount, root.Size, root.AllocatedSize, elapsed.Ticks, capturedAt.UtcTicks,
            excludedCount, exclusions.Length);
        var check = new TreeBuilder(NormalizeRoot(root.Path), expected); foreach (var node in nodes) check.Add(node, token);
        foreach (var boundary in exclusions) check.AddExclusion(boundary, token);
        _ = check.Complete(nodes.Count, true, token); check.ValidateIncompleteFlags(nodes, token);
        long bytes = 12 + 4 + 6 * 8 + 8 + 4;
        foreach (var node in nodes) bytes = checked(bytes + 12 + 4 + 4 + 1 + 4 + node.Name.Length * 2L + 24 + 16 + 4 + 8);
        foreach (var boundary in exclusions) bytes = checked(bytes + 12 + 4 + 24 + 4 + boundary.Name.Length * 2L + 1);
        Require(bytes + 17 <= MaximumWireBytes);
        Write(stream, Frame.TreeBegin, writer =>
        {
            writer.Write(expected.Nodes); writer.Write(expected.Files); writer.Write(expected.Directories); writer.Write(expected.Logical);
            writer.Write(expected.Allocated); writer.Write(expected.ElapsedTicks); writer.Write(expected.CapturedTicks);
            writer.Write(expected.ExcludedReparseEntries); writer.Write(expected.ExclusionDetails);
        }, token);
        for (int start = 0; start < nodes.Count; start += 64)
        {
            token.ThrowIfCancellationRequested(); Write(stream, Frame.Node, writer =>
            {
                int count = Math.Min(64, nodes.Count - start); writer.Write(count);
                for (int i = start; i < start + count; i++)
                {
                    var node = nodes[i]; writer.Write(node.Index); writer.Write(node.Parent); writer.Write(node.Directory); Text(writer, node.Name);
                    writer.Write(node.Storage.Identity.Volume); writer.Write(node.Storage.Identity.Low); writer.Write(node.Storage.Identity.High);
                    writer.Write(node.Directory ? 0L : node.Storage.LogicalBytes); writer.Write(node.Storage.AllocatedBytes);
                    writer.Write(node.Storage.LinkCount); writer.Write(node.Modified.Ticks);
                }
            }, token);
        }
        for (int start = 0; start < exclusions.Length; start += 64)
        {
            token.ThrowIfCancellationRequested(); Write(stream, Frame.Exclusion, writer =>
            {
                int count = Math.Min(64, exclusions.Length - start); writer.Write(count);
                for (int i = start; i < start + count; i++)
                {
                    var boundary = exclusions[i]; writer.Write(boundary.ParentIdentity.Volume);
                    writer.Write(boundary.ParentIdentity.Low); writer.Write(boundary.ParentIdentity.High);
                    Text(writer, boundary.Name); writer.Write(boundary.Directory);
                }
            }, token);
        }
        Write(stream, Frame.Done, writer => { writer.Write(nodes.Count); writer.Write(true); }, token);
    }
    internal static Header ReadHeader(BinaryReader reader) => new(reader.ReadInt32(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(),
        reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt32());
    internal static NtfsReparseBoundary ReadExclusion(BinaryReader reader) =>
        new(new(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64()), Text(reader, 255), Boolean(reader));
    internal static Node ReadNode(BinaryReader reader)
    {
        int index = reader.ReadInt32(), parent = reader.ReadInt32(); bool directory = Boolean(reader); string name = Text(reader, 255);
        var identity = new FileIdentity(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());
        long logical = reader.ReadInt64(), allocated = reader.ReadInt64(); uint links = reader.ReadUInt32(); long ticks = reader.ReadInt64();
        Require(ticks >= DateTime.FromFileTimeUtc(0).Ticks && ticks <= DateTime.MaxValue.Ticks);
        var modified = new DateTime(ticks, DateTimeKind.Utc); var storage = new FileStorageInfo(identity, allocated, links, false, directory ? -1 : logical, modified);
        Require(!directory || logical == 0); return new(index, parent, directory, name, storage, modified);
    }
    internal sealed class TreeBuilder
    {
        private readonly string rootPath; private readonly Header header;
        private readonly List<DiskEntry> nodes; private readonly List<int> depths, pathLengths;
        private readonly Dictionary<FileIdentity, (FileStorageInfo Storage, bool Directory, int Aliases)> objects = new();
        private readonly HashSet<ulong> slots = new();
        private readonly HashSet<(int Parent, string Name)> siblingNames = new(new SiblingComparer());
        private readonly Dictionary<FileIdentity, int> directoryIdentities = new();
        private readonly List<ScanIssue> issues = new();
        private long nameCharacters, pathCharacters, logical, allocated, files, directories;
        internal TreeBuilder(string rootPath, Header header)
        {
            this.rootPath = rootPath; this.header = header;
            Require(header.Nodes is >= 1 and <= MaximumNodes && header.Files >= 0 && header.Directories >= 0 &&
                header.Files + header.Directories == header.Nodes - 1L && header.Logical >= 0 && header.Allocated >= 0 &&
                header.ElapsedTicks >= 0 && header.ElapsedTicks <= TimeSpan.FromDays(1).Ticks &&
                header.CapturedTicks >= DateTime.FromFileTimeUtc(0).Ticks && header.CapturedTicks <= DateTime.MaxValue.Ticks);
            Require(header.ExcludedReparseEntries >= 0 && header.ExcludedReparseEntries <= MaximumNodes - header.Nodes &&
                header.ExclusionDetails == Math.Min(header.ExcludedReparseEntries, MaximumExclusionDetails));
            nodes = new(header.Nodes); depths = new(header.Nodes); pathLengths = new(header.Nodes);
        }
        internal void Add(Node value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); int index = nodes.Count; var storage = value.Storage;
            Require(index < header.Nodes && value.Index == index && storage != null && storage.Identity.High == 0 && storage.Identity.Low >> 48 != 0 &&
                storage.AllocatedBytes >= 0 && storage.LinkCount > 0 && !storage.WritableHandleOpen &&
                value.Modified.Kind == DateTimeKind.Utc && value.Modified.Ticks >= DateTime.FromFileTimeUtc(0).Ticks);
            Require(index == 0 ? value.Parent == -1 && value.Directory && value.Name.Length == 0 :
                value.Parent >= 0 && value.Parent < index && nodes[value.Parent].IsDirectory && ValidName(value.Name));
            Require(index == 0 || storage.Identity.Volume == nodes[0].Storage!.Identity.Volume);
            Require(index == 0 || siblingNames.Add((value.Parent, value.Name)));
            int depth = index == 0 ? 0 : depths[value.Parent] + 1;
            int pathLength = index == 0 ? 0 : checked(pathLengths[value.Parent] + value.Name.Length + (value.Parent == 0 ? 0 : 1));
            Require(depth <= MaximumDepth && rootPath.Length + pathLength + 1 <= 32767);
            nameCharacters = checked(nameCharacters + value.Name.Length);
            // DiskEntry stores shared file prefixes. Bound the paths actually
            // retained for directories, including a long original root prefix.
            if (value.Directory) pathCharacters = checked(pathCharacters + rootPath.Length + pathLength + (index == 0 ? 0 : 1));
            Require(nameCharacters <= MaximumNameCharacters && pathCharacters <= MaximumNameCharacters);
            if (objects.TryGetValue(storage.Identity, out var previous))
            {
                Require(!value.Directory && !previous.Directory && previous.Storage == storage && previous.Aliases < storage.LinkCount);
                objects[storage.Identity] = (storage, false, checked(previous.Aliases + 1));
            }
            else
            {
                Require(slots.Add(storage.Identity.Low & 0x0000FFFFFFFFFFFFUL)); objects.Add(storage.Identity, (storage, value.Directory, 1));
                if (!value.Directory) allocated = checked(allocated + storage.AllocatedBytes);
            }
            DiskEntry node;
            if (index == 0) node = new(rootPath, true);
            else
            {
                node = DiskEntry.FromName(value.Name, value.Directory, nodes[value.Parent], value.Directory ? "文件夹" : DiskEntry.ExtensionOf(value.Name));
                nodes[value.Parent].MutableChildren.Add(node);
            }
            node.Storage = storage; node.Modified = value.Modified.ToLocalTime();
            if (value.Directory) { Require(storage.AllocatedBytes == 0 && storage.LogicalBytes == -1); if (index != 0) directories++; }
            else { Require(storage.LogicalBytes >= 0); node.Size = storage.LogicalBytes; node.FileCount = 1; logical = checked(logical + storage.LogicalBytes); files++; }
            nodes.Add(node); depths.Add(depth); pathLengths.Add(pathLength);
            if (node.IsDirectory) Require(directoryIdentities.TryAdd(storage.Identity, index));
        }
        internal void AddExclusion(NtfsReparseBoundary boundary, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); int parent = -1;
            Require(nodes.Count == header.Nodes && issues.Count < header.ExclusionDetails && boundary != null && ValidName(boundary.Name) &&
                directoryIdentities.TryGetValue(boundary.ParentIdentity, out parent) && siblingNames.Add((parent, boundary.Name)));
            string path = Path.Join(nodes[parent].Path, boundary.Name);
            Require(path.Length <= 32767);
            pathCharacters = checked(pathCharacters + path.Length); nameCharacters = checked(nameCharacters + boundary.Name.Length);
            Require(pathCharacters <= MaximumNameCharacters && nameCharacters <= MaximumNameCharacters);
            issues.Add(new(path, NtfsReparsePolicy.Reason));
            if (header.ExcludedReparseEntries <= MaximumExclusionDetails)
                for (DiskEntry? entry = nodes[parent]; entry != null; entry = entry.Parent) entry.Incomplete = true;
        }
        internal void ValidateIncompleteFlags(IReadOnlyList<Node> original, CancellationToken token)
        {
            Require(original.Count == nodes.Count);
            for (int i = 0; i < nodes.Count; i++) { token.ThrowIfCancellationRequested(); Require(original[i].Incomplete == nodes[i].Incomplete); }
        }
        internal FastScanWireResult Complete(int count, bool cleanupConfirmed, CancellationToken token)
        {
            Require(cleanupConfirmed && count == header.Nodes && nodes.Count == header.Nodes && issues.Count == header.ExclusionDetails && files == header.Files && directories == header.Directories &&
                logical == header.Logical && allocated == header.Allocated);
            for (int i = nodes.Count - 1; i > 0; i--)
            {
                token.ThrowIfCancellationRequested(); var entry = nodes[i]; var parent = entry.Parent!;
                parent.Size = checked(parent.Size + entry.Size); parent.FileCount = checked(parent.FileCount + entry.FileCount);
                parent.FolderCount = checked(parent.FolderCount + entry.FolderCount + (entry.IsDirectory ? 1 : 0));
            }
            StorageMetrics.Calculate(nodes[0], token);
            if (header.ExcludedReparseEntries > MaximumExclusionDetails)
                foreach (var node in nodes) { token.ThrowIfCancellationRequested(); if (node.IsDirectory) node.Incomplete = true; }
            Require(nodes[0].Size == logical && nodes[0].AllocatedSize == allocated && nodes[0].UnknownAllocationFiles == 0 &&
                nodes[0].FileCount == files && nodes[0].FolderCount == directories);
            token.ThrowIfCancellationRequested(); return new(nodes[0], TimeSpan.FromTicks(header.ElapsedTicks), new DateTimeOffset(header.CapturedTicks, TimeSpan.Zero),
                header.ExcludedReparseEntries, Array.AsReadOnly(issues.ToArray()));
        }
        private sealed class SiblingComparer : IEqualityComparer<(int Parent, string Name)>
        {
            public bool Equals((int Parent, string Name) left, (int Parent, string Name) right) =>
                left.Parent == right.Parent && StringComparer.OrdinalIgnoreCase.Equals(left.Name, right.Name);
            public int GetHashCode((int Parent, string Name) value) => HashCode.Combine(value.Parent, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
        }
    }
}

internal sealed class FastScanWireReader
{
    private readonly Stream stream; private readonly string root; private long bytes; private bool terminal;
    internal FastScanWireReader(Stream stream, string expectedRoot) { this.stream = stream; root = FastScanWire.NormalizeRoot(expectedRoot); }
    internal FastScanWireEvent Read(CancellationToken token = default)
    {
        FastScanWire.Require(!terminal);
        try
        {
            var frame = FastScanWire.Read(stream, ref bytes, token);
            if (frame.Kind == FastScanWire.Frame.Status)
                return FastScanWire.Parse(frame.Payload, reader =>
                {
                    var stage = (FastScanWireStage)reader.ReadByte(); FastScanWire.Require(Enum.IsDefined(stage));
                    return new FastScanWireEvent(FastScanWireEventKind.Status, Stage: stage);
                });
            if (frame.Kind == FastScanWire.Frame.Progress)
                return FastScanWire.Parse(frame.Payload, reader => new FastScanWireEvent(FastScanWireEventKind.Progress, Progress: FastScanWire.Progress(reader)));
            if (frame.Kind == FastScanWire.Frame.Failure)
            {
                var result = FastScanWire.Parse(frame.Payload, reader =>
                {
                    var error = (FastScanWireError)reader.ReadByte(); FastScanWire.Require(Enum.IsDefined(error)); bool cleanup = FastScanWire.Boolean(reader);
                    FastScanWire.Require(error != FastScanWireError.CleanupUnconfirmed || !cleanup);
                    FastScanFailureContext? context = null;
                    if (FastScanWire.Boolean(reader))
                    {
                        var stage = (FastScanWireStage)reader.ReadByte(); var detail = (FastScanFailureDetail)reader.ReadByte();
                        FastScanWire.Require(Enum.IsDefined(stage) && Enum.IsDefined(detail));
                        int? nativeError = FastScanWire.Boolean(reader) ? reader.ReadInt32() : null;
                        context = new(stage, detail, nativeError);
                    }
                    return new FastScanWireEvent(FastScanWireEventKind.Failure, Error: error, CleanupConfirmed: cleanup, Context: context);
                }); terminal = true; return result;
            }
            FastScanWire.Require(frame.Kind == FastScanWire.Frame.TreeBegin);
            var header = FastScanWire.Parse(frame.Payload, FastScanWire.ReadHeader); var builder = new FastScanWire.TreeBuilder(root, header);
            for (int i = 0; i < header.Nodes;)
            {
                token.ThrowIfCancellationRequested(); frame = FastScanWire.Read(stream, ref bytes, token); FastScanWire.Require(frame.Kind == FastScanWire.Frame.Node);
                int count = FastScanWire.Parse(frame.Payload, reader =>
                {
                    int count = reader.ReadInt32(); FastScanWire.Require(count is >= 1 and <= 64 && count <= header.Nodes - i);
                    for (int n = 0; n < count; n++) builder.Add(FastScanWire.ReadNode(reader), token);
                    return count;
                }); i += count;
            }
            for (int i = 0; i < header.ExclusionDetails;)
            {
                token.ThrowIfCancellationRequested(); frame = FastScanWire.Read(stream, ref bytes, token); FastScanWire.Require(frame.Kind == FastScanWire.Frame.Exclusion);
                int count = FastScanWire.Parse(frame.Payload, reader =>
                {
                    int count = reader.ReadInt32(); FastScanWire.Require(count is >= 1 and <= 64 && count <= header.ExclusionDetails - i);
                    for (int n = 0; n < count; n++) builder.AddExclusion(FastScanWire.ReadExclusion(reader), token);
                    return count;
                }); i += count;
            }
            frame = FastScanWire.Read(stream, ref bytes, token); FastScanWire.Require(frame.Kind == FastScanWire.Frame.Done);
            var completed = FastScanWire.Parse(frame.Payload, reader => builder.Complete(reader.ReadInt32(), FastScanWire.Boolean(reader), token));
            terminal = true; return new(FastScanWireEventKind.Completed, Result: completed, CleanupConfirmed: true);
        }
        catch (FastScanWireException) { terminal = true; token.ThrowIfCancellationRequested(); throw; }
        catch { terminal = true; token.ThrowIfCancellationRequested(); throw new FastScanWireException(); }
    }
}
