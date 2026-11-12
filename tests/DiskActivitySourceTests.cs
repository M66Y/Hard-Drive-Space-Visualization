using System.Runtime.InteropServices;
using System.Text;
using DiskAtlas;

internal static class DiskActivitySourceTests
{
    private sealed class Query : IDiskActivityQuery
    {
        internal bool Success = true, Disposed;
        internal int Collects, Reads;
        internal Action? OnCollect;
        internal Action<int>? OnRead;
        internal IReadOnlyList<DiskActivityCounter>?[] Values =
        [new[] { new DiskActivityCounter("0 C: D:", 0, 75) }, new[] { new DiskActivityCounter("0 C: D:", 0, 123456) },
            new[] { new DiskActivityCounter("0 C: D:", 0, 654321) }, new[] { new DiskActivityCounter("0 C: D:", 0, 0.002) }];
        public bool Collect(CancellationToken token) { Collects++; OnCollect?.Invoke(); return Success; }
        public IReadOnlyList<DiskActivityCounter>? Read(int index, CancellationToken token) { Reads++; OnRead?.Invoke(index); return Values[index]; }
        public void Dispose() => Disposed = true;
    }

    // All buffers and counter observations below are synthetic; no native query is opened.
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Disk activity source check failed: " + name);
            checks++; Console.WriteLine("PASS disk activity source " + name);
        }
        Check(IntPtr.Size == 8 && Marshal.SizeOf<PdhDiskActivityQuery.FormattedItem>() == 24 &&
            Marshal.OffsetOf<PdhDiskActivityQuery.FormattedItem>(nameof(PdhDiskActivityQuery.FormattedItem.Name)).ToInt32() == 0 &&
            Marshal.OffsetOf<PdhDiskActivityQuery.FormattedItem>(nameof(PdhDiskActivityQuery.FormattedItem.Status)).ToInt32() == 8 &&
            Marshal.OffsetOf<PdhDiskActivityQuery.FormattedItem>(nameof(PdhDiskActivityQuery.FormattedItem.Value)).ToInt32() == 16,
            "x64 PDH item offsets and stride");
        foreach (var (name, expected) in new[] { ("0", 0), ("0 C: D:", 0), ("12 X:", 12), ("2147483647", int.MaxValue) })
            Check(PdhDiskActivitySource.TryParseInstance(name, out int disk) && disk == expected, "known instance grammar");
        foreach (string? name in new string?[] { null, "", "_Total", "01 C:", "-1", "2147483648", " 0 C:", "0 C: ", "0 ", "0 other", "0 C: C:", "0 C:\\", "０ C:", "0  C:" })
            Check(!PdhDiskActivitySource.TryParseInstance(name, out _), "unknown instance rejected");
        bool negativeDisk = false;
        try { using var invalid = new PdhDiskActivitySource(-1); } catch (ArgumentOutOfRangeException) { negativeDisk = true; }
        Check(negativeDisk, "negative target disk rejected before native work");
        using (var unavailable = new PdhDiskActivitySource(0, () => throw new DllNotFoundException("synthetic private marker")))
            Check(unavailable.Collect(default).Status == DiskActivityStatus.Unavailable, "missing native provider degrades without exposing its message");
        var stamp = DateTimeOffset.UtcNow;
        var fake = new Query();
        int created = 0;
        using (var source = new PdhDiskActivitySource(0, () => { created++; return fake; }))
        {
            Check(created == 0, "construction has no native work");
            var first = source.Collect(default);
            Check(first.Status == DiskActivityStatus.Waiting && first.ActivePercent == null && fake.Reads == 0, "first collect primes without fake zero");
            DateTimeOffset beforeRead = default;
            fake.OnRead = index => { if (index == 0) { beforeRead = DateTimeOffset.UtcNow; Thread.Sleep(20); } };
            var second = source.Collect(default);
            Check(second.Timestamp <= beforeRead, "sample freshness begins at collection before slow formatting reads");
            fake.OnRead = null;
            Check(second.Status == DiskActivityStatus.Available && second.DiskNumber == 0 && second.ActivePercent == 25 &&
                second.ReadBytesPerSecond == 123456 && second.WriteBytesPerSecond == 654321 && second.ResponseMilliseconds == 2,
                "idle conversion and unscaled byte and millisecond units");
            fake.Success = false;
            Check(source.Collect(default).Status == DiskActivityStatus.Unavailable, "failed collection is unavailable");
            fake.Success = true;
            Check(source.Collect(default).Status == DiskActivityStatus.Waiting, "collection failure invalidates prime");
            Check(source.Collect(default).Status == DiskActivityStatus.Available && created == 1, "query reused after fresh prime");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            int before = fake.Collects; bool stopped = false;
            try { source.Collect(cancel.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && fake.Collects == before, "pre-cancel avoids provider work");
            using var mid = new CancellationTokenSource(); fake.OnCollect = mid.Cancel; stopped = false;
            try { source.Collect(mid.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "post-provider cancellation publishes no sample");
            fake.OnCollect = null;
        }
        Check(fake.Disposed, "source disposes its query");
        var disposed = new PdhDiskActivitySource(0, () => new Query()); disposed.Dispose(); disposed.Dispose();
        bool refused = false;
        try { disposed.Collect(default); } catch (ObjectDisposedException) { refused = true; }
        Check(refused, "dispose is idempotent and blocks later samples");
        using (var resumed = new PdhDiskActivitySource(0, () => new Query()))
            Check(resumed.Collect(default).Status == DiskActivityStatus.Waiting, "new source resumes with a fresh prime");
        IReadOnlyList<DiskActivityCounter> Metric(double? value, string instance = "0 C:") => new[] { new DiskActivityCounter(instance, 0, value) };
        var partial = PdhDiskActivitySource.Compose(stamp, 0, null, Metric(10), null, null);
        Check(partial.Status == DiskActivityStatus.Available && partial.ReadBytesPerSecond == 10 && partial.ActivePercent == null,
            "single usable metric remains available");
        foreach (double number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 101 })
            Check(PdhDiskActivitySource.Compose(stamp, 0, Metric(number), null, null, null).ActivePercent == null,
                "invalid idle value cannot become activity");
        foreach (double number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1 })
        {
            var invalid = PdhDiskActivitySource.Compose(stamp, 0, null, Metric(number), Metric(number), Metric(number));
            Check(invalid.Status == DiskActivityStatus.Unavailable && invalid.ReadBytesPerSecond == null && invalid.WriteBytesPerSecond == null && invalid.ResponseMilliseconds == null,
                "invalid rates and latency remain unknown");
        }
        Check(PdhDiskActivitySource.Compose(stamp, 0, Metric(100), Metric(0), Metric(0), Metric(0)).ActivePercent == 0,
            "valid zero remains measured zero");
        Check(PdhDiskActivitySource.Compose(stamp, 0, Metric(0), null, null, null).ActivePercent == 100, "fully busy disk is bounded at 100");
        Check(PdhDiskActivitySource.Compose(stamp, 0, null, null, null, Metric(double.MaxValue)).ResponseMilliseconds == null,
            "unit conversion overflow stays unknown");
        Check(PdhDiskActivitySource.Compose(stamp, 0, Metric(50), Metric(1, "0 D:"), null, null).Status == DiskActivityStatus.Unavailable,
            "different instance names never join across counters");
        Check(PdhDiskActivitySource.Compose(stamp, 0, new[] { new DiskActivityCounter("0 C:", 0, 1), new DiskActivityCounter("0 C:", 0, 2) }, Metric(5), null, null).ActivePercent == null,
            "duplicate target invalidates the ambiguous metric");
        var other = new[] { new DiskActivityCounter("1 D:", 1, 5) };
        Check(PdhDiskActivitySource.Compose(stamp, 0, other, null, null, null).Status == DiskActivityStatus.Unavailable, "other disk is never substituted");

        bool Decode((string Name, uint Status, double Value)[] entries, Action<IntPtr, int>? mutate, out IReadOnlyList<DiskActivityCounter> decoded)
        {
            int stringBytes = entries.Sum(item => Encoding.Unicode.GetByteCount(item.Name + '\0'));
            int capacity = entries.Length * 24 + stringBytes;
            IntPtr buffer = Marshal.AllocHGlobal(Math.Max(1, capacity));
            try
            {
                int offset = entries.Length * 24;
                for (int i = 0; i < entries.Length; i++)
                {
                    byte[] text = Encoding.Unicode.GetBytes(entries[i].Name + '\0');
                    IntPtr name = IntPtr.Add(buffer, offset); Marshal.Copy(text, 0, name, text.Length);
                    Marshal.StructureToPtr(new PdhDiskActivityQuery.FormattedItem { Name = name, Status = entries[i].Status, Value = entries[i].Value }, IntPtr.Add(buffer, i * 24), false);
                    offset += text.Length;
                }
                mutate?.Invoke(buffer, capacity);
                return PdhDiskActivityQuery.TryDecodeArray(buffer, capacity, (uint)entries.Length, out decoded);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        Check(Decode(new[] { ("0 C: D:", 0u, 75.5), ("1 X:", 1u, 2.0), ("_Total", 0u, 9.0), ("unknown", 0u, 1.0) }, null, out var decoded) &&
            decoded.Count == 2 && decoded[0].Value == 75.5 && decoded[1].Value == 2, "array values and valid/new statuses decoded while totals ignored");
        Check(Decode(new[] { ("0 C:", 0xC0000BBAu, 5.0) }, null, out decoded) && decoded.Single().Value == null,
            "item status checked independently of API status");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1 })
            Check(Decode(new[] { ("0 C:", 0u, invalid) }, null, out decoded) && decoded.Single().Value == null, "nonfinite or negative buffer value ignored");
        Check(!Decode(new[] { ("0 C:", 0u, 1.0), ("0 D:", 0u, 2.0) }, null, out _), "duplicate disk number rejected in native array");
        Check(!Decode(new[] { ("0 C:", 0u, 1.0) }, (buffer, _) => Marshal.WriteIntPtr(buffer, IntPtr.Zero), out _), "null name pointer rejected");
        Check(!Decode(new[] { ("0 C:", 0u, 1.0) }, (buffer, _) => Marshal.WriteIntPtr(buffer, buffer), out _), "name pointer into item headers rejected");
        Check(!Decode(new[] { ("0 C:", 0u, 1.0) }, (buffer, size) => Marshal.WriteIntPtr(buffer, IntPtr.Add(buffer, size)), out _), "name pointer past allocation rejected");
        Check(!Decode(new[] { ("0 C:", 0u, 1.0) }, (buffer, _) => Marshal.WriteIntPtr(buffer, IntPtr.Add(buffer, 25)), out _), "unaligned name pointer rejected");
        Check(!Decode(new[] { ("0 C:", 0u, 1.0) }, (buffer, size) => Marshal.WriteInt16(buffer, size - 2, 65), out _), "unterminated name rejected within buffer");
        Check(!Decode(new[] { (new string('x', 257), 0u, 1.0) }, null, out _), "name length bounded");
        Check(PdhDiskActivityQuery.TryDecodeArray(IntPtr.Zero, 0, 0, out decoded) && decoded.Count == 0, "empty arrays remain empty");
        Check(!PdhDiskActivityQuery.TryDecodeArray(IntPtr.Zero, 23, 1, out _), "array header truncation rejected before dereference");
        Check(!PdhDiskActivityQuery.TryDecodeArray(IntPtr.Zero, 1024 * 1024 + 1, 0, out _), "buffer cap enforced");
        Check(!PdhDiskActivityQuery.TryDecodeArray(IntPtr.Zero, 0, 4097, out _), "item cap enforced");
        Check(!PdhDiskActivityQuery.TryDecodeArray(IntPtr.Zero, -1, 0, out _), "negative buffer size rejected");
        Console.WriteLine("PASS disk activity source checks: " + checks + "; native sampling: none");
        return checks;
    }
}
