using DiskAtlas;
using System.Collections;
using System.Reflection;

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
        if (args.Length is < 2 or > 4 || args[0] != "--stage-one-preview" ||
            args[1] is not ("baseline" or "a" or "b") ||
            args.Skip(2).Any(value => value is not ("--minimum" or "--dpi-96")) ||
            args.Skip(2).Distinct(StringComparer.Ordinal).Count() != args.Length - 2)
        {
            Console.Error.WriteLine("FAIL stage-one preview invocation");
            return 2;
        }

        var fixture = new Fixture();
        string? previousData = Environment.GetEnvironmentVariable(AppDataLocation.EnvironmentVariable);
        bool uiStarted = false, uiExited = false;
        int result = 1;
        Exception? failure = null;
        var stage = PreviewStage.CreateFixture;
        try
        {
            fixture.Create();
            stage = PreviewStage.ConfigureData;
            Environment.SetEnvironmentVariable(AppDataLocation.EnvironmentVariable, fixture.Settings);
            stage = PreviewStage.StartUiThread;
            var thread = new Thread(() =>
            {
                var uiStage = PreviewStage.UiBootstrap;
                try { Show(args[1], args.Contains("--minimum"), args.Contains("--dpi-96"), fixture, value => uiStage = value); }
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

    private static void Show(string variant, bool minimum, bool virtualized96Dpi, Fixture fixture, Action<PreviewStage> setStage)
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
        var constructor = formType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 4);
        using var form = (Form)constructor.Invoke([null, false, Path.Combine(fixture.Settings, "background.dat"), null]);
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
        using var listenerStop = new CancellationTokenSource();
        var listener = Task.Run(async () =>
        {
            try
            {
                while (!listenerStop.IsCancellationRequested)
                {
                    string? line = await Console.In.ReadLineAsync(listenerStop.Token).ConfigureAwait(false);
                    if (line == null) return; // EOF stops input only; it never closes the preview.
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
        aliases.Tick += (_, _) => AliasPaths(formType, form, controls, fixture.Scope);
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
                float scale = form.DeviceDpi / 96f;
                var area = Screen.FromControl(form).WorkingArea;
                var desired = minimum ? new Size(1120, 780) : new Size(1440, 940);
                var size = new Size(Math.Min((int)(desired.Width * scale), area.Width), Math.Min((int)(desired.Height * scale), area.Height));
                form.MinimumSize = new Size(Math.Min((int)(1120 * scale), size.Width), Math.Min((int)(780 * scale), size.Height));
                form.Size = size;
                form.Location = new Point(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2);
                aliases.Start(); lifetime.Start();
                if (Volatile.Read(ref closeRequested) != 0) { RequestClose(); return; }
                workStage = PreviewStage.Scan; setStage(workStage);
                activeScan = (Task)formType.GetMethod("ScanFolder", InstanceFields)!.Invoke(form, [fixture.Scope])!;
                await activeScan;
                if (form.IsDisposed || closing) return;
                workStage = PreviewStage.Query; setStage(workStage);
                await (Task)formType.GetMethod("UpdateViewAsync", InstanceFields)!.Invoke(form, [false])!;
                if (form.IsDisposed || closing) return;
                workStage = PreviewStage.Select; setStage(workStage);
                if (formType.GetField("sortedRows", InstanceFields)?.GetValue(form) is IList { Count: > 0 } rows)
                    formType.GetMethod("SelectItem", InstanceFields)!.Invoke(form, [rows[0], true]);
                AliasPaths(formType, form, controls, fixture.Scope);
                form.Text = "AsukaLens";
                form.Activate();
                await Task.Delay(350);
                if (form.IsDisposed || closing) return;
                workStage = PreviewStage.Ready; setStage(workStage);
                form.PerformLayout(); form.Refresh();
                var grid = controls.OfType<DataGridView>().Single();
                int firstRowHeight = grid.Rows.Count == 0 ? 0 : grid.Rows[0].Height;
                Console.WriteLine("READY stage-one preview variant=" + variant + "; actual owned directory scan; displayed paths aliased; diagnostics unavailable" +
                    "; dpi=" + form.DeviceDpi + "; client_width=" + form.ClientSize.Width + "; client_height=" + form.ClientSize.Height +
                    "; grid_template_height=" + grid.RowTemplate.Height + "; grid_first_row_height=" + firstRowHeight);
                Console.Out.Flush();
                setStage(PreviewStage.MessageLoop);
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

    private sealed class Fixture
    {
        private const string Prefix = "AsukaLens-stage-one-";
        private readonly string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private readonly string nonce = Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, long> files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        private string Root => Path.Combine(temp, Prefix + nonce);
        internal string Scope => Path.Combine(Root, "AsukaLens-Demo");
        internal string Settings => Path.Combine(Root, "settings");
        internal bool Created { get; private set; }
        internal void Create()
        {
            if (File.Exists(Root) || Directory.Exists(Root)) throw new InvalidOperationException("PreviewFixtureCollision");
            Directory.CreateDirectory(Root); Created = true;
            File.WriteAllText(Path.Combine(Root, ".owner"), nonce);
            files.Add(".owner", new FileInfo(Path.Combine(Root, ".owner")).Length);
            Folder("settings"); Folder("AsukaLens-Demo");
            const int mb = 1024 * 1024;
            Add("视频 Videos", ("travel-demo.mp4", 8 * mb), ("recording-demo.mp4", 4 * mb));
            Add("图片 Pictures", ("photo-collection.jpg", 3 * mb), ("wallpaper-demo.png", 2 * mb));
            Add("应用 Apps", ("app-demo.exe", 4 * mb), ("tools-demo.zip", 2 * mb));
            Add("项目 Projects", ("project-backup.zip", 3 * mb), ("带有较长名称的项目说明与实现示例 source-demo.cs", 32768));
            Add("文档 Documents", ("guide-demo.pdf", mb), ("report-demo.docx", mb / 2));
            string nested = Path.Combine("AsukaLens-Demo", "项目 Projects", "源代码 Source");
            Folder(nested); Write(Path.Combine(nested, "README.md"), 4096); Write(Path.Combine(nested, "main.cs"), 12288);
            Folder(Path.Combine("AsukaLens-Demo", "文档 Documents", "空文件夹 Empty"));
            var modified = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
            foreach (string path in files.Keys) File.SetLastWriteTimeUtc(Path.Combine(Root, path), modified);
            foreach (string path in directories.OrderByDescending(value => value.Length)) Directory.SetLastWriteTimeUtc(Path.Combine(Root, path), modified);
        }
        private void Add(string name, params (string Name, int Bytes)[] values)
        {
            string path = Path.Combine("AsukaLens-Demo", name); Folder(path);
            foreach (var value in values) Write(Path.Combine(path, value.Name), value.Bytes);
        }
        private void Folder(string relative) { Directory.CreateDirectory(Path.Combine(Root, relative)); directories.Add(relative); }
        private void Write(string relative, int length)
        {
            if (files.Values.Sum() + length >= 64L * 1024 * 1024) throw new InvalidOperationException("PreviewFixtureBudget");
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
