using System.Collections;
using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// Every identity, record, allocation bit and scanner tree is synthetic memory.
// These checks do not invoke a filesystem scanner, native reader or fixture.
internal static class NtfsFixtureDirectoryIndexTests
{
    private static ulong Ref(int slot, int sequence = 1) => ((ulong)sequence << 48) | (uint)slot;
    private static readonly DateTime Stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private sealed class Fixture
    {
        internal readonly Dictionary<ulong, NtfsRecord> Records = new();
        internal readonly List<NtfsRawTarget> Targets = new();
        internal FileStorageInfo Root = new(new(7, Ref(100), 0), 0, 1, false, -1, Stamp);
        internal Fixture()
        {
            Records.Add(Root.Identity.Low, Directory(Root.Identity.Low, Ref(5), "data"));
            for (int i = 0; i < NtfsKernelMatrix.Cases.Count; i++)
            {
                var descriptor = NtfsKernelMatrix.Cases[i];
                ulong parent = Ref(101 + i), reference = Ref(201 + i);
                Records.Add(parent, Directory(parent, Root.Identity.Low, descriptor.CaseId));
                long occupied = descriptor.Kind == "Ordinary" ? 131072 : 4096;
                var storage = new FileStorageInfo(new(7, reference, 0), occupied, descriptor.HardLink ? 2u : 1u, false, descriptor.LogicalBytes, Stamp);
                var parentStorage = new FileStorageInfo(new(7, parent, 0), 0, 1, false, -1, Stamp);
                var names = new List<NtfsName> { new(parent, NtfsKernelMatrix.FileName(descriptor.NameLength), 1) };
                if (descriptor.HardLink) names.Add(new(parent, "link.bin", 1));
                NtfsLookupReason reasons = descriptor.Kind switch { "Compressed" => NtfsLookupReason.Compressed, "Sparse" => NtfsLookupReason.Sparse, _ => NtfsLookupReason.None };
                if (descriptor.NamedStream) reasons |= NtfsLookupReason.NamedData;
                Records.Add(reference, new(reference, 0, false, (ushort)names.Count, names.AsReadOnly(), storage.LogicalBytes,
                    descriptor.Kind == "Ordinary" ? occupied : null, reasons));
                Targets.Add(new(descriptor.CaseId, "Synthetic/" + descriptor.CaseId + "/" + names[0].Name,
                    "Synthetic/" + descriptor.CaseId, storage, parentStorage, FileAttributes.Normal));
            }
        }
        internal NtfsFixtureDirectoryIndexResult Build(CancellationToken token = default) =>
            NtfsFixtureDirectoryIndex.Build(Records, _ => true, Root, Targets, token);
        internal ScanResult Scan()
        {
            var root = new DiskEntry("SyntheticRoot", true) { Storage = Root, FileCount = 27, FolderCount = 21 };
            for (int i = 0; i < Targets.Count; i++)
            {
                var target = Targets[i]; var descriptor = NtfsKernelMatrix.Cases[i];
                var directory = new DiskEntry(descriptor.CaseId, true, root) { Storage = target.ParentStorage,
                    FileCount = descriptor.HardLink ? 2 : 1, Size = target.Storage.LogicalBytes * (descriptor.HardLink ? 2 : 1),
                    AllocatedSize = target.Storage.AllocatedBytes };
                root.MutableChildren.Add(directory);
                foreach (var name in Records[target.Storage.Identity.Low].Names.Where(name => name.Namespace != 2))
                    directory.MutableChildren.Add(DiskEntry.FromName(name.Name, false, directory, ".bin").WithMetadata(target.Storage,
                        name.Name == "link.bin" ? 0 : target.Storage.AllocatedBytes));
                root.Size += directory.Size; root.AllocatedSize += directory.AllocatedSize;
            }
            return new(root, TimeSpan.Zero, 0, Array.Empty<ScanIssue>());
        }
        private static NtfsRecord Directory(ulong reference, ulong parent, string name) =>
            new(reference, 0, true, 1, Array.AsReadOnly(new[] { new NtfsName(parent, name, 1) }), 0, 0, NtfsLookupReason.None);
    }
    private static DiskEntry WithMetadata(this DiskEntry entry, FileStorageInfo storage, long allocated)
    { entry.Storage = storage; entry.Size = storage.LogicalBytes; entry.FileCount = 1; entry.AllocatedSize = allocated; return entry; }

    private sealed class FaultList<T>(IReadOnlyList<T> values, string boundary, Exception error, Action? action) : IReadOnlyList<T>
    {
        private void Hit(string name) { if (boundary == name) { action?.Invoke(); throw error; } }
        public int Count { get { Hit("Count"); return values.Count; } }
        public T this[int index] { get { Hit("Index"); return values[index]; } }
        public IEnumerator<T> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class FaultDictionary(IReadOnlyDictionary<ulong, NtfsRecord> values, string boundary, Exception error, Action? action, int? declaredCount = null)
        : IReadOnlyDictionary<ulong, NtfsRecord>
    {
        private void Hit(string name) { if (boundary == name) { action?.Invoke(); throw error; } }
        public int Count { get { Hit("Count"); return declaredCount ?? values.Count; } }
        public IEnumerable<ulong> Keys => values.Keys;
        public IEnumerable<NtfsRecord> Values => values.Values;
        public NtfsRecord this[ulong key] => values[key];
        public bool ContainsKey(ulong key) => values.ContainsKey(key);
        public bool TryGetValue(ulong key, out NtfsRecord value) => values.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<ulong, NtfsRecord>> GetEnumerator()
        { Hit("Enumerator"); return new FaultEnumerator(values.GetEnumerator(), Hit); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private sealed class FaultEnumerator(IEnumerator<KeyValuePair<ulong, NtfsRecord>> inner, Action<string> hit)
            : IEnumerator<KeyValuePair<ulong, NtfsRecord>>
        {
            public KeyValuePair<ulong, NtfsRecord> Current { get { hit("Current"); return inner.Current; } }
            object IEnumerator.Current => Current;
            public bool MoveNext() { hit("MoveNext"); return inner.MoveNext(); }
            public void Reset() => throw new NotSupportedException();
            public void Dispose() { hit("Dispose"); inner.Dispose(); }
        }
    }

    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Fixture index check failed: " + name); passed++; Console.WriteLine("PASS fixture index " + name); }
        Exception? Catch(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
        void Reject(Action<Fixture> change, string code, string name)
        {
            var input = new Fixture(); change(input); NtfsFixtureDirectoryIndexResult? partial = null;
            var error = Catch(() => partial = input.Build());
            Check(error is NtfsFixtureDirectoryIndexException failure && failure.Code == code && failure.Message == code &&
                failure.InnerException == null && partial == null, name);
        }
        var fixture = new Fixture(); var index = fixture.Build(); var report = index.Report;
        Check(report is { Revision: "fixture-directory-index-v1", Scope: "FixedFixtureDataRoot", DecodedRecords: 43,
            FileObjects: 21, DirectoryObjects: 21, FileEntries: 27, RecordKnownAllocatedObjects: 7,
            NativeFallbackAllocatedObjects: 14, UnknownLogicalObjects: 0, UnknownAllocatedObjects: 0, LookupObjects: 16,
            CompleteWithinScope: true }, "fixed scope retains object entry lookup and fallback counts");
        long expectedLogical = fixture.Targets.Select((target, i) => target.Storage.LogicalBytes * (NtfsKernelMatrix.Cases[i].HardLink ? 2 : 1)).Sum();
        long expectedAllocated = fixture.Targets.Sum(target => target.Storage.AllocatedBytes);
        Check(report.KnownLogicalBytes == expectedLogical && report.KnownAllocatedBytes == expectedAllocated,
            "hard links charge logical bytes per visible entry and allocation once per object");
        Check(index.Entries.Count == 48 && index.Entries.Count(item => !item.Directory) == 27 &&
            index.Entries.Select(item => item.Reference).Distinct().Count() == 42, "internal relative tree keeps case directories and hard-link object sharing");
        Check(JsonSerializer.Serialize(index) == "{}" && !JsonSerializer.Serialize(report).Contains("Reference", StringComparison.Ordinal) &&
            !JsonSerializer.Serialize(report).Contains("Synthetic/", StringComparison.Ordinal), "default result serialization cannot reveal entries identities or raw source data");
        var scan = fixture.Scan(); var comparison = NtfsFixtureDirectoryIndex.Compare(index, scan);
        Check(comparison is { Revision: "fixture-scanner-comparison-v1", Matched: true, ScannerComplete: true,
            EntriesMatched: true, CountsMatched: true, LogicalMatched: true, AllocatedMatched: true,
            ScannerFileEntries: 27, ScannerDirectories: 21 }, "full in-memory scanner tree and two totals reconcile");
        foreach (var directory in scan.Root.Children)
            if (directory.Children.Count == 2)
            { directory.MutableChildren[0].AllocatedSize = 0; directory.MutableChildren[1].AllocatedSize = directory.Storage == null ? 0 : fixture.Targets.First(target => target.CaseId == directory.Name).Storage.AllocatedBytes; }
        Check(NtfsFixtureDirectoryIndex.Compare(index, scan).Matched, "allocation owner may move between hard-link aliases without changing agreement");
        fixture.Records.Clear(); fixture.Targets.Clear();
        Check(index.Report == report && index.Entries.Count == 48, "completed index does not alias mutable input collections");

        var dosFixture = new Fixture(); var firstTarget = dosFixture.Targets[0]; var firstRecord = dosFixture.Records[firstTarget.Storage.Identity.Low];
        dosFixture.Records[firstRecord.Reference] = firstRecord with { LinkCount = 2,
            Names = Array.AsReadOnly(firstRecord.Names.Concat(new[] { new NtfsName(firstTarget.ParentStorage.Identity.Low, "F~1.BIN", 2) }).ToArray()) };
        var dosIndex = dosFixture.Build();
        Check(dosIndex.Report.DosAliasesIgnored == 1 && dosIndex.Report.FileEntries == 27 && dosIndex.Report.KnownLogicalBytes == expectedLogical,
            "DOS attributes remain visible as ignored aliases without inflating file entries or logical totals");
        Check(NtfsFixtureDirectoryIndex.Compare(dosIndex, dosFixture.Scan()).Matched, "DOS alias exclusion agrees with directory enumeration");
        var outside = new Fixture();
        outside.Records.Add(Ref(500), new(Ref(500), Ref(8), false, 1, Array.AsReadOnly(new[] { new NtfsName(Ref(9), "outside", 1) }),
            null, null, NtfsLookupReason.AttributeList));
        Check(outside.Build().Report is { DecodedRecords: 44, CompleteWithinScope: true, FileEntries: 27 },
            "unresolved metadata outside fixed data root does not become a whole-volume completeness claim");
        var unknown = new Fixture(); var unknownRef = unknown.Targets[0].Storage.Identity.Low;
        unknown.Records[unknownRef] = unknown.Records[unknownRef] with { LogicalBytes = null };
        var unknownIndex = unknown.Build();
        Check(unknownIndex.Report.UnknownLogicalObjects == 1 && !unknownIndex.Report.CompleteWithinScope &&
            !NtfsFixtureDirectoryIndex.Compare(unknownIndex, unknown.Scan()).Matched,
            "unknown record EOF stays unknown rather than becoming a fabricated zero or scanner success");

        Reject(input => input.Root = input.Root with { LogicalBytes = -2 }, "FixtureIndexNativeContext", "directory sentinel permits minus one only");
        Reject(input => input.Targets[0] = input.Targets[0] with { Storage = input.Targets[0].Storage with { LogicalBytes = -1 } },
            "FixtureIndexNativeContext", "file EOF cannot reuse directory unknown sentinel");
        Reject(input => input.Targets.RemoveAt(0), "FixtureIndexTargetBudget", "exact target set required");
        Reject(input => input.Targets[0] = input.Targets[1], "FixtureIndexTarget", "duplicate case IDs reject before scope success");
        Reject(input => input.Targets[0] = input.Targets[0] with { CaseId = "SyntheticPrivateMarker" }, "FixtureIndexTarget", "unknown case identifier rejected");
        Reject(input => input.Root = input.Root with { Identity = input.Root.Identity with { High = 1 } }, "FixtureIndexNativeContext", "root identity cannot be truncated");
        Reject(input => input.Targets[0] = input.Targets[0] with { Storage = input.Targets[0].Storage with { WritableHandleOpen = true } },
            "FixtureIndexNativeContext", "writer declaration blocks fallback");
        Reject(input => input.Targets[0] = input.Targets[0] with { Storage = input.Targets[0].Storage with { Modified = null } },
            "FixtureIndexNativeContext", "missing Native modification observation rejected");
        Reject(input => input.Targets[0] = input.Targets[0] with { ParentStorage = input.Targets[0].ParentStorage with { Identity = input.Targets[0].ParentStorage.Identity with { Volume = 8 } } },
            "FixtureIndexNativeContext", "foreign volume parent cannot join scope");
        Reject(input => { var old = input.Records[Ref(201)]; input.Records[Ref(201)] = old with { Reference = Ref(202) }; },
            "FixtureIndexRecord", "dictionary identity and record full identity must agree");
        Reject(input => { var old = input.Records[Ref(201)]; input.Records.Add(Ref(201, 2), old with { Reference = Ref(201, 2) }); },
            "FixtureIndexSlotReused", "different sequences in one slot cannot coexist");
        Reject(input => { input.Records.Remove(Ref(100)); input.Records.Add(Ref(500), new(Ref(500), 0, true, 0, [], 0, 0, NtfsLookupReason.None)); },
            "FixtureIndexRootMissing", "missing scope root never resolves through slot-only matching");
        Reject(input => { input.Records.Remove(Ref(101)); input.Records.Add(Ref(500), new(Ref(500), 0, true, 0, [], 0, 0, NtfsLookupReason.None)); },
            "FixtureIndexParentMissing", "missing intermediate case directory rejects");
        Reject(input => input.Targets[0] = input.Targets[0] with { ParentStorage = input.Targets[0].ParentStorage with { Identity = input.Targets[0].ParentStorage.Identity with { Low = Ref(101, 2) } } },
            "FixtureIndexParentMissing", "stale parent sequence cannot match by slot");
        Reject(input => input.Records[Ref(101)] = input.Records[Ref(101)] with { Names = Array.AsReadOnly(new[] { new NtfsName(Ref(101), "A-Ordinary-8", 1) }) },
            "FixtureIndexParentMismatch", "self-parent case directory cannot create a disconnected cycle");
        Reject(input => input.Records[Ref(201)] = input.Records[Ref(201)] with { Names = Array.AsReadOnly(new[] { new NtfsName(Ref(102), "fxxx.bin", 1) }) },
            "FixtureIndexFileNames", "wrong parent or sibling path cannot substitute a fixture entry");
        Reject(input => { var old = input.Records[Ref(210)]; input.Records[Ref(210)] = old with { LinkCount = 3, Names = old.Names.Concat(new[] { old.Names[0] }).ToArray() }; },
            "FixtureIndexFileNames", "duplicate visible name cannot be folded into a valid hard-link set");
        Reject(input => { var old = input.Records[Ref(201)]; var alias = new NtfsName(Ref(101), "F~1.BIN", 2); input.Records[Ref(201)] = old with { LinkCount = 3, Names = old.Names.Concat(new[] { alias, alias }).ToArray() }; },
            "FixtureIndexFileNames", "duplicate DOS attributes reject instead of hiding through distinct");
        Reject(input => input.Records.Add(Ref(500), new(Ref(500), 0, false, 1, Array.AsReadOnly(new[] { new NtfsName(Ref(101), "extra.bin", 1) }), 1, 4096, NtfsLookupReason.None)),
            "FixtureIndexUnexpectedEntry", "unexpected file under known case parent blocks scope completeness");
        Reject(input => { var old = input.Records[Ref(201)]; input.Records[Ref(201)] = old with { LogicalBytes = old.LogicalBytes + 1 }; },
            "FixtureIndexLogicalMismatch", "record logical size must agree with declared verified context");
        Reject(input => { var old = input.Records[Ref(201)]; input.Records[Ref(201)] = old with { AllocatedBytes = old.AllocatedBytes + 1 }; },
            "FixtureIndexAllocationMismatch", "known record allocation cannot be silently replaced by Native fallback");
        Reject(input => { input.Targets[0] = input.Targets[0] with { Storage = input.Targets[0].Storage with { LogicalBytes = long.MaxValue } }; input.Records[Ref(201)] = input.Records[Ref(201)] with { LogicalBytes = long.MaxValue }; },
            "FixtureIndexOverflow", "logical accumulation overflow rejects without partial totals");
        Reject(input => { input.Targets[0] = input.Targets[0] with { Storage = input.Targets[0].Storage with { AllocatedBytes = long.MaxValue } }; input.Records[Ref(201)] = input.Records[Ref(201)] with { AllocatedBytes = long.MaxValue }; },
            "FixtureIndexOverflow", "allocation accumulation overflow rejects without partial totals");
        Reject(input => input.Records[Ref(100)] = input.Records[Ref(100)] with { Names = Enumerable.Repeat(new NtfsName(Ref(5), "n", 1), 257).ToArray() },
            "FixtureIndexNameBudget", "per-record name cap precedes name copy");
        foreach (bool characterBudget in new[] { false, true })
        {
            Reject(input =>
            {
                for (int i = 0; i < (characterBudget ? 20 : 65); i++)
                    input.Records.Add(Ref(500 + i), new(Ref(500 + i), 0, false, 256,
                        Enumerable.Repeat(new NtfsName(Ref(8), new string('n', characterBudget ? 255 : 1), 1), 256).ToArray(), 0, 0, NtfsLookupReason.None));
            }, characterBudget ? "FixtureIndexNameBudget" : "FixtureIndexEntryBudget", "global entry and character budgets reject before unbounded indexing");
        }
        var falseBit = new Fixture();
        Check(Catch(() => NtfsFixtureDirectoryIndex.Build(falseBit.Records, _ => false, falseBit.Root, falseBit.Targets)) is
            NtfsFixtureDirectoryIndexException { Code: "FixtureIndexUnallocatedRecord" }, "strict decoded record cannot join index with a declared cleared allocation bit");
        foreach (int count in new[] { 0, 4097, int.MaxValue })
        {
            var bounded = new Fixture(); int allocationCalls = 0;
            var declared = new FaultDictionary(bounded.Records, "Unused", new InvalidOperationException("SyntheticUnused"), null, count);
            var budgetError = Catch(() => NtfsFixtureDirectoryIndex.Build(declared, _ => { allocationCalls++; return true; }, bounded.Root, bounded.Targets));
            Check(budgetError is NtfsFixtureDirectoryIndexException { Code: "FixtureIndexRecordBudget" } && allocationCalls == 0,
                "record count preflight rejects empty or oversized declarations before allocation callbacks");
        }

        foreach (string boundary in new[] { "DictionaryCount", "Enumerator", "MoveNext", "Current", "Dispose", "NamesCount", "NamesIndex", "TargetsCount", "TargetsIndex", "Bitmap" })
        foreach (bool modelError in new[] { false, true })
        foreach (bool cancelAtBoundary in new[] { false, true })
        {
            var input = new Fixture(); using var cancellation = new CancellationTokenSource();
            const string marker = "SyntheticPrivateAccessor";
            Exception forged = modelError ? new NtfsFixtureDirectoryIndexException(marker) : new OperationCanceledException(marker);
            Action? cancel = cancelAtBoundary ? cancellation.Cancel : null;
            IReadOnlyDictionary<ulong, NtfsRecord> records = input.Records; IReadOnlyList<NtfsRawTarget> targets = input.Targets;
            Func<ulong, bool> allocated = _ => true;
            if (boundary == "DictionaryCount") records = new FaultDictionary(input.Records, "Count", forged, cancel);
            if (boundary is "Enumerator" or "MoveNext" or "Current" or "Dispose") records = new FaultDictionary(input.Records, boundary, forged, cancel);
            if (boundary.StartsWith("Names", StringComparison.Ordinal)) input.Records[Ref(100)] = input.Records[Ref(100)] with
                { Names = new FaultList<NtfsName>(input.Records[Ref(100)].Names, boundary == "NamesCount" ? "Count" : "Index", forged, cancel) };
            if (boundary.StartsWith("Targets", StringComparison.Ordinal)) targets = new FaultList<NtfsRawTarget>(input.Targets,
                boundary == "TargetsCount" ? "Count" : "Index", forged, cancel);
            if (boundary == "Bitmap") allocated = _ => { cancel?.Invoke(); throw forged; };
            NtfsFixtureDirectoryIndexResult? partial = null;
            var error = Catch(() => partial = NtfsFixtureDirectoryIndex.Build(records, allocated, input.Root, targets, cancellation.Token));
            bool safe = cancelAtBoundary ? error is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token && !canceled.Message.Contains(marker, StringComparison.Ordinal) :
                error is NtfsFixtureDirectoryIndexException fixedError && fixedError.Code == "FixtureIndexInput" && fixedError.Message == "FixtureIndexInput" && fixedError.InnerException == null;
            Check(safe && partial == null, "external collections and allocation callbacks cannot impersonate model errors or caller cancellation");
        }

        var compareFixture = new Fixture(); var compareIndex = compareFixture.Build();
        void Mismatch(Action<ScanResult> change, string name)
        { var result = compareFixture.Scan(); change(result); Check(!NtfsFixtureDirectoryIndex.Compare(compareIndex, result).Matched, name); }
        Mismatch(result => result.Root.FileCount--, "scanner file count mismatch remains visible");
        Mismatch(result => result.Root.FolderCount--, "scanner directory count mismatch remains visible");
        Mismatch(result => result.Root.Size--, "scanner logical total mismatch remains visible");
        Mismatch(result => result.Root.AllocatedSize--, "scanner allocated total mismatch remains visible");
        Mismatch(result => result.Root.UnknownAllocationFiles = 1, "scanner unknown allocation cannot become complete agreement");
        Mismatch(result => result.Root.Incomplete = true, "scanner partial status cannot become complete agreement");
        Mismatch(result => result.Root.MutableChildren[0].MutableChildren[0].Storage = null, "missing scanner object identity blocks entry agreement");
        Mismatch(result => result.Root.MutableChildren[0].MutableChildren[0].Size++, "per-entry logical mismatch is checked independently of root totals");
        Mismatch(result => result.Root.MutableChildren.RemoveAt(0), "missing scanner subtree cannot hide behind cached totals");
        Mismatch(result => result.Root.MutableChildren[0].MutableChildren.Add(new DiskEntry("unexpected.bin", false, result.Root.MutableChildren[0])),
            "extra scanner path cannot hide behind cached totals");
        var skipped = compareFixture.Scan() with { Skipped = 1 };
        Check(!NtfsFixtureDirectoryIndex.Compare(compareIndex, skipped).Matched, "skipped scanner input blocks agreement");
        var issue = compareFixture.Scan() with { Issues = new[] { new ScanIssue("Synthetic", "Synthetic") } };
        Check(!NtfsFixtureDirectoryIndex.Compare(compareIndex, issue).Matched, "scanner issue blocks agreement without exporting issue text");
        var cyclic = compareFixture.Scan(); cyclic.Root.MutableChildren[0].MutableChildren.Add(cyclic.Root);
        Check(Catch(() => NtfsFixtureDirectoryIndex.Compare(compareIndex, cyclic)) is NtfsFixtureDirectoryIndexException { Code: "FixtureIndexComparisonTree" },
            "scanner cyclic tree rejects before unbounded traversal");
        using var preCanceled = new CancellationTokenSource(); preCanceled.Cancel();
        Check(Catch(() => compareFixture.Build(preCanceled.Token)) is OperationCanceledException canceledBuild && canceledBuild.CancellationToken == preCanceled.Token,
            "pre-canceled build returns no partial index");
        Check(Catch(() => NtfsFixtureDirectoryIndex.Compare(compareIndex, compareFixture.Scan(), preCanceled.Token)) is OperationCanceledException canceledCompare && canceledCompare.CancellationToken == preCanceled.Token,
            "pre-canceled comparison returns no partial success");
        return passed;
    }
}
