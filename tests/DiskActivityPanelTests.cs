using DiskAtlas;
using System.Reflection;

internal static class DiskActivityPanelTests
{
    public static int Run(bool dpi96 = false)
    {
        Exception? failure = null;
        int passed = 0;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F));
                using var form = new Form { Size = new Size(1150, 260), ShowInTaskbar = false };
                using var panel = new DiskActivityPanel { Dock = DockStyle.Fill };
                form.Controls.Add(panel);
                form.Show();
                Application.DoEvents();
                void Check(bool condition, string code)
                {
                    if (!condition) throw new InvalidOperationException(code);
                    passed++;
                    Console.WriteLine("PASS disk activity panel " + code);
                }
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                int Count() => (int)typeof(DiskActivityPanel).GetField("historyCount", flags)!.GetValue(panel)!;
                List<PointF[]> Segments() => (List<PointF[]>)typeof(DiskActivityPanel)
                    .GetMethod("BuildSegments", flags)!.Invoke(panel, [new RectangleF(0, 0, 600, 100)])!;
                var button = panel.Controls.OfType<Button>().Single();
                var box = panel.Controls.OfType<ComboBox>().Single();
                int expanded = 0, changed = 0;
                panel.ExpansionChanged += (_, _) => expanded++;
                panel.TargetChanged += (_, _) => changed++;
                Check(!panel.Expanded && panel.SelectedDiskNumber == null, "starts collapsed and follows scan");
                button.PerformClick();
                Check(panel.Expanded && expanded == 1 && button.AccessibleName!.Contains("收起"), "expand is keyboard-focusable button");
                button.PerformClick();
                Check(!panel.Expanded && expanded == 2, "collapse raises one event");
                panel.SetTargets([new(0, "磁盘 0"), new(1, "磁盘 1"), new(1, "重复")]);
                Check(box.Items.Count == 3 && changed == 0, "targets deduplicate without changing follow selection");
                box.SelectedIndex = 2;
                Check(panel.SelectedDiskNumber == 1 && changed == 1, "explicit disk selection raises event");
                panel.SetTargets([new(1, "磁盘 1 · 更新"), new(0, "磁盘 0")]);
                Check(panel.SelectedDiskNumber == 1 && changed == 1, "target refresh preserves selected disk");
                panel.SetTargets([new(0, "磁盘 0")]);
                Check(panel.SelectedDiskNumber == null && changed == 2, "missing disk returns to follow selection");
                DateTimeOffset at = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                DiskActivitySample Sample(int second, double? active = 40, DiskActivityStatus status = DiskActivityStatus.Available, int disk = 0) =>
                    new(at.AddSeconds(second), disk, active, 1024 * 1024, 2 * 1024 * 1024, .5, status);
                for (int i = 0; i < 150; i++) panel.SetSample(Sample(i));
                Check(Count() == 60 && Segments().Single().Length == 60, "history is bounded to sixty points");
                panel.SetSample(Sample(148, 90));
                Check(Count() == 60, "out-of-order samples cannot replace current state");
                panel.SetSample(Sample(211));
                Check(Count() == 1, "sixty-second gap expires old history");
                panel.ResetHistory();
                panel.SetSample(Sample(0)); panel.SetSample(Sample(1));
                panel.SetSample(Sample(2, null, DiskActivityStatus.Waiting));
                panel.SetSample(Sample(3)); panel.SetSample(Sample(4));
                Check(Segments().Count == 2 && Segments().All(segment => segment.Length == 2), "waiting leaves a gap instead of zero");
                panel.SetSample(Sample(10));
                Check(Segments().Count == 3, "timestamp discontinuity breaks curve");
                panel.SetSample(Sample(11, null));
                Check(panel.AccessibleDescription!.Contains("活动时间 —"), "unknown activity is a dash");
                panel.SetSample(new(at.AddSeconds(12), 0, double.NaN, double.PositiveInfinity, -1, null, DiskActivityStatus.Available));
                Check(panel.AccessibleDescription!.Count(character => character == '—') == 4, "invalid and unknown metrics stay unknown");
                panel.SetSample(Sample(13, 30, DiskActivityStatus.Unavailable));
                Check(panel.AccessibleDescription!.Count(character => character == '—') == 4, "unavailable metrics never show supplied stale values");
                panel.SetSample(Sample(14, disk: 1));
                Check(Count() == 1, "switching disks clears previous disk history");
                Check(panel.AccessibleDescription!.Contains("MiB/s") && panel.AccessibleDescription.Contains("毫秒"), "units explicitly identify MiB and milliseconds");
                panel.SetMessage("等待目标磁盘");
                Check(Count() == 0 && panel.AccessibleDescription!.Contains("等待目标磁盘"), "message clears stale samples");
                button.PerformClick();
                int S(int logical) => (int)Math.Round(logical * panel.DeviceDpi / 96f);
                foreach (int width in new[] { 800, 1100 })
                {
                    float shortChartHeight = 0;
                    foreach (int height in new[] { 112, 180 })
                    {
                        form.ClientSize = new Size(S(width), S(height));
                        form.PerformLayout(); panel.PerformLayout();
                        using var image = new Bitmap(panel.Width, panel.Height);
                        panel.DrawToBitmap(image, panel.ClientRectangle);
                        var layout = panel.ExpandedGeometry();
                        Check(box.Right < button.Left && button.Right <= panel.Width && box.Left >= 0,
                            "header controls do not overlap at " + width + "x" + height);
                        Check(layout.TwoRows == (height == 180),
                            "metric arrangement follows available height at " + width + "x" + height);
                        Check(layout.Body.Contains(Rectangle.Ceiling(layout.Chart)) && layout.Body.Contains(layout.Caption),
                            "chart and caption remain in body at " + width + "x" + height);
                        Check(layout.Metrics.Count == 4 && layout.Metrics.All(metric =>
                            layout.Body.Contains(metric.Label) && layout.Body.Contains(metric.Value) &&
                            metric.Label.Height >= S(20) && metric.Value.Height >= S(28) &&
                            !metric.Label.IntersectsWith(metric.Value)),
                            "all metric labels and values fit without overlap at " + width + "x" + height);
                        var cells = layout.Metrics.Select(metric => Rectangle.Union(metric.Label, metric.Value)).ToArray();
                        Check(Enumerable.Range(0, cells.Length).All(left => Enumerable.Range(left + 1, cells.Length - left - 1)
                            .All(right => !cells[left].IntersectsWith(cells[right]))),
                            "metric cells remain separate at " + width + "x" + height);
                        if (height == 112) shortChartHeight = layout.Chart.Height;
                        else Check(layout.Chart.Height > shortChartHeight && layout.Chart.Height >= S(110),
                            "expanded curve uses added height at width " + width);
                    }
                }
                button.PerformClick();
                panel.Dock = DockStyle.None;
                panel.Size = new Size(S(800), S(30));
                panel.PerformLayout();
                Check(!panel.Expanded && button.Top >= 0 && box.Top >= 0 && button.Bottom <= panel.Height && box.Bottom <= panel.Height && box.Right < button.Left,
                    "thirty-pixel collapsed summary keeps both controls inside one line");
                Check(box.Height >= box.PreferredHeight && Math.Abs(box.Font.SizeInPoints - Theme.CaptionFontSize) < .01f,
                    "collapsed native selector preserves preferred font height");
                panel.ResetHistory();
                Check(Count() == 0 && Segments().Count == 0, "reset drops history");
                panel.SetSample(Sample(0)); panel.SetSample(Sample(1));
                panel.AdvanceClock(at.AddSeconds(3));
                Check(panel.AccessibleDescription!.Contains("活动时间 40%") && Segments().Single()[^1].X < 600,
                    "host clock advances the chart without inventing samples");
                panel.AdvanceClock(at.AddSeconds(6));
                Check(panel.AccessibleDescription!.Contains("采样延迟") && panel.AccessibleDescription.Contains("活动时间 —"),
                    "blocked provider cannot leave stale current metrics visible");
                panel.SetSample(Sample(1, 90));
                Check(panel.AccessibleDescription!.Contains("活动时间 —"), "late stale sample cannot revive current metrics");
                panel.SetSample(Sample(7, disk: 1));
                Check(Count() == 1 && panel.AccessibleDescription!.Contains("活动时间 40%"),
                    "fresh sample after a stall recovers and clears another disk history");
                panel.AdvanceClock(at.AddSeconds(68));
                Check(Count() == 0 && Segments().Count == 0, "host clock expires history without provider callbacks");
                panel.SetSample(Sample(69));
                panel.AdvanceClock(at.AddSeconds(10));
                Check(Count() == 0 && panel.AccessibleDescription!.Contains("时间变化"),
                    "wall clock rollback discards incompatible history");
                int eventsBeforeDispose = expanded + changed;
                panel.Dispose();
                panel.SetTargets([new(1, "磁盘 1")]);
                panel.SetSample(Sample(15)); panel.SetMessage("ignored"); panel.ResetHistory();
                Check(expanded + changed == eventsBeforeDispose && panel.IsDisposed, "late callbacks after disposal are ignored");
                form.Close();

                using var sidebarHost = new Form { ClientSize = new Size(480, 320), ShowInTaskbar = false };
                using var sidebar = new DiskActivityPanel { SidebarMode = true, Location = new Point(10, 10), Size = new Size(204, 44) };
                sidebarHost.Controls.Add(sidebar); sidebarHost.Show(); Application.DoEvents();
                int sidebarExpansions = 0, detailRequests = 0, sidebarTargets = 0;
                sidebar.ExpansionChanged += (_, _) => sidebarExpansions++;
                sidebar.DetailsRequested += (_, _) => detailRequests++;
                sidebar.TargetChanged += (_, _) => sidebarTargets++;
                var sidebarDetails = sidebar.DetailsButton!;
                Check(!sidebar.Expanded && sidebarExpansions == 0 && !sidebar.TargetSelector.Visible && sidebar.ToggleButton.Text == "启用",
                    "sidebar starts off without requesting a sampling transition");
                sidebarDetails.PerformClick();
                Check(detailRequests == 1 && sidebarExpansions == 0 && !sidebar.Expanded,
                    "requesting details does not enable activity sampling");
                sidebar.ToggleButton.PerformClick();
                Check(sidebar.Expanded && sidebarExpansions == 1 && sidebar.ToggleButton.Text == "暂停",
                    "sidebar enable keeps the original expansion contract");
                int MiniS(int logical) => (int)Math.Round(logical * sidebar.DeviceDpi / 96f);
                foreach (int height in new[] { 44, 96, 108, 160 })
                {
                    sidebar.Size = new Size(MiniS(204), MiniS(height)); sidebar.PerformLayout();
                    using var image = new Bitmap(sidebar.Width, sidebar.Height); sidebar.DrawToBitmap(image, sidebar.ClientRectangle);
                    var mini = sidebar.SidebarGeometry();
                    Check(sidebarDetails.Right <= sidebar.Width && sidebar.ToggleButton.Right < sidebarDetails.Left &&
                        sidebar.ToggleButton.Bottom <= sidebar.Height,
                        "sidebar controls stay in their header at height " + height);
                    Check(mini.ShowsCurve == (height >= 100), "sidebar uses an honest compact fallback at height " + height);
                    if (mini.ShowsCurve)
                        Check(sidebar.ClientRectangle.Contains(Rectangle.Ceiling(mini.Chart)) && sidebar.ClientRectangle.Contains(mini.ActivityValue) &&
                            sidebar.ClientRectangle.Contains(mini.Read) && sidebar.ClientRectangle.Contains(mini.Write) &&
                            !mini.Read.IntersectsWith(mini.Write) && !mini.ActivityValue.IntersectsWith(mini.Read),
                            "sidebar curve and metric lines do not overlap at height " + height);
                }
                sidebar.SetTargets([new(0, "磁盘 0"), new(1, "磁盘 1")]);
                for (int i = 0; i < 70; i++) sidebar.SetSample(Sample(i));
                var beforeDetails = sidebar.GetViewSnapshot();
                int Listeners() => (typeof(DiskActivityPanel).GetField("StateChanged", flags)!.GetValue(sidebar) as MulticastDelegate)?.GetInvocationList().Length ?? 0;
                void CheckDetailsBounds(DiskActivityDetailsDialog detail, string state)
                {
                    var wide = detail.ViewPanel;
                    var geometry = wide.ExpandedGeometry();
                    var metricFont = (Font)typeof(DiskActivityPanel).GetField("value", flags)!.GetValue(wide)!;
                    var captionFont = (Font)typeof(DiskActivityPanel).GetField("small", flags)!.GetValue(wide)!;
                    using var graphics = wide.CreateGraphics();
                    string[] measurements = ["100%", "22.3 MiB/s", "1.1 MiB/s", "0.5 毫秒"];
                    Check(Math.Abs(metricFont.SizeInPoints - 14) < .01f && Enumerable.Range(0, 4).All(index =>
                    {
                        var measured = TextRenderer.MeasureText(graphics, measurements[index], metricFont,
                            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                        var bounds = geometry.Metrics[index].Value;
                        return measured.Width <= bounds.Width && measured.Height <= bounds.Height;
                    }), "details shows typical complete metric values and units at " + state);
                    var captionSize = TextRenderer.MeasureText(graphics, "最近 60 秒 · 活动时间", captionFont,
                        new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                    Check(Math.Abs(captionFont.SizeInPoints - Theme.CaptionFontSize) < .01f &&
                        captionSize.Width <= geometry.Caption.Width && captionSize.Height <= geometry.Caption.Height,
                        "details chart caption fits its unchanged font at " + state);
                    Check(detail.TargetSelector.Left >= 0 && detail.TargetSelector.Right <= wide.Width &&
                        detail.TargetSelector.Top >= 0 && detail.TargetSelector.Bottom <= (int)Math.Round(30 * wide.DeviceDpi / 96f),
                        "details native selector stays within its DPI-scaled header at " + state);
                }
                int beforeListeners = Listeners();
                using (var dialog = new DiskActivityDetailsDialog(sidebar))
                {
                    dialog.Show(sidebarHost); Application.DoEvents();
                    var area = Screen.FromControl(sidebarHost).WorkingArea;
                    var ownerBounds = sidebarHost.Bounds;
                    var expectedLeft = Math.Clamp(ownerBounds.Left + (ownerBounds.Width - dialog.Width) / 2,
                        area.Left, Math.Max(area.Left, area.Right - dialog.Width));
                    var expectedTop = Math.Clamp(ownerBounds.Top + (ownerBounds.Height - dialog.Height) / 2,
                        area.Top, Math.Max(area.Top, area.Bottom - dialog.Height));
                    Check(dialog.Owner == sidebarHost && Math.Abs(dialog.Left - expectedLeft) <= 1 && Math.Abs(dialog.Top - expectedTop) <= 1 &&
                        area.Contains(dialog.Bounds), "modeless details centers on its owner within the working area");
                    Check(dialog.ClientSize.Width >= Math.Min((int)Math.Round(860 * dialog.DeviceDpi / 96f), area.Width - (dialog.Width - dialog.ClientSize.Width)) &&
                        dialog.ClientSize.Height >= Math.Min((int)Math.Round(250 * dialog.DeviceDpi / 96f), area.Height - (dialog.Height - dialog.ClientSize.Height)),
                        "details client dimensions follow the actual monitor DPI");
                    CheckDetailsBounds(dialog, "default size");
                    dialog.Size = dialog.MinimumSize; dialog.PerformLayout(); Application.DoEvents();
                    CheckDetailsBounds(dialog, "minimum size");
                    var mirrored = dialog.ViewPanel.GetViewSnapshot();
                    Check(sidebarExpansions == 1 && sidebar.Expanded && Listeners() == beforeListeners + 1 && mirrored.History.Count == 60 &&
                        mirrored.History.SequenceEqual(beforeDetails.History) && mirrored.Current == beforeDetails.Current,
                        "details mirrors the same bounded history without changing source enable state");
                    var item = dialog.TargetSelector.Items[1];
                    sidebar.SetSample(Sample(70));
                    Check(ReferenceEquals(item, dialog.TargetSelector.Items[1]) && dialog.TargetSelector.SelectedIndex == 0,
                        "ordinary samples preserve selector items and selection");
                    dialog.TargetSelector.SelectedIndex = 2;
                    Check(sidebar.SelectedDiskNumber == 1 && sidebarTargets == 1 && sidebar.Expanded,
                        "detail disk selection returns to the canonical panel without changing enable state");
                    sidebar.SetSample(Sample(71, disk: 1));
                    Check(dialog.ViewPanel.GetViewSnapshot().Current == sidebar.GetViewSnapshot().Current,
                        "details observes canonical target samples");
                    sidebar.SetMessage("已暂停 · 测试状态");
                    Check(dialog.ViewPanel.GetViewSnapshot().History.Count == 0 && dialog.ViewPanel.AccessibleDescription!.Contains("活动时间 —") &&
                        dialog.ViewPanel.GetViewSnapshot().Message == "已暂停 · 测试状态",
                        "pause and unavailable values synchronize to the detail view");
                    dialog.ApplyTheme();
                    dialog.Close(); Application.DoEvents();
                }
                Check(Listeners() == beforeListeners && sidebar.Expanded && !sidebar.IsDisposed,
                    "closing details releases subscription and preserves canonical sampling state");
                sidebar.ToggleButton.PerformClick();
                using (var pausedDialog = new DiskActivityDetailsDialog(sidebar))
                {
                    pausedDialog.Show(sidebarHost); Application.DoEvents();
                    Check(!sidebar.Expanded && sidebarExpansions == 2 && !pausedDialog.ViewPanel.GetViewSnapshot().Enabled,
                        "opening details while off cannot enable the source");
                    sidebar.Dispose(); Application.DoEvents();
                    Check(pausedDialog.IsDisposed, "canonical disposal closes its detail view");
                }
                sidebarHost.Close();
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new InvalidOperationException("Disk activity panel test timed out");
        if (failure != null) throw new InvalidOperationException("Disk activity panel test failed", failure);
        Console.WriteLine("PASS disk activity panel checks: " + passed);
        return 0;
    }
}
