using System.Buffers.Binary;
using System.Collections;
using System.Text;
using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// High-numbered slots and address callbacks only. Never opens a disk, folder,
// VSS owner or handle; passing these checks grants no Native provenance.
internal static class NtfsPersonalIndexTests
{
    private const long BitmapLcn = 10000000;
    private static readonly DateTime Modified = new(2020, 2, 3, 4, 5, 6);
    private static FileIdentity Identity(ulong slot) => new(71, (3UL << 48) | slot, 0);
    private static NtfsMftGeometry Geometry(long slots = 16777216) => new(512, 4096, 1024,
        64L * 1024 * 1024 * 1024, 16777216, 10, slots * 1024);
    private static byte[] Run(long count, long delta)
    {
        var bytes = new byte[17]; bytes[0] = 0x88;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(1), count);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(9), delta); return bytes;
    }
    private static byte[] Attribute(uint type, byte[] runs, long bytes, long logical)
    {
        var attr = new byte[NtfsRecordFixtures.Align(64 + runs.Length)];
        NtfsRecordFixtures.U32(attr, 0, type); NtfsRecordFixtures.U32(attr, 4, (uint)attr.Length); attr[8] = 1;
        NtfsRecordFixtures.U64(attr, 24, (ulong)(bytes / 4096 - 1)); NtfsRecordFixtures.U16(attr, 32, 64);
        NtfsRecordFixtures.U64(attr, 40, (ulong)bytes); NtfsRecordFixtures.U64(attr, 48, (ulong)logical);
        NtfsRecordFixtures.U64(attr, 56, (ulong)logical); runs.CopyTo(attr, 64); return attr;
    }
    private static byte[] Seed(NtfsMftGeometry geometry, byte[]? bitmap = null)
    {
        long mft = (geometry.MftValidDataLength + 4095) / 4096 * 4096;
        long bitmapLogical = (geometry.MftValidDataLength / 1024 + 7) / 8;
        long bitmapAllocated = (bitmapLogical + 4095) / 4096 * 4096;
        return NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("$MFT"),
            Attribute(0x80, Run(mft / 4096, 10).Append((byte)0).ToArray(), mft, geometry.MftValidDataLength),
            bitmap ?? Attribute(0xb0, Run(bitmapAllocated / 4096, BitmapLcn).Append((byte)0).ToArray(), bitmapAllocated, bitmapLogical)]);
    }
    private sealed class HostileList : IReadOnlyList<ulong>
    {
        public int Count => 1;
        public ulong this[int i] => throw new NtfsMftBitmapSelectionException("secret-name");
        public IEnumerator<ulong> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Personal index check failed: " + name); passed++; Console.WriteLine("PASS personal index " + name); }
        Exception? Catch(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
        void Reject(Action action, string code, string name) => Check(Catch(action) is NtfsMftBitmapSelectionException error && error.Code == code, name);

        var geometry = Geometry(); byte[] raw = Seed(geometry); byte[] before = raw.ToArray();
        ulong[] selected = [900000, 900001, 1048575, 1048576, 900001];
        var plan = NtfsMftBitmapSelection.Parse(raw, geometry, selected);
        Check(plan is { PageCount: 3, RequestedBytes: 1536, Form: "Nonresident" },
            "large MFT reads only three selected bitmap sectors instead of its entire multi-megabyte bitmap");
        int reads = 0; var offsets = new List<long>();
        int Read(long physical, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); reads++; offsets.Add(physical); destination.Span.Clear();
            long relative = physical - BitmapLcn * 4096;
            foreach (ulong slot in selected.Distinct())
            {
                long index = (long)(slot / 8) - relative;
                if (index >= 0 && index < destination.Length && slot != 1048575)
                    destination.Span[(int)index] |= (byte)(1 << (int)(slot % 8));
            }
            return destination.Length;
        }
        var observed = plan.Read(Read);
        Check(reads == 3 && observed.IsAllocated(900000) && observed.IsAllocated(900001) &&
            !observed.IsAllocated(1048575) && observed.IsAllocated(1048576),
            "selected bits preserve true and false across sector boundaries and duplicate slots share one observation");
        Check(offsets.Distinct().Count() == 3 && offsets.All(offset => offset % 512 == 0) && raw.SequenceEqual(before),
            "aligned reads are unique and strict parsing preserves caller bytes");
        Reject(() => observed.IsAllocated(12), "BitmapSelectionUnobserved", "unrequested slot never becomes implicitly free");
        Check(JsonSerializer.Serialize(plan) == "{}" && JsonSerializer.Serialize(observed) == "{}",
            "byte plans and observations do not serialize addresses identifiers or raw data");
        selected[0] = 12; raw.AsSpan().Clear(); reads = 0;
        Check(plan.Read(Read).IsAllocated(900001) && reads == 3, "plan retains private immutable slots and mapping after input mutation");
        raw = before.ToArray();
        Reject(() => NtfsMftBitmapSelection.Parse(raw, geometry, new HostileList()), "BitmapSelectionInput",
            "untrusted indexer exception of a controlled-looking type cannot leak its arbitrary message");
        Reject(() => NtfsMftBitmapSelection.Parse(raw, geometry, []), "BitmapSelectionSlotBudget", "empty selection cannot mint an allocation observation");
        Reject(() => NtfsMftBitmapSelection.Parse(raw, geometry, [(ulong)(geometry.MftValidDataLength / 1024)]),
            "BitmapSelectionSlotBounds", "slot at valid-data end is rejected before reads");
        Reject(() => NtfsMftBitmapSelection.Parse(raw, geometry, [NtfsRecordDecoder.MaximumRecordNumber + 1]),
            "BitmapSelectionSlotBounds", "a full sequence reference cannot masquerade as a slot");
        var dispersed = Enumerable.Range(0, 513).Select(i => (ulong)i * 4096).ToArray();
        Reject(() => NtfsMftBitmapSelection.Parse(raw, geometry, dispersed), "BitmapSelectionPageBudget",
            "513 sparse bitmap pages exceed bounded request budget before any I O");
        var maximum = NtfsMftBitmapSelection.Parse(raw, geometry, dispersed.Take(512).ToArray());
        Check(maximum is { PageCount: 512, RequestedBytes: 262144 }, "512 selected bitmap pages fit exact 256 KiB sub-budget");
        Reject(() => plan.Read((_, _, _) => 511), "BitmapSelectionShortRead", "short raw read returns no partial bitmap result");
        Reject(() => plan.Read((_, _, _) => throw new IOException("private path")), "BitmapSelectionRead", "raw callback failures are sanitized");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); Check(Catch(() => NtfsMftBitmapSelection.Parse(raw, geometry, [900000], cancellation.Token)) is OperationCanceledException,
                "pre-cancelled planning returns no plan");
            Check(Catch(() => plan.Read(Read, cancellation.Token)) is OperationCanceledException, "pre-cancelled reading returns no partial observation");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            int calls = 0;
            Check(Catch(() => plan.Read((_, _, token) => { calls++; cancellation.Cancel(); throw new IOException("private"); }, cancellation.Token)) is OperationCanceledException && calls == 1,
                "real cancellation takes priority over callback errors and halts remaining pages");
        }
        var broken = raw.ToArray(); broken[510] ^= 1;
        Reject(() => NtfsMftBitmapSelection.Parse(broken, geometry, [900000]), "BitmapSelectionSeed", "invalid strict USA seed never permits bitmap reads");
        var overlap = Seed(geometry, Attribute(0xb0, Run(512, 10).Append((byte)0).ToArray(), 2097152, 2097152));
        Reject(() => NtfsMftBitmapSelection.Parse(overlap, geometry, [900000]), "BitmapSelectionMftOverlap", "bitmap mapping may not alias MFT data extents");
        var outside = Seed(geometry, Attribute(0xb0, Run(512, 16777215).Append((byte)0).ToArray(), 2097152, 2097152));
        Reject(() => NtfsMftBitmapSelection.Parse(outside, geometry, [900000]), "BitmapSelectionPhysicalBounds", "unrequested out-of-volume tail invalidates entire run map");

        // One high-numbered MFT page is reconciled against the full scoped namespace.
        var rootId = Identity(900000); var fileId = Identity(900001);
        var entries = new[] { new NtfsSnapshotNamespaceEntry(0, -1, "", rootId, FileAttributes.Directory, false) { Modified = Modified },
            new NtfsSnapshotNamespaceEntry(1, 0, "demo.bin", fileId, FileAttributes.Archive, false) { Modified = Modified } };
        var scope = new NtfsSnapshotNamespaceResult(new("snapshot-namespace-v1", 2, 1, 0, 1, 0, 1, 1, 2, 8, 1, true), entries, ["", "demo.bin"], rootId);
        var mft = NtfsMftMap.Parse(raw, geometry).Map!; int pageReads = 0;
        int MftRead(long offset, Memory<byte> destination, CancellationToken token)
        {
            pageReads++; destination.Span.Clear();
            byte[] rootRecord = NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("scope"), NtfsRecordFixtures.Data(0)], directory: true);
            byte[] fileRecord = NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("demo.bin", parent: rootId.Low), NtfsRecordFixtures.Nonresident()]);
            rootRecord.CopyTo(destination.Span[(int)(900000L * 1024 - offset)..]);
            fileRecord.CopyTo(destination.Span[(int)(900001L * 1024 - offset)..]); return destination.Length;
        }
        var index = NtfsSnapshotSizeIndex.BuildAsync(scope, mft, MftRead, _ => true,
            (_, _) => throw new InvalidOperationException("UnexpectedNative")).GetAwaiter().GetResult();
        var tree = NtfsSnapshotSizeIndex.Materialize(index, "T:\\Synthetic", requireModified: true);
        Check(pageReads == 1 && index.Report is { RawPagesRead: 1, NativeCalls: 0, LogicalBytes: 6000, AllocatedBytes: 8192, Complete: true },
            "high selected slot in a sixteen-million-slot MFT does not require scanning preceding records");
        Check(tree.Modified == Modified && tree.Children.Single().Modified == Modified && tree.Children.Single().Storage?.Modified == Modified,
            "projection preserves observed modification times instead of year-one defaults");
        var aliasEntries = new[] { entries[0], entries[1],
            new NtfsSnapshotNamespaceEntry(2, 0, "alias.bin", fileId, FileAttributes.Archive, false) { Modified = Modified.AddHours(1) } };
        var aliasScope = new NtfsSnapshotNamespaceResult(new("snapshot-namespace-v1", 3, 2, 0, 1, 0, 1, 1, 2, 17, 1, true),
            aliasEntries, ["", "demo.bin", "alias.bin"], rootId);
        var actualTime = Modified.AddYears(1); byte[] standard = new byte[48];
        NtfsRecordFixtures.U64(standard, 8, (ulong)actualTime.ToUniversalTime().ToFileTimeUtc());
        var aliasRecord = NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("demo.bin", parent: rootId.Low),
            NtfsRecordFixtures.Name("alias.bin", parent: rootId.Low), NtfsRecordFixtures.Nonresident(),
            NtfsRecordFixtures.Resident(0x10, standard)], links: 2, addStandard: false);
        int AliasRead(long offset, Memory<byte> destination, CancellationToken token)
        { MftRead(offset, destination, token); aliasRecord.CopyTo(destination.Span[(int)(900001L * 1024 - offset)..]); return destination.Length; }
        var aliasIndex = NtfsSnapshotSizeIndex.BuildAsync(aliasScope, mft, AliasRead, _ => true,
            (_, _) => throw new InvalidOperationException("UnexpectedNative")).GetAwaiter().GetResult();
        var aliasTree = NtfsSnapshotSizeIndex.Materialize(aliasIndex, "T:\\Synthetic", requireModified: true);
        Check(aliasIndex.Report.NativeCalls == 0 && aliasTree.Size == 12000 && aliasTree.AllocatedSize == 8192 &&
            aliasTree.Children.All(child => child.Modified == actualTime && child.Storage?.Modified == actualTime),
            "strict standard-information time gives every hard-link alias one object time despite stale directory-entry copies");
        var missingTime = new NtfsSnapshotNamespaceResult(scope.Report, entries.Select(entry => entry with { Modified = null }).ToArray(), ["", "demo.bin"], rootId);
        var missingIndex = NtfsSnapshotSizeIndex.BuildAsync(missingTime, mft, MftRead, _ => true, (_, _) => throw new InvalidOperationException()).GetAwaiter().GetResult();
        Check(Catch(() => NtfsSnapshotSizeIndex.Materialize(missingIndex, "T:\\Synthetic", requireModified: true)) is NtfsSnapshotSizeException { Code: "SnapshotSizeModifiedUnknown" },
            "production projection requires observed times and returns no incomplete year-one display tree");

        byte[] nativePage = new byte[65536]; byte[] name = Encoding.Unicode.GetBytes("high.bin");
        NtfsRecordFixtures.U64(nativePage, 16, (ulong)Modified.ToUniversalTime().ToFileTimeUtc());
        NtfsRecordFixtures.U32(nativePage, 56, (uint)FileAttributes.Archive); NtfsRecordFixtures.U32(nativePage, 60, (uint)name.Length);
        NtfsRecordFixtures.U64(nativePage, 72, fileId.Low); name.CopyTo(nativePage, 88);
        var parsed = NtfsSnapshotNamespace.ParsePage(nativePage).Single();
        Check(parsed.Low == fileId.Low && parsed.Modified == Modified, "namespace accepts high full IDs and reads last-write time from the correct field");
        NtfsRecordFixtures.U64(nativePage, 16, (ulong)long.MaxValue);
        Check(Catch(() => NtfsSnapshotNamespace.ParsePage(nativePage)) is NtfsSnapshotNamespaceException { Code: "NamespaceModifiedTime" },
            "out-of-range time is a controlled failure rather than an exception containing data");
        var broadEntries = new List<NtfsSnapshotNamespaceEntry> { entries[0] };
        for (int i = 0; i < 4097; i++) broadEntries.Add(new(i + 1, 0, $"f{i}", Identity((ulong)i * 64), FileAttributes.Archive, false));
        int names = broadEntries.Sum(entry => entry.Name.Length); int attemptedReads = 0;
        var broadScope = new NtfsSnapshotNamespaceResult(new("snapshot-namespace-v1", broadEntries.Count, 4097, 0, 4097, 0,
            1, 1, 2, names, 1, true), broadEntries, broadEntries.Select(entry => entry.Name).ToArray(), rootId);
        Check(Catch(() => NtfsSnapshotSizeIndex.BuildAsync(broadScope, mft,
            (_, _, _) => { attemptedReads++; return 0; }, _ => true, (_, _) => throw new InvalidOperationException()).GetAwaiter().GetResult())
                is NtfsSnapshotSizeException { Code: "SnapshotSizeRawBudget" } && attemptedReads == 0,
            "dispersed selected records cannot exceed 4096 raw pages even when the namespace entry count is small");
        return passed;
    }
}
