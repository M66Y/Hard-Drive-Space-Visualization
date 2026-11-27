using System.Buffers.Binary;
using System.Collections;
using System.Text;
using DiskAtlas.NtfsPrototype;

namespace DiskAtlas;

// In-memory inventories, page buffers and leases only. No source factory,
// native handle, volume, snapshot, user directory or file is opened here.
internal static class NtfsNamespaceAncestorComparisonTests
{
    private const string PrivateMarker = "SyntheticPrivateExceptionContent";
    private static FileIdentity Id(int slot) => new(7, (1ul << 48) | (uint)slot, 0);
    private static readonly FileIdentity RootId = Id(5);
    private static readonly DateTime Stamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Local);
    private static NtfsSnapshotNamespaceEntry Entry(int index, int parent, string name, int slot, bool directory = false) =>
        new(index, parent, name, Id(slot), directory ? FileAttributes.Directory : FileAttributes.Archive, false) { Modified = Stamp };
    private static NtfsSnapshotNamespaceEntry[] Entries(bool reordered = false) => reordered ?
        [Entry(0, -1, "", 5, true), Entry(1, 0, "b", 102, true), Entry(2, 0, "a", 101, true),
         Entry(3, 1, "second.bin", 202), Entry(4, 2, "first.bin", 201)] :
        [Entry(0, -1, "", 5, true), Entry(1, 0, "a", 101, true), Entry(2, 0, "b", 102, true),
         Entry(3, 1, "first.bin", 201), Entry(4, 2, "second.bin", 202)];

    private static NtfsSnapshotNamespaceResult Inventory(NtfsSnapshotNamespaceEntry[]? entries = null,
        Func<NtfsSnapshotNamespaceReport, NtfsSnapshotNamespaceReport>? report = null, string?[]? suppliedPaths = null,
        FileIdentity? root = null)
    {
        entries ??= Entries();
        var paths = new string?[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (i == 0) paths[i] = "";
            else if (e.Directory)
            {
                string prefix = e.ParentIndex >= 0 && e.ParentIndex < i ? paths[e.ParentIndex] ?? "invalid" : "invalid";
                paths[i] = prefix.Length == 0 ? e.Name : prefix + "/" + e.Name;
            }
        }
        int files = entries.Skip(1).Count(e => !e.Directory), directories = entries.Skip(1).Count(e => e.Directory);
        int opened = 1 + entries.Skip(1).Count(e => e.Directory && !e.Reparse);
        int pages = entries.Skip(1).Select(e => e.ParentIndex).Distinct().Count();
        var r = new NtfsSnapshotNamespaceReport("snapshot-namespace-v1", entries.Length, files, directories,
            entries.Where(e => !e.Directory).Select(e => e.Identity).Distinct().Count(), entries.Count(e => e.Reparse), opened,
            pages, pages + opened, entries.Sum(e => e.Name.Length), opened, true);
        return new(report?.Invoke(r) ?? r, entries, suppliedPaths ?? paths, root ?? entries[0].Identity, NtfsScanLimits.Large);
    }
    private static Exception? Catch(Action action) { try { action(); return null; } catch (Exception e) { return e; } }
    private static bool Has(NamespaceAncestorChecks flags, NamespaceAncestorChecks value) => (flags & value) == value;

    private sealed class Source : NtfsNamespaceAncestorComparison.ISource
    {
        internal readonly List<bool> Modes = [];
        internal readonly List<Backend> Backends = [];
        internal readonly List<string> Events = [];
        internal Func<bool, Backend>? Build;
        internal Action? OnCleanup;
        internal bool CleanupValue = true, CleanupThrows, DisposeThrows;
        internal int CleanupCalls, DisposeCalls, RootReads, ChangeRootAfter = int.MaxValue;
        internal bool PriorLeaseSettled = true;
        public FileIdentity RootIdentity => ++RootReads >= ChangeRootAfter ? Id(99) : RootId;
        public NtfsSnapshotNamespace.IBackendLease Open(bool reuse, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            PriorLeaseSettled &= Backends.All(b => b.Disposed && b.Opened == b.Closed);
            Modes.Add(reuse); Events.Add(reuse ? "candidate" : "original");
            var backend = Build?.Invoke(reuse) ?? new Backend(); Backends.Add(backend); return backend;
        }
        public bool Cleanup()
        {
            Events.Add("cleanup"); CleanupCalls++; OnCleanup?.Invoke();
            if (CleanupThrows) throw new IOException(PrivateMarker); return CleanupValue;
        }
        public void Dispose()
        { Events.Add("dispose"); DisposeCalls++; if (DisposeThrows) throw new IOException(PrivateMarker); }
    }
    private sealed class Backend : NtfsSnapshotNamespace.IBackendLease
    {
        internal readonly Dictionary<string, List<byte[]>> Pages = new(StringComparer.Ordinal);
        internal int Opened, Closed, Reads;
        internal bool Disposed, FailOpen, FailRead, FailCursorDispose, FailDispose;
        internal FileIdentity? RootOverride;
        internal Action? OnRead;
        internal Backend()
        {
            var e = Entries();
            Pages[""] = [Page(e[1], e[2])]; Pages["a"] = [Page(e[3])]; Pages["b"] = [Page(e[4])];
        }
        public FileIdentity RootIdentity => RootOverride ?? RootId;
        public DateTime? RootModified => Stamp;
        public NtfsSnapshotNamespace.ICursor OpenDirectory(NtfsSnapshotNamespaceEntry directory, string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Disposed || FailOpen) throw new IOException(PrivateMarker);
            Opened++; return new Cursor(this, directory.Identity, path);
        }
        private sealed class Cursor(Backend owner, FileIdentity identity, string path) : NtfsSnapshotNamespace.ICursor
        {
            private int page, disposed;
            public FileIdentity Identity => identity;
            public FileAttributes Attributes => FileAttributes.Directory;
            public NtfsSnapshotNamespaceIo Read(bool restart, Memory<byte> buffer, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); owner.Reads++; owner.OnRead?.Invoke();
                if (owner.FailRead) throw new IOException(PrivateMarker);
                if (!owner.Pages.TryGetValue(path, out var values) || page >= values.Count) return new(false, 18);
                values[page++].AsMemory().CopyTo(buffer); return new(true, 0);
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                owner.Closed++; if (owner.FailCursorDispose) throw new IOException(PrivateMarker);
            }
        }
        public void Dispose()
        { Disposed = true; if (FailDispose) throw new IOException(PrivateMarker); }
    }
    private static byte[] Page(params NtfsSnapshotNamespaceEntry[] entries)
    {
        var bytes = new byte[NtfsSnapshotNamespace.PageBytes]; int offset = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i]; byte[] name = Encoding.Unicode.GetBytes(e.Name); int length = (88 + name.Length + 7) & ~7;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), i + 1 == entries.Length ? 0u : (uint)length);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset + 16), Stamp.ToFileTimeUtc());
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 40), ulong.MaxValue);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 48), ulong.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 56), (uint)e.Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 60), (uint)name.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 72), e.Identity.Low);
            name.CopyTo(bytes, offset + 88); offset += length;
        }
        return bytes;
    }
    private sealed class OverBudgetEntries : IReadOnlyList<NtfsSnapshotNamespaceEntry>
    {
        public int Count => NtfsScanLimits.Large.MaximumEntries + 1;
        public NtfsSnapshotNamespaceEntry this[int index] => throw new IOException(PrivateMarker);
        public IEnumerator<NtfsSnapshotNamespaceEntry> GetEnumerator() => throw new IOException(PrivateMarker);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal static int Run()
    {
        int passed = 0;
        void Check(bool result, string name)
        { if (!result) throw new InvalidOperationException(name); passed++; Console.WriteLine("PASS ancestor comparison " + name); }
        void Invalid(NtfsSnapshotNamespaceResult input, string name) =>
            Check(Catch(() => NtfsNamespaceAncestorComparison.Compare(Inventory(), input)) is InvalidDataException or NtfsVssProbeException, name);
        void SafeFailure(NamespaceAncestorReport r, string name) => Check(r.Outcome is NamespaceAncestorOutcome.Failed or NamespaceAncestorOutcome.Cancelled &&
            !r.SameSourceVerified && !r.ComparisonComplete && r.Checks == NamespaceAncestorChecks.None && !r.ToString().Contains(PrivateMarker, StringComparison.Ordinal), name);

        Check(NtfsNamespaceAncestorComparison.Compare(Inventory(), Inventory(Entries(true))) == NamespaceAncestorChecks.All,
            "reordered entries compare by relative parent and name");
        var missing = Entries().Take(4).ToArray();
        var missingFlags = NtfsNamespaceAncestorComparison.Compare(Inventory(), Inventory(missing));
        Check(!Has(missingFlags, NamespaceAncestorChecks.Paths) && !Has(missingFlags, NamespaceAncestorChecks.Counts), "missing object changes path and count checks");
        foreach (string field in new[] { "identity", "parent-identity", "attributes", "time", "null-time" })
        {
            var entries = Entries();
            switch (field)
            {
                case "identity": entries[3] = entries[3] with { Identity = Id(301) }; break;
                case "parent-identity": entries[1] = entries[1] with { Identity = Id(302) }; break;
                case "attributes": entries[3] = entries[3] with { Attributes = FileAttributes.Archive | FileAttributes.Hidden }; break;
                case "time": entries[3] = entries[3] with { Modified = Stamp.AddTicks(1) }; break;
                case "null-time": entries[3] = entries[3] with { Modified = null }; break;
            }
            var flags = NtfsNamespaceAncestorComparison.Compare(Inventory(), Inventory(entries));
            var absent = field switch { "identity" => NamespaceAncestorChecks.Identities, "parent-identity" => NamespaceAncestorChecks.Parents | NamespaceAncestorChecks.Identities,
                "attributes" => NamespaceAncestorChecks.Attributes, _ => NamespaceAncestorChecks.Times };
            Check((flags & absent) == 0 && Has(flags, NamespaceAncestorChecks.Paths), field + " difference remains visible with unchanged paths");
        }
        foreach (bool complete in new[] { false, true })
        {
            var flags = NtfsNamespaceAncestorComparison.Compare(Inventory(), Inventory(report: r => complete ? r with { CompletedDirectories = r.CompletedDirectories - 1 } : r with { CompleteWithinBoundary = false }));
            Check(!Has(flags, NamespaceAncestorChecks.Coverage), "unfinished coverage cannot match");
        }
        foreach (string kind in new[] { "parent-index", "parent-file", "parent-reparse", "index", "root-parent", "duplicate", "name-dot", "name-slash", "name-control", "name-long" })
        {
            var e = Entries();
            switch (kind)
            {
                case "parent-index": e[3] = e[3] with { ParentIndex = 3 }; break;
                case "parent-file": e[4] = e[4] with { ParentIndex = 3 }; break;
                case "parent-reparse": e[1] = e[1] with { Reparse = true, Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint }; break;
                case "index": e[3] = e[3] with { Index = 9 }; break;
                case "root-parent": e[0] = e[0] with { ParentIndex = 0 }; break;
                case "duplicate": e[4] = e[4] with { ParentIndex = 1, Name = "first.bin" }; break;
                case "name-dot": e[3] = e[3] with { Name = ".." }; break;
                case "name-slash": e[3] = e[3] with { Name = "bad/name" }; break;
                case "name-control": e[3] = e[3] with { Name = "bad\nname" }; break;
                case "name-long": e[3] = e[3] with { Name = new string('n', 256) }; break;
            }
            Invalid(Inventory(e), kind + " invalid inventory rejected");
        }
        foreach (string kind in new[] { "files", "directories", "unique", "reparse", "completed", "names", "queries", "pages", "opened", "declared-entries", "query-budget" })
        {
            var input = Inventory(report: r => kind switch
            {
                "files" => r with { FileEntries = -1 }, "directories" => r with { Directories = -1 }, "unique" => r with { UniqueFileObjects = -1 },
                "reparse" => r with { ReparseEntries = -1 }, "completed" => r with { CompletedDirectories = -1 }, "names" => r with { NameCharacters = -1 },
                "queries" => r with { Queries = 0 }, "pages" => r with { Pages = -1 }, "opened" => r with { OpenedDirectories = 0 },
                "declared-entries" => r with { Entries = r.Entries + 1 }, _ => r with { Queries = NtfsScanLimits.Large.MaximumEntries * 4 + 2 }
            });
            Invalid(input, kind + " budget or report bound rejected");
        }
        var valid = Inventory();
        Invalid(new(valid.Report with { Entries = NtfsScanLimits.Large.MaximumEntries + 1 }, new OverBudgetEntries(), [""], RootId, NtfsScanLimits.Large),
            "entry limit checked before touching over-budget collection");
        var badPaths = new string?[] { "", "../a", "b", null, null };
        Invalid(Inventory(suppliedPaths: badPaths), "noncanonical cached directory path rejected");
        Invalid(Inventory(suppliedPaths: ["unexpected", "a", "b", null, null]), "cached root must remain empty");
        Invalid(Inventory(suppliedPaths: ["", new string('a', 32768), "b", null, null]), "cached directory path length remains bounded");
        Invalid(Inventory(suppliedPaths: ["", string.Join('/', Enumerable.Repeat("a", 513)), "b", null, null]), "cached directory depth remains bounded");
        Invalid(Inventory(report: r => r with { NameCharacters = NtfsScanLimits.Large.MaximumCharacters + 1 }), "declared name characters remain bounded");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); var error = Catch(() => NtfsNamespaceAncestorComparison.Compare(Inventory(), Inventory(), cancellation.Token));
            Check(error is OperationCanceledException c && c.CancellationToken == cancellation.Token, "comparison honors caller cancellation");
        }

        {
            var source = new Source(); var stages = new List<NamespaceAncestorStage>();
            var r = NtfsNamespaceAncestorComparison.Execute(() => source, default, stages.Add);
            Check(r.Outcome == NamespaceAncestorOutcome.Matched && r.Code == NamespaceAncestorCode.None && r.Checks == NamespaceAncestorChecks.All &&
                r.ComparisonComplete && r.SameSourceVerified && r.CleanupConfirmed, "same-source execute matches");
            Check(source.Modes.SequenceEqual(new[] { false, true }) && source.PriorLeaseSettled, "original then candidate uses separately settled leases");
            Check(stages.SequenceEqual(new[] { NamespaceAncestorStage.Snapshot, NamespaceAncestorStage.Original, NamespaceAncestorStage.Candidate, NamespaceAncestorStage.Compare, NamespaceAncestorStage.Cleanup }),
                "fixed stages finish with cleanup");
            Check(source.Backends.All(b => b.Disposed && b.Opened == 3 && b.Closed == 3) && source.CleanupCalls == 1 && source.DisposeCalls == 1,
                "both real-policy leases and all synthetic handles close once");
            Check(r.Original is { HandlesSettled: true, Entries: 5, Opened: 3, Completed: 3 } && r.Candidate is { HandlesSettled: true, Entries: 5, Opened: 3, Completed: 3 } &&
                r.Original.Milliseconds >= 0 && r.Candidate.Milliseconds >= 0, "measurements include settled complete enumerations");
            Check(!r.Balanced && r.OriginalRepeat is null && r.CandidateRepeat is null, "default execute retains two-round measurements");
        }
        {
            var source = new Source();
            var stages = new List<NamespaceAncestorStage>();
            var result = NtfsNamespaceAncestorComparison.Execute(() => source, default, stages.Add, balanced: true);
            Check(result.Balanced && result.Outcome == NamespaceAncestorOutcome.Matched && result.Checks == NamespaceAncestorChecks.All &&
                result.ComparisonComplete && result.SameSourceVerified && result.CleanupConfirmed, "balanced comparison matches on one source");
            Check(source.Modes.SequenceEqual(new[] { true, false, false, true }) && source.PriorLeaseSettled,
                "balanced order uses four separately settled leases");
            Check(stages.SequenceEqual(new[] { NamespaceAncestorStage.Snapshot, NamespaceAncestorStage.Candidate,
                NamespaceAncestorStage.Original, NamespaceAncestorStage.Compare, NamespaceAncestorStage.Original,
                NamespaceAncestorStage.Compare, NamespaceAncestorStage.Candidate, NamespaceAncestorStage.Compare, NamespaceAncestorStage.Cleanup }),
                "balanced comparisons run immediately after each traversal following the reference");
            Check(new[] { result.Original, result.Candidate, result.OriginalRepeat, result.CandidateRepeat }.All(
                m => m is { HandlesSettled: true, Entries: 5, Opened: 3, Completed: 3, Milliseconds: >= 0 }),
                "balanced success reports four complete measurements");
            Check(source.Backends.All(b => b.Disposed && b.Opened == 3 && b.Closed == 3) &&
                source.CleanupCalls == 1 && source.DisposeCalls == 1, "balanced success settles all handles and cleans the source once");
        }
        {
            var source = new Source();
            source.Build = _ =>
            {
                var backend = new Backend();
                if (source.Modes.Count == 2)
                    backend.Pages["a"] = [Page(Entry(3, 1, "changed.bin", 201))];
                return backend;
            };
            var result = NtfsNamespaceAncestorComparison.Execute(() => source, default, _ => { }, balanced: true);
            Check(result.Balanced && result.Outcome == NamespaceAncestorOutcome.Different && result.CleanupConfirmed &&
                result.ComparisonComplete && !Has(result.Checks, NamespaceAncestorChecks.Paths),
                "balanced mode preserves first observed mismatch");
            Check(source.Modes.SequenceEqual(new[] { true, false }) && source.PriorLeaseSettled &&
                result.Original is not null && result.Candidate is not null && result.OriginalRepeat is null && result.CandidateRepeat is null &&
                source.Backends.All(b => b.Disposed && b.Opened == b.Closed) && source.CleanupCalls == 1 && source.DisposeCalls == 1,
                "balanced mismatch skips remaining traversals and settles the source");
        }
        foreach (int faultRound in new[] { 3, 4 })
        {
            var source = new Source { CleanupValue = faultRound == 3 };
            source.Build = _ => new Backend { FailRead = source.Modes.Count == faultRound };
            var result = NtfsNamespaceAncestorComparison.Execute(() => source, default, _ => { }, balanced: true);
            SafeFailure(result, "balanced traversal failure keeps private details out");
            Check(result.Balanced && result.Outcome == NamespaceAncestorOutcome.Failed && result.CleanupConfirmed == (faultRound == 3) &&
                result.Code == (faultRound == 3 ? NamespaceAncestorCode.EnumerationFailed : NamespaceAncestorCode.CleanupUnconfirmed),
                "balanced traversal failure gives uncertain cleanup precedence");
            Check(source.Modes.Count == faultRound && source.PriorLeaseSettled && result.CandidateRepeat is null &&
                (result.OriginalRepeat is not null) == (faultRound == 4) &&
                source.Backends.All(b => b.Disposed && b.Opened == b.Closed) && source.CleanupCalls == 1 && source.DisposeCalls == 1,
                "failed balanced round closes its lease without retry");
        }
        foreach (string fault in new[] { "open", "read", "cursor-close", "lease-close", "malformed-page" })
        foreach (bool candidate in new[] { false, true })
        {
            var source = new Source { Build = mode =>
            {
                var b = new Backend(); if (mode != candidate) return b;
                switch (fault)
                {
                    case "open": b.FailOpen = true; break; case "read": b.FailRead = true; break;
                    case "cursor-close": b.FailCursorDispose = true; break; case "lease-close": b.FailDispose = true; break;
                    default: b.Pages[""] = [new byte[NtfsSnapshotNamespace.PageBytes]]; break;
                }
                return b;
            } };
            var r = NtfsNamespaceAncestorComparison.Execute(() => source, default, _ => { });
            SafeFailure(r, fault + " does not leak private exception content");
            Check(source.Modes.Count == (candidate ? 2 : 1) && source.Backends.All(b => b.Disposed && b.Opened == b.Closed) &&
                source.CleanupCalls == 1 && source.DisposeCalls == 1 && r.CleanupConfirmed, fault + " stops without retry and settles source");
        }
        {
            var source = new Source { ChangeRootAfter = 2 };
            var r = NtfsNamespaceAncestorComparison.Execute(() => source, default, _ => { });
            SafeFailure(r, "changed source root cannot compare");
            Check(r.Code == NamespaceAncestorCode.SourceChanged && source.Modes.SequenceEqual(new[] { false }) && r.CleanupConfirmed, "source mismatch stops before candidate");
        }
        {
            var source = new Source { Build = _ => new Backend { RootOverride = Id(600) } };
            var r = NtfsNamespaceAncestorComparison.Execute(() => source, default, _ => { });
            SafeFailure(r, "inventory root must remain bound to source");
            Check(r.Code == NamespaceAncestorCode.SourceChanged && source.Modes.SequenceEqual(new[] { false }) && r.CleanupConfirmed, "inventory root mismatch stops before candidate");
        }
        {
            var source = new Source { Build = reuse =>
            {
                var b = new Backend();
                if (reuse) b.Pages["a"] = [Page(Entry(3, 1, "changed.bin", 201))];
                return b;
            } };
            var r = NtfsNamespaceAncestorComparison.Execute(() => source, default, _ => { });
            Check(r.Outcome == NamespaceAncestorOutcome.Different && r.Code == NamespaceAncestorCode.None && r.CleanupConfirmed && r.ComparisonComplete && r.SameSourceVerified &&
                !Has(r.Checks, NamespaceAncestorChecks.Paths), "observed directory difference remains a complete comparison without retry");
        }
        foreach (bool confirmed in new[] { true, false })
        {
            var r = NtfsNamespaceAncestorComparison.Execute(() => throw new NtfsPersonalSnapshotOpenException(PrivateMarker, 5, confirmed), default, _ => { });
            SafeFailure(r, "controlled factory failure is sanitized");
            Check(r.CleanupConfirmed == confirmed && r.Code == (confirmed ? NamespaceAncestorCode.SnapshotUnavailable : NamespaceAncestorCode.CleanupUnconfirmed),
                "controlled factory cleanup evidence is retained");
        }
        {
            var r = NtfsNamespaceAncestorComparison.Execute(() => throw new IOException(PrivateMarker), default, _ => { });
            SafeFailure(r, "unknown factory failure is sanitized");
            Check(!r.CleanupConfirmed && r.Code == NamespaceAncestorCode.CleanupUnconfirmed, "unknown factory exception cannot prove cleanup");
        }
        {
            var r = NtfsNamespaceAncestorComparison.Execute(() => throw new OperationCanceledException(PrivateMarker), default, _ => { });
            SafeFailure(r, "external cancellation exception content is sanitized");
            Check(r.Outcome == NamespaceAncestorOutcome.Failed && r.Code == NamespaceAncestorCode.CleanupUnconfirmed,
                "external cancellation type cannot impersonate caller cancellation or cleanup");
        }
        foreach (string kind in new[] { "cleanup-false", "cleanup-throws", "dispose-throws", "cancelled-cleanup-false" })
        {
            using var cancellation = new CancellationTokenSource();
            var source = new Source { CleanupValue = kind is not ("cleanup-false" or "cancelled-cleanup-false"), CleanupThrows = kind == "cleanup-throws", DisposeThrows = kind == "dispose-throws" };
            if (kind == "cancelled-cleanup-false") source.OnCleanup = cancellation.Cancel;
            var r = NtfsNamespaceAncestorComparison.Execute(() => source, cancellation.Token, _ => { });
            SafeFailure(r, kind + " clears successful comparison");
            Check(r.Outcome == NamespaceAncestorOutcome.Failed && r.Code == NamespaceAncestorCode.CleanupUnconfirmed && !r.CleanupConfirmed && source.DisposeCalls == 1,
                kind + " cleanup uncertainty takes priority");
        }
        foreach (string point in new[] { "before-factory", "original-read", "candidate-read", "cleanup" })
        {
            using var cancellation = new CancellationTokenSource(); int created = 0;
            var source = new Source { Build = reuse => new Backend { OnRead = (point == "original-read" && !reuse || point == "candidate-read" && reuse) ? cancellation.Cancel : null } };
            if (point == "before-factory") cancellation.Cancel();
            if (point == "cleanup") source.OnCleanup = cancellation.Cancel;
            var r = NtfsNamespaceAncestorComparison.Execute(() => { created++; return source; }, cancellation.Token, _ => { });
            Check(r.Outcome == NamespaceAncestorOutcome.Cancelled && r.Code == NamespaceAncestorCode.Cancelled && r.CleanupConfirmed && !r.ComparisonComplete && !r.SameSourceVerified,
                point + " cancellation is distinct from failure");
            Check(point == "before-factory" ? created == 0 : source.CleanupCalls == 1 && source.DisposeCalls == 1 && source.Backends.All(b => b.Disposed && b.Opened == b.Closed),
                point + " cancellation leaves no outstanding lease");
        }
        foreach (NamespaceAncestorStage point in new[] { NamespaceAncestorStage.Snapshot, NamespaceAncestorStage.Original, NamespaceAncestorStage.Candidate, NamespaceAncestorStage.Compare })
        {
            var source = new Source(); int created = 0;
            var r = NtfsNamespaceAncestorComparison.Execute(() => { created++; return source; }, default, stage => { if (stage == point) throw new IOException(PrivateMarker); });
            SafeFailure(r, "stage callback exception is sanitized");
            Check(r.CleanupConfirmed && (created == 0 || source.CleanupCalls == 1 && source.DisposeCalls == 1), "stage callback failure still settles owned source");
        }
        return passed;
    }
}
