using DiskAtlas;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

// Synthetic in-process drawing cost, not physical mouse-to-screen latency.
internal static class TreemapBenchmark
{
    public static int Run(string label)
    {
        if (label is not ("before" or "after")) throw new ArgumentException("Use before or after.");
        Exception? failure = null; var results = new List<object>();
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                using var form = new Form { ClientSize = new Size(1280, 760), BackgroundImage = Wallpaper.Load(), Text = "AsukaLens drawing benchmark" };
                using var map = new TreemapControl { Dock = DockStyle.Fill, AnimationsEnabled = false };
                form.Controls.Add(map); form.Show(); Application.DoEvents();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var move = typeof(TreemapControl).GetMethod("OnMouseMove", flags)!;
                var background = typeof(TreemapControl).GetMethod("OnPaintBackground", flags)!;
                var paint = typeof(TreemapControl).GetMethod("OnPaint", flags)!;
                foreach (bool nested in new[] { true, false })
                {
                    map.ShowNested = nested;
                    map.SetItems(CreateItems(nested)); Application.DoEvents();
                    var tiles = (List<MapTile>)typeof(TreemapControl).GetField("tiles", flags)!.GetValue(map)!;
                    var points = tiles.Select(t => Point.Round(new PointF(t.Bounds.X + t.Bounds.Width / 2, t.Bounds.Y + t.Bounds.Height / 2))).ToArray();
                    using var bitmap = new Bitmap(map.Width, map.Height); bitmap.SetResolution(map.DeviceDpi, map.DeviceDpi);
                    using var g = Graphics.FromImage(bitmap);
                    Rectangle dirty = Rectangle.Empty;
                    InvalidateEventHandler invalidated = (_, e) => dirty = dirty.IsEmpty ? e.InvalidRect : Rectangle.Union(dirty, e.InvalidRect);
                    map.Invalidated += invalidated;
                    var samples = new List<double>(); var areas = new List<double>();
                    long allocated = 0;
                    for (int cycle = 0; cycle < 6; cycle++)
                    {
                        foreach (var point in cycle % 2 == 0 ? points : points.Reverse())
                        {
                            dirty = Rectangle.Empty;
                            long bytes = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
                            move.Invoke(map, [new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0)]);
                            if (!dirty.IsEmpty)
                            {
                                var state = g.Save(); g.SetClip(dirty);
                                using var e = new PaintEventArgs(g, dirty);
                                background.Invoke(map, [e]); paint.Invoke(map, [e]);
                                g.Restore(state);
                            }
                            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                            if (cycle > 0)
                            {
                                allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
                                samples.Add(elapsed); areas.Add((double)dirty.Width * dirty.Height / (map.Width * map.Height));
                            }
                        }
                    }
                    map.Invalidated -= invalidated;
                    samples.Sort(); areas.Sort();
                    var result = new { shape = nested ? "12-folders-with-36-nested-tiles" : "241-flat-files", dpi = map.DeviceDpi,
                        width = map.Width, height = map.Height, samples = samples.Count,
                        median_ms = samples[samples.Count / 2], p95_ms = samples[(int)Math.Ceiling(samples.Count * .95) - 1],
                        allocated_bytes_per_event = allocated / samples.Count, median_dirty_fraction = areas[areas.Count / 2] };
                    results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
                }
                var image = form.BackgroundImage; form.BackgroundImage = null; image?.Dispose(); form.Close();
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new Exception("Drawing benchmark timed out.");
        if (failure != null) throw new Exception("Drawing benchmark failed.", failure);
        string output = Path.Combine("artifacts", $"treemap-{label}.json");
        if (File.Exists(output)) throw new IOException("Archive the previous benchmark first.");
        Directory.CreateDirectory("artifacts");
        File.WriteAllText(output, JsonSerializer.Serialize(new { label, method = "warm synthetic handler plus clipped offscreen GDI drawing, animations disabled; excludes OS presentation latency", results }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    internal static List<MapItem> CreateItems(bool nested)
    {
        var items = new List<MapItem>();
        for (int i = 0; i < (nested ? 12 : 241); i++)
        {
            DiskEntry? directory = null;
            if (nested)
            {
                directory = new DiskEntry(Path.GetFullPath($"synthetic-folder-{i:00}"), true);
                for (int j = 0; j < 36; j++)
                {
                    var child = DiskEntry.FromName($"sample-{j:00}.dat", false, directory, ".dat");
                    child.Size = 100; child.FileCount = 1; directory.MutableChildren.Add(child);
                }
                directory.Size = 3600;
            }
            items.Add(new MapItem($"sample-{i:000}", nested ? 3600 : 100, directory, "图片"));
        }
        return items;
    }
}
