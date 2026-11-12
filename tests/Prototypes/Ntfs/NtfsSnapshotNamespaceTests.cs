using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// In-memory directory buffers and cursors only. No directory handle, native
// source, VSS requester, volume or filesystem is opened by these checks.
internal static class NtfsSnapshotNamespaceTests
{
    private static ulong Ref(int slot, int sequence = 1) => ((ulong)sequence << 48) | (uint)slot;
    private static readonly FileIdentity RootId = new(7, Ref(100), 0);
    private static byte[] Page(params (string Name, ulong Reference, FileAttributes Attributes)[] rows)
    {
        var buffer = new byte[NtfsSnapshotNamespace.PageBytes]; int offset = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i]; byte[] name = Encoding.Unicode.GetBytes(row.Name);
            int length = (88 + name.Length + 7) & ~7;
            if (offset + length > buffer.Length) throw new InvalidOperationException("SyntheticPageBudget");
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), i == rows.Length - 1 ? 0u : (uint)length);
            // Poison cached EOF and allocation. Neither is authoritative input.
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset + 40), ulong.MaxValue);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset + 48), ulong.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 56), (uint)row.Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 60), (uint)name.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset + 72), row.Reference);
            name.CopyTo(buffer, offset + 88); offset += length;
        }
        return buffer;
    }
    private sealed class Backend : NtfsSnapshotNamespace.IBackend
    {
        internal readonly Dictionary<ulong, List<byte[]>> Pages = new();
        internal readonly List<(string Path, bool Restart)> Reads = new();
        internal readonly List<string> Opens = new();
        internal int Closed;
        internal string? FaultAt;
        internal Exception? Error;
        internal Action? OnFault;
        internal FileIdentity? CursorIdentity;
        internal FileAttributes CursorAttributes = FileAttributes.Directory;
        internal int EndError = 18;
        private void Fault(string stage) { if (FaultAt == stage) { OnFault?.Invoke(); throw Error!; } }
        public FileIdentity RootIdentity { get { Fault("Root"); return RootId; } }
        public NtfsSnapshotNamespace.ICursor OpenDirectory(NtfsSnapshotNamespaceEntry directory, string relativePath, CancellationToken token)
        { Fault("Open"); Opens.Add(relativePath); return new Cursor(this, directory, relativePath); }
        private sealed class Cursor(Backend owner, NtfsSnapshotNamespaceEntry directory, string path) : NtfsSnapshotNamespace.ICursor
        {
            private int page;
            public FileIdentity Identity { get { owner.Fault("Identity"); return owner.CursorIdentity ?? directory.Identity; } }
            public FileAttributes Attributes { get { owner.Fault("Attributes"); return owner.CursorAttributes; } }
            public NtfsSnapshotNamespaceIo Read(bool restart, Memory<byte> destination, CancellationToken token)
            {
                owner.Fault("Read"); owner.Reads.Add((path, restart));
                if (!owner.Pages.TryGetValue(directory.Identity.Low, out var values)) return new(false, 2);
                if (page >= values.Count) return new(false, owner.EndError);
                values[page++].AsMemory().CopyTo(destination); return new(true, 0);
            }
            public void Dispose() { owner.Closed++; owner.Fault("Dispose"); }
        }
    }
    private static Backend Fixture()
    {
        var backend = new Backend();
        backend.Pages.Add(RootId.Low, [Page((".", RootId.Low, FileAttributes.Directory), ("..", Ref(5), FileAttributes.Directory),
            ("folder", Ref(101), FileAttributes.Directory), ("first.dat", Ref(102), FileAttributes.Archive)),
            Page(("alias.dat", Ref(102), FileAttributes.Archive), ("empty", Ref(103), FileAttributes.Directory))]);
        backend.Pages.Add(Ref(101), [Page((".", Ref(101), FileAttributes.Directory), ("..", RootId.Low, FileAttributes.Directory),
            ("nested.dat", Ref(104), FileAttributes.Archive))]);
        backend.Pages.Add(Ref(103), []); // First ERROR_NO_MORE_FILES on verified handle is a valid empty directory.
        return backend;
    }
    private sealed class VirtualBackend(int count, int nameLength) : NtfsSnapshotNamespace.IBackend
    {
        public FileIdentity RootIdentity => RootId;
        public NtfsSnapshotNamespace.ICursor OpenDirectory(NtfsSnapshotNamespaceEntry directory, string relativePath, CancellationToken token) => new Cursor(count, nameLength);
        private sealed class Cursor(int count, int nameLength) : NtfsSnapshotNamespace.ICursor
        {
            private int emitted;
            public FileIdentity Identity => RootId;
            public FileAttributes Attributes => FileAttributes.Directory;
            public NtfsSnapshotNamespaceIo Read(bool restart, Memory<byte> destination, CancellationToken token)
            {
                if (emitted == count) return new(false, 18);
                int perPage = 65536 / ((88 + nameLength * 2 + 7) & ~7);
                int length = Math.Min(perPage, count - emitted);
                var rows = new (string, ulong, FileAttributes)[length];
                for (int i = 0; i < length; i++) rows[i] = ((emitted + i).ToString("D8") + new string('x', nameLength - 8), Ref(200), FileAttributes.Archive);
                emitted += length; Page(rows).AsMemory().CopyTo(destination); return new(true, 0);
            }
            public void Dispose() { }
        }
    }

    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Namespace check failed: " + name); passed++; Console.WriteLine("PASS snapshot namespace " + name); }
        Exception? Catch(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
        void Reject(Backend input, string code, string name)
        {
            NtfsSnapshotNamespaceResult? partial = null; var error = Catch(() => partial = NtfsSnapshotNamespace.Read(input));
            Check(error is NtfsSnapshotNamespaceException failure && failure.Code == code && failure.Message == code && failure.InnerException == null && partial == null, name);
            Check(input.Closed == input.Opens.Count, "every opened fake cursor is released on rejection");
        }
        var input = Fixture(); var result = NtfsSnapshotNamespace.Read(input);
        Check(result.Report is { Revision: "snapshot-namespace-v1", Entries: 6, FileEntries: 3, Directories: 2, UniqueFileObjects: 2,
            ReparseEntries: 0, OpenedDirectories: 3, Pages: 3, Queries: 6, CompletedDirectories: 3, CompleteWithinBoundary: true },
            "all directory cursors close including an empty first cursor and paged root");
        Check(input.Closed == 3 && input.Opens.SequenceEqual(new[] { "", "folder", "empty" }), "breadth-first traversal opens each observed ordinary directory once");
        Check(input.Reads.Where(item => item.Path == "").Select(item => item.Restart).SequenceEqual(new[] { true, false, false }) &&
            input.Reads.Where(item => item.Path == "empty").Single().Restart, "first query restarts each directory and continuation queries never restart");
        Check(result.Entries[0] is { Index: 0, ParentIndex: -1, Name: "", Directory: true } && result.Entries[5].ParentIndex == 1 &&
            result.RelativePath(5) == "folder/nested.dat", "root and parent indices preserve a complete scoped path graph");
        Check(result.Entries[2].Identity == result.Entries[3].Identity && result.Report.FileEntries > result.Report.UniqueFileObjects,
            "hard-link entries keep a shared full identity without merging their names");
        Check(JsonSerializer.Serialize(result) == "{}" && !JsonSerializer.Serialize(result.Report).Contains("Identity", StringComparison.Ordinal),
            "only safe report counts can be serialized without private names source identities or paths");
        Check(!typeof(NtfsSnapshotNamespaceEntry).GetProperties().Any(property => property.Name.Contains("Size", StringComparison.Ordinal) ||
            property.Name.Contains("Logical", StringComparison.Ordinal) || property.Name.Contains("Allocated", StringComparison.Ordinal)),
            "poisoned directory EOF and allocation cache fields are not size inputs");
        var empty = new Backend(); empty.Pages.Add(RootId.Low, []);
        Check(NtfsSnapshotNamespace.Read(empty).Report is { Entries: 1, Pages: 0, Queries: 1, CompletedDirectories: 1 },
            "verified empty root does not require fabricated dot entries or a prior successful page");
        var boundary = new Backend(); boundary.Pages.Add(RootId.Low, [Page(("junction", Ref(101), FileAttributes.Directory | FileAttributes.ReparsePoint))]);
        var boundaryResult = NtfsSnapshotNamespace.Read(boundary);
        Check(boundaryResult.Report is { Entries: 2, Directories: 1, ReparseEntries: 1, OpenedDirectories: 1 } && boundary.Opens.Count == 1,
            "reparse entry is retained as an explicit untraversed boundary rather than followed or zero-sized");
        Check(Catch(() => result.RelativePath(-1)) is NtfsSnapshotNamespaceException { Code: "NamespaceEntryIndex" } &&
            Catch(() => result.RelativePath(result.Entries.Count)) is NtfsSnapshotNamespaceException { Code: "NamespaceEntryIndex" }, "private path lookup is index bounded");
        input.Pages.Clear(); Check(result.Entries.Count == 6 && result.RelativePath(5) == "folder/nested.dat", "inventory does not alias backend pages");

        foreach (string kind in new[] { "NextSmall", "NextAlignment", "NextHuge", "NameOdd", "NameZero", "NameHuge", "ZeroId", "HighId", "Sequence", "Encoding", "Separator" })
        {
            byte[] page = Page(("ordinary", Ref(102), FileAttributes.Archive));
            switch (kind)
            {
                case "NextSmall": BinaryPrimitives.WriteUInt32LittleEndian(page, 8); break;
                case "NextAlignment": BinaryPrimitives.WriteUInt32LittleEndian(page, 105); break;
                case "NextHuge": BinaryPrimitives.WriteUInt32LittleEndian(page, uint.MaxValue); break;
                case "NameOdd": BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(60), 3); break;
                case "NameZero": BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(60), 0); break;
                case "NameHuge": BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(60), uint.MaxValue); break;
                case "ZeroId": BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(72), 0); break;
                case "HighId": BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(80), 1); break;
                case "Sequence": BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(72), 102); break;
                case "Encoding": page[88] = 0; page[89] = 0xd8; break;
                case "Separator": page[88] = (byte)'/'; break;
            }
            Check(Catch(() => NtfsSnapshotNamespace.ParsePage(page)) is NtfsSnapshotNamespaceException,
                "malformed offsets identities names and encoding cannot publish a parsed prefix");
        }
        foreach (int length in new[] { 0, 88, 89, 65537 }) Check(Catch(() => NtfsSnapshotNamespace.ParsePage(new byte[length])) is NtfsSnapshotNamespaceException,
            "directory page size bounds precede parsing");
        foreach (int length in new[] { 1, 255 })
        {
            var rows = NtfsSnapshotNamespace.ParsePage(Page((new string('n', length), Ref(102), FileAttributes.Archive)));
            Check(rows.Count == 1 && rows[0].Name.Length == length, "bounded Unicode name endpoints are accepted");
        }
        var duplicate = Fixture(); duplicate.Pages[RootId.Low].Add(Page(("FIRST.DAT", Ref(105), FileAttributes.Archive)));
        Reject(duplicate, "NamespaceDuplicateName", "case-insensitive duplicate name across pages never becomes another object");
        var replay = Fixture(); replay.Pages[RootId.Low].Add(replay.Pages[RootId.Low][0]);
        Reject(replay, "NamespaceDotEntry", "replayed page or repeated dot cannot fabricate cursor progress");
        var slotReuse = Fixture(); slotReuse.Pages[RootId.Low].Add(Page(("reused.dat", Ref(102, 2), FileAttributes.Archive)));
        Reject(slotReuse, "NamespaceSlotReused", "full reference sequence reuse cannot be folded by slot");
        var cycle = Fixture(); cycle.Pages[Ref(101)].Add(Page(("cycle", RootId.Low, FileAttributes.Directory)));
        Reject(cycle, "NamespaceDirectoryCycle", "root or directory alias cannot create a graph cycle");
        var wrongDot = Fixture(); wrongDot.Pages[Ref(101)][0] = Page(("..", Ref(5), FileAttributes.Directory));
        Reject(wrongDot, "NamespaceDotEntry", "non-root dot-dot must name the actual observed parent identity");
        var missing = Fixture(); missing.Pages.Remove(Ref(101)); Reject(missing, "NamespaceReadFailed", "missing child directory is not treated as an empty directory");
        var wrongType = Fixture(); wrongType.Pages[RootId.Low].Add(Page(("conflict", Ref(102), FileAttributes.Directory)));
        Reject(wrongType, "NamespaceObjectType", "one full identity cannot change file versus directory kind");
        var incorrectId = Fixture(); incorrectId.CursorIdentity = new(7, Ref(101), 0);
        Reject(incorrectId, "NamespaceDirectoryIdentity", "opened handle must match the listed directory identity");
        var reparseHandle = Fixture(); reparseHandle.CursorAttributes |= FileAttributes.ReparsePoint;
        Reject(reparseHandle, "NamespaceDirectoryType", "handle that became a reparse point is not traversed");
        foreach (int error in new[] { 0, 5, 234, -1 })
        { var failed = Fixture(); failed.EndError = error; Reject(failed, "NamespaceReadFailed", "only error eighteen closes a directory cursor"); }
        var noData = new Backend(); noData.Pages.Add(RootId.Low, [new byte[65536]]);
        Reject(noData, "NamespaceNameBounds", "empty successful buffer is not a completed empty directory");

        foreach (string stage in new[] { "Root", "Open", "Identity", "Attributes", "Read", "Dispose" })
        foreach (bool model in new[] { false, true })
        foreach (bool cancelAtBoundary in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource(); const string marker = "SyntheticPrivateAccessor";
            var backend = Fixture(); backend.FaultAt = stage; backend.Error = model ? new NtfsSnapshotNamespaceException(marker) : new OperationCanceledException(marker);
            backend.OnFault = cancelAtBoundary ? cancellation.Cancel : null;
            NtfsSnapshotNamespaceResult? partial = null; var error = Catch(() => partial = NtfsSnapshotNamespace.Read(backend, cancellation.Token));
            bool safe = cancelAtBoundary ? error is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token && !canceled.Message.Contains(marker, StringComparison.Ordinal) :
                error is NtfsSnapshotNamespaceException fixedError && fixedError.Code == "NamespaceInput" && fixedError.Message == "NamespaceInput" && fixedError.InnerException == null;
            Check(safe && partial == null, "external cursor exception types cannot bypass fixed input codes or caller cancellation priority");
        }
        foreach (var tuple in new[] { (Count: 262144, Length: 8, Code: "NamespaceEntryBudget"),
            (Count: 140000, Length: 255, Code: "NamespaceNameBudget") })
        {
            var error = Catch(() => NtfsSnapshotNamespace.Read(new VirtualBackend(tuple.Count, tuple.Length)));
            Check(error is NtfsSnapshotNamespaceException failure && failure.Code == tuple.Code,
                "virtual large cursors stop at entry and UTF16 character limits without unbounded growth");
        }
        using var canceledToken = new CancellationTokenSource(); canceledToken.Cancel();
        Check(Catch(() => NtfsSnapshotNamespace.Read(Fixture(), canceledToken.Token)) is OperationCanceledException canceledRead &&
            canceledRead.CancellationToken == canceledToken.Token, "pre-canceled inventory returns no partial graph");
        Check(Catch(() => NtfsSnapshotNamespace.ParsePage(Page(("file", Ref(102), FileAttributes.Archive)), canceledToken.Token)) is OperationCanceledException,
            "pre-canceled page parser does not emit a parsed prefix");
        return passed;
    }
}
