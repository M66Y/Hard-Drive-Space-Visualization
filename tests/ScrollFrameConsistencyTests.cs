using DiskAtlas;
using System.Reflection;
using System.Runtime.InteropServices;

// Observe the existing visible grid at Scroll entry, before its repair paint.
// No DrawToBitmap, forced paint or delay is allowed at this observation point.
internal static class ScrollFrameConsistencyTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
    internal static int Run(bool dpi96)
    {
        string? failure = null;
        int checks = 0, observed = 0, protectedEvents = 0, shiftedEvents = 0, compared = 0, finalMismatch = 0, deferredNotMoved = 0, maxChanged = 0;
        int nativeHolds = 0, trackMismatch = 0, prematureResume = 0;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                AppContext.SetSwitch("AsukaLens.TestComposition", true);
                using var form = new MainForm(null, false,
                    Path.Combine(Path.GetTempPath(), "AsukaLens-scroll-frame-unused-" + Guid.NewGuid().ToString("N")))
                { ShowInTaskbar = false };
                var grid = Field<WallpaperGrid>(form, "grid");
                form.Show(); Pump(60);
                var root = new DiskEntry(Path.Combine(Path.GetTempPath(), "AsukaLens-frame-synthetic"), true)
                    { FileCount = 160, Size = 160 * 1024 };
                var items = new List<MapItem>();
                for (int i = 0; i < 160; i++)
                {
                    var entry = new DiskEntry(Path.Combine(root.Path, "synthetic-" + i.ToString("000") + ".txt"), false, root)
                        { FileCount = 1, Size = 1024 };
                    root.MutableChildren.Add(entry); items.Add(new(entry.Name, entry.Size, entry, "文档"));
                }
                typeof(MainForm).GetField("current", Hidden)!.SetValue(form, root);
                typeof(MainForm).GetMethod("PopulateGrid", Hidden)!.Invoke(form, [items]);
                var pattern = Pattern();
                typeof(MainForm).GetMethod("ApplyBackground", Hidden)!.Invoke(form,
                    [pattern, BackgroundView.Default, Theme.DefaultAccent, true]);
                grid.CurrentCell = null; grid.ClearSelection(); grid.LocateAnimationEnabled = false;
                form.Invalidate(true); form.Update(); Pump(60);
                var bar = grid.Controls.OfType<VScrollBar>().Single(b => b.Visible);
                var row = grid.GetRowDisplayRectangle(grid.FirstDisplayedScrollingRowIndex, false);
                int right = bar.Left - 3;
                var points = new List<Point>();
                for (int y = row.Top + 4; y < grid.ClientSize.Height - 6; y += row.Height)
                    for (int x = 5; x < right; x += 11) points.Add(new(x, y));
                Check(points.Count > 100, "WholeViewportBackgroundSampleAvailable");
                uint[] expected = Read(grid, points);
                Check(expected.All(p => p != uint.MaxValue), "OwnedGridPixelsReadable");
                Check(GridScrollbarSkin.TryGetGeometry(bar, out var initialShape), "RailSampleGeometry");
                var railPoints = Enumerable.Range(initialShape.StartArrow.Bottom + 3,
                    initialShape.EndArrow.Top - initialShape.StartArrow.Bottom - 6).Where(y => y % 7 == 0).Select(y => new Point(0, y)).ToList();
                uint[] expectedRail = Read(bar, railPoints);
                int backdropBuilds = grid.BackdropBuilds;
                int paints = 0, paintsAtScroll = -1;
                grid.Paint += (_, _) => paints++;
                grid.PresentationResumed += (_, _) => { if (paints <= paintsAtScroll) prematureResume++; };
                string input = "none";
                grid.Scroll += (_, _) =>
                {
                    observed++;
                    paintsAtScroll = paints;
                    if (GetProp(grid.Handle, "SysSetRedraw") != IntPtr.Zero) nativeHolds++;
                    if (grid.ScrollbarPaintingPaused) { protectedEvents++; return; }
                    uint[] actual = Read(grid, points);
                    int changed = actual.Zip(expected).Count(p => Distance(p.First, p.Second) > 12);
                    maxChanged = Math.Max(maxChanged, changed);
                    compared += actual.Length;
                    if (changed > actual.Length / 10) shiftedEvents++;
                };
                void Final(string code)
                {
                    // Observe immediately on input return. Do not force another
                    // paint, which could hide delayed publication or tearing.
                    uint[] actual = Read(grid, points);
                    int changed = actual.Zip(expected).Count(p => Distance(p.First, p.Second) > 12);
                    if (changed > actual.Length / 10) finalMismatch++;
                    uint[] rail = Read(bar, railPoints);
                    if (rail.Zip(expectedRail).Any(p => Distance(p.First, p.Second) > 12)) trackMismatch++;
                    Check(grid.BackdropBuilds == backdropBuilds, code + "ReusesFixedBackdrop");
                }
                foreach (bool live in new[] { true, false })
                {
                    input = live ? "live" : "deferred";
                    using var pointer = new ScrollbarPointerTracking(grid, bar,
                        () => { bar.Invalidate(); bar.Update(); }, () => live);
                    for (int repeat = 0; repeat < 3; repeat++)
                    {
                        grid.PerformBufferedScroll(() => grid.FirstDisplayedScrollingRowIndex = 0);
                        Check(GridScrollbarSkin.TryGetGeometry(bar, out var shape), "PointerGeometry");
                        Point start = Center(shape.Thumb);
                        Message(pointer, bar, 0x201, start);
                        int rowBefore = grid.FirstDisplayedScrollingRowIndex;
                        int[] positions = [start.Y + 4, start.Y + 8, start.Y + 12, start.Y + 5, start.Y + 15,
                            shape.EndArrow.Top - 4, shape.StartArrow.Bottom + 5,
                            (shape.EndArrow.Top + shape.StartArrow.Bottom) / 2, shape.EndArrow.Top - 7];
                        foreach (int y in positions)
                        {
                            Message(pointer, bar, 0x200, new(start.X, y));
                            Final(input + "RapidMove");
                        }
                        if (!live) Check(grid.FirstDisplayedScrollingRowIndex == rowBefore, "DeferredKeepsRowsUntilRelease");
                        Message(pointer, bar, 0x202, new(start.X, positions[^1]));
                        Final(input + "Release");
                        if (grid.FirstDisplayedScrollingRowIndex <= rowBefore) deferredNotMoved++;
                        Check(!bar.Capture, "ReleaseDropsCapture");
                    }
                }
                input = "wheel";
                grid.PerformBufferedScroll(() => grid.FirstDisplayedScrollingRowIndex = 60);
                foreach (int delta in new[] { -120, -120, 120, 120, -120, 120 })
                {
                    typeof(WallpaperGrid).GetMethod("OnMouseWheel", Hidden)!.Invoke(grid,
                        [new HandledMouseEventArgs(MouseButtons.None, 0, grid.Width / 2, grid.ColumnHeadersHeight + 30, delta)]);
                    Final("SlowWheel"); Pump(45);
                }
                Check(observed > 10, "RealGridScrollEventsObserved");
                Check(shiftedEvents == 0, "NoIntermediateWholeBackgroundShift");
                Check(deferredNotMoved == 0, "DeferredReleaseMovesActualGridRows");
                Check(finalMismatch == 0, "InputReturnsWithWholeBackgroundRestored");
                Check(protectedEvents == observed, "EveryTestedScrollUsesPresentationBoundary");
                Check(nativeHolds == observed, "EveryScrollHasRealNativeRedrawProtection");
                Check(trackMismatch == 0, "TrackBackgroundStaysFixedAcrossEveryInput");
                Check(prematureResume == 0, "ViewportPaintPrecedesChildTrackPublication");
                form.Close();
            }
            catch (Failure error) { failure = error.Code; }
            catch (Exception error) { failure = "Unexpected_" + error.GetType().Name; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(35))) failure = "WorkerDeadline";
        Console.WriteLine($"OBSERVE scroll frames checks={checks}; scroll_events={observed}; protected={protectedEvents}; native_holds={nativeHolds}; shifted={shiftedEvents}; max_changed={maxChanged}; intermediate_pixels={compared}; final_mismatch={finalMismatch}; track_mismatch={trackMismatch}; premature_resume={prematureResume}; deferred_not_moved={deferredNotMoved}; dpi96={dpi96}");
        if (failure != null) { Console.Error.WriteLine("FAIL scroll frames code=" + failure); return 1; }
        Console.WriteLine("PASS scroll frames; owned_synthetic=True; continuous_desktop_review_separate=True"); return 0;
        void Check(bool condition, string code) { checks++; if (!condition) throw new Failure(code); }
    }
    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, Hidden)!.GetValue(form)!;
    private static Point Center(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);
    private static void Message(ScrollbarPointerTracking pointer, ScrollBar bar, int type, Point point)
    {
        var message = System.Windows.Forms.Message.Create(bar.Handle, type, new IntPtr(type == 0x202 ? 0 : 1),
            new IntPtr((point.X & 0xffff) | ((point.Y & 0xffff) << 16)));
        if (!pointer.Handle(ref message)) throw new Failure("PointerMessageNotHandled");
    }
    private static uint[] Read(Control owner, List<Point> points)
    {
        IntPtr dc = GetDC(owner.Handle);
        try { return points.Select(p => GetPixel(dc, p.X, p.Y)).ToArray(); }
        finally { if (dc != IntPtr.Zero) ReleaseDC(owner.Handle, dc); }
    }
    private static int Distance(uint a, uint b) => Math.Abs((int)(a & 255) - (int)(b & 255)) +
        Math.Abs((int)((a >> 8) & 255) - (int)((b >> 8) & 255)) + Math.Abs((int)((a >> 16) & 255) - (int)((b >> 16) & 255));
    private static Bitmap Pattern()
    {
        var bitmap = new Bitmap(211, 197);
        using var graphics = Graphics.FromImage(bitmap);
        for (int y = 0; y < bitmap.Height; y += 7)
        for (int x = 0; x < bitmap.Width; x += 9)
        {
            using var brush = new SolidBrush(Color.FromArgb((x * 13 + y * 7) % 256, (x * 3 + y * 17) % 256, (x * 19 + y * 5) % 256));
            graphics.FillRectangle(brush, x, y, 9, 7);
        }
        return bitmap;
    }
    private static void Pump(int ms) { var clock = System.Diagnostics.Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(5); } }
    private sealed class Failure(string code) : Exception { internal string Code => code; }
}
