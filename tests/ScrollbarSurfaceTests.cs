using System.Reflection;
using System.Runtime.InteropServices;
using DiskAtlas;

// Uses only owned controls and bitmaps. Native input and accessibility remain
// on the original scrollbar; these pixels are not desktop-frame evidence.
internal static class ScrollbarSurfaceTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    internal static int Run(bool dpi96 = false)
    {
        int passed = 0;
        string failure = "None";
        var worker = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using var background = Pattern();
                using var form = new Form { ClientSize = new Size(700, 460), BackgroundImage = background, ShowInTaskbar = false };
                using var grid = new WallpaperGrid { Dock = DockStyle.Fill, AllowUserToAddRows = false,
                    RowHeadersVisible = false, VirtualMode = true, ReadOnly = true };
                grid.Columns.Add("name", "Name"); grid.Columns.Add("size", "Size");
                foreach (DataGridViewColumn column in grid.Columns) column.Width = 550;
                grid.CellValueNeeded += (_, e) => e.Value = e.ColumnIndex == 0 ? "Owned synthetic item " + e.RowIndex : "17 B";
                grid.RowCount = 80; form.Controls.Add(grid); form.Show(); Application.DoEvents();
                grid.Invalidate(true); grid.Update();
                var bars = grid.Controls.OfType<ScrollBar>().Where(bar => bar.Visible).ToArray();
                Check(bars.Length == 2 && bars.Any(bar => bar is VScrollBar) && bars.Any(bar => bar is HScrollBar), "BothNativeBarsAvailable");
                Check(grid.SkinnedScrollbarCount == 2 && bars.All(bar => bar.AccessibilityObject.Role == AccessibleRole.ScrollBar),
                    "OriginalNativeBarsAndAccessibilityRetained");
                int builds = grid.BackdropBuilds;
                foreach (var bar in bars)
                {
                    Check(GridScrollbarSkin.TryGetGeometry(bar, out var geometry), "NativeGeometryAvailable");
                    using var expected = new Bitmap(bar.Width, bar.Height);
                    using (var graphics = Graphics.FromImage(expected)) grid.PaintScrollbarBackdrop(bar, graphics);
                    using var painted = new Bitmap(bar.Width, bar.Height);
                    using (var graphics = Graphics.FromImage(painted))
                        Check(GridScrollbarSkin.PaintBar(grid, bar, graphics, false), "TransparentSkinPainted");
                    bool vertical = bar is VScrollBar;
                    // Outside the slim thumb/arrow ink, both rail edges must
                    // be exactly the corresponding fixed grid background.
                    int railMatches = 0;
                    for (int at = geometry.StartArrow.Bottom + 2; vertical && at < geometry.EndArrow.Top - 2; at++)
                        if (painted.GetPixel(0, at) == expected.GetPixel(0, at)) railMatches++;
                    for (int at = geometry.StartArrow.Right + 2; !vertical && at < geometry.EndArrow.Left - 2; at++)
                        if (painted.GetPixel(at, 0) == expected.GetPixel(at, 0)) railMatches++;
                    Check(railMatches >= 10, "RailUsesFixedBackdropWithoutGreyTrack");
                    var thumb = geometry.Thumb;
                    if (thumb.Width > 0 && thumb.Height > 0)
                    {
                        var point = new Point(thumb.Left + thumb.Width / 2, thumb.Top + thumb.Height / 2);
                        Check(painted.GetPixel(point.X, point.Y) != expected.GetPixel(point.X, point.Y), "NativeThumbRemainsVisible");
                    }
                    using var contrast = new Bitmap(bar.Width, bar.Height);
                    using (var graphics = Graphics.FromImage(contrast))
                    {
                        graphics.Clear(Color.Magenta);
                        Check(!GridScrollbarSkin.PaintBar(grid, bar, graphics, true) && contrast.GetPixel(0, 0).ToArgb() == Color.Magenta.ToArgb(),
                            "HighContrastDelegatesWithoutChangingSurface");
                    }
                    // The real WM_PRINTCLIENT hook must draw the same skin as
                    // its off-screen renderer, not the old native grey rail.
                    using var printed = new Bitmap(bar.Width, bar.Height);
                    using (var graphics = Graphics.FromImage(printed))
                    {
                        IntPtr dc = graphics.GetHdc();
                        try { SendMessage(bar.Handle, 0x0318, dc, new IntPtr(0x0004)); }
                        finally { graphics.ReleaseHdc(dc); }
                    }
                    Check(printed.GetPixel(0, Math.Min(bar.Height - 1, 3)).ToArgb() == expected.GetPixel(0, Math.Min(bar.Height - 1, 3)).ToArgb(),
                        "PrintClientUsesBackdropSkin");
                }
                Check(grid.BackdropBuilds == builds, "BothBarsReuseTheGridBackdrop");
                var corner = grid.ScrollbarCornerBounds;
                Check(corner.Width > 1 && corner.Height > 1, "BothAxesExposeNativeResizeCorner");
                using (var expectedGrid = new Bitmap(grid.Width, grid.Height))
                using (var paintedGrid = new Bitmap(grid.Width, grid.Height))
                {
                    using (var graphics = Graphics.FromImage(expectedGrid))
                        typeof(WallpaperGrid).GetMethod("PaintBackground", Hidden, null,
                            [typeof(Graphics), typeof(Rectangle), typeof(Rectangle)], null)!.Invoke(grid,
                            [graphics, corner, corner]);
                    grid.DrawToBitmap(paintedGrid, grid.ClientRectangle);
                    bool same = true;
                    for (int y = corner.Top; same && y < corner.Bottom; y++)
                        for (int x = corner.Left; x < corner.Right; x++)
                            if (expectedGrid.GetPixel(x, y).ToArgb() != paintedGrid.GetPixel(x, y).ToArgb()) { same = false; break; }
                    Check(same, "TwoAxisCornerUsesFixedBackdropWithoutSystemGreyPatch");
                }
                int oldRow = grid.FirstDisplayedScrollingRowIndex;
                typeof(WallpaperGrid).GetMethod("OnMouseWheel", Hidden)!.Invoke(grid,
                    [new HandledMouseEventArgs(MouseButtons.None, 0, grid.Width / 2, 80, -120)]);
                Check(grid.FirstDisplayedScrollingRowIndex > oldRow && grid.BackdropBuilds == builds, "WheelKeepsNativeMovementAndCacheReuse");
                int oldOffset = grid.HorizontalScrollingOffset;
                grid.PerformBufferedScroll(() => grid.HorizontalScrollingOffset = 40);
                Check(grid.HorizontalScrollingOffset > oldOffset && grid.BackdropBuilds == builds, "HorizontalMovementKeepsCacheReuse");
                var verticalBar = bars.OfType<VScrollBar>().Single();
                typeof(Control).GetMethod("RecreateHandle", Hidden)!.Invoke(verticalBar, null); Application.DoEvents();
                Check(verticalBar.IsHandleCreated && grid.SkinnedScrollbarCount == 2, "RecreatedHandleKeepsOneSkinPerNativeBar");
                using (var image = new Bitmap(verticalBar.Width, verticalBar.Height))
                using (var graphics = Graphics.FromImage(image))
                    Check(GridScrollbarSkin.PaintBar(grid, verticalBar, graphics, false), "RecreatedHandleHasValidGeometryAndPaint");
                grid.Hide(); Check(!grid.Visible, "SkinDoesNotForceHiddenGridVisible"); grid.Show(); Application.DoEvents();
                grid.Dispose(); Check(grid.SkinnedScrollbarCount == 0 && grid.BackdropBytes == 0, "DisposalReleasesSkinHooksAndBackdrop");
                form.BackgroundImage = null; form.Close();
            }
            catch (CheckFailure error) { failure = error.Code; }
            catch { failure = "OwnedScrollbarFixtureFailure"; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(25))) failure = "WorkerTimeout";
        if (failure != "None") { Console.Error.WriteLine("FAIL scrollbar surface code=" + failure + "; checks=" + passed); return 1; }
        Console.WriteLine("PASS scrollbar surface checks=" + passed + "; native_controls_retained=True; desktop_review_separate=True");
        return 0;

        void Check(bool condition, string code) { if (!condition) throw new CheckFailure(code); passed++; }
    }
    private static Bitmap Pattern()
    {
        var image = new Bitmap(80, 64);
        using var graphics = Graphics.FromImage(image); graphics.Clear(Color.FromArgb(245, 205, 180));
        using var brush = new SolidBrush(Color.FromArgb(110, 145, 165));
        for (int x = 0; x < image.Width; x += 16) graphics.FillRectangle(brush, x, 0, 5, image.Height);
        return image;
    }
    private sealed class CheckFailure(string code) : Exception("ScrollbarSurface") { internal string Code { get; } = code; }
}
