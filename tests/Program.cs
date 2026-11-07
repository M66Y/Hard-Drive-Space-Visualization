using DiskAtlas;
using System.Drawing;

if (args.Contains("--background-preview")) return BackgroundRegression.Preview(args[1]);
if (args.Contains("--background-child")) return BackgroundRegression.RunChild(args[1], args[2]);
if (args.Contains("--background-tests")) return BackgroundRegression.Run(args.Contains("--dpi-96"));

if (args.Contains("--treemap-benchmark")) return TreemapBenchmark.Run(args.Last());

if (args.Contains("--scan-benchmark")) return ScanBenchmark.Run();
if (args.Contains("--policy-benchmark")) return ScanPolicyBenchmark.Run();
if (args.Contains("--memory-benchmark")) return MemoryBenchmark.Run();
if (args.Contains("--ui-regression")) return UiRegression.Run(args.Contains("--dpi-96"));
if (args.Contains("--advanced-tests")) return AdvancedFeatures.Run();
if (args.Contains("--journal-tests")) return JournalRegression.Run();
if (args.Contains("--refresh-benchmark")) return RefreshBenchmark.Run();

int passed = 0;
var temp = Path.Combine(Path.GetTempPath(), "DiskAtlas-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
void Test(string name, Action action)
{
    action(); passed++; Console.WriteLine("PASS " + name);
}
try
{
    var root = Path.Combine(temp, "中文 空格目录");
    Directory.CreateDirectory(Path.Combine(root, "图片", "空目录"));
    Directory.CreateDirectory(Path.Combine(root, "代码"));
    File.WriteAllBytes(Path.Combine(root, "图片", "photo.JPG"), new byte[3072]);
    File.WriteAllBytes(Path.Combine(root, "代码", "main.cs"), new byte[1024]);
    File.WriteAllBytes(Path.Combine(root, "零字节.txt"), []);
    File.WriteAllBytes(Path.Combine(root, "无扩展名"), new byte[17]);
    ScanResult? result = null;
    Test("recursive totals, Unicode paths, empty folders, zero-byte files, sort order", () =>
    {
        result = new DiskScanner().Scan(root, null, CancellationToken.None);
        Check(result.Root.Size == 4113, "Byte total mismatch");
        Check(result.Root.FileCount == 4, "File count mismatch");
        Check(result.Root.FolderCount == 3, "Folder count mismatch");
        Check(result.Root.Children[0].Name == "图片", "Descending order mismatch");
        Check(result.Root.Descendants().Count() == 7, "Traversal mismatch");
        Check(result.Skipped == 0 && !result.Root.Incomplete, "Unexpected skipped items");
        Check(result.Root.Descendants().First(e => e.Name == "photo.JPG").Extension == ".jpg", "Extension normalization mismatch");
        Check(result.Root.Descendants().First(e => e.Name == "无扩展名").Extension == "无扩展名", "Extensionless mismatch");
    });
    Test("directory totals and parent references", () =>
    {
        var pictures = result!.Root.Children[0];
        Check(pictures.Parent == result.Root && pictures.Size == 3072 && pictures.FileCount == 1 && pictures.FolderCount == 1, "Directory aggregation mismatch");
        Check(result.Root.Children.Sum(e => e.Size) == result.Root.Size, "Partition mismatch");
    });
    Test("empty root", () =>
    {
        var empty = new DiskScanner().Scan(Path.Combine(root, "图片", "空目录"), null, CancellationToken.None).Root;
        Check(empty.Size == 0 && empty.FileCount == 0 && empty.FolderCount == 0, "Empty totals mismatch");
    });
    Test("missing root has explicit error", () =>
    {
        try { new DiskScanner().Scan(Path.Combine(temp, "missing"), null, CancellationToken.None); throw new Exception("Expected missing error"); }
        catch (DirectoryNotFoundException) { }
    });
    Test("pre-cancelled and mid-scan cancellation", () =>
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { new DiskScanner().Scan(root, null, cts.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
        using var during = new CancellationTokenSource();
        try { new DiskScanner().Scan(root, new InlineProgress(_ => during.Cancel()), during.Token); throw new Exception("Mid-scan cancellation ignored"); } catch (OperationCanceledException) { }
    });
    Test("final progress matches completed scan", () =>
    {
        ScanProgress? latest = null;
        var scan = new DiskScanner().Scan(root, new InlineProgress(p => latest = p), CancellationToken.None);
        Check(latest != null && latest.Bytes == scan.Root.Size && latest.Files == scan.Root.FileCount && latest.Folders == scan.Root.FolderCount, "Progress mismatch");
    });
    Test("deep directory traversal", () =>
    {
        var deepRoot = Path.Combine(temp, "deep");
        string last = deepRoot;
        for (int i = 0; i < 140; i++) last = Path.Combine(last, "sub");
        Directory.CreateDirectory(last); File.WriteAllBytes(Path.Combine(last, "leaf.bin"), new byte[127]);
        var deep = new DiskScanner().Scan(deepRoot, null, CancellationToken.None).Root;
        Check(deep.Size == 127 && deep.FolderCount == 140 && deep.FileCount == 1, "Deep totals mismatch");
    });
    Test("size formatting supports zero and sizes above 32-bit", () =>
    {
        Check(SizeFormat.Bytes(0) == "0 B", "Zero mismatch");
        Check(SizeFormat.Bytes(1024) == "1 KB", "KB mismatch");
        Check(SizeFormat.Bytes(5L * 1024 * 1024 * 1024) == "5 GB", "64-bit mismatch");
        Check(SizeFormat.Percent(0, 0) == "0%", "Zero percentage mismatch");
    });
    Test("treemap empty/zero/degenerate inputs", () =>
    {
        Check(TreemapLayout.Build([], new RectangleF(0, 0, 100, 100)).Count == 0, "Empty layout");
        Check(TreemapLayout.Build([new("empty", 0, null, "")], new RectangleF(0, 0, 100, 100)).Count == 0, "Zero layout");
        Check(TreemapLayout.Build([new("file", 10, null, "")], new RectangleF(0, 0, 0, 100)).Count == 0, "Degenerate layout");
    });
    Test("random treemaps preserve area, proportion, bounds, and do not overlap", () =>
    {
        var random = new Random(2381);
        for (int iteration = 0; iteration < 80; iteration++)
        {
            var items = Enumerable.Range(0, random.Next(1, 180)).Select(i => new MapItem(i.ToString(), random.NextInt64(1, 10000000000), null, "")).ToList();
            var bounds = new RectangleF(10, 20, random.Next(40, 1400), random.Next(40, 900));
            var tiles = TreemapLayout.Build(items, bounds);
            double area = bounds.Width * bounds.Height, total = items.Sum(i => (double)i.Size);
            Check(tiles.Count == items.Count, "Lost items");
            double sumArea = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                var rect = tiles[i].Bounds;
                Check(float.IsFinite(rect.Width) && float.IsFinite(rect.Height) && rect.Width >= 0 && rect.Height >= 0, "Invalid rectangle");
                Check(rect.Left >= bounds.Left - .02 && rect.Top >= bounds.Top - .02 && rect.Right <= bounds.Right + .02 && rect.Bottom <= bounds.Bottom + .02, "Out of bounds");
                double actual = (double)rect.Width * rect.Height;
                double expected = items.First(it => it == tiles[i].Item).Size / total * area;
                Check(Math.Abs(actual - expected) < Math.Max(.5, expected * .003), "Area ratio mismatch");
                sumArea += actual;
                for (int j = 0; j < i; j++)
                {
                    var intersection = RectangleF.Intersect(rect, tiles[j].Bounds);
                    Check(intersection.Width * intersection.Height < .1, "Overlapping tiles");
                }
            }
            Check(Math.Abs(sumArea - area) < Math.Max(1, area * .0001), "Area not preserved");
        }
    });
    // Creating a directory junction needs no symlink privilege on Windows.
    Test("junctions are skipped to avoid cycles and duplicate counting", () =>
    {
        var link = Path.Combine(root, "循环链接");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("/c"); psi.ArgumentList.Add("mklink"); psi.ArgumentList.Add("/J"); psi.ArgumentList.Add(link); psi.ArgumentList.Add(root);
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        Check(process.ExitCode == 0, "Could not create test junction: " + process.StandardError.ReadToEnd());
        try
        {
            var linked = new DiskScanner().Scan(root, null, CancellationToken.None);
            Check(linked.Root.Size == 4113 && linked.Root.FileCount == 4, "Junction was followed");
            Check(linked.Skipped == 1 && linked.Root.Incomplete && linked.Issues.Count == 1, "Skip report mismatch");
        }
        finally { Directory.Delete(link); }
    });
    Test("hidden and system entries remain included", () =>
    {
        string hiddenRoot = Path.Combine(temp, "hidden");
        Directory.CreateDirectory(hiddenRoot);
        string hiddenFile = Path.Combine(hiddenRoot, ".hidden.bin");
        File.WriteAllBytes(hiddenFile, new byte[79]);
        File.SetAttributes(hiddenFile, FileAttributes.Hidden | FileAttributes.System);
        var hidden = new DiskScanner().Scan(hiddenRoot, null, CancellationToken.None);
        Check(hidden.Root.Size == 79 && hidden.Root.FileCount == 1, "Hidden/system file omitted");
    });
    Test("root removed during scan fails rather than reporting an empty success", () =>
    {
        string disappearing = Path.Combine(temp, "disappearing");
        Directory.CreateDirectory(disappearing);
        try
        {
            new DiskScanner().Scan(disappearing, new InlineProgress(_ => Directory.Delete(disappearing)), CancellationToken.None);
            throw new Exception("Expected root read failure");
        }
        catch (IOException) { }
    });
    Test("cancellation on final progress is honored", () =>
    {
        string emptyRoot = Path.Combine(temp, "cancel-final");
        Directory.CreateDirectory(emptyRoot);
        using var finalCancellation = new CancellationTokenSource();
        int reports = 0;
        try
        {
            new DiskScanner().Scan(emptyRoot, new InlineProgress(_ => { if (++reports == 2) finalCancellation.Cancel(); }), finalCancellation.Token);
            throw new Exception("Final cancellation ignored");
        }
        catch (OperationCanceledException) { Check(reports == 2, "Did not reach final report"); }
    });
    Test("treemap extreme skew and non-finite bounds", () =>
    {
        var skew = new List<MapItem> { new("huge", long.MaxValue / 2, null, "") };
        skew.AddRange(Enumerable.Range(0, 240).Select(i => new MapItem(i.ToString(), 1, null, "")));
        var tiles = TreemapLayout.Build(skew, new RectangleF(0, 0, 900, 500));
        Check(tiles.Count == skew.Count && tiles.All(t => float.IsFinite(t.Bounds.X) && float.IsFinite(t.Bounds.Y) && float.IsFinite(t.Bounds.Width) && float.IsFinite(t.Bounds.Height)), "Non-finite skew layout");
        Check(TreemapLayout.Build(skew, new RectangleF(0, 0, float.NaN, 50)).Count == 0, "NaN bounds accepted");
        Check(TreemapLayout.Build(skew, new RectangleF(0, 0, float.PositiveInfinity, 50)).Count == 0, "Infinite bounds accepted");
    });
    Test("folder query sorting, case-insensitive search and zero-size rows", () =>
    {
        var folder = ViewQuery.Build(new(result!.Root, ViewMode.Folder), CancellationToken.None);
        Check(folder.Rows.Count == 4 && folder.VisibleBytes == 4113 && folder.MapItems.Sum(i => i.Size) == 4113, "Folder query mismatch");
        Check(folder.Rows.Any(i => i.Size == 0) && folder.MapItems.All(i => i.Size > 0), "Zero-size row handling");
        var byName = ViewQuery.Build(new(result.Root, ViewMode.Folder, Sort: ViewSort.Name, Ascending: true), CancellationToken.None);
        Check(byName.Rows.Select(i => i.Name).SequenceEqual(byName.Rows.Select(i => i.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)), "Name sort mismatch");
        var searched = ViewQuery.Build(new(result.Root, ViewMode.LargestFiles, "JPG"), CancellationToken.None);
        Check(searched.Rows.Count == 1 && searched.Rows[0].Name == "photo.JPG", "Case-insensitive nested search");
    });
    Test("type aggregation matches file totals and supports category search", () =>
    {
        var types = ViewQuery.Build(new(result!.Root, ViewMode.FileTypes), CancellationToken.None);
        Check(types.Rows.Sum(i => i.Count) == 4 && types.VisibleBytes == 4113, "Type aggregation mismatch");
        var pictures = ViewQuery.Build(new(result.Root, ViewMode.FileTypes, "图片"), CancellationToken.None);
        Check(pictures.Rows.Count == 1 && pictures.Rows[0].Name == ".jpg", "Category search mismatch");
        Check(FileKinds.Category(".ico") == "图片" && FileKinds.Category(".tiff") == "图片", "Image category mismatch");
    });
    Test("top-500 cutoff, exact type filter and aggregate area", () =>
    {
        var large = new DiskEntry(Path.Combine(temp, "synthetic"), true);
        for (int i = 0; i < 900; i++) large.MutableChildren.Add(new DiskEntry(Path.Combine(large.Path, $"file-{i:0000}.{(i % 2 == 0 ? "bin" : "txt")}"), false, large) { Size = i, FileCount = 1 });
        var top = ViewQuery.Build(new(large, ViewMode.LargestFiles), CancellationToken.None);
        Check(top.Rows.Count == 500 && top.MatchedCount == 900 && top.Rows[0].Size == 899 && top.Rows[^1].Size == 400, "Top cutoff mismatch");
        Check(top.MapItems.Count == 241 && top.MapItems[^1].IsAggregate && top.MapItems[^1].Count == 260, "Aggregate identity/count mismatch");
        Check(top.MapItems.Sum(i => i.Size) == top.VisibleBytes, "Aggregate lost area");
        var exact = ViewQuery.Build(new(large, ViewMode.LargestFiles, ExtensionFilter: ".TXT"), CancellationToken.None);
        Check(exact.Rows.Count == 450 && exact.Rows.All(i => i.Entry!.Extension == ".txt"), "Exact type filter mismatch");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { ViewQuery.Build(new(large, ViewMode.FileTypes), cancellation.Token); throw new Exception("Query cancellation ignored"); } catch (OperationCanceledException) { }
    });
    Test("100k-file query benchmark and deterministic top-file results", () =>
    {
        var large = new DiskEntry(Path.Combine(temp, "benchmark"), true);
        var random = new Random(4721);
        string[] extensions = [".jpg", ".bin", ".txt", ".cs", ".mp4"];
        for (int i = 0; i < 100000; i++) large.MutableChildren.Add(new DiskEntry(Path.Combine(large.Path, $"file-{i:000000}" + extensions[i % 5]), false, large) { Size = random.Next(0, 1000000), FileCount = 1 });
        var timer = System.Diagnostics.Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var baseline = large.Descendants().Where(e => !e.IsDirectory).OrderByDescending(e => e.Size).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Take(500).ToList();
        long baselineBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        double baselineMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart(); before = GC.GetAllocatedBytesForCurrentThread();
        var optimized = ViewQuery.Build(new(large, ViewMode.LargestFiles), CancellationToken.None);
        long queryBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"BENCH 100k top files: baseline {baselineMs:F1} ms / {baselineBytes:N0} B; optimized {timer.Elapsed.TotalMilliseconds:F1} ms / {queryBytes:N0} B");
        Check(optimized.Rows.Select(i => i.Entry).SequenceEqual(baseline), "Top results differ from full-sort reference");
        before = GC.GetAllocatedBytesForCurrentThread();
        var grouped = large.Descendants().Where(e => !e.IsDirectory).GroupBy(e => e.Extension).Select(g => (g.Key, Bytes: g.Sum(e => e.Size), Count: g.LongCount())).ToList();
        long groupBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
        var optimizedTypes = ViewQuery.Build(new(large, ViewMode.FileTypes), CancellationToken.None);
        long typeBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"BENCH 100k types: baseline {groupBytes:N0} B; optimized {timer.Elapsed.TotalMilliseconds:F1} ms / {typeBytes:N0} B");
        Check(optimizedTypes.Rows.All(i => grouped.Any(g => g.Key == i.Name && g.Bytes == i.Size && g.Count == i.Count)), "Type totals differ from reference");
        Check(optimizedTypes.Rows.Sum(i => i.Count) == 100000, "Lost type entries");
    });
    Test("refresh restores a surviving directory and rejects deleted/outside/file paths", () =>
    {
        Check(result!.Root.FindDirectory(root) == result.Root, "Root resolution mismatch");
        Check(result.Root.FindDirectory(Path.Combine(root, "代码"))?.Name == "代码", "Nested refresh location lost");
        Check(result.Root.FindDirectory(Path.Combine(root, "removed")) == null, "Missing directory accepted");
        Check(result.Root.FindDirectory(temp) == null, "Outside directory accepted");
        Check(result.Root.FindDirectory(Path.Combine(root, "零字节.txt")) == null, "File accepted as directory");
    });
    Test("type drilldown supports extensionless/zero-byte files and combined search", () =>
    {
        var emptyFile = ViewQuery.Build(new(result!.Root, ViewMode.LargestFiles, ExtensionFilter: ".txt"), CancellationToken.None);
        Check(emptyFile.Rows.Count == 1 && emptyFile.Rows[0].Size == 0 && emptyFile.MapItems.Count == 0, "Zero-byte type drilldown mismatch");
        var extensionless = ViewQuery.Build(new(result.Root, ViewMode.LargestFiles, ExtensionFilter: "无扩展名"), CancellationToken.None);
        Check(extensionless.Rows.Count == 1 && extensionless.Rows[0].Name == "无扩展名", "Extensionless drilldown mismatch");
        var combined = ViewQuery.Build(new(result.Root, ViewMode.LargestFiles, "photo", ExtensionFilter: ".cs"), CancellationToken.None);
        Check(combined.Rows.Count == 0 && combined.MatchedCount == 0, "Combined exact type/name query mismatch");
    });
    Test("type count and file location sorting", () =>
    {
        var scope = new DiskEntry(Path.Combine(temp, "sort"), true);
        foreach (string folderName in new[] { "B", "A" })
        {
            var directory = new DiskEntry(Path.Combine(scope.Path, folderName), true, scope); scope.MutableChildren.Add(directory);
            for (int i = 0; i < 3; i++) directory.MutableChildren.Add(new DiskEntry(Path.Combine(directory.Path, $"file{i}" + (i == 0 ? ".cs" : ".txt")), false, directory) { Size = 50, FileCount = 1 });
        }
        var count = ViewQuery.Build(new(scope, ViewMode.FileTypes, Sort: ViewSort.Count), CancellationToken.None);
        Check(count.Rows[0].Name == ".txt" && count.Rows[0].Count == 4 && count.Rows[1].Count == 2, "Count sort mismatch");
        var location = ViewQuery.Build(new(scope, ViewMode.LargestFiles, Sort: ViewSort.Location, Ascending: true), CancellationToken.None);
        Check(location.Rows.Take(3).All(i => i.Entry!.Parent!.Name == "A"), "Location sort mismatch");
    });
    Test("equal-size top-file boundary is stable across enumeration order", () =>
    {
        var scope = new DiskEntry(Path.Combine(temp, "ties"), true);
        for (int i = 0; i < 600; i++) scope.MutableChildren.Add(new DiskEntry(Path.Combine(scope.Path, $"{i:000}.bin"), false, scope) { Size = 100, FileCount = 1 });
        var first = ViewQuery.Build(new(scope, ViewMode.LargestFiles), CancellationToken.None);
        scope.MutableChildren.Reverse();
        var second = ViewQuery.Build(new(scope, ViewMode.LargestFiles), CancellationToken.None);
        Check(first.Rows.Select(i => i.Entry).SequenceEqual(second.Rows.Select(i => i.Entry)) && first.Rows[^1].Name == "499.bin", "Unstable equal-size boundary");
    });
    Test("type-map aggregation preserves totals and is explicitly marked", () =>
    {
        var scope = new DiskEntry(Path.Combine(temp, "many-types"), true);
        for (int i = 0; i < 270; i++) scope.MutableChildren.Add(new DiskEntry(Path.Combine(scope.Path, "file.type" + i), false, scope) { Size = i + 1, FileCount = 1 });
        var groups = ViewQuery.Build(new(scope, ViewMode.FileTypes), CancellationToken.None);
        Check(groups.Rows.Count == 270 && groups.MapItems.Count == 241 && groups.MapItems[^1].IsAggregate && groups.MapItems[^1].Count == 30, "Type aggregation marker mismatch");
        Check(groups.MapItems.Sum(i => i.Size) == groups.VisibleBytes, "Type aggregation area lost");
    });
    Test("queued directory replaced by a junction is rechecked before traversal", () =>
    {
        string scope = Path.Combine(temp, "replacement"), child = Path.Combine(scope, "child");
        Directory.CreateDirectory(child); File.WriteAllBytes(Path.Combine(scope, "root.bin"), new byte[99]);
        bool replaced = false;
        try
        {
            // Force queue order so replacement occurs before this directory's validation.
            var changed = new DiskScanner(1).Scan(scope, new InlineProgress(p =>
            {
                if (p.CurrentPath == scope && !replaced) Thread.Sleep(120);
                else if (p.CurrentPath == child && !replaced)
                {
                    Directory.Delete(child);
                    var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (string arg in new[] { "/c", "mklink", "/J", child, scope }) start.ArgumentList.Add(arg);
                    using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit();
                    Check(process.ExitCode == 0, "Replacement junction failed"); replaced = true;
                }
            }), CancellationToken.None);
            Check(replaced && changed.Root.Size == 99 && changed.Root.FileCount == 1 && changed.Skipped == 1, "Replaced directory was followed");
        }
        finally { if (replaced) Directory.Delete(child); }
    });
    Test("pagination covers every row and clamps boundary requests", () =>
    {
        var first = PageSlice.Create(1005, -10);
        var last = PageSlice.Create(1005, 99);
        Check(first.Index == 0 && first.Offset == 0 && first.Length == 1000 && first.Pages == 2, "First-page boundary mismatch");
        Check(last.Index == 1 && last.Offset == 1000 && last.Length == 5, "Last-page boundary mismatch");
        Check(PageSlice.Create(0, 9).Length == 0 && PageSlice.Create(1000, 1).Pages == 1, "Empty/exact page boundary mismatch");
        Check(PageSlice.Create(int.MaxValue, int.MaxValue).Length > 0, "Large page arithmetic overflow");
        for (int total = 0; total < 10000; total += 199)
        {
            int pages = PageSlice.Create(total, 0).Pages;
            var indices = Enumerable.Range(0, pages).SelectMany(p => { var slice = PageSlice.Create(total, p); return Enumerable.Range(slice.Offset, slice.Length); });
            Check(indices.SequenceEqual(Enumerable.Range(0, total)), "Pagination loses or repeats rows");
        }
    });
    Test("parallel and serial scans match every entry and file timestamp", () =>
    {
        string scope = Path.Combine(temp, "parallel");
        for (int d = 0; d < 72; d++)
        {
            string folder = Path.Combine(scope, "目录-" + d, "nested");
            Directory.CreateDirectory(folder);
            for (int f = 0; f < 24; f++)
            {
                string file = Path.Combine(folder, $"文件-{f}.bin");
                File.WriteAllBytes(file, new byte[(d * 7 + f * 11) % 701]);
                File.SetLastWriteTimeUtc(file, new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddMinutes(f));
            }
        }
        // Fix directory timestamps too; filesystem creation updates may otherwise settle between scans.
        foreach (string directory in Directory.EnumerateDirectories(scope, "*", SearchOption.AllDirectories).Append(scope))
            Directory.SetLastWriteTimeUtc(directory, new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        var serial = new DiskScanner(1).Scan(scope, null, CancellationToken.None);
        var reference = serial.Root.Descendants().Prepend(serial.Root).OrderBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => (e.Path, e.Name, e.IsDirectory, e.Size, e.FileCount, e.FolderCount, e.Modified, e.Incomplete, e.Extension)).ToArray();
        for (int repeat = 0; repeat < 4; repeat++)
        {
            ScanProgress? latest = null;
            var parallel = new DiskScanner(repeat % 2 == 0 ? 8 : 16).Scan(scope, new InlineProgress(p =>
            {
                if (latest != null) Check(p.Files >= latest.Files && p.Folders >= latest.Folders && p.Bytes >= latest.Bytes, "Progress went backwards");
                latest = p;
            }), CancellationToken.None);
            var actual = parallel.Root.Descendants().Prepend(parallel.Root).OrderBy(e => e.Path, StringComparer.Ordinal)
                .Select(e => (e.Path, e.Name, e.IsDirectory, e.Size, e.FileCount, e.FolderCount, e.Modified, e.Incomplete, e.Extension)).ToArray();
            Check(reference.SequenceEqual(actual), "Parallel metadata/totals differ from serial: " +
                string.Join("; ", reference.Zip(actual).Where(pair => pair.First != pair.Second).Take(3).Select(pair => $"expected {pair.First}; actual {pair.Second}; timestamp ticks {pair.First.Modified.Ticks}/{pair.Second.Modified.Ticks}")));
            Check(parallel.Root.FileCount == 1728 && parallel.Root.FolderCount == 144, "Lost or duplicated entries");
            Check(latest != null && latest.Bytes == parallel.Root.Size && latest.Files == 1728 && latest.Folders == 144, "Final parallel progress mismatch");
            foreach (var directory in parallel.Root.Descendants().Prepend(parallel.Root).Where(e => e.IsDirectory))
                Check(directory.Children.Select(e => e.Size).SequenceEqual(directory.Children.Select(e => e.Size).OrderDescending()), "Parallel children unsorted");
        }
        foreach (var file in serial.Root.Descendants().Where(e => !e.IsDirectory))
            Check(file.Modified == File.GetLastWriteTime(file.Path), "Enumeration timestamp differs from FileInfo");
    });
    Test("cancelling active parallel workers stops without leaving a blocked queue", () =>
    {
        string scope = Path.Combine(temp, "parallel");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var reachedWorker = new ManualResetEventSlim();
        var task = Task.Run(() =>
        {
            try
            {
                new DiskScanner().Scan(scope, new InlineProgress(p =>
                {
                    // Root is also a pool worker. Wait for published work, rather than assuming
                    // a child-specific progress report survives the 100 ms throttle.
                    if (p.CurrentPath == scope && p.Folders == 0) Thread.Sleep(120);
                    else
                    {
                        reachedWorker.Set();
                        cancellation.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                    }
                }), cancellation.Token);
                throw new Exception("Active worker cancellation ignored");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        });
        Check(reachedWorker.Wait(TimeSpan.FromSeconds(5)), "No worker reached cancellation checkpoint");
        cancellation.Cancel();
        Check(task.Wait(TimeSpan.FromSeconds(3)), "Parallel cancellation left workers blocked");
        // A new scan after cancellation must be independent of abandoned state.
        Check(new DiskScanner().Scan(scope, null, CancellationToken.None).Root.FileCount == 1728, "Subsequent scan corrupted");
    });
    Test("queued directory disappearance records an issue and preserves siblings", () =>
    {
        string scope = Path.Combine(temp, "parallel-missing");
        for (int d = 0; d < 32; d++)
        {
            string child = Path.Combine(scope, "child-" + d);
            Directory.CreateDirectory(child); File.WriteAllBytes(Path.Combine(child, "data.bin"), new byte[19]);
        }
        string? removed = null;
        // Other tests exercise concurrency; this fault injection needs deterministic start timing.
        var changed = new DiskScanner(1).Scan(scope, new InlineProgress(p =>
        {
            if (p.CurrentPath == scope && removed == null) Thread.Sleep(120);
            else if (p.CurrentPath != scope && removed == null)
            {
                removed = p.CurrentPath;
                File.Delete(Path.Combine(removed, "data.bin")); Directory.Delete(removed);
            }
        }), CancellationToken.None);
        Check(removed != null && changed.Skipped == 1 && changed.Issues.Count == 1 && changed.Issues[0].Path == removed, "Missing-directory issue lost");
        Check(changed.Root.FileCount == 31 && changed.Root.Size == 31 * 19 && changed.Root.Incomplete, "Sibling totals or incomplete flag lost");
    });
    Test("parallel workers count all skipped junctions exactly once", () =>
    {
        string scope = Path.Combine(temp, "parallel-links");
        var links = new List<string>();
        try
        {
            for (int d = 0; d < 16; d++)
            {
                string child = Path.Combine(scope, "child-" + d); Directory.CreateDirectory(child);
                File.WriteAllBytes(Path.Combine(child, "data.bin"), new byte[23]);
                string link = Path.Combine(child, "loop");
                var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in new[] { "/c", "mklink", "/J", link, scope }) start.ArgumentList.Add(arg);
                using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit();
                Check(process.ExitCode == 0, "Parallel test junction creation failed"); links.Add(link);
            }
            for (int repeat = 0; repeat < 3; repeat++)
            {
                var live = new LiveScanSession();
                var linked = new DiskScanner().Scan(scope, null, CancellationToken.None, live);
                Check(linked.Root.FileCount == 16 && linked.Root.Size == 368 && linked.Root.FolderCount == 16, "Parallel link traversal or missing file");
                Check(linked.Skipped == 16 && linked.Issues.Count == 16 && linked.Issues.Select(i => i.Path).Distinct().Count() == 16 && linked.Root.Incomplete, "Concurrent issue recording mismatch");
                var preview = live.Capture(null, CancellationToken.None)!;
                Check(preview.Skipped == 16 && preview.Root.Incomplete && preview.Root.Size == 368, "Live skip/incomplete statistics lost");
            }
            try { new DiskScanner().Scan(links[0], null, CancellationToken.None); throw new Exception("Root junction accepted"); }
            catch (IOException) { }
        }
        finally { foreach (string link in links) Directory.Delete(link); }
    });
    Test("worker exceptions propagate without deadlocking other readers", () =>
    {
        string scope = Path.Combine(temp, "parallel");
        int failed = 0;
        var task = Task.Run(() =>
        {
            try
            {
                new DiskScanner().Scan(scope, new InlineProgress(p =>
                {
                    if (p.CurrentPath == scope && p.Folders == 0) Thread.Sleep(120);
                    else if (Interlocked.Exchange(ref failed, 1) == 0) throw new InvalidOperationException("test-worker-failure");
                }), CancellationToken.None);
                throw new Exception("Expected worker failure");
            }
            catch (InvalidOperationException ex) when (ex.Message == "test-worker-failure") { }
        });
        Check(task.Wait(TimeSpan.FromSeconds(5)) && failed == 1, "Worker fault left the queue blocked");
    });
    Test("scan concurrency bounds are explicit", () =>
    {
        foreach (int count in new[] { -1, 0, 17 })
        {
            try { _ = new DiskScanner(count); throw new Exception("Invalid worker count accepted"); }
            catch (ArgumentOutOfRangeException) { }
        }
    });
    Test("file nodes share an empty collection and scanner paths/extension storage", () =>
    {
        var source = new DiskScanner().Scan(root, null, CancellationToken.None).Root;
        var files = source.Descendants().Where(e => !e.IsDirectory).ToList();
        var listField = typeof(DiskEntry).GetField("children", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var pathField = typeof(DiskEntry).GetField("storedPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (var file in files)
        {
            Check(file.Children.Count == 0 && listField.GetValue(file) == null, "File allocated an empty child list");
            Check(pathField.GetValue(file) == null && file.Path == Path.Combine(file.Parent!.Path, file.Name), "File retains a full path or lost its path");
        }
        Check(ReferenceEquals(files[0].Children, files[1].Children), "Empty child collections are not shared");
        var many = new DiskScanner().Scan(Path.Combine(temp, "parallel"), null, CancellationToken.None).Root.Descendants().Where(e => !e.IsDirectory).ToList();
        Check(many.All(e => ReferenceEquals(e.Extension, many[0].Extension)), "Repeated extensions were not shared");
        var sameParent = many.Where(e => e.Parent == many[0].Parent).Take(2).ToArray();
        _ = DiskEntry.ComparePath(sameParent[0], sameParent[1]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) _ = DiskEntry.ComparePath(sameParent[0], sameParent[1]);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Same-directory comparison allocated paths");
        for (int i = 1; i < many.Count; i += 101)
            Check(Math.Sign(DiskEntry.ComparePath(many[0], many[i])) == Math.Sign(StringComparer.OrdinalIgnoreCase.Compare(many[0].Path, many[i].Path)), "Path comparison changed order");
        _ = DiskEntry.ComparePath(many[0], many[^1]);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) _ = DiskEntry.ComparePath(many[0], many[^1]);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Cross-directory comparison allocated paths");
    });
    Test("preview appears before completion and held snapshots never mutate", () =>
    {
        string scope = Path.Combine(temp, "live-flat"); Directory.CreateDirectory(scope);
        for (int i = 0; i < 1105; i++) File.WriteAllBytes(Path.Combine(scope, $"item-{i:0000}.txt"), new byte[i % 23]);
        var live = new LiveScanSession(); ScanPreview? first = null;
        var final = new DiskScanner(1).Scan(scope, new InlineProgress(p =>
        {
            if (p.Files == 0) Thread.Sleep(120);
            else if (first == null && p.Files < 1105) first = live.Capture(null, CancellationToken.None);
        }), CancellationToken.None, live);
        Check(first != null && first.Directory.FileCount > 0 && first.Directory.FileCount < final.Root.FileCount, "No partial result before completion");
        int heldCount = first!.Directory.Children.Count; long heldBytes = first.Directory.Size;
        var completed = live.Capture(null, CancellationToken.None)!;
        Check(completed.Truncated && completed.DiscoveredChildren == 1105 && completed.Directory.Children.Count == 1000, "Preview limit/truncation mismatch");
        Check(completed.Directory.Size == final.Root.Size && completed.Directory.FileCount == 1105, "Completed preview totals mismatch");
        Check(first.Directory.Children.Count == heldCount && first.Directory.Size == heldBytes, "Published snapshot was mutated");
        Check(!ReferenceEquals(completed.Directory, final.Root) && !ReferenceEquals(completed.Directory.Children[0], final.Root.Children[0]), "Preview exposes mutable scan entries");
    });
    Test("preview supports nested browsing, parent paths and rejects outside paths", () =>
    {
        string scope = Path.Combine(temp, "parallel"); var live = new LiveScanSession();
        var final = new DiskScanner().Scan(scope, null, CancellationToken.None, live);
        string nested = Path.Combine(scope, "目录-5", "nested");
        var preview = live.Capture(nested, CancellationToken.None)!;
        Check(preview.Directory.Path == nested && preview.Directory.Parent!.Name == "目录-5" && preview.Root.Path == scope, "Preview ancestry mismatch");
        Check(preview.Directory.Children.Count == 24 && preview.Directory.FileCount == 24 && !preview.Truncated, "Nested preview lost files");
        Check(preview.Directory.Children.All(c => c.Parent == preview.Directory && File.Exists(c.Path)), "Preview file paths broken");
        Check(preview.Root.Size == final.Root.Size && preview.Directory.Size == final.Root.FindDirectory(nested)!.Size, "Nested snapshot totals differ");
        Check(live.Capture(temp, CancellationToken.None) == null && live.Capture(Path.Combine(scope, "missing"), CancellationToken.None) == null, "Invalid preview path accepted");
        Check(live.Capture(preview.Directory.Children[0].Path, CancellationToken.None) == null, "File accepted as preview directory");
    });
    Test("parallel scanning and preview queries safely run together", () =>
    {
        string scope = Path.Combine(temp, "parallel"); var live = new LiveScanSession();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var attached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var scanTask = Task.Run(() => new DiskScanner().Scan(scope, new InlineProgress(p =>
        {
            if (p.Files == 0 && p.Folders == 0) { attached.Set(); Check(release.Wait(TimeSpan.FromSeconds(5)), "Preview test gate timed out"); }
        }), stop.Token, live));
        Check(attached.Wait(TimeSpan.FromSeconds(5)), "Scanner did not attach preview root");
        int snapshots = 0;
        try
        {
            release.Set();
            do
            {
                var snapshot = live.Capture(null, stop.Token)!;
                var query = ViewQuery.Build(new(snapshot.Directory, ViewMode.Folder), stop.Token);
                Check(query.Rows.Count <= 1000 && snapshot.Directory.Children.All(c => c.Parent == snapshot.Directory), "Unsafe/big preview");
                Check(query.VisibleBytes <= snapshot.Directory.Size, "Preview area exceeds displayed total");
                snapshots++;
            } while (!scanTask.IsCompleted && snapshots < 200);
            var result = scanTask.GetAwaiter().GetResult();
            Check(snapshots > 0 && result.Root.FileCount == 1728 && result.Root.FolderCount == 144, "Concurrent snapshots corrupted scan");
        }
        finally { release.Set(); stop.Cancel(); }
    });
    Test("preview cancellation and empty sessions have explicit behavior", () =>
    {
        var live = new LiveScanSession(); Check(live.Capture(null, CancellationToken.None) == null, "Unattached session produced a snapshot");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        try { live.Capture(null, stop.Token); throw new Exception("Preview cancellation ignored"); } catch (OperationCanceledException) { }
        string scope = Path.Combine(root, "图片", "空目录");
        new DiskScanner().Scan(scope, null, CancellationToken.None, live);
        var empty = live.Capture(null, CancellationToken.None)!;
        Check(empty.Directory.Size == 0 && empty.DiscoveredChildren == 0 && !empty.Truncated, "Empty preview mismatch");
        try { new DiskScanner().Scan(scope, null, CancellationToken.None, live); throw new Exception("Live session reused silently"); } catch (InvalidOperationException) { }
    });
    Test("allocation-free path ordering matches full paths across prefixes and Unicode", () =>
    {
        var scope = new DiskEntry(Path.Combine(temp, "path-order"), true);
        var entries = new List<DiskEntry>();
        foreach (string directoryName in new[] { "A", "A-child", "A0", "aZ", "中文", "𐐀", "𐐨", "目录" })
        {
            var directory = DiskEntry.FromName(directoryName, true, scope, "文件夹");
            entries.Add(directory);
            foreach (string name in new[] { "a.txt", "A.txt", "中文.txt", "𐐀.txt", "𐐨.txt", "z" })
                entries.Add(DiskEntry.FromName(name, false, directory, DiskEntry.ExtensionOf(name)));
        }
        foreach (var a in entries)
        foreach (var b in entries)
            Check(Math.Sign(DiskEntry.ComparePath(a, b)) == Math.Sign(StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path)), "Prefix/Unicode path ordering differs from the full path");
    });
    Console.WriteLine($"\nAll {passed} tests passed.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
finally
{
    string tempBase = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(temp).StartsWith(tempBase + "DiskAtlas-tests-", StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe cleanup target");
    Directory.Delete(temp, true);
}

sealed class InlineProgress(Action<ScanProgress> action) : IProgress<ScanProgress>
{
    public void Report(ScanProgress value) => action(value);
}

