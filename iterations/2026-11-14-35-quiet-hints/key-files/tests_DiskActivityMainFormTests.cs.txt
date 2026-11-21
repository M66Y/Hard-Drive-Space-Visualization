using DiskAtlas;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

// The actual MainForm/monitor/panel pipeline, with synthetic discovery and counters.
// No real PDH source, filesystem fixture, saved configuration or diagnostic store.
internal static class DiskActivityMainFormTests
{
    public static int Run(bool dpi96 = false)
    {
        string? failure = null;
        int passed = 0;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F));
                using var state = new FakeState();
                DiskActivityMonitor? monitor = null;
                Action<DiskActivityUpdate>? deliver = null;
                int factories = 0;
                var root = new DiskEntry(@"X:\AsukaLens-Monitor-Demo", true) { Size = 17, AllocatedSize = 17, FileCount = 1 };
                root.MutableChildren.Add(new DiskEntry(Path.Combine(root.Path, "sample.txt"), false, root)
                {
                    Size = 17, AllocatedSize = 17, FileCount = 1, Modified = new DateTime(2026, 1, 1),
                    Storage = new(new(1, 1, 0), 17, 1, false)
                });
                using var form = new MainForm(null)
                {
                    ScanRunner = (_, _, _, _) => new(root, TimeSpan.FromSeconds(.01), 0, []),
                    ActivityMonitorFactory = callback =>
                    {
                        factories++;
                        deliver = callback;
                        monitor = new DiskActivityMonitor(update =>
                        {
                            state.Updates.Enqueue(update);
                            callback(update);
                        }, (_, token) => { token.ThrowIfCancellationRequested(); return new(0, "磁盘 0"); },
                            token => { token.ThrowIfCancellationRequested(); return [new(0, "磁盘 0"), new(1, "磁盘 1")]; },
                            state.Create, TimeSpan.FromMilliseconds(20));
                        return monitor;
                    }
                };
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                T Field<T>(string name) => (T)typeof(MainForm).GetField(name, flags)!.GetValue(form)!;
                var panel = Field<DiskActivityPanel>("activityPanel");
                var progress = Field<ScanProgressControl>("progressBar");
                var grid = Field<DataGridView>("grid");
                var primary = Ancestors(grid).OfType<SplitContainer>().First();
                var main = Ancestors(primary).OfType<TableLayoutPanel>().First(table => Ancestors(progress).Contains(table));
                var navigation = Field<NavigationPanel>("navigation");
                var toggle = panel.ToggleButton;
                var detailsButton = panel.DetailsButton!;
                var selection = panel.TargetSelector;
                DiskActivitySample? Current() => panel.GetViewSnapshot().Current;
                int HistoryCount() => panel.GetViewSnapshot().History.Count;
                DiskActivityDetailsDialog? Details() => typeof(MainForm).GetField("activityDetails", flags)!.GetValue(form) as DiskActivityDetailsDialog;
                async Task<DiskActivityDetailsDialog> OpenDetails()
                {
                    detailsButton.PerformClick();
                    await PumpUntil(() => Details() is { Visible: true, IsDisposed: false }, "DETAILS_OPEN_TIMEOUT");
                    return Details()!;
                }
                bool DisplayingAvailableAfter(long revision, int disk = 0) =>
                    Current() is { Status: DiskActivityStatus.Available } sample && sample.DiskNumber == disk &&
                    state.Updates.Any(update => update.Revision > revision && update.Sample == sample);
                void Check(bool condition, string code)
                {
                    if (!condition) throw new CheckFailure(code);
                    passed++;
                    Console.WriteLine("PASS disk activity main form " + code);
                }
                async Task PumpUntil(Func<bool> ready, string code)
                {
                    var clock = Stopwatch.StartNew();
                    while (!ready())
                    {
                        if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new CheckFailure(code);
                        await Task.Delay(10);
                    }
                }
                async Task CheckLayout(string label)
                {
                    form.PerformLayout(); main.PerformLayout(); panel.PerformLayout();
                    await Task.Delay(30);
                    var client = form.RectangleToScreen(form.ClientRectangle);
                    var mainBounds = main.RectangleToScreen(main.ClientRectangle);
                    var panelBounds = panel.RectangleToScreen(panel.ClientRectangle);
                    var progressBounds = progress.RectangleToScreen(progress.ClientRectangle);
                    var primaryBounds = primary.RectangleToScreen(primary.ClientRectangle);
                    var status = Field<Label>("status");
                    Check(Ancestors(primary).Contains(main) && Ancestors(progress).Contains(main) &&
                        !Ancestors(panel).Contains(main) && !status.Visible &&
                        !primaryBounds.IntersectsWith(progressBounds) && client.Contains(progressBounds),
                        label + "_MAIN_ANALYSIS_SEPARATE_FROM_VISIBLE_PROGRESS_AND_MONITOR");
                    Check(panel.SidebarMode && ReferenceEquals(panel.Parent, navigation) &&
                        navigation.ActivityBounds.Contains(panel.Bounds) && panel.Top >= navigation.LegendBounds.Bottom,
                        label + "_SIDEBAR_STAYS_BELOW_LEGEND");
                    Check(client.Contains(panelBounds) && navigation.RectangleToScreen(navigation.ClientRectangle).Contains(panelBounds) &&
                        panel.Visible && panel.Height > 0, label + "_SIDEBAR_MONITOR_VISIBLE");
                    Check(primary.Height > 0 && !panelBounds.IntersectsWith(primaryBounds) &&
                        !panelBounds.IntersectsWith(progressBounds) && progress.Height > 0,
                        label + "_CONTENT_PROGRESS_SEPARATE");
                    foreach (Control child in panel.Controls.Cast<Control>().Where(child => child.Visible))
                        Check(child.Width > 0 && child.Height > 0 && panel.ClientRectangle.Contains(child.Bounds),
                            label + "_SIDEBAR_COMMAND_VISIBLE");
                    foreach (string name in new[] { "choose", "refresh", "cancel" })
                    {
                        var button = Field<Button>(name);
                        var bounds = button.RectangleToScreen(button.ClientRectangle);
                        Check(button.Width > 0 && button.Height > 0 && client.Contains(bounds) && mainBounds.Contains(bounds),
                            label + "_HEADER_" + name.ToUpperInvariant());
                    }
                    Check(detailsButton.Visible && toggle.Visible && !detailsButton.Bounds.IntersectsWith(toggle.Bounds),
                        label + "_SIDEBAR_COMMANDS_DO_NOT_OVERLAP");
                    Check(grid.Rows.Count > 0 && grid.ClientSize.Height >= grid.ColumnHeadersHeight + grid.RowTemplate.Height &&
                        grid.GetRowDisplayRectangle(0, true).Height >= grid.Rows[0].Height,
                        label + "_ANALYSIS_RETAINS_A_FULL_RESULT_ROW");
                }

                form.Shown += async (_, _) =>
                {
                    // Let Application.Run() begin before any checks or failure cleanup.
                    await Task.Yield();
                    try
                    {
                        Check(panel.SidebarMode && !panel.Expanded && factories == 0 && monitor == null,
                            "INITIAL_DISABLED_HAS_NO_WORKER");
                        await (Task)typeof(MainForm).GetMethod("ScanFolder", flags)!.Invoke(form, [root.Path])!;
                        Check(factories == 0, "DISABLED_SCAN_DOES_NOT_CREATE_WORKER");
                        await CheckLayout("COMMON_DISABLED");
                        var disabledAnalysis = primary.Bounds;
                        var disabledDetail = await OpenDetails();
                        Check(!panel.Expanded && factories == 0 && monitor == null && HistoryCount() == 0 &&
                            disabledDetail.ViewPanel.GetViewSnapshot().Current == null,
                            "OPENING_DISABLED_DETAILS_DOES_NOT_ENABLE_OR_SAMPLE");
                        disabledDetail.Close();
                        await PumpUntil(() => disabledDetail.IsDisposed || !disabledDetail.Visible, "DISABLED_DETAILS_CLOSE_TIMEOUT");
                        Check(!panel.IsDisposed && factories == 0, "DISABLED_DETAILS_CLOSE_KEEPS_CANONICAL_PANEL");
                        toggle.PerformClick();
                        await PumpUntil(() => Current()?.Status == DiskActivityStatus.Available, "EXPAND_PIPELINE_TIMEOUT");
                        Check(panel.Expanded && factories == 1 && state.Created == 1 && Current()?.DiskNumber == 0,
                            "EXPAND_USES_MONITOR_PIPELINE");
                        Check(state.Updates.First(update => update.Sample != null).Sample!.Status == DiskActivityStatus.Waiting,
                            "NEW_SOURCE_STARTS_WITH_WAITING");
                        Check(HistoryCount() is > 0 and <= 60, "PANEL_HISTORY_REMAINS_BOUNDED");
                        await CheckLayout("COMMON_ENABLED");
                        Check(primary.Bounds == disabledAnalysis, "ENABLING_SIDEBAR_DOES_NOT_SHRINK_ANALYSIS");
                        state.BlockOneCollection();
                        await PumpUntil(() => state.BlockEntered.IsSet, "DETAILS_PROVIDER_DID_NOT_BLOCK");
                        var sharedBeforeDetails = panel.GetViewSnapshot();
                        int sharedCreated = state.Created;
                        long sharedRevision = state.Updates.Last().Revision;
                        var sharedDetail = await OpenDetails();
                        Check(factories == 1 && state.Created == sharedCreated && monitor!.IsCurrent(sharedRevision),
                            "DETAILS_REUSES_SINGLE_SAMPLING_OWNER");
                        Check(sharedDetail.ViewPanel.GetViewSnapshot().Current == sharedBeforeDetails.Current &&
                            sharedDetail.ViewPanel.GetViewSnapshot().History.SequenceEqual(sharedBeforeDetails.History),
                            "DETAILS_MIRRORS_CANONICAL_SAMPLE_AND_HISTORY");
                        sharedDetail.Close();
                        await PumpUntil(() => sharedDetail.IsDisposed || !sharedDetail.Visible, "ACTIVE_DETAILS_CLOSE_TIMEOUT");
                        Check(!panel.IsDisposed && panel.Expanded && state.Created == sharedCreated && monitor!.IsCurrent(sharedRevision) &&
                            panel.GetViewSnapshot().History.SequenceEqual(sharedBeforeDetails.History),
                            "CLOSING_DETAILS_PRESERVES_ACTIVE_SOURCE_AND_HISTORY");
                        int beforeDetailsResume = state.Collected;
                        state.ReleaseBlockedCollection();
                        await PumpUntil(() => state.Collected > beforeDetailsResume, "SOURCE_DID_NOT_CONTINUE_AFTER_DETAILS_CLOSE");
                        var area = Screen.FromControl(form).WorkingArea;
                        form.WindowState = FormWindowState.Normal;
                        form.Location = area.Location;
                        form.Size = form.MinimumSize;
                        var cancel = Field<Button>("cancel");
                        cancel.Visible = true;
                        await CheckLayout("MINIMUM_ENABLED");
                        var minimumAnalysis = primary.Bounds;
                        var originalRows = Field<List<MapItem>>("sortedRows");
                        typeof(MainForm).GetField("sortedRows", flags)!.SetValue(form,
                            Enumerable.Repeat(originalRows[0], PageSlice.Size + 1).ToList());
                        typeof(MainForm).GetMethod("PopulatePage", flags)!.Invoke(form, [0]);
                        await CheckLayout("MINIMUM_ENABLED_PAGED");
                        Check(primary.Bounds == minimumAnalysis && Field<Button>("nextPage").Enabled && Field<Button>("nextPage").Visible &&
                            grid.Parent!.ClientRectangle.Contains(Field<Button>("nextPage").Parent!.Bounds),
                            "PAGING_RETAINS_ANALYSIS_AND_NAVIGATION_CONTROLS");
                        typeof(MainForm).GetField("sortedRows", flags)!.SetValue(form, originalRows);
                        typeof(MainForm).GetMethod("PopulatePage", flags)!.Invoke(form, [0]);
                        toggle.PerformClick();
                        await PumpUntil(() => state.Released == state.Created && HistoryCount() == 0, "COLLAPSE_RELEASE_TIMEOUT");
                        await CheckLayout("MINIMUM_DISABLED");
                        Check(primary.Bounds == minimumAnalysis, "DISABLING_SIDEBAR_DOES_NOT_RESIZE_ANALYSIS");
                        int pausedCalls = state.Collected;
                        await Task.Delay(65);
                        Check(state.Collected == pausedCalls, "COLLAPSED_MONITOR_DOES_NOT_COLLECT");
                        int beforeCreate = state.Created;
                        long beforeRevision = state.Updates.Last().Revision;
                        toggle.PerformClick();
                        await PumpUntil(() => state.Created == beforeCreate + 1 && DisplayingAvailableAfter(beforeRevision),
                            "REEXPAND_PIPELINE_TIMEOUT");
                        Check(state.Updates.First(update => update.Revision > beforeRevision && update.Sample != null).Sample!.Status == DiskActivityStatus.Waiting,
                            "REEXPAND_CREATES_FRESH_BASELINE");
                        float scale = form.DeviceDpi / 96f;
                        form.Size = new Size(Math.Min((int)(1440 * scale), area.Width), Math.Min((int)(940 * scale), area.Height));
                        await CheckLayout("COMMON_REENABLED");
                        beforeCreate = state.Created;
                        beforeRevision = state.Updates.Last().Revision;
                        form.Width -= (int)(20 * scale);
                        await Task.Delay(40);
                        Check(state.Created == beforeCreate && state.Updates.Last().Revision == beforeRevision && HistoryCount() > 0,
                            "RESIZE_RETAINS_SOURCE_REVISION_AND_HISTORY");
                        beforeCreate = state.Created;
                        form.WindowState = FormWindowState.Minimized;
                        await PumpUntil(() => state.Released == state.Created && HistoryCount() == 0, "MINIMIZE_RELEASE_TIMEOUT");
                        pausedCalls = state.Collected;
                        await Task.Delay(65);
                        Check(state.Collected == pausedCalls && state.Created == beforeCreate, "MINIMIZED_MONITOR_STAYS_PAUSED");
                        beforeRevision = state.Updates.Last().Revision;
                        form.WindowState = FormWindowState.Normal;
                        await PumpUntil(() => state.Created == beforeCreate + 1 && DisplayingAvailableAfter(beforeRevision),
                            "RESTORE_PIPELINE_TIMEOUT");
                        Check(state.Updates.First(update => update.Revision > beforeRevision && update.Sample != null).Sample!.Status == DiskActivityStatus.Waiting,
                            "RESTORE_CREATES_FRESH_BASELINE");
                        beforeCreate = state.Created;
                        beforeRevision = state.Updates.Last().Revision;
                        var diskDetails = await OpenDetails();
                        diskDetails.TargetSelector.SelectedIndex = 2; // Follow, synthetic disk 0, synthetic disk 1.
                        await PumpUntil(() => state.Created == beforeCreate + 1 && DisplayingAvailableAfter(beforeRevision, 1),
                            "MANUAL_DISK_PIPELINE_TIMEOUT");
                        Check(panel.SelectedDiskNumber == 1 && selection.SelectedIndex == 2 && Current()?.DiskNumber == 1,
                            "DETAILS_DISK_SELECTION_REACHES_CANONICAL_PANEL");
                        Check(state.Released == state.Created - 1, "SWITCH_RELEASES_PREVIOUS_SOURCE");
                        await CheckLayout("COMMON_MANUAL_DISK");
                        diskDetails.Close();
                        var systemEvent = typeof(MainForm).GetMethod("HandleMonitoringSystemEvent", flags)!;
                        systemEvent.Invoke(form, [0x0218, 4L]);
                        await PumpUntil(() => state.Released == state.Created, "POWER_SUSPEND_RELEASE_TIMEOUT");
                        pausedCalls = state.Collected;
                        await Task.Delay(65);
                        Check(state.Collected == pausedCalls, "POWER_SUSPEND_PAUSES_SOURCE");
                        beforeCreate = state.Created;
                        beforeRevision = state.Updates.Last().Revision;
                        systemEvent.Invoke(form, [0x0218, 18L]);
                        await PumpUntil(() => state.Created == beforeCreate + 1 && DisplayingAvailableAfter(beforeRevision, 1),
                            "POWER_RESUME_PIPELINE_TIMEOUT");
                        Check(state.Updates.First(update => update.Revision > beforeRevision && update.Sample != null).Sample!.Status == DiskActivityStatus.Waiting,
                            "POWER_RESUME_STARTS_NEW_BASELINE");
                        beforeCreate = state.Created;
                        beforeRevision = state.Updates.Last().Revision;
                        systemEvent.Invoke(form, [0x0219, 0x8004L]);
                        await PumpUntil(() => state.Created == beforeCreate + 1 && DisplayingAvailableAfter(beforeRevision, 1),
                            "DEVICE_CHANGE_PIPELINE_TIMEOUT");
                        Check(state.Updates.First(update => update.Revision > beforeRevision && update.Sample != null).Sample!.Status == DiskActivityStatus.Waiting,
                            "DEVICE_CHANGE_REVALIDATES_AND_REPRIMES");
                        beforeCreate = state.Created;
                        beforeRevision = state.Updates.Last().Revision;
                        systemEvent.Invoke(form, [0x0218, 7L]);
                        await PumpUntil(() => state.Created == beforeCreate + 1 && DisplayingAvailableAfter(beforeRevision, 1),
                            "RESUME_WITHOUT_SUSPEND_PIPELINE_TIMEOUT");
                        Check(state.Updates.First(update => update.Revision > beforeRevision && update.Sample != null).Sample!.Status == DiskActivityStatus.Waiting,
                            "RESUME_WITHOUT_PRIOR_SUSPEND_REPRIMES");
                        state.BlockOneCollection();
                        await PumpUntil(() => state.BlockEntered.IsSet, "DELAYED_PROVIDER_DID_NOT_ENTER");
                        var closingDetails = await OpenDetails();
                        long oldRevision = state.Updates.Last().Revision;
                        form.Close();
                        string? description = panel.AccessibleDescription;
                        deliver!(new(oldRevision, new(DateTimeOffset.UtcNow, 99, 99, 99, 99, 99, DiskActivityStatus.Available), "late"));
                        Check(panel.IsDisposed && panel.AccessibleDescription == description,
                            "CLOSE_IGNORES_DELAYED_UI_CALLBACK");
                        Check(closingDetails.IsDisposed, "MAIN_CLOSE_DISPOSES_OWNED_DETAILS");
                        state.ReleaseBlockedCollection();
                        await PumpUntil(() => monitor!.Completion.IsCompleted, "CLOSE_WORKER_COMPLETION_TIMEOUT");
                        Check(monitor!.Completion.IsCompletedSuccessfully && state.Released == state.Created,
                            "CLOSE_RELEASES_ALL_SOURCES");
                        Check(!monitor.IsCurrent(oldRevision), "CLOSED_MONITOR_REJECTS_OLD_REVISION");
                    }
                    catch (CheckFailure error) { failure = error.Code; }
                    catch { failure = "UNEXPECTED"; }
                    finally
                    {
                        state.ReleaseBlockedCollection();
                        monitor?.Dispose();
                        if (!form.IsDisposed) form.Close();
                        Application.ExitThread();
                    }
                };
                form.Show();
                Application.Run();
            }
            catch (CheckFailure error) { failure = error.Code; }
            catch { failure = "INITIALIZATION"; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(35))) failure = "THREAD_TIMEOUT";
        if (failure != null)
        {
            Console.Error.WriteLine("FAIL DISK_ACTIVITY_MAINFORM_TESTS " + failure);
            return 1;
        }
        Console.WriteLine("PASS disk activity MainForm checks: " + passed);
        return 0;
    }

    private sealed class CheckFailure(string code) : Exception
    {
        internal string Code { get; } = code;
    }

    private static IEnumerable<Control> Ancestors(Control child)
    {
        for (Control? parent = child.Parent; parent != null; parent = parent.Parent) yield return parent;
    }

    private sealed class FakeState : IDisposable
    {
        private int created, released, collected, blockNext;
        private readonly ManualResetEventSlim unblock = new();
        internal readonly ManualResetEventSlim BlockEntered = new();
        internal readonly ConcurrentQueue<DiskActivityUpdate> Updates = new();
        internal int Created => Volatile.Read(ref created);
        internal int Released => Volatile.Read(ref released);
        internal int Collected => Volatile.Read(ref collected);
        internal IDiskActivitySource Create(int disk)
        {
            Interlocked.Increment(ref created);
            return new FakeSource(this, disk);
        }
        internal void BlockOneCollection()
        {
            BlockEntered.Reset(); unblock.Reset();
            Interlocked.Exchange(ref blockNext, 1);
        }
        internal void ReleaseBlockedCollection() => unblock.Set();
        public void Dispose() { unblock.Set(); unblock.Dispose(); BlockEntered.Dispose(); }

        private sealed class FakeSource(FakeState owner, int disk) : IDiskActivitySource
        {
            private int calls, disposed;
            public DiskActivitySample Collect(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref disposed) != 0) throw new InvalidOperationException("DisposedFakeSource");
                Interlocked.Increment(ref owner.collected);
                if (Interlocked.Exchange(ref owner.blockNext, 0) != 0)
                {
                    owner.BlockEntered.Set();
                    // Models a provider that finishes late instead of cancelling mid-native-call.
                    if (!owner.unblock.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("SyntheticProviderTimeout");
                }
                bool waiting = Interlocked.Increment(ref calls) == 1;
                return new(DateTimeOffset.UtcNow, disk, waiting ? null : 35, waiting ? null : 1048576,
                    waiting ? null : 2097152, waiting ? null : .5,
                    waiting ? DiskActivityStatus.Waiting : DiskActivityStatus.Available);
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Increment(ref owner.released);
            }
        }
    }
}
