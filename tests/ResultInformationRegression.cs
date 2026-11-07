using DiskAtlas;
using System.Reflection;

// Synthetic snapshots exercise UI states without scanning user folders or changing app data.
internal static class ResultInformationRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, Private)!.GetValue(form)!;
    private static object? Invoke(MainForm form, string name, params object[] args) =>
        typeof(MainForm).GetMethod(name, Private)!.Invoke(form, args);
    private static Task Scan(MainForm form, string path) => (Task)Invoke(form, "ScanFolder", path)!;
    private static Task View(MainForm form) => (Task)Invoke(form, "UpdateViewAsync", false)!;

    private static ScanResult Snapshot(string path, int kind)
    {
        var root = new DiskEntry(path, true) { FileCount = 1101, FolderCount = 1, Size = 1100 * 1024 + 2048,
            AllocatedSize = 1101 * 4096, Incomplete = kind is 1 or 3 };
        for (int i = 0; i < 1100; i++)
            root.MutableChildren.Add(new DiskEntry(Path.Combine(path, $"sample-{i:0000}.txt"), false, root)
            { Size = 1024, AllocatedSize = 4096, FileCount = 1, Storage = new(new(1, (ulong)i, 0), 4096, 1, false) });
        var child = new DiskEntry(Path.Combine(path, "图片 Pictures"), true, root)
        { FileCount = 1, Size = 2048, AllocatedSize = 4096 };
        var file = new DiskEntry(Path.Combine(child.Path, "photo.jpg"), false, child)
        { Size = 2048, FileCount = 1, AllocatedSize = 4096, Storage = new(new(1, 2000, 0), 4096, 1, false) };
        child.MutableChildren.Add(file); root.MutableChildren.Add(child);
        if (kind == 2)
        {
            file.Storage = null; file.AllocatedSize = child.AllocatedSize = 0;
            file.UnknownAllocationFiles = child.UnknownAllocationFiles = root.UnknownAllocationFiles = 1;
            root.AllocatedSize -= 4096;
        }
        return new(root, TimeSpan.FromSeconds(.2), kind == 1 ? 1 : 0,
            kind == 1 ? [new(Path.Combine(path, "unread-demo"), "合成未读取项")] : [])
        { AllocationIssues = kind == 2 ? [new(file.Path, "合成占用读取失败")] : [] };
    }

    public static async Task Check(Form owner, string path, Action<bool, string> check)
    {
        int kind = 0;
        using var form = new MainForm(null)
        { ScanRunner = (p, _, token, _) =>
            { if (kind == 4) { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
              return Snapshot(p, kind); } };
        form.Show(owner); await Scan(form, path); await View(form);
        var search = Field<TextBox>(form, "search");
        var grid = Field<DataGridView>(form, "grid");
        var total = Field<StatCard>(form, "totalCard");
        string originalTotal = total.Value;
        check(grid.RowCount == 1000 && Field<List<MapItem>>(form, "sortedRows").Count == 1101, "Full list pagination incorrect");
        Field<ActionButton>(form, "nextPage").PerformClick();
        check(grid.RowCount == 101 && total.Value == originalTotal, "Page altered totals");
        search.Text = "sample-0000"; await View(form);
        check(grid.RowCount == 1 && total.Value == originalTotal &&
            Field<Label>(form, "subtitle").Text.Contains("不受筛选影响") &&
            Field<Label>(form, "mapCaption").Text.Contains("已搜索"), "Filtered view hides unfiltered scope");
        search.Text = "no-such-name"; await View(form);
        check(grid.RowCount == 0 && Field<TreemapControl>(form, "map").EmptyTitle.Contains("没有找到") && total.Value == originalTotal,
            "Empty search claimed empty directory or changed totals");
        Invoke(form, "SetMode", 1); await View(form);
        check(grid.RowCount == 500 && Field<Label>(form, "mapCaption").Text.Contains("1,101") &&
            search.PlaceholderText.Contains("所有子目录"), "Largest-file limit or recursive search scope missing");
        Invoke(form, "SetMode", 2); await View(form);
        var type = Field<List<MapItem>>(form, "sortedRows").Single(i => i.Name == ".jpg");
        Invoke(form, "ActivateItem", type); await View(form);
        check(grid.RowCount == 1 && total.Value == originalTotal && Field<ActionButton>(form, "clearType").Visible &&
            Field<Label>(form, "subtitle").Text.Contains("筛选 .jpg"), "Type filter lost scope or totals");
        Field<ActionButton>(form, "clearType").PerformClick(); await View(form);
        check(grid.RowCount == 500, "Cannot clear type filter");
        Console.WriteLine("PASS UI result scope, search, no matches, recursive top-500, type filter and pagination");

        foreach (int incompleteKind in new[] { 1, 2, 3 })
        {
            kind = incompleteKind; await Scan(form, path); await View(form);
            check(Field<ScanProgressControl>(form, "progressBar").State == ScanDisplayState.Incomplete &&
                Field<Label>(form, "status").Text.Contains("结果不完整") &&
                Field<StatCard>(form, "timeCard").Caption.Contains("结果不完整") && Field<ActionButton>(form, "issues").Visible,
                "Incomplete scan presented as successful or has no details entry");
            bool inspected = false;
            using var closer = new System.Windows.Forms.Timer { Interval = 30 };
            closer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text.StartsWith("未读取项"));
                if (dialog == null) return;
                string details = dialog.Controls.OfType<TextBox>().Single().Text;
                inspected = details.Contains("不受当前视图筛选影响") && details.Contains("2,000") &&
                    (incompleteKind != 3 || details.Contains("没有保留逐项原因"));
                closer.Stop(); dialog.Close();
            };
            closer.Start(); Invoke(form, "ShowIssues");
            check(inspected, "Issue details missing full-scan scope, bounds or fallback reason");
            if (kind == 2)
            {
                Field<ComboBox>(form, "metricCombo").SelectedIndex = 1; await View(form);
                check(total.Value.Contains("未知") && Field<Label>(form, "subtitle").Text.Contains("1 项占用未知") &&
                    Field<ComboBox>(form, "metricCombo").AccessibleDescription!.Contains("不等于删除后释放"),
                    "Unknown allocation or reclaimable-space caveat missing");
            }
        }
        kind = 4; var cancelled = Scan(form, path);
        check(Field<Label>(form, "subtitle").Text.Contains("上一次扫描结果"), "Preparing scan does not identify old results");
        Field<ActionButton>(form, "cancel").PerformClick(); await cancelled; await View(form);
        check(Field<Label>(form, "status").Text.Contains("上一次扫描结果") &&
            Field<ActionButton>(form, "issues").Visible && Field<StatCard>(form, "timeCard").Caption.Contains("不完整"),
            "Cancellation lost incomplete state of retained snapshot");
        kind = 0; await Scan(form, path); await View(form);
        check(!Field<ActionButton>(form, "issues").Visible && Field<Label>(form, "status").Text.Contains("扫描完成") &&
            !Field<StatCard>(form, "timeCard").Caption.Contains("不完整"), "Complete scan retained stale incomplete labels");
        Console.WriteLine("PASS UI skipped, unknown and flag-only partial results, issue details, cancellation and recovery");

        Invoke(form, "SetMode", 0); await View(form);
        Invoke(form, "HandleKeys", form, new KeyEventArgs(Keys.Control | Keys.F));
        check(search.Focused, "Ctrl+F did not focus search");
        search.Text = "demo"; Invoke(form, "HandleKeys", form, new KeyEventArgs(Keys.Escape)); await View(form);
        check(search.Text == "", "Escape did not clear search");
        var directory = Field<List<MapItem>>(form, "sortedRows").Single(i => i.Entry?.IsDirectory == true);
        Invoke(form, "SelectItem", directory, true); grid.Focus();
        typeof(Control).GetMethod("OnKeyDown", Private)!.Invoke(grid, [new KeyEventArgs(Keys.Enter)]); await View(form);
        check(Field<DiskEntry>(form, "current").Name == "图片 Pictures", "Enter did not open selected folder");
        Invoke(form, "HandleKeys", form, new KeyEventArgs(Keys.Alt | Keys.Up)); await View(form);
        check(Field<DiskEntry>(form, "current").Path == path, "Alt+Up did not return to parent");
        check(search.AccessibleName != null && grid.AccessibleName != null &&
            Field<ComboBox>(form, "metricCombo").AccessibleName == "统计口径", "Core inputs lack accessible names");
        form.Size = form.MinimumSize;
        using var frame = new Bitmap(form.Width, form.Height); form.DrawToBitmap(frame, new Rectangle(Point.Empty, frame.Size));
        check(Field<Label>(form, "subtitle").Width > 0 && Field<ActionButton>(form, "issues").Width > 0, "Minimum layout collapsed");
        form.Close();
        Console.WriteLine($"PASS UI keyboard search/clear/open/parent, accessible input names and minimum layout at {owner.DeviceDpi} DPI");
    }

    public static int Preview()
    {
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            int scans = 0;
            using var form = new MainForm(null)
            { ScanRunner = (p, _, _, _) => ++scans == 2 ? throw new IOException("合成演示：目录暂时不可访问。") : Snapshot(p, scans == 1 ? 2 : 0) };
            form.Shown += async (_, _) => await Scan(form, Path.Combine(Path.GetTempPath(), "AsukaLens-合成演示"));
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return 0;
    }
}
