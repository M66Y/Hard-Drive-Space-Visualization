using System.Globalization;
using System.Runtime.InteropServices;

namespace DiskAtlas;

internal enum DiskActivityStatus { Waiting, Available, Unavailable }
internal sealed record DiskActivitySample(DateTimeOffset Timestamp, int DiskNumber, double? ActivePercent,
    double? ReadBytesPerSecond, double? WriteBytesPerSecond, double? ResponseMilliseconds, DiskActivityStatus Status);
internal interface IDiskActivitySource : IDisposable
{
    DiskActivitySample Collect(CancellationToken token);
}

// Internal seam keeps buffer/state tests independent of the machine's disks and counters.
internal sealed record DiskActivityCounter(string Instance, int DiskNumber, double? Value);
internal interface IDiskActivityQuery : IDisposable
{
    bool Collect(CancellationToken token);
    IReadOnlyList<DiskActivityCounter>? Read(int counter, CancellationToken token);
}

// Owned by one background sampling loop. The constructor does not call PDH.
internal sealed class PdhDiskActivitySource : IDiskActivitySource
{
    private readonly int diskNumber;
    private readonly Func<IDiskActivityQuery> createQuery;
    private readonly object gate = new();
    private IDiskActivityQuery? query;
    private bool primed, disposed;

    internal PdhDiskActivitySource(int diskNumber) : this(diskNumber, () => new PdhDiskActivityQuery()) { }
    internal PdhDiskActivitySource(int diskNumber, Func<IDiskActivityQuery> createQuery)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(diskNumber);
        this.diskNumber = diskNumber;
        this.createQuery = createQuery ?? throw new ArgumentNullException(nameof(createQuery));
    }

    public DiskActivitySample Collect(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            try
            {
                query ??= createQuery();
                bool collected = query.Collect(token);
                DateTimeOffset collectedAt = DateTimeOffset.UtcNow;
                token.ThrowIfCancellationRequested();
                if (!collected)
                {
                    primed = false;
                    return Empty(DiskActivityStatus.Unavailable);
                }
                if (!primed)
                {
                    primed = true;
                    return Empty(DiskActivityStatus.Waiting);
                }
                var counters = new IReadOnlyList<DiskActivityCounter>?[4];
                for (int i = 0; i < counters.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    counters[i] = query.Read(i, token);
                }
                token.ThrowIfCancellationRequested();
                return Compose(collectedAt, diskNumber, counters[0], counters[1], counters[2], counters[3]);
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                primed = false;
                return Empty(DiskActivityStatus.Unavailable);
            }
        }
    }

    private DiskActivitySample Empty(DiskActivityStatus status) => new(DateTimeOffset.UtcNow, diskNumber, null, null, null, null, status);

    internal static DiskActivitySample Compose(DateTimeOffset timestamp, int diskNumber,
        IReadOnlyList<DiskActivityCounter>? idle, IReadOnlyList<DiskActivityCounter>? read,
        IReadOnlyList<DiskActivityCounter>? write, IReadOnlyList<DiskActivityCounter>? response)
    {
        var arrays = new[] { idle, read, write, response };
        var matches = new DiskActivityCounter?[4];
        for (int i = 0; i < arrays.Length; i++)
        {
            if (arrays[i] is not { } items) continue;
            bool duplicate = false;
            foreach (var item in items)
            {
                if (item.DiskNumber != diskNumber || !TryParseInstance(item.Instance, out int parsed) || parsed != diskNumber) continue;
                if (matches[i] != null) { duplicate = true; break; }
                matches[i] = item;
            }
            if (duplicate) matches[i] = null;
        }
        // A topology/instance-name change between arrays must not join different instances.
        string[] names = matches.Where(item => item != null).Select(item => item!.Instance).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length != 1) return new(timestamp, diskNumber, null, null, null, null, DiskActivityStatus.Unavailable);
        static double? Nonnegative(double? value) => value is { } number && double.IsFinite(number) && number >= 0 ? number : null;
        double? idleValue = Nonnegative(matches[0]?.Value);
        double? active = idleValue is <= 100 ? 100 - idleValue.Value : null;
        double? readValue = Nonnegative(matches[1]?.Value), writeValue = Nonnegative(matches[2]?.Value);
        double? seconds = Nonnegative(matches[3]?.Value);
        double? milliseconds = seconds.HasValue ? Nonnegative(seconds.Value * 1000) : null;
        bool available = active.HasValue || readValue.HasValue || writeValue.HasValue || milliseconds.HasValue;
        return new(timestamp, diskNumber, active, readValue, writeValue, milliseconds,
            available ? DiskActivityStatus.Available : DiskActivityStatus.Unavailable);
    }

    internal static bool TryParseInstance(string? name, out int diskNumber)
    {
        diskNumber = -1;
        if (string.IsNullOrEmpty(name) || name.Length > 256) return false;
        int digits = 0;
        while (digits < name.Length && char.IsAsciiDigit(name[digits])) digits++;
        if (digits == 0 || (digits > 1 && name[0] == '0') ||
            !int.TryParse(name.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out int number)) return false;
        var letters = new HashSet<char>();
        for (int i = digits; i < name.Length; i += 3)
        {
            if (i + 2 >= name.Length || name[i] != ' ' || !char.IsAsciiLetter(name[i + 1]) || name[i + 2] != ':' ||
                !letters.Add(char.ToUpperInvariant(name[i + 1]))) return false;
        }
        diskNumber = number;
        return true;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            query?.Dispose();
            query = null;
        }
    }
}

internal sealed class PdhDiskActivityQuery : IDiskActivityQuery
{
    internal const int MaximumBufferBytes = 1024 * 1024, MaximumItems = 4096, ItemBytes = 24;
    private const uint MoreData = 0x800007D2, InvalidArgument = 0xC0000BBD;
    private const uint Format = 0x00000200 | 0x00001000 | 0x00008000; // DOUBLE | NOSCALE | NOCAP100; validate idle bounds ourselves.
    private readonly IntPtr[] counters = new IntPtr[4];
    private IntPtr query;
    private bool disposed;

    [StructLayout(LayoutKind.Explicit, Size = ItemBytes)]
    internal struct FormattedItem
    {
        [FieldOffset(0)] internal IntPtr Name;
        [FieldOffset(8)] internal uint Status;
        [FieldOffset(16)] internal double Value;
    }

    internal PdhDiskActivityQuery()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) return;
        if (PdhOpenQueryW(null, UIntPtr.Zero, out query) != 0 || query == IntPtr.Zero)
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
            query = IntPtr.Zero;
            return;
        }
        try
        {
            string[] paths = [@"\PhysicalDisk(*)\% Idle Time", @"\PhysicalDisk(*)\Disk Read Bytes/sec",
                @"\PhysicalDisk(*)\Disk Write Bytes/sec", @"\PhysicalDisk(*)\Avg. Disk sec/Transfer"];
            for (int i = 0; i < paths.Length; i++)
                if (PdhAddEnglishCounterW(query, paths[i], UIntPtr.Zero, out var counter) == 0) counters[i] = counter;
        }
        catch
        {
            PdhCloseQuery(query);
            query = IntPtr.Zero;
            throw;
        }
    }

    public bool Collect(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        bool success = query != IntPtr.Zero && counters.Any(counter => counter != IntPtr.Zero) && PdhCollectQueryData(query) == 0;
        token.ThrowIfCancellationRequested();
        return success;
    }

    public IReadOnlyList<DiskActivityCounter>? Read(int counter, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(counter);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(counter, counters.Length);
        token.ThrowIfCancellationRequested();
        if (query == IntPtr.Zero || counters[counter] == IntPtr.Zero) return null;
        // Restart the size probe after a race; PDH does not guarantee the size
        // returned when an already nonzero buffer is insufficient.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            uint bytes = 0;
            uint status = PdhGetFormattedCounterArrayW(counters[counter], Format, ref bytes, out uint count, IntPtr.Zero);
            if (status == 0 && count == 0) return Array.Empty<DiskActivityCounter>();
            if (status != MoreData || bytes is 0 or > MaximumBufferBytes || count > MaximumItems) return null;
            int capacity = (int)bytes;
            IntPtr buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                status = PdhGetFormattedCounterArrayW(counters[counter], Format, ref bytes, out count, buffer);
                token.ThrowIfCancellationRequested();
                if (status is MoreData or InvalidArgument) continue;
                if (status != 0 || bytes > capacity) return null;
                return TryDecodeArray(buffer, (int)bytes, count, out var values) ? values : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    // The caller owns a readable buffer of exactly bufferBytes. Every embedded
    // string pointer is checked against that same allocation before dereferencing.
    internal static bool TryDecodeArray(IntPtr buffer, int bufferBytes, uint count, out IReadOnlyList<DiskActivityCounter> values)
    {
        values = Array.Empty<DiskActivityCounter>();
        if (IntPtr.Size != 8 || bufferBytes < 0 || bufferBytes > MaximumBufferBytes || count > MaximumItems ||
            (long)count * ItemBytes > bufferBytes) return false;
        if (count == 0) return true;
        long start = buffer.ToInt64();
        if (start <= 0 || start > long.MaxValue - bufferBytes) return false;
        long stringsStart = start + (long)count * ItemBytes, end = start + bufferBytes;
        var output = new List<DiskActivityCounter>((int)count);
        var disks = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            var item = Marshal.PtrToStructure<FormattedItem>(IntPtr.Add(buffer, i * ItemBytes));
            long name = item.Name.ToInt64();
            if (name < stringsStart || name > end - 2 || (name - start) % 2 != 0) return false;
            int maximum = (int)Math.Min(257, (end - name) / 2), length = 0;
            while (length < maximum && Marshal.ReadInt16(item.Name, length * 2) != 0) length++;
            if (length == maximum || length > 256) return false;
            string instance = Marshal.PtrToStringUni(item.Name, length) ?? string.Empty;
            if (!PdhDiskActivitySource.TryParseInstance(instance, out int disk)) continue;
            if (!disks.Add(disk)) return false;
            double? number = item.Status is 0 or 1 && double.IsFinite(item.Value) && item.Value >= 0 ? item.Value : null;
            output.Add(new(instance, disk, number));
        }
        values = output.AsReadOnly();
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (query != IntPtr.Zero) PdhCloseQuery(query); // Also removes all added counters.
        query = IntPtr.Zero;
        Array.Clear(counters);
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhOpenQueryW(string? source, UIntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferBytes, out uint count, IntPtr buffer);
    [DllImport("pdh.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
