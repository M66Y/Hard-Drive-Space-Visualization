using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DiskAtlas.ResizePrototype;

// Own memory DCs, synthetic images and an owned prototype only. These checks
// do not sample the desktop or alter diagnostic sampling/visual acceptance.
internal static class ResizeCoverageTests
{
    private static readonly Color Sentinel = Color.FromArgb(19, 47, 71);

    internal static int Run(bool dpi96 = false, bool nativeDib = false)
    {
        int checks = 0;
        long pixelsChecked = 0;
        string failure = "None";
        bool cleanupDeferred = false;
        Action? close = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var worker = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                DrawingChecks(Check, ref pixelsChecked);
                using var form = new LiveResizePrototypeForm(ResizeNativeMode.Canvas) { ShowInTaskbar = false };
                Image? originalImage = form.BackgroundImage;
                form.EnableCoverageCandidate(nativeDib);
                var hook = typeof(LiveResizePrototypeForm).GetField("coverageHook", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(form) as ResizeRootCoverageHook ?? throw new CheckFailure("CoverageHookUnavailable");
                close = () =>
                {
                    try { if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke((Action)(() => form.Close())); }
                    catch (InvalidOperationException) { }
                };
                form.Shown += async (_, _) =>
                {
                    try { await CompatibilityChecks(form, hook, originalImage, Check, timeout.Token); }
                    catch (CheckFailure error) { failure = error.Code; }
                    catch (OperationCanceledException) { failure = "CoverageWindowDeadline"; }
                    catch { failure = "UnexpectedCoverageWindowFailure"; }
                    finally { if (!form.IsDisposed) form.Close(); }
                };
                Application.Run(form);
                Check(form.IsDisposed && form.BufferBytes == 0, "CoveragePrototypeDisposed");
                Check(hook.Handle == IntPtr.Zero, "CoverageNativeHookReleased");
            }
            catch (CheckFailure error) { failure = error.Code; }
            catch { failure = "UnexpectedCoverageWorkerFailure"; }
        }) { IsBackground = true, Name = "ResizeCoverageChecks" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(54)))
        {
            timeout.Cancel(); close?.Invoke();
            cleanupDeferred = !worker.Join(TimeSpan.FromSeconds(2));
            failure = "CoverageWorkerDeadline";
        }
        Console.WriteLine($"COVERAGE checks={checks}; rgb_pixels_checked={pixelsChecked}; native_dib={nativeDib}; cleanup_deferred={cleanupDeferred.ToString().ToLowerInvariant()}; desktop_acceptance=separate");
        if (failure != "None") { Console.Error.WriteLine($"FAIL resize coverage: {failure}"); return 1; }
        Console.WriteLine("PASS resize coverage; synthetic memory DC and owned prototype; no scan or system changes");
        return 0;

        void Check(bool condition, string code)
        {
            if (!condition) throw new CheckFailure(code);
            checks++;
        }
    }

    private static void DrawingChecks(Action<bool, string> check, ref long pixelsChecked)
    {
        var native = new Size(320, 240);
        var old = new Size(128, 96);
        using var wallpaper = CreateWallpaper();
        using var coverage = new ResizeBackgroundCoverage();
        check(ResizeBackgroundCoverage.MaxPixels == 8_000_000, "CoverageBudgetIsExplicit");
        check(coverage.OwnedBytes == 0, "CoverageDoesNotOwnAnotherFullFrame");
        foreach (int dpi in new[] { 96, 144 })
        foreach (var view in new[] { BackgroundView.Default, new BackgroundView(.2f, .7f, 1.6f, null) })
        {
            using var reference = BackgroundReference(native, dpi, wallpaper, view, 0);
            using var actual = NewTarget(native, dpi);
            WithDc(actual, dc => check(coverage.TryPaint(dc, native, wallpaper, view), "FullNativeBackgroundCovered"));
            CompareRgb(actual, reference, (_, _) => true, check, "FullCoverageUsesRealWallpaperAtNativeGeometry", ref pixelsChecked);
            check(Rgb(actual.GetPixel(native.Width - 8, native.Height - 8)) != Rgb(Theme.Surface), "NewCornerIsNotWhitePlaceholder");

            using var exposed = NewTarget(native, dpi);
            WithDc(exposed, dc => check(coverage.TryPaint(dc, native, wallpaper, view, previousClient: old), "ExposedNativeRegionCovered"));
            CompareRgb(exposed, reference, (x, y) => x >= old.Width || y >= old.Height, check,
                "ExposedRegionsUseNewWallpaperCoordinatesAndPreserveOldContent", ref pixelsChecked);
            check(coverage.OwnedBytes == 0, "RepeatedCoverageKeepsZeroRasterOwnership");

            using var washedReference = BackgroundReference(native, dpi, wallpaper, view, 93);
            using var washed = NewTarget(native, dpi);
            WithDc(washed, dc => check(coverage.TryPaint(dc, native, wallpaper, view, 93), "NativeWashAccepted"));
            CompareRgb(washed, washedReference, (_, _) => true, check, "NativeWashRetainsWallpaperMapping", ref pixelsChecked);
        }

        using (var clipped = NewTarget(native, 96))
        using (var reference = BackgroundReference(native, 96, wallpaper, BackgroundView.Default, 0))
        {
            IntPtr mask = CreateRectRgn(0, 0, 31, native.Height);
            IntPtr bottom = CreateRectRgn(0, native.Height - 47, native.Width, native.Height);
            IntPtr right = CreateRectRgn(native.Width - 61, 0, native.Width, native.Height);
            IntPtr hole = CreateRectRgn(native.Width - 43, native.Height - 32, native.Width - 12, native.Height - 8);
            IntPtr before = CreateRectRgn(0, 0, 0, 0), after = CreateRectRgn(0, 0, 0, 0);
            try
            {
                check(mask != IntPtr.Zero && bottom != IntPtr.Zero && right != IntPtr.Zero && hole != IntPtr.Zero &&
                    before != IntPtr.Zero && after != IntPtr.Zero, "OwnedClipRegionsCreated");
                CombineRgn(mask, mask, bottom, 2); CombineRgn(mask, mask, right, 2); CombineRgn(mask, mask, hole, 4);
                WithDc(clipped, dc =>
                {
                    check(SelectClipRgn(dc, mask) > 0 && GetClipRgn(dc, before) == 1, "ExternalComplexClipInstalled");
                    bool viewportReadable = GetViewportOrgEx(dc, out var viewport);
                    bool windowReadable = GetWindowOrgEx(dc, out var window);
                    check(viewportReadable && windowReadable, "ExternalDcOriginsReadable");
                    int stackBefore = SaveDC(dc); check(stackBefore > 0 && RestoreDC(dc, stackBefore), "ExternalDcStackReadable");
                    for (int i = 0; i < 12; i++)
                        check(coverage.TryPaint(dc, native, wallpaper, previousClient: old), "ComplexClippedCoverageAccepted");
                    check(GetClipRgn(dc, after) == 1 && EqualRgn(before, after), "ExternalDcClipPreservedExactly");
                    check(GetViewportOrgEx(dc, out var nextViewport) && GetWindowOrgEx(dc, out var nextWindow) &&
                        viewport.Equals(nextViewport) && window.Equals(nextWindow), "ExternalDcOriginsPreserved");
                    int stackAfter = SaveDC(dc); check(stackAfter == stackBefore && RestoreDC(dc, stackAfter), "RepeatedCoverageBalancesDcStateStack");
                });
                CompareRgb(clipped, reference, (x, y) => PtInRegion(mask, x, y) && (x >= old.Width || y >= old.Height),
                    check, "ExternalComplexClipAndOldContentRemainIntact", ref pixelsChecked);
            }
            finally
            {
                foreach (IntPtr region in new[] { mask, bottom, right, hole, before, after })
                    if (region != IntPtr.Zero) DeleteObject(region);
            }
        }

        using (var rejected = NewTarget(native, 96))
        {
            WithDc(rejected, dc =>
            {
                check(!coverage.TryPaint(dc, Size.Empty, wallpaper), "EmptyNativeSizeRejected");
                check(!coverage.TryPaint(dc, new Size(-1, 24), wallpaper), "NegativeNativeSizeRejected");
                check(!coverage.TryPaint(dc, new Size(4001, 2000), wallpaper), "NativePixelBudgetEnforced");
                check(!coverage.TryPaint(dc, new Size(32769, 1), wallpaper), "NativeSideBudgetEnforced");
                check(!coverage.TryPaint(dc, native, wallpaper, wash: -1) && !coverage.TryPaint(dc, native, wallpaper, wash: 256),
                    "InvalidWashRejected");
                check(!coverage.TryPaint(dc, native, wallpaper, previousClient: native), "NoNewRegionNotReportedAsPainted");
                check(!coverage.TryPaint(dc, native, wallpaper, previousClient: new Size(640, 480)), "ShrinkNotReportedAsNewCoverage");
                check(!coverage.TryPaint(IntPtr.Zero, native, wallpaper), "MissingDcRejected");
            });
            CompareRgb(rejected, rejected, (_, _) => false, check, "RejectedCoverageDoesNotChangeCallerPixels", ref pixelsChecked);
        }

        using (var edge = NewTarget(native, 96))
            WithDc(edge, dc => check(coverage.TryPaint(dc, new Size(4000, 2000), wallpaper), "ExactNativePixelBudgetAcceptedWithoutExtraRaster"));
        check(coverage.OwnedBytes == 0, "BudgetEdgeDoesNotAllocateFullNativeFrame");

        var state = new ResizeSceneState { View = 1, Selected = 9, Hover = 12, Scroll = 3, Search = "sample", Metric = 1, Progress = .4, Revision = 31 };
        var layout = ResizeSceneLayout.Build(new Size(1440, 940), 96, state);
        using (var target = NewTarget(native, 96)) WithDc(target, dc => coverage.TryPaint(dc, native, wallpaper));
        var unchanged = ResizeSceneLayout.Build(layout.PixelSize, layout.Dpi, state);
        check(layout.State == unchanged.State && layout.Search == unchanged.Search && layout.MapPanel == unchanged.MapPanel &&
            layout.ListPanel == unchanged.ListPanel && layout.Rows.SequenceEqual(unchanged.Rows) &&
            layout.Tiles.Select(tile => tile.Bounds).SequenceEqual(unchanged.Tiles.Select(tile => tile.Bounds)),
            "BackgroundCoverageDoesNotMutateLayoutOrViewState");
        coverage.Dispose(); coverage.Dispose();
        check(coverage.OwnedBytes == 0, "CoverageDisposalLeavesNoOwnedRaster");
        check(wallpaper.GetPixel(2, 2).ToArgb() == Color.FromArgb(178, 61, 38).ToArgb(), "CallerWallpaperRemainsOwnedByCaller");
        bool disposedRejected = false;
        try { coverage.TryPaint(IntPtr.Zero, native, wallpaper); }
        catch (ObjectDisposedException) { disposedRejected = true; }
        check(disposedRejected, "DisposedCoverageCannotPaint");
    }

    private static async Task CompatibilityChecks(LiveResizePrototypeForm form, ResizeRootCoverageHook hook,
        Image? originalImage, Action<bool, string> check, CancellationToken token)
    {
        await Wait(() => form.FrameCount > 0 && form.PresentedLayout?.PixelSize == form.ClientSize, token);
        check(form.Controls.Count == 0, "CoverageCanvasKeepsSingleManagedSurface");
        foreach (var mode in new[] { ResizeNativeMode.Canvas, ResizeNativeMode.All })
        {
            int beforeFrame = form.FrameCount;
            form.SetMode(mode);
            await Wait(() => form.FrameCount > beforeFrame && form.PresentedLayout?.PixelSize == form.ClientSize, token);
            check(form.Controls.Count == (mode == ResizeNativeMode.Canvas ? 0 : 4), "CoverageDoesNotReplaceNativeControls");
            check(ReferenceEquals(form.BackgroundImage, originalImage), "CoverageDoesNotReplaceRealBackgroundImage");
            var ordinary = ResizeSceneLayout.Build(form.ClientSize, form.DeviceDpi, form.SceneState);
            check(SameGeometry(ordinary, form.PresentedLayout!), "CoveragePreservesCurrentLayoutRules");
            form.SetWindowSize(true);
            await Wait(() => form.PresentedLayout?.PixelSize == form.ClientSize, token);
            int beforePaints = hook.PaintCount;
            int preparedBefore = (int)typeof(LiveResizePrototypeForm).GetField("preparedPresents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            form.SetWindowSize(false);
            await Wait(() => form.PresentedLayout?.PixelSize == form.ClientSize && hook.PaintCount > beforePaints, token);
            check(hook.Failures == 0, "NativeExpansionCoverageHasNoHookFailures");
            check((int)typeof(LiveResizePrototypeForm).GetField("preparationCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)! > 0,
                "NewGeometryScenePreparedBeforeNativeCommit");
            check((int)typeof(LiveResizePrototypeForm).GetField("preparedPresents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)! > preparedBefore,
                "CompletePreparedScenePresentedAtExpansion");
            check((int)typeof(LiveResizePrototypeForm).GetField("preparationFailures", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)! == 0,
                "PreparedSceneHasNoHiddenFailures");
            check(SameGeometry(ResizeSceneLayout.Build(form.ClientSize, form.DeviceDpi, form.SceneState), form.PresentedLayout!),
                "NativeExpansionKeepsUnifiedGeometry");
            if (mode == ResizeNativeMode.All)
            {
                var search = form.Controls.OfType<TextBox>().Single();
                search.Text = "__coverage_no_match__";
                await Wait(() => form.PresentedLayout?.Data.Count == 0, token);
                check(form.SceneState.Search == search.Text, "CoverageKeepsNativeTextBinding");
                search.Text = "";
                await Wait(() => form.PresentedLayout?.Data.Count > 0, token);
                var metric = form.Controls.OfType<ComboBox>().Single(control => control.Name == "prototypeMetric");
                metric.SelectedIndex = 1;
                await Wait(() => form.PresentedLayout?.State.Metric == 1, token);
                check(form.SceneState.Metric == 1, "CoverageKeepsNativeMetricBinding");
                var grid = form.Controls.OfType<DataGridView>().Single();
                check(grid.RowCount == form.PresentedLayout!.Data.Count && grid.Visible && grid.IsHandleCreated,
                    "CoverageKeepsNativeGridDataAndHandle");
            }
        }
    }

    private static bool SameGeometry(ResizeSceneLayout first, ResizeSceneLayout second) => first.PixelSize == second.PixelSize &&
        first.Dpi == second.Dpi && first.State == second.State && first.Sidebar == second.Sidebar &&
        first.MapPanel == second.MapPanel && first.MapHeader == second.MapHeader && first.MapBody == second.MapBody &&
        first.ListPanel == second.ListPanel && first.ListHeader == second.ListHeader && first.ListBody == second.ListBody &&
        first.Search == second.Search && first.Strategy == second.Strategy && first.Metric == second.Metric &&
        first.Details == second.Details && first.ProgressBounds == second.ProgressBounds &&
        first.Toolbar.SequenceEqual(second.Toolbar) && first.Stats.SequenceEqual(second.Stats) && first.Rows.SequenceEqual(second.Rows);

    private static Bitmap NewTarget(Size size, int dpi)
    {
        var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        result.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(result); graphics.Clear(Sentinel);
        return result;
    }

    private static Bitmap CreateWallpaper()
    {
        var result = new Bitmap(64, 48, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        using var a = new SolidBrush(Color.FromArgb(178, 61, 38));
        using var b = new SolidBrush(Color.FromArgb(42, 128, 83));
        using var c = new SolidBrush(Color.FromArgb(57, 86, 173));
        using var d = new SolidBrush(Color.FromArgb(224, 150, 43));
        graphics.FillRectangle(a, 0, 0, 32, 24); graphics.FillRectangle(b, 32, 0, 32, 24);
        graphics.FillRectangle(c, 0, 24, 32, 24); graphics.FillRectangle(d, 32, 24, 32, 24);
        return result;
    }

    private static Bitmap BackgroundReference(Size size, int dpi, Image wallpaper, BackgroundView view, int wash)
    {
        var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb); image.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Theme.Background); graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.DrawImage(wallpaper, Wallpaper.CoverBounds(wallpaper, size, Point.Empty, view));
        if (wash != 0)
        { using var veil = new SolidBrush(Color.FromArgb(wash, Theme.Surface)); graphics.FillRectangle(veil, new Rectangle(Point.Empty, size)); }
        return image;
    }

    private static void WithDc(Bitmap target, Action<IntPtr> action)
    {
        using var graphics = Graphics.FromImage(target);
        IntPtr dc = graphics.GetHdc();
        try { action(dc); }
        finally { graphics.ReleaseHdc(dc); }
    }

    private static void CompareRgb(Bitmap actual, Bitmap reference, Func<int, int, bool> expectedPainted,
        Action<bool, string> check, string code, ref long pixelsChecked)
    {
        long mismatches = 0;
        var bounds = new Rectangle(Point.Empty, actual.Size);
        var a = actual.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData? b = null;
        try
        {
            if (!ReferenceEquals(actual, reference)) b = reference.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var actualRow = new byte[actual.Width * 4]; var expectedRow = new byte[actualRow.Length];
            for (int y = 0; y < actual.Height; y++)
            {
                Marshal.Copy(a.Scan0 + y * a.Stride, actualRow, 0, actualRow.Length);
                if (b != null) Marshal.Copy(b.Scan0 + y * b.Stride, expectedRow, 0, expectedRow.Length);
                for (int x = 0; x < actual.Width; x++)
                {
                    int offset = x * 4;
                    int wanted = expectedPainted(x, y) ? expectedRow[offset] | expectedRow[offset + 1] << 8 | expectedRow[offset + 2] << 16 : Rgb(Sentinel);
                    int observed = actualRow[offset] | actualRow[offset + 1] << 8 | actualRow[offset + 2] << 16;
                    if (observed != wanted) mismatches++;
                    pixelsChecked++;
                }
            }
        }
        finally { actual.UnlockBits(a); if (b != null) reference.UnlockBits(b); }
        if (mismatches != 0) Console.WriteLine($"COVERAGE mismatch_code={code}; rgb_mismatch_pixels={mismatches}");
        check(mismatches == 0, code);
    }

    private static int Rgb(Color color) => color.ToArgb() & 0x00ffffff;
    private static async Task Wait(Func<bool> predicate, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(8)) throw new CheckFailure("CoverageExpectedFrameDeadline");
            await Task.Delay(12, token);
        }
    }

    private sealed class CheckFailure(string code) : Exception { internal string Code { get; } = code; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);
    [DllImport("gdi32.dll")] private static extern int SelectClipRgn(IntPtr dc, IntPtr region);
    [DllImport("gdi32.dll")] private static extern int GetClipRgn(IntPtr dc, IntPtr region);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool EqualRgn(IntPtr first, IntPtr second);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr dc);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool RestoreDC(IntPtr dc, int saved);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetViewportOrgEx(IntPtr dc, out NativePoint point);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowOrgEx(IntPtr dc, out NativePoint point);
}
