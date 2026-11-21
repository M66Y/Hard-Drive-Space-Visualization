using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using DiskAtlas;

// Owned, hidden HWNDs and memory DCs only. The baseline is the framework's
// actual transparent SplitterPanel erase, not a copy of the candidate formula.
// Passing this comparison does not establish desktop resize smoothness.
internal static class SplitPanelBackgroundExperimentTests
{
    private static readonly Color Sentinel = Color.FromArgb(19, 237, 171);

    internal static int Run(bool dpi96 = false)
    {
        int checks = 0, comparisons = 0;
        long referenceParentBackgrounds = 0, referenceParentForegrounds = 0;
        bool frameworkTransparentFrame = false, frameworkEraseFrame = false;
        string failure = "None";
        var worker = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var image = Pattern();
                using var form = new Form { ClientSize = new Size(640, 480), ShowInTaskbar = false, BackgroundImage = image };
                using var split = new WallpaperSplitContainer { Bounds = new Rectangle(37, 29, 420, 280), SplitterWidth = 8 };
                form.Controls.Add(split);
                _ = form.Handle; _ = split.Handle; _ = split.Panel1.Handle; _ = split.Panel2.Handle;
                Check(!form.Visible && !split.Visible, "OwnedWindowRemainsHidden");
                Check(split.BackgroundExperimentMode == WallpaperSplitContainer.PanelBackgroundMode.Disabled,
                    "NormalDefaultIsDisabled");
                Check(split.GetPanelBackgroundCounts(1) == default && split.GetPanelBackgroundCounts(2) == default,
                    "DefaultHasNoObservation");
                using var rounded = new GraphicsPath();
                rounded.AddEllipse(0, 0, 300, 200);
                using var childClip = new Region(rounded);
                using var child = new Panel { Bounds = new Rectangle(4, 4, 80, 60), Region = childClip };
                split.Panel1.Controls.Add(child);
                Region? originalRegion = child.Region;

                split.Paint += (_, _) =>
                {
                    // Only fixed booleans are emitted; no stack text or paths.
                    foreach (var frame in new StackTrace().GetFrames())
                    {
                        string? name = frame.GetMethod()?.Name;
                        frameworkTransparentFrame |= name == "PaintTransparentBackground";
                        frameworkEraseFrame |= name == "WmEraseBkgnd";
                    }
                };
                foreach (var orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
                foreach (Size size in new[] { new Size(380, 250), new Size(420, 280) })
                {
                    split.Size = size;
                    split.Orientation = orientation;
                    split.SplitterDistance = (orientation == Orientation.Vertical ? size.Width : size.Height) / 2;
                    foreach (var panel in new[] { split.Panel1, split.Panel2 })
                    foreach (int clipKind in new[] { 0, 1, 2 })
                    {
                        int panelNumber = panel == split.Panel1 ? 1 : 2;
                        Rectangle bounds = panel.Bounds;
                        using var baseline = Canvas(panel.ClientSize);
                        using var candidate = Canvas(panel.ClientSize);
                        split.ConfigurePanelBackgroundExperiment(WallpaperSplitContainer.PanelBackgroundMode.Observe);
                        split.ResetPanelBackgroundCounts();
                        Check(Erase(panel, baseline, clipKind) != IntPtr.Zero, "BaselineEraseHandled");
                        var observed = split.GetPanelBackgroundCounts(panelNumber);
                        Check(observed.Erases == 1 && observed.ParentBackgrounds == 1 && observed.ParentForegrounds == 1 &&
                            observed.DirectPaints == 0 && observed.Fallbacks == 0,
                            "NativeEraseInvokesParentBackgroundAndForeground");
                        referenceParentBackgrounds += observed.ParentBackgrounds;
                        referenceParentForegrounds += observed.ParentForegrounds;
                        split.ConfigurePanelBackgroundExperiment(WallpaperSplitContainer.PanelBackgroundMode.DirectWallpaper);
                        split.ResetPanelBackgroundCounts();
                        Check(Erase(panel, candidate, clipKind) != IntPtr.Zero, "DirectEraseHandled");
                        var direct = split.GetPanelBackgroundCounts(panelNumber);
                        Check(direct.Erases == 1 && direct.DirectPaints == 1 && direct.ParentBackgrounds == 0 &&
                            direct.ParentForegrounds == 0 && direct.Fallbacks == 0,
                            "DirectEraseAvoidsBothParentPasses");
                        Check(Identical(baseline, candidate), "DirectWallpaperMatchesTransparentFrameworkPixels");
                        Check(candidate.GetPixel(candidate.Width / 2, candidate.Height / 2).ToArgb() != Sentinel.ToArgb(),
                            "CandidateActuallyPaintedWallpaper");
                        Check(panel.Bounds == bounds && panel.BackColor == Color.Transparent &&
                            ReferenceEquals(child.Region, originalRegion), "GeometryTransparencyAndChildClipUnchanged");
                        comparisons++;
                    }
                }

                // Opaque panels and local images must retain their standard
                // framework behavior instead of pretending they are wallpaper.
                foreach (bool localImage in new[] { false, true })
                {
                    var panel = split.Panel2;
                    panel.BackColor = localImage ? Color.Transparent : Color.CadetBlue;
                    panel.BackgroundImage = localImage ? image : null;
                    using var baseline = Canvas(panel.ClientSize);
                    using var fallback = Canvas(panel.ClientSize);
                    split.ConfigurePanelBackgroundExperiment(WallpaperSplitContainer.PanelBackgroundMode.Observe);
                    Erase(panel, baseline, 0);
                    split.ConfigurePanelBackgroundExperiment(WallpaperSplitContainer.PanelBackgroundMode.DirectWallpaper);
                    split.ResetPanelBackgroundCounts();
                    Erase(panel, fallback, 0);
                    Check(Identical(baseline, fallback), "UnsupportedPanelAppearanceFallsBackExactly");
                    Check(split.GetPanelBackgroundCounts(2) is { Erases: 1, DirectPaints: 0, Fallbacks: 1 },
                        "FallbackIsCounted");
                    panel.BackgroundImage = null; panel.BackColor = Color.Transparent;
                }

                // The observer follows an owned panel's handle replacement and
                // detaches completely when the experiment is disabled.
                typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(split.Panel1, null);
                split.ResetPanelBackgroundCounts();
                using (var afterRecreate = Canvas(split.Panel1.ClientSize))
                    Erase(split.Panel1, afterRecreate, 0);
                Check(split.GetPanelBackgroundCounts(1) is { Erases: 1, DirectPaints: 1 }, "HandleRecreationRetainsCandidate");
                split.ConfigurePanelBackgroundExperiment(WallpaperSplitContainer.PanelBackgroundMode.Disabled);
                split.ResetPanelBackgroundCounts();
                using (var disabled = Canvas(split.Panel1.ClientSize)) Erase(split.Panel1, disabled, 0);
                Check(split.GetPanelBackgroundCounts(1) == default, "DisableDetachesMessageObserver");
                split.ConfigurePanelBackgroundExperiment(WallpaperSplitContainer.PanelBackgroundMode.Observe);
                split.ResetPanelBackgroundCounts();
                using (var restored = Canvas(split.Panel1.ClientSize)) Erase(split.Panel1, restored, 0);
                Check(split.GetPanelBackgroundCounts(1) is { Erases: 1, ParentBackgrounds: 1, ParentForegrounds: 1, DirectPaints: 0 },
                    "OriginalFrameworkPathCanBeRestored");
                Check(frameworkTransparentFrame && frameworkEraseFrame, "FrameworkTransparentEraseStackObserved");
                split.Dispose();
                Check(split.BackgroundExperimentMode == WallpaperSplitContainer.PanelBackgroundMode.Disabled,
                    "DisposalDetachesObservers");
            }
            catch (Failure error) { failure = error.Code; }
            catch (Exception error) { failure = "OwnedComparisonFailure_" + error.HResult.ToString("X8"); }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        bool completed = worker.Join(TimeSpan.FromSeconds(24));
        if (!completed || failure != "None")
        {
            Console.Error.WriteLine($"FAIL split background code={(completed ? failure : "OwnedComparisonBudgetExceeded")}; checks={checks}; comparisons={comparisons}");
            return 1;
        }
        Console.WriteLine($"PASS split background checks={checks}; exact_pixel_comparisons={comparisons}; baseline_parent_backgrounds={referenceParentBackgrounds}; baseline_parent_foregrounds={referenceParentForegrounds}; framework_transparent_frame={frameworkTransparentFrame}; framework_erase_frame={frameworkEraseFrame}; owned_hidden_windows_only=True; visual_acceptance=False");
        return 0;

        void Check(bool condition, string code)
        {
            if (!condition) throw new Failure(code);
            checks++;
        }
    }

    private static Bitmap Pattern()
    {
        var image = new Bitmap(97, 73, PixelFormat.Format32bppPArgb);
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
            image.SetPixel(x, y, Color.FromArgb(50 + x * 2, 30 + y * 3, 25 + (x + y) % 190));
        return image;
    }
    private static Bitmap Canvas(Size size)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Sentinel);
        return bitmap;
    }
    private static IntPtr Erase(Control panel, Bitmap bitmap, int clipKind)
    {
        using var graphics = Graphics.FromImage(bitmap);
        IntPtr hdc = graphics.GetHdc();
        IntPtr region = IntPtr.Zero;
        try
        {
            if (clipKind == 1) region = CreateRectRgn(9, 7, bitmap.Width - 11, bitmap.Height - 9);
            if (clipKind == 2) region = CreateEllipticRgn(9, 7, bitmap.Width - 11, bitmap.Height - 9);
            if (clipKind != 0 && (region == IntPtr.Zero || SelectClipRgn(hdc, region) == 0)) throw new Failure("OwnedClipSetupFailed");
            return SendMessage(panel.Handle, 0x0014, hdc, IntPtr.Zero);
        }
        finally
        {
            if (region != IntPtr.Zero) { SelectClipRgn(hdc, IntPtr.Zero); DeleteObject(region); }
            graphics.ReleaseHdc(hdc);
        }
    }
    private static bool Identical(Bitmap first, Bitmap second)
    {
        var bounds = new Rectangle(Point.Empty, first.Size);
        var left = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var right = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var a = new byte[first.Width * 4]; var b = new byte[a.Length];
                for (int y = 0; y < first.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(left.Scan0, y * left.Stride), a, 0, a.Length);
                    Marshal.Copy(IntPtr.Add(right.Scan0, y * right.Stride), b, 0, b.Length);
                    if (!a.AsSpan().SequenceEqual(b)) return false;
                }
                return true;
            }
            finally { second.UnlockBits(right); }
        }
        finally { first.UnlockBits(left); }
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern int SelectClipRgn(IntPtr hdc, IntPtr region);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);
    private sealed class Failure(string code) : Exception("SplitPanelBackgroundComparison")
    {
        internal string Code { get; } = code;
    }
}
