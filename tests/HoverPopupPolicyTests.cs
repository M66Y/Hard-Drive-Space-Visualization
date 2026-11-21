using DiskAtlas;
using System.Globalization;
using System.Reflection;

// Synthetic controls and owned bitmaps only. No desktop pointer movement,
// tooltip Show, scanner, disk counter, or saved personal configuration.
internal static class HoverPopupPolicyTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run(bool dpi96 = false)
    {
        int passed = 0;
        string? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                CheckProviderPolicy();
                CheckMainForm();
                CheckRetainedInformationAndStates();
            }
            catch (Failure error) { failure = error.Code; }
            catch { failure = "UnexpectedHoverPolicyFailure"; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(30))) failure = "HoverPolicyWorkerStillActive";
        if (failure != null)
        {
            Console.Error.WriteLine("FAIL hover popup policy code=" + failure + "; no private details emitted");
            return 1;
        }
        Console.WriteLine("PASS hover popup policy checks=" + passed + "; synthetic controls, no scan or desktop hover claim");
        return 0;

        void Check(bool condition, string code)
        {
            if (!condition) throw new Failure(code);
            passed++;
        }

        void CheckProviderPolicy()
        {
            using var form = new Form { ClientSize = new Size(360, 100), ShowInTaskbar = false };
            using var label = new Label { Bounds = new Rectangle(10, 10, 320, 40), Text = "合成说明" };
            using var tip = AppToolTips.Create();
            string original = string.Concat(Enumerable.Repeat("D:\\Synthetic\\完整说明👩‍💻e\u0301\\", 70));
            tip.SetToolTip(label, original);
            form.Controls.Add(label);
            form.Show();
            Application.DoEvents();
            Check(!tip.Active && tip.GetToolTip(label) == original, "ProviderStartsInactiveAndRetainsCompleteOriginal");
            var onPopup = typeof(ToolTip).GetMethod("OnPopup", Hidden)!;
            var inactivePopup = new PopupEventArgs(label, label, false, new Size(400, 240));
            onPopup.Invoke(tip, [inactivePopup]);
            Check(inactivePopup.Cancel, "ProviderCancelsPopupEvent");
            try
            {
                tip.Active = true;
                var reenabledPopup = new PopupEventArgs(label, label, false, new Size(400, 240));
                onPopup.Invoke(tip, [reenabledPopup]);
                Check(reenabledPopup.Cancel && tip.GetToolTip(label) == original,
                    "ProviderCancelsPopupEvenIfActiveIsAccidentallyReenabled");
            }
            finally { tip.Active = false; form.Close(); Application.DoEvents(); }
        }

        void CheckMainForm()
        {
            string unused = Path.Combine(Path.GetTempPath(), "AsukaLens-hover-policy-unused-" + Guid.NewGuid().ToString("N"), "background.json");
            using var form = new MainForm(null, loadSavedBackground: false, backgroundSettingsPath: unused)
            {
                ShowInTaskbar = false,
                ScanRunner = (_, _, _, _) => throw new Failure("UnexpectedScan"),
                ActivityMonitorFactory = _ => throw new Failure("UnexpectedDiskMonitor")
            };
            form.Show();
            Application.DoEvents();
            try
            {
                var controls = Descendants(form).ToArray();
                var labels = controls.OfType<Label>().ToArray();
                Check(labels.Length > 0 && labels.All(label => !label.AutoEllipsis),
                    "MainFormLabelsDoNotEnableNativeEllipsisTooltips");
                Check(!Field<ToolTip>(form, "scanTip").Active, "MainFormExplanationProviderIsInactive");
                var grids = controls.OfType<DataGridView>().ToArray();
                Check(grids.Length > 0 && grids.All(grid => !grid.ShowCellToolTips), "MainFormNativeCellTooltipsAreDisabled");
                Check(ToolTipsInactive(form), "MainFormHasNoActiveTooltipField");
                Check(ToolTipsInactive(Field<TreemapControl>(form, "map")), "MainFormMapHasNoActiveTooltipProvider");
                Check(ToolTipsInactive(Field<ScanProgressControl>(form, "progressBar")), "MainFormProgressHasNoActiveTooltipProvider");
                var menus = controls.Select(control => control.ContextMenuStrip).Where(menu => menu != null)
                    .Cast<ContextMenuStrip>().Concat(grids.SelectMany(grid => grid.Columns.Cast<DataGridViewColumn>())
                        .Select(column => column.HeaderCell.ContextMenuStrip).Where(menu => menu != null).Cast<ContextMenuStrip>())
                    .Distinct().ToArray();
                Check(menus.Length > 0 && menus.All(menu => !menu.ShowItemToolTips),
                    "MainFormContextMenusDoNotShowItemTooltips");
                using var defaultMenu = Theme.CreateContextMenu();
                Check(!defaultMenu.ShowItemToolTips, "DefaultContextMenuDisablesItemTooltips");
            }
            finally { form.Close(); Application.DoEvents(); }
        }

        void CheckRetainedInformationAndStates()
        {
            using var form = new Form
            {
                Text = "Synthetic hover popup policy", AutoScaleMode = AutoScaleMode.None,
                ClientSize = new Size(780, 570), ShowInTaskbar = false
            };
            using var labelFont = new Font("Microsoft YaHei UI", 10F);
            using var label = new NoHoverLabel
            {
                AutoSize = false, Bounds = new Rectangle(15, 12, 300, 40),
                Font = labelFont, ForeColor = Color.Black, BackColor = Color.White,
                Text = ""
            };
            using var map = new TreemapControl
            {
                Bounds = new Rectangle(15, 65, 740, 290), ShowNested = false, AnimationsEnabled = false, TotalSize = 1000
            };
            using var progress = new ScanProgressControl
            {
                Bounds = new Rectangle(15, 365, 740, 60), AnimationsEnabled = false
            };
            using var activity = new DiskActivityPanel
            {
                SidebarMode = true, Bounds = new Rectangle(15, 435, 280, 120)
            };
            form.Controls.AddRange([label, map, progress, activity]);
            form.Show();
            Application.DoEvents();
            try
            {
                using var emptyLabel = Capture(label);
                string fullText = string.Concat(Enumerable.Repeat("完整中文说明与VeryLongSyntheticText👩‍💻", 12));
                label.Text = fullText;
                using var textLabel = Capture(label);
                Check(!label.AutoEllipsis && label.Text == fullText &&
                    ChangedPixels(emptyLabel, textLabel, label.ClientRectangle) > 20,
                    "NoHoverLabelDrawsLongTextAndRetainsOriginalWithoutNativeEllipsis");
                label.WrapText = true;
                using var wrappedLabel = Capture(label);
                Check(!label.AutoEllipsis && label.Text == fullText &&
                    ChangedPixels(emptyLabel, wrappedLabel, label.ClientRectangle) > 20,
                    "NoHoverLabelWrappedTextStillDrawsWithoutNativeEllipsis");
                Check(ToolTipsInactive(label), "NoHoverLabelHasNoActiveTooltipProvider");

                map.SetItems([new("Synthetic documents", 600, null, "文档"), new("Synthetic images", 400, null, "图片")]);
                var tiles = Field<List<MapTile>>(map, "tiles").ToArray();
                Check(tiles.Length == 2, "MapHasTwoSyntheticTiles");
                using var baseline = Capture(map);
                map.SelectItem(tiles[0].Item);
                using var selected = Capture(map);
                float scale = map.DeviceDpi / 96f;
                Check(Field<MapItem>(map, "selected") == tiles[0].Item &&
                    ChangedPixels(baseline, selected, Rectangle.Ceiling(TreemapControl.GlowBounds(tiles[0].Bounds, scale))) > 10,
                    "MapSelectionRetainsVisibleSoftContour");
                Point point = Point.Round(new PointF(tiles[1].Bounds.X + tiles[1].Bounds.Width / 2,
                    tiles[1].Bounds.Y + tiles[1].Bounds.Height / 2));
                typeof(TreemapControl).GetMethod("OnMouseMove", Hidden)!.Invoke(map,
                    [new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0)]);
                using var hovered = Capture(map);
                Check(Field<MapItem>(map, "hovered") == tiles[1].Item &&
                    ChangedPixels(selected, hovered, Rectangle.Ceiling(TreemapControl.GlowBounds(tiles[1].Bounds, scale))) > 10,
                    "MapHoverRetainsVisibleSoftContour");
                Check(ToolTipsInactive(map), "MapHoverAndSelectionHaveNoActiveTooltipProvider");

                const string firstDetail = "合成扫描详情 · 12 个文件 · D:\\Synthetic\\First.data";
                const string secondDetail = "合成扫描详情 · 28 个文件 · D:\\Synthetic\\Second.data";
                progress.BeginScan();
                progress.SetDetailText(firstDetail);
                Check(progress.AccessibleDescription!.Contains(ScanProgressControl.Explanation, StringComparison.Ordinal) &&
                    progress.AccessibleDescription.Contains(firstDetail, StringComparison.Ordinal),
                    "ProgressAccessibilityRetainsFullExplanationAndDetail");
                progress.SetDetailText(secondDetail);
                Check(progress.AccessibleDescription!.Contains(ScanProgressControl.Explanation, StringComparison.Ordinal) &&
                    progress.AccessibleDescription.Contains(secondDetail, StringComparison.Ordinal) &&
                    !progress.AccessibleDescription.Contains(firstDetail, StringComparison.Ordinal),
                    "ProgressAccessibilityTracksLatestDetailWithoutPopup");
                Check(ToolTipsInactive(progress), "ProgressHasNoActiveTooltipProvider");

                DateTimeOffset at = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                activity.SetSample(new(at, 0, 37, 2 * 1024 * 1024, 3 * 1024 * 1024, .5, DiskActivityStatus.Available));
                string description = activity.AccessibleDescription!;
                string decimalSeparator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
                Check(description.Contains("整块磁盘，包含其他进程", StringComparison.Ordinal) &&
                    description.Contains("活动时间 37%", StringComparison.Ordinal) &&
                    description.Contains("读取速度 2" + decimalSeparator + "0 MiB/s", StringComparison.Ordinal) &&
                    description.Contains("写入速度 3" + decimalSeparator + "0 MiB/s", StringComparison.Ordinal) &&
                    description.Contains("平均响应 0" + decimalSeparator + "5 毫秒", StringComparison.Ordinal),
                    "ActivityAccessibilityRetainsAllSourceMetrics");
                Check(ToolTipsInactive(activity) && activity.DetailsButton is { Visible: true },
                    "ActivityDetailsRemainsAvailableWithoutActiveTooltipProvider");
                activity.AdvanceClock(at.AddSeconds(6));
                Check(activity.AccessibleDescription!.Contains("采样延迟", StringComparison.Ordinal) &&
                    activity.AccessibleDescription.Contains("活动时间 —", StringComparison.Ordinal) && ToolTipsInactive(activity),
                    "ActivityDelayedStateRemainsAccessibleWithoutPopup");
            }
            finally { form.Close(); Application.DoEvents(); }
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;

    private static bool ToolTipsInactive(object owner) => owner.GetType().GetFields(Hidden)
        .Where(field => typeof(ToolTip).IsAssignableFrom(field.FieldType))
        .All(field => field.GetValue(owner) is not ToolTip { Active: true });

    private static Bitmap Capture(Control control)
    {
        var image = new Bitmap(control.Width, control.Height);
        image.SetResolution(control.DeviceDpi, control.DeviceDpi);
        control.DrawToBitmap(image, control.ClientRectangle);
        return image;
    }

    private static int ChangedPixels(Bitmap first, Bitmap second, Rectangle region)
    {
        region.Intersect(new Rectangle(Point.Empty, first.Size));
        int changed = 0;
        for (int y = region.Top; y < region.Bottom; y++)
        for (int x = region.Left; x < region.Right; x++)
        {
            Color old = first.GetPixel(x, y), current = second.GetPixel(x, y);
            if (Math.Abs(old.R - current.R) + Math.Abs(old.G - current.G) + Math.Abs(old.B - current.B) >= 30) changed++;
        }
        return changed;
    }

    private sealed class Failure(string code) : Exception("HoverPopupPolicy")
    {
        internal string Code { get; } = code;
    }
}
