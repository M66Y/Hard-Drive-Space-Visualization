using System.Reflection;
using System.Runtime.Loader;

// Loads the shipped application assembly, not the linked copies in the regression host.
// The host is copied into a disposable payload clone and uses that clone's bundled runtime.
internal static class PackageSmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static object? Member(object value, string name) =>
        value.GetType().GetProperty(name, Members)?.GetValue(value) ?? value.GetType().GetField(name, Members)?.GetValue(value);
    private static object? Call(object value, string method, params object?[] args) =>
        value.GetType().GetMethod(method, Members)!.Invoke(value, args);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static int Run()
    {
        string payload = AppContext.BaseDirectory;
        string root = Path.Combine(Path.GetTempPath(), "AsukaLens-package-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Exception? failure = null;
        try
        {
            Check(Path.GetDirectoryName(typeof(object).Assembly.Location) == payload.TrimEnd(Path.DirectorySeparatorChar), "CoreCLR must come from the disposable payload");
            Check(Path.GetDirectoryName(typeof(Form).Assembly.Location) == payload.TrimEnd(Path.DirectorySeparatorChar), "WinForms must come from the disposable payload");
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(payload, "DiskAtlas.dll"));
            Check(assembly.GetName().Name == "DiskAtlas", "Wrong shipped assembly");
            string fixture = Path.Combine(root, "demo-files");
            Directory.CreateDirectory(Path.Combine(fixture, "child"));
            for (int i = 0; i < 120; i++) File.WriteAllBytes(Path.Combine(fixture, $"sample-{i:000}.txt"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(fixture, "child", "photo.png"), new byte[8192]);
            var thread = new Thread(() =>
            {
                try
                {
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Type main = assembly.GetType("DiskAtlas.MainForm", true)!;
                    Type diagnostic = assembly.GetType("DiskAtlas.DiagnosticService", true)!;
                    using var service = (IDisposable)Activator.CreateInstance(diagnostic, [Path.Combine(root, "app-data"), null])!;
                    ((Task)Member(service, "Ready")!).GetAwaiter().GetResult();
                    using var form = (Form)Activator.CreateInstance(main, [null, false, Path.Combine(root, "background.dat"), service])!;
                    form.Text = "AsukaLens · 安装包合成验收";
                    form.Shown += async (_, _) =>
                    {
                        try
                        {
                            await (Task)Call(form, "ScanFolder", fixture)!;
                            object scan = Member(form, "scan")!;
                            Check(scan != null && Convert.ToInt64(Member(Member(scan, "Root")!, "FileCount")) == 121, "Shipped scan result");
                            for (int mode = 0; mode < 3; mode++)
                            {
                                Call(form, "SetMode", mode);
                                for (int metric = 0; metric < 2; metric++)
                                {
                                    ((ComboBox)Member(form, "metricCombo")!).SelectedIndex = metric;
                                    await (Task)Call(form, "UpdateViewAsync", false)!;
                                    Check(((DataGridView)Member(form, "grid")!).RowCount > 0, "Shipped view/metric empty");
                                }
                            }
                            var pending = (Task)Call(form, "ScanFolder", fixture)!;
                            typeof(Control).GetMethod("OnClick", Members)!.Invoke(Member(form, "cancel"), [EventArgs.Empty]);
                            await pending;
                            Check(ReferenceEquals(scan, Member(form, "scan")), "Cancel must preserve prior result");
                            File.WriteAllText(Path.Combine(fixture, "new.txt"), "refresh sample");
                            await (Task)Call(form, "ScanFolder", fixture)!;
                            Check(Convert.ToInt64(Member(Member(Member(form, "scan")!, "Root")!, "FileCount")) == 122, "Shipped refresh result");
                            using (var dialog = (Form)Activator.CreateInstance(assembly.GetType("DiskAtlas.DiagnosticsDialog", true)!, [service])!)
                            { dialog.Show(form); await Task.Delay(250); Check(dialog.Visible, "Diagnostics opens"); dialog.Close(); }
                            var view = assembly.GetType("DiskAtlas.BackgroundView", true)!.GetProperty("Default", Members)!.GetValue(null);
                            using (var dialog = (Form)Activator.CreateInstance(assembly.GetType("DiskAtlas.BackgroundDialog", true)!,
                                [form.BackgroundImage!, view, Color.Coral, form.ClientSize, Path.Combine(root, "background.dat"), true, null, null])!)
                            { dialog.Show(form); await Task.Delay(200); Check(dialog.Visible, "Background settings opens"); dialog.Close(); }
                            Console.WriteLine("PASS shipped DLL: actual scan, 3 views x 2 metrics, cancel, refresh, background and diagnostics; bundled runtime only; isolated data");
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { form.Close(); }
                    };
                    Application.Run(form);
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Package smoke timed out");
            if (failure != null) throw new InvalidOperationException("Package workflow failed", failure);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            string boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cleanup boundary");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
