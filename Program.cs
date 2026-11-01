namespace DiskAtlas;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F));
            Application.ThreadException += (_, e) => ReportError(e.Exception);
            Application.Run(new MainForm(args.FirstOrDefault(a => !a.StartsWith("--"))));
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private static void ReportError(Exception error)
    {
        string log = Path.Combine(Path.GetTempPath(), "AsukaLens-error.log");
        try { File.WriteAllText(log, error.ToString()); } catch { }
        MessageBox.Show($"程序遇到问题：{error.Message}\n\n详情已保存到：{log}", "AsukaLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
