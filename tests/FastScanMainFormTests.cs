using DiskAtlas;
using System.Diagnostics;
using System.Reflection;

// Actual MainForm/coordinator/view pipeline with synthetic, already-closed
// snapshot results. No VSS/UAC, disk enumeration, Explorer, clipboard, saved
// configuration or personal data is used by these checks.
internal static class FastScanMainFormTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static int Run(bool dpi96 = false, string? screenshotPath = null, string? partialScreenshotPath = null)
    {
        int passed = 0;
        string? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F));
                int scans = 0, monitorFactories = 0, block = 0, partial = 0;
                using var entered = new ManualResetEventSlim();
                IProgress<ScanProgress>? late = null;
                const string rootPath = @"X:\AsukaLens-Fast-Synthetic";
                var captured = new DateTimeOffset(2026, 1, 1, 12, 34, 56, TimeSpan.Zero);
                using var form = new MainForm(null)
                {
                    FastScanRunner = (path, progress, token) =>
                    {
                        int round = Interlocked.Increment(ref scans);
                        if (Volatile.Read(ref block) != 0)
                        {
                            late = progress; entered.Set();
                            progress?.Report(new(0, 0, 0, "合成回退", ScanPhase.Preparing) { FastScanFallback = FastScanWireError.Unsupported });
                            progress?.Report(new(0, 0, 0, "合成普通扫描", ScanPhase.Scanning));
                            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(8)); token.ThrowIfCancellationRequested();
                            throw new CheckFailure("BLOCK_DID_NOT_CANCEL");
                        }
                        var result = Snapshot(path, captured.AddSeconds(round));
                        if (Volatile.Read(ref partial) == 0) return result;
                        result.Root.Incomplete = true;
                        return result with { Skipped = 1, Issues = [new(Path.Combine(path, "linked-cache"), DiskAtlas.NtfsPrototype.NtfsReparsePolicy.Reason)],
                            Source = new(captured.AddSeconds(round), 1) };
                    },
                    ActivityMonitorFactory = callback =>
                    {
                        monitorFactories++;
                        return new DiskActivityMonitor(callback, (_, token) => { token.ThrowIfCancellationRequested(); return new(0, "合成磁盘"); },
                            token => { token.ThrowIfCancellationRequested(); return [new(0, "合成磁盘")]; }, _ => new SyntheticActivity(), TimeSpan.FromMilliseconds(20));
                    }
                };
                T Field<T>(string name) => (T)typeof(MainForm).GetField(name, Private)!.GetValue(form)!;
                object? Invoke(string name, params object[] args) => typeof(MainForm).GetMethod(name, Private)!.Invoke(form, args);
                Task Scan(string path) => (Task)Invoke("ScanFolder", path)!;
                Task View() => (Task)Invoke("UpdateViewAsync", false)!;
                void Check(bool condition, string code)
                {
                    if (!condition) throw new CheckFailure(code);
                    passed++; Console.WriteLine("PASS fast scan main form " + code);
                }
                async Task Until(Func<bool> ready, string code)
                {
                    var watch = Stopwatch.StartNew();
                    while (!ready())
                    {
                        if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new CheckFailure(code);
                        await Task.Delay(10);
                    }
                }
                async Task Layout(string code)
                {
                    form.PerformLayout(); await Task.Delay(30);
                    var client = form.RectangleToScreen(form.ClientRectangle);
                    var checkbox = Field<CheckBox>("ntfsFast");
                    var info = Field<Label>("refreshInfo");
                    var checkboxBounds = checkbox.RectangleToScreen(checkbox.ClientRectangle);
                    var infoBounds = info.RectangleToScreen(info.ClientRectangle);
                    if (info.Width <= 0 || info.Height <= 0 || !client.Contains(infoBounds) || checkboxBounds.IntersectsWith(infoBounds))
                        Console.WriteLine($"LAYOUT {code} client={client} checkbox={checkboxBounds} info={infoBounds} rows={checkbox.Parent!.Height}");
                    Check(checkbox.Width > 0 && checkbox.Height > 0 && client.Contains(checkboxBounds) &&
                        checkbox.Parent!.ClientRectangle.Contains(checkbox.Bounds), code + "_FAST_ENTRY_VISIBLE");
                    Check(info.Width > 0 && info.Height > 0 && client.Contains(infoBounds) && !checkboxBounds.IntersectsWith(infoBounds), code + "_SOURCE_INFO_SEPARATE");
                    foreach (string name in new[] { "strategyCombo", "metricCombo", "choose", "refresh", "backgroundSettings", "diagnosticsButton" })
                    {
                        var control = Field<Control>(name);
                        var bounds = control.RectangleToScreen(control.ClientRectangle);
                        Check(control.Width > 0 && control.Height > 0 && client.Contains(bounds) && !checkboxBounds.IntersectsWith(bounds), code + "_" + name.ToUpperInvariant());
                    }
                    Check(Field<TreemapControl>("map").Width > 0 && Field<DataGridView>("grid").Width > 0 && Field<ScanProgressControl>("progressBar").Height > 0,
                        code + "_DATA_AND_PROGRESS_REMAIN");
                }
                form.Shown += async (_, _) =>
                {
                    await Task.Yield();
                    try
                    {
                        var checkbox = Field<CheckBox>("ntfsFast");
                        var meter = Field<ScanProgressControl>("progressBar");
                        var grid = Field<DataGridView>("grid");
                        var search = Field<TextBox>("search");
                        var metric = Field<ComboBox>("metricCombo");
                        Check(checkbox.Visible && checkbox.Enabled && !checkbox.Checked && !form.NtfsFastEnabled, "EXPERIMENTAL_ENTRY_DEFAULT_OFF");
                        string entryTip = Field<ToolTip>("scanTip").GetToolTip(checkbox) ?? "";
                        Check(entryTip.Contains("管理员授权") && entryTip.Contains("文件和清单不会上传"), "ENTRY_EXPLAINS_SOURCE_PRIVILEGE_AND_LOCAL_SCOPE");
                        Check(Field<ActionButton>("choose").Enabled && Field<ComboBox>("strategyCombo").Items.Count == 3 && metric.Items.Count == 2, "PICKER_STRATEGIES_AND_METRICS_RETAINED");
                        var area = Screen.FromControl(form).WorkingArea;
                        form.WindowState = FormWindowState.Normal; form.Location = area.Location; form.Size = form.MinimumSize;
                        await Layout("MINIMUM_DEFAULT");
                        float scale = form.DeviceDpi / 96f;
                        form.Size = new Size(Math.Min((int)(1440 * scale), area.Width), Math.Min((int)(940 * scale), area.Height));
                        await Layout("COMMON_DEFAULT");

                        form.NtfsFastEnabled = true;
                        Check(Field<Label>("refreshInfo").Text.Contains("快照扫描"), "SELECTED_ENTRY_EXPLAINS_SNAPSHOT");
                        await Scan(rootPath); await View();
                        var baseline = Field<ScanResult>("scan");
                        Check(scans == 1 && baseline.Source?.CapturedAt == captured.AddSeconds(1) && baseline.Checkpoint == null, "SUCCESS_USES_FAST_COORDINATOR_AND_NO_LIVE_CHECKPOINT");
                        Check(baseline.Root.FileCount == 5 && baseline.Root.FolderCount == 1 && baseline.Root.Size == 13352 && baseline.Root.AllocatedSize == 20512, "LOGICAL_AND_DEDUPLICATED_TOTALS_RETAINED");
                        Check(meter.State == ScanDisplayState.Completed && meter.Percent == 100 && !meter.PulseRunning && grid.RowCount == 4, "COMPLETE_TREE_REACHES_TREEMAP_AND_LIST");
                        Check(Field<TreemapControl>("map").Enabled && Field<TreemapControl>("map").TotalSize == baseline.Root.Size && Field<Label>("mapTitle").Text == "空间分布", "SPATIAL_VIEW_USES_SELECTED_METRIC");
                        Check(Field<Label>("refreshInfo").Text.Contains("快照") && Field<Label>("status").Text.Contains("只读快照") && Field<Label>("subtitle").Text.Contains("非实时"), "AS_OF_SOURCE_VISIBLE_IN_RESULT");
                        if (screenshotPath != null)
                        {
                            form.BringToFront(); form.Activate(); await Task.Delay(150);
                            using var screenshot = new Bitmap(form.Width, form.Height);
                            using (var graphics = Graphics.FromImage(screenshot)) graphics.CopyFromScreen(form.Location, Point.Empty, screenshot.Size);
                            using var output = new FileStream(screenshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            screenshot.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                            Check(output.Length > 0, "SCREEN_CAPTURE_SAVED_REQUIRES_VISUAL_REVIEW");
                        }

                        Invoke("SetMode", 1); await View();
                        Check(grid.RowCount == 5 && Field<Label>("mapTitle").Text == "大文件分布" && grid.Columns["location"]!.Visible, "LARGE_FILES_VIEW_INCLUDES_CHILD_FILES");
                        Invoke("SetMode", 2); await View();
                        var types = Field<List<MapItem>>("sortedRows");
                        Check(types.Count == 4 && types.Single(row => row.Name == ".txt").Count == 2 && grid.Columns["count"]!.Visible, "FILE_TYPE_VIEW_RETAINS_HARDLINK_ENTRY_COUNTS");
                        Invoke("SetMode", 0); await View();
                        var folder = Field<List<MapItem>>("sortedRows").Single(row => row.Entry?.IsDirectory == true);
                        Invoke("ActivateItem", folder); await View();
                        Check(Field<DiskEntry>("current").Name == "Cache" && grid.RowCount == 2 && Field<ActionButton>("up").Enabled && Field<ActionButton>("home").Enabled, "DIRECTORY_BROWSING_AND_PARENT_ACTIONS");
                        Field<ActionButton>("home").PerformClick(); await View();
                        Check(Field<DiskEntry>("current").Path == rootPath, "BROWSE_RETURNS_TO_SCAN_ROOT");
                        var file = Field<List<MapItem>>("sortedRows").Single(row => row.Name == "notes.txt");
                        Invoke("SelectItem", file, true);
                        Check(Field<ActionButton>("reveal").Enabled && Field<Label>("detailPath").Text.Contains(file.Entry!.Path) &&
                            Field<Label>("detailTitle").Text.Contains("硬链接") && grid.ContextMenuStrip!.Items.Cast<ToolStripItem>().Any(item => item.Text == "复制完整路径"), "FILE_DETAILS_LOCATION_AND_COPY_AUXILIARIES_RETAINED");
                        metric.SelectedIndex = 1; await View();
                        Check(Field<StatCard>("totalCard").Title == "实际占用空间" && Field<TreemapControl>("map").TotalSize == 20512 &&
                            Field<List<MapItem>>("sortedRows").Sum(row => row.Size) == 20512, "ALLOCATED_METRIC_REACHES_CARD_MAP_AND_ROWS");
                        Invoke("SetMode", 1); search.Text = "archive"; await View();
                        Check(grid.RowCount == 1 && Field<List<MapItem>>("sortedRows")[0].Name == "archive.zip" && Field<StatCard>("totalCard").Value == SizeFormat.Bytes(20512), "RECURSIVE_SEARCH_PRESERVES_UNFILTERED_TOTAL");

                        Volatile.Write(ref block, 1); var pending = Scan(rootPath + "-next");
                        await Until(() => entered.IsSet, "CANCEL_RUN_DID_NOT_START");
                        await Until(() => Field<Label>("refreshInfo").Text.Contains("快扫已回退"), "FALLBACK_DID_NOT_REACH_UI");
                        Check(Field<ToolTip>("scanTip").GetToolTip(Field<Label>("refreshInfo"))?.Contains(FastScanCoordinator.Reason(FastScanWireError.Unsupported)) == true &&
                            Field<Label>("status").Text.Contains("快扫回退"), "FALLBACK_REASON_VISIBLE_BEFORE_ORDINARY_COMPLETION");
                        Check(!checkbox.Enabled && !metric.Enabled && !Field<ActionButton>("choose").Enabled && Field<ActionButton>("cancel").Visible &&
                            meter.State == ScanDisplayState.Running && meter.Percent != 100 && ReferenceEquals(Field<ScanResult>("scan"), baseline), "IN_FLIGHT_OPTION_LOCK_AND_OLD_TREE_ISOLATION");
                        Check(Field<Label>("subtitle").Text.Contains("上一次扫描结果"), "IN_FLIGHT_OLD_RESULT_IDENTIFIED");
                        form.Size = form.MinimumSize; await Layout("MINIMUM_SCANNING");
                        form.Size = new Size(Math.Min((int)(1440 * scale), area.Width), Math.Min((int)(940 * scale), area.Height)); await Layout("COMMON_SCANNING");
                        Field<ActionButton>("cancel").PerformClick(); await pending; await View();
                        Check(ReferenceEquals(Field<ScanResult>("scan"), baseline) && Field<DiskEntry>("current").Path == rootPath && meter.State == ScanDisplayState.Cancelled && meter.Percent == null,
                            "CANCEL_RESTORES_PREVIOUS_COMPLETE_TREE");
                        Check(checkbox.Enabled && checkbox.Checked && metric.Enabled && metric.SelectedIndex == 1 && Field<int>("mode") == 1 && search.Text == "archive" && grid.RowCount == 1,
                            "CANCEL_RESTORES_QUERY_METRIC_AND_CONTROLS");
                        late?.Report(new(999, 999, 999, "过期合成回调", ScanPhase.Finalizing)); await Task.Delay(40);
                        Check(meter.State == ScanDisplayState.Cancelled && !Field<Label>("status").Text.Contains("过期"), "LATE_PROGRESS_CANNOT_OVERWRITE_CANCELLATION");
                        Volatile.Write(ref block, 0); await Scan(rootPath); await View();
                        Check(scans == 3 && !ReferenceEquals(Field<ScanResult>("scan"), baseline) && Field<ScanResult>("scan").Source?.CapturedAt == captured.AddSeconds(3), "REFRESH_TAKES_A_FRESH_SNAPSHOT_RESULT");
                        Check(search.Text == "archive" && metric.SelectedIndex == 1 && Field<int>("mode") == 1 && grid.RowCount == 1 && meter.State == ScanDisplayState.Completed,
                            "SUCCESSFUL_REFRESH_PRESERVES_VIEW_CONTEXT");
                        var latestComplete = Field<ScanResult>("scan");
                        Volatile.Write(ref partial, 1); await Scan(rootPath); await View();
                        var partialResult = Field<ScanResult>("scan");
                        var recovery = Field<ActionButton>("restoreComplete");
                        Check(partialResult.Source?.ExcludedReparseEntries == 1 && partialResult.Skipped == 1 && partialResult.Root.Incomplete &&
                            meter.State == ScanDisplayState.Incomplete && Field<Label>("status").Text.Contains("部分快扫完成") &&
                            Field<Label>("status").Text.Contains("链接及目标未纳入统计") && Field<ActionButton>("issues").Visible,
                            "DECLARED_LINK_EXCLUSIONS_ARE_VISIBLE_PARTIAL_RESULTS");
                        Check(recovery.Visible && recovery.Enabled && !latestComplete.Root.Incomplete &&
                            search.Text == "archive" && metric.SelectedIndex == 1 && grid.RowCount == 1,
                            "PARTIAL_RESULT_RETAINS_COMPLETE_RECOVERY_AND_QUERY");
                        form.Size = form.MinimumSize; await Layout("MINIMUM_PARTIAL");
                        var footerBounds = recovery.Parent!.RectangleToScreen(recovery.Parent.ClientRectangle);
                        Check(footerBounds.Contains(recovery.RectangleToScreen(recovery.ClientRectangle)) &&
                            !recovery.RectangleToScreen(recovery.ClientRectangle).IntersectsWith(Field<ActionButton>("issues").RectangleToScreen(Field<ActionButton>("issues").ClientRectangle)),
                            "MINIMUM_PARTIAL_RECOVERY_AND_ISSUES_FIT");
                        form.Size = new Size(Math.Min((int)(1440 * scale), area.Width), Math.Min((int)(940 * scale), area.Height)); await Layout("COMMON_PARTIAL");
                        if (partialScreenshotPath != null)
                        {
                            form.BringToFront(); form.Activate(); await Task.Delay(150);
                            using var image = new Bitmap(form.Width, form.Height);
                            using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(form.Location, Point.Empty, image.Size);
                            using var output = new FileStream(partialScreenshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            image.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        entered.Reset(); Volatile.Write(ref block, 1); var pendingFromPartial = Scan(rootPath + "-cancel");
                        await Until(() => entered.IsSet, "PARTIAL_CANCEL_RUN_DID_NOT_START");
                        Check(recovery.Visible && !recovery.Enabled, "RECOVERY_LOCKED_DURING_SCAN");
                        Field<ActionButton>("cancel").PerformClick(); await pendingFromPartial; await View();
                        Check(ReferenceEquals(Field<ScanResult>("scan"), partialResult) && recovery.Enabled,
                            "CANCEL_RETAINS_PARTIAL_RESULT_AND_OLDER_COMPLETE_RECOVERY");
                        Volatile.Write(ref block, 0); recovery.PerformClick(); await View();
                        Check(ReferenceEquals(Field<ScanResult>("scan"), latestComplete) && !recovery.Visible && meter.State == ScanDisplayState.Completed &&
                            search.Text == "archive" && metric.SelectedIndex == 1 && Field<int>("mode") == 1 &&
                            Field<Label>("status").Text.Contains("未重新扫描"), "RESTORE_COMPLETE_RESULT_PRESERVES_QUERY_AND_LABELS_AGE");
                        await Scan(rootPath); await View(); Volatile.Write(ref partial, 0); await Scan(rootPath); await View();
                        Check(!recovery.Visible && typeof(MainForm).GetField("completeRecovery", Private)!.GetValue(form) == null &&
                            !Field<ScanResult>("scan").Root.Incomplete, "NEW_COMPLETE_RESULT_RELEASES_OLDER_RECOVERY");
                        Check(monitorFactories == 0, "COLLAPSED_MONITOR_NEVER_TOUCHES_REAL_DISKS");
                    }
                    catch (CheckFailure error) { failure = error.Code; }
                    catch { failure = "UNEXPECTED"; }
                    finally { form.Close(); Application.ExitThread(); }
                };
                form.Show(); Application.Run();
            }
            catch (CheckFailure error) { failure = error.Code; }
            catch { failure = "INITIALIZATION"; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) failure = "THREAD_TIMEOUT";
        if (failure != null) { Console.Error.WriteLine("FAIL FAST_SCAN_MAINFORM_TESTS " + failure); return 1; }
        Console.WriteLine("PASS fast scan MainForm checks: " + passed); return 0;
    }

    private static ScanResult Snapshot(string path, DateTimeOffset captured)
    {
        var modified = new DateTime(2026, 1, 1, 10, 0, 0);
        var root = new DiskEntry(path, true) { Modified = modified, Size = 13352, FileCount = 5, FolderCount = 1, Storage = new(new(85, 5, 0), 0, 1, false, -1, modified) };
        var child = new DiskEntry(Path.Combine(path, "Cache"), true, root) { Modified = modified, Size = 8212, FileCount = 2, Storage = new(new(85, 6, 0), 0, 1, false, -1, modified) };
        DiskEntry File(DiskEntry parent, string name, long logical, long allocated, ulong id, uint links = 1) =>
            new(Path.Combine(parent.Path, name), false, parent) { Modified = modified, Size = logical, FileCount = 1, Storage = new(new(85, id, 0), allocated, links, false, logical, modified) };
        root.MutableChildren.AddRange([child, File(root, "notes.txt", 20, 32, 20, 2), File(root, "app.exe", 4096, 8192, 21), File(root, "image.png", 1024, 4096, 22)]);
        child.MutableChildren.AddRange([File(child, "archive.zip", 8192, 8192, 23), File(child, "alias.txt", 20, 32, 20, 2)]);
        StorageMetrics.Calculate(root, default);
        return new(root, TimeSpan.FromMilliseconds(25), 0, []) { Source = new(captured) };
    }
    private sealed class CheckFailure(string code) : Exception { internal string Code { get; } = code; }
    private sealed class SyntheticActivity : IDiskActivitySource
    {
        public DiskActivitySample Collect(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return new(DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, DiskActivityStatus.Available); }
        public void Dispose() { }
    }
}
