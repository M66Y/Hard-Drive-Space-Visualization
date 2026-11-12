using DiskAtlas;
using System.Reflection;
using System.Runtime.InteropServices;

// A visible, synthetic layout study. It neither samples disks nor loads saved settings.
internal static class DiskActivityLayoutPreview
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    public static int Run(string output, bool minimum = false)
    {
        try { return Render(output, minimum); }
        catch
        {
            Console.Error.WriteLine("FAIL DISK_ACTIVITY_LAYOUT_PREVIEW");
            return 1;
        }
    }

    private static int Render(string output, bool minimum)
    {
        string target = Path.GetFullPath(output);
        string expected = Path.GetFullPath(Path.Combine("artifacts", minimum ? "monitor-minimum.png" : "monitor-layout.png"));
        if (!target.Equals(expected, StringComparison.OrdinalIgnoreCase) || File.Exists(target))
            throw new InvalidOperationException();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F));
                var root = CreateSample();
                using var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("DiskAtlas.AppIcon")!;
                using var icon = new Icon(iconStream);
                using var form = new MainForm(null)
                {
                    Icon = icon,
                    ScanRunner = (_, _, _, _) => new(root, TimeSpan.FromSeconds(1.2), 0, [])
                };
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                        if (minimum) { form.Size = form.MinimumSize; form.Location = Point.Empty; }
                        await (Task)typeof(MainForm).GetMethod("ScanFolder", flags)!.Invoke(form, [root.Path])!;
                        await (Task)typeof(MainForm).GetMethod("UpdateViewAsync", flags)!.Invoke(form, [false])!;
                        var main = Descendants(form).OfType<TableLayoutPanel>()
                            .Single(table => table.ColumnCount == 1 && table.RowCount == 9);
                        typeof(MainForm).GetField("activityMonitor", flags)!.SetValue(form,
                            new DiskActivityMonitor(_ => { }, (_, _) => null, _ => [], _ => throw new InvalidOperationException()));
                        var panel = Descendants(form).OfType<DiskActivityPanel>().Single();
                        var toggle = panel.Controls.OfType<Button>().Single();
                        toggle.PerformClick();
                        if (!panel.Expanded || Math.Abs(main.RowStyles[6].Height - 110 * form.DeviceDpi / 96f) > 1)
                            throw new InvalidOperationException();
                        var end = DateTimeOffset.UtcNow;
                        for (int i = 0; i < 60; i++)
                            panel.SetSample(new(end.AddSeconds(i - 59), 0,
                                12 + 10 * Math.Sin(i * .36) + (i is >= 20 and <= 27 ? 38 : 0) + (i is >= 42 and <= 47 ? 57 : 0),
                                24.8 * 1024 * 1024, 2.1 * 1024 * 1024, .4, DiskActivityStatus.Available));
                        ((Label)typeof(MainForm).GetField("subtitle", flags)!.GetValue(form)!).Text = "布局预览 · 合成数据，不代表当前磁盘活动";
                        form.Text = "AsukaLens";
                        form.Activate();
                        Cursor.Position = form.PointToScreen(new Point(12, 12));
                        await Task.Delay(1000);
                        form.PerformLayout();
                        form.Refresh();
                        using var image = new Bitmap(form.Width, form.Height);
                        using (var graphics = Graphics.FromImage(image))
                        {
                            IntPtr dc = graphics.GetHdc();
                            try { if (!PrintWindow(form.Handle, dc, 2)) throw new InvalidOperationException(); }
                            finally { graphics.ReleaseHdc(dc); }
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            image.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                        Console.WriteLine("PASS DISK_ACTIVITY_LAYOUT_PREVIEW production panel; synthetic data");
                    }
                    catch (Exception error) { failure = error; }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(25)) || failure != null) throw new InvalidOperationException();
        return 0;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static DiskEntry CreateSample()
    {
        const long mb = 1024 * 1024;
        var root = new DiskEntry(@"X:\AsukaLens-Demo", true);
        ulong identity = 1;
        void Folder(string name, params (string Name, long Bytes)[] files)
        {
            var folder = new DiskEntry(Path.Combine(root.Path, name), true, root);
            foreach (var (fileName, bytes) in files)
                folder.MutableChildren.Add(new DiskEntry(Path.Combine(folder.Path, fileName), false, folder)
                {
                    Size = bytes, AllocatedSize = bytes, FileCount = 1,
                    Modified = new DateTime(2026, 1, 1),
                    Storage = new(new(1, identity++, 0), bytes, 1, false)
                });
            folder.Size = folder.AllocatedSize = files.Sum(file => file.Bytes);
            folder.FileCount = files.Length;
            root.MutableChildren.Add(folder);
            root.Size += folder.Size;
            root.AllocatedSize += folder.AllocatedSize;
            root.FileCount += folder.FileCount;
            root.FolderCount++;
        }
        Folder("视频 Videos", ("travel-demo.mp4", 4200 * mb), ("recording-demo.mp4", 2400 * mb));
        Folder("图片 Pictures", ("photo-collection.zip", 2100 * mb), ("wallpaper-demo.png", 160 * mb));
        Folder("应用 Apps", ("app-demo.exe", 1600 * mb), ("tools-demo.zip", 800 * mb));
        Folder("项目 Projects", ("project-backup.zip", 900 * mb), ("source-demo.cs", 2 * mb));
        Folder("文档 Documents", ("notes-demo.pdf", 320 * mb), ("report-demo.docx", 70 * mb));
        return root;
    }

}
