using System.Collections;
using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// No filesystem access. Large cases are records and scanner-shaped objects in
// memory, not evidence of real disk throughput or Native snapshot provenance.
internal static class NtfsScaleDirectoryIndexTests
{
    private static ulong Ref(int slot, int sequence = 1) => ((ulong)sequence << 48) | (uint)slot;
    private static readonly DateTime Stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private sealed class Fixture
    {
        internal readonly Dictionary<ulong, NtfsRecord> Records = new();
        internal readonly Dictionary<ulong, FileStorageInfo> Fallback = new();
        internal FileStorageInfo Root = new(new(9, Ref(32), 0), 0, 1, false, -1, Stamp);
        internal readonly int Files, Directories, Links;
        internal Fixture(int files = 100, int directories = 10, bool hardLinks = true)
        {
            Files = files; Directories = directories;
            Records.Add(Root.Identity.Low, Directory(Root.Identity.Low, Ref(5), "data"));
            for (int d = 0; d < directories; d++) Records.Add(Ref(64 + d), Directory(Ref(64 + d), Root.Identity.Low, "d" + d.ToString("D4")));
            for (int i = 0; i < files; i++)
            {
                ulong reference = Ref(512 + i), parent = Ref(64 + i * directories / files);
                long logical = i % 10 == 0 ? 32 : i % 10 == 9 ? 0 : 1024;
                long? allocated = i % 10 == 0 ? null : logical == 0 ? 0 : 4096;
                Records.Add(reference, new(reference, 0, false, 1, Array.AsReadOnly(new[] { new NtfsName(parent, "f" + i.ToString("D7") + ".dat", 1) }),
                    logical, allocated, allocated.HasValue ? NtfsLookupReason.None : NtfsLookupReason.ResidentAllocation));
                if (!allocated.HasValue) Fallback.Add(reference, new(new(9, reference, 0), 0, 1, false, logical, Stamp));
            }
            int links = 0;
            if (hardLinks)
                for (int d = 0; d < Math.Min(10, directories); d++)
                {
                    int start = d * files / directories, end = (d + 1) * files / directories;
                    int i = Enumerable.Range(start, end - start).First(index => index % 10 is >= 1 and <= 8);
                    ulong reference = Ref(512 + i); var record = Records[reference];
                    Records[reference] = record with { LinkCount = 2, Names = Array.AsReadOnly(record.Names.Concat(new[] {
                        new NtfsName(Ref(64 + d), "h" + i.ToString("D7") + ".dat", 1) }).ToArray()) };
                    links++;
                }
            Links = links;
        }
        internal NtfsScaleDirectoryIndexResult Build(bool fallback = true, CancellationToken token = default) =>
            NtfsScaleDirectoryIndex.Build(Records, _ => true, Root, fallback ? Fallback : null, token);
        internal static NtfsRecord Directory(ulong reference, ulong parent, string name) =>
            new(reference, 0, true, 1, Array.AsReadOnly(new[] { new NtfsName(parent, name, 1) }), 0, 0, NtfsLookupReason.None);
    }
    private sealed class FaultList<T>(IReadOnlyList<T> values, string boundary, Exception error, Action? action) : IReadOnlyList<T>
    {
        private void Hit(string name) { if (boundary == name) { action?.Invoke(); throw error; } }
        public int Count { get { Hit("Count"); return values.Count; } }
        public T this[int index] { get { Hit("Index"); return values[index]; } }
        public IEnumerator<T> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class FaultDictionary<T>(IReadOnlyDictionary<ulong, T> values, string boundary, Exception error, Action? action,
        int? declaredCount = null) : IReadOnlyDictionary<ulong, T>
    {
        private void Hit(string name) { if (boundary == name) { action?.Invoke(); throw error; } }
        public int Count { get { Hit("Count"); return declaredCount ?? values.Count; } }
        public IEnumerable<ulong> Keys => values.Keys;
        public IEnumerable<T> Values => values.Values;
        public T this[ulong key] => values[key];
        public bool ContainsKey(ulong key) => values.ContainsKey(key);
        public bool TryGetValue(ulong key, out T value) => values.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<ulong, T>> GetEnumerator()
        { Hit("Enumerator"); return new Enumerator(values.GetEnumerator(), Hit); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private sealed class Enumerator(IEnumerator<KeyValuePair<ulong, T>> inner, Action<string> hit) : IEnumerator<KeyValuePair<ulong, T>>
        {
            public KeyValuePair<ulong, T> Current { get { hit("Current"); return inner.Current; } }
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
        { if (!condition) throw new InvalidOperationException("Scale index check failed: " + name); passed++; Console.WriteLine("PASS scale index " + name); }
        Exception? Catch(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
        void Reject(Action<Fixture> change, string code, string name)
        {
            var input = new Fixture(); change(input); NtfsScaleDirectoryIndexResult? partial = null;
            var error = Catch(() => partial = input.Build());
            Check(error is NtfsScaleDirectoryIndexException failure && failure.Code == code && failure.Message == code &&
                failure.InnerException == null && partial == null, name);
        }
        foreach (int size in new[] { 10000, 100000 })
        {
            var input = new Fixture(size, 100); var index = input.Build(); var report = index.Report;
            long logical = size / 10L * 32 + size / 10L * 8 * 1024 + 10 * 1024;
            long allocated = size / 10L * 8 * 4096;
            Check(report.FileObjects == size && report.DirectoryObjects == 100 && report.FileEntries == size + 10 &&
                report.KnownLogicalBytes == logical && report.KnownAllocatedBytes == allocated && report.CompleteWithinObservedRecords,
                "ten-thousand and hundred-thousand object graphs retain both size conventions and hard links");
            Check(report.RecordKnownAllocatedObjects == size * 9 / 10 && report.NativeFallbackAllocatedObjects == size / 10 &&
                report.UnknownLogicalObjects == 0 && report.UnknownAllocatedObjects == 0 && report.LookupObjects == size / 10,
                "resident fallback and record-known allocation sources are counted independently even when fallback is zero");
            var projected = NtfsScaleDirectoryIndex.Materialize(index, "SyntheticRoot");
            var scan = new ScanResult(projected, TimeSpan.Zero, 0, []);
            Check(NtfsScaleDirectoryIndex.Compare(index, scan).Matched && projected.FileCount == size + 10 && projected.FolderCount == 100,
                "bounded breadth-first projection and scanner-shaped memory tree agree at both requested sizes");
        }
        var fixture = new Fixture(); var result = fixture.Build();
        Check(result.Report is { Revision: "scale-directory-index-v1", Scope: "ObservedDataRoot", FileObjects: 100,
            DirectoryObjects: 10, FileEntries: 110, NativeFallbackAllocatedObjects: 10, RecordKnownAllocatedObjects: 90, IncompleteObjects: 0 },
            "safe report is rooted scope arithmetic rather than fixed twenty-one-object policy");
        Check(JsonSerializer.Serialize(result) == "{}" && !JsonSerializer.Serialize(result.Report).Contains("Reference", StringComparison.Ordinal),
            "raw references relative paths and Native input records are not automatically serialized");
        var noFallback = fixture.Build(false);
        Check(noFallback.Report.UnknownAllocatedObjects == 10 && noFallback.Report.NativeFallbackAllocatedObjects == 0 && !noFallback.Report.CompleteWithinObservedRecords,
            "missing Native supplements preserve unknown resident allocation rather than assuming zero");
        Check(Catch(() => NtfsScaleDirectoryIndex.Materialize(noFallback, "SyntheticRoot")) is NtfsScaleDirectoryIndexException { Code: "ScaleIndexIncomplete" },
            "incomplete allocation is not materialized into zero-valued product entries");
        var unknown = new Fixture(); unknown.Records[Ref(513)] = unknown.Records[Ref(513)] with { LogicalBytes = null };
        Check(unknown.Build().Report is { UnknownLogicalObjects: 1, CompleteWithinObservedRecords: false }, "unknown EOF stays separate from an empty default stream");
        var unresolved = new Fixture(); unresolved.Records[Ref(513)] = unresolved.Records[Ref(513)] with { LookupReasons = NtfsLookupReason.AttributeList };
        Check(unresolved.Build().Report is { IncompleteObjects: 1, CompleteWithinObservedRecords: false }, "known numbers do not hide unresolved extension or namespace evidence");
        var dos = new Fixture(); var file = dos.Records[Ref(513)];
        dos.Records[Ref(513)] = file with { LinkCount = (ushort)(file.LinkCount + 1), Names = file.Names.Concat(new[] { new NtfsName(Ref(64), "F~1.DAT", 2) }).ToArray() };
        Check(dos.Build().Report is { DosAliasesIgnored: 1, FileEntries: 110, CompleteWithinObservedRecords: true }, "DOS name attributes do not become hard-link entries");
        var outside = new Fixture(); outside.Records.Add(Ref(6), Fixture.Directory(Ref(6), Ref(6), "."));
        Check(outside.Build().Report.CompleteWithinObservedRecords, "unrelated volume-root dot name and disconnected outside graph are not traversed");
        var crossScope = new Fixture(); file = crossScope.Records[Ref(513)];
        crossScope.Records[Ref(513)] = file with { LinkCount = (ushort)(file.LinkCount + 1), Names = file.Names.Concat(new[] { new NtfsName(Ref(8), "outside.dat", 1) }).ToArray() };
        Check(crossScope.Build().Report.FileEntries == 110 && crossScope.Build().Report.KnownAllocatedBytes == result.Report.KnownAllocatedBytes,
            "a hard link outside data root does not inflate scoped logical bytes or object allocation");
        var nested = new Fixture(); var dir = nested.Records[Ref(65)]; nested.Records[Ref(65)] = dir with { Names = new[] { new NtfsName(Ref(64), "nested", 1) } };
        var nestedIndex = nested.Build();
        Check(nestedIndex.Entries.Any(entry => entry.RelativePath == "d0000/nested") && NtfsScaleDirectoryIndex.Compare(nestedIndex,
            new(NtfsScaleDirectoryIndex.Materialize(nestedIndex, "SyntheticRoot"), TimeSpan.Zero, 0, [])).Matched,
            "multi-level directories use parent adjacency and breadth-first indices rather than repeated ancestor walks");
        var missing = new Fixture(); missing.Records.Remove(Ref(65)); var observed = missing.Build();
        Check(observed.Report.FileEntries < result.Report.FileEntries && !NtfsScaleDirectoryIndex.Compare(observed,
            new(NtfsScaleDirectoryIndex.Materialize(result, "SyntheticRoot"), TimeSpan.Zero, 0, [])).Matched,
            "missing parent cannot silently pass full scanner or expected creation-scope comparison");
        fixture.Records.Clear(); fixture.Fallback.Clear();
        Check(result.Report.FileObjects == 100 && result.Entries.Count == 120, "published calculation does not alias caller dictionaries");

        Reject(input => input.Root = input.Root with { LogicalBytes = -2 }, "ScaleIndexNativeContext", "only minus-one directory EOF sentinel is accepted");
        Reject(input => input.Records.Remove(input.Root.Identity.Low), "ScaleIndexRootMissing", "root full reference must be present");
        Reject(input => input.Records[Ref(32)] = input.Records[Ref(32)] with { LookupReasons = NtfsLookupReason.ReparsePoint },
            "ScaleIndexDirectoryUnsupported", "root reparse semantics cannot become an ordinary tree");
        Reject(input => input.Records[Ref(64)] = input.Records[Ref(64)] with { Names = new[] { new NtfsName(Ref(32), "same", 1), new NtfsName(Ref(65), "other", 1) } },
            "ScaleIndexDirectoryParents", "a directory with two visible parents cannot create an ambiguous graph");
        Reject(input => input.Records[Ref(64)] = input.Records[Ref(64)] with { Names = new[] { new NtfsName(Ref(32), "bad/child", 1) } },
            "ScaleIndexEntryCollision", "scoped component cannot escape through separators");
        Reject(input => input.Records[Ref(65)] = input.Records[Ref(65)] with { Names = new[] { new NtfsName(Ref(32), "D0000", 1) } },
            "ScaleIndexEntryCollision", "case-insensitive sibling collision rejects rather than merging names");
        Reject(input => input.Records[Ref(513)] = input.Records[Ref(513)] with { Reference = Ref(514) }, "ScaleIndexRecord", "dictionary and record complete reference must match");
        Reject(input => input.Records.Add(Ref(513, 2), input.Records[Ref(513)] with { Reference = Ref(513, 2) }), "ScaleIndexSlotReused", "sequence reuse is not collapsed by record slot");
        Reject(input => input.Records[Ref(513)] = input.Records[Ref(513)] with { BaseReference = Ref(100) }, "ScaleIndexExtension", "scope extension record cannot masquerade as its base object");
        Reject(input => input.Records[Ref(513)] = input.Records[Ref(513)] with { LogicalBytes = -1 }, "ScaleIndexLogical", "negative logical input rejected");
        Reject(input => input.Records[Ref(513)] = input.Records[Ref(513)] with { AllocatedBytes = -1 }, "ScaleIndexAllocated", "negative allocation input rejected");
        Reject(input => { var old = input.Records[Ref(513)]; var alias = new NtfsName(Ref(64), "F~1.DAT", 2); input.Records[Ref(513)] = old with
            { LinkCount = (ushort)(old.LinkCount + 2), Names = old.Names.Concat(new[] { alias, alias }).ToArray() }; },
            "ScaleIndexDuplicateName", "repeated DOS attributes are rejected rather than distinct-folded");
        Reject(input => input.Fallback[Ref(512)] = input.Fallback[Ref(512)] with { Identity = input.Fallback[Ref(512)].Identity with { High = 1 } },
            "ScaleIndexNativeContext", "Native supplement identity is never truncated");
        Reject(input => input.Fallback[Ref(512)] = input.Fallback[Ref(512)] with { WritableHandleOpen = true }, "ScaleIndexNativeContext", "writer declaration prevents fallback reuse");
        Reject(input => input.Fallback[Ref(512)] = input.Fallback[Ref(512)] with { LogicalBytes = 33 }, "ScaleIndexNativeLogicalMismatch", "fallback EOF must agree with decoded object");
        Reject(input => input.Fallback[Ref(512)] = input.Fallback[Ref(512)] with { LinkCount = 2 }, "ScaleIndexNativeLinksMismatch", "fallback links must agree with all visible object names");
        Reject(input => input.Fallback.Add(Ref(513), new(new(9, Ref(513), 0), 8192, 2, false, 1024, Stamp)),
            "ScaleIndexNativeAllocationMismatch", "record-known allocation cannot be replaced by contradictory Native input");
        Reject(input => input.Records[Ref(513)] = input.Records[Ref(513)] with { LogicalBytes = long.MaxValue }, "ScaleIndexOverflow", "logical overflow cannot publish a valid prefix");
        Reject(input => input.Records[Ref(513)] = input.Records[Ref(513)] with { AllocatedBytes = long.MaxValue }, "ScaleIndexOverflow", "allocated overflow cannot publish a valid prefix");
        Reject(input => input.Records[Ref(32)] = input.Records[Ref(32)] with { Names = Enumerable.Repeat(new NtfsName(Ref(5), "n", 1), 257).ToArray() },
            "ScaleIndexEntryBudget", "per-record name bound precedes copying");
        foreach (int count in new[] { 262145, int.MaxValue })
        {
            var input = new Fixture(); int callbacks = 0;
            var declared = new FaultDictionary<NtfsRecord>(input.Records, "Unused", new InvalidOperationException("SyntheticUnused"), null, count);
            Check(Catch(() => NtfsScaleDirectoryIndex.Build(declared, _ => { callbacks++; return true; }, input.Root, input.Fallback)) is
                NtfsScaleDirectoryIndexException { Code: "ScaleIndexRecordBudget" } && callbacks == 0, "record budget rejects before allocation callback or enumeration");
        }
        {
            var input = new Fixture(); var declared = new FaultDictionary<FileStorageInfo>(input.Fallback, "Unused", new InvalidOperationException("SyntheticUnused"), null, 32769);
            Check(Catch(() => NtfsScaleDirectoryIndex.Build(input.Records, _ => true, input.Root, declared)) is
                NtfsScaleDirectoryIndexException { Code: "ScaleIndexRecordBudget" }, "Native fallback input has a separate bounded object cap");
        }
        foreach (string boundary in new[] { "Count", "Enumerator", "MoveNext", "Current", "Dispose", "NameCount", "NameIndex", "FallbackCount", "Bitmap" })
        foreach (bool modelError in new[] { false, true })
        foreach (bool cancelAtBoundary in new[] { false, true })
        {
            var input = new Fixture(); using var cancellation = new CancellationTokenSource();
            const string marker = "SyntheticPrivateAccessor";
            Exception forged = modelError ? new NtfsScaleDirectoryIndexException(marker) : new OperationCanceledException(marker);
            Action? cancel = cancelAtBoundary ? cancellation.Cancel : null;
            IReadOnlyDictionary<ulong, NtfsRecord> records = input.Records; IReadOnlyDictionary<ulong, FileStorageInfo> supplements = input.Fallback;
            Func<ulong, bool> allocated = _ => true;
            if (boundary is "Count" or "Enumerator" or "MoveNext" or "Current" or "Dispose") records = new FaultDictionary<NtfsRecord>(input.Records, boundary, forged, cancel);
            if (boundary.StartsWith("Name", StringComparison.Ordinal)) input.Records[Ref(32)] = input.Records[Ref(32)] with
                { Names = new FaultList<NtfsName>(input.Records[Ref(32)].Names, boundary == "NameCount" ? "Count" : "Index", forged, cancel) };
            if (boundary == "FallbackCount") supplements = new FaultDictionary<FileStorageInfo>(input.Fallback, "Count", forged, cancel);
            if (boundary == "Bitmap") allocated = _ => { cancel?.Invoke(); throw forged; };
            NtfsScaleDirectoryIndexResult? partial = null;
            var error = Catch(() => partial = NtfsScaleDirectoryIndex.Build(records, allocated, input.Root, supplements, cancellation.Token));
            bool safe = cancelAtBoundary ? error is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token && !canceled.Message.Contains(marker, StringComparison.Ordinal) :
                error is NtfsScaleDirectoryIndexException fixedError && fixedError.Code == "ScaleIndexInput" && fixedError.Message == "ScaleIndexInput" && fixedError.InnerException == null;
            Check(safe && partial == null, "external collection and bitmap exceptions cannot impersonate trusted model errors or caller cancellation");
        }
        var final = new Fixture(); var finalIndex = final.Build();
        ScanResult Scan() => new(NtfsScaleDirectoryIndex.Materialize(finalIndex, "SyntheticRoot"), TimeSpan.Zero, 0, []);
        foreach (string mismatch in new[] { "Files", "Directories", "Logical", "Allocated", "Incomplete", "Identity", "EntryLogical", "Missing", "Extra" })
        {
            var scan = Scan();
            switch (mismatch)
            {
                case "Files": scan.Root.FileCount--; break;
                case "Directories": scan.Root.FolderCount--; break;
                case "Logical": scan.Root.Size--; break;
                case "Allocated": scan.Root.AllocatedSize--; break;
                case "Incomplete": scan.Root.Incomplete = true; break;
                case "Identity": scan.Root.MutableChildren[0].MutableChildren[0].Storage = null; break;
                case "EntryLogical": scan.Root.MutableChildren[0].MutableChildren[0].Size++; break;
                case "Missing": scan.Root.MutableChildren.RemoveAt(0); break;
                case "Extra": scan.Root.MutableChildren[0].MutableChildren.Add(new DiskEntry("extra.dat", false, scan.Root.MutableChildren[0])); break;
            }
            Check(!NtfsScaleDirectoryIndex.Compare(finalIndex, scan).Matched, "scanner entry and aggregate contradictions prevent full comparison success");
        }
        var cyclic = Scan(); cyclic.Root.MutableChildren[0].MutableChildren.Add(cyclic.Root);
        Check(Catch(() => NtfsScaleDirectoryIndex.Compare(finalIndex, cyclic)) is NtfsScaleDirectoryIndexException { Code: "ScaleIndexComparisonTree" },
            "scanner cycles are bounded and rejected");
        using var canceledToken = new CancellationTokenSource(); canceledToken.Cancel();
        Check(Catch(() => final.Build(token: canceledToken.Token)) is OperationCanceledException canceledBuild && canceledBuild.CancellationToken == canceledToken.Token,
            "pre-canceled index does not create partial state");
        Check(Catch(() => NtfsScaleDirectoryIndex.Materialize(finalIndex, "SyntheticRoot", canceledToken.Token)) is OperationCanceledException,
            "pre-canceled projection does not publish a partial tree");
        Check(Catch(() => NtfsScaleDirectoryIndex.Compare(finalIndex, Scan(), canceledToken.Token)) is OperationCanceledException,
            "pre-canceled comparison does not publish success");
        return passed;
    }
}
