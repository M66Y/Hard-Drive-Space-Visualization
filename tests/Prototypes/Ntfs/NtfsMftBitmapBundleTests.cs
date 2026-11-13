using System.Buffers.Binary;
using System.Collections;
using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// Pure completed-layout models and synthetic buffers. No volume, file, native
// handle or snapshot is opened; a successful plan is not source provenance.
internal static class NtfsMftBitmapBundleTests
{
    private const long BitmapLcn = 10000000, SecondLcn = 11000000;
    private static NtfsMftGeometry Geometry(long slots = 16777216) => new(512, 4096, 1024,
        64L * 1024 * 1024 * 1024, 16777216, 10, slots * 1024);
    private static byte[] Attribute(uint type, long count, long lcn, long logical)
    {
        byte[] attr = new byte[88];
        NtfsRecordFixtures.U32(attr, 0, type); NtfsRecordFixtures.U32(attr, 4, (uint)attr.Length); attr[8] = 1;
        NtfsRecordFixtures.U64(attr, 24, (ulong)(count - 1)); NtfsRecordFixtures.U16(attr, 32, 64);
        NtfsRecordFixtures.U64(attr, 40, (ulong)(count * 4096)); NtfsRecordFixtures.U64(attr, 48, (ulong)logical);
        NtfsRecordFixtures.U64(attr, 56, (ulong)logical); attr[64] = 0x88;
        BinaryPrimitives.WriteInt64LittleEndian(attr.AsSpan(65), count);
        BinaryPrimitives.WriteInt64LittleEndian(attr.AsSpan(73), lcn); return attr;
    }
    private static byte[] Seed(NtfsMftGeometry geometry, byte[]? extra = null)
    {
        long bytes = (geometry.MftValidDataLength + 4095) / 4096 * 4096;
        var attrs = new List<byte[]> { NtfsRecordFixtures.Name("$MFT"), Attribute(0x80, bytes / 4096, 10, geometry.MftValidDataLength) };
        if (extra != null) attrs.Add(extra);
        return NtfsRecordFixtures.Record(attrs);
    }
    private static NtfsMftMap Map(NtfsMftGeometry geometry) => NtfsMftMap.Parse(Seed(geometry), geometry).Map
        ?? throw new InvalidOperationException("Synthetic map");
    private static NtfsMftMetadataBundle Bundle(NtfsMftMap map, NtfsMftRun[] runs, long allocated = 2097152,
        long data = 2097152, long initialized = 2097152) => new(map, new(null, runs, data, initialized, allocated));
    private sealed class HostileSlots(int count, bool throwCount = false) : IReadOnlyList<ulong>
    {
        public int Count => throwCount ? throw new IOException("PRIVATE_INPUT") : count;
        public ulong this[int index] => throw new NtfsMftBitmapSelectionException("PRIVATE_INPUT");
        public IEnumerator<ulong> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class RepeatedSlots(int count) : IReadOnlyList<ulong>
    {
        public int Count => count;
        public ulong this[int index] => index >= 0 && index < count ? 0UL : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<ulong> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Bitmap bundle " + name); passed++; Console.WriteLine("PASS bitmap bundle " + name); }
        Exception? Caught(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
        void Reject(Action action, string code, string name)
        {
            Check(Caught(action) is NtfsMftBitmapSelectionException failure && failure.Code == code &&
                failure.InnerException == null && !failure.Message.Contains("PRIVATE"), name);
        }
        var geometry = Geometry(); var map = Map(geometry);
        NtfsMftRun[] runs = [new(0, 256, BitmapLcn), new(256, 256, SecondLcn)];
        var bundle = Bundle(map, runs);
        ulong[] selected = [0, 900000, 8388608, 16777215, 900000];
        var plan = NtfsMftBitmapSelection.Parse(bundle, selected);
        Check(plan is { PageCount: 4, RequestedBytes: 2048, Form: "Nonresident" }, "merged extents address only four selected bitmap sectors");
        var expected = new Dictionary<long, ulong[]>
        {
            [BitmapLcn * 4096] = [0],
            [BitmapLcn * 4096 + (900000 / 4096) * 512] = [900000],
            [SecondLcn * 4096] = [8388608],
            [SecondLcn * 4096 + 1048576 - 512] = [16777215]
        };
        var offsets = new List<long>();
        int Read(long offset, Memory<byte> destination, CancellationToken token)
        {
            offsets.Add(offset); destination.Span.Clear();
            foreach (ulong slot in expected[offset]) destination.Span[(int)((slot / 8) % 512)] |= (byte)(1 << (int)(slot % 8));
            return 512;
        }
        var bits = plan.Read(Read);
        Check(offsets.Distinct().Count() == 4 && selected.All(bits.IsAllocated), "merged extents preserve allocation bits and duplicate slots use one observation");
        Reject(() => bits.IsAllocated(1), "BitmapSelectionUnobserved", "unselected slot remains unknown");
        Check(!JsonSerializer.Serialize(plan).Contains("physical", StringComparison.OrdinalIgnoreCase) &&
            !JsonSerializer.Serialize(bits).Contains("allocated", StringComparison.OrdinalIgnoreCase), "plans and observations expose no raw or addressing payload");

        var smallGeometry = Geometry(8); var smallMap = Map(smallGeometry); byte[] resident = [0x85];
        var residentLayout = new NtfsMftBitmapLayout(resident, [], 1, 1, 1); resident[0] = 0;
        var residentPlan = NtfsMftBitmapSelection.Parse(new(smallMap, residentLayout), [0, 1, 2, 7]);
        int callbacks = 0; var residentBits = residentPlan.Read((_, _, _) => { callbacks++; throw new IOException("PRIVATE"); });
        Check(callbacks == 0 && residentPlan is { PageCount: 0, Form: "Resident" } && residentBits.IsAllocated(0) &&
            !residentBits.IsAllocated(1) && residentBits.IsAllocated(2) && residentBits.IsAllocated(7), "resident bundle retains private bytes and performs no native request");
        var largerGeometry = Geometry(4104); var largerMap = Map(largerGeometry); byte[] largerResident = new byte[513]; largerResident[^1] = 0x80;
        var larger = NtfsMftBitmapSelection.Parse(new(largerMap, new(largerResident, [], 513, 513, 513)), [4103]);
        Check(larger.Read((_, _, _) => throw new InvalidOperationException()).IsAllocated(4103), "bundle supports an already decoded resident payload above the old prefix size");
        byte[] largerSeed = Seed(largerGeometry, NtfsRecordFixtures.Resident(0xb0, largerResident));
        Reject(() => NtfsMftBitmapSelection.Parse(largerSeed, largerGeometry, [4103]), "BitmapSelectionCoverage", "old record-zero-only resident limit remains unchanged");

        Reject(() => NtfsMftBitmapSelection.Parse(new(smallMap, new([0x85], [new(0, 1, BitmapLcn)], 1, 1, 1)), [0]),
            "BitmapSelectionLengths", "resident payload cannot also supply physical runs");
        Reject(() => NtfsMftBitmapSelection.Parse(new(smallMap, new([0x85], [], 2, 2, 2)), [0]),
            "BitmapSelectionResidentBounds", "resident lengths bind exact payload capacity");
        Reject(() => NtfsMftBitmapSelection.Parse(new(smallMap, new(new byte[1025], [], 1025, 1025, 1025)), [0]),
            "BitmapSelectionLengths", "resident payload remains bounded by record size");
        Reject(() => NtfsMftBitmapSelection.Parse(Bundle(map, runs, initialized: 1), [0]), "BitmapSelectionLengths", "selected prefix does not excuse incomplete whole bitmap initialization");
        Reject(() => NtfsMftBitmapSelection.Parse(Bundle(map, runs, allocated: 4096), [0]), "BitmapSelectionLengths", "logical data cannot exceed allocated backing");
        Reject(() => NtfsMftBitmapSelection.Parse(Bundle(map, [new(0, 128, BitmapLcn), new(129, 384, SecondLcn)]), [0]),
            "BitmapSelectionRunCoverage", "VCN hole invalidates whole layout");
        Reject(() => NtfsMftBitmapSelection.Parse(Bundle(map, [new(0, 256, BitmapLcn), new(255, 256, SecondLcn)]), [0]),
            "BitmapSelectionRunCoverage", "VCN overlap invalidates whole layout");
        Reject(() => NtfsMftBitmapSelection.Parse(Bundle(map, [new(0, 256, BitmapLcn), new(256, 256, BitmapLcn + 128)]), [0]),
            "BitmapSelectionPhysicalOverlap", "physical bitmap extents may not overlap");
        Reject(() => NtfsMftBitmapSelection.Parse(Bundle(map, [new(0, 512, 10)]), [0]), "BitmapSelectionMftOverlap", "bitmap may not alias MFT data");
        Reject(() => NtfsMftBitmapSelection.Parse(Bundle(map, [new(0, 256, BitmapLcn), new(256, 256, geometry.TotalClusters - 1)]), [0]),
            "BitmapSelectionPhysicalBounds", "unselected tail physical bounds are checked");
        Reject(() => NtfsMftBitmapSelection.Parse(Bundle(map, [new(0, 512, 0)]), [0]), "BitmapSelectionPhysicalBounds", "sparse or zero LCN is not invented as known allocation");
        Reject(() => NtfsMftBitmapSelection.Parse(Bundle(map, [new(0, 512, long.MaxValue)]), [0]), "BitmapSelectionOverflow", "address arithmetic overflow is controlled");
        Reject(() => NtfsMftBitmapSelection.Parse(Bundle(map, []), [0]), "BitmapSelectionRunBudget", "nonresident layout requires backing runs");
        var maximumRuns = Enumerable.Range(0, 1024).Select(i => new NtfsMftRun(i, 1, BitmapLcn + i * 2)).ToArray();
        Check(NtfsMftBitmapSelection.Parse(Bundle(map, maximumRuns, 4194304), [0]).PageCount == 1, "completed bundle permits bounded cross-record run maps");
        Reject(() => NtfsMftBitmapSelection.Parse(Bundle(map, maximumRuns.Append(new(1024, 1, BitmapLcn + 2048)).ToArray(), 4198400), [0]),
            "BitmapSelectionRunBudget", "1025 bitmap runs exceed new bundle bound");

        Reject(() => NtfsMftBitmapSelection.Parse(bundle, []), "BitmapSelectionSlotBudget", "empty scope cannot produce allocation observations");
        Reject(() => NtfsMftBitmapSelection.Parse(bundle, new HostileSlots(262145)), "BitmapSelectionSlotBudget", "entry count checked before hostile access");
        Reject(() => NtfsMftBitmapSelection.Parse(bundle, new HostileSlots(1)), "BitmapSelectionInput", "hostile controlled-looking indexer text sanitized");
        Reject(() => NtfsMftBitmapSelection.Parse(bundle, new HostileSlots(1, true)), "BitmapSelectionInput", "hostile count exception sanitized");
        Reject(() => NtfsMftBitmapSelection.Parse(bundle, [16777216]), "BitmapSelectionSlotBounds", "slot past valid MFT range rejected");
        Reject(() => NtfsMftBitmapSelection.Parse(bundle, [NtfsRecordDecoder.MaximumRecordNumber + 1]), "BitmapSelectionSlotBounds", "full references cannot be used as slot offsets");
        var dispersed = Enumerable.Range(0, 513).Select(i => (ulong)i * 4096).ToArray();
        Check(NtfsMftBitmapSelection.Parse(bundle, dispersed[..512]).RequestedBytes == 262144, "exact 512 sector budget remains bounded");
        Reject(() => NtfsMftBitmapSelection.Parse(bundle, dispersed), "BitmapSelectionPageBudget", "513 selected sectors rejected before reading");
        var largePages = Enumerable.Range(0, 4096).Select(i => (ulong)i * 4096).ToArray();
        Check(NtfsMftBitmapSelection.Parse(bundle, largePages, limits: NtfsScanLimits.Large).RequestedBytes == 2097152,
            "explicit large preset permits exactly 4096 bitmap sectors");
        Check(NtfsMftBitmapSelection.Parse(bundle, new RepeatedSlots(1048576), limits: NtfsScanLimits.Large).PageCount == 1,
            "large entry budget remains bounded while duplicate slots retain one observation");
        Reject(() => NtfsMftBitmapSelection.Parse(bundle, new HostileSlots(1048577), limits: NtfsScanLimits.Large),
            "BitmapSelectionSlotBudget", "large preset rejects one extra entry before hostile access");
        var largerMft = Map(Geometry(33554432));
        var largerBitmap = Bundle(largerMft, [new(0, 1024, BitmapLcn)], 4194304, 4194304, 4194304);
        Reject(() => NtfsMftBitmapSelection.Parse(largerBitmap, Enumerable.Range(0, 4097).Select(i => (ulong)i * 4096).ToArray(), limits: NtfsScanLimits.Large),
            "BitmapSelectionPageBudget", "large preset does not permit a 4097th sector");
        Reject(() => plan.Read((_, _, _) => 511), "BitmapSelectionShortRead", "short reads return no partial allocation result");
        Reject(() => plan.Read((_, _, _) => throw new NtfsMftBitmapSelectionException("PRIVATE")), "BitmapSelectionRead", "external reader exception is never forwarded");
        Memory<byte> retained = default;
        Reject(() => plan.Read((_, memory, _) => { retained = memory; memory.Span.Fill(0x5a); throw new IOException("PRIVATE"); }),
            "BitmapSelectionRead", "failure does not publish allocation observations");
        Check(retained.Length == 512 && retained.ToArray().All(value => value == 0), "scratch bitmap sector is cleared on failed reads");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); Check(Caught(() => NtfsMftBitmapSelection.Parse(bundle, [0], cancelled.Token)) is OperationCanceledException,
                "cancelled planning publishes no plan");
            Check(Caught(() => plan.Read(Read, cancelled.Token)) is OperationCanceledException, "cancelled reading publishes no result");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            int calls = 0;
            Check(Caught(() => plan.Read((_, _, _) => { calls++; cancelled.Cancel(); throw new IOException("PRIVATE"); }, cancelled.Token)) is OperationCanceledException && calls == 1,
                "real cancellation takes priority and prevents later reads");
        }
        return passed;
    }
}
