using DiskAtlas;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

// A diagnostic of owned-window message ordering, not a screenshot, frame-rate
// test, or assertion that no intermediate pixels were visible on the desktop.
internal static class WallpaperScrollPresentationProbe
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int MaxPaintRecords = 256;
    private enum InputKind { Wheel, Scrollbar, Direct }
    private readonly record struct PaintRecord(long Started, long Finished, int NamePaints);
    private readonly record struct InputRecord(InputKind Kind, long Started, long Returned, long ObservedUntil,
        int PaintsBefore, int PaintsAtReturn, int NamesBefore, int NamesAtReturn, bool Moved);

    internal static int Run(bool dpi96 = false)
    {
        string failure = "None";
        int dpi = 0, rows = 0, namePaints = 0;
        bool overflow = false, realNamesCovered = false;
        var paints = new List<PaintRecord>(MaxPaintRecords);
        var inputs = new List<InputRecord>(9);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(24));
        CancellationToken token = cancellation.Token;
        Action? requestClose = null;
        var worker = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string unused = Path.Combine(Path.GetTempPath(), "AsukaLens-scroll-probe-unused-" + Guid.NewGuid().ToString("N"), "background.json");
                using var form = new MainForm(null, false, unused)
                {
                    ShowInTaskbar = false,
                    ScanRunner = (_, _, _, _) => throw new ProbeFailure("UnexpectedScan"),
                    ActivityMonitorFactory = _ => throw new ProbeFailure("UnexpectedMonitor")
                };
                var grid = Field<WallpaperGrid>(form, "grid");
                requestClose = () =>
                {
                    try
                    {
                        if (!form.IsDisposed && form.IsHandleCreated)
                            form.BeginInvoke((Action)(() => { if (!form.IsDisposed) form.Close(); }));
                    }
                    catch (InvalidOperationException) { }
                };
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        Field<CheckBox>(form, "reduceMotion").Checked = true;
                        Field<System.Windows.Forms.Timer>(form, "previewTimer").Stop();
                        var data = Enumerable.Range(0, 160).Select(index =>
                            new MapItem("合成名称 Synthetic item " + index.ToString("000", CultureInfo.InvariantCulture),
                                1024 + index, null, index % 2 == 0 ? "图片" : "文档")).ToList();
                        typeof(MainForm).GetMethod("PopulateGrid", Hidden)!.Invoke(form, [data]);
                        Bitmap pattern = CreatePattern();
                        try
                        {
                            typeof(MainForm).GetMethod("ApplyBackground", Hidden)!.Invoke(form,
                                [pattern, BackgroundView.Default, Theme.DefaultAccent, true]);
                        }
                        catch { pattern.Dispose(); throw; }
                        dpi = grid.DeviceDpi; rows = grid.RowCount;
                        if (rows != data.Count || !ReferenceEquals(Field<List<MapItem>>(form, "sortedRows"), data))
                            throw new ProbeFailure("RealRowsUnavailable");
                        grid.CurrentCell = grid[0, 0];
                        grid.Rows[0].Selected = true;
                        grid.CellPainting += (_, e) =>
                        {
                            // MainForm's actual name callback ran first and
                            // marked this cell handled. Plain VirtualMode-only
                            // rows would not cover that callback.
                            if (e.RowIndex < 0 || e.ColumnIndex != 0 || !e.Handled) return;
                            namePaints++;
                            realNamesCovered = true;
                        };
                        using var observation = new PaintObservation(grid, paints,
                            () => namePaints, () => overflow = true);
                        form.Invalidate(true);
                        await Settle();
                        var bar = grid.Controls.OfType<VScrollBar>().SingleOrDefault(control => control.Visible && control.Enabled)
                            ?? throw new ProbeFailure("ScrollbarUnavailable");
                        for (int repeat = 0; repeat < 3; repeat++)
                        {
                            await Measure(InputKind.Wheel, () => typeof(DataGridView).GetMethod("OnMouseWheel", Hidden)!
                                .Invoke(grid, [new HandledMouseEventArgs(MouseButtons.None, 0, grid.Width / 2,
                                    grid.ColumnHeadersHeight + 10, -120)]));
                            await Measure(InputKind.Scrollbar, () => typeof(ScrollBar).GetMethod("OnScroll", Hidden)!
                                .Invoke(bar, [new ScrollEventArgs(ScrollEventType.SmallIncrement, bar.Value,
                                    Math.Min(bar.Maximum, bar.Value + Math.Max(1, grid.RowTemplate.Height)), ScrollOrientation.VerticalScroll)]));
                            await Measure(InputKind.Direct, () => grid.FirstDisplayedScrollingRowIndex =
                                Math.Min(grid.RowCount - 12, grid.FirstDisplayedScrollingRowIndex + 2));
                        }
                        if (!realNamesCovered || namePaints == 0) throw new ProbeFailure("RealNamePaintNotObserved");
                        if (inputs.Any(input => !input.Moved)) throw new ProbeFailure("InputDidNotMoveRows");

                        async Task Measure(InputKind kind, Action action)
                        {
                            token.ThrowIfCancellationRequested();
                            int oldRow = grid.FirstDisplayedScrollingRowIndex;
                            int oldPaints = paints.Count, oldNames = namePaints;
                            long started = Stopwatch.GetTimestamp();
                            action();
                            long returned = Stopwatch.GetTimestamp();
                            int paintsAtReturn = paints.Count, namesAtReturn = namePaints;
                            bool moved = grid.FirstDisplayedScrollingRowIndex != oldRow;
                            // Observe ordinary pending WM_PAINT; do not create
                            // an Update/DrawToBitmap repaint that masks delay.
                            await Settle();
                            inputs.Add(new(kind, started, returned, Stopwatch.GetTimestamp(), oldPaints,
                                paintsAtReturn, oldNames, namesAtReturn, moved));
                        }
                        async Task Settle() => await Task.Delay(90, token);
                    }
                    catch (ProbeFailure error) { failure = error.Code; }
                    catch (OperationCanceledException) { failure = "ProbeCancelled"; }
                    catch { failure = "UnexpectedProbeFailure"; }
                    finally { if (!form.IsDisposed) form.Close(); }
                };
                Application.Run(form);
            }
            catch (ProbeFailure error) { failure = error.Code; }
            catch { failure = "UnexpectedWorkerFailure"; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        bool finished = worker.Join(TimeSpan.FromSeconds(24));
        if (!finished)
        {
            cancellation.Cancel();
            requestClose?.Invoke();
            finished = worker.Join(TimeSpan.FromSeconds(5));
            failure = finished ? "ProbeBudgetExceeded" : "WorkerStillActive";
        }
        if (!finished || failure != "None")
        {
            Console.Error.WriteLine("FAIL scroll presentation code=" + failure + "; worker_finished=" + finished);
            return 1;
        }
        if (overflow)
        {
            Console.Error.WriteLine("FAIL scroll presentation code=PaintRecordLimit; worker_finished=True");
            return 1;
        }
        foreach (InputKind kind in Enum.GetValues<InputKind>())
        {
            var group = inputs.Where(input => input.Kind == kind).ToArray();
            var during = group.SelectMany(input => paints.Skip(input.PaintsBefore).Take(input.PaintsAtReturn - input.PaintsBefore)).ToArray();
            var nextDelays = group.Select(input => input.PaintsAtReturn != input.PaintsBefore ? default :
                    paints.FirstOrDefault(paint => paint.Started >= input.Returned && paint.Started <= input.ObservedUntil))
                .Zip(group, (paint, input) => paint.Started == 0 ? (double?)null : Milliseconds(input.Returned, paint.Started))
                .Where(delay => delay.HasValue).Select(delay => delay!.Value).ToArray();
            var allInputPaints = paints.Where(paint => group.Any(input => paint.Started >= input.Started &&
                paint.Started <= input.ObservedUntil)).ToArray();
            Console.WriteLine("OBSERVE scroll presentation input=" + kind.ToString().ToLowerInvariant() +
                "; calls=" + group.Length + "; moved=" + group.Count(input => input.Moved) +
                "; call_total_ms=" + Number(group.Sum(input => Milliseconds(input.Started, input.Returned))) +
                "; paints_before_return=" + during.Length +
                "; name_paints_before_return=" + group.Sum(input => input.NamesAtReturn - input.NamesBefore) +
                "; next_paint_samples=" + nextDelays.Length +
                "; next_paint_max_ms=" + Number(nextDelays.Length == 0 ? -1 : nextDelays.Max()) +
                "; paint_samples=" + allInputPaints.Length +
                "; paint_total_ms=" + Number(allInputPaints.Sum(paint => Milliseconds(paint.Started, paint.Finished))) +
                "; paint_max_ms=" + Number(allInputPaints.Length == 0 ? 0 : allInputPaints.Max(paint => Milliseconds(paint.Started, paint.Finished))) +
                "; name_paints=" + allInputPaints.Sum(paint => paint.NamePaints));
        }
        Console.WriteLine("PASS scroll presentation diagnostic dpi=" + dpi + "; rows=" + rows +
            "; paint_records=" + paints.Count + "; real_name_callback=" + realNamesCovered +
            "; owned_window_closed=True; no_desktop_pixel_or_frame_rate_claim=True");
        return 0;
    }

    private static double Milliseconds(long start, long end) => (end - start) * 1000d / Stopwatch.Frequency;
    private static string Number(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, Hidden)!.GetValue(form)!;
    private static Bitmap CreatePattern()
    {
        var pattern = new Bitmap(80, 80);
        using var graphics = Graphics.FromImage(pattern);
        graphics.Clear(Color.FromArgb(26, 51, 86));
        using var warm = new SolidBrush(Color.FromArgb(245, 181, 101));
        using var light = new SolidBrush(Color.FromArgb(250, 245, 220));
        for (int offset = 0; offset < 80; offset += 16)
        {
            graphics.FillRectangle(warm, offset, 0, 6, 80);
            graphics.FillRectangle(light, 0, offset, 80, 3);
        }
        return pattern;
    }
    private sealed class ProbeFailure(string code) : Exception("ScrollPresentation") { internal string Code { get; } = code; }

    private sealed class PaintObservation : NativeWindow, IDisposable
    {
        private readonly Control owner;
        private readonly List<PaintRecord> records;
        private readonly Func<int> names;
        private readonly Action overflow;
        internal PaintObservation(Control owner, List<PaintRecord> records, Func<int> names, Action overflow)
        {
            this.owner = owner; this.records = records; this.names = names; this.overflow = overflow;
            owner.HandleCreated += Created;
            owner.HandleDestroyed += Destroyed;
            if (owner.IsHandleCreated) AssignHandle(owner.Handle);
        }
        private void Created(object? sender, EventArgs args) { if (Handle == IntPtr.Zero) AssignHandle(owner.Handle); }
        private void Destroyed(object? sender, EventArgs args) { if (Handle != IntPtr.Zero) ReleaseHandle(); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg != 0x000F) { base.WndProc(ref message); return; }
            long started = Stopwatch.GetTimestamp();
            int before = names();
            try { base.WndProc(ref message); }
            finally
            {
                if (records.Count < MaxPaintRecords) records.Add(new(started, Stopwatch.GetTimestamp(), names() - before));
                else overflow();
            }
        }
        public void Dispose()
        {
            owner.HandleCreated -= Created;
            owner.HandleDestroyed -= Destroyed;
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }
    }
}
