using DiskAtlas;
using System.Reflection;

internal static class SelectionLinkageTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed class Failure(string code) : Exception { internal string Code => code; }
    internal static int Run(bool dpi96)
    {
        int checks = 0; string? failure = null;
        var thread = new Thread(() =>
        {
            MainForm? form = null;
            try
            {
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles(); Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                var root = Fixture(); var video = root.Children.First(x => x.Name == "a-videos");
                var other = root.Children.First(x => x.Name == "b-apps");
                var left = video.Children[0]; var right = other.Children[0];
                MapItem Item(DiskEntry entry, SizeMetric metric = SizeMetric.Logical) =>
                    new(entry.Name, entry.Bytes(metric), entry, FileKinds.Category(entry.Extension), UnknownFiles: metric == SizeMetric.Allocated ? entry.UnknownAllocationFiles : 0);
                var sameNames = new[] { Item(left), Item(right) };
                Check(SelectionLinkage.FindRow(sameNames, Item(right)) == 1, "SameNameUsesExactEntry");
                Check(SelectionLinkage.FindRow(sameNames, Item(right, SizeMetric.Allocated)) == 1, "MetricDoesNotChangeSelectionIdentity");
                var replacement = new DiskEntry(right.Path, false, other) { Size = right.Size };
                Check(SelectionLinkage.FindRow(sameNames, Item(replacement)) == -1, "ReplacementAtSamePathDoesNotReuseIdentity");
                var aggregate = new MapItem("other", 10, null, "其他", 3, true);
                Check(SelectionLinkage.FindRow([aggregate], aggregate with { }) == -1, "AggregateDoesNotGuessMembership");
                Check(SelectionLinkage.ContainingRow([Item(video), Item(other)], Item(right), root) == 1, "InternalFileResolvesContainingDirectory");
                Check(SelectionLinkage.ScrollTarget(20, 8, 23) == 20, "VisibleRowDoesNotJump");
                Check(SelectionLinkage.ScrollTarget(20, 8, 40) == 33 && SelectionLinkage.ScrollTarget(20, 8, 10) == 10, "OffscreenRowMovesNearestEdge");
                var unknown = root.Children.First(x => x.Name == "unknown.bin");
                Check(SelectionDetails.Create(Item(unknown), SizeMetric.Logical, 0, "").Allocation == "未知", "UnknownIsNotZero");
                var shared = root.Children.First(x => x.Name == "shared.bin");
                var sharedDetails = SelectionDetails.Create(Item(shared), SizeMetric.Allocated, 0, "");
                Check(sharedDetails.Allocation == SizeFormat.Bytes(8192) && sharedDetails.Note.Contains("未重复"), "NativeAllocationSeparateFromSharedAccounting");
                var directoryDetails = SelectionDetails.Create(Item(root), SizeMetric.Logical, 0, "partial scope");
                Check(directoryDetails.Allocation.Contains("未知") && directoryDetails.Scope == "partial scope", "DirectoryKnownPlusUnknownAndScope");
                var groupDetails = SelectionDetails.Create(aggregate, SizeMetric.Logical, 0, "");
                Check(groupDetails.Path == "" && groupDetails.Action == SelectionAction.ShowList && groupDetails.Allocation.Contains("未在"), "AggregateHasNoFileOperationOrInventedSecondMetric");
                Check(SelectionDetails.Create(new(".txt", 50, null, "文档", 5), SizeMetric.Logical, 2, "").Action == SelectionAction.ShowType, "TypeSummaryHasDetailAction");

                form = new MainForm(null, compositionCandidate: true, showCandidateLabel: false);
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        Set("scan", new ScanResult(root, TimeSpan.FromSeconds(1), 0, [])); Set("current", root);
                        Set("sortColumn", "name"); Set("sortAscending", true);
                        await Query(false);
                        var grid = Field<WallpaperGrid>("grid"); var map = Field<TreemapControl>("map");
                        var rows = Field<List<MapItem>>("sortedRows");
                        var large = rows.Single(x => x.Name == "z-large.bin");
                        map.Focus(); Select(large);
                        Check(Field<int>("pageIndex") == 1 && grid.SelectedRows.Count == 1 && EntryOfSelected() == large.Entry, "MapCrossPageLocatesActualRow");
                        Check(Field<Label>("pageInfo").Text.Contains("跨页"), "CrossPageFeedbackUsesExistingFooter");
                        Check(map.Focused, "MapSelectionDoesNotStealFocus");
                        var nearby = rows[1002]; Select(nearby);
                        int first = grid.FirstDisplayedScrollingRowIndex; Select(nearby);
                        Check(first == grid.FirstDisplayedScrollingRowIndex, "RepeatedVisibleSelectionKeepsScroll");
                        Check(grid.LocateFlashRunning, "LocateFlashStarts");
                        using (var before = new Bitmap(grid.Width, grid.Height)) grid.DrawToBitmap(before, grid.ClientRectangle);
                        int builds = grid.BackdropBuilds; Pump(100);
                        using (var after = new Bitmap(grid.Width, grid.Height)) grid.DrawToBitmap(after, grid.ClientRectangle);
                        Check(grid.BackdropBuilds == builds, "RowFlashDoesNotRebuildBackdrop");
                        Pump(750); Check(!grid.LocateFlashRunning, "LocateFlashEndsWithinBound");
                        Field<CheckBox>("reduceMotion").Checked = true; Select(nearby);
                        Check(!grid.LocateFlashRunning && !map.AnimationsEnabled, "ReducedMotionDisablesLocatePulse");
                        Field<CheckBox>("reduceMotion").Checked = false;

                        Select(rows.Single(x => ReferenceEquals(x.Entry, video)));
                        using (var frame = new Bitmap(map.Width, map.Height)) map.DrawToBitmap(frame, map.ClientRectangle);
                        var nested = (Dictionary<MapItem, List<MapTile>>)typeof(TreemapControl).GetField("nestedLayouts", Private)!.GetValue(map)!;
                        var child = nested.Values.SelectMany(x => x).Single(x => ReferenceEquals(x.Item.Entry, left));
                        typeof(TreemapControl).GetMethod("OnMouseClick", Private)!.Invoke(map,
                            [new MouseEventArgs(MouseButtons.Left, 1, (int)(child.Bounds.X + child.Bounds.Width / 2), (int)(child.Bounds.Y + child.Bounds.Height / 2), 0)]);
                        Check(ReferenceEquals(Field<DiskEntry>("current"), root) && ReferenceEquals(Field<MapItem>("selection").Entry, left), "InternalClickKeepsOverviewAndSelectsFile");
                        Check(grid.SelectedRows.Count == 0 && grid.ContainingRow >= 0, "ContainingDirectoryNotPretendedAsSelectedFile");
                        Check(Field<ActionButton>("showInList").Visible && Field<ActionButton>("showInList").Enabled, "ExplicitListActionAvailable");
                        Check(Field<Label>("detailTitle").Text.Contains(left.Name) && Field<Label>("detailPath").Text == left.Path, "SummaryFieldsReferToOneObject");
                        var selectedMap = (MapItem)typeof(TreemapControl).GetField("selected", Private)!.GetValue(map)!;
                        Check(ReferenceEquals(selectedMap.Entry, left) && map.OrbitRunning, "InternalSelectionUsesExistingEdgeMotion");
                        using (var lit = new Bitmap(map.Width, map.Height))
                        using (var plain = new Bitmap(map.Width, map.Height))
                        {
                            typeof(TreemapControl).GetMethod("OnMouseLeave", Private)!.Invoke(map, [EventArgs.Empty]);
                            map.DrawToBitmap(lit, map.ClientRectangle); map.SelectItem(null); map.DrawToBitmap(plain, map.ClientRectangle);
                            var body = Rectangle.Ceiling(RectangleF.Inflate(child.Bounds, -6 * map.DeviceDpi / 96f, -6 * map.DeviceDpi / 96f));
                            int changed = 0;
                            for (int y = body.Top; y < body.Bottom; y++) for (int x = body.Left; x < body.Right; x++)
                                if (lit.GetPixel(x, y).ToArgb() != plain.GetPixel(x, y).ToArgb()) changed++;
                            Check(changed == 0, "InternalEdgeGlowPreservesLabelBodyPixels");
                            map.SelectItem(selectedMap);
                        }
                        await (Task)Invoke("NavigateToEntryAsync", left)!;
                        Check(ReferenceEquals(Field<DiskEntry>("current"), video) && ReferenceEquals(EntryOfSelected(), left), "ExplicitListActionEntersAndLocates");
                        Check(!Field<ActionButton>("showInList").Visible && Field<ActionButton>("reveal").Text == "定位文件", "FileActionAndLayoutSettle");

                        Invoke("OpenDirectory", root); await Query(false); Invoke("SetMode", 1); await Query(false);
                        rows = Field<List<MapItem>>("sortedRows"); Select(rows.Single(x => ReferenceEquals(x.Entry, right)));
                        Check(Field<Label>("detailPath").Text == right.Path, "SameNameSecondPathShown");
                        Field<ComboBox>("metricCombo").SelectedIndex = 1; await Query(true);
                        Check(ReferenceEquals(Field<MapItem>("selection").Entry, right) && ReferenceEquals(EntryOfSelected(), right), "MetricRebindsByIdentity");
                        Set("sortColumn", "name"); Set("sortAscending", false); await Query(true);
                        Check(ReferenceEquals(EntryOfSelected(), right), "SortRebindsByIdentity");
                        Select(Field<List<MapItem>>("sortedRows").Single(x => ReferenceEquals(x.Entry, left)));
                        Field<TextBox>("search").Text = "no-such-file"; await Query(true);
                        Check(grid.RowCount == 0 && Field<MapItem?>("selection") == null && !Field<ActionButton>("selectionDetails").Enabled, "FilterClearsUnavailableSelectionWithoutClearingSearch");
                        Check(Field<TextBox>("search").Text == "no-such-file", "SelectionNeverSilentlyClearsFilter");
                        Field<TextBox>("search").Text = "duplicate"; Task stale = Query(true);
                        Field<TextBox>("search").Text = "z-large"; Task latest = Query(false);
                        await Task.WhenAll(stale, latest);
                        Check(Field<List<MapItem>>("sortedRows").All(x => x.Name.Contains("z-large")), "OnlyLatestQueryPublishes");

                        Invoke("ResetSearch"); Set("current", root); Set("mode", 0); await Query(false);
                        Select(new(left.Name, left.Size, left, "其他"));
                        Task entering = (Task)Invoke("NavigateToEntryAsync", left)!;
                        Invoke("OpenDirectory", other); await Query(false); await entering;
                        Check(ReferenceEquals(Field<DiskEntry>("current"), other) && Field<MapItem?>("selection") == null, "SupersededEntryNavigationCannotReselectOldFile");

                        using (var details = new SelectionDetailsDialog(sharedDetails))
                        {
                            details.Show(form); Application.DoEvents();
                            Check(details.FullPath.ReadOnly && details.FullPath.Text == shared.Path && details.CopyPath.Enabled, "DialogHasCompleteLocalPath");
                            Check(details.Primary.Text == "定位文件" && details.ClientSize.Width > 0, "DialogUsesObjectAction");
                            Check(Math.Abs(details.ClientSize.Width - 760 * details.DeviceDpi / 96f) <= 3 ||
                                details.Width == Screen.FromControl(details).WorkingArea.Width, "DialogScalesLikeMainWindow");
                            details.Close();
                        }
                        using (var details = new SelectionDetailsDialog(groupDetails))
                        {
                            details.Show(form); Application.DoEvents();
                            Check(!details.CopyPath.Enabled && details.Primary.Text == "查看当前列表", "AggregateDialogDoesNotOfferFileLocation");
                            details.Close();
                        }
                        Size common = form.Size; form.Size = form.MinimumSize; Application.DoEvents();
                        Select(Field<List<MapItem>>("sortedRows").First());
                        Check(Field<ActionButton>("selectionDetails").Bounds.Width > 0 && Field<ActionButton>("reveal").Bounds.Width > 0, "MinimumWindowActionsRemainAvailable");
                        var summary = Field<SelectionSummaryLabel>("detailTitle");
                        summary.SetDetails(new(new string('N', 240), "文件", "12.34 GB", "56.78 GB", left.Path, "未知", "", "", SelectionAction.RevealFile), form.Font);
                        using (var frame = new Bitmap(summary.Width, summary.Height)) summary.DrawToBitmap(frame, summary.ClientRectangle);
                        Check(summary.MetadataBounds.Width > 0 && summary.NameBounds.Right <= summary.MetadataBounds.Left &&
                            summary.MetadataBounds.Right <= summary.ClientSize.Width, "LongNameCannotCoverSizeMetrics");
                        form.Size = common;
                        var replacementRoot = Fixture(); Set("scan", new ScanResult(replacementRoot, TimeSpan.Zero, 0, [])); Set("current", replacementRoot);
                        await Query(true);
                        Check(Field<MapItem?>("selection") == null, "NewResultTreeCannotRestoreOldSelection");
                        Check(TreemapControl.OrbitPeriodMilliseconds == 2800d / 1.15d, "ExistingGlowSpeedUnchanged");
                    }
                    catch (Exception e) { failure = Code(e); }
                    finally { form.Close(); }
                };
                Application.Run(form);
                Check(!Field<WallpaperGrid>("grid").LocateFlashRunning, "CloseStopsLocateTimer");
                form.Dispose();
                T Field<T>(string name) => (T)typeof(MainForm).GetField(name, Private)!.GetValue(form)!;
                void Set(string name, object value) => typeof(MainForm).GetField(name, Private)!.SetValue(form, value);
                object? Invoke(string name, params object[] args) => typeof(MainForm).GetMethod(name, Private)!.Invoke(form, args);
                Task Query(bool preserve) { Field<System.Windows.Forms.Timer>("searchTimer").Stop(); return (Task)Invoke("UpdateViewAsync", preserve)!; }
                void Select(MapItem item) => Invoke("SelectItem", item, true);
                DiskEntry? EntryOfSelected() => Field<DataGridView>("grid").SelectedRows.Count > 0
                    ? ((MapItem)Invoke("RowItem", Field<DataGridView>("grid").SelectedRows[0].Index)!).Entry : null;
            }
            catch (Exception e) { failure = Code(e); form?.Dispose(); }
            void Check(bool ok, string code) { checks++; if (!ok) throw new Failure(code); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(55))) { Console.Error.WriteLine("FAIL SelectionLinkageDeadline"); return 1; }
        if (failure != null) { Console.Error.WriteLine($"FAIL {failure}; completed_checks={checks}"); return 1; }
        Console.WriteLine($"PASS selection linkage checks={checks}; synthetic only; dpi96={dpi96}"); return 0;
    }
    private static string Code(Exception error)
    {
        while (error is TargetInvocationException { InnerException: { } inner }) error = inner;
        if (error is Failure f) return f.Code;
        string category = error switch { InvalidOperationException => "InvalidOperation", NullReferenceException => "NullReference",
            ArgumentException => "Argument", System.Runtime.InteropServices.ExternalException => "Graphics", _ => "Other" };
        var frame = new System.Diagnostics.StackTrace(error, false).GetFrames()?.FirstOrDefault(f => f.GetMethod()?.DeclaringType == typeof(SelectionLinkageTests) || f.GetMethod()?.DeclaringType == typeof(MainForm));
        return "UnexpectedSelectionLinkage_" + category + "_" + (frame?.GetMethod()?.Name ?? error.TargetSite?.Name ?? "Boundary");
    }
    private static void Pump(int milliseconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
    private static DiskEntry Fixture()
    {
        var root = new DiskEntry(@"X:\AsukaLens-Linkage-Test", true);
        var video = new DiskEntry(Path.Combine(root.Path, "a-videos"), true, root);
        var other = new DiskEntry(Path.Combine(root.Path, "b-apps"), true, root);
        root.MutableChildren.Add(video); root.MutableChildren.Add(other);
        Add(video, "duplicate-demo.exe", 4_000_000, 4_001_792);
        Add(other, "duplicate-demo.exe", 3_500_000, 3_502_080);
        Add(video, "second-long-name-for-details-and-path-demo.mp4", 2_000_000, 2_002_944);
        for (int i = 0; i < 1105; i++) Add(root, $"sample-{i:0000}.txt", 17, 4096);
        Add(root, "z-large.bin", 3_000_000, 3_002_368);
        var unknown = Add(root, "unknown.bin", 33, 0); unknown.UnknownAllocationFiles = 1; unknown.Storage = null;
        var shared = Add(root, "shared.bin", 100, 0); shared.SharedAllocation = true;
        shared.Storage = new(new(1, 7000, 0), 8192, 2, false);
        root.Size = root.Children.Sum(x => x.Size); root.AllocatedSize = root.Children.Sum(x => x.AllocatedSize);
        root.UnknownAllocationFiles = 1; root.FileCount = root.Descendants().Count(x => !x.IsDirectory); root.FolderCount = 2;
        return root;
        static DiskEntry Add(DiskEntry parent, string name, long size, long allocated)
        {
            var entry = new DiskEntry(Path.Combine(parent.Path, name), false, parent) { Size = size, AllocatedSize = allocated, FileCount = 1,
                Modified = new DateTime(2026, 1, 1), Storage = new(new(1, (ulong)parent.Children.Count, (ulong)parent.Name.Length), allocated, 1, false) };
            parent.MutableChildren.Add(entry); parent.Size += size; parent.AllocatedSize += allocated; parent.FileCount++;
            return entry;
        }
    }
}
