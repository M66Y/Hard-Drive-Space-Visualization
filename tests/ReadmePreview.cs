using DiskAtlas;
using System.Reflection;

// Renders the real form with an in-memory sample; no disk scan or personal settings.
internal static class ReadmePreview
{
    public static int Run(string output)
    {
        try { return Render(output); }
        catch (Exception error)
        {
            Console.Error.WriteLine($"README preview failed ({error.GetType().Name}). Use docs/images/overview.png from the project root; the target must not exist.");
            return 1;
        }
    }

    private static int Render(string output)
    {
        string target = Path.GetFullPath(output);
        string expected = Path.GetFullPath(Path.Combine("docs", "images", "overview.png"));
        if (!target.Equals(expected, StringComparison.OrdinalIgnoreCase) || File.Exists(target))
            throw new ArgumentException("Run from the project root with docs/images/overview.png; existing images are never overwritten.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.DpiUnaware); Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var root = new DiskEntry(@"X:\AsukaLens-Demo", true);
            void Folder(string name, params (string Name, long Bytes)[] files)
            {
                var folder = new DiskEntry(Path.Combine(root.Path, name), true, root);
                foreach (var (fileName, bytes) in files)
                    folder.MutableChildren.Add(new DiskEntry(Path.Combine(folder.Path, fileName), false, folder)
                    { Size = bytes, AllocatedSize = bytes, FileCount = 1,
                      Modified = new DateTime(2026, 1, 1), Storage = new(new(1, (ulong)(root.FileCount + folder.FileCount++), 0), bytes, 1, false) });
                folder.Size = folder.AllocatedSize = files.Sum(f => f.Bytes);
                root.MutableChildren.Add(folder); root.Size += folder.Size; root.AllocatedSize += folder.AllocatedSize;
                root.FileCount += folder.FileCount; root.FolderCount++;
            }
            const long mb = 1024 * 1024;
            Folder("视频 Videos", ("travel-demo.mp4", 4200 * mb), ("recording-demo.mp4", 2400 * mb));
            Folder("图片 Pictures", ("photo-collection.zip", 2100 * mb), ("wallpaper-demo.png", 160 * mb));
            Folder("应用 Apps", ("app-demo.exe", 1600 * mb), ("tools-demo.zip", 800 * mb));
            Folder("项目 Projects", ("project-backup.zip", 900 * mb), ("source-demo.cs", 2 * mb));
            Folder("文档 Documents", ("notes-demo.pdf", 320 * mb), ("report-demo.docx", 70 * mb));
            using var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("DiskAtlas.AppIcon")!;
            using var icon = new Icon(iconStream);
            using var form = new MainForm(null) { Icon = icon, ScanRunner = (_, _, _, _) => new(root, TimeSpan.FromSeconds(1.2), 0, []) };
            form.Shown += async (_, _) =>
            {
                try
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    await (Task)typeof(MainForm).GetMethod("ScanFolder", flags)!.Invoke(form, [root.Path])!;
                    await (Task)typeof(MainForm).GetMethod("UpdateViewAsync", flags)!.Invoke(form, [false])!;
                    form.Text = "AsukaLens · 合成演示数据 / Synthetic demo";
                    await Task.Delay(200);
                    form.PerformLayout(); form.Refresh();
                    using var image = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        image.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine($"PASS README preview: {image.Width}x{image.Height}, synthetic data only");
                }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); }
            };
            Application.Run(form);
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("README preview timed out");
        if (failure != null) throw new Exception("README preview failed", failure);
        return 0;
    }
}
