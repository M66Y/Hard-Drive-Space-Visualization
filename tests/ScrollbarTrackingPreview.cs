using DiskAtlas;
using System.Runtime.InteropServices;

// Only an owned synthetic window is sampled. This is not a screen recording.
internal static class ScrollbarTrackingPreview
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo
    {
        internal uint Size, Flags;
        internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal int Left, Top, Right, Bottom;
    }
    internal static int Run(bool dpi96)
    {
        Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var image = new Bitmap(128, 96);
        using (var g = Graphics.FromImage(image))
        {
            g.Clear(Color.FromArgb(247, 208, 171));
            using var ink = new SolidBrush(Color.FromArgb(154, 183, 171));
            for (int x = 0; x < image.Width; x += 24) g.FillRectangle(ink, x, 0, 8, image.Height);
        }
        using var form = new Form { Text = "AsukaLens · 滚动条拖动核验（合成列表）", ClientSize = new(760, 570),
            StartPosition = FormStartPosition.CenterScreen, BackgroundImage = image };
        using var grid = new WallpaperGrid { Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false,
            VirtualMode = true, ReadOnly = true, RowTemplate = { Height = 48 } };
        grid.Columns.Add("name", "Name"); grid.Columns.Add("size", "Size");
        grid.Columns[0].Width = 700; grid.Columns[1].Width = 230;
        grid.CellValueNeeded += (_, e) => e.Value = e.ColumnIndex == 0 ? "Synthetic item " + e.RowIndex : "17 B";
        grid.RowCount = 100; form.Controls.Add(grid);
        using var lifetime = new CancellationTokenSource();
        Task? sampler = null;
        int samples = 0, captured = 0, changed = 0, moving = 0;
        int lastRow = 0; grid.Scroll += (_, _) => { Interlocked.Exchange(ref lastRow, grid.FirstDisplayedScrollingRowIndex); Interlocked.Increment(ref moving); };
        var references = new Dictionary<IntPtr, (Point[] Points, uint[] Pixels)>();
        form.Shown += (_, _) =>
        {
            grid.Invalidate(true); grid.Update();
            foreach (var bar in grid.Controls.OfType<ScrollBar>().Where(b => b.Visible))
            { bar.Invalidate(); bar.Update(); var points = Points(bar); references[bar.Handle] = (points, Read(bar.Handle, points)); }
            uint threadId = GetWindowThreadProcessId(form.Handle, out _);
            Console.WriteLine("READY owned synthetic scrollbar tracking; no personal data; drag vertical/horizontal thumb; stdin close");
            sampler = Task.Run(() =>
            {
                while (!lifetime.IsCancellationRequested && samples < 30_000)
                {
                    Interlocked.Increment(ref samples);
                    var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
                    if (GetGUIThreadInfo(threadId, ref info) && references.TryGetValue(info.Capture, out var sample))
                    {
                        Interlocked.Increment(ref captured);
                        uint[] actual = Read(info.Capture, sample.Points);
                        if (actual.Zip(sample.Pixels).Count(pair => pair.First != pair.Second) > actual.Length / 3)
                            Interlocked.Increment(ref changed);
                    }
                    Thread.Sleep(5);
                }
            });
            _ = Task.Run(() =>
            {
                while (Console.ReadLine() is { } command)
                    if (command == "close") { if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke(form.Close); break; }
            });
        };
        form.FormClosing += (_, _) => { lifetime.Cancel(); sampler?.Wait(TimeSpan.FromSeconds(2)); };
        Application.Run(form); form.BackgroundImage = null;
        Console.WriteLine($"OBSERVE owned scrollbar captured_samples={captured}; rail_changed_samples={changed}; moving_events={moving}; total_samples={samples}; final_row={lastRow}");
        return 0;
    }
    private static Point[] Points(ScrollBar bar)
    {
        if (!GridScrollbarSkin.TryGetGeometry(bar, out var geometry)) return [];
        bool vertical = bar is VScrollBar;
        int start = vertical ? geometry.StartArrow.Bottom : geometry.StartArrow.Right;
        int end = vertical ? geometry.EndArrow.Top : geometry.EndArrow.Left;
        return Enumerable.Range(start + 3, Math.Max(1, end - start - 6)).Where(at => at % 7 == 0)
            .Select(at => new Point(vertical ? 0 : at, vertical ? at : 0)).ToArray();
    }
    private static uint[] Read(IntPtr window, Point[] points)
    {
        IntPtr dc = GetDC(window);
        try { return points.Select(p => GetPixel(dc,p.X,p.Y)).ToArray(); }
        finally { if (dc != IntPtr.Zero) ReleaseDC(window,dc); }
    }
}
