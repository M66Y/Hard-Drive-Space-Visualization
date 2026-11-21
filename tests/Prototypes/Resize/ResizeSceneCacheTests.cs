using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DiskAtlas.ResizePrototype;

// Fixed synthetic images and memory drawing only. No Form, screen capture,
// filesystem, scan, or system-setting change; exact pixels are not a claim
// about native resize scheduling or the physical display.
internal static class ResizeSceneCacheTests
{
    internal static int Run(bool dpi96 = false)
    {
        int checks = 0, comparisons = 0;
        long comparedPixels = 0, differingPixels = 0;
        string failure = "None";
        int dpi = dpi96 ? 96 : 144;
        var clock = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(36));
        var worker = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var background = Pattern(127, 83);
                using var icon = Pattern(29, 31);
                using var renderer = new ResizeSceneRenderer();
                using (var cache = new ResizeSceneBaseCache())
                {
                    Size Scale(int width, int height) => new(width * dpi / 96, height * dpi / 96);
                    foreach (Size size in new[] { Scale(1440, 940), Scale(1120, 780) })
                    foreach (var selection in new[] { (View: 0, Selected: 0, Hover: 1),
                        (View: 0, Selected: -1, Hover: -1), (View: 1, Selected: 5, Hover: 6),
                        (View: 2, Selected: 100, Hover: 101) })
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        var state = new ResizeSceneState
                        {
                            View = selection.View, Selected = selection.Selected, Hover = selection.Hover,
                            Progress = .43, Metric = 1, Revision = 7
                        };
                        var layout = ResizeSceneLayout.Build(size, dpi, state);
                        Check(cache.TryGet(renderer, layout, state, background, icon, out var baseImage) && baseImage != null,
                            "StaticBaseAvailable");
                        Check(baseImage!.Size == size && cache.CachedSize == size &&
                            Math.Abs(baseImage.HorizontalResolution - dpi) < .5 &&
                            cache.RetainedBytes == (long)size.Width * size.Height * 4 &&
                            cache.RetainedBytes <= ResizeSceneBaseCache.MaxPixels * 4,
                            "SingleExactGeometryAndDpiWithinCapacity");
                        foreach (double seconds in new[] { 0d, .817d })
                        {
                            deadline.Token.ThrowIfCancellationRequested();
                            using var reference = Canvas(size, dpi);
                            using var composed = Canvas(size, dpi);
                            using (var graphics = Graphics.FromImage(reference))
                                renderer.Render(graphics, layout, state, background, icon, seconds);
                            using (var graphics = Graphics.FromImage(composed))
                            {
                                graphics.CompositingMode = CompositingMode.SourceCopy;
                                graphics.DrawImageUnscaled(baseImage!, Point.Empty);
                                graphics.CompositingMode = CompositingMode.SourceOver;
                                renderer.RenderEdgeAnimation(graphics, layout, seconds);
                            }
                            long differing = DifferentPixels(reference, composed);
                            differingPixels += differing;
                            comparedPixels += (long)size.Width * size.Height;
                            comparisons++;
                            Check(differing == 0, "FullAndSplitRenderExactRgbaPixels");
                        }
                    }
                }
                KeyAndOwnershipChecks(renderer, background, icon, dpi, Check, deadline.Token);
            }
            catch (CheckFailure error) { failure = error.Code; }
            catch (OperationCanceledException) { failure = "MemoryDrawingDeadline"; }
            catch (Exception error) { failure = "UnexpectedMemoryDrawing_" + error.HResult.ToString("X8"); }
        }) { IsBackground = true, Name = "ResizeSceneCacheChecks" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        bool completed = worker.Join(TimeSpan.FromSeconds(39));
        if (!completed)
        {
            deadline.Cancel();
            completed = worker.Join(TimeSpan.FromSeconds(2));
            failure = "MemoryDrawingWorkerDeadline";
        }
        Console.WriteLine(FormattableString.Invariant(
            $"CACHE checks={checks}; exact_comparisons={comparisons}; rgba_pixels_compared={comparedPixels}; differing_rgba_pixels={differingPixels}; synthetic_dpi={dpi}; elapsed_ms={clock.Elapsed.TotalMilliseconds:F1}; worker_finished={completed}; visual_acceptance=False"));
        if (failure != "None") { Console.Error.WriteLine("FAIL resize scene cache code=" + failure); return 1; }
        Console.WriteLine("PASS resize scene cache; synthetic memory drawing only; zero_tolerance=True");
        return 0;

        void Check(bool condition, string code)
        {
            if (!condition) throw new CheckFailure(code);
            checks++;
        }
    }

    private static void KeyAndOwnershipChecks(ResizeSceneRenderer renderer, Bitmap background, Bitmap icon, int dpi,
        Action<bool, string> check, CancellationToken token)
    {
        using var cache = new ResizeSceneBaseCache();
        var state = new ResizeSceneState { Selected = 0, Hover = 1, Revision = 10 };
        var size = new Size(640, 480);
        var layout = ResizeSceneLayout.Build(size, dpi, state);
        check(cache.TryGet(renderer, layout, state, background, icon, out var first) && first != null,
            "InitialKeyBuild");
        check(cache.Builds == 1 && cache.Misses == 1 && cache.Hits == 0, "InitialBuildCounters");
        check(cache.TryGet(renderer, ResizeSceneLayout.Build(size, dpi, state), state, background, icon, out var hit) &&
            ReferenceEquals(first, hit) && cache.Builds == 1 && cache.Hits == 1,
            "ValueEquivalentLayoutReturnsSameBorrowedBitmap");
        check(!IsDisposed(first!), "BorrowRemainsOwnedAndAliveBetweenHits");

        token.ThrowIfCancellationRequested();
        size = new Size(641, 480);
        layout = ResizeSceneLayout.Build(size, dpi, state);
        check(cache.TryGet(renderer, layout, state, background, icon, out var resized) && resized != null &&
            !ReferenceEquals(first, resized) && IsDisposed(first!) && resized.Size == size,
            "SizeMissReleasesOldBorrowWithoutStretching");
        check(cache.RetainedBytes == (long)size.Width * size.Height * 4, "OnlyCurrentSizeRetained");

        state.Revision++;
        layout = ResizeSceneLayout.Build(size, dpi, state);
        check(cache.TryGet(renderer, layout, state, background, icon, out var revised) && revised != null &&
            !ReferenceEquals(resized, revised) && IsDisposed(resized!), "RevisionInvalidatesAndReleases");

        // A caller is not allowed to rely solely on revision discipline: the
        // captured state itself must distinguish another scene at that revision.
        state.Search = "demo";
        layout = ResizeSceneLayout.Build(size, dpi, state);
        check(cache.TryGet(renderer, layout, state, background, icon, out var changedState) && changedState != null &&
            !ReferenceEquals(revised, changedState) && IsDisposed(revised!), "StateChangeAtSameRevisionInvalidates");

        using var replacementBackground = new Bitmap(background);
        check(cache.TryGet(renderer, layout, state, replacementBackground, icon, out var changedBackground) &&
            changedBackground != null && !ReferenceEquals(changedState, changedBackground) && IsDisposed(changedState!),
            "BackgroundReferenceChangeInvalidates");
        using var replacementIcon = new Bitmap(icon);
        check(cache.TryGet(renderer, layout, state, replacementBackground, replacementIcon, out var changedIcon) &&
            changedIcon != null && !ReferenceEquals(changedBackground, changedIcon) && IsDisposed(changedBackground!),
            "IconReferenceChangeInvalidates");

        int otherDpi = dpi == 96 ? 144 : 96;
        var otherLayout = ResizeSceneLayout.Build(size, otherDpi, state);
        check(cache.TryGet(renderer, otherLayout, state, replacementBackground, replacementIcon, out var changedDpi) &&
            changedDpi != null && !ReferenceEquals(changedIcon, changedDpi) && IsDisposed(changedIcon!) &&
            Math.Abs(changedDpi.HorizontalResolution - otherDpi) < .5,
            "DpiChangeRebuildsRatherThanResampling");

        cache.Invalidate();
        check(cache.RetainedBytes == 0 && cache.CachedSize == Size.Empty && IsDisposed(changedDpi!),
            "ExplicitInvalidationRetiresBorrowedImage");
        check(cache.TryGet(renderer, layout, state, replacementBackground, replacementIcon, out var beforeOversize) &&
            beforeOversize != null, "ValidSceneRebuildsAfterInvalidate");
        token.ThrowIfCancellationRequested();
        var oversized = ResizeSceneLayout.Build(new Size(4001, 2000), dpi, state);
        long rejections = cache.Rejections;
        check(!cache.TryGet(renderer, oversized, state, replacementBackground, replacementIcon, out var rejected) &&
            rejected == null && cache.Rejections == rejections + 1, "OverEightMillionPixelsRejected");
        check(cache.RetainedBytes == 0 && cache.CachedSize == Size.Empty && IsDisposed(beforeOversize!),
            "OversizeMissReleasesOldImageWithoutKeepingFallback");

        check(cache.TryGet(renderer, layout, state, replacementBackground, replacementIcon, out var lastBorrow) &&
            lastBorrow != null, "CacheCanRecoverAfterOversizeRejection");
        cache.Dispose();
        check(cache.RetainedBytes == 0 && cache.CachedSize == Size.Empty && IsDisposed(lastBorrow!),
            "DisposeReleasesCurrentBorrow");
        cache.Dispose();
        bool disposedRejected = false;
        try { cache.TryGet(renderer, layout, state, replacementBackground, replacementIcon, out _); }
        catch (ObjectDisposedException) { disposedRejected = true; }
        check(disposedRejected, "DisposedCacheCannotReturnAnOldImage");
        check(!IsDisposed(background) && !IsDisposed(icon) && !IsDisposed(replacementBackground) && !IsDisposed(replacementIcon),
            "CacheNeverDisposesCallerOwnedSources");
    }

    private static bool IsDisposed(Bitmap bitmap)
    {
        try { _ = bitmap.GetPixel(0, 0); return false; }
        catch (ArgumentException) { return true; }
        catch (ObjectDisposedException) { return true; }
    }
    private static Bitmap Canvas(Size size, int dpi)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(dpi, dpi);
        return bitmap;
    }
    private static Bitmap Pattern(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            bitmap.SetPixel(x, y, Color.FromArgb(90 + x % 150, 45 + y % 180, 35 + (x * 7 + y * 3) % 190));
        return bitmap;
    }
    private static long DifferentPixels(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) throw new CheckFailure("ComparisonSizeMismatch");
        var bounds = new Rectangle(Point.Empty, first.Size);
        var a = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var b = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var left = new byte[first.Width * 4]; var right = new byte[left.Length];
                long differing = 0;
                for (int y = 0; y < first.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(a.Scan0, y * a.Stride), left, 0, left.Length);
                    Marshal.Copy(IntPtr.Add(b.Scan0, y * b.Stride), right, 0, right.Length);
                    if (left.AsSpan().SequenceEqual(right)) continue;
                    for (int x = 0; x < left.Length; x += 4)
                        if (!left.AsSpan(x, 4).SequenceEqual(right.AsSpan(x, 4))) differing++;
                }
                return differing;
            }
            finally { second.UnlockBits(b); }
        }
        finally { first.UnlockBits(a); }
    }
    private sealed class CheckFailure(string code) : Exception("ResizeSceneCache")
    {
        internal string Code { get; } = code;
    }
}
