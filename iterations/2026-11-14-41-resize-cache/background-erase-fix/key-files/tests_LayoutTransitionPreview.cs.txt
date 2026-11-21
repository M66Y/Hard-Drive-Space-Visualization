using DiskAtlas;
using System.Diagnostics;
using System.Reflection;

// A real MainForm and DiskScanner over owned ordinary files. Commands change
// only this window; no capture API or forced repaint participates in observing
// the product's normal layout/paint transitions.
internal static class LayoutTransitionPreview
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string PublicRoot = @"X:\AsukaLens-Demo";
    private const string WindowTitle = "AsukaLens · 布局切换预览（合成目录/受控暂停）";

    internal static int Run(bool dpi96 = false)
    {
        var fixture = new Fixture();
        string? previousData = Environment.GetEnvironmentVariable(AppDataLocation.EnvironmentVariable);
        bool started = false, exited = false;
        int result = 1;
        Action? requestClose = null;
        Func<bool>? backgroundStopped = null;
        try
        {
            fixture.Create();
            Environment.SetEnvironmentVariable(AppDataLocation.EnvironmentVariable, fixture.Settings);
            var thread = new Thread(() =>
            {
                try
                {
                    Show(fixture, dpi96, close => Volatile.Write(ref requestClose, close),
                        ready => Volatile.Write(ref backgroundStopped, ready));
                    result = 0;
                }
                catch { Console.Error.WriteLine("FAIL layout-transition preview stage=ui"); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start(); started = true;
            exited = thread.Join(TimeSpan.FromMinutes(31));
            if (!exited)
            {
                Volatile.Read(ref requestClose)?.Invoke();
                exited = thread.Join(TimeSpan.FromSeconds(10));
                result = 1;
                Console.Error.WriteLine("FAIL layout-transition preview stage=lifetime");
            }
        }
        catch { Console.Error.WriteLine("FAIL layout-transition preview stage=setup"); }
        finally
        {
            if ((exited || !started) && Volatile.Read(ref backgroundStopped)?.Invoke() != false)
            {
                Environment.SetEnvironmentVariable(AppDataLocation.EnvironmentVariable, previousData);
                try { fixture.Cleanup(); }
                catch { result = 1; Console.Error.WriteLine("FAIL layout-transition preview stage=cleanup"); }
            }
            else { result = 1; Console.WriteLine("CLEANUP layout-transition preview deferred=True"); }
        }
        return result;
    }

    private static void Show(Fixture fixture, bool dpi96, Action<Action> setClose, Action<Func<bool>> setCleanupCheck)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var defaultFont = new Font("Microsoft YaHei UI", 9F);
        Application.SetDefaultFont(defaultFont);
        using var gate = new ScanGate();
        using var form = new MainForm(null, false, Path.Combine(fixture.Settings, "background.dat"))
        {
            ScanRunner = gate.Scan,
            FastScanConfirmation = () => false,
            ActivityMonitorFactory = callback => new DiskActivityMonitor(callback,
                (_, token) => { token.ThrowIfCancellationRequested(); return new(0, "合成活动 · 不采样磁盘"); },
                token => { token.ThrowIfCancellationRequested(); return [new(0, "合成活动 · 不采样磁盘")]; },
                _ => new SyntheticActivity())
        };
        using var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("DiskAtlas.AppIcon");
        using var icon = iconStream == null ? null : new Icon(iconStream);
        if (icon != null) form.Icon = icon;
        using var sizingInfo = new SizingInfo(form);
        void UpdateTitle() => form.Text = WindowTitle + " · NativeLayoutHold=" + (form.NativeLayoutHoldEnabled ? "ON" : "OFF");
        UpdateTitle();
        T Field<T>(string name) => (T)typeof(MainForm).GetField(name, Private)!.GetValue(form)!;
        Task InvokeTask(string method, params object[] args) =>
            (Task)typeof(MainForm).GetMethod(method, Private)!.Invoke(form, args)!;
        void SetScopeDetails(string details) => typeof(MainForm).GetMethod("SetScopeDetails", Private)!
            .Invoke(form, [Alias(details, fixture.Scope)]);
        var controls = Descendants(form).ToArray();
        var navigation = Field<NavigationPanel>("navigation");
        foreach (var control in controls.Where(value => value is Label or TextBox { ReadOnly: true }))
            control.TextChanged += (_, _) =>
            {
                string aliased = Alias(control.Text, fixture.Scope);
                if (control.Text != aliased) control.Text = aliased;
            };
        // Alias the display-only property before its already-requested paint.
        // Do not introduce a periodic repaint that could hide a stale surface.
        navigation.Invalidated += (_, _) => navigation.RootPath = Alias(navigation.RootPath, fixture.Scope);
        foreach (string name in new[] { "choose", "backgroundSettings", "diagnosticsButton", "ntfsFast" })
        {
            var control = Field<Control>(name);
            control.EnabledChanged += (_, _) => { if (control.Enabled) control.Enabled = false; };
            control.Enabled = false;
        }

        Task activeScan = Task.CompletedTask;
        setCleanupCheck(() => activeScan.IsCompleted && gate.Active == 0);
        bool closing = false, drained = false, failed = false, sweeping = false;
        using var listenerStop = new CancellationTokenSource();
        using var lifetime = new System.Windows.Forms.Timer { Interval = 30 * 60 * 1000 };
        using var closeOwned = new System.Windows.Forms.Timer { Interval = 40 };

        void CancelScan()
        {
            (typeof(MainForm).GetField("scanCancellation", Private)!.GetValue(form) as CancellationTokenSource)?.Cancel();
            gate.Release.Set();
        }
        void CloseOnUi()
        {
            if (form.IsDisposed) return;
            foreach (var owned in form.OwnedForms)
                if (!owned.IsDisposed) { owned.DialogResult = DialogResult.Cancel; owned.Close(); }
            closeOwned.Start();
        }
        void Post(Action action)
        {
            try
            {
                if (!form.IsDisposed && form.IsHandleCreated)
                    form.BeginInvoke((Action)(() => { if (!form.IsDisposed) action(); }));
            }
            catch (InvalidOperationException) { }
        }
        void RequestClose() => Post(CloseOnUi);
        setClose(RequestClose);
        closeOwned.Tick += (_, _) =>
        {
            if (form.IsDisposed) { closeOwned.Stop(); return; }
            if (form.OwnedForms.Any(owned => !owned.IsDisposed && owned.Modal)) return;
            closeOwned.Stop(); form.Close();
        };
        lifetime.Tick += (_, _) => { lifetime.Stop(); CloseOnUi(); };

        async Task Scan()
        {
            if (closing || !activeScan.IsCompleted || Field<CancellationTokenSource?>("scanCancellation") != null)
            { Console.WriteLine("STAGE layout-transition preview stage=busy"); return; }
            try
            {
                Console.WriteLine("STAGE layout-transition preview stage=scan-start");
                activeScan = InvokeTask("ScanFolder", fixture.Scope);
                await activeScan;
                if (closing || form.IsDisposed) return;
                await InvokeTask("UpdateViewAsync", false);
                Console.WriteLine("STAGE layout-transition preview stage=scan-ended; round=" + gate.Rounds +
                    "; state=" + Field<ScanProgressControl>("progressBar").State);
            }
            catch
            {
                failed = true;
                Console.Error.WriteLine("FAIL layout-transition preview stage=scan");
                CloseOnUi();
            }
        }
        void Resize(bool minimum)
        {
            var area = Screen.FromControl(form).WorkingArea;
            float scale = form.DeviceDpi / 96f;
            Size wanted = minimum ? new(1120, 780) : new(1440, 940);
            form.WindowState = FormWindowState.Normal;
            form.Size = new(Math.Min((int)(wanted.Width * scale), area.Width), Math.Min((int)(wanted.Height * scale), area.Height));
            form.Location = new(area.Left + (area.Width - form.Width) / 2, area.Top + (area.Height - form.Height) / 2);
        }
        async Task SweepSizes()
        {
            if (sweeping || !activeScan.IsCompleted || closing || form.IsDisposed) return;
            sweeping = true;
            Rectangle original = form.Bounds;
            var watch = Stopwatch.StartNew();
            int steps = 0;
            long maximumSetMilliseconds = 0;
            long setTicks = 0, waitTicks = 0, restoreTicks = 0;
            PaintMessageObservation? paintObservation = null;
            Console.WriteLine("STAGE layout-transition sweep=start");
            Console.WriteLine("OBSERVE sweep kind=programmatic; native_drag=False");
            Console.WriteLine("OBSERVE sweep planned_steps=24");
            try
            {
                paintObservation = new PaintMessageObservation(form);
                for (int step = 1; step <= 24; step++)
                {
                    if (closing || form.IsDisposed || watch.Elapsed > TimeSpan.FromSeconds(40)) break;
                    double amount = step <= 12 ? step / 12d : (24 - step) / 12d;
                    var before = Stopwatch.GetTimestamp();
                    try
                    {
                        form.Size = new Size(
                            (int)Math.Round(original.Width + (form.MinimumSize.Width - original.Width) * amount),
                            (int)Math.Round(original.Height + (form.MinimumSize.Height - original.Height) * amount));
                    }
                    finally
                    {
                        long elapsed = Stopwatch.GetTimestamp() - before;
                        setTicks += elapsed;
                        maximumSetMilliseconds = Math.Max(maximumSetMilliseconds,
                            (long)(elapsed * 1000d / Stopwatch.Frequency));
                    }
                    steps++;
                    long waitStarted = Stopwatch.GetTimestamp();
                    try { await Task.Delay(60, listenerStop.Token); }
                    finally { waitTicks += Stopwatch.GetTimestamp() - waitStarted; }
                }
            }
            catch (OperationCanceledException) when (listenerStop.IsCancellationRequested) { }
            finally
            {
                try
                {
                    if (!closing && !form.IsDisposed)
                    {
                        long restoreStarted = Stopwatch.GetTimestamp();
                        try { form.Bounds = original; }
                        finally { restoreTicks = Stopwatch.GetTimestamp() - restoreStarted; }
                    }
                }
                finally
                {
                    paintObservation?.Dispose();
                    sweeping = false;
                    Console.WriteLine("STAGE layout-transition sweep=end");
                    Console.WriteLine($"OBSERVE sweep steps={steps}; restored={!form.IsDisposed && form.Bounds == original}");
                    Console.WriteLine($"OBSERVE sweep elapsed_ms={watch.ElapsedMilliseconds}; max_set_ms={maximumSetMilliseconds}");
                    Console.WriteLine(FormattableString.Invariant($"OBSERVE sweep set_total_ms={setTicks * 1000d / Stopwatch.Frequency:F3}"));
                    Console.WriteLine(FormattableString.Invariant($"OBSERVE sweep wait_total_ms={waitTicks * 1000d / Stopwatch.Frequency:F3}"));
                    Console.WriteLine(FormattableString.Invariant($"OBSERVE sweep restore_ms={restoreTicks * 1000d / Stopwatch.Frequency:F3}"));
                    paintObservation?.Report();
                }
            }
        }
        async void Command(string command)
        {
            if (closing) return;
            if (sweeping && command is not ("close" or "cancel" or "sizing-info")) return;
            switch (command)
            {
                case "sweep": await SweepSizes(); break;
                case "scan": await Scan(); break;
                case "release": gate.Release.Set(); Console.WriteLine("STAGE layout-transition preview stage=release"); break;
                case "cancel": CancelScan(); Console.WriteLine("STAGE layout-transition preview stage=cancel"); break;
                case "scope":
                    SetScopeDetails("后台事实变化测试 · 固定合成内容，非扫描事实\n仅更新可访问描述，不显示说明或新增界面行\n统计仍来自自建目录");
                    Console.WriteLine("STAGE layout-transition preview stage=scope-details; metadata_only=True"); break;
                case "clear-scope":
                    SetScopeDetails("");
                    Console.WriteLine("STAGE layout-transition preview stage=clear-scope-details; metadata_only=True"); break;
                case "minimum": Resize(true); Console.WriteLine("STAGE layout-transition preview stage=minimum"); break;
                case "common": Resize(false); Console.WriteLine("STAGE layout-transition preview stage=common"); break;
                case "native-on":
                case "native-off":
                    form.NativeLayoutHoldEnabled = command == "native-on";
                    UpdateTitle();
                    Console.WriteLine("STAGE layout-transition preview stage=native-hold; enabled=" + form.NativeLayoutHoldEnabled);
                    break;
                case "sizing-info": sizingInfo.Report(form.NativeLayoutHoldEnabled); break;
                case "close": CloseOnUi(); break;
                default: Console.WriteLine("STAGE layout-transition preview stage=unknown-command"); break;
            }
        }
        ConsoleCancelEventHandler consoleCancel = (_, args) => { args.Cancel = true; RequestClose(); };
        Console.CancelKeyPress += consoleCancel;
        form.FormClosing += async (_, args) =>
        {
            if (drained || activeScan.IsCompleted && gate.Active == 0 && Field<CancellationTokenSource?>("scanCancellation") == null) return;
            args.Cancel = true;
            if (closing) return;
            closing = true; CancelScan();
            var watch = Stopwatch.StartNew();
            while (!activeScan.IsCompleted || gate.Active != 0 || Field<CancellationTokenSource?>("scanCancellation") != null)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(8))
                {
                    failed = true; closing = false;
                    Console.Error.WriteLine("FAIL layout-transition preview stage=close-wait");
                    return;
                }
                await Task.Delay(25);
            }
            drained = true; form.Close();
        };
        form.Shown += async (_, _) =>
        {
            foreach (var grid in controls.OfType<DataGridView>())
                grid.CellToolTipTextNeeded += (_, args) => args.ToolTipText = Alias(args.ToolTipText ?? "", fixture.Scope);
            Resize(false); lifetime.Start();
            await Scan();
            if (!form.IsDisposed && !closing)
                Console.WriteLine("READY layout-transition preview; native_hold=" + form.NativeLayoutHoldEnabled +
                    "; files=1106; pause_from_round=2; scope_commands=metadata-only; visual_acceptance=unverified" +
                    "; commands=scan,release,cancel,scope,clear-scope,minimum,common,sweep,native-on,native-off,sizing-info,close");
        };
        var listener = Task.Run(async () =>
        {
            try
            {
                while (!listenerStop.IsCancellationRequested)
                {
                    string? line = await Console.In.ReadLineAsync(listenerStop.Token).ConfigureAwait(false);
                    if (line == null) return; // EOF stops input, not this preview.
                    string command = line.Trim();
                    Post(() => Command(command));
                    if (command == "close") return;
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                Console.Error.WriteLine("FAIL layout-transition preview stage=input");
                Post(() => { failed = true; CloseOnUi(); });
            }
        });
        try { Application.Run(form); }
        finally
        {
            Console.CancelKeyPress -= consoleCancel;
            listenerStop.Cancel();
            _ = listener.ContinueWith(task => { _ = task.Exception; }, TaskScheduler.Default);
        }
        if (gate.Active != 0 || !activeScan.IsCompleted || failed) throw new InvalidOperationException("PreviewIncomplete");
    }

    private static string Alias(string text, string root) => text.Replace(root, PublicRoot, StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }

    // Observe only this fixture's native sizing messages. Counts describe
    // dispatch into the native sizing loop, never screen pixels or DWM commits.
    // Passive hooks on at most 256 existing owned controls. Timing wraps the
    // normal message chain; nested HWND dispatch is inclusive and cannot be
    // summed across controls or message types as total UI/CPU time.
    private sealed class PaintMessageObservation : IDisposable
    {
        private const int MaximumControls = 256;
        private static readonly MethodInfo GetControlStyle = typeof(Control).GetMethod("GetStyle", Private)!;
        private readonly List<PaintHook> hooks = [];
        private readonly Dictionary<string, Counts> groups = new(StringComparer.Ordinal);
        private bool limitReached, disposed;

        internal PaintMessageObservation(Form owner)
        {
            try
            {
                var pending = new Queue<Control>(); pending.Enqueue(owner);
                while (pending.TryDequeue(out var control))
                {
                    if (control.IsDisposed || control.Disposing || control != owner && control.FindForm() != owner) continue;
                    string kind = control.GetType().Name;
                    if (!groups.TryGetValue(kind, out var counts)) groups.Add(kind, counts = new Counts());
                    counts.Controls++;
                    int styles = 0;
                    if ((bool)GetControlStyle.Invoke(control, [ControlStyles.UserPaint])!) styles |= 1;
                    if ((bool)GetControlStyle.Invoke(control, [ControlStyles.AllPaintingInWmPaint])!) styles |= 2;
                    if ((bool)GetControlStyle.Invoke(control, [ControlStyles.OptimizedDoubleBuffer])!) styles |= 4;
                    counts.StylesAll &= styles; counts.StylesAny |= styles;
                    hooks.Add(new PaintHook(control, counts));
                    foreach (Control child in control.Controls)
                    {
                        if (hooks.Count + pending.Count < MaximumControls) pending.Enqueue(child);
                        else limitReached = true;
                    }
                }
            }
            catch { Dispose(); throw; }
        }

        internal void Report()
        {
            Console.WriteLine("OBSERVE resize-paint-messages; controls=" + hooks.Count);
            Console.WriteLine("OBSERVE paint control_limit=256; limit_reached=" + limitReached);
            Console.WriteLine("OBSERVE paint initial_control_tree_only=True");
            Console.WriteLine("OBSERVE paint includes_wait_and_restore=True");
            Console.WriteLine("OBSERVE paint inclusive_nested=True; additive_total=False");
            Console.WriteLine("OBSERVE paint capture=False; input=False");
            Console.WriteLine("OBSERVE styles_initial UserPaint=1; AllPaintingInWmPaint=2");
            Console.WriteLine("OBSERVE styles_initial OptimizedDoubleBuffer=4");
            foreach (var group in groups.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var counts = group.Value;
                Console.WriteLine("OBSERVE paint type=" + group.Key);
                Console.WriteLine($"OBSERVE paint controls={counts.Controls}; erase_calls={counts.EraseCalls}; paint_calls={counts.PaintCalls}");
                Console.WriteLine(FormattableString.Invariant($"OBSERVE paint erase_ms={counts.EraseTicks * 1000d / Stopwatch.Frequency:F3}; paint_ms={counts.PaintTicks * 1000d / Stopwatch.Frequency:F3}"));
                Console.WriteLine($"OBSERVE paint styles_all={counts.StylesAll}; styles_any={counts.StylesAny}");
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var hook in hooks) hook.Dispose();
        }

        private sealed class Counts
        {
            internal int Controls;
            internal int StylesAll = 7, StylesAny;
            internal long EraseCalls, EraseTicks, PaintCalls, PaintTicks;
        }

        private sealed class PaintHook : NativeWindow, IDisposable
        {
            private readonly Control owner;
            private readonly Counts counts;
            private bool disposed;

            internal PaintHook(Control owner, Counts counts)
            {
                this.owner = owner; this.counts = counts;
                owner.HandleCreated += Created; owner.HandleDestroyed += Destroyed;
                try { if (owner.IsHandleCreated) AssignHandle(owner.Handle); }
                catch { Dispose(); throw; }
            }

            private void Created(object? sender, EventArgs args)
            {
                if (disposed || owner.IsDisposed || owner.Disposing || !owner.IsHandleCreated) return;
                if (Handle != IntPtr.Zero) ReleaseHandle();
                AssignHandle(owner.Handle);
            }

            private void Destroyed(object? sender, EventArgs args)
            {
                if (Handle != IntPtr.Zero) ReleaseHandle();
            }

            protected override void WndProc(ref Message message)
            {
                int kind = message.Msg;
                if (kind is not (0x0014 or 0x000F)) { base.WndProc(ref message); return; }
                long started = Stopwatch.GetTimestamp();
                try { base.WndProc(ref message); }
                finally
                {
                    long ticks = Stopwatch.GetTimestamp() - started;
                    if (kind == 0x0014) { counts.EraseCalls++; counts.EraseTicks += ticks; }
                    else { counts.PaintCalls++; counts.PaintTicks += ticks; }
                }
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                owner.HandleCreated -= Created; owner.HandleDestroyed -= Destroyed;
                if (Handle != IntPtr.Zero) ReleaseHandle();
            }
        }
    }

    private sealed class SizingInfo : NativeWindow, IDisposable
    {
        private const int MaxSizes = 64;
        private readonly Form owner;
        private readonly HashSet<Size> sizes = [];
        private int enters, sizing, size, exits;
        private bool sizeLimitReached, inSizeMove, nativeResizeObserved, disposed;
        private bool sizingSeenInLoop;
        private Size loopStartSize;

        internal SizingInfo(Form owner)
        {
            this.owner = owner;
            owner.HandleCreated += HandleCreated;
            owner.HandleDestroyed += HandleDestroyed;
            if (owner.IsHandleCreated) AssignHandle(owner.Handle);
        }
        private void HandleCreated(object? sender, EventArgs e)
        {
            if (!disposed && owner.IsHandleCreated)
            {
                if (Handle != IntPtr.Zero) ReleaseHandle();
                AssignHandle(owner.Handle);
            }
        }
        private void HandleDestroyed(object? sender, EventArgs e)
        {
            inSizeMove = false;
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }
        protected override void WndProc(ref Message message)
        {
            switch (message.Msg)
            {
                case 0x0231: // WM_ENTERSIZEMOVE
                    Increment(ref enters); inSizeMove = true;
                    sizingSeenInLoop = false; loopStartSize = owner.ClientSize;
                    break;
                case 0x0214: // WM_SIZING
                    Increment(ref sizing);
                    if (inSizeMove) sizingSeenInLoop = true;
                    break;
                case 0x0005: // WM_SIZE: lParam contains client extents, not a position.
                    Increment(ref size);
                    long packed = message.LParam.ToInt64();
                    var observed = new Size((int)(packed & 0xffff), (int)((packed >> 16) & 0xffff));
                    // Entering a sizing loop or pushing against MinimumSize
                    // alone is not evidence that client geometry changed.
                    if (inSizeMove && sizingSeenInLoop && message.WParam.ToInt32() != 1 &&
                        observed.Width > 0 && observed.Height > 0 && observed != loopStartSize)
                        nativeResizeObserved = true;
                    if (!sizes.Contains(observed))
                    {
                        if (sizes.Count < MaxSizes) sizes.Add(observed);
                        else sizeLimitReached = true;
                    }
                    break;
                case 0x0232: // WM_EXITSIZEMOVE
                    Increment(ref exits); inSizeMove = false;
                    break;
            }
            base.WndProc(ref message);
        }
        private static void Increment(ref int count) { if (count < int.MaxValue) count++; }
        internal void Report(bool nativeHold) => Console.WriteLine(
            "OBSERVE layout-transition sizing-info; cumulative=True; native_hold=" + nativeHold +
            "; wm_entersizemove=" + enters + "; wm_sizing=" + sizing + "; wm_size=" + size +
            "; wm_exitsizemove=" + exits + "; distinct_sizes=" + sizes.Count +
            "; size_limit_reached=" + sizeLimitReached + "; in_size_move=" + inSizeMove +
            "; native_resize_observed=" + nativeResizeObserved);
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.HandleCreated -= HandleCreated;
            owner.HandleDestroyed -= HandleDestroyed;
            if (Handle != IntPtr.Zero) ReleaseHandle();
            sizes.Clear();
        }
    }

    private sealed class ScanGate : IDisposable
    {
        private int rounds, active;
        internal int Rounds => Volatile.Read(ref rounds);
        internal int Active => Volatile.Read(ref active);
        internal ManualResetEventSlim Release { get; } = new();
        internal ScanResult Scan(string path, IProgress<ScanProgress> progress, CancellationToken token, LiveScanSession live)
        {
            int round = Interlocked.Increment(ref rounds), paused = 0, preparing = 0;
            Interlocked.Increment(ref active); Release.Reset();
            try
            {
                return new DiskScanner(1).Scan(path, new ForwardProgress(value =>
                {
                    progress.Report(value with { CurrentPath = Alias(value.CurrentPath, path), Message = value.Message == null ? null : Alias(value.Message, path) });
                    if (round < 2) return;
                    if (value.Files == 0 && value.Folders == 0 && Interlocked.Exchange(ref preparing, 1) == 0)
                    {
                        Console.WriteLine("STAGE layout-transition preview stage=preparing; round=" + round);
                        token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(1200)); token.ThrowIfCancellationRequested();
                    }
                    else if (Volatile.Read(ref paused) == 0 && live.Capture(path, token)?.Directory.Children.Any(entry => entry.Name == "A-child") == true &&
                        Interlocked.Exchange(ref paused, 1) == 0)
                    {
                        Console.WriteLine("STAGE layout-transition preview stage=paused; round=" + round);
                        if (!Release.Wait(TimeSpan.FromMinutes(2), token))
                            Console.WriteLine("STAGE layout-transition preview stage=automatic-release; round=" + round);
                    }
                }), token, live);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public void Dispose() => Release.Dispose();
    }
    private sealed class ForwardProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    { public void Report(ScanProgress value) => report(value); }
    private sealed class SyntheticActivity : IDiskActivitySource
    {
        public DiskActivitySample Collect(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return new(DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, DiskActivityStatus.Available); }
        public void Dispose() { }
    }

    private sealed class Fixture
    {
        private const string Prefix = "AsukaLens-layout-transition-";
        private readonly string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private readonly string nonce = Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, long> files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        private bool created;
        private string Root => Path.Combine(temp, Prefix + nonce);
        internal string Scope => Path.Combine(Root, "AsukaLens-Demo");
        internal string Settings => Path.Combine(Root, "settings");
        internal void Create()
        {
            if (Directory.Exists(Root) || File.Exists(Root)) throw new InvalidOperationException("FixtureCollision");
            Directory.CreateDirectory(Root); created = true;
            Write(".owner", System.Text.Encoding.UTF8.GetBytes(nonce));
            foreach (string relative in new[] { "settings", "AsukaLens-Demo", Path.Combine("AsukaLens-Demo", "A-child") })
            { Directory.CreateDirectory(Path.Combine(Root, relative)); directories.Add(relative); }
            for (int index = 0; index < 1105; index++)
                Write(Path.Combine("AsukaLens-Demo", $"sample-{index:0000}.txt"), new byte[17 + index % 31]);
            Write(Path.Combine("AsukaLens-Demo", "A-child", "leaf.txt"), new byte[256]);
        }
        private void Write(string relative, byte[] bytes)
        { File.WriteAllBytes(Path.Combine(Root, relative), bytes); files.Add(relative, bytes.Length); }
        internal void Cleanup()
        {
            if (!created) return;
            string resolved = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(resolved), temp, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(resolved) != Prefix + nonce || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0 ||
                File.ReadAllText(Path.Combine(resolved, ".owner")) != nonce) throw new InvalidOperationException("CleanupBoundary");
            var pending = new Stack<string>(); pending.Push(resolved);
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.TryPop(out var directory))
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    string relative = Path.GetRelativePath(resolved, entry);
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("CleanupLink");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!directories.Contains(relative)) throw new InvalidOperationException("CleanupUnknownDirectory");
                        seenDirectories.Add(relative); pending.Push(entry);
                    }
                    else
                    {
                        if (!files.TryGetValue(relative, out long length) || new FileInfo(entry).Length != length)
                            throw new InvalidOperationException("CleanupUnknownFile");
                        seenFiles.Add(relative);
                    }
                }
            if (!seenFiles.SetEquals(files.Keys) || !seenDirectories.SetEquals(directories)) throw new InvalidOperationException("CleanupChanged");
            foreach (string relative in files.Keys) File.Delete(Path.Combine(resolved, relative));
            foreach (string relative in directories.OrderByDescending(value => value.Length)) Directory.Delete(Path.Combine(resolved, relative), false);
            Directory.Delete(resolved, false); created = false;
            Console.WriteLine("CLEANUP layout-transition preview completed=True");
        }
    }
}
