using DiskAtlas;
using System.Reflection;

// In-process WinForms integration tests. No external app, Windows settings or user files are controlled.
internal static class UiRegression
{
    public static int Run(bool virtualized96Dpi = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "DiskAtlas-ui-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "A-child"));
        Exception? failure = null;
        try
        {
            for (int f = 0; f < 1105; f++) File.WriteAllBytes(Path.Combine(root, $"file-{f:0000}.txt"), new byte[17]);
            File.WriteAllBytes(Path.Combine(root, "A-child", "leaf.txt"), new byte[31]);
            var thread = new Thread(() =>
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(virtualized96Dpi ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var gate = new ScanGate();
                using var form = new MainForm(null) { ScanRunner = gate.Scan };
                T Field<T>(string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                object? Invoke(string name, params object[] args) => typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args);
                Task Scan() => (Task)Invoke("ScanFolder", root)!;
                async Task Wait(Func<bool> ready, string message)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(8);
                    while (!ready())
                    {
                        if (DateTime.UtcNow > deadline) throw new Exception(message);
                        await Task.Delay(25);
                    }
                }
                void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        gate.Pause = true;
                        var first = Scan();
                        await Wait(() => Field<bool>("showingPreview"), "First scan never displayed a preview");
                        Check(Field<ScanResult?>("scan") == null && Field<DiskEntry>("current").FileCount < 1106, "First preview was presented as a complete result");
                        Check(Field<List<MapItem>>("sortedRows").Count is > 0 and <= 1000, "Preview has no rows or exceeds its limit");
                        Check(!Field<TextBox>("search").Enabled && !Field<NavigationPanel>("navigation").Enabled, "Full queries enabled during preview");
                        Check(Field<Label>("subtitle").Text.Contains("尚未完成"), "Partial statistics label missing");
                        var meter = Field<ScanProgressControl>("progressBar");
                        Check(meter.State == ScanDisplayState.Running && meter.Percent is >= 0 and <= 99, "Preview has no honest live progress");
                        var previewGrid = Field<DataGridView>("grid");
                        previewGrid.ClearSelection(); previewGrid.Rows[100].Selected = true;
                        previewGrid.FirstDisplayedScrollingRowIndex = 80;
                        string selectedName = Field<MapItem>("selection").Name;
                        await Task.Delay(1100);
                        Check(previewGrid.FirstDisplayedScrollingRowIndex == 80 && Field<MapItem>("selection").Name == selectedName, "Automatic preview update lost scroll position or selection");
                        Field<ActionButton>("cancel").PerformClick(); await first;
                        Check(meter.State == ScanDisplayState.Cancelled && meter.Percent == null && !meter.PulseRunning, "Cancelled scan left successful progress/animation");
                        Check(Field<DiskEntry?>("current") == null && Field<List<MapItem>>("sortedRows").Count == 0 && Field<TextBox>("search").Enabled, "First cancellation left a partial result behind");
                        Console.WriteLine("PASS UI first preview, bounded rows, partial labels and first-scan cancellation");

                        gate.Pause = false; await Scan();
                        await Wait(() => Field<DataGridView>("grid").Enabled && Field<List<MapItem>>("sortedRows").Count == 1106, "Completed scan did not restore all rows");
                        var baseline = Field<ScanResult>("scan");
                        Check(meter.State == ScanDisplayState.Completed && meter.Percent == 100 && !meter.PulseRunning, "Success did not publish terminal 100%");
                        Check(baseline.Root.FileCount == 1106 && baseline.Root.Size == 18816, "Completed totals mismatch");
                        Invoke("SetMode", 2); Field<TextBox>("search").Text = "txt";
                        await Wait(() => Field<List<MapItem>>("sortedRows").Count == 1 && Field<List<MapItem>>("sortedRows")[0].Name == ".txt", "Type query did not settle");
                        gate.Pause = true; var second = Scan();
                        await Wait(() => Field<bool>("showingPreview"), "Refresh never displayed a preview");
                        Check(ReferenceEquals(Field<ScanResult>("scan"), baseline), "Preview replaced the completed snapshot");
                        var child = Field<DiskEntry>("current").Children.Single(e => e.Name == "A-child");
                        Invoke("OpenDirectory", child);
                        await Wait(() => Field<TextBox>("pathBox").Text == child.Path, "Cannot browse a child while scanning");
                        Check(Field<ActionButton>("up").Enabled && Field<ActionButton>("home").Enabled, "Preview parent navigation disabled");
                        Field<ActionButton>("home").PerformClick();
                        await Wait(() => Field<TextBox>("pathBox").Text == root, "Cannot return to preview root");
                        Field<ActionButton>("cancel").PerformClick(); await second;
                        await Wait(() => Field<List<MapItem>>("sortedRows").Count == 1 && Field<List<MapItem>>("sortedRows")[0].Name == ".txt", "Cancel did not restore the type query");
                        Check(ReferenceEquals(Field<ScanResult>("scan"), baseline) && Field<int>("mode") == 2 && Field<TextBox>("search").Text == "txt", "Cancel lost completed scan/context");
                        Console.WriteLine("PASS UI live child/root browsing, cancel restores completed scan and filtered type view");

                        var third = Scan();
                        await Wait(() => Field<bool>("showingPreview"), "Final refresh never displayed a preview");
                        gate.Release.Set(); await third;
                        await Wait(() => Field<DataGridView>("grid").Enabled && Field<List<MapItem>>("sortedRows").Count == 1, "Final view did not settle");
                        Check(!Field<bool>("showingPreview") && Field<TextBox>("search").Enabled && Field<NavigationPanel>("navigation").Enabled, "Preview did not end cleanly");
                        Check(!ReferenceEquals(Field<ScanResult>("scan"), baseline) && Field<ScanResult>("scan").Root.FileCount == 1106, "Completed snapshot was not published");
                        Check(Field<Label>("status").Text.Contains("扫描完成") && !Field<StatCard>("totalCard").Caption.Contains("非最终"), "Stale preview labels after completion");
                        Check(Field<int>("mode") == 2 && Field<TextBox>("search").Text == "txt", "Successful refresh lost query context");
                        await Task.Delay(700);
                        Check(!Field<bool>("showingPreview") && Field<Label>("status").Text.Contains("扫描完成"), "Late preview overwrote the completed view");
                        Check(meter.State == ScanDisplayState.Completed && meter.Percent == 100, "Late progress overwrote completion");
                        Console.WriteLine("PASS UI final handoff, controls restored, no stale preview, refresh context preserved");

                        // Exercise the shipped coordinator (without the deterministic scan gate).
                        using var product = new MainForm(null); product.Show();
                        T Product<T>(string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(product)!;
                        Task ProductScan(string method = "ScanFolder") => (Task)typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(product, method == "ScanFolder" ? [root] : null)!;
                        Product<ComboBox>("strategyCombo").SelectedIndex = 1;
                        await ProductScan();
                        await Wait(() => Product<DataGridView>("grid").Enabled, "Product scan did not settle");
                        Check(Product<ScanResult>("scan").Statistics is { Mode: ScanMode.LowImpact, Workers: 1 }, "UI strategy not passed to the scanner");
                        Check(Product<ToolTip>("scanTip").GetToolTip(Product<Label>("refreshInfo"))?.Contains("目录峰值 1/1") == true, "Concurrency missing from refresh details");
                        Check(Product<ToolTip>("scanTip").GetToolTip(Product<ComboBox>("strategyCombo"))?.Contains("256") == true, "Low-impact explanation missing");
                        Product<ComboBox>("metricCombo").SelectedIndex = 1;
                        await Wait(() => Product<StatCard>("totalCard").Title == "实际占用空间" && Product<DataGridView>("grid").Enabled, "Allocated metric did not render");
                        Check(Product<List<MapItem>>("sortedRows").Sum(i => i.Size) == Product<ScanResult>("scan").Root.AllocatedSize, "UI allocated rows do not match deduplicated total");
                        File.WriteAllBytes(Path.Combine(root, "A-child", "added.bin"), new byte[8000]);
                        await ProductScan();
                        await Wait(() => Product<DataGridView>("grid").Enabled, "Product refresh did not settle");
                        Check(Product<ScanResult>("scan").Root.FileCount == 1107 && Product<ScanResult>("scan").Statistics is { Incremental: true, ReusedFiles: > 1000 }, "UI did not use incremental refresh");
                        Check((Product<ScanResult>("scan").Statistics!.Method is RefreshMethod.MetadataVerification or RefreshMethod.ChangeJournal) && Product<Label>("refreshInfo").Text.Contains("详情"), "Actual refresh method/details missing");
                        Check(Product<ComboBox>("metricCombo").SelectedIndex == 1 && Product<StatCard>("totalCard").Title == "实际占用空间", "Refresh lost the size metric");
                        Product<ComboBox>("strategyCombo").SelectedIndex = 2; await ProductScan("ForceRefresh");
                        Check(Product<ScanResult>("scan").Statistics is { Incremental: false, Mode: ScanMode.HighSpeed, Workers: 8 }, "Force full / high-speed UI path failed");
                        Check(Product<Label>("status").Text.Contains("强制"), "Force full reason missing");
                        bool detailsRead = false;
                        using (var closeDetails = new System.Windows.Forms.Timer { Interval = 50 })
                        {
                            closeDetails.Tick += (_, _) =>
                            {
                                var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "最近完成的扫描 · 刷新详情");
                                if (dialog == null) return;
                                string text = dialog.Controls.OfType<TextBox>().Single().Text;
                                // Do not require a native journal to be available on the test machine.
                                detailsRead = text.Contains("完整扫描") && text.Contains("原因：") && text.Contains("核验目录") && text.Contains("处理记录");
                                dialog.Close(); closeDetails.Stop();
                            };
                            closeDetails.Start();
                            typeof(MainForm).GetMethod("ShowRefreshDetails", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(product, null);
                        }
                        Check(detailsRead, "Refresh details dialog is missing the actual method/counts/reason");
                        product.Close();
                        Console.WriteLine("PASS UI actual coordinator, low/high strategy selection, allocated metric, incremental refresh and force-full action");

                        await CheckInteractions(form, Check);
                        CheckRenderCache(form, Check);
                        await CheckLongPath(form, root, Check);
                        CheckDpiAndMenu(form, virtualized96Dpi, Check);
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { gate.Release.Set(); form.Close(); }
                };
                Application.Run(form);
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(45))) throw new Exception("UI regression timed out");
            if (failure != null) throw new Exception("UI regression failed", failure);
            Console.WriteLine("All UI regression checks passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "DiskAtlas-ui-regression-";
            if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe UI regression cleanup path");
            Directory.Delete(root, true);
        }
    }

    private static async Task CheckLongPath(MainForm owner, string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "long-path");
        while (folder.Length <= 270)
            folder = Path.Combine(folder, "长路径-" + new string('x', 36));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "中英文-long-file.txt");
        File.WriteAllBytes(file, new byte[64]);
        using var product = new MainForm(null);
        product.Show(owner);
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        T Field<T>(string name) => (T)typeof(MainForm).GetField(name, fields)!.GetValue(product)!;
        await (Task)typeof(MainForm).GetMethod("ScanFolder", fields)!.Invoke(product, [folder])!;
        var grid = Field<DataGridView>("grid");
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while ((!grid.Enabled || grid.Rows.Count != 1) && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        var pathBox = Field<TextBox>("pathBox");
        check(grid.Enabled && grid.Rows.Count == 1 && pathBox.Text == folder, "Long directory path lost in scan or path input");
        check(Field<List<MapItem>>("sortedRows").Single().Entry?.Path == file, "Long file path lost in the list model");
        pathBox.Focus();
        pathBox.SelectAll();
        check(pathBox.SelectedText == folder, "Full long path is not selectable in the path input");
        using var frame = new Bitmap(product.Width, product.Height);
        product.DrawToBitmap(frame, product.ClientRectangle);
        product.Close();
        Console.WriteLine("PASS UI scanned, rendered and selected a mixed-language path longer than 270 characters");
    }

    private static void CheckDpiAndMenu(MainForm owner, bool virtualized96Dpi, Action<bool, string> check)
    {
        void CheckInputs(MainForm form)
        {
            foreach (string name in new[] { "pathBox", "search", "strategyCombo", "metricCombo" })
            {
                var input = (Control)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                check(input.Parent is InputFrame && input.Width > 0 && input.Left > 0 && input.Top > 0 &&
                    input.Right < input.Parent.ClientSize.Width && input.Bottom < input.Parent.ClientSize.Height,
                    "Input is clipped by its themed frame at " + form.DeviceDpi + " DPI: " + name);
            }
        }

        CheckInputs(owner);
        if (virtualized96Dpi) check(owner.DeviceDpi == 96, "Virtualized process did not get 96 DPI");

        bool highContrast = false;
        using var menu = Theme.CreateContextMenu(() => highContrast);
        menu.Items.Add("复制完整路径");
        menu.Show(owner, new Point(20, 20));
        check(menu.Visible && menu.BackColor == Theme.Surface, "Normal menu did not use the theme");
        menu.Close();
        highContrast = true;
        menu.Show(owner, new Point(20, 20));
        check(menu.Visible && menu.RenderMode == ToolStripRenderMode.System &&
            menu.BackColor == SystemColors.Menu && menu.ForeColor == SystemColors.MenuText,
            "High-contrast menu still uses app colors or custom rendering");
        using (var image = new Bitmap(menu.Width, menu.Height)) menu.DrawToBitmap(image, menu.ClientRectangle);
        menu.Close();
        highContrast = false;
        menu.Show(owner, new Point(20, 20));
        check(menu.Visible && menu.RenderMode == ToolStripRenderMode.Custom && menu.ForeColor == Theme.Text,
            "Menu did not restore theme after high contrast was disabled");
        menu.Close();
        Console.WriteLine($"PASS UI input layout at {owner.DeviceDpi} DPI, system menu palette and theme restoration");
    }

    private static async Task CheckInteractions(Form owner, Action<bool, string> check)
    {
        using var fixture = new Form { Size = new Size(960, 620), Text = "AsukaLens interaction checks" };
        using var meter = new ScanProgressControl { Dock = DockStyle.Bottom, Height = 48, AnimationsEnabled = true };
        using var map = new TreemapControl { Dock = DockStyle.Fill, ShowNested = false, AnimationsEnabled = true };
        fixture.Controls.Add(map); fixture.Controls.Add(meter); fixture.Show(owner);
        meter.BeginScan();
        check(meter.AccessibleRole == AccessibleRole.StaticText && meter.AccessibleName!.Contains("总量未知"), "Unknown progress exposes a misleading numeric range");
        check(meter.Percent == null && meter.PercentText == "总量未知" && meter.PulseRunning, "Unknown work did not start a distinct indicator");
        await Task.Delay(100);
        check(meter.Percent == null, "Time advanced the percentage");
        meter.Report(new(1, 2, 3, "", CompletedDirectories: 3, DiscoveredDirectories: 4));
        check(meter.Percent == 75 && !meter.PulseRunning, "Measured progress retained the unknown animation");
        meter.Report(new(1, 2, 3, "", CompletedDirectories: 3, DiscoveredDirectories: 8));
        check(meter.Percent == 37, "Growing work is hidden by monotonic animation");
        meter.Report(new(1, 2, 3, "", ScanPhase.Verifying, CompletedDirectories: 0, DiscoveredDirectories: 3));
        check(meter.Percent == 0 && meter.StageText.Contains("核验"), "New phase did not reset its estimate");
        meter.Report(new(1, 2, 3, "", ScanPhase.Finalizing, CompletedDirectories: 3, DiscoveredDirectories: 3));
        check(meter.Percent == 99, "Finalization claimed premature completion");
        foreach (var state in new[] { ScanDisplayState.Incomplete, ScanDisplayState.Cancelled, ScanDisplayState.Failed, ScanDisplayState.Completed })
        {
            meter.Finish(state); meter.Report(new(0, 0, 0, "", CompletedDirectories: 0, DiscoveredDirectories: 1));
            check(meter.State == state && meter.Percent == (state == ScanDisplayState.Completed ? 100 : null) && !meter.PulseRunning, "Terminal state accepted stale progress or animated");
            meter.BeginScan();
        }
        meter.AnimationsEnabled = false;
        check(!meter.PulseRunning && meter.PercentText == "总量未知", "Reduced motion removed the unknown state or kept animating");
        meter.AnimationsEnabled = true; meter.Hide();
        check(!meter.PulseRunning, "Hidden progress kept animating"); meter.Show();
        meter.Finish(ScanDisplayState.Completed);

        var items = Enumerable.Range(0, ViewQuery.MapLimit + 1).Select(i => new MapItem(i == 0 ? "long-file-name-中文-" + new string('x', 100) : $"file-{i}", 100, null, "图片")).ToList();
        map.SetItems(items);
        List<MapTile> Tiles() => (List<MapTile>)typeof(TreemapControl).GetField("tiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(map)!;
        void Input(string method, Point point) => typeof(TreemapControl).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(map, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
        var before = Tiles().Select(t => t.Bounds).ToArray();
        var first = Tiles().First(t => t.Bounds.Width > 22 && t.Bounds.Height > 22);
        var point = Point.Round(new PointF(first.Bounds.X + first.Bounds.Width / 2, first.Bounds.Y + first.Bounds.Height / 2));
        MapItem? hit = null; map.ItemSelected += value => hit = value;
        Input("OnMouseMove", point); check(map.MotionRunning, "Hover did not start feedback");
        Input("OnMouseClick", point); check(hit == first.Item && map.MotionRunning, "Animated hit target changed");
        using (var bitmap = new Bitmap(map.Width, map.Height)) map.DrawToBitmap(bitmap, map.ClientRectangle);
        await Task.Delay(TreemapControl.MotionDurationMilliseconds + 100);
        check(!map.MotionRunning && before.SequenceEqual(Tiles().Select(t => t.Bounds)), "Animation moved area/hit rectangles or did not stop");
        Input("OnMouseClick", point); map.AnimationsEnabled = false;
        check(!map.MotionRunning, "Reduced motion retained tile animation");
        Input("OnMouseClick", point); check(hit == first.Item && !map.MotionRunning, "Static interaction regressed");
        map.AnimationsEnabled = true; Input("OnMouseClick", point); map.Hide();
        check(!map.MotionRunning, "Hidden treemap kept animating"); map.Show();
        foreach (int width in new[] { 420, 1440 })
        {
            fixture.ClientSize = new Size(width, 420);
            using var image = new Bitmap(fixture.Width, fixture.Height); fixture.DrawToBitmap(image, fixture.ClientRectangle);
            check(meter.Width > 0 && map.Height > 0, "Resized interaction area collapsed");
        }
        var reduced = (CheckBox)typeof(MainForm).GetField("reduceMotion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        var actualMap = (TreemapControl)typeof(MainForm).GetField("map", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        var actualMeter = (ScanProgressControl)typeof(MainForm).GetField("progressBar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        reduced.Checked = true; check(!actualMap.AnimationsEnabled && !actualMeter.AnimationsEnabled, "Visible motion preference not wired to both controls");
        reduced.Checked = false; check(actualMap.AnimationsEnabled && actualMeter.AnimationsEnabled, "Motion preference cannot be restored");
        var originalSize = owner.Size;
        foreach (var size in new[] { new Size(1120, 780), new Size(1600, 1000) })
        {
            owner.Size = size; await Task.Delay(40);
            check(actualMeter.Height > 20 && actualMeter.Width > 200 && actualMap.Height > 0, "Main window lost progress or map when resized");
            check(actualMeter.Bottom <= actualMeter.Parent!.ClientSize.Height && reduced.Right <= reduced.Parent!.ClientSize.Width,
                "Main window clipped progress or motion preference");
            using var image = new Bitmap(owner.Width, owner.Height); owner.DrawToBitmap(image, owner.ClientRectangle);
        }
        owner.Size = originalSize;
        fixture.Close();
        Console.WriteLine($"UI render DPI: {owner.DeviceDpi}; main window sizes 1120x780 and 1600x1000 (subject to minimum size).");
        Console.WriteLine("PASS UI honest progress states, phase reset, reduced motion, stable animated hits/area, bounded animation and resizing");
    }

    private static void CheckRenderCache(Form owner, Action<bool, string> check)
    {
        using var fixture = new Form { ClientSize = new Size(1000, 700), Text = "AsukaLens render cache checks", BackgroundImage = Wallpaper.Load() };
        using var map = new TreemapControl { Dock = DockStyle.Fill, AnimationsEnabled = false };
        fixture.Controls.Add(map); fixture.Show(owner); map.SetItems(TreemapBenchmark.CreateItems(true));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object[] args) => typeof(TreemapControl).GetMethod(name, flags)!.Invoke(map, args);
        List<MapTile> Tiles() => (List<MapTile>)typeof(TreemapControl).GetField("tiles", flags)!.GetValue(map)!;
        Bitmap Capture()
        {
            var image = new Bitmap(map.Width, map.Height); image.SetResolution(map.DeviceDpi, map.DeviceDpi);
            map.DrawToBitmap(image, map.ClientRectangle); return image;
        }
        using var initial = Capture();
        int builds = map.StaticLayerBuilds;
        check(builds > 0 && map.StaticLayerBytes == (long)map.Width * map.Height * 4 && map.StaticLayerBytes <= TreemapControl.MaxCachePixels * 4, "Static cache is missing or unbounded");
        var dirty = new List<Rectangle>(); InvalidateEventHandler changed = (_, e) => dirty.Add(e.InvalidRect);
        map.Invalidated += changed;
        foreach (var tile in Tiles().Take(3))
        {
            var point = Point.Round(new PointF(tile.Bounds.X + tile.Bounds.Width / 2, tile.Bounds.Y + tile.Bounds.Height / 2));
            Call("OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0));
            using var frame = Capture();
        }
        map.SelectItem(Tiles()[1].Item); using (var frame = Capture()) { }
        check(dirty.Count > 0 && dirty.All(r => (long)r.Width * r.Height < (long)map.Width * map.Height / 2), "Hover/selection still invalidate the whole map");
        check(map.StaticLayerBuilds == builds, "Hover/selection rebuild static labels or wallpaper");
        map.Invalidated -= changed;
        Call("OnMouseLeave", EventArgs.Empty); map.SelectItem(null);
        using var cached = Capture();
        Call("ReleaseStaticLayer");
        typeof(TreemapControl).GetField("rejectedKey", flags)!.SetValue(map, Call("CurrentCacheKey"));
        using var direct = Capture();
        long different = 0;
        for (int y = 0; y < cached.Height; y++) for (int x = 0; x < cached.Width; x++)
        {
            var a = cached.GetPixel(x, y); var b = direct.GetPixel(x, y);
            if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 12) different++;
        }
        check(different < (long)map.Width * map.Height / 100, "Cached and direct drawing differ materially");
        Call("ReleaseStaticLayer"); using (var frame = Capture()) { }
        builds = map.StaticLayerBuilds;
        map.ShowNested = false; using (var frame = Capture()) { }
        check(map.StaticLayerBuilds > builds, "Nested layout change retained stale cache"); builds = map.StaticLayerBuilds;
        map.Metric = SizeMetric.Allocated; using (var frame = Capture()) { }
        check(map.StaticLayerBuilds > builds, "Metric change retained stale cache"); builds = map.StaticLayerBuilds;
        var old = fixture.BackgroundImage; fixture.BackgroundImage = new Bitmap(10, 10); old?.Dispose();
        using (var frame = Capture()) { }
        check(map.StaticLayerBuilds > builds, "Wallpaper change retained stale pixels"); builds = map.StaticLayerBuilds;
        fixture.ClientSize = new Size(800, 500); using (var frame = Capture()) { }
        check(map.StaticLayerBuilds > builds, "Viewport resize retained stale cache"); builds = map.StaticLayerBuilds;
        map.Dock = DockStyle.None; map.Size = new Size(760, 440); map.Location = new Point(9, 13); using (var frame = Capture()) { }
        check(map.StaticLayerBuilds > builds, "Wallpaper offset change retained stale cache");
        map.Size = new Size(4096, 2048);
        check(map.StaticLayerBytes == 0 && Call("EnsureStaticLayer") is false && map.StaticLayerBytes == 0, "Oversized viewport allocated a bitmap above the cap");
        map.Hide(); check(map.StaticLayerBytes == 0, "Hidden map retains static bitmap");
        map.Dispose(); check(map.StaticLayerBytes == 0, "Disposed map retains static bitmap");
        var wallpaper = fixture.BackgroundImage; fixture.BackgroundImage = null; wallpaper?.Dispose(); fixture.Close();

        using var meter = new ScanProgressControl { Size = new Size(800, 72) };
        var load = typeof(ScanProgressControl).GetMethod("GetMarker", flags)!;
        var avatar = (Bitmap)load.Invoke(meter, [39])!;
        check(avatar.Width == 39 && avatar.Height == 39 && avatar.GetPixel(0, 0).A == 0, "Avatar resource lost transparency or DPI size");
        check(ReferenceEquals(avatar, load.Invoke(meter, [39])), "Avatar decodes on every frame");
        check(!ReferenceEquals(avatar, load.Invoke(meter, [52])), "Avatar does not rebuild for a different DPI size");
        foreach (int completed in new[] { 0, 5, 10 })
        {
            meter.BeginScan(); meter.Report(new(0, 0, 0, "", CompletedDirectories: completed, DiscoveredDirectories: 10));
            using var bitmap = new Bitmap(meter.Width, meter.Height); meter.DrawToBitmap(bitmap, meter.ClientRectangle);
        }
        meter.Finish(ScanDisplayState.Completed);
        using (var bitmap = new Bitmap(meter.Width, meter.Height)) meter.DrawToBitmap(bitmap, meter.ClientRectangle);
        Console.WriteLine("PASS UI avatar transparency/reuse, partial hover invalidation, cache reuse/cap/disposal and layout/background/metric invalidation");
    }

    private sealed class ScanGate : IDisposable
    {
        public bool Pause;
        public ManualResetEventSlim Release { get; } = new();
        public ScanResult Scan(string path, IProgress<ScanProgress> progress, CancellationToken token, LiveScanSession live)
        {
            Release.Reset(); int paused = 0;
            return new DiskScanner(1).Scan(path, new GatedProgress(p =>
            {
                progress.Report(p);
                if (!Pause) return;
                if (p.Files == 0 && p.Folders == 0) Thread.Sleep(120);
                else if (Interlocked.Exchange(ref paused, 1) == 0) Release.Wait(token);
            }), token, live);
        }
        public void Dispose() => Release.Dispose();
    }

    private sealed class GatedProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
