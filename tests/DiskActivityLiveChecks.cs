using DiskAtlas;

internal static class DiskActivityLiveChecks
{
    internal static int Run()
    {
        try
        {
            var targets = DiskActivityResolver.Discover();
            var current = DiskActivityResolver.Resolve(Environment.CurrentDirectory);
            Console.WriteLine($"MAPPING targets={targets.Count}; current_supported={current != null}");
            if (current == null) { Console.WriteLine("SKIP disk activity live: current mapping unsupported"); return 2; }
            using var source = new PdhDiskActivitySource(current.DiskNumber);
            var first = source.Collect(default);
            if (first.Status != DiskActivityStatus.Waiting) throw new InvalidOperationException();
            int complete = 0;
            for (int i = 0; i < 3; i++)
            {
                Thread.Sleep(1100);
                var sample = source.Collect(default);
                bool all = sample.Status == DiskActivityStatus.Available && sample.ActivePercent.HasValue &&
                    sample.ReadBytesPerSecond.HasValue && sample.WriteBytesPerSecond.HasValue && sample.ResponseMilliseconds.HasValue;
                if (all) complete++;
                Console.WriteLine($"SAMPLE {i + 1}; all_metrics_valid={all}; activity={sample.ActivePercent:F2}; read_MiBs={sample.ReadBytesPerSecond / 1048576:F2}; write_MiBs={sample.WriteBytesPerSecond / 1048576:F2}; response_ms={sample.ResponseMilliseconds:F3}");
            }
            if (complete != 3) throw new InvalidOperationException();
            Console.WriteLine("PASS disk activity live: native mapping and unlocalized PDH metrics; no paths or instance names emitted");
            return 0;
        }
        catch { Console.Error.WriteLine("FAIL disk activity live; no private details emitted"); return 1; }
    }
}
