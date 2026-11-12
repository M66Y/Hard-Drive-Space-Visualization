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
                foreach (int width in new[] { 800, 1100 })
                {
                    form.ClientSize = new Size(width, 180);
                    form.PerformLayout(); panel.PerformLayout();
                    using var image = new Bitmap(panel.Width, panel.Height);
                    panel.DrawToBitmap(image, panel.ClientRectangle);
                    Check(box.Right < button.Left && button.Right <= panel.Width && box.Left >= 0,
                        "header controls do not overlap at width " + width);
                }
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
