using System.Buffers.Binary;
using System.Text;

namespace DiskAtlas.NtfsPrototype;

// Immutable addressing metadata, not proof of a live volume or snapshot.
internal sealed class NtfsMftBitmapLayout
{
    private readonly byte[]? resident;
    internal bool Resident => resident != null;
    internal IReadOnlyList<NtfsMftRun> Runs { get; }
    internal long DataBytes { get; }
    internal long InitializedBytes { get; }
    internal long AllocatedBytes { get; }
    internal NtfsMftBitmapLayout(byte[]? resident, IEnumerable<NtfsMftRun> runs,
        long dataBytes, long initializedBytes, long allocatedBytes)
    {
        this.resident = resident?.ToArray();
        Runs = Array.AsReadOnly(runs.ToArray());
        DataBytes = dataBytes; InitializedBytes = initializedBytes; AllocatedBytes = allocatedBytes;
    }
    internal byte[] CopyResidentData() => resident?.ToArray() ?? [];
}

internal sealed record NtfsMftMetadataBundle(NtfsMftMap Map, NtfsMftBitmapLayout Bitmap);
internal sealed record NtfsMftMetadataResult(string Status, string Code, NtfsMftMetadataBundle? Metadata);

// Bounded bootstrap resolver. Reads only addresses established by already
// validated mapping pairs; an unreachable extension is never guessed by slot.
internal static class NtfsMftMetadataResolver
{
    internal const int MaximumAttributeListBytes = 65536;
    internal const int MaximumExtensionRecords = 64;
    internal const int MaximumRuns = 1024;
    private static readonly UnicodeEncoding Unicode = new(false, false, true);
    private sealed class Failure(string status, string code) : Exception
    { internal readonly string Status = status; internal readonly string Code = code; }
    private static void Invalid(string code) => throw new Failure("Invalid", code);
    private static void Unsupported(string code) => throw new Failure("Unsupported", code);
    private static void Limit(string code) => throw new Failure("LimitExceeded", code);
    private static ushort U16(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
    private static uint U32(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b[p..]);
    private static ulong U64(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt64LittleEndian(b[p..]);
    private static long I64(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadInt64LittleEndian(b[p..]);
    private static bool Fits(int p, long n, int size) => p >= 0 && n >= 0 && p <= size && n <= size - p;

    private sealed record Attribute(uint Type, ushort Instance, string Name, byte[]? Value,
        long Low, long High, IReadOnlyList<NtfsMftRun> Runs, long Data, long Initialized, long Allocated);
    private sealed record Record(ulong Reference, IReadOnlyList<NtfsName> Names, IReadOnlyList<Attribute> Attributes);
    private sealed record Entry(uint Type, ushort Instance, string Name, long Low, ulong Reference);

    internal static NtfsMftMetadataResult Resolve(ReadOnlySpan<byte> rawRecord0, NtfsMftGeometry geometry,
        Func<long, Memory<byte>, CancellationToken, int> readPhysical, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (readPhysical == null) return new("Invalid", "MftReaderMissing", null);
        try
        {
            // The safe factory supplies the common geometry validation without
            // treating its deliberately incomplete placeholder as a usable map.
            if (geometry == null) return new("Invalid", "MftGeometry", null);
            var gate = NtfsMftMap.FromResolvedLayout(geometry, [], 0, 0, 0, token);
            if (gate.Code is "MftGeometry" or "MftFixupStride") return new(gate.Status, gate.Code, null);
            if (rawRecord0.Length != geometry.RecordBytes) Invalid("MftRecordLength");
            return new Worker(geometry, readPhysical, token).Run(rawRecord0);
        }
        catch (Failure f) { token.ThrowIfCancellationRequested(); return new(f.Status, f.Code, null); }
        catch (OverflowException) { token.ThrowIfCancellationRequested(); return new("Invalid", "MftArithmeticOverflow", null); }
        catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); return new("ReadFailed", "MftReadFailed", null); }
        catch (Exception) { token.ThrowIfCancellationRequested(); return new("Invalid", "MftMetadataFailure", null); }
    }

    private sealed class Worker(NtfsMftGeometry geometry,
        Func<long, Memory<byte>, CancellationToken, int> readPhysical, CancellationToken token)
    {
        private ulong rootReference;
        private readonly List<byte[]> transient = [];

        internal NtfsMftMetadataResult Run(ReadOnlySpan<byte> raw)
        {
            try
            {
                Record root = ParseRecord(raw, 0, null);
                rootReference = root.Reference;
                var bootstrapRuns = ValidateKnownData(root.Attributes.Where(a => a.Type == 0x80 && a.Name.Length == 0).ToList());
                var records = new Dictionary<ulong, Record> { [rootReference] = root };
                Attribute[] lists = root.Attributes.Where(a => a.Type == 0x20).ToArray();
                if (lists.Length > 1) Unsupported("MftAttributeListContinuation");
                List<Entry>? entries = null;
                IReadOnlyList<NtfsMftRun> listRuns = [];
                if (lists.Length == 1)
                {
                    Attribute list = lists[0];
                    if (list.Name.Length != 0 || list.Low != 0) Unsupported("MftAttributeListForm");
                    byte[] value;
                    if (list.Value != null) value = list.Value;
                    else
                    {
                        if (list.Data is <= 0 or > MaximumAttributeListBytes) Limit("MftAttributeListByteLimit");
                        ValidateStream([list], false, false);
                        listRuns = list.Runs;
                        NoOverlap(bootstrapRuns, listRuns, "MftListPhysicalOverlap");
                        value = ReadStream(listRuns, 0, checked((int)list.Data));
                    }
                    if (value.Length is <= 0 or > MaximumAttributeListBytes) Limit("MftAttributeListByteLimit");
                    entries = ParseEntries(value);
                }
                if (entries != null)
                {
                    // Names only need extra reads if record zero did not itself
                    // establish the MFT name. All stream extents are mandatory.
                    bool needName = !MftName(root);
                    Entry[] relevant = entries.Where(e => e.Name.Length == 0 &&
                        (e.Type is 0x80 or 0xb0 || needName && e.Type == 0x30)).ToArray();
                    ulong[] references = relevant.Select(e => e.Reference).Distinct().ToArray();
                    if (references.Count(r => r != rootReference) > MaximumExtensionRecords) Limit("MftExtensionLimit");
                    while (references.Any(r => !records.ContainsKey(r)))
                    {
                        token.ThrowIfCancellationRequested();
                        var known = Collect(records, relevant, 0x80, false);
                        var runs = ValidateKnownData(known);
                        bool progress = false;
                        foreach (ulong reference in references)
                        {
                            if (records.ContainsKey(reference)) continue;
                            long offset = checked((long)(reference & NtfsRecordDecoder.MaximumRecordNumber) * geometry.RecordBytes);
                            var plan = TryPlan(runs, offset, geometry.RecordBytes);
                            if (plan == null) continue;
                            byte[] bytes = ReadPlan(plan, geometry.RecordBytes);
                            Record extension = ParseRecord(bytes, reference & NtfsRecordDecoder.MaximumRecordNumber, reference);
                            if (extension.Attributes.Any(a => a.Type == 0x20)) Unsupported("MftAttributeListContinuation");
                            records.Add(reference, extension); progress = true;
                        }
                        if (!progress) Unsupported("MftExtensionUnreachable");
                    }
                    foreach (Entry entry in relevant)
                    {
                        token.ThrowIfCancellationRequested();
                        Record record = records[entry.Reference];
                        if (record.Attributes.Count(a => Matches(entry, record, a)) != 1)
                            Invalid("MftListAttributeMismatch");
                    }
                    if (needName)
                    {
                        var names = Collect(records, relevant, 0x30, true);
                        if (!names.Any(a => a.Value != null && a.Value.Length >= 66 && a.Value[65] != 2 &&
                            string.Equals(Unicode.GetString(a.Value.AsSpan(66)), "$MFT", StringComparison.OrdinalIgnoreCase)))
                            Invalid("MftName");
                    }
                    // Every target attribute in records used for the stream must
                    // have a matching list entry. No unlisted extent is trusted.
                    foreach (Record record in records.Values)
                        foreach (Attribute attribute in record.Attributes.Where(a => a.Type is 0x80 or 0xb0 && a.Name.Length == 0))
                            if (!relevant.Any(e => Matches(e, record, attribute))) Invalid("MftAttributeListCoverage");
                }
                List<Attribute> data = entries == null ? root.Attributes.Where(a => a.Type == 0x80 && a.Name.Length == 0).ToList() :
                    Collect(records, entries, 0x80, true);
                List<Attribute> bitmap = entries == null ? root.Attributes.Where(a => a.Type == 0xb0 && a.Name.Length == 0).ToList() :
                    Collect(records, entries, 0xb0, true);
                if (!records.Values.Any(MftName)) Invalid("MftName");
                if (data.Count == 0) Unsupported("MftMissingData");
                if (bitmap.Count == 0) Unsupported("MftMissingBitmap");
                var dataRuns = ValidateStream(data, true, false);
                Attribute first = data.Single(a => a.Low == 0);
                var mapResult = NtfsMftMap.FromResolvedLayout(geometry, dataRuns, first.Data,
                    first.Initialized, first.Allocated, token);
                if (mapResult.Map == null) throw new Failure(mapResult.Status, mapResult.Code);
                var bitmapRuns = ValidateStream(bitmap, false, true);
                NoOverlap(dataRuns, bitmapRuns, "MftBitmapPhysicalOverlap");
                NoOverlap(dataRuns, listRuns, "MftListPhysicalOverlap");
                NoOverlap(bitmapRuns, listRuns, "MftListPhysicalOverlap");
                Attribute bitFirst = bitmap.Single(a => a.Low == 0);
                var layout = new NtfsMftBitmapLayout(bitFirst.Value, bitmapRuns,
                    bitFirst.Data, bitFirst.Initialized, bitFirst.Allocated);
                token.ThrowIfCancellationRequested();
                return new("Resolved", "Resolved", new(mapResult.Map, layout));
            }
            finally { foreach (byte[] bytes in transient) Array.Clear(bytes); }
        }

        private Record ParseRecord(ReadOnlySpan<byte> raw, ulong slot, ulong? expected)
        {
            token.ThrowIfCancellationRequested();
            byte[] owned = raw.ToArray(); transient.Add(owned);
            var decoded = NtfsRecordDecoder.Decode(owned, geometry.SectorBytes, 3, 1, token, slot, geometry.ClusterBytes);
            if (decoded.Status == NtfsDecodeStatus.Unsupported) Unsupported("MftRecord-" + decoded.Code);
            if (decoded.Status != NtfsDecodeStatus.Decoded || decoded.Record == null) Invalid("MftRecord-" + decoded.Code);
            var record = decoded.Record!;
            if (record.IsDirectory) Unsupported("MftDirectoryRecord");
            if (expected.HasValue)
            {
                if (record.Reference != expected.Value) Invalid("MftExtensionSequence");
                if (record.BaseReference != rootReference) Invalid("MftExtensionBase");
            }
            else if (slot != 0 || record.BaseReference != 0) Invalid("MftBaseRecord");
            int usa = U16(owned, 4), count = U16(owned, 6);
            for (int i = 1; i < count; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(owned.AsSpan(i * 512 - 2), U16(owned, usa + i * 2));
            int used = checked((int)U32(owned, 24));
            var attrs = new List<Attribute>();
            for (int p = U16(owned, 20); U32(owned, p) != uint.MaxValue;)
            {
                token.ThrowIfCancellationRequested();
                int length = checked((int)U32(owned, p + 4));
                ReadOnlySpan<byte> attr = owned.AsSpan(p, length);
                uint type = U32(attr, 0);
                if (type is 0x20 or 0x80 or 0xb0 or 0x30)
                {
                    string name = attr[9] == 0 ? "" : Unicode.GetString(attr.Slice(U16(attr, 10), attr[9] * 2));
                    ushort instance = U16(attr, 14);
                    if (type is 0x20 or 0x80 or 0xb0)
                    {
                        if (name.Length != 0) Unsupported("MftNamedMetadataStream");
                        if (U16(attr, 12) != 0 || attr[8] == 1 && U16(attr, 34) != 0) Unsupported("MftMetadataStreamForm");
                    }
                    if (attr[8] == 0)
                    {
                        byte[] value = attr.Slice(U16(attr, 20), checked((int)U32(attr, 16))).ToArray();
                        transient.Add(value);
                        if (type == 0x80) Unsupported("MftResidentData");
                        attrs.Add(new(type, instance, name, value, 0, 0, [], value.Length, value.Length, value.Length));
                    }
                    else
                    {
                        if (type == 0x30) Unsupported("MftFileNameForm");
                        ulong low = U64(attr, 16), high = U64(attr, 24);
                        if (low > long.MaxValue || high >= long.MaxValue || high < low) Invalid("MftVcnBounds");
                        var runs = ParseRuns(attr, (long)low, (long)high);
                        attrs.Add(new(type, instance, name, null, (long)low, (long)high, runs,
                            low == 0 ? I64(attr, 48) : -1, low == 0 ? I64(attr, 56) : -1, low == 0 ? I64(attr, 40) : -1));
                    }
                }
                p = checked(p + length);
                if (p >= used) Invalid("MftAttributeBounds");
            }
            return new(record.Reference, record.Names, attrs.AsReadOnly());
        }

        private IReadOnlyList<NtfsMftRun> ParseRuns(ReadOnlySpan<byte> attr, long low, long high)
        {
            var runs = new List<NtfsMftRun>();
            long vcn = low, lcn = 0;
            for (int p = U16(attr, 32); p < attr.Length;)
            {
                token.ThrowIfCancellationRequested();
                byte header = attr[p++];
                if (header == 0)
                {
                    if (runs.Count == 0 || vcn != checked(high + 1)) Invalid("MftRunCoverage");
                    ValidateRunSet(runs); return runs.AsReadOnly();
                }
                if (runs.Count >= MaximumRuns) Limit("MftResolvedRunLimit");
                int n = header & 15, d = header >> 4;
                if (n is < 1 or > 8 || d is < 1 or > 8 || !Fits(p, n + d, attr.Length)) Invalid("MftRunEncoding");
                ulong size = 0, delta = 0;
                for (int i = 0; i < n; i++) size |= (ulong)attr[p++] << (i * 8);
                for (int i = 0; i < d; i++) delta |= (ulong)attr[p++] << (i * 8);
                if (d < 8 && (delta & (1UL << (d * 8 - 1))) != 0) delta |= ulong.MaxValue << (d * 8);
                if (size == 0 || size > long.MaxValue) Invalid("MftRunLength");
                lcn = checked(lcn + unchecked((long)delta));
                runs.Add(new(vcn, (long)size, lcn)); vcn = checked(vcn + (long)size);
                if (vcn > checked(high + 1)) Invalid("MftRunCoverage");
            }
            Invalid("MftRunTerminator"); return [];
        }

        private List<Entry> ParseEntries(ReadOnlySpan<byte> bytes)
        {
            var result = new List<Entry>();
            var keys = new HashSet<(uint, string, long)>();
            for (int p = 0; p < bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                if (!Fits(p, 26, bytes.Length)) Invalid("MftListEntryBounds");
                uint type = U32(bytes, p); int length = U16(bytes, p + 4), names = bytes[p + 6], nameOffset = bytes[p + 7];
                if (length < 32 || length % 8 != 0 || !Fits(p, length, bytes.Length) ||
                    names != 0 && (nameOffset < 26 || !Fits(nameOffset, names * 2, length))) Invalid("MftListEntryBounds");
                if (type is < 0x10 or > 0x100 || type % 0x10 != 0) Unsupported("MftListAttributeType");
                ulong low = U64(bytes, p + 8), reference = U64(bytes, p + 16);
                if (low > long.MaxValue || reference >> 48 == 0 ||
                    (reference & NtfsRecordDecoder.MaximumRecordNumber) >= (ulong)(geometry.MftValidDataLength / geometry.RecordBytes))
                    Invalid("MftListReference");
                if ((reference & NtfsRecordDecoder.MaximumRecordNumber) == 0 && reference != rootReference) Invalid("MftListReference");
                string name = names == 0 ? "" : Unicode.GetString(bytes.Slice(p + nameOffset, names * 2));
                if (name.IndexOf('\0') >= 0) Invalid("MftListName");
                if (type is 0x80 or 0xb0 && !keys.Add((type, name, (long)low))) Invalid("MftListDuplicateExtent");
                result.Add(new(type, U16(bytes, p + 24), name, (long)low, reference));
                if (result.Count > 2048) Limit("MftListEntryLimit");
                p = checked(p + length);
            }
            return result;
        }

        private static bool Matches(Entry entry, Record record, Attribute attr) => entry.Reference == record.Reference &&
            entry.Type == attr.Type && entry.Instance == attr.Instance && entry.Name == attr.Name && entry.Low == attr.Low;

        private List<Attribute> Collect(Dictionary<ulong, Record> records, IEnumerable<Entry> entries, uint type, bool complete)
        {
            var result = new List<Attribute>();
            foreach (Entry entry in entries.Where(e => e.Type == type && e.Name.Length == 0))
            {
                token.ThrowIfCancellationRequested();
                if (!records.TryGetValue(entry.Reference, out Record? record))
                { if (complete) Invalid("MftListRecordMissing"); else continue; }
                Attribute[] attrs = record!.Attributes.Where(a => Matches(entry, record, a)).ToArray();
                if (attrs.Length != 1) Invalid("MftListAttributeMismatch");
                result.Add(attrs[0]);
            }
            return result;
        }

        private IReadOnlyList<NtfsMftRun> ValidateKnownData(List<Attribute> attrs)
        {
            if (attrs.Count(a => a.Low == 0 && a.Value == null) != 1) Unsupported("MftBootstrapDataMissing");
            Attribute first = attrs.Single(a => a.Low == 0);
            ValidateLengths(first, true);
            var runs = attrs.SelectMany(a => a.Runs).OrderBy(r => r.FirstVcn).ToArray();
            ValidateRunSet(runs);
            if (runs.Length == 0 || runs[0].FirstVcn != 0 || runs[0].FirstLcn != geometry.MftStartLcn) Invalid("MftBootstrapLcn");
            if (runs.Any(r => checked((r.FirstVcn + r.ClusterCount) * geometry.ClusterBytes) > first.Allocated)) Invalid("MftRunCoverage");
            var bootstrap = TryPlan(runs, 0, geometry.RecordBytes);
            if (bootstrap == null || bootstrap.Count != 1 || bootstrap[0].Length != geometry.RecordBytes ||
                bootstrap[0].VolumeOffset != checked(geometry.MftStartLcn * geometry.ClusterBytes))
                Unsupported("MftBootstrapFragmented");
            return runs;
        }

        private IReadOnlyList<NtfsMftRun> ValidateStream(List<Attribute> attrs, bool mft, bool bitmap)
        {
            if (attrs.Count == 0 || attrs.Count(a => a.Low == 0) != 1) Invalid("MftStreamExtentCoverage");
            Attribute first = attrs.Single(a => a.Low == 0);
            if (first.Value != null)
            {
                if (mft || attrs.Count != 1 || first.Value.Length > geometry.RecordBytes) Unsupported("MftResidentMetadataForm");
                if (bitmap && first.Data < RequiredBitmapBytes()) Invalid("MftBitmapCoverage");
                return [];
            }
            if (attrs.Any(a => a.Value != null)) Invalid("MftMixedStreamForm");
            ValidateLengths(first, mft);
            var runs = attrs.SelectMany(a => a.Runs).OrderBy(r => r.FirstVcn).ToArray();
            ValidateRunSet(runs);
            long next = 0;
            foreach (var run in runs)
            {
                if (run.FirstVcn != next) Invalid("MftStreamExtentCoverage");
                next = checked(next + run.ClusterCount);
            }
            if (checked(next * geometry.ClusterBytes) != first.Allocated) Invalid("MftStreamExtentCoverage");
            if (bitmap && first.Initialized < RequiredBitmapBytes()) Invalid("MftBitmapCoverage");
            if (mft && runs[0].FirstLcn != geometry.MftStartLcn) Invalid("MftBootstrapLcn");
            return runs;
        }

        private long RequiredBitmapBytes() => checked((geometry.MftValidDataLength / geometry.RecordBytes + 7) / 8);

        private void ValidateLengths(Attribute first, bool mft)
        {
            if (first.Allocated <= 0 || first.Allocated % geometry.ClusterBytes != 0 || first.Data <= 0 ||
                first.Initialized < 0 || first.Initialized > first.Data || first.Data > first.Allocated) Invalid("MftStreamLengths");
            if (mft && (first.Data % geometry.RecordBytes != 0 || first.Initialized % geometry.RecordBytes != 0 ||
                geometry.MftValidDataLength > first.Initialized)) Invalid("MftDataLengths");
            if (!mft && first.Initialized != first.Data) Unsupported("MftMetadataUninitialized");
        }

        private void ValidateRunSet(IReadOnlyList<NtfsMftRun> runs)
        {
            if (runs.Count > MaximumRuns) Limit("MftResolvedRunLimit");
            for (int i = 0; i < runs.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                NtfsMftRun run = runs[i];
                long end = checked(run.FirstLcn + run.ClusterCount);
                if (run.FirstVcn < 0 || run.ClusterCount <= 0 || run.FirstLcn < 0 || end > geometry.TotalClusters ||
                    checked(end * geometry.ClusterBytes) > geometry.VolumeBytes) Invalid("MftRunVolumeBounds");
                long endVcn = checked(run.FirstVcn + run.ClusterCount);
                foreach (var previous in runs.Take(i))
                {
                    if (run.FirstVcn < checked(previous.FirstVcn + previous.ClusterCount) && previous.FirstVcn < endVcn)
                        Invalid("MftRunVcnOverlap");
                    if (run.FirstLcn < checked(previous.FirstLcn + previous.ClusterCount) && previous.FirstLcn < end)
                        Invalid("MftRunPhysicalOverlap");
                }
            }
        }

        private void NoOverlap(IReadOnlyList<NtfsMftRun> a, IReadOnlyList<NtfsMftRun> b, string code)
        {
            foreach (var x in a) foreach (var y in b)
            {
                token.ThrowIfCancellationRequested();
                if (x.FirstLcn < checked(y.FirstLcn + y.ClusterCount) && y.FirstLcn < checked(x.FirstLcn + x.ClusterCount)) Invalid(code);
            }
        }

        private IReadOnlyList<NtfsRawSlice>? TryPlan(IReadOnlyList<NtfsMftRun> runs, long offset, int length)
        {
            long cursor = offset, end = checked(offset + length);
            var plan = new List<NtfsRawSlice>();
            foreach (var run in runs.OrderBy(r => r.FirstVcn))
            {
                long start = checked(run.FirstVcn * geometry.ClusterBytes), stop = checked((run.FirstVcn + run.ClusterCount) * geometry.ClusterBytes);
                if (cursor < start || cursor >= stop) continue;
                int count = checked((int)Math.Min(end - cursor, stop - cursor));
                long physical = checked(run.FirstLcn * geometry.ClusterBytes + cursor - start);
                if (physical % 512 != 0 || count % 512 != 0 || physical > geometry.VolumeBytes - count) Invalid("MftReadBounds");
                plan.Add(new(physical, count, checked((int)(cursor - offset)))); cursor = checked(cursor + count);
                if (cursor == end) return plan;
            }
            return null;
        }

        private byte[] ReadStream(IReadOnlyList<NtfsMftRun> runs, long offset, int length)
        {
            int aligned = checked((length + 511) / 512 * 512);
            var plan = TryPlan(runs, offset, aligned);
            if (plan == null) Invalid("MftStreamReadCoverage");
            byte[] bytes = ReadPlan(plan!, aligned);
            byte[] value = bytes.AsSpan(0, length).ToArray(); transient.Add(value); return value;
        }

        private byte[] ReadPlan(IReadOnlyList<NtfsRawSlice> plan, int length)
        {
            byte[] result = new byte[length]; transient.Add(result);
            foreach (NtfsRawSlice slice in plan)
            {
                token.ThrowIfCancellationRequested();
                int count;
                try { count = readPhysical(slice.VolumeOffset, result.AsMemory(slice.RecordOffset, slice.Length), token); }
                catch (Exception) { token.ThrowIfCancellationRequested(); throw new Failure("ReadFailed", "MftReadFailed"); }
                token.ThrowIfCancellationRequested();
                if (count != slice.Length) throw new Failure("ReadFailed", "MftShortRead");
            }
            return result;
        }

        private static bool MftName(Record record) => record.Names.Any(n => n.Namespace != 2 &&
            string.Equals(n.Name, "$MFT", StringComparison.OrdinalIgnoreCase));
    }
}
