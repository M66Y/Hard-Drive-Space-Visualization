using DiskAtlas;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Loader;

// Optional filesystem benchmark; excluded from the normal, fast regression suite.
internal static class ScanBenchmark
{
    public static int Run()
    {
        string workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string artifacts = Path.Combine(workspace, "artifacts");
        string fixture = Path.Combine(artifacts, "scan-benchmark-" + Guid.NewGuid().ToString("N"));
        var baselineContext = new AssemblyLoadContext("DiskAtlas-benchmark-baseline", isCollectible: true);
        try
        {
            var scanners = new List<(string Name, Func<string, object> Scan)>();
            string archive = Path.Combine(workspace, "空间透镜-1.2.0-Windows.zip");
            if (File.Exists(archive))
            {
                using var zip = ZipFile.OpenRead(archive);
                using var dll = zip.GetEntry("DiskAtlas.dll")!.Open();
                using var buffer = new MemoryStream(); dll.CopyTo(buffer); buffer.Position = 0;
                var type = baselineContext.LoadFromStream(buffer).GetType("DiskAtlas.DiskScanner")!;
                var scanner = Activator.CreateInstance(type, [8])!;
                var method = type.GetMethod("Scan")!;
                scanners.Add(("v1.2 SSD baseline", p => method.Invoke(scanner, [p, null, CancellationToken.None])!));
            }
            scanners.Add(("v1.4 serial (allocation + identity)", p => new DiskScanner(1).Scan(p, null, CancellationToken.None)));
            scanners.Add(("v1.4 SSD (allocation + identity, 8 workers)", p => new DiskScanner().Scan(p, null, CancellationToken.None, new LiveScanSession())));
            Console.WriteLine("Warm filesystem metadata cache; nine alternating runs per mode; whole-scan wall time includes aggregation/sorting.");
            foreach (string shape in new[] { "wide", "flat" })
            {
                string root = Path.Combine(fixture, shape); Directory.CreateDirectory(root);
                int directories = shape == "wide" ? 600 : 1, count = shape == "wide" ? 80 : 24000;
                for (int d = 0; d < directories; d++)
                {
                    string dir = directories == 1 ? root : Path.Combine(root, $"folder-{d:0000}");
                    Directory.CreateDirectory(dir);
                    for (int f = 0; f < count; f++)
                        File.WriteAllBytes(Path.Combine(dir, $"file-{f:00000}.bin"), new byte[(f * 37 + d) % 2048]);
                }
                var reference = Totals(scanners[0].Scan(root));
                Console.WriteLine($"DATA {shape}: {reference.Files} files / {reference.Folders} folders / {reference.Bytes} bytes");
                foreach (var scanner in scanners) _ = scanner.Scan(root);
                var times = scanners.Select(_ => new List<double>()).ToArray();
                var allocations = scanners.Select(_ => new List<long>()).ToArray();
                for (int run = 0; run < 9; run++)
                {
                    var order = Enumerable.Range(0, scanners.Count);
                    foreach (int mode in run % 2 == 0 ? order : order.Reverse())
                    {
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        long before = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
                        object scan = scanners[mode].Scan(root);
                        watch.Stop();
                        allocations[mode].Add(GC.GetTotalAllocatedBytes(true) - before);
                        times[mode].Add(watch.Elapsed.TotalMilliseconds);
                        if (Totals(scan) != reference) throw new Exception("Scan totals changed: " + scanners[mode].Name);
                        GC.KeepAlive(scan);
                    }
                }
                for (int mode = 0; mode < scanners.Count; mode++)
                    Console.WriteLine($"BENCH {shape}, {scanners[mode].Name}: median {times[mode].Order().ElementAt(4):F2} ms; allocation {allocations[mode].Order().ElementAt(4):N0} B; runs {string.Join(", ", times[mode].Select(t => t.ToString("F2")))}");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            baselineContext.Unload();
            string prefix = Path.GetFullPath(artifacts) + Path.DirectorySeparatorChar + "scan-benchmark-";
            if (!Path.GetFullPath(fixture).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe benchmark cleanup target");
            if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
        }
    }

    private static (long Bytes, long Files, long Folders, long Skipped) Totals(object scan)
    {
        object root = scan.GetType().GetProperty("Root")!.GetValue(scan)!;
        long Read(string name) => (long)root.GetType().GetProperty(name)!.GetValue(root)!;
        return (Read("Size"), Read("FileCount"), Read("FolderCount"), (long)scan.GetType().GetProperty("Skipped")!.GetValue(scan)!);
    }
}
