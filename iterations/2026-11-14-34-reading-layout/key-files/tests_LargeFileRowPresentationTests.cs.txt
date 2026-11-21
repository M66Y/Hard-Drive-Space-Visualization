using System.Reflection;
using DiskAtlas;

// The tree exists only in memory. No scan, shell action or personal settings are used.
internal static class LargeFileRowPresentationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run(bool dpi96 = false)
    {
        int passed = 0;
        string failure = "None";
        Action? requestClose = null;
        var worker = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                CheckGeometry(Check);
                var fixture = CreateTree();
                CheckRelativeLocations(fixture, Check);
                string unused = Path.Combine(Path.GetTempPath(), "AsukaLens-row-presentation-unused-" + Guid.NewGuid().ToString("N"), "background.json");
                using var form = new MainForm(null, false, unused)
                {
                    ScanRunner = (_, _, _, _) => throw new CheckFailure("UnexpectedScan"),
                    ActivityMonitorFactory = _ => throw new CheckFailure("UnexpectedMonitor")
                };
                typeof(MainForm).GetField("scan", Hidden)!.SetValue(form,
                    new ScanResult(fixture.Root, TimeSpan.FromSeconds(.1), 0, []));
                typeof(MainForm).GetField("current", Hidden)!.SetValue(form, fixture.Root);
                requestClose = () =>
                {
                    try
                    {
                        if (!form.IsDisposed && form.IsHandleCreated)
                            form.BeginInvoke((Action)(() => { if (!form.IsDisposed) form.Close(); }));
                    }
                    catch (InvalidOperationException) { }
                };
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        Field<CheckBox>(form, "reduceMotion").Checked = true;
                        var grid = Field<WallpaperGrid>(form, "grid");
                        var usualSize = form.Size;
                        foreach (bool minimum in new[] { false, true })
                        {
                            form.Size = minimum ? form.MinimumSize : usualSize;
                            await View(form, 0);
                            Rectangle gridBounds = InForm(form, grid);
                            Rectangle mapBounds = InForm(form, Field<TreemapControl>(form, "map"));
                            foreach (int mode in new[] { 0, 1, 2, 1 })
                            {
                                await View(form, mode);
                                int height = FileListPresentation.RowHeight(grid.DeviceDpi, mode == 1);
                                Check(grid.RowCount > 0 && grid.RowTemplate.Height == height &&
                                    grid.Rows.Cast<DataGridViewRow>().All(row => row.Height == height), "ModeUsesCorrectActualRowHeight");
                                Check(!grid.Columns["location"]!.Visible && grid.Columns["count"]!.Visible == (mode == 2),
                                    "ModeUsesExpectedVisibleColumns");
                                Check(grid.Columns["name"]!.HeaderText.StartsWith(mode == 2 ? "类型" : "名称", StringComparison.Ordinal) &&
                                    grid.Columns["size"]!.HeaderText.StartsWith("大小", StringComparison.Ordinal), "ModeKeepsColumnHeaders");
                                Check(InForm(form, grid) == gridBounds && InForm(form, Field<TreemapControl>(form, "map")) == mapBounds,
                                    "ModeSwitchKeepsWorkspaceGeometry");
                                Check(grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible)
                                    .All(column => column.Width >= column.MinimumWidth), "VisibleColumnsKeepMinimumWidths");
                                string value = Convert.ToString(grid["name", 0].Value) ?? "";
                                Check((mode == 1) == value.Contains('\n'), "OnlyLargeFilesExposeTwoLineCellValues");
                            }
                            CheckRowsAndTooltips(form, fixture, Check);
                            CheckPaint(form, fixture.LongName, Check);
                        }
                        await CheckSortSearchAndSelection(form, fixture, Check);
                        await View(form, 0);
                        Check(grid.RowTemplate.Height == FileListPresentation.RowHeight(grid.DeviceDpi, false) &&
                            grid.Rows.Cast<DataGridViewRow>().All(row => row.Height == grid.RowTemplate.Height),
                            "LeavingLargeFilesRestoresSingleLineRows");
                    }
                    catch (CheckFailure error) { failure = error.Code; }
                    catch { failure = "OwnedUiFixtureFailure"; }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (CheckFailure error) { failure = error.Code; }
            catch { failure = "OwnedFixtureFailure"; }
            finally { requestClose = null; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(40)))
        {
            failure = "WorkerTimeout";
            requestClose?.Invoke();
            worker.Join(TimeSpan.FromSeconds(5));
        }
        Console.WriteLine((failure == "None" ? "PASS" : "FAIL") + " large-file row presentation checks=" + passed + "; code=" + failure);
        return failure == "None" ? 0 : 1;

        void Check(bool condition, string code)
        {
            if (!condition) throw new CheckFailure(code);
            passed++;
        }
    }

    private static void CheckGeometry(Action<bool, string> check)
    {
        foreach (int dpi in new[] { 96, 144 })
        foreach (bool detailed in new[] { false, true })
        {
            int height = FileListPresentation.RowHeight(dpi, detailed);
            check(height == (int)Math.Round((detailed ? 58 : 44) * dpi / 96f), "RowHeightUsesDipScaleOnce");
            foreach (int width in new[] { 0, 1, 8, 24, 100, 360 })
            foreach (int cellHeight in new[] { 0, 1, 20, height })
            {
                var cell = new Rectangle(17, 23, width, cellHeight);
                var bands = FileListPresentation.Bands(cell, dpi, detailed);
                foreach (var band in new[] { bands.Name, bands.Path, bands.Dot })
                    check(band.Width >= 0 && band.Height >= 0 &&
                        (band.Width == 0 || band.Height == 0 || cell.Contains(band)), "DrawingBandsRemainInsideNarrowCell");
                check(bands.Path.Width == 0 || bands.Path.Height == 0 || detailed, "SingleLineHasNoPathBand");
                if (width == 360 && cellHeight == height)
                {
                    check(bands.Name.Width > 0 && bands.Name.Height > 0 && bands.Dot.Width > 0 && bands.Dot.Height > 0,
                        "OrdinaryCellHasNameAndCategoryBands");
                    if (detailed)
                        check(bands.Path.Width > 0 && bands.Path.Height > 0 && bands.Name.Bottom <= bands.Path.Top,
                            "DetailedTextBandsDoNotOverlap");
                }
            }
        }
    }

    private static void CheckRelativeLocations(Fixture fixture, Action<bool, string> check)
    {
        MapItem Item(DiskEntry entry) => new(entry.Name, entry.Size, entry, "文档");
        check(FileListPresentation.RelativeLocation(Item(fixture.Direct), fixture.Root) == "当前目录", "RootFileUsesCurrentDirectoryLabel");
        check(FileListPresentation.RelativeLocation(Item(fixture.FirstDuplicate), fixture.Root) == fixture.FirstDuplicate.Parent!.Name,
            "NestedFileUsesRelativeParentDirectory");
        string relative = Path.GetRelativePath(fixture.Root.Path, fixture.LongName.Parent!.Path);
        check(FileListPresentation.RelativeLocation(Item(fixture.LongName), fixture.Root) == relative && relative.Length > 200,
            "LongRelativeDirectoryRemainsCompleteInModel");
        check(FileListPresentation.RelativeLocation(Item(fixture.LongName), fixture.LongName.Parent) == "当前目录",
            "RelativeLocationFollowsCurrentScope");
        check(FileListPresentation.RelativeLocation(new MapItem("合成类型", 1, null, "其他"), fixture.Root) == "" &&
            FileListPresentation.RelativeLocation(Item(fixture.Direct), null) == "", "MissingEntryOrScopeHasNoInventedLocation");
    }

    private static void CheckRowsAndTooltips(MainForm form, Fixture fixture, Action<bool, string> check)
    {
        var grid = Field<WallpaperGrid>(form, "grid");
        var rows = Field<List<MapItem>>(form, "sortedRows");
        foreach (var entry in new[] { fixture.Direct, fixture.FirstDuplicate, fixture.SecondDuplicate, fixture.LongName })
        {
            int row = rows.FindIndex(item => ReferenceEquals(item.Entry, entry));
            check(row >= 0, "ExpectedSyntheticFileIsPresent");
            string expected = entry.Name + "\n" + FileListPresentation.RelativeLocation(rows[row], fixture.Root);
            check(Convert.ToString(grid["name", row].Value) == expected && rows[row].Name == entry.Name,
                "DisplayCombinesNameAndPathWithoutChangingModelName");
            check(grid["name", row].ToolTipText == entry.Path, "TwoLineCellKeepsCompletePathTooltip");
            Invoke(form, "SelectItem", rows[row], true);
            check(ReferenceEquals(Field<MapItem>(form, "selection").Entry, entry) && grid.Rows[row].Selected &&
                Field<ActionButton>(form, "reveal").Enabled && Field<Label>(form, "detailPath").Text.Contains(entry.Path, StringComparison.Ordinal),
                "DuplicateNamesKeepExactSelectionAndLocationTarget");
            foreach (string column in new[] { "size", "percent" })
            {
                Rectangle rectangle = grid.GetCellDisplayRectangle(grid.Columns[column]!.Index, row, true);
                check(grid.Columns[column]!.Visible && rectangle.Width >= grid.Columns[column]!.MinimumWidth - 2 && rectangle.Height > 0 &&
                    !string.IsNullOrWhiteSpace(Convert.ToString(grid[column, row].Value)), "NumericColumnsStayVisibleAndPopulated");
            }
            var bands = FileListPresentation.Bands(grid.GetCellDisplayRectangle(0, row, false), grid.DeviceDpi, true);
            Point pathPoint = new(bands.Path.Left + Math.Min(2, Math.Max(0, bands.Path.Width - 1)), bands.Path.Top + bands.Path.Height / 2);
            check(grid.HitTest(pathPoint.X, pathPoint.Y).RowIndex == row, "PathLineStillHitsItsOriginalNativeRow");
        }
        int first = rows.FindIndex(item => ReferenceEquals(item.Entry, fixture.FirstDuplicate));
        int second = rows.FindIndex(item => ReferenceEquals(item.Entry, fixture.SecondDuplicate));
        check(Convert.ToString(grid["name", first].Value) != Convert.ToString(grid["name", second].Value),
            "DuplicateNamesAreDistinguishedByRelativeDirectory");
        check(grid.Columns["name"]!.Width >= grid.Columns["size"]!.Width &&
            grid.Columns["name"]!.Width >= grid.Columns["percent"]!.Width, "DetailedNameKeepsPriorityOverNumericColumns");
        using var graphics = grid.CreateGraphics();
        foreach (string columnName in new[] { "size", "percent" })
        foreach (DataGridViewRow row in grid.Rows)
        {
            var cell = row.Cells[columnName];
            string value = Convert.ToString(cell.FormattedValue) ?? "";
            int width = TextRenderer.MeasureText(graphics, value, cell.InheritedStyle.Font ?? grid.Font,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
            check(width + cell.InheritedStyle.Padding.Horizontal <= cell.Size.Width,
                "DetailedNumericValueFitsItsAllocatedWidth");
        }
    }

    private static void CheckPaint(MainForm form, DiskEntry entry, Action<bool, string> check)
    {
        var grid = Field<WallpaperGrid>(form, "grid");
        var rows = Field<List<MapItem>>(form, "sortedRows");
        int row = rows.FindIndex(item => ReferenceEquals(item.Entry, entry));
        Invoke(form, "SelectItem", rows[row], true);
        var cell = grid.GetCellDisplayRectangle(0, row, false);
        var bands = FileListPresentation.Bands(cell, grid.DeviceDpi, true);
        var sentinel = Color.FromArgb(19, 47, 71);
        Rectangle viewport = grid.ClientRectangle;
        var originalItem = rows[row];
        var originalScope = Field<DiskEntry?>(form, "current");
        try
        {
            using var complete = Render(viewport, false);
            rows[row] = originalItem with { Name = "" };
            using var withoutName = Render(viewport, false);
            rows[row] = originalItem;
            check(DifferentPixels(complete, withoutName, Rectangle.Intersect(bands.Name, viewport)) > 0,
                "DetailedNameLinePaintsText");
            typeof(MainForm).GetField("current", Hidden)!.SetValue(form, null);
            using var withoutPath = Render(viewport, false);
            check(DifferentPixels(complete, withoutPath, Rectangle.Intersect(bands.Path, viewport)) > 0,
                "DetailedPathLinePaintsText");
        }
        finally
        {
            rows[row] = originalItem;
            typeof(MainForm).GetField("current", Hidden)!.SetValue(form, originalScope);
        }
        foreach (bool selected in new[] { false, true })
        foreach (var dirty in new[]
        {
            new Rectangle(cell.Left + cell.Width / 3, cell.Top, Math.Max(1, cell.Width / 3), cell.Height),
            Rectangle.Intersect(bands.Name, cell), Rectangle.Intersect(bands.Path, cell),
            new Rectangle(cell.Left, cell.Top, Math.Max(1, (int)(5 * grid.DeviceDpi / 96f)), Math.Max(1, cell.Height / 4))
        })
        {
            Rectangle clip = Rectangle.Intersect(dirty, viewport);
            using var image = Render(clip, selected);
            Rectangle envelope = Rectangle.Intersect(new Rectangle(viewport.Left, cell.Top - 6, viewport.Width, cell.Height + 12), viewport);
            bool outsideUnchanged = true;
            for (int y = envelope.Top; y < envelope.Bottom && outsideUnchanged; y++)
            for (int x = envelope.Left; x < envelope.Right; x++)
                if (!clip.Contains(x, y) && image.GetPixel(x, y).ToArgb() != sentinel.ToArgb())
                { outsideUnchanged = false; break; }
            check(outsideUnchanged, "BothTextLinesRespectCellAndDirtyClips");
        }

        Bitmap Render(Rectangle clip, bool selected)
        {
            var image = new Bitmap(grid.Width, grid.Height);
            image.SetResolution(grid.DeviceDpi, grid.DeviceDpi);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(sentinel);
            graphics.SetClip(viewport);
            var args = new DataGridViewCellPaintingEventArgs(grid, graphics, clip, cell, row, 0,
                selected ? DataGridViewElementStates.Selected : DataGridViewElementStates.None,
                grid[0, row].Value, grid[0, row].FormattedValue, "", grid[0, row].InheritedStyle,
                grid.AdvancedCellBorderStyle, DataGridViewPaintParts.All);
            bool productHandled = false;
            DataGridViewCellPaintingEventHandler observe = (_, actual) =>
            {
                if (ReferenceEquals(actual, args)) productHandled = actual.Handled;
            };
            grid.CellPainting += observe;
            try
            {
                typeof(WallpaperGrid).GetMethod("OnCellPainting", Hidden)!.Invoke(grid, [args]);
                check(productHandled && args.Handled, "DetailedNameUsesProductPaintingCallback");
                return image;
            }
            catch { image.Dispose(); throw; }
            finally { grid.CellPainting -= observe; }
        }
    }

    private static async Task CheckSortSearchAndSelection(MainForm form, Fixture fixture, Action<bool, string> check)
    {
        var grid = Field<WallpaperGrid>(form, "grid");
        List<MapItem> Rows() => Field<List<MapItem>>(form, "sortedRows");
        Invoke(form, "SelectItem", Rows().Single(item => ReferenceEquals(item.Entry, fixture.SecondDuplicate)), true);
        await Header("name");
        check(Field<string>(form, "sortColumn") == "name" && Field<bool>(form, "sortAscending") &&
            Ordered(Rows(), (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name)), "NameHeaderSortsAscending");
        check(ReferenceEquals(Field<MapItem>(form, "selection").Entry, fixture.SecondDuplicate), "NameSortPreservesExactDuplicateSelection");
        await Header("name");
        check(!Field<bool>(form, "sortAscending") &&
            Ordered(Rows(), (a, b) => -StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name)), "NameHeaderTogglesDescending");
        await Header("size");
        check(Field<string>(form, "sortColumn") == "size" && !Field<bool>(form, "sortAscending") &&
            Ordered(Rows(), (a, b) => b.Size.CompareTo(a.Size)), "SizeHeaderKeepsDescendingSort");
        await Header("percent");
        check(Field<string>(form, "sortColumn") == "size" && Field<bool>(form, "sortAscending") &&
            Ordered(Rows(), (a, b) => a.Size.CompareTo(b.Size)), "PercentHeaderSortsBySize");
        var locationSort = grid.Columns["name"]!.HeaderCell.ContextMenuStrip!.Items[1];
        locationSort.PerformClick();
        DateTime locationDeadline = DateTime.UtcNow.AddSeconds(5);
        while ((!grid.Enabled || Field<CancellationTokenSource?>(form, "viewCancellation") != null) && DateTime.UtcNow < locationDeadline)
            await Task.Delay(10);
        check(grid.Enabled && Field<string>(form, "sortColumn") == "location" && Field<bool>(form, "sortAscending") &&
            Ordered(Rows(), (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Entry?.Parent?.Path, b.Entry?.Parent?.Path)) &&
            grid.Columns["name"]!.HeaderText.Contains("按位置", StringComparison.Ordinal) &&
            grid.Columns["name"]!.HeaderText.Contains('↑'), "PathSortRetainsVisibleMenuAndHeaderIndicator");
        var search = Field<TextBox>(form, "search");
        search.Text = "shared-file";
        Field<System.Windows.Forms.Timer>(form, "searchTimer").Stop();
        await (Task)Invoke(form, "UpdateViewAsync", false)!;
        check(Rows().Count == 2 && Rows().All(item => item.Name == "shared-file.txt") &&
            Rows().Select(item => item.Entry!.Parent!.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "NameQueryKeepsBothDuplicatePaths");
        check(grid.Rows.Cast<DataGridViewRow>().All(row => row.Height == FileListPresentation.RowHeight(grid.DeviceDpi, true)) &&
            Convert.ToString(grid["name", 0].Value)!.Contains('\n'), "QueryKeepsDetailedPresentation");
        search.Text = "no-synthetic-file-matches";
        Field<System.Windows.Forms.Timer>(form, "searchTimer").Stop();
        await (Task)Invoke(form, "UpdateViewAsync", false)!;
        check(grid.RowCount == 0 && !grid.Columns["location"]!.Visible &&
            Field<TreemapControl>(form, "map").EmptyTitle.Contains("没有找到", StringComparison.Ordinal), "EmptyQueryKeepsHonestStateAndColumns");
        await View(form, 1);
        check(Rows().Count == fixture.Root.FileCount && grid.RowTemplate.Height == FileListPresentation.RowHeight(grid.DeviceDpi, true),
            "ClearingQueryRestoresAllDetailedRows");

        async Task Header(string name)
        {
            var mouse = new MouseEventArgs(MouseButtons.Left, 1, 2, 2, 0);
            var args = new DataGridViewCellMouseEventArgs(grid.Columns[name]!.Index, -1, 2, 2, mouse);
            typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick", Hidden)!.Invoke(grid, [args]);
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while ((!grid.Enabled || Field<CancellationTokenSource?>(form, "viewCancellation") != null) && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            check(grid.Enabled && Field<CancellationTokenSource?>(form, "viewCancellation") == null, "HeaderQueryFinishes");
        }
    }

    private static bool Ordered(List<MapItem> rows, Comparison<MapItem> comparison)
    {
        for (int index = 1; index < rows.Count; index++)
            if (comparison(rows[index - 1], rows[index]) > 0) return false;
        return true;
    }

    private static int DifferentPixels(Bitmap first, Bitmap second, Rectangle bounds)
    {
        int count = 0;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        for (int x = bounds.Left; x < bounds.Right; x++)
            if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) count++;
        return count;
    }

    private static async Task View(MainForm form, int mode)
    {
        Invoke(form, "SetMode", mode);
        await (Task)Invoke(form, "UpdateViewAsync", false)!;
        form.PerformLayout();
        await Task.Delay(20);
    }

    private static Rectangle InForm(MainForm form, Control control) =>
        new(form.PointToClient(control.PointToScreen(Point.Empty)), control.Size);
    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, Hidden)!.GetValue(form)!;
    private static object? Invoke(MainForm form, string name, params object[] args) =>
        typeof(MainForm).GetMethod(name, Hidden)!.Invoke(form, args);

    private static Fixture CreateTree()
    {
        var root = new DiskEntry(@"C:\AsukaLens-synthetic-row-presentation", true);
        DiskEntry DirectoryOf(DiskEntry parent, string name)
        {
            var directory = new DiskEntry(Path.Combine(parent.Path, name), true, parent);
            parent.MutableChildren.Add(directory);
            return directory;
        }
        DiskEntry FileOf(DiskEntry parent, string name, long size)
        {
            var file = new DiskEntry(Path.Combine(parent.Path, name), false, parent)
                { Size = size, AllocatedSize = size, FileCount = 1 };
            parent.MutableChildren.Add(file);
            return file;
        }
        var first = DirectoryOf(root, "目录 A");
        var second = DirectoryOf(root, "目录 B");
        var longParent = DirectoryOf(root, "长路径父目录-" + new string('p', 130));
        var deep = DirectoryOf(longParent, "第二层目录-" + new string('q', 120));
        var direct = FileOf(root, "root-file.log", 512);
        var duplicateA = FileOf(first, "shared-file.txt", 1200);
        var duplicateB = FileOf(second, "shared-file.txt", 3400);
        var longName = FileOf(deep, "相同前缀-中文-long-name-" + new string('x', 95) + "-辨识末尾.txt", 2500);
        FileOf(root, "empty-file.txt", 0);
        Summarize(root);
        return new(root, direct, duplicateA, duplicateB, longName);

        static void Summarize(DiskEntry directory)
        {
            foreach (var child in directory.Children)
            {
                if (child.IsDirectory) Summarize(child);
                directory.Size += child.Size;
                directory.AllocatedSize += child.AllocatedSize;
                directory.FileCount += child.FileCount;
                directory.FolderCount += child.FolderCount + (child.IsDirectory ? 1 : 0);
            }
        }
    }

    private sealed record Fixture(DiskEntry Root, DiskEntry Direct, DiskEntry FirstDuplicate,
        DiskEntry SecondDuplicate, DiskEntry LongName);
    private sealed class CheckFailure(string code) : Exception("LargeFileRowPresentation") { internal string Code { get; } = code; }
}
