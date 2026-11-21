using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DiskAtlas;

// Own bitmap HDCs and an owned synthetic window only. These are erase/paint
// behavior checks, not desktop capture, input injection or visual acceptance.
internal static class WallpaperBackgroundEraseTests
{
    private static readonly Color Sentinel = Color.FromArgb(17, 239, 173);

    internal static int Run(bool dpi96 = false)
    {
        string failure = "None";
        int passed = 0;
        Action? close = null;
        var worker = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new MainForm(null, false,
                    Path.Combine(Path.GetTempPath(), "AsukaLens-erase-unused-" + Guid.NewGuid().ToString("N"), "background.json"))
                {
                    ShowInTaskbar = false,
                    ScanRunner = (_, _, _, _) => throw new CheckFailure("UnexpectedScan"),
                    ActivityMonitorFactory = _ => throw new CheckFailure("UnexpectedMonitor")
                };
                using var image = new Bitmap(64, 64);
                using (var graphics = Graphics.FromImage(image))
                {
                    graphics.Clear(Color.FromArgb(40, 70, 120));
                    using var brush = new SolidBrush(Color.FromArgb(225, 130, 50));
                    graphics.FillRectangle(brush, 32, 0, 32, 64);
                }
                var previous = form.BackgroundImage;
                form.BackgroundImage = image; previous?.Dispose();
                using var split = new WallpaperSplitContainer
                {
                    Bounds = new Rectangle(20, 20, 320, 180), Dock = DockStyle.None,
                    SplitterDistance = 145, SplitterWidth = 8
                };
                form.Controls.Add(split); split.BringToFront();
                close = () =>
                {
                    try
                    {
                        if (!form.IsDisposed && form.IsHandleCreated)
                            form.BeginInvoke((Action)(() => { if (!form.IsDisposed) form.Close(); }));
                    }
                    catch (InvalidOperationException) { }
                };
                form.Shown += (_, _) =>
                {
                    try
                    {
                        Check(split.IsHandleCreated && split.FindForm() == form && split.Dock == DockStyle.None,
                            "OwnedUndockedSplitHasLiveHandle");
                        using var bitmap = new Bitmap(split.ClientSize.Width, split.ClientSize.Height, PixelFormat.Format32bppArgb);
                        Clear(bitmap);
                        IntPtr result = SendToBitmap(bitmap, split.Handle, 0x0014, IntPtr.Zero);
                        Check(result != IntPtr.Zero, "StandaloneEraseMessageIsHandled");
                        Check(Samples(bitmap).All(pixel => Rgb(pixel) == Rgb(Sentinel)),
                            "StandaloneEraseDoesNotRedrawWallpaper");
                        // Printing the client uses its ordinary background and
                        // foreground paint path, with an owned memory HDC.
                        SendToBitmap(bitmap, split.Handle, 0x0318, new IntPtr(0x0004 | 0x0008));
                        Check(Samples(bitmap).Any(pixel => Rgb(pixel) != Rgb(Sentinel)),
                            "NormalClientPaintStillRestoresBackground");
                        int paints = 0;
                        PaintEventHandler count = (_, _) => paints++;
                        split.Paint += count;
                        try { split.Invalidate(); split.Update(); }
                        finally { split.Paint -= count; }
                        Check(paints > 0, "SubsequentOrdinaryPaintStillRuns");
                        // Returning to an independent erase after a successful
                        // paint must remain quiet, rather than rely on a cache.
                        Clear(bitmap);
                        SendToBitmap(bitmap, split.Handle, 0x0014, IntPtr.Zero);
                        Check(Samples(bitmap).All(pixel => Rgb(pixel) == Rgb(Sentinel)),
                            "StandaloneEraseRemainsQuietAfterNormalPaint");
                    }
                    catch (CheckFailure error) { failure = error.Code; }
                    catch { failure = "UnexpectedOwnedPaintFailure"; }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (CheckFailure error) { failure = error.Code; }
            catch { failure = "UnexpectedOwnedWindowFailure"; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        bool finished = worker.Join(TimeSpan.FromSeconds(24));
        if (!finished)
        {
            close?.Invoke(); finished = worker.Join(TimeSpan.FromSeconds(4));
            failure = finished ? "OwnedPaintBudgetExceeded" : "OwnedPaintWorkerStillActive";
        }
        if (failure != "None" || !finished || passed == 0)
        {
            Console.Error.WriteLine("FAIL background erase code=" + failure);
            Console.Error.WriteLine("OBSERVE checks=" + passed + "; worker_finished=" + finished);
            return 1;
        }
        Console.WriteLine("PASS background erase checks=" + passed);
        Console.WriteLine("OBSERVE owned_bitmaps_only=True; desktop_capture=False");
        return 0;

        void Check(bool condition, string code)
        {
            if (!condition) throw new CheckFailure(code);
            passed++;
        }
    }

    private static void Clear(Bitmap bitmap)
    {
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Sentinel);
    }

    private static IEnumerable<Color> Samples(Bitmap bitmap)
    {
        foreach (int x in new[] { 3, bitmap.Width / 3, bitmap.Width - 4 })
        foreach (int y in new[] { 3, bitmap.Height / 3, bitmap.Height - 4 })
            yield return bitmap.GetPixel(x, y);
    }

    private static int Rgb(Color color) => color.ToArgb() & 0x00ffffff;

    private static IntPtr SendToBitmap(Bitmap bitmap, IntPtr window, uint message, IntPtr parameter)
    {
        using var graphics = Graphics.FromImage(bitmap);
        IntPtr hdc = graphics.GetHdc();
        try { return SendMessage(window, message, hdc, parameter); }
        finally { graphics.ReleaseHdc(hdc); }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private sealed class CheckFailure(string code) : Exception("WallpaperBackgroundErase")
    {
        internal string Code { get; } = code;
    }
}
