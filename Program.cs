namespace DiskAtlas;

internal static class Program
{
    private static int reportingError;
    private static DiagnosticService? diagnostics;
    private static Mutex? installationGuard;
    [MTAThread]
    private static void Main(string[] args)
    {
        if (FastScanBridge.IsWorkerRequest(args))
        {
            Environment.ExitCode = FastScanBridge.RunWorker(args);
            return;
        }
        // Keep the worker's process entry MTA: waiting on an STA entry thread
        // can initialize COM security before VSS establishes its own settings.
        // Desktop controls, clipboard and folder dialogs still require STA.
        var desktop = new Thread(() => RunDesktop(args)) { Name = "AsukaLens UI" };
        desktop.SetApartmentState(ApartmentState.STA);
        desktop.Start();
        desktop.Join();
    }

    private static void RunDesktop(string[] args)
    {
        try
        {
            // Process lifetime, including error dialogs and diagnostic shutdown. Multiple windows remain supported.
            installationGuard = new Mutex(false, @"Local\AsukaLens.Running");
            var data = AppDataLocation.Resolve(Environment.GetEnvironmentVariable(AppDataLocation.EnvironmentVariable));
            diagnostics = new DiagnosticService(data.Root);
            ApplicationConfiguration.Initialize();
            Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F));
            Application.ThreadException += async (_, e) => await ReportErrorAsync(e.Exception, true);
            using var form = new MainForm(args.FirstOrDefault(a => !a.StartsWith("--")), loadSavedBackground: true,
                backgroundSettingsPath: data.BackgroundPath, diagnostics: diagnostics);
            if (data.IsIsolated) form.Text += " · 隔离数据";
            Application.Run(form);
        }
        catch (Exception ex) { ReportErrorAsync(ex, false).GetAwaiter().GetResult(); }
        finally { diagnostics?.Dispose(); GC.KeepAlive(installationGuard); }
    }

    private static async Task ReportErrorAsync(Exception error, bool captureContext)
    {
        if (Interlocked.Exchange(ref reportingError, 1) != 0) return;
        try
        {
            var report = diagnostics == null
                ? new ErrorReportResult(DiagnosticCodec.Classify(error).ToString(), $"0x{error.HResult:X8}", false, null)
                : await diagnostics.ReportErrorAsync(error).ConfigureAwait(captureContext);
            MessageBox.Show(report.UserMessage, "AsukaLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Volatile.Write(ref reportingError, 0); }
    }
}
