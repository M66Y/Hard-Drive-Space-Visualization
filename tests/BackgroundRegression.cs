using DiskAtlas;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;

internal static class BackgroundRegression
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static T Field<T>(object item, string name) => (T)item.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item)!;
    private static void Reject(Action work)
    {
        try { work(); }
        catch (Exception error) when (BackgroundSettings.IsFileError(error)) { return; }
        throw new Exception("Invalid input or denied write unexpectedly succeeded");
    }
    private static readonly BackgroundView CustomView = new(.72f, .26f, 1.8f, Color.SteelBlue.ToArgb());

    public static int Run(bool dpi96)
    {
        string root = Path.Combine(Path.GetTempPath(), "AsukaLens-background-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string imagePath = Path.Combine(root, "source.png"), settings = Path.Combine(root, "background.dat");
            using (var source = new Bitmap(600, 300))
            { using var g = Graphics.FromImage(source); g.Clear(Color.SteelBlue); source.Save(imagePath, ImageFormat.Png); }
            using var image = BackgroundSettings.Import(imagePath);
            var custom = image with { View = CustomView };
            BackgroundSettings.Save(settings, custom);
            var valid = File.ReadAllBytes(settings);
            Check(BitConverter.ToInt32(valid) == 2, "New saves lack integrity format");
            foreach (int length in new[] { 0, 3, 20, valid.Length - 1 })
            { File.WriteAllBytes(settings, valid[..length]); Reject(() => BackgroundSettings.Load(settings)?.Dispose()); }
            var damaged = (byte[])valid.Clone(); damaged[^10] ^= 1;
            File.WriteAllBytes(settings, damaged); Reject(() => BackgroundSettings.Load(settings)?.Dispose());
            damaged = (byte[])valid.Clone(); BitConverter.GetBytes(99).CopyTo(damaged, 0);
            File.WriteAllBytes(settings, damaged); Reject(() => BackgroundSettings.Load(settings)?.Dispose());
            Legacy(settings, custom, 1);
            using (var old = BackgroundSettings.Load(settings)) Check(old?.View == CustomView, "Legacy preview settings no longer load");
            Legacy(settings, custom with { View = CustomView with { X = float.NaN } }, 1);
            Reject(() => BackgroundSettings.Load(settings)?.Dispose());
            Legacy(settings, custom, 2); Reject(() => BackgroundSettings.Load(settings)?.Dispose());
            File.WriteAllBytes(settings, valid);
            Reject(() => BackgroundSettings.Save(settings, custom with { View = CustomView with { Zoom = 4 } }));
            Check(File.ReadAllBytes(settings).SequenceEqual(valid), "Invalid save changed old data");
            Console.WriteLine("PASS background version compatibility, checksum, truncation and invalid fields");

            using (var locked = new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.Read))
            { Reject(() => BackgroundSettings.Save(settings, image)); Reject(() => BackgroundSettings.Reset(settings)); }
            Check(File.ReadAllBytes(settings).SequenceEqual(valid), "Failed replace/reset damaged previous settings");
            Check(!Directory.GetFiles(root, "*.tmp").Any(), "Save failure leaked temporary data");
            using (var locked = new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None))
                Reject(() => BackgroundSettings.Load(settings)?.Dispose());
            Console.WriteLine("PASS background denied writes/read and reset preserve existing data and clean temporary files");

            string extreme = Path.Combine(root, "extreme.png");
            using (var thin = new Bitmap(1, 6000)) thin.Save(extreme, ImageFormat.Png);
            using (var loaded = BackgroundSettings.Import(extreme)) Check(loaded.Image.Size == new Size(1, 2048), "Extreme aspect ratio failed");
            string unsupported = Path.Combine(root, "unsupported.gif");
            image.Image.Save(unsupported, ImageFormat.Gif); Reject(() => BackgroundSettings.Import(unsupported).Dispose());
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { BackgroundSettings.Import(imagePath, cancelled.Token).Dispose(); throw new Exception("Cancelled import succeeded"); }
                catch (OperationCanceledException) { }
            }
            CheckOrientations(root);
            Console.WriteLine("PASS background extreme ratio, unsupported format, cancellation and all 8 EXIF orientations");

            RunUi(() => CheckDialogs(root, imagePath, settings, image), dpi96);
            File.Delete(imagePath);
            BackgroundSettings.Save(settings, custom);
            Child(settings, "custom"); Child(settings, "custom");
            BackgroundSettings.Reset(settings); Child(settings, "default");
            File.WriteAllBytes(settings, [1, 2, 3]); Child(settings, "corrupt");
            Console.WriteLine("PASS background independent-process startup, source deletion, default reset and corrupt fallback");
            Console.WriteLine("All 7 background regression groups passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "AsukaLens-background-tests-");
            if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup");
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Legacy(string path, BackgroundChoice choice, byte manual)
    {
        using var file = File.Create(path); using var writer = new BinaryWriter(file);
        writer.Write(1); writer.Write(choice.View.X); writer.Write(choice.View.Y); writer.Write(choice.View.Zoom);
        writer.Write(manual); writer.Write(choice.View.AccentArgb ?? 0);
        using var png = new MemoryStream(); choice.Image.Save(png, ImageFormat.Png); png.WriteTo(file);
    }

    private static void CheckOrientations(string root)
    {
        using var source = new Bitmap(80, 40);
        using (var g = Graphics.FromImage(source))
        {
            g.Clear(Color.Red); g.FillRectangle(Brushes.Blue, 40, 0, 40, 20);
            g.FillRectangle(Brushes.Lime, 0, 20, 40, 20); g.FillRectangle(Brushes.Yellow, 40, 20, 40, 20);
        }
        using var stream = new MemoryStream(); source.Save(stream, ImageFormat.Jpeg);
        byte[] jpeg = stream.ToArray();
        // Expected top-left / top-right / bottom-left / bottom-right from EXIF row/column definitions.
        int[][] corners = [[0,1,2,3],[1,0,3,2],[3,2,1,0],[2,3,0,1],[0,2,1,3],[2,0,3,1],[3,1,2,0],[1,3,0,2]];
        Color[] colors = [Color.Red, Color.Blue, Color.Lime, Color.Yellow];
        for (byte orientation = 1; orientation <= 8; orientation++)
        {
            byte[] exif = [69,120,105,102,0,0,73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,orientation,0,0,0,0,0,0,0];
            string path = Path.Combine(root, "oriented.jpg");
            using (var file = File.Create(path))
            { file.Write(jpeg.AsSpan(0,2)); file.Write([255,225,0,34]); file.Write(exif); file.Write(jpeg.AsSpan(2)); }
            using var loaded = BackgroundSettings.Import(path);
            var bitmap = loaded.Image;
            Check(bitmap.Size == (orientation < 5 ? new Size(80,40) : new Size(40,80)), "EXIF dimensions wrong");
            Point[] points = [new(5,5),new(bitmap.Width-6,5),new(5,bitmap.Height-6),new(bitmap.Width-6,bitmap.Height-6)];
            for (int i = 0; i < 4; i++)
            {
                var actual = bitmap.GetPixel(points[i].X,points[i].Y); var expected = colors[corners[orientation-1][i]];
                Check(Math.Abs(actual.R-expected.R)+Math.Abs(actual.G-expected.G)+Math.Abs(actual.B-expected.B)<70, "EXIF corner wrong");
            }
            Check(!bitmap.PropertyIdList.Contains(0x0112), "Processed bitmap retained source orientation metadata");
        }
    }

    private static async Task Wait(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("UI operation timed out"); await Task.Delay(20); }
    }

    private static void RunUi(Func<Task> work, bool dpi96)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            using var host = new Form { Text = "AsukaLens background regression", Size = new Size(320,180) };
            host.Shown += async (_, _) => { try { await work(); } catch (Exception e) { failure=e; } finally { host.Close(); } };
            Application.Run(host);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new Exception("Background UI timed out");
        if (failure != null) throw new Exception("Background UI failed", failure);
    }

    private static async Task Dialog(BackgroundDialog dialog, Func<BackgroundDialog, Task> steps)
    {
        var done = new TaskCompletionSource(); Exception? failure = null;
        dialog.Shown += async (_, _) =>
        { try { await steps(dialog); } catch (Exception e) { failure=e; } finally { dialog.Close(); done.SetResult(); } };
        dialog.ShowDialog(); await done.Task;
        if (failure != null) throw failure;
    }

    private static async Task CheckDialogs(string root, string source, string path, BackgroundChoice image)
    {
        BackgroundDialog New() => new(image.Image, CustomView, image.AutomaticAccent, new Size(1440,940),path);
        var before = File.ReadAllBytes(path);
        using (var dialog = New()) await Dialog(dialog, async d =>
        {
            await d.ImportImageAsync(source);
            Field<TrackBar>(d,"zoom").Value = 180;
            Field<NumericUpDown>(d,"focusX").Value = 72; Field<NumericUpDown>(d,"focusY").Value = 26;
            Field<CheckBox>(d,"automatic").Checked = false;
            foreach (var size in new[] { new Size(570,410), new Size(800,560) })
            {
                d.Size = size; d.PerformLayout();
                using var bitmap = new Bitmap(d.Width,d.Height); d.DrawToBitmap(bitmap,d.ClientRectangle);
                Check(Field<FlowLayoutPanel>(d,"commands").Bottom<=d.ClientSize.Height, "Commands clipped");
            }
            Field<Button>(d,"apply").PerformClick(); await Wait(()=>!d.IsBusy);
            Check(d.DialogResult == DialogResult.OK && d.Result?.View == CustomView, "Apply did not preserve chosen view");
            d.Result?.Dispose();
        });
        using (var saved=BackgroundSettings.Load(path)) Check(saved?.View == CustomView,"Dialog save failed");
        Console.WriteLine("PASS background real dialog apply, controls, focus and resized preview");

        before = File.ReadAllBytes(path);
        using (var dialog = New()) await Dialog(dialog, async d =>
        {
            await d.RestoreDefaultAsync();
            Check(File.ReadAllBytes(path).SequenceEqual(before),"Default preview changed persisted settings");
            Field<Button>(d,"cancel").PerformClick();
        });
        Check(File.ReadAllBytes(path).SequenceEqual(before),"Cancel changed saved settings");
        using (var dialog = New()) await Dialog(dialog, async d =>
        {
            using (var locked = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                await d.ApplyAsync();
                Check(d.DialogResult!=DialogResult.OK && Field<Label>(d,"status").Text.Contains("保存背景失败"),"Save failure is not specific");
                await d.RestoreDefaultAsync(); await d.ApplyAsync();
                Check(!d.RestoredDefault && Field<Label>(d,"status").Text.Contains("恢复默认设置失败"),"Reset failure hidden");
            }
            Check(File.ReadAllBytes(path).SequenceEqual(before),"Failed save/reset changed settings");
            await d.ApplyAsync(); Check(d.RestoredDefault && !File.Exists(path),"Default retry failed");
        });
        Console.WriteLine("PASS background cancel/default preview, denied save/reset feedback and retry");

        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(); Bitmap? late = null;
        using (var dialog = new BackgroundDialog(image.Image,CustomView,image.AutomaticAccent,new Size(1440,940),path,
            importImage: (_, _) => { entered.SetResult(); gate.Wait(); late = new Bitmap(20,20); return new(late,BackgroundView.Default,Color.Blue); }))
        {
            dialog.Show(); var pending=dialog.ImportImageAsync(source); await entered.Task;
            try
            {
                await dialog.ImportImageAsync(source); // Busy duplicate must not enqueue another import.
                dialog.Close(); dialog.Dispose();
            }
            finally { gate.Set(); }
            await pending;
            Check(late!=null,"Delayed import missing");
            Reject(()=>late!.GetPixel(0,0));
        }
        Theme.SetAccent(Color.SteelBlue);
        Check(ScanProgressControl.TrackStart==Theme.Accent && ScanProgressControl.TrackEnd!=Color.FromArgb(237,156,104),"Progress ignored custom theme");
        Theme.SetAccent(null);
        Check(ScanProgressControl.TrackEnd==Color.FromArgb(237,156,104),"Default progress color not restored");
        Check(File.Exists(source),"Default reset deleted source");
        Console.WriteLine("PASS background late import disposal, duplicate/close isolation and progress theme reset");
    }

    private static void Child(string path, string expected)
    {
        // Keep startup checks on the same dotnet host as the parent test process.
        string host = Environment.ProcessPath is { } executable &&
            string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? executable : "dotnet";
        var start = new ProcessStartInfo(host) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--background-child"); start.ArgumentList.Add(path); start.ArgumentList.Add(expected);
        using var process=Process.Start(start)!;
        if (!process.WaitForExit(20000)) { process.Kill(entireProcessTree:true); throw new Exception("Startup child timed out"); }
        Check(process.ExitCode==0,"Startup child failed: "+process.StandardError.ReadToEnd());
        Check(process.StandardOutput.ReadToEnd().Contains($"RUNTIME {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription};"), "Startup child used a different runtime");
    }

    public static int Preview(string root)
    {
        Directory.CreateDirectory(root);
        string source=Path.Combine(root,"demo-background.png");
        using (var bitmap=new Bitmap(1200,800))
        {
            using var g=Graphics.FromImage(bitmap);
            using var fill=new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0,0,1200,800),Color.FromArgb(12,35,80),Color.FromArgb(72,150,172),30f);
            g.FillRectangle(fill,0,0,1200,800);
            bitmap.Save(source,ImageFormat.Png);
        }
        var thread=new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
            using var form=new MainForm(null,true,Path.Combine(root,"background.dat")) { Text="AsukaLens · 背景验收" };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return 0;
    }

    public static int RunChild(string path, string expected)
    {
        try
        {
            RunUi(async () =>
            {
                using var form=new MainForm(null,loadSavedBackground:true,backgroundSettingsPath:path);
                form.Show(); await Wait(()=>Field<ActionButton>(form,"backgroundSettings").Enabled);
                Check(form.HasCustomBackground == (expected=="custom"),"Startup background mode wrong");
                if (expected=="custom")
                    Check(form.BackgroundCrop==CustomView && Theme.Accent==Theme.ReadableAccent(Color.SteelBlue),"Startup crop/color lost");
                if (expected=="corrupt") Check(Field<Label>(form,"status").Text.Contains("无法读取"),"Corrupt settings not explained");
                using var bitmap=new Bitmap(form.Width,form.Height); form.DrawToBitmap(bitmap,form.ClientRectangle);
                form.Close();
            },false);
            return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
