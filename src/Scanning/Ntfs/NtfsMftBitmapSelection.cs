using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace DiskAtlas.NtfsPrototype;

internal sealed class NtfsMftBitmapSelectionException(string code) : IOException(code)
{ internal string Code { get; } = code; }

internal sealed class NtfsMftBitmapSelectionResult(IReadOnlyDictionary<ulong, bool> values)
{
    private readonly IReadOnlyDictionary<ulong, bool> allocated = values;
    // No observation is invented for an unselected slot.
    internal bool IsAllocated(ulong slot) => allocated.TryGetValue(slot, out bool value) ? value :
        throw new NtfsMftBitmapSelectionException("BitmapSelectionUnobserved");
}

// Private addressing/byte plans remain pure data, never proof of a snapshot.
// Actual provenance and cumulative request accounting belong to its source owner.
internal sealed class NtfsMftBitmapSelectionPlan
{
    private readonly ulong[] slots;
    private readonly Dictionary<long, long> physicalPages;
    private readonly byte[]? resident;
    internal int PageCount => physicalPages.Count;
    internal int RequestedBytes => checked(PageCount * 512);
    internal string Form => resident == null ? "Nonresident" : "Resident";
    internal NtfsMftBitmapSelectionPlan(ulong[] selected, Dictionary<long, long> pages, byte[]? bytes)
    { slots = selected.ToArray(); physicalPages = new(pages); resident = bytes?.ToArray(); }

    internal NtfsMftBitmapSelectionResult Read(Func<long, Memory<byte>, CancellationToken, int> read,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (read == null) throw new NtfsMftBitmapSelectionException("BitmapSelectionReader");
        var values = new Dictionary<ulong, bool>(slots.Length);
        if (resident != null)
        {
            foreach (ulong slot in slots)
            {
                token.ThrowIfCancellationRequested();
                values.Add(slot, (resident[checked((int)(slot / 8))] & (1 << (int)(slot % 8))) != 0);
            }
        }
        else
        {
            byte[] page = new byte[512];
            foreach (var group in slots.GroupBy(slot => checked((long)(slot / 4096))).OrderBy(group => group.Key))
            {
                token.ThrowIfCancellationRequested(); page.AsSpan().Clear(); int received;
                try { received = read(physicalPages[group.Key], page, token); }
                catch (Exception) { token.ThrowIfCancellationRequested(); throw new NtfsMftBitmapSelectionException("BitmapSelectionRead"); }
                token.ThrowIfCancellationRequested();
                if (received != page.Length) throw new NtfsMftBitmapSelectionException("BitmapSelectionShortRead");
                foreach (ulong slot in group)
                {
                    token.ThrowIfCancellationRequested(); int index = (int)((slot / 8) % 512);
                    values.Add(slot, (page[index] & (1 << (int)(slot % 8))) != 0);
                }
            }
            Array.Clear(page);
        }
        token.ThrowIfCancellationRequested();
        return new(new ReadOnlyDictionary<ulong, bool>(values));
    }
}

internal static class NtfsMftBitmapSelection
{
    internal const int MaximumSlots = 262144, MaximumPages = 512, MaximumRuns = 128;
    private static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
    private static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
    private static ulong U64(byte[] b, int p) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p));
    private static long I64(byte[] b, int p) => BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(p));
    private static bool Fits(int p, long n, int end) => p >= 0 && n >= 0 && p <= end && n <= end - p;
    private static void Require([DoesNotReturnIf(false)] bool condition, string code)
    { if (!condition) throw new NtfsMftBitmapSelectionException(code); }

    internal static NtfsMftBitmapSelectionPlan Parse(ReadOnlySpan<byte> rawRecord0, NtfsMftGeometry geometry,
        IReadOnlyList<ulong> selectedSlots, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Require(geometry is { SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024 } && rawRecord0.Length == 1024,
            "BitmapSelectionGeometry");
        Require(selectedSlots != null, "BitmapSelectionInput");
        int count;
        try { count = selectedSlots.Count; }
        catch (Exception) { token.ThrowIfCancellationRequested(); throw new NtfsMftBitmapSelectionException("BitmapSelectionInput"); }
        Require(count is >= 1 and <= MaximumSlots, "BitmapSelectionSlotBudget");
        var unique = new HashSet<ulong>();
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested(); ulong slot;
            try { slot = selectedSlots[i]; }
            catch (Exception) { token.ThrowIfCancellationRequested(); throw new NtfsMftBitmapSelectionException("BitmapSelectionInput"); }
            token.ThrowIfCancellationRequested();
            Require(slot <= NtfsRecordDecoder.MaximumRecordNumber && geometry.MftValidDataLength >= 1024 &&
                slot < (ulong)(geometry.MftValidDataLength / 1024), "BitmapSelectionSlotBounds");
            unique.Add(slot);
        }
        var slots = unique.Order().ToArray();
        var selectedPages = slots.Select(slot => checked((long)(slot / 4096))).Distinct().ToArray();
        Require(selectedPages.Length <= MaximumPages, "BitmapSelectionPageBudget");
        byte[] owned = rawRecord0.ToArray();
        var seed = NtfsMftMap.Parse(owned, geometry, token);
        Require(seed.Status == "Mapped" && seed.Map != null, "BitmapSelectionSeed");
        try
        {
            int usa = U16(owned, 4), usaCount = U16(owned, 6);
            for (int i = 1; i < usaCount; i++)
            { token.ThrowIfCancellationRequested(); BinaryPrimitives.WriteUInt16LittleEndian(owned.AsSpan(i * 512 - 2), U16(owned, usa + i * 2)); }
            int used = checked((int)U32(owned, 24)), found = -1, length = 0, matches = 0;
            bool ended = false;
            for (int p = U16(owned, 20), attributes = 0; p < used;)
            {
                token.ThrowIfCancellationRequested(); Require(Fits(p, 4, used), "BitmapSelectionAttribute");
                uint type = U32(owned, p); if (type == uint.MaxValue) { ended = true; break; }
                Require(++attributes <= 64 && Fits(p, 24, used), "BitmapSelectionAttribute");
                uint bytes = U32(owned, p + 4);
                Require(bytes >= 24 && bytes % 8 == 0 && Fits(p, bytes, used), "BitmapSelectionAttribute");
                Require(type != 0x20, "BitmapSelectionExtent");
                if (type == 0xb0) { found = p; length = (int)bytes; matches++; }
                p = checked(p + (int)bytes);
            }
            Require(ended && matches == 1, "BitmapSelectionAttribute");
            Require(owned[found + 9] == 0 && U16(owned, found + 12) == 0, "BitmapSelectionFlags");
            long fullBytes = checked((geometry.MftValidDataLength / 1024 + 7) / 8);
            if (owned[found + 8] == 0)
            {
                uint bytes = U32(owned, found + 16); int offset = U16(owned, found + 20);
                Require(offset >= 24 && Fits(offset, bytes, length), "BitmapSelectionResidentBounds");
                Require(bytes <= 512 && bytes >= fullBytes, "BitmapSelectionCoverage");
                token.ThrowIfCancellationRequested();
                return new(slots, [], owned.AsSpan(found + offset, (int)bytes).ToArray());
            }
            Require(owned[found + 8] == 1 && length >= 64, "BitmapSelectionAttribute");
            Require(U16(owned, found + 34) == 0 && U64(owned, found + 16) == 0, "BitmapSelectionExtent");
            ulong high = U64(owned, found + 24);
            long allocated = I64(owned, found + 40), logical = I64(owned, found + 48), initialized = I64(owned, found + 56);
            Require(allocated > 0 && allocated % 4096 == 0 && allocated <= geometry.VolumeBytes &&
                logical >= fullBytes && initialized >= fullBytes && initialized <= logical && logical <= allocated,
                "BitmapSelectionLengths");
            Require(high < long.MaxValue / 4096 && checked(((long)high + 1) * 4096) == allocated, "BitmapSelectionVcn");
            int mapping = U16(owned, found + 32), end = checked(found + length);
            Require(mapping >= 64 && mapping < length, "BitmapSelectionMappingBounds");
            var runs = new List<NtfsMftRun>(); long vcn = 0, lcn = 0; bool terminated = false;
            for (int p = checked(found + mapping); p < end;)
            {
                token.ThrowIfCancellationRequested(); byte header = owned[p++];
                if (header == 0) { terminated = true; break; }
                Require(runs.Count < MaximumRuns, "BitmapSelectionRunBudget");
                int lengths = header & 15, offsets = header >> 4;
                Require(lengths is >= 1 and <= 8 && offsets is >= 1 and <= 8 && Fits(p, lengths + offsets, end), "BitmapSelectionRunEncoding");
                ulong rawCount = 0, delta = 0;
                for (int i = 0; i < lengths; i++) rawCount |= (ulong)owned[p++] << (i * 8);
                for (int i = 0; i < offsets; i++) delta |= (ulong)owned[p++] << (i * 8);
                Require(rawCount is > 0 and <= long.MaxValue, "BitmapSelectionRunLength");
                if (offsets < 8 && (delta & (1UL << (offsets * 8 - 1))) != 0) delta |= ulong.MaxValue << (offsets * 8);
                lcn = checked(lcn + unchecked((long)delta)); long lastLcn = checked(lcn + (long)rawCount), lastVcn = checked(vcn + (long)rawCount);
                Require(lcn > 0 && lastLcn <= geometry.TotalClusters && checked(lastLcn * 4096) <= geometry.VolumeBytes,
                    "BitmapSelectionPhysicalBounds");
                Require(lastVcn <= (long)high + 1, "BitmapSelectionRunCoverage");
                foreach (var previous in runs)
                    Require(lcn >= previous.FirstLcn + previous.ClusterCount || previous.FirstLcn >= lastLcn, "BitmapSelectionPhysicalOverlap");
                foreach (var data in seed.Map.Runs)
                    Require(lcn >= data.FirstLcn + data.ClusterCount || data.FirstLcn >= lastLcn, "BitmapSelectionMftOverlap");
                runs.Add(new(vcn, (long)rawCount, lcn)); vcn = lastVcn;
            }
            Require(terminated && runs.Count > 0 && vcn == (long)high + 1 && checked(vcn * 4096) == allocated,
                "BitmapSelectionRunCoverage");
            var pages = new Dictionary<long, long>();
            foreach (long page in selectedPages)
            {
                token.ThrowIfCancellationRequested(); long offset = checked(page * 512);
                Require(offset >= 0 && offset <= allocated - 512, "BitmapSelectionPageBounds");
                var run = runs.FirstOrDefault(item => offset >= checked(item.FirstVcn * 4096) &&
                    offset < checked((item.FirstVcn + item.ClusterCount) * 4096));
                Require(run != null, "BitmapSelectionRunCoverage");
                long physical = checked(run.FirstLcn * 4096 + offset - run.FirstVcn * 4096);
                Require(physical % 512 == 0 && physical >= 0 && physical <= geometry.VolumeBytes - 512, "BitmapSelectionPageBounds");
                pages.Add(page, physical);
            }
            token.ThrowIfCancellationRequested(); return new(slots, pages, null);
        }
        catch (OverflowException) { throw new NtfsMftBitmapSelectionException("BitmapSelectionOverflow"); }
    }
}
