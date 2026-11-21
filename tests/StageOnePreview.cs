using DiskAtlas;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

// A visible real-scanner preview. The fixture contains ordinary files only;
// displayed path prefixes are aliases, while the actual result tree stays intact.
internal static class StageOnePreview
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string PublicRoot = @"X:\AsukaLens-Demo";
    private enum PreviewStage
    {
        CreateFixture, ConfigureData, StartUiThread, WaitUiThread, UiBootstrap,
        LoadBaseline, CreateForm, ApplyVariant, ConfigureUi, Scan, Query, Select,
        Ready, MessageLoop, CloseRequest, CloseOwned, CancelScan, InputListener, Cleanup
    }

    internal static int Run(string[] args)
    {
        if (args.Length is < 2 or > 16 || args[0] != "--stage-one-preview" ||
            args[1] is not ("baseline" or "a" or "b") ||
            args.Skip(2).Any(value => value is not ("--minimum" or "--dpi-96" or "--expanded" or "--details" or "--scroll-fixture" or "--reading-fixture" or "--restore-trace" or "--no-window-transitions" or "--unknown-progress" or "--resize-trace" or "--resize-pixels" or "--replay-30" or "--replay-100" or "--resize-candidate" or "--panel-observe" or "--readme")) ||
            args.Skip(2).Distinct(StringComparer.Ordinal).Count() != args.Length - 2 ||
            args.Contains("--scroll-fixture") && args.Contains("--reading-fixture") ||
            args.Contains("--resize-trace") && args.Contains("--resize-pixels") ||
            args.Contains("--replay-30") && args.Contains("--replay-100") ||
            args.Contains("--resize-candidate") && args.Contains("--panel-observe") ||
            args.Any(value => value.StartsWith("--replay-", StringComparison.Ordinal)) && !args.Contains("--resize-trace") && !args.Contains("--resize-pixels") ||
            args[1] == "baseline" && args.Any(value => value.StartsWith("--resize-", StringComparison.Ordinal) || value.StartsWith("--replay-", StringComparison.Ordinal)) ||
            args[1] == "baseline" && (args.Contains("--expanded") || args.Contains("--details") || args.Contains("--unknown-progress") || args.Contains("--reading-fixture") || args.Contains("--panel-observe")))
        {
            Console.Error.WriteLine("FAIL stage-one preview invocation");
            return 2;
        }

        var fixture = new Fixture();
        string? previousData = Environment.GetEnvironmentVariable(AppDataLocation.EnvironmentVariable);
        bool uiStarted = false, uiExited = false;
        int result = 1;
        Exception? failure = null;
        RestoreTrace? restoreTrace = null;
        var stage = PreviewStage.CreateFixture;
        try
        {
            fixture.Create(args.Contains("--scroll-fixture"), args.Contains("--reading-fixture"));
            stage = PreviewStage.ConfigureData;
            Environment.SetEnvironmentVariable(AppDataLocation.EnvironmentVariable, fixture.Settings);
            stage = PreviewStage.StartUiThread;
            var thread = new Thread(() =>
            {
                var uiStage = PreviewStage.UiBootstrap;
                try { Show(args[1], args.Contains("--minimum"), args.Contains("--dpi-96"), args.Contains("--expanded"), args.Contains("--details"), args.Contains("--restore-trace"), args.Contains("--no-window-transitions"), fixture, value => uiStage = value, trace => Volatile.Write(ref restoreTrace, trace), args.Contains("--unknown-progress"), args.Contains("--reading-fixture"), args.Contains("--resize-trace") || args.Contains("--resize-pixels"), args.Contains("--resize-pixels"), args.Contains("--replay-30") ? 30 : args.Contains("--replay-100") ? 100 : 0, args.Contains("--resize-candidate"), args.Contains("--panel-observe"), args.Contains("--readme")); }
                catch (Exception error) { failure = error; ReportFailure(uiStage, error); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            uiStarted = true;
            stage = PreviewStage.WaitUiThread;
            uiExited = thread.Join(TimeSpan.FromMinutes(35));
            if (!uiExited) throw new TimeoutException("StageOnePreview");
            if (failure == null) result = 0;
        }
        catch (Exception error)
        {
            ReportFailure(stage, error);
        }
        finally
        {
            // The UI has detached the trace on exit. Drain its bounded records
            // from this caller thread, never from the observed UI thread.
            Volatile.Read(ref restoreTrace)?.FlushPending();
            Environment.SetEnvironmentVariable(AppDataLocation.EnvironmentVariable, previousData);
            try
            {
                if (uiExited || !uiStarted) fixture.Cleanup();
                else { result = 1; Console.WriteLine("CLEANUP stage-one preview deferred=True; window still active"); }
            }
            catch (Exception error) { result = 1; ReportFailure(PreviewStage.Cleanup, error); }
        }
        return result;
    }

    private static void Show(string variant, bool minimum, bool virtualized96Dpi, bool expanded, bool details, bool traceRestore, bool noWindowTransitions, Fixture fixture, Action<PreviewStage> setStage, Action<RestoreTrace> traceCreated, bool unknownProgress, bool readingFixture, bool resizeDiagnostics, bool resizePixels, int replayInterval, bool resizeCandidate, bool panelObserve, bool readme)
    {
        setStage(PreviewStage.UiBootstrap);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.SetHighDpiMode(virtualized96Dpi ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var defaultFont = new Font("Microsoft YaHei UI", 9F);
        Application.SetDefaultFont(defaultFont);

        Type formType = typeof(MainForm);
        if (variant == "baseline")
        {
            setStage(PreviewStage.LoadBaseline);
            string path = Path.GetFullPath(Path.Combine("artifacts", "ui-stage1", "baseline", "DiskAtlas.dll"));
            var assembly = Assembly.LoadFrom(path);
            if (assembly.GetName().Name != "DiskAtlas") throw new InvalidOperationException("PreviewBaseline");
            formType = assembly.GetType("DiskAtlas.MainForm", throwOnError: true)!;
        }

        setStage(PreviewStage.CreateForm);
        var startupRendering = DesktopRendering.Resolve([], "AsukaLens.exe");
        using var form = variant == "baseline"
            ? (Form)formType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 4)
                .Invoke([null, false, Path.Combine(fixture.Settings, "background.dat"), null])
            : new MainForm(null, backgroundSettingsPath: Path.Combine(fixture.Settings, "background.dat"),
                dibCandidate: startupRendering.Dib, compositionCandidate: startupRendering.Composition, showCandidateLabel: false);
        var dibCoverage = form is MainForm ? (DibWindowCoverage?)typeof(MainForm).GetField("dibCoverage",InstanceFields)!.GetValue(form) : null;
        if(dibCoverage != null) dibCoverage.ObservePixels = resizePixels;
        using var rootCoverage = resizeCandidate
            ? new DiskAtlas.ResizePrototype.ResizeRootCoverageHook(form, () => ((MainForm)form).BackgroundCrop, 42) : null;
        var panelExperiments = resizeCandidate || panelObserve
            ? Descendants(form).OfType<WallpaperSplitContainer>().ToArray() : [];
        foreach (var split in panelExperiments)
            split.ConfigurePanelBackgroundExperiment(resizeCandidate
                ? WallpaperSplitContainer.PanelBackgroundMode.DirectWallpaper
                : WallpaperSplitContainer.PanelBackgroundMode.Observe);
        using var geometryTrace = resizeDiagnostics ? new DiskAtlas.ResizePrototype.ResizeDiagnosticTrace(form, resizePixels, resizeCandidate, resizeCandidate) : null;
        using var titleGeometry = resizeCandidate || panelObserve
            ? new DiskAtlas.ResizePrototype.ResizeTitleGeometryAudit(form,
                new[] { (Control)formType.GetField("mapTitle", InstanceFields)!.GetValue(form)!,
                    (Control)formType.GetField("listTitle", InstanceFields)!.GetValue(form)! }) : null;
        DiskAtlas.ResizePrototype.ResizeDiagnosticReplay? geometryReplay = null;
        System.Windows.Forms.Timer? diagnosticWarmup = null, diagnosticSettle = null;
        using var windowTransitions = noWindowTransitions ? new WindowTransitionOverride(form) : null;
        using var iconStream = formType.Assembly.GetManifestResourceStream("DiskAtlas.AppIcon");
        using var icon = iconStream == null ? null : new Icon(iconStream);
        if (icon != null) form.Icon = icon;
        if (variant != "baseline")
        {
            setStage(PreviewStage.ApplyVariant);
            var apply = formType.GetMethod("ApplyStageOnePreview", InstanceFields);
            if (apply == null) throw new InvalidOperationException("PreviewVariantUnavailable");
            apply.Invoke(form, [variant]);
        }

        DiskActivityPanel? activity = null;
        var activityControl = formType.GetField("activityPanel", InstanceFields)?.GetValue(form) as Control;
        DiskActivityMonitor? syntheticMonitor = null;
        long seededRevision = -1;
        if (form is MainForm currentForm)
        {
            var previewActivity = (DiskActivityPanel)activityControl!;
            activity = previewActivity;
            currentForm.ActivityMonitorFactory = callback =>
            {
                syntheticMonitor = new DiskActivityMonitor(update =>
                {
                    callback(update);
                    if (update.Sample is not { Status: DiskActivityStatus.Available } sample || form.IsDisposed || !form.IsHandleCreated) return;
                    try
                    {
                        form.BeginInvoke((Action)(() =>
                        {
                            if (form.IsDisposed || !previewActivity.Expanded || syntheticMonitor?.IsCurrent(update.Revision) != true || seededRevision == update.Revision) return;
                            // The normal host callback was posted first. Seed
                            // after it so initial status messages cannot erase
                            // this synthetic 60-second layout history.
                            previewActivity.ResetHistory();
                            for (int second = 0; second < 60; second++)
                                previewActivity.SetSample(SyntheticActivitySample(sample.Timestamp.AddSeconds(second - 59), 0, second));
                            seededRevision = update.Revision;
                        }));
                    }
                    catch (InvalidOperationException) { }
                }, (_, token) => { token.ThrowIfCancellationRequested(); return new(0, "合成磁盘 · 布局预览"); },
                    token => { token.ThrowIfCancellationRequested(); return [new(0, "合成磁盘 · 布局预览")]; },
                    disk => new SyntheticActivitySource(disk), TimeSpan.FromSeconds(1));
                return syntheticMonitor;
            };
        }
        else if (activityControl is { } baselineActivity)
        {
            // Cross-assembly baselines remain collapsed. Do not manufacture a
            // delegate of the old monitor type or accidentally enable native I/O.
            foreach (var button in baselineActivity.Controls.OfType<Button>()) button.Enabled = false;
        }

        Task? activeScan = null;
        bool closing = false, released = false;
        int closeRequested = 0;
        Exception? failure = null;
        var failureStage = PreviewStage.ConfigureUi;
        Form[] closingOwned = [];
        using var closeAfterDialogs = new System.Windows.Forms.Timer { Interval = 40 };
        void CloseOwnedThenParent()
        {
            if (form.IsDisposed) return;
            closingOwned = closingOwned.Concat(form.OwnedForms).Distinct().ToArray();
            foreach (var owned in closingOwned)
            {
                if (owned.IsDisposed) continue;
                try { owned.DialogResult = DialogResult.Cancel; owned.Close(); }
                catch (Exception error)
                {
                    if (failure == null) { failure = error; failureStage = PreviewStage.CloseOwned; }
                }
            }
            // BeginInvoke callbacks can run inside ShowDialog's nested message
            // loop. A later timer tick waits for Modal to clear before posting
            // the parent close, so the dialog's using/finally has returned.
            closeAfterDialogs.Start();
        }
        closeAfterDialogs.Tick += (_, _) =>
        {
            if (form.IsDisposed) { closeAfterDialogs.Stop(); return; }
            if (closingOwned.Any(owned => owned.Modal)) return;
            if (form.OwnedForms.Any(owned => owned.Modal)) { CloseOwnedThenParent(); return; }
            closeAfterDialogs.Stop();
            try { form.BeginInvoke((Action)(() => { if (!form.IsDisposed) form.Close(); })); }
            catch (InvalidOperationException error)
            {
                if (!form.IsDisposed && failure == null) { failure = error; failureStage = PreviewStage.CloseRequest; }
            }
        };
        void RequestClose()
        {
            Interlocked.Exchange(ref closeRequested, 1);
            try
            {
                if (form.IsHandleCreated && !form.IsDisposed)
                    form.BeginInvoke((Action)CloseOwnedThenParent);
            }
            catch (InvalidOperationException error) { if (!form.IsDisposed) ReportFailure(PreviewStage.CloseRequest, error); }
        }
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; RequestClose(); };
        Console.CancelKeyPress += cancel;
        using var restoreTrace = traceRestore ? new RestoreTrace(form, formType) : null;
        if (restoreTrace != null) traceCreated(restoreTrace);
        using var listenerStop = new CancellationTokenSource();
        var listener = Task.Run(async () =>
        {
            try
            {
                while (!listenerStop.IsCancellationRequested)
                {
                    string? line = await Console.In.ReadLineAsync(listenerStop.Token).ConfigureAwait(false);
                    if (line == null) return; // EOF stops input only; it never closes the preview.
                    if (line.Equals("trace", StringComparison.Ordinal)) { restoreTrace?.FlushPending(); continue; }
                    if (line.Equals("close", StringComparison.Ordinal)) { RequestClose(); return; }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException error) { ReportFailure(PreviewStage.InputListener, error); }
            catch (ObjectDisposedException error) { if (!listenerStop.IsCancellationRequested) ReportFailure(PreviewStage.InputListener, error); }
        });
        setStage(PreviewStage.ConfigureUi);
        var controls = Descendants(form).ToArray();
        foreach (var control in controls.Where(control => control is Label || control is TextBox { ReadOnly: true }))
            control.TextChanged += (_, _) => AliasControl(control, fixture.Scope);
        foreach (var grid in controls.OfType<DataGridView>()) grid.ShowCellToolTips = false;
        var map = formType.GetField("map", InstanceFields)?.GetValue(form);
        if (map != null && map.GetType().GetField("tip", InstanceFields)?.GetValue(map) is ToolTip tip)
            tip.Popup += (_, args) =>
            {
                if (tip.GetToolTip(args.AssociatedControl)?.Contains(fixture.Scope, StringComparison.OrdinalIgnoreCase) == true)
                    args.Cancel = true;
            };
        using var aliases = new System.Windows.Forms.Timer { Interval = 50 };
        aliases.Tick += (_, _) =>
        {
            AliasPaths(formType, form, controls, fixture.Scope);
            foreach (var dialog in form.OwnedForms.OfType<DiskActivityDetailsDialog>())
                if (!dialog.IsDisposed) dialog.Text = "磁盘活动详情 · 合成数据 · 布局预览";
        };
        using var lifetime = new System.Windows.Forms.Timer { Interval = 30 * 60 * 1000 };
        lifetime.Tick += (_, _) => { lifetime.Stop(); RequestClose(); };
        form.FormClosing += async (_, args) =>
        {
            if (released || activeScan?.IsCompleted != false) return;
            args.Cancel = true;
            if (closing) return;
            closing = true;
            (formType.GetField("scanCancellation", InstanceFields)?.GetValue(form) as CancellationTokenSource)?.Cancel();
            try { await activeScan; }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (failure == null) { failure = error; failureStage = PreviewStage.CancelScan; } }
            released = true;
            form.Close();
        };
        form.Shown += async (_, _) =>
        {
            var workStage = PreviewStage.ConfigureUi;
            try
            {
                // Register after MainForm.OnLoad's virtual-cell handler. The
                // quiet grid hint uses that same complete value, with only the
                // owned fixture root replaced for this visible preview.
                foreach (var hintGrid in controls.OfType<DataGridView>())
                    hintGrid.CellToolTipTextNeeded += (_, args) => args.ToolTipText =
                        args.ToolTipText?.Replace(fixture.Scope, PublicRoot, StringComparison.OrdinalIgnoreCase) ?? "";
                float scale = form.DeviceDpi / 96f;
                var area = Screen.FromControl(form).WorkingArea;
                var desired = minimum ? new Size(1120, 780) : new Size(1440, 940);
                var size = new Size(Math.Min((int)(desired.Width * scale), area.Width), Math.Min((int)(desired.Height * scale), area.Height));
                form.MinimumSize = new Size(Math.Min((int)(1120 * scale), size.Width), Math.Min((int)(780 * scale), size.Height));
                form.Size = size;
                form.Location = readme ? new Point(area.X, area.Y + Math.Min(24, Math.Max(0,area.Height-size.Height)))
                    : new Point(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2);
                aliases.Start(); lifetime.Start();
                if (Volatile.Read(ref closeRequested) != 0) { RequestClose(); return; }
                workStage = PreviewStage.Scan; setStage(workStage);
                activeScan = (Task)formType.GetMethod("ScanFolder", InstanceFields)!.Invoke(form, [fixture.Scope])!;
                await activeScan;
                if (form.IsDisposed || closing) return;
                workStage = PreviewStage.Query; setStage(workStage);
                if (readingFixture) formType.GetMethod("SetMode", InstanceFields)!.Invoke(form, [1]);
                await (Task)formType.GetMethod("UpdateViewAsync", InstanceFields)!.Invoke(form, [false])!;
                if (form.IsDisposed || closing) return;
                workStage = PreviewStage.Select; setStage(workStage);
                if (formType.GetField("sortedRows", InstanceFields)?.GetValue(form) is IList { Count: > 0 } rows)
                {
                    object? selected = readingFixture ? rows.Cast<MapItem>().FirstOrDefault(item => item.Entry is { IsDirectory: false }) : rows[0];
                    if (readingFixture && selected == null) throw new InvalidOperationException("PreviewReadingSelection");
                    formType.GetMethod("SelectItem", InstanceFields)!.Invoke(form, [selected, true]);
                }
                else if (readingFixture) throw new InvalidOperationException("PreviewReadingRows");
                AliasPaths(formType, form, controls, fixture.Scope);
                if (expanded)
                {
                    if (activity == null) throw new InvalidOperationException("PreviewActivityUnavailable");
                    activity.ToggleButton.PerformClick();
                    var seedDeadline = DateTime.UtcNow.AddSeconds(5);
                    while (seededRevision < 0)
                    {
                        if (form.IsDisposed || closing) return;
                        if (DateTime.UtcNow >= seedDeadline) throw new TimeoutException("PreviewActivitySeed");
                        await Task.Delay(20);
                    }
                }
                if (details)
                {
                    if (activity?.DetailsButton == null) throw new InvalidOperationException("PreviewActivityDetailsUnavailable");
                    activity.DetailsButton.PerformClick();
                    var dialog = form.OwnedForms.OfType<DiskActivityDetailsDialog>().Single();
                    dialog.Text = "磁盘活动详情 · 合成数据 · 布局预览";
                }
                form.Text = readme ? "AsukaLens" : readingFixture ? "AsukaLens · 双行列表实扫预览（合成文件）" :
                    activity == null ? "AsukaLens" : "AsukaLens · 合成磁盘活动预览";
                form.Activate();
                if (details) form.OwnedForms.OfType<DiskActivityDetailsDialog>().Single().Activate();
                await Task.Delay(350);
                if (form.IsDisposed || closing) return;
                if (unknownProgress)
                {
                    ((ScanProgressControl)formType.GetField("progressBar", InstanceFields)!.GetValue(form)!).BeginScan();
                    ((Label)formType.GetField("status", InstanceFields)!.GetValue(form)!).Text = "合成未知进度 · 图标泛光预览（非实际扫描）";
                    ((Label)formType.GetField("refreshInfo", InstanceFields)!.GetValue(form)!).Text = "合成未知进度（非实际扫描）";
                }
                workStage = PreviewStage.Ready; setStage(workStage);
                form.PerformLayout(); form.Refresh();
                var grid = controls.OfType<DataGridView>().Single();
                if (readingFixture && grid.RowCount > 0 && grid[0, 0].ToolTipText.Contains(fixture.Scope, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("PreviewHintAlias");
                var analysis = Ancestors(grid).OfType<SplitContainer>().FirstOrDefault();
                var detailsWindow = form.OwnedForms.OfType<DiskActivityDetailsDialog>().FirstOrDefault(dialog => dialog.Visible && !dialog.IsDisposed);
                int firstRowHeight = grid.Rows.Count == 0 ? 0 : grid.Rows[0].Height;
                Console.WriteLine("READY stage-one preview variant=" + variant + "; actual owned directory scan; displayed paths aliased; diagnostics unavailable" +
                    "; visual_acceptance=unverified" +
                    "; dpi=" + form.DeviceDpi + "; client_width=" + form.ClientSize.Width + "; client_height=" + form.ClientSize.Height +
                    "; grid_template_height=" + grid.RowTemplate.Height + "; grid_first_row_height=" + firstRowHeight +
                    "; reading_fixture=" + (readingFixture ? "true" : "false") +
                    "; activity_expanded=" + (activity?.Expanded == true ? "true" : "false") +
                    "; activity_width=" + (activityControl?.Width ?? 0) + "; activity_height=" + (activityControl?.Height ?? 0) +
                    "; activity_synthetic=" + (activity != null ? "true" : "false") +
                    "; activity_sidebar=" + (activity?.SidebarMode == true ? "true" : "false") +
                    "; analysis_height=" + (analysis?.Height ?? 0) +
                    "; activity_details_visible=" + (detailsWindow != null ? "true" : "false") +
                    "; activity_details_width=" + (detailsWindow?.ClientSize.Width ?? 0) +
                    "; activity_details_height=" + (detailsWindow?.ClientSize.Height ?? 0) +
                    "; transitions_requested_disabled=" + (noWindowTransitions ? "true" : "false") +
                    "; transitions_disabled=" + (windowTransitions?.Disabled == true ? "true" : "false") +
                    "; transitions_hresult=" + (windowTransitions?.Attempted == true ? "0x" + unchecked((uint)windowTransitions.HResult).ToString("X8", System.Globalization.CultureInfo.InvariantCulture) : "not-requested"));
                Console.Out.Flush();
                setStage(PreviewStage.MessageLoop);
                if (resizeCandidate)
                {
                    form.Text = "AsukaLens · 正常主窗合成数据【缩放修复对照 · F6尺寸演示】";
                    if (replayInterval == 0)
                    {
                        Size common = form.Size;
                        form.KeyPreview = true;
                        form.KeyDown += (_, key) =>
                        {
                            if (key.KeyCode != Keys.F6 || geometryReplay is { Completed: false }) return;
                            geometryReplay?.Dispose();
                            geometryReplay = new DiskAtlas.ResizePrototype.ResizeDiagnosticReplay(form, 30, () => geometryTrace,
                                () => Console.WriteLine("DEMO mainform complete=True; program-driven=True; visual_acceptance=separate"), common);
                            geometryReplay.Start(); key.Handled = true; key.SuppressKeyPress = true;
                        };
                    }
                }
                if (geometryTrace != null)
                {
                    geometryTrace.AttachCurrentChildren();
                    geometryTrace.Mark("Shown");
                    form.Text = "AsukaLens · 正常主窗合成数据 · 缩放定位采样" + (resizeCandidate ? "【修复候选】" : "【基线】");
                    if (replayInterval != 0)
                    {
                        geometryReplay = new DiskAtlas.ResizePrototype.ResizeDiagnosticReplay(form, replayInterval, () => geometryTrace, () =>
                        {
                            diagnosticSettle = new System.Windows.Forms.Timer { Interval = 500 };
                            diagnosticSettle.Tick += (_, _) => { diagnosticSettle.Dispose(); RequestClose(); };
                            diagnosticSettle.Start();
                        });
                        diagnosticWarmup = new System.Windows.Forms.Timer { Interval = 700 };
                        diagnosticWarmup.Tick += (_, _) => { diagnosticWarmup.Dispose(); if (!form.IsDisposed) geometryReplay.Start(); };
                        diagnosticWarmup.Start();
                    }
                }
            }
            catch (Exception error)
            {
                if (failure == null) { failure = error; failureStage = workStage; }
                RequestClose();
            }
        };
        setStage(PreviewStage.MessageLoop);
        try { Application.Run(form); }
        catch (Exception error)
        {
            if (failure == null) { failure = error; failureStage = PreviewStage.MessageLoop; }
        }
        finally
        {
            if (form is MainForm { Rendering: { } rendering })
            {
                Console.WriteLine($"DIB_MAINFORM active={rendering.Active}; native_copies={rendering.Copies}; fallback={rendering.Fallbacks}; peak_pixel_bytes={rendering.PeakBytes}");
                var coverage = dibCoverage;
                if(coverage != null) Console.WriteLine($"DIB_ROOT copies={coverage.EarlyCopies}; exposed={coverage.ExposedCopies}; visible={coverage.EarlyVisible}; black_after_copy={coverage.EarlyBlack}");
            }
            diagnosticWarmup?.Dispose(); diagnosticSettle?.Dispose(); geometryReplay?.Dispose();
            if (titleGeometry != null)
                Console.WriteLine("TITLE geometry " + System.Text.Json.JsonSerializer.Serialize(titleGeometry.Summary()));
            if (rootCoverage != null)
                Console.WriteLine($"COVERAGE root paints={rootCoverage.PaintCount}; failures={rootCoverage.Failures}; extra_owned_bytes=0");
            foreach (var split in panelExperiments)
                for (int panel = 1; panel <= 2; panel++)
                {
                    var c = split.GetPanelBackgroundCounts(panel);
                    Console.WriteLine($"PANEL background panel={panel}; candidate={resizeCandidate}; erases={c.Erases}; parent_backgrounds={c.ParentBackgrounds}; parent_foregrounds={c.ParentForegrounds}; direct={c.DirectPaints}; fallback={c.Fallbacks}; elapsed_ticks={c.ElapsedTicks}");
                }
            if (geometryTrace != null)
            {
                geometryTrace.Dispose();
                if (!form.IsDisposed && form.IsHandleCreated)
                    Console.WriteLine("DIAG mainform trace_written=False; deferred=True; code=OwnedWindowStillOpen");
                else try
                {
                    string traceDirectory = Path.Combine(AppContext.BaseDirectory, "traces");
                    Directory.CreateDirectory(traceDirectory);
                    geometryTrace.Flush(Path.Combine(traceDirectory, "mainform-" + Guid.NewGuid().ToString("N") + ".json"));
                    if (geometryReplay != null)
                        File.WriteAllText(Path.Combine(traceDirectory, "replay-" + Guid.NewGuid().ToString("N") + ".json"),
                            System.Text.Json.JsonSerializer.Serialize(geometryReplay.Summary()));
                    Console.WriteLine("DIAG mainform trace_written=True; owned-fixture-only=True; pixels=" + resizePixels);
                }
                catch (Exception error)
                {
                    if (failure == null) { failure = error; failureStage = PreviewStage.MessageLoop; }
                    Console.WriteLine("DIAG mainform trace_written=False; code=TraceWriteFailed");
                }
            }
            Console.CancelKeyPress -= cancel;
            listenerStop.Cancel();
            // Console input may be attached to a native console that does not
            // interrupt its pending read. Never wait on that reader at exit.
            _ = listener.ContinueWith(task => { _ = task.Exception; }, TaskScheduler.Default);
        }
        if (failure != null)
        {
            setStage(failureStage);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static DiskActivitySample SyntheticActivitySample(DateTimeOffset timestamp, int disk, int second)
    {
        int phase = second % 60;
        double active = 16 + 9 * Math.Sin(phase * .36) + (phase is >= 20 and <= 27 ? 35 : 0) + (phase is >= 42 and <= 47 ? 48 : 0);
        return new(timestamp, disk, Math.Clamp(active, 0, 100), (20 + 5 * Math.Sin(phase * .25)) * 1024 * 1024,
            (2 + Math.Sin(phase * .19)) * 1024 * 1024, .4 + .15 * Math.Sin(phase * .13), DiskActivityStatus.Available);
    }

    // Pure numbers and a clock; no PDH, resolver, storage query or file I/O.
    private sealed class SyntheticActivitySource(int disk) : IDiskActivitySource
    {
        private int second = 59, disposed;
        public DiskActivitySample Collect(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(SyntheticActivitySource));
            return SyntheticActivitySample(DateTimeOffset.UtcNow, disk, Interlocked.Increment(ref second));
        }
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
    }

    private static void ReportFailure(PreviewStage stage, Exception error)
    {
        for (int depth = 0; depth < 4; depth++)
        {
            if (error is TargetInvocationException { InnerException: { } inner }) error = inner;
            else if (error is AggregateException { InnerExceptions.Count: 1 } aggregate) error = aggregate.InnerExceptions[0];
            else break;
        }
        string category = error switch
        {
            ObjectDisposedException => "Disposed",
            OperationCanceledException => "Cancelled",
            UnauthorizedAccessException => "Access",
            IOException => "IO",
            ArgumentException => "Argument",
            TimeoutException => "Timeout",
            InvalidOperationException => "InvalidOperation",
            NotSupportedException => "Unsupported",
            System.ComponentModel.Win32Exception => "Native",
            System.Runtime.InteropServices.ExternalException => "Interop",
            OutOfMemoryException => "Memory",
            MemberAccessException or TypeLoadException => "Reflection",
            _ => "Other"
        };
        Console.Error.WriteLine("FAIL stage-one preview stage=" + stage + "; category=" + category +
            "; hresult=0x" + unchecked((uint)error.HResult).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AliasControl(Control control, string scope)
    {
        if (control.Text.Contains(scope, StringComparison.OrdinalIgnoreCase))
            control.Text = control.Text.Replace(scope, PublicRoot, StringComparison.OrdinalIgnoreCase);
    }
    private static void AliasPaths(Type type, Form form, Control[] controls, string scope)
    {
        foreach (var control in controls) if (!control.IsDisposed) AliasControl(control, scope);
        if (type.GetField("scopeDetails", InstanceFields)?.GetValue(form) is string details &&
            details.Contains(scope, StringComparison.OrdinalIgnoreCase))
            (type.GetMethod("SetScopeDetails", InstanceFields) ?? throw new InvalidOperationException("PreviewScopeMetadata"))
                .Invoke(form,
                [details.Replace(scope, PublicRoot, StringComparison.OrdinalIgnoreCase)]);
        var navigation = type.GetField("navigation", InstanceFields)?.GetValue(form);
        var path = navigation?.GetType().GetProperty("RootPath");
        if (path?.GetValue(navigation) is string text && text.Contains(scope, StringComparison.OrdinalIgnoreCase))
        { path.SetValue(navigation, text.Replace(scope, PublicRoot, StringComparison.OrdinalIgnoreCase)); (navigation as Control)?.Invalidate(); }
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static IEnumerable<Control> Ancestors(Control child)
    {
        for (Control? parent = child.Parent; parent != null; parent = parent.Parent) yield return parent;
    }

    // Optional A/B for this fixture window only. No global animation setting or
    // product control animation is changed, and the attribute dies with HWND.
    private sealed class WindowTransitionOverride : IDisposable
    {
        private readonly Form form;
        internal int HResult { get; private set; }
        internal bool Attempted { get; private set; }
        internal bool Disabled { get; private set; }
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        internal WindowTransitionOverride(Form form)
        {
            this.form = form;
            form.HandleCreated += Apply;
            form.HandleDestroyed += Destroyed;
            if (form.IsHandleCreated) Apply(form, EventArgs.Empty);
        }
        private void Apply(object? sender, EventArgs args)
        {
            int disabled = 1;
            Attempted = true;
            try { HResult = DwmSetWindowAttribute(form.Handle, 3, ref disabled, sizeof(int)); } // DWMWA_TRANSITIONS_FORCEDISABLED
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { HResult = error.HResult; }
            Disabled = HResult >= 0;
        }
        private void Destroyed(object? sender, EventArgs args) => Disabled = false;
        public void Dispose()
        {
            form.HandleCreated -= Apply;
            form.HandleDestroyed -= Destroyed;
        }
    }

    // Test-only observation of this fixture's window. It neither generates
    // input nor changes native messages; Paint means dispatch, not DWM commit.
    private sealed class RestoreTrace : NativeWindow, IDisposable
    {
        private const int MaxLines = 64, MaxRestores = 3;
        private readonly Form form;
        private readonly int uiThread = Environment.CurrentManagedThreadId;
        private readonly FieldInfo? presentationPasses;
        private readonly object recordsGate = new(), flushGate = new();
        private readonly List<TraceEntry> records = new(MaxLines);
        private readonly List<(Control Control, PaintEventHandler Paint, LayoutEventHandler Layout)> subscriptions = [];
        private readonly HashSet<int> messages = [];
        private readonly HashSet<Role> painted = [], laidOut = [];
        private int lineCount, restores;
        private bool awaitingRestore, observingRestore, resized, activated, disposed, outputFailed;
        private long phaseStarted;
        private enum Role { Form, Map, List, Bottom }
        private readonly record struct TraceEntry(int Sequence, int Ordinal, string Phase, long ElapsedTicks,
            string Kind, string Code, Role Role, FormWindowState State, int ClientWidth, int ClientHeight, int PresentationPasses);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);

        internal RestoreTrace(Form form, Type formType)
        {
            this.form = form;
            presentationPasses = formType.GetField("restorePresentationPasses", InstanceFields);
            var modules = new Dictionary<Control, Role>();
            AddFrame("map", Role.Map);
            AddFrame("grid", Role.List);
            AddFrame("progressBar", Role.Bottom);
            foreach (var (control, role) in modules)
            {
                PaintEventHandler paint = (_, _) =>
                {
                    if (observingRestore && painted.Add(role)) Emit("Paint", "Dispatch", role);
                };
                LayoutEventHandler layout = (_, _) =>
                {
                    if (observingRestore && laidOut.Add(role)) Emit("Layout", "Event", role);
                };
                control.Paint += paint;
                control.Layout += layout;
                subscriptions.Add((control, paint, layout));
            }
            form.HandleCreated += HandleCreated;
            form.HandleDestroyed += HandleDestroyed;
            form.Resize += Resized;
            form.Activated += Activated;
            form.Layout += LaidOut;
            if (form.IsHandleCreated) AssignHandle(form.Handle);
            phaseStarted = Stopwatch.GetTimestamp();
            Emit("Contract", "DispatchIsNotDwmCommit", Role.Form);

            void AddFrame(string field, Role role)
            {
                if (formType.GetField(field, InstanceFields)?.GetValue(form) is not Control leaf) return;
                for (Control? node = leaf; node != null && node != form; node = node.Parent)
                {
                    if (node.GetType().GetProperty("Framed")?.GetValue(node) is not true) continue;
                    modules.TryAdd(node, role);
                    break;
                }
            }
        }

        protected override void WndProc(ref Message message)
        {
            int code = message.Msg;
            int command = (int)(message.WParam.ToInt64() & 0xfff0);
            if (code == 0x0112 && command == 0xf020) ObserveMinimize("SysMinimize");
            else if (code == 0x0005 && message.WParam == (IntPtr)1) ObserveMinimize("SizeMinimized");
            if (awaitingRestore && (code == 0x0112 && command == 0xf120 ||
                code == 0x0005 && (message.WParam == IntPtr.Zero || message.WParam == (IntPtr)2) ||
                code == 0x0047 && Handle != IntPtr.Zero && !IsIconic(Handle)))
                ObserveRestore();

            string? messageCode = code switch
            {
                0x0005 => "Size",
                0x0046 => "WindowPosChanging",
                0x0047 => "WindowPosChanged",
                0x0112 when command == 0xf120 => "SysRestore",
                _ => null
            };
            bool loggedBefore = observingRestore && messageCode != null && messages.Add(code);
            int beforeCycle = restores;
            if (loggedBefore) Emit("MessageBefore", messageCode!, Role.Form);
            base.WndProc(ref message);
            if (loggedBefore && beforeCycle == restores)
                Emit("MessageAfter", messageCode!, Role.Form);
            else if (observingRestore && messageCode != null && messages.Add(code))
                Emit("MessageAfter", messageCode, Role.Form);
        }

        private void ObserveMinimize(string code)
        {
            if (disposed || awaitingRestore) return;
            observingRestore = false;
            if (restores >= MaxRestores) return;
            awaitingRestore = true;
            phaseStarted = Stopwatch.GetTimestamp();
            Emit("Minimize", code, Role.Form);
        }

        private void ObserveRestore()
        {
            if (disposed || !awaitingRestore || restores >= MaxRestores) return;
            awaitingRestore = false;
            restores++;
            observingRestore = true;
            resized = activated = false;
            messages.Clear(); painted.Clear(); laidOut.Clear();
            phaseStarted = Stopwatch.GetTimestamp();
            Emit("Restore", "Observed", Role.Form);
        }

        private void Resized(object? sender, EventArgs args)
        {
            if (form.WindowState == FormWindowState.Minimized) ObserveMinimize("ResizeMinimized");
            else if (awaitingRestore) ObserveRestore();
            if (observingRestore && !resized) { resized = true; Emit("Resize", "Event", Role.Form); }
        }
        private void Activated(object? sender, EventArgs args)
        {
            if (awaitingRestore && form.WindowState != FormWindowState.Minimized) ObserveRestore();
            if (observingRestore && !activated) { activated = true; Emit("Activated", "Event", Role.Form); }
        }
        private void LaidOut(object? sender, LayoutEventArgs args)
        {
            if (observingRestore && laidOut.Add(Role.Form)) Emit("Layout", "Event", Role.Form);
        }
        private void HandleCreated(object? sender, EventArgs args)
        {
            if (!disposed && Handle == IntPtr.Zero) AssignHandle(form.Handle);
        }
        private void HandleDestroyed(object? sender, EventArgs args)
        {
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }
        private void Emit(string kind, string code, Role role)
        {
            if (disposed || lineCount >= MaxLines) return;
            int ordinal = awaitingRestore ? restores + 1 : restores;
            string phase = awaitingRestore ? "Minimize" : observingRestore ? "Restore" : "Idle";
            long elapsed = Stopwatch.GetTimestamp() - phaseStarted;
            Size client = form.ClientSize;
            int passes = presentationPasses?.GetValue(form) is int count && count >= 0 ? count : -1;
            // Only fixed metadata is captured on the UI thread. In particular,
            // a PTY write cannot delay native message or paint dispatch here.
            lock (recordsGate)
            {
                if (lineCount >= MaxLines) return;
                records.Add(new(++lineCount, ordinal, phase, elapsed, kind, code, role, form.WindowState,
                    client.Width, client.Height, passes));
            }
        }

        internal void FlushPending()
        {
            // Stdin runs on the thread pool; Run also calls this after the UI
            // joins. Keep disposal itself free of synchronous console I/O.
            if (Environment.CurrentManagedThreadId == uiThread) return;
            lock (flushGate)
            {
                TraceEntry[] pending;
                lock (recordsGate) { pending = records.ToArray(); records.Clear(); }
                if (outputFailed) return;
                try
                {
                    foreach (var entry in pending)
                    {
                        string state = entry.State switch
                        {
                            FormWindowState.Minimized => "Minimized",
                            FormWindowState.Maximized => "Maximized",
                            _ => "Normal"
                        };
                        double elapsed = entry.ElapsedTicks * 1000d / Stopwatch.Frequency;
                        Console.WriteLine(FormattableString.Invariant($"TRACE restore seq={entry.Sequence}; ordinal={entry.Ordinal}; phase={entry.Phase}; elapsed_ms={elapsed:F3}; kind={entry.Kind}; code={entry.Code}; role={entry.Role}; state={state}; client_width={entry.ClientWidth}; client_height={entry.ClientHeight}; presentation_passes={entry.PresentationPasses}"));
                    }
                    Console.Out.Flush();
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { outputFailed = true; }
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            form.HandleCreated -= HandleCreated;
            form.HandleDestroyed -= HandleDestroyed;
            form.Resize -= Resized;
            form.Activated -= Activated;
            form.Layout -= LaidOut;
            foreach (var (control, paint, layout) in subscriptions)
            {
                control.Paint -= paint;
                control.Layout -= layout;
            }
            subscriptions.Clear();
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }
    }

    private sealed class Fixture
    {
        private const string Prefix = "AsukaLens-stage-one-";
        private readonly string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private readonly string nonce = Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, long> files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        private long maxWrittenBytes = 64L * 1024 * 1024;
        private string Root => Path.Combine(temp, Prefix + nonce);
        internal string Scope => Path.Combine(Root, "AsukaLens-Demo");
        internal string Settings => Path.Combine(Root, "settings");
        internal bool Created { get; private set; }
        internal void Create(bool scrollFixture, bool readingFixture)
        {
            if (File.Exists(Root) || Directory.Exists(Root)) throw new InvalidOperationException("PreviewFixtureCollision");
            Directory.CreateDirectory(Root); Created = true;
            File.WriteAllText(Path.Combine(Root, ".owner"), nonce);
            files.Add(".owner", new FileInfo(Path.Combine(Root, ".owner")).Length);
            Folder("settings"); Folder("AsukaLens-Demo");
            const int mb = 1024 * 1024;
            if (readingFixture)
            {
                maxWrittenBytes = 32L * mb;
                CreateReadingFixture(mb);
            }
            else
            {
                Add("视频 Videos", ("travel-demo.mp4", 8 * mb), ("recording-demo.mp4", 4 * mb));
                Add("图片 Pictures", ("photo-collection.jpg", 3 * mb), ("wallpaper-demo.png", 2 * mb));
                Add("应用 Apps", ("app-demo.exe", 4 * mb), ("tools-demo.zip", 2 * mb));
                Add("项目 Projects", ("project-backup.zip", 3 * mb), ("带有较长名称的项目说明与实现示例 source-demo.cs", 32768));
                Add("文档 Documents", ("guide-demo.pdf", mb), ("report-demo.docx", mb / 2));
                string nested = Path.Combine("AsukaLens-Demo", "项目 Projects", "源代码 Source");
                Folder(nested); Write(Path.Combine(nested, "README.md"), 4096); Write(Path.Combine(nested, "main.cs"), 12288);
                Folder(Path.Combine("AsukaLens-Demo", "文档 Documents", "空文件夹 Empty"));
                if (scrollFixture)
                    for (int index = 1; index <= 40; index++)
                        Write(Path.Combine("AsukaLens-Demo", $"scroll-demo-{index:00}.txt"), 65536 + index);
            }
            var modified = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            foreach (string path in files.Keys) File.SetLastWriteTimeUtc(Path.Combine(Root, path), modified);
            foreach (string path in directories.OrderByDescending(value => value.Length)) Directory.SetLastWriteTimeUtc(Path.Combine(Root, path), modified);
        }
        private void CreateReadingFixture(int mb)
        {
            // Exactly 80 ordinary files. Similar names and duplicate leaf names
            // have distinct parent paths, while total payload stays below 21 MiB.
            Add("正式版本 Releases", ("AsukaLens v1.5.0-portable.zip", 4 * mb), ("AsukaLens v1.5.0-installer.exe", 3 * mb));
            Add("历史备份 Archives", ("AsukaLens v1.5.0-portable.zip", 3 * mb + mb / 2), ("AsukaLens v1.5.0-installer.exe", 2 * mb + mb / 2));
            const string sharedName = "AsukaLens v1.5.0-文件名称与路径阅读示例-release-candidate-Windows-x64-相同前缀需要辨认完整后缀";
            Add("长名称 Long names", (sharedName + "-portable.zip", 2 * mb), (sharedName + "-installer.exe", mb + mb / 2), (sharedName + "-source.tar.gz", mb));
            string deep = Path.Combine("AsukaLens-Demo", "长路径 Long paths");
            Folder(deep);
            foreach (string part in new[] { "项目归档与演示材料 Project archives", "界面阅读与同名文件检查 Reading samples", "Windows-x64-Release-Candidate-2026", "完整目录层级与文件位置 Path identification" })
            {
                deep = Path.Combine(deep, part); Folder(deep);
            }
            Write(Path.Combine(deep, "带有较长名称的目录路径阅读演示-完整文件名称-first-document.pdf"), mb);
            Write(Path.Combine(deep, "带有较长名称的目录路径阅读演示-完整文件名称-second-document.docx"), mb / 2);
            Add("说明 Documents", ("阅读示例 README.txt", 65536));
            string small = Path.Combine("AsukaLens-Demo", "小文件 Small files"); Folder(small);
            for (int index = 1; index <= 70; index++)
                Write(Path.Combine(small, $"reading-demo-{index:00}.txt"), 16384 + index);
        }
        private void Add(string name, params (string Name, int Bytes)[] values)
        {
            string path = Path.Combine("AsukaLens-Demo", name); Folder(path);
            foreach (var value in values) Write(Path.Combine(path, value.Name), value.Bytes);
        }
        private void Folder(string relative) { Directory.CreateDirectory(Path.Combine(Root, relative)); directories.Add(relative); }
        private void Write(string relative, int length)
        {
            if (files.Values.Sum() + length > maxWrittenBytes) throw new InvalidOperationException("PreviewFixtureBudget");
            byte[] bytes = new byte[Math.Min(length, 65536)];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251 + 1);
            using var file = new FileStream(Path.Combine(Root, relative), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            for (int left = length; left > 0; left -= Math.Min(left, bytes.Length)) file.Write(bytes, 0, Math.Min(left, bytes.Length));
            files.Add(relative, length);
        }
        internal void Cleanup()
        {
            if (!Created) return;
            string resolved = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(resolved) != temp || Path.GetFileName(resolved) != Prefix + nonce ||
                (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(Path.Combine(resolved, ".owner")) != nonce)
                throw new InvalidOperationException("PreviewCleanupBoundary");
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(); pending.Push(resolved);
            while (pending.TryPop(out string? current))
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                string relative = Path.GetRelativePath(resolved, entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("PreviewCleanupLink");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!directories.Contains(relative)) throw new InvalidOperationException("PreviewCleanupUnknownDirectory");
                    seenDirectories.Add(relative); pending.Push(entry);
                }
                else
                {
                    if (!files.TryGetValue(relative, out long expected) || new FileInfo(entry).Length != expected)
                        throw new InvalidOperationException("PreviewCleanupUnknownFile");
                    seenFiles.Add(relative);
                }
            }
            if (!seenFiles.SetEquals(files.Keys) || !seenDirectories.SetEquals(directories)) throw new InvalidOperationException("PreviewCleanupChanged");
            foreach (string relative in files.Keys) File.Delete(Path.Combine(resolved, relative));
            foreach (string relative in directories.OrderByDescending(value => value.Length)) Directory.Delete(Path.Combine(resolved, relative), recursive: false);
            Directory.Delete(resolved, recursive: false); Created = false;
            Console.WriteLine("CLEANUP stage-one preview completed=True");
        }
    }
}
