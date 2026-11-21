using DiskAtlas;
using System.Diagnostics;
using System.Reflection;

// Exercises the real window lifecycle using only an in-memory preview tree.
// No scan, configuration write, screen capture or personal directory is used.
internal static class BackdropStabilityTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run(bool dpi96 = false)
    {
        int passed = 0;
        string? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string unusedSettings = Path.Combine(Path.GetTempPath(), "AsukaLens-Backdrop-Synthetic-" + Guid.NewGuid().ToString("N"), "background.json");
                using var stop = new CancellationTokenSource();
                using var form = new MainForm(null, false, unusedSettings)
                {
                    ShowInTaskbar = false,
                    ScanRunner = (_, _, _, _) => throw new CheckFailure("UnexpectedScannerStart"),
                    ActivityMonitorFactory = _ => throw new CheckFailure("UnexpectedMonitorStart")
                };
                T Field<T>(string name) => (T)typeof(MainForm).GetField(name, Hidden)!.GetValue(form)!;
                void Set(string name, object value) => typeof(MainForm).GetField(name, Hidden)!.SetValue(form, value);
                object? Invoke(string name, params object[] arguments) => typeof(MainForm).GetMethod(name, Hidden)!.Invoke(form, arguments);
                Task Preview() => (Task)Invoke("UpdatePreviewAsync")!;
                Bitmap? Raster() => form.WallpaperRaster.Get(form.BackgroundImage, form.ClientSize, form.BackgroundCrop);
                Rectangle InWindow(Control control) => form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                async Task Until(Func<bool> ready, string code)
                {
                    var clock = Stopwatch.StartNew();
                    while (!ready())
                    {
                        if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new CheckFailure(code);
                        await Task.Delay(10);
                    }
                }
                async Task Settle()
                {
                    form.PerformLayout();
                    await Task.Delay(30);
                }
                form.Shown += async (_, _) =>
                {
                    await Task.Yield();
                    try
                    {
                        Field<System.Windows.Forms.Timer>("previewTimer").Stop();
                        Field<CheckBox>("reduceMotion").Checked = true;
                        var session = SyntheticSession();
                        Set("liveSession", session); Set("scanCancellation", stop);
                        var map = Field<TreemapControl>("map");
                        var grid = Field<DataGridView>("grid");
                        using var regions = new RegionObservation(form);
                        var beforeFirstPreview = regions.Current;
                        var originalMapBounds = InWindow(map);
                        var originalRaster = Raster();
                        Check(originalRaster != null, "InitialWindowRasterAvailable");

                        await Preview(); await Settle();
                        Observe("first-preview", regions.Since(beforeFirstPreview));
                        string previewFacts = Field<string>("scopeDetails");
                        Check(previewFacts.Contains("暂显前") &&
                            previewFacts.Contains(LiveScanSession.PreviewLimit.ToString("N0")), "TruncatedPreviewRetainsScopeFacts");
                        Check(grid.RowCount > 0 && InWindow(map) == originalMapBounds &&
                            !Descendants(form).OfType<Label>().Any(label => label.Visible && label.Text == previewFacts),
                            "PreviewRetainsFactsWithoutInsertingExplanationOrMovingMap");
                        var mapBounds = InWindow(map); var gridBounds = InWindow(grid);
                        var firstRaster = Raster();
                        Check(ReferenceEquals(originalRaster, firstRaster), "FirstPreviewReusesWindowRaster");
                        var beforeRepeat = regions.Current;
                        await Preview(); await Settle();
                        var repeatedRegions = regions.Since(beforeRepeat);
                        Observe("repeated-preview", repeatedRegions);
                        Check(Field<string>("scopeDetails") == previewFacts, "UnchangedPreviewKeepsSameScopeFacts");
                        Check(InWindow(map) == mapBounds && InWindow(grid) == gridBounds,
                            "RepeatedPreviewKeepsAnalysisGeometry");
                        Check(ReferenceEquals(firstRaster, Raster()), "RepeatedPreviewReusesWindowRaster");
                        Check(repeatedRegions == default, "UnchangedPreviewDoesNotRecreateLiveRegions");
                        Check(!Field<System.Windows.Forms.Timer>("previewTimer").Enabled, "TestDoesNotStartPeriodicPreviewWork");

                        form.WindowState = FormWindowState.Normal;
                        form.Size = form.MinimumSize;
                        await Settle();
                        mapBounds = InWindow(map); gridBounds = InWindow(grid);
                        string longFacts = string.Join(" · ", Enumerable.Repeat("只读快照 12:34:56，链接及目标未纳入统计，未知大小保持未知", 4));
                        Invoke("SetScopeDetails", longFacts);
                        await Settle();
                        Check(Field<string>("scopeDetails") == longFacts &&
                            !Descendants(form).OfType<Label>().Any(label => label.Visible && label.Text == longFacts),
                            "LongScopeFactsRemainCompleteWithoutVisibleExplanation");
                        Check(InWindow(map) == mapBounds && InWindow(grid) == gridBounds,
                            "LongScopeFactsDoNotChangeMinimumAnalysisGeometry");
                        var client = form.ClientSize;
                        mapBounds = InWindow(map); gridBounds = InWindow(grid);
                        var beforeMinimize = regions.Current;
                        form.WindowState = FormWindowState.Minimized;
                        await Until(() => form.WindowState == FormWindowState.Minimized, "MinimizeDidNotComplete");
                        Observe("minimize", regions.Since(beforeMinimize));
                        Check(Field<string>("scopeDetails") == longFacts, "MinimizedWindowKeepsScopeFacts");
                        var beforeRestore = regions.Current;
                        form.WindowState = FormWindowState.Normal;
                        Check(Field<bool>("restoreRepaintQueued"), "RestoreQueuesOneFinalRepaint");
                        await Until(() => form.WindowState == FormWindowState.Normal && form.ClientSize == client &&
                            !Field<bool>("restoreRepaintQueued"), "RestoreRepaintDidNotSettle");
                        await Settle();
                        Observe("restore", regions.Since(beforeRestore));
                        Check(Field<string>("scopeDetails") == longFacts && InWindow(map) == mapBounds && InWindow(grid) == gridBounds,
                            "RestoreKeepsFactsAndStableAnalysisGeometry");
                        Check(!Field<bool>("restoreRepaintQueued") && Raster() != null, "RestoreFinishesWithValidRasterAndNoQueuedWork");
                        var beforeIdle = regions.Current;
                        await Settle();
                        var idleRegions = regions.Since(beforeIdle);
                        Observe("settled", idleRegions);
                        Check(idleRegions == default, "SettledRestoreDoesNotKeepChangingLiveRegions");
                    }
                    catch (CheckFailure error) { failure = error.Code; }
                    catch { failure = "UnexpectedBackdropLifecycleFailure"; }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (CheckFailure error) { failure = error.Code; }
            catch { failure = "UnexpectedBackdropUiFailure"; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) failure = "BackdropWorkerStillActive";
        if (failure != null)
        {
            Console.Error.WriteLine("FAIL backdrop stability code=" + failure + "; no private details emitted");
            return 1;
        }
        Console.WriteLine("PASS backdrop stability checks=" + passed + "; synthetic preview and owned window lifecycle, no screenshot or pixel-completeness claim");
        return 0;

        void Check(bool condition, string code)
        {
            if (!condition) throw new CheckFailure(code);
            passed++;
        }
        static void Observe(string phase, RegionCounts counts) => Console.WriteLine(
            $"OBSERVE backdrop region phase={phase} framed={counts.Framed} leaf={counts.Leaf}; managed changes on live HWNDs, not native-call counts");
    }

    private readonly record struct RegionCounts(int Framed, int Leaf);

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class RegionObservation : IDisposable
    {
        private readonly List<(Control Control, EventHandler Handler)> subscriptions = [];
        private int framed, leaf;
        internal RegionCounts Current => new(framed, leaf);
        internal RegionCounts Since(RegionCounts previous) => new(framed - previous.Framed, leaf - previous.Leaf);

        internal RegionObservation(Control root)
        {
            Visit(root);
            void Visit(Control control)
            {
                bool isFrame = control is WallpaperTable { Framed: true };
                if (isFrame || control.Region != null)
                {
                    EventHandler handler = (_, _) =>
                    {
                        if (!control.IsHandleCreated || control.IsDisposed) return;
                        if (isFrame) framed++; else leaf++;
                    };
                    control.RegionChanged += handler;
                    subscriptions.Add((control, handler));
                }
                foreach (Control child in control.Controls) Visit(child);
            }
        }

        public void Dispose()
        {
            foreach (var (control, handler) in subscriptions) control.RegionChanged -= handler;
            subscriptions.Clear();
        }
    }

    private static LiveScanSession SyntheticSession()
    {
        const int count = 1105;
        var root = new DiskEntry(@"X:\AsukaLens-Backdrop-Synthetic", true)
        {
            Size = count * 4096L, AllocatedSize = count * 4096L, FileCount = count
        };
        for (int index = 0; index < count; index++)
            root.MutableChildren.Add(new DiskEntry(Path.Combine(root.Path, $"sample-{index:D4}.bin"), false, root)
            {
                Size = 4096, AllocatedSize = 4096, FileCount = 1,
                Modified = new DateTime(2026, 1, 1)
            });
        var session = new LiveScanSession(); session.Attach(root);
        return session;
    }

    private sealed class CheckFailure(string code) : Exception("BackdropStability")
    {
        internal string Code { get; } = code;
    }
}
