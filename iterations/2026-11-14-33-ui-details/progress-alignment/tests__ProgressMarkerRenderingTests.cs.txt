using System.Drawing.Drawing2D;
using System.Reflection;
using DiskAtlas;

// Fixed phases render the production track painter into owned bitmaps. These
// assertions cover appearance/state, not desktop frame rate or scan duration.
internal static class ProgressMarkerRenderingTests
{
    internal static int Run(bool dpi96 = false)
    {
        int passed = 0;
        int failureHresult = 0;
        string failure = "None";
        Action? requestClose = null;
        var worker = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new Form { ClientSize = new Size(900, 200), ShowInTaskbar = false };
                using var meter = new ScanProgressControl { Bounds = new Rectangle(10, 20, 800, 72) };
                form.Controls.Add(meter); form.Show(); Application.DoEvents();
                requestClose = () =>
                {
                    try { if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke((Action)(() => form.Close())); }
                    catch (InvalidOperationException) { }
                };
                float scale = meter.DeviceDpi / 96f;
                // These programmatically created fixtures do not inherit the
                // product form's designer AutoScaleDimensions. Size them in
                // physical pixels before drawing DPI-scaled track geometry.
                form.ClientSize = new Size((int)Math.Ceiling(900 * scale), (int)Math.Ceiling(200 * scale));
                meter.Bounds = new Rectangle((int)Math.Ceiling(10 * scale), (int)Math.Ceiling(20 * scale),
                    (int)Math.Ceiling(800 * scale), (int)Math.Ceiling(72 * scale));
                Application.DoEvents();
                var bands = meter.ContentBands;
                Check(bands.Stage.Left == bands.Track.Left && bands.Percent.Right == bands.Track.Right,
                    "StageAndPercentageAlignWithTrackEnds");
                Check(bands.Stage.Top == bands.Percent.Top && bands.Stage.Height == bands.Percent.Height &&
                    bands.Stage.Right < bands.Percent.Left, "ProgressHeadingsShareVerticalBandWithoutOverlap");
                meter.SetDetailText("合成扫描状态 · 17 个文件 · 1 MiB");
                Check(meter.AccessibleDescription!.Contains("合成扫描状态") && meter.AccessibleDescription.Contains("新目录出现"),
                    "DetailedStatusRetainsEstimateExplanationWithoutExtraTextRow");
                meter.SetDetailText(new string('x', 5000));
                Check(meter.AccessibleDescription!.Length <= ScanProgressControl.Explanation.Length + 4098,
                    "DetailedStatusIsBounded");
                var rail = new RectangleF(20 * scale, 45 * scale, meter.Width - 40 * scale, 6 * scale);
                var paper = Color.FromArgb(33, 57, 81);
                Bitmap Render(float position, bool highContrast = false)
                {
                    var image = new Bitmap(meter.Width, meter.Height);
                    image.SetResolution(meter.DeviceDpi, meter.DeviceDpi);
                    using var graphics = Graphics.FromImage(image);
                    graphics.Clear(paper); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    meter.PaintTrack(graphics, rail, scale, position, highContrast);
                    return image;
                }
                bool Equal(Bitmap first, Bitmap second, Rectangle rectangle)
                {
                    rectangle.Intersect(new Rectangle(Point.Empty, first.Size));
                    for (int y = rectangle.Top; y < rectangle.Bottom; y++)
                    for (int x = rectangle.Left; x < rectangle.Right; x++)
                        if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) return false;
                    return true;
                }
                int Difference(Color first, Color second) => Math.Abs(first.R - second.R) + Math.Abs(first.G - second.G) + Math.Abs(first.B - second.B);
                int centerY = (int)Math.Round(rail.Top + rail.Height / 2);
                using var idle = Render(.5f);
                meter.BeginScan();
                Check(meter.Percent == null && meter.PercentText == "总量未知" && meter.PulseRunning,
                    "UnknownWorkKeepsHonestStateAndOnePulse");
                foreach (float position in new[] { .2f, .5f, .8f })
                {
                    using var image = Render(position);
                    int centerX = (int)Math.Round(rail.Left + rail.Width * position);
                    // The former 45-DIP tail occupied this region. The soft
                    // symmetric icon glow has ended before this sample ring.
                    var oldTail = Rectangle.FromLTRB((int)Math.Ceiling(centerX - 44 * scale),
                        (int)Math.Ceiling(rail.Top + scale), (int)Math.Floor(centerX - 24 * scale),
                        (int)Math.Floor(rail.Bottom - scale));
                    Check(Equal(idle, image, oldTail), "UnknownMarkerHasNoFilledTailAtPhase" + position);
                    var futureRail = Rectangle.FromLTRB((int)Math.Ceiling(centerX + 24 * scale),
                        (int)Math.Ceiling(rail.Top + scale), (int)Math.Floor(rail.Right - 2 * scale),
                        (int)Math.Floor(rail.Bottom - scale));
                    Check(Equal(idle, image, futureRail), "UnknownMarkerDoesNotFillFutureWorkAtPhase" + position);
                    Check(Difference(idle.GetPixel(centerX, centerY), image.GetPixel(centerX, centerY)) > 30,
                        "ArtworkRemainsVisibleAtPhase" + position);
                }
                Check(meter.Percent == null && meter.PercentText == "总量未知", "RenderingNeverInventsPercentage");

                const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
                var getGlow = typeof(ScanProgressControl).GetMethod("GetMarkerGlow", hidden)!;
                var glow = (Bitmap)getGlow.Invoke(meter, [52])!;
                Check(glow.Width == 104 && glow.Height == 104 && meter.GlowBytes <= 192L * 192 * 4,
                    "GlowTextureIsBoundedByMarkerSize");
                int middle = glow.Width / 2;
                Color core = glow.GetPixel(middle, middle), near = glow.GetPixel(middle + glow.Width / 4, middle), edge = glow.GetPixel(glow.Width - 2, middle);
                Check(core.A > near.A && near.A > edge.A && edge.A <= 2 && glow.GetPixel(0, 0).A == 0,
                    "WarmGlowFadesContinuouslyToTransparentEdges");
                Check(core.R > core.G && core.G > core.B && core.A < 200,
                    "GlowUsesSoftWarmLightInsteadOfHardWhiteDisk");
                Check(ReferenceEquals(glow, getGlow.Invoke(meter, [52])), "SameThemeAndDpiReuseGlowTexture");
                Theme.SetAccent(Color.FromArgb(42, 83, 126));
                var rethemed = (Bitmap)getGlow.Invoke(meter, [52])!;
                Check(!ReferenceEquals(glow, rethemed), "ThemeChangeReplacesGlowTexture");
                Theme.SetAccent(null);
                var smaller = (Bitmap)getGlow.Invoke(meter, [26])!;
                Check(smaller.Width == 52 && !ReferenceEquals(smaller, rethemed), "DpiChangeReplacesGlowTexture");

                meter.Report(new(0, 0, 0, "", CompletedDirectories: 3, DiscoveredDirectories: 4));
                using var measured = Render(.1f);
                int processedX = (int)Math.Round(rail.Left + rail.Width * .25f);
                int remainingX = (int)Math.Round(rail.Left + rail.Width * .9f);
                Check(Difference(idle.GetPixel(processedX, centerY), measured.GetPixel(processedX, centerY)) > 40,
                    "MeasuredWorkStillFillsCompletedRail");
                Check(idle.GetPixel(remainingX, centerY).ToArgb() == measured.GetPixel(remainingX, centerY).ToArgb(),
                    "MeasuredWorkLeavesRemainingRailTransparent");
                Check(meter.Percent == 75 && meter.PercentText == "估算 75%" && !meter.PulseRunning,
                    "MeasuredProgressRetainsItsPercentageAndStopsUnknownPulse");
                using var highContrastMeter = new ScanProgressControl();
                highContrastMeter.BeginScan();
                using var highContrastImage = new Bitmap(meter.Width, meter.Height);
                using (var graphics = Graphics.FromImage(highContrastImage))
                {
                    graphics.Clear(SystemColors.Window); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    highContrastMeter.PaintTrack(graphics, rail, scale, .5f, true);
                }
                Check(highContrastMeter.GlowBytes == 0, "HighContrastUsesSystemCueWithoutSoftTexture");

                meter.BeginScan(); meter.AnimationsEnabled = false;
                Check(!meter.PulseRunning && meter.PercentText == "总量未知", "ReducedMotionRetainsUnknownStateWithoutTimer");
                meter.AnimationsEnabled = true; meter.Hide();
                Check(!meter.PulseRunning, "HiddenProgressStopsPulse");
                meter.Show();
                Check(meter.PulseRunning, "ShowingActiveUnknownWorkRestoresPulse");
                form.WindowState = FormWindowState.Minimized;
                Pump(90);
                Check(!meter.PulseRunning, "MinimizedOwnerStopsPulse");
                form.WindowState = FormWindowState.Normal; Application.DoEvents();
                meter.Refresh();
                Check(meter.PulseRunning, "RestoredOwnerResumesOnlyActiveUnknownWork");
                foreach (var state in new[] { ScanDisplayState.Cancelled, ScanDisplayState.Failed, ScanDisplayState.Incomplete })
                {
                    meter.Finish(state);
                    using var stopped = Render(.5f);
                    Check(!meter.PulseRunning && meter.Percent == null && Equal(idle, stopped, meter.ClientRectangle),
                        "TerminalStateRemovesMarkerAndPulse" + state);
                    meter.BeginScan();
                }
                meter.Finish(ScanDisplayState.Completed);
                using var completed = Render(.1f);
                Check(meter.Percent == 100 && !meter.PulseRunning &&
                    Difference(idle.GetPixel(processedX, centerY), completed.GetPixel(processedX, centerY)) > 40,
                    "CompletionKeepsRealFullRailAndTerminalState");
                meter.Dispose();
                Check(!meter.PulseRunning && meter.GlowBytes == 0, "DisposalReleasesGlowAndStopsTimer");
                form.Close();
            }
            catch (CheckFailure error) { failure = error.Code; }
            catch (Exception error)
            {
                failure = "OwnedRenderingFailure";
                failureHresult = (error.InnerException ?? error).HResult;
            }
            finally { Theme.SetAccent(null); }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        bool finished = worker.Join(TimeSpan.FromSeconds(15));
        if (!finished)
        {
            requestClose?.Invoke(); finished = worker.Join(TimeSpan.FromSeconds(3));
            failure = finished ? "RenderingBudgetExceeded" : "WorkerStillActive";
        }
        if (!finished || failure != "None")
        {
            Console.Error.WriteLine("FAIL progress marker code=" + failure + "; checks=" + passed + "; worker_finished=" + finished +
                "; hresult=" + failureHresult.ToString("X8"));
            return 1;
        }
        Console.WriteLine("PASS progress marker checks=" + passed + "; fixed_phases=True; desktop_review_separate=True");
        return 0;

        void Check(bool condition, string code)
        {
            if (!condition) throw new CheckFailure(code);
            passed++;
        }
    }
    private static void Pump(int milliseconds)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(3); }
    }
    private sealed class CheckFailure(string code) : Exception("ProgressMarkerRendering") { internal string Code { get; } = code; }
}
