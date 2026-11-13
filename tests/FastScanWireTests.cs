using System.Buffers.Binary;
using System.Text;

namespace DiskAtlas;

// Memory streams and synthetic metadata only: no pipe, elevation, disk or VSS.
internal static class FastScanWireTests
{
    private const string RootPath = @"B:\AsukaLens-Synthetic";
    private static readonly DateTime Modified = new(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private static readonly DateTimeOffset Captured = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    private static FileIdentity Id(ulong slot, ushort sequence = 3) => new(41, ((ulong)sequence << 48) | slot, 0);
    private static DiskEntry Tree()
    {
        var root = new DiskEntry(RootPath, true) { Storage = new(Id(5), 0, 1, false, -1, Modified), Modified = Modified.ToLocalTime() };
        var directory = DiskEntry.FromName("folder", true, root, "文件夹"); directory.Storage = new(Id(6), 0, 1, false, -1, Modified); directory.Modified = Modified.ToLocalTime();
        root.MutableChildren.Add(directory);
        foreach (var pair in new[] { ("a.bin", root), ("b.bin", directory) })
        {
            var file = DiskEntry.FromName(pair.Item1, false, pair.Item2, ".bin"); file.Storage = new(Id(7), 2048, 2, false, 17, Modified);
            file.Modified = Modified.ToLocalTime(); file.Size = 17; file.FileCount = 1; pair.Item2.MutableChildren.Add(file);
        }
        directory.Size = 17; directory.FileCount = 1; root.Size = 34; root.FileCount = 2; root.FolderCount = 1;
        StorageMetrics.Calculate(root, default); return root;
    }
    private static byte[] Encoded(DiskEntry? tree = null)
    {
        using var stream = new MemoryStream(); FastScanWire.WriteResult(stream, tree ?? Tree(), TimeSpan.FromMilliseconds(123), Captured); return stream.ToArray();
    }
    private static FastScanWireEvent Decode(byte[] value)
    { using var stream = new MemoryStream(value, false); return new FastScanWireReader(stream, RootPath).Read(); }
    private static List<(FastScanWire.Frame Kind, int Header, int Payload, int Size)> Frames(byte[] value)
    {
        var frames = new List<(FastScanWire.Frame, int, int, int)>();
        for (int at = 0; at < value.Length;)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(at + 8));
            frames.Add(((FastScanWire.Frame)BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(at + 6)), at, at + 12, size)); at += 12 + size;
        }
        return frames;
    }
    private static List<(int Start, int NameLength, int Name, int Identity)> Nodes(byte[] value)
    {
        var nodes = new List<(int, int, int, int)>();
        foreach (var frame in Frames(value).Where(frame => frame.Kind == FastScanWire.Frame.Node))
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(frame.Payload)), at = frame.Payload + 4;
            for (int i = 0; i < count; i++)
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(at + 9));
                int identity = at + 13 + length * 2; nodes.Add((at, length, at + 13, identity)); at = identity + 52;
            }
        }
        return nodes;
    }
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Fast scan wire check failed: " + name); passed++; Console.WriteLine("PASS fast scan wire " + name); }
        void Reject(Action action, string name)
        {
            try { action(); throw new InvalidOperationException("Fast scan wire accepted invalid case: " + name); }
            catch (FastScanWireException error) { Check(error.Message == "NTFS 快速扫描通信数据无效。", name + " sanitized"); }
        }
        foreach (string path in new[] { @"B:\", RootPath, "b:/synthetic/子目录/", @"B:\Folder (test)\file.ext" })
        {
            using var stream = new MemoryStream(); FastScanWire.WriteRequest(stream, path); stream.Position = 0;
            Check(FastScanWire.ReadRequest(stream) == FastScanWire.NormalizeRoot(path), "request roundtrip");
        }
        foreach (string path in new[] { "", "B:", @"\root", @"\\server\share", @"\\?\B:\scope", @"\\.\B:", @"B:\scope\..\other",
            @"B:\scope\.\other", @"B:\scope:file", @"B:\scope\bad.", @"B:\scope\bad ", @"B:\CON\x", @"B:\scope\LPT9.txt", @"B:\scope\\other",
            "B:\\scope\u0000", "B:\\" + new string('x', 256), "B:\\bad\ud800" })
            Reject(() => FastScanWire.NormalizeRoot(path), "request invalid path");
        using (var stream = new MemoryStream())
        {
            foreach (var stage in Enum.GetValues<FastScanWireStage>()) FastScanWire.WriteStatus(stream, stage);
            FastScanWire.WriteProgress(stream, new(1, 2, 3, @"B:\private-name", ScanPhase.Verifying, 8, 1, 4, Message: "private message"));
            FastScanWire.WriteFailure(stream, FastScanWireError.Unsupported, true); stream.Position = 0; var reader = new FastScanWireReader(stream, RootPath);
            foreach (var stage in Enum.GetValues<FastScanWireStage>()) Check(reader.Read() is { Kind: FastScanWireEventKind.Status, Stage: var seen } && seen == stage, "status roundtrip");
            var progress = reader.Read(); Check(progress.Kind == FastScanWireEventKind.Progress && progress.Progress is { Files: 1, Folders: 2, Bytes: 3,
                WorkerLimit: 8, CurrentPath: "", Message: null, Method: null, CompletedDirectories: 1, DiscoveredDirectories: 4 }, "progress counts only");
            var failure = reader.Read(); Check(failure.Kind == FastScanWireEventKind.Failure && failure.Error == FastScanWireError.Unsupported && failure.CleanupConfirmed, "known failure");
            Reject(() => reader.Read(), "terminal reader cannot replay");
            Check(!Encoding.Unicode.GetString(stream.ToArray()).Contains("private-name", StringComparison.Ordinal), "private progress path excluded");
        }
        var encoded = Encoded(); var result = Decode(encoded);
        Check(result.Kind == FastScanWireEventKind.Completed && result.CleanupConfirmed && result.Result is { } value &&
            value.Root.Path == RootPath && value.Root.FileCount == 2 && value.Root.FolderCount == 1 && value.Root.Size == 34 && value.Root.AllocatedSize == 2048 &&
            value.Root.UnknownAllocationFiles == 0 && value.Elapsed == TimeSpan.FromMilliseconds(123) && value.CapturedAt == Captured, "complete counts and hardlink dedup");
        Check(result.Result!.Root.Descendants().All(node => node.Modified.ToUniversalTime() == Modified && node.Storage?.Modified?.ToUniversalTime() == Modified), "modified dates retained");
        Check(result.Result.Root.Descendants().Count(node => node.SharedAllocation) == 1, "hardlink alias ownership retained");
        Check(Frames(encoded).Count(frame => frame.Kind == FastScanWire.Frame.Node) == 1, "nodes use bounded batch");
        var frames = Frames(encoded); var begin = frames.Single(frame => frame.Kind == FastScanWire.Frame.TreeBegin); var end = frames.Single(frame => frame.Kind == FastScanWire.Frame.Done); var nodes = Nodes(encoded);
        void Mutate(Action<byte[]> action, string name) { var changed = encoded.ToArray(); action(changed); Reject(() => Decode(changed), name); }
        void I32(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
        void I64(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
        void U64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
        Mutate(bytes => bytes[0] ^= 1, "bad magic"); Mutate(bytes => bytes[4] = 2, "future schema");
        Mutate(bytes => bytes[6] = 255, "unknown frame"); Mutate(bytes => I32(bytes, 8, FastScanWire.MaximumFrameBytes + 1), "frame byte budget");
        Mutate(bytes => I32(bytes, begin.Payload, 0), "no root"); Mutate(bytes => I32(bytes, begin.Payload, FastScanWire.MaximumNodes + 1), "node budget");
        Mutate(bytes => I64(bytes, begin.Payload + 4, -1), "negative files"); Mutate(bytes => I64(bytes, begin.Payload + 12, 2), "count declaration mismatch");
        Mutate(bytes => I64(bytes, begin.Payload + 20, -1), "negative logical declaration"); Mutate(bytes => I64(bytes, begin.Payload + 28, 2049), "allocated declaration mismatch");
        Mutate(bytes => I64(bytes, begin.Payload + 36, -1), "negative elapsed"); Mutate(bytes => I64(bytes, begin.Payload + 44, 0), "invalid capture timestamp");
        Mutate(bytes => I32(bytes, nodes[0].Start + 4, 0), "root parent"); Mutate(bytes => bytes[nodes[0].Start + 8] = 0, "root must be directory");
        Mutate(bytes => I32(bytes, nodes[1].Start, 3), "nonsequential node"); Mutate(bytes => I32(bytes, nodes[1].Start + 4, 2), "future parent");
        Mutate(bytes => I32(bytes, nodes[3].Start + 4, 2), "file cannot be parent"); Mutate(bytes => bytes[nodes[1].Start + 8] = 2, "strict boolean");
        Mutate(bytes => I32(bytes, nodes[1].Start + 9, 256), "name length bound");
        foreach (char character in new[] { '/', '\\', ':', '<', '>', '"', '|', '?', '*', '\0' })
            Mutate(bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(nodes[1].Name), character), "invalid leaf character");
        Mutate(bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(nodes[1].Name + 2 * (nodes[1].NameLength - 1)), '.'), "trailing dot");
        Mutate(bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(nodes[1].Name), 0xD800), "invalid UTF16");
        Mutate(bytes => Encoding.Unicode.GetBytes("CON.tx").CopyTo(bytes, nodes[1].Name), "reserved device leaf");
        Mutate(bytes => U64(bytes, nodes[1].Identity, 42), "volume mismatch"); Mutate(bytes => U64(bytes, nodes[1].Identity + 16, 1), "high file id unsupported");
        Mutate(bytes => U64(bytes, nodes[1].Identity + 8, 6), "zero sequence");
        Mutate(bytes => U64(bytes, nodes[1].Identity + 8, Id(5, 4).Low), "same slot changed sequence");
        Mutate(bytes => U64(bytes, nodes[1].Identity + 8, Id(5).Low), "directory alias");
        Mutate(bytes => I64(bytes, nodes[1].Identity + 24, 1), "directory logical nonzero"); Mutate(bytes => I64(bytes, nodes[1].Identity + 32, 1), "directory allocation nonzero");
        Mutate(bytes => I64(bytes, nodes[2].Identity + 24, -1), "negative file logical"); Mutate(bytes => I64(bytes, nodes[2].Identity + 32, -1), "negative file allocation");
        Mutate(bytes => I32(bytes, nodes[2].Identity + 40, 0), "zero hardlink count");
        Mutate(bytes => I32(bytes, nodes[2].Identity + 40, 1), "hardlink metadata contradiction");
        Mutate(bytes => I64(bytes, nodes[3].Identity + 24, 18), "alias logical contradiction");
        Mutate(bytes => { U64(bytes, nodes[3].Identity + 8, Id(8).Low); I64(bytes, nodes[3].Identity + 24, long.MaxValue); }, "logical sum overflow");
        Mutate(bytes => I64(bytes, nodes[3].Identity + 44, Modified.AddSeconds(1).Ticks), "alias modified contradiction");
        Mutate(bytes => I64(bytes, nodes[2].Identity + 44, 0), "invalid modified date");
        Mutate(bytes => I32(bytes, end.Payload, 3), "done count mismatch"); Mutate(bytes => bytes[end.Payload + 4] = 0, "done cleanup required");
        Mutate(bytes => bytes[end.Payload + 4] = 2, "done cleanup strict boolean");
        foreach (int length in new[] { 0, 1, 11, 12, begin.Payload + 5, nodes[2].Start + 1, end.Payload, encoded.Length - 1 })
            Reject(() => Decode(encoded[..length]), "truncated transport");
        using (var stream = new MemoryStream())
        {
            FastScanWire.Write(stream, FastScanWire.Frame.Status, writer => writer.Write((byte)255), default); Reject(() => Decode(stream.ToArray()), "unknown stage");
        }
        using (var stream = new MemoryStream())
        {
            FastScanWire.Write(stream, FastScanWire.Frame.Failure, writer => { writer.Write((byte)255); writer.Write(true); }, default);
            Reject(() => Decode(stream.ToArray()), "unknown failure code");
        }
        using (var stream = new MemoryStream())
        {
            FastScanWire.Write(stream, FastScanWire.Frame.Failure, writer => { writer.Write((byte)FastScanWireError.ReadFailed); writer.Write((byte)2); }, default);
            Reject(() => Decode(stream.ToArray()), "failure cleanup strict boolean");
        }
        using (var stream = new MemoryStream())
        {
            FastScanWire.Write(stream, FastScanWire.Frame.Failure, writer => { writer.Write((byte)FastScanWireError.CleanupUnconfirmed); writer.Write(true); }, default);
            Reject(() => Decode(stream.ToArray()), "cleanup status contradiction");
            Reject(() => FastScanWire.WriteFailure(new MemoryStream(), FastScanWireError.CleanupUnconfirmed, true), "writer cleanup status contradiction");
        }
        foreach (var progress in new[] { new ScanProgress(-1, 0, 0, ""), new ScanProgress(0, -1, 0, ""), new ScanProgress(0, 0, -1, ""),
            new ScanProgress(0, 0, 0, "", (ScanPhase)255), new ScanProgress(0, 0, 0, "", WorkerLimit: 17),
            new ScanProgress(0, 0, 0, "", CompletedDirectories: 2, DiscoveredDirectories: 1) })
            Reject(() => FastScanWire.WriteProgress(new MemoryStream(), progress), "invalid progress");
        using (var stream = new MemoryStream())
        {
            FastScanWire.WriteStatus(stream, FastScanWireStage.Preparing); stream.Position = 0;
            long total = FastScanWire.MaximumWireBytes - stream.Length; _ = FastScanWire.Read(stream, ref total, default);
            Check(total == FastScanWire.MaximumWireBytes, "exact cumulative byte budget"); stream.Position = 0;
            total = FastScanWire.MaximumWireBytes - stream.Length + 1;
            Reject(() => FastScanWire.Read(stream, ref total, default), "cumulative byte budget exceeded");
        }
        {
            var tree = Tree(); tree.MutableChildren[1].Incomplete = true;
            using var stream = new MemoryStream(); Reject(() => FastScanWire.WriteResult(stream, tree, TimeSpan.Zero, Captured), "incomplete tree rejected");
            Check(stream.Length == 0, "invalid tree not published");
        }
        {
            var tree = Tree(); tree.Storage = null; Reject(() => Encoded(tree), "missing storage rejected");
            tree = Tree(); tree.Size++; Reject(() => Encoded(tree), "root totals must match nodes");
            tree = Tree(); tree.MutableChildren[0].Modified = DateTime.MinValue; Reject(() => Encoded(tree), "unknown modification rejected");
            tree = Tree(); tree.MutableChildren.Add(tree); Reject(() => Encoded(tree), "tree cycle rejected");
        }
        {
            var tree = Tree(); var duplicate = DiskEntry.FromName("A.BIN", false, tree, ".bin");
            duplicate.Storage = new(Id(8), 0, 1, false, 0, Modified); duplicate.Modified = Modified.ToLocalTime(); duplicate.FileCount = 1;
            tree.MutableChildren.Add(duplicate); tree.FileCount++;
            Reject(() => Encoded(tree), "case insensitive sibling path duplicate");
        }
        {
            var builder = new FastScanWire.TreeBuilder(RootPath, new(514, 0, 513, 0, 0, 0, Captured.UtcTicks));
            for (int i = 0; i <= FastScanWire.MaximumDepth; i++)
                builder.Add(new(i, i - 1, true, i == 0 ? "" : "d", new(Id((ulong)i + 5), 0, 1, false, -1, Modified), Modified), default);
            Reject(() => builder.Add(new(513, 512, true, "d", new(Id(518), 0, 1, false, -1, Modified), Modified), default), "tree depth bounded");
        }
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool cancelledRead = false, cancelledWrite = false;
            try { _ = new FastScanWireReader(new MemoryStream(encoded), RootPath).Read(cancelled.Token); } catch (OperationCanceledException) { cancelledRead = true; }
            try { FastScanWire.WriteRequest(new MemoryStream(), RootPath, cancelled.Token); } catch (OperationCanceledException) { cancelledWrite = true; }
            Check(cancelledRead && cancelledWrite, "pre-cancel isolated");
        }
        using (var stream = new ThrowingStream())
        {
            Reject(() => new FastScanWireReader(stream, RootPath).Read(), "foreign stream error sanitized");
        }
        Console.WriteLine($"Fast scan wire: {passed} checks passed."); return passed;
    }
    private sealed class ThrowingStream : MemoryStream
    { public override int Read(Span<byte> buffer) => throw new IOException(@"B:\private-path must not escape"); }
}
