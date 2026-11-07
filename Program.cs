namespace DiskAtlas;

internal static class Program
{
    private static int reportingError;
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F));
            Application.ThreadException += (_, e) => ReportError(e.Exception);
            Application.Run(new MainForm(args.FirstOrDefault(a => !a.StartsWith("--")), loadSavedBackground: true));
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private static void ReportError(Exception error)
    {
        if (Interlocked.Exchange(ref reportingError, 1) != 0) return;
        try
        {
            var report = ErrorReport.Write(error);
            MessageBox.Show(report.UserMessage, "AsukaLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Volatile.Write(ref reportingError, 0); }
    }
}
