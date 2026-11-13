using System.Buffers.Binary;
using System.Text;
using DiskAtlas.NtfsPrototype;

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
            var failure = reader.Read(); Check(failure.Kind == FastScanWireEventKind.Failure && failure.Error == FastScanWireError.Unsupported && failure.CleanupConfirmed && failure.Context == null, "known failure without context");
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
        Mutate(bytes => bytes[0] ^= 1, "bad magic"); Mutate(bytes => bytes[4] = 4, "future schema");
        Mutate(bytes => bytes[4] = 2, "old schema cannot omit exclusion declaration");
        Mutate(bytes => bytes[4] = 1, "old schema cannot omit failure context shape");
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
        byte[] Failure(FastScanFailureContext? context)
        {
            using var stream = new MemoryStream();
            FastScanWire.WriteFailure(stream, FastScanWireError.LimitExceeded, true, context: context);
            return stream.ToArray();
        }
        void Roundtrip(FastScanFailureContext context, string name)
        {
            byte[] bytes = Failure(context); var seen = Decode(bytes);
            Check(seen is { Kind: FastScanWireEventKind.Failure, Error: FastScanWireError.LimitExceeded, CleanupConfirmed: true } &&
                seen.Context == context, name);
            Check(bytes.Length == (context.NativeError.HasValue ? 22 : 18), name + " has a fixed bounded payload");
        }
        foreach (var detail in Enum.GetValues<FastScanFailureDetail>())
            Roundtrip(new(FastScanWireStage.Discovering, detail), "fixed failure detail roundtrip");
        foreach (var stage in Enum.GetValues<FastScanWireStage>())
            Roundtrip(new(stage, FastScanFailureDetail.None, 5), "fixed failure stage roundtrip");
        foreach (int native in new[] { 0, 5, int.MaxValue, int.MinValue, unchecked((int)0x80042308) })
            Roundtrip(new(FastScanWireStage.Snapshot, FastScanFailureDetail.None, native), "signed native failure roundtrip");
        Check(Failure(null).Length == 15 && Decode(Failure(null)).Context == null, "explicit absent context has no diagnostic values");
        foreach (var invalid in new[]
        {
            new FastScanFailureContext((FastScanWireStage)(-1), FastScanFailureDetail.None),
            new FastScanFailureContext((FastScanWireStage)256, FastScanFailureDetail.None),
            new FastScanFailureContext(FastScanWireStage.Preparing, (FastScanFailureDetail)(-1)),
            new FastScanFailureContext(FastScanWireStage.Preparing, (FastScanFailureDetail)256)
        })
        {
            using var stream = new MemoryStream();
            Reject(() => FastScanWire.WriteFailure(stream, FastScanWireError.ReadFailed, true, context: invalid), "writer rejects unknown failure context enum");
            Check(stream.Length == 0, "unknown failure context is not partially written");
        }
        {
            byte[] failure = Failure(new(FastScanWireStage.Discovering, FastScanFailureDetail.NamespacePathLimit, int.MinValue));
            void Malformed(Action<byte[]> change, string name)
            { byte[] bytes = failure.ToArray(); change(bytes); Reject(() => Decode(bytes), name); }
            Check(BinaryPrimitives.ReadUInt16LittleEndian(failure.AsSpan(4)) == 3, "failure envelope uses version three");
            Malformed(bytes => bytes[4] = 1, "old failure schema rejected before accepting cleanup");
            Malformed(bytes => bytes[14] = 2, "failure context presence uses strict boolean");
            Malformed(bytes => bytes[15] = 255, "unknown failure context stage");
            Malformed(bytes => bytes[16] = 255, "unknown failure context detail");
            Malformed(bytes => bytes[17] = 2, "native error presence uses strict boolean");
            Malformed(bytes => bytes[14] = 0, "absent failure context cannot retain unknown fields");
            Malformed(bytes => bytes[17] = 0, "absent native error cannot retain a numeric field");
            foreach (int length in new[] { 14, 15, 16, 17, 18, 19, 20, 21 })
                Reject(() => Decode(failure[..length]), "truncated failure context");
            Check(!Encoding.Unicode.GetString(failure).Contains("private", StringComparison.OrdinalIgnoreCase) &&
                typeof(FastScanFailureContext).GetProperties().All(property => property.PropertyType != typeof(string)),
                "failure context has no arbitrary text carrier");
        }
        using (var stream = new MemoryStream())
        {
            FastScanWire.Write(stream, FastScanWire.Frame.Failure, writer =>
            {
                writer.Write((byte)FastScanWireError.ReadFailed); writer.Write(true); writer.Write(false);
                writer.Write(@"B:\PRIVATE_PATH_MUST_NOT_ESCAPE");
            }, default);
            Reject(() => Decode(stream.ToArray()), "unknown private text field is rejected and sanitized");
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
            byte[] Partial(DiskEntry root, long excluded, IReadOnlyList<NtfsReparseBoundary> boundaries)
            {
                using var stream = new MemoryStream();
                FastScanWire.WriteResult(stream, root, TimeSpan.Zero, Captured, excludedCount: excluded, boundaries: boundaries);
                return stream.ToArray();
            }
            var root = Tree(); var directory = root.Children[0]; root.Incomplete = directory.Incomplete = true;
            var boundary = new NtfsReparseBoundary(directory.Storage!.Identity, "cache-link", true);
            byte[] partialBytes = Partial(root, 1, [boundary]); var partial = Decode(partialBytes).Result!;
            Check(partial.ExcludedReparseEntries == 1 && partial.Issues.Count == 1 && partial.Root.Incomplete && partial.Root.Children[0].Incomplete &&
                partial.Root.Descendants().Where(entry => !entry.IsDirectory).All(entry => !entry.Incomplete), "declared reparse boundary marks only its directory ancestors");
            Check(partial.Issues[0] == new ScanIssue(Path.Combine(RootPath, "folder", "cache-link"), NtfsReparsePolicy.Reason) &&
                partial.Root.Size == 34 && partial.Root.AllocatedSize == 2048 && partial.Root.FileCount == 2 && partial.Root.UnknownAllocationFiles == 0 &&
                partial.Root.Descendants().All(entry => entry.Name != "cache-link"), "excluded link and target do not become zero-size tree entries");
            var partialFrames = Frames(partialBytes); var exclusion = partialFrames.Single(frame => frame.Kind == FastScanWire.Frame.Exclusion);
            var partialBegin = partialFrames.Single(frame => frame.Kind == FastScanWire.Frame.TreeBegin);
            void Malformed(Action<byte[]> change, string name)
            { byte[] changed = partialBytes.ToArray(); change(changed); Reject(() => Decode(changed), name); }
            Malformed(bytes => I64(bytes, partialBegin.Payload + 52, -1), "negative reparse count");
            Malformed(bytes => I64(bytes, partialBegin.Payload + 52, FastScanWire.MaximumNodes), "included and excluded entry sum bounded");
            Malformed(bytes => I32(bytes, partialBegin.Payload + 60, 0), "positive reparse count requires complete bounded details");
            Malformed(bytes => I32(bytes, partialBegin.Payload + 60, 2001), "reparse detail count bounded");
            Malformed(bytes => I32(bytes, exclusion.Payload, 0), "empty exclusion batch rejected");
            Malformed(bytes => I32(bytes, exclusion.Payload, 65), "exclusion batch bounded");
            Malformed(bytes => U64(bytes, exclusion.Payload + 4, 42), "exclusion parent volume must be included");
            Malformed(bytes => U64(bytes, exclusion.Payload + 12, Id(99).Low), "unknown exclusion parent rejected");
            Malformed(bytes => U64(bytes, exclusion.Payload + 12, Id(7).Low), "file cannot be exclusion parent");
            Malformed(bytes => U64(bytes, exclusion.Payload + 20, 1), "full exclusion parent identity required");
            Malformed(bytes => I32(bytes, exclusion.Payload + 28, 256), "exclusion leaf length bounded");
            Malformed(bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(exclusion.Payload + 32), ':'), "exclusion cannot carry an arbitrary path");
            Malformed(bytes => bytes[exclusion.Payload + 32 + boundary.Name.Length * 2] = 2, "exclusion type uses strict boolean");
            byte[] collisionBytes = Partial(root, 1, [boundary with { Name = "q.bin", Directory = false }]);
            var collisionFrame = Frames(collisionBytes).Single(frame => frame.Kind == FastScanWire.Frame.Exclusion);
            BinaryPrimitives.WriteUInt16LittleEndian(collisionBytes.AsSpan(collisionFrame.Payload + 32), 'b');
            Reject(() => Decode(collisionBytes), "receiver rejects excluded and included sibling collision");
            byte[] duplicateBytes = Partial(root, 2, [boundary with { Name = "link-one" }, boundary with { Name = "link-two" }]);
            var duplicateFrame = Frames(duplicateBytes).Single(frame => frame.Kind == FastScanWire.Frame.Exclusion);
            duplicateBytes.AsSpan(duplicateFrame.Payload + 32, 16).CopyTo(duplicateBytes.AsSpan(duplicateFrame.Payload + 32 + 45, 16));
            Reject(() => Decode(duplicateBytes), "receiver rejects duplicate exclusion details");
            byte[] missing = partialBytes[..exclusion.Header].Concat(partialBytes[(exclusion.Payload + exclusion.Size)..]).ToArray();
            Reject(() => Decode(missing), "missing exclusion frame cannot publish partial success");
            foreach (var bad in new[]
            {
                new NtfsReparseBoundary(Id(99), "link", true), new NtfsReparseBoundary(Id(7), "link", true),
                new NtfsReparseBoundary(Id(5), "a.bin", false), new NtfsReparseBoundary(Id(5), "../outside", false),
                new NtfsReparseBoundary(Id(5), "CON", false)
            }) Reject(() => Partial(root, 1, [bad]), "writer rejects invalid reparse boundary");
            Reject(() => Partial(root, 0, [boundary]), "zero excluded count cannot carry a detail");
            Reject(() => Partial(root, 1, []), "positive excluded count cannot omit details");
            Reject(() => Partial(root, 2, [boundary, boundary with { Name = "CACHE-LINK" }]), "reparse details are case insensitive unique siblings");
            root.Incomplete = false;
            Reject(() => Partial(root, 1, [boundary]), "writer requires incomplete root for excluded scope");
            root.Incomplete = true; directory.Incomplete = false;
            Reject(() => Partial(root, 1, [boundary]), "writer requires incomplete boundary ancestor");
            directory.Incomplete = true; root.Children[1].Incomplete = true;
            Reject(() => Partial(root, 1, [boundary]), "reparse exclusion never permits incomplete file metadata");
            root.Children[1].Incomplete = false; root.Children[1].UnknownAllocationFiles = 1;
            Reject(() => Partial(root, 1, [boundary]), "reparse exclusion never permits unknown allocation");
            root = Tree(); root.Incomplete = true;
            var rootBoundary = new NtfsReparseBoundary(Id(5), "root-link", false);
            var rootPartial = Decode(Partial(root, 1, [rootBoundary])).Result!;
            Check(rootPartial.Root.Incomplete && !rootPartial.Root.Children[0].Incomplete, "unaffected included directory remains complete");
            root.Children[0].Incomplete = true;
            Reject(() => Partial(root, 1, [rootBoundary]), "unexplained incomplete directory cannot hide behind a reparse count");
            var details = Enumerable.Range(0, 2000).Select(index => new NtfsReparseBoundary(Id(5), "link-" + index, index % 2 == 0)).ToArray();
            var truncated = Decode(Partial(root, 2001, details)).Result!;
            Check(truncated.ExcludedReparseEntries == 2001 && truncated.Issues.Count == 2000 &&
                truncated.Root.Descendants().Where(entry => entry.IsDirectory).Prepend(truncated.Root).All(entry => entry.Incomplete),
                "truncated reparse details conservatively mark every included directory incomplete");
            Reject(() => Partial(root, 2001, details[..1999]), "truncated details still require the full fixed cap");
            root.Children[0].Incomplete = false;
            Reject(() => Partial(root, 2001, details), "truncated details cannot claim an unobserved directory complete");
            root = Tree(); root.Incomplete = root.Children[0].Incomplete = true;
            using var indexedStream = new MemoryStream();
            FastScanWire.WriteResult(indexedStream, root, TimeSpan.Zero, Captured, excludedCount: 1, boundaries: new IndexOnlyBoundaries(boundary));
            Check(Decode(indexedStream.ToArray()).Result?.ExcludedReparseEntries == 1,
                "bounded indexed boundary copy never trusts an unbounded external enumerator");
            using var cancelled = new CancellationTokenSource(); bool cancelledWrite = false;
            try { FastScanWire.WriteResult(new MemoryStream(), root, TimeSpan.Zero, Captured, cancelled.Token, 1, new CancellingBoundaries(cancelled)); }
            catch (OperationCanceledException) { cancelledWrite = true; }
            Check(cancelledWrite, "real cancellation wins over an external controlled-looking boundary exception");
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
    private sealed class IndexOnlyBoundaries(NtfsReparseBoundary value) : IReadOnlyList<NtfsReparseBoundary>
    {
        public int Count => 1;
        public NtfsReparseBoundary this[int index] => index == 0 ? value : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<NtfsReparseBoundary> GetEnumerator() => throw new IOException("PRIVATE_ENUMERATOR");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class CancellingBoundaries(CancellationTokenSource cancellation) : IReadOnlyList<NtfsReparseBoundary>
    {
        public int Count => 1;
        public NtfsReparseBoundary this[int index] { get { cancellation.Cancel(); throw new FastScanWireException(); } }
        public IEnumerator<NtfsReparseBoundary> GetEnumerator() => throw new IOException("PRIVATE_ENUMERATOR");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
