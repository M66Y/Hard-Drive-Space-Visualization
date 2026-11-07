namespace DiskAtlas;

internal static class Program
{
    private static int reportingError;
    private static DiagnosticService? diagnostics;
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            diagnostics = new DiagnosticService();
            ApplicationConfiguration.Initialize();
            Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F));
            Application.ThreadException += async (_, e) => await ReportErrorAsync(e.Exception, true);
            Application.Run(new MainForm(args.FirstOrDefault(a => !a.StartsWith("--")), loadSavedBackground: true, diagnostics: diagnostics));
        }
        catch (Exception ex) { ReportErrorAsync(ex, false).GetAwaiter().GetResult(); }
        finally { diagnostics?.Dispose(); }
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
