using DiskAtlas;

// Actual navigation controls with a generic activity host. No sampler, scan,
// settings, user data, or desktop configuration is touched.
internal static class NavigationActivityLayoutTests
{
    public static int Run(bool virtualized96Dpi = false)
    {
        int passed = 0;
        string? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(virtualized96Dpi ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new Form { AutoScaleMode = AutoScaleMode.None, ShowInTaskbar = false };
                using var navigation = new NavigationPanel { Dock = DockStyle.Fill };
                form.Controls.Add(navigation);
                using var activity = new Panel { BackColor = Color.LightSalmon };
                bool expanded = false;
                form.Shown += (_, _) =>
                {
                    try
                    {
                        float scale = navigation.DeviceDpi / 96f;
                        SetHeight(960);
                        CheckRetiredFooter(navigation);
                        navigation.AttachActivity(activity, () => expanded);
                        Check(ReferenceEquals(navigation.ActivityContent, activity) && ReferenceEquals(activity.Parent, navigation), "ActivityOwnership");
                        var common = new Dictionary<bool, Rectangle>();
                        foreach (int height in new[] { 960, 742, 690, 620 })
                        foreach (bool open in new[] { false, true })
                        {
                            expanded = open;
                            SetHeight(height);
                            var buttons = navigation.Controls.OfType<ActionButton>().ToArray();
                            Check(buttons.Length == 3, "NavigationButtonsRetained");
                            foreach (var button in buttons)
                                Check(button.Width > 0 && button.Height > 0 && navigation.ClientRectangle.Contains(button.Bounds) &&
                                    button.Bottom <= navigation.LegendBounds.Top, "NavigationButtonBounds");
                            Check(navigation.LegendBounds.Height > 0 && navigation.ClientRectangle.Contains(navigation.LegendBounds), "LegendContained");
                            if (height == 620)
                            {
                                Check(!activity.Visible && activity.Bounds == Rectangle.Empty && navigation.ActivityBounds == Rectangle.Empty,
                                    "NoSpaceActivityHidden");
                            }
                            else
                            {
                                Check(activity.Visible && navigation.ClientRectangle.Contains(activity.Bounds) &&
                                    activity.Top > navigation.LegendBounds.Bottom && activity.Left > 0 && activity.Right < navigation.Width &&
                                    activity.Bottom < navigation.Height && navigation.ActivityBounds == activity.Bounds, "ActivityDoesNotCoverLegend");
                            }
                            var enabledBounds = activity.Bounds;
                            activity.Enabled = false; navigation.PerformLayout();
                            Check(activity.Bounds == enabledBounds, "UnavailableActivityKeepsLayout");
                            activity.Enabled = true; navigation.PerformLayout();
                            Check(activity.Bounds == enabledBounds, "AvailableActivityKeepsLayout");
                            if (height == 960) common.Add(open, activity.Bounds);
                        }
                        Check(common[true].Height > common[false].Height && common[true].Bottom == common[false].Bottom,
                            "ExpansionAnchoredAtBottom");
                        using var replacement = new Panel();
                        expanded = false; SetHeight(742);
                        navigation.AttachActivity(replacement, () => expanded);
                        Check(activity.Parent == null && !activity.IsDisposed && ReferenceEquals(replacement.Parent, navigation) &&
                            ReferenceEquals(navigation.ActivityContent, replacement), "ReplacementTransfersWithoutDisposingPrevious");
                        Check(replacement.Visible && replacement.Top > navigation.LegendBounds.Bottom, "ReplacementLaidOut");

                        void SetHeight(int logicalHeight)
                        {
                            form.ClientSize = new Size((int)Math.Round(232 * scale), (int)Math.Round(logicalHeight * scale));
                            form.PerformLayout(); navigation.PerformLayout(); form.Refresh();
                        }
                    }
                    catch (Failure error) { failure = error.Code; }
                    catch { failure = "UnexpectedLayoutFailure"; }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (Failure error) { failure = error.Code; }
            catch { failure = "UnexpectedUiFailure"; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) failure = "UiWorkerStillActive";
        if (failure != null)
        {
            Console.Error.WriteLine("FAIL navigation activity layout code=" + failure + "; no private details emitted");
            return 1;
        }
        Console.WriteLine("PASS navigation activity layout checks=" + passed + "; generic host, no sampling or scan");
        return 0;

        void Check(bool condition, string code)
        {
            if (!condition) throw new Failure(code);
            passed++;
        }
        void CheckRetiredFooter(NavigationPanel navigation)
        {
            using var actual = new Bitmap(navigation.Width, navigation.Height);
            using var expected = new Bitmap(navigation.Width, navigation.Height);
            navigation.DrawToBitmap(actual, navigation.ClientRectangle);
            using (var graphics = Graphics.FromImage(expected)) Wallpaper.Paint(navigation, graphics, 225);
            float scale = navigation.DeviceDpi / 96f;
            var region = new Rectangle((int)(25 * scale), navigation.Height - (int)(46 * scale),
                navigation.Width - (int)(45 * scale), (int)(25 * scale));
            bool equal = region.Width > 0 && region.Height > 0;
            for (int y = region.Top; equal && y < region.Bottom; y++)
            for (int x = region.Left; equal && x < region.Right; x++)
                equal = actual.GetPixel(x, y).ToArgb() == expected.GetPixel(x, y).ToArgb();
            Check(equal, "RetiredFooterAbsentFromPaint");
        }
    }

    private sealed class Failure(string code) : Exception("NavigationActivityLayout") { internal string Code { get; } = code; }
}
