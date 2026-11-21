using DiskAtlas;
using System.Reflection;

// Native editable controls and owned bitmaps only. Pixel checks cover surface
// consistency and complete outlines; the real desktop is reviewed separately.
internal static class InputFrameSurfaceTests
{
    internal static int Run(bool dpi96 = false)
    {
        string failure = "None";
        int passed = 0;
        var worker = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var paper = Paper(Color.FromArgb(80, 40, 140), Color.FromArgb(240, 115, 80));
                using var replacement = Paper(Color.FromArgb(25, 90, 45), Color.FromArgb(120, 175, 235));
                using var font = Theme.Font(Theme.ControlFontSize);
                using var form = new Form { AutoScaleMode = AutoScaleMode.None, ShowInTaskbar = false, BackgroundImage = paper };
                using var path = new TextBox { Text = "X:\\AsukaLens-Demo", ReadOnly = true, BorderStyle = BorderStyle.None, Font = font };
                using var search = new TextBox { PlaceholderText = "搜索当前层级名称…", BorderStyle = BorderStyle.None, Font = font };
                using var pathFrame = new InputFrame(path) { Dock = DockStyle.None };
                using var searchFrame = new InputFrame(search) { Dock = DockStyle.None };
                using var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = font };
                combo.Items.Add("自动"); combo.SelectedIndex = 0;
                using var comboFrame = new InputFrame(combo, "扫描策略") { Dock = DockStyle.None };
                using var card = new StatCard { Title = "文件数量", Value = "52", ShowCaption = false };
                using var sink = new Button { Text = "Focus" };
                form.Controls.AddRange([pathFrame, searchFrame, comboFrame, card, sink]);
                form.Show();
                float scale = form.DeviceDpi / 96f;
                int S(float value) => (int)Math.Round(value * scale);
                form.ClientSize = new Size(S(650), S(260));
                pathFrame.Bounds = new Rectangle(S(20), S(20), S(295), S(40));
                searchFrame.Bounds = new Rectangle(S(335), S(20), S(295), S(40));
                comboFrame.Bounds = new Rectangle(S(20), S(90), S(260), S(40));
                card.Bounds = new Rectangle(S(335), S(90), S(295), S(90));
                sink.Bounds = new Rectangle(S(20), S(195), S(100), S(35));
                form.PerformLayout(); sink.Focus(); form.Refresh(); Application.DoEvents();

                foreach (var pair in new[] { (Frame: pathFrame, Editor: path), (Frame: searchFrame, Editor: search) })
                {
                    sink.Focus(); form.Refresh(); Application.DoEvents();
                    using var normal = Draw(pair.Frame);
                    var expected = Surface(pair.Editor);
                    Check(pair.Editor.BackColor == expected && pair.Frame.BackColor == expected, "TextFrameAndNativeEditorShareNeutralSurface");
                    Check(Interior(normal, pair.Frame).ToArgb() == expected.ToArgb(), "TextFrameInteriorHasNoWallpaperTint");
                    Check(NativeInterior(normal, pair.Editor).ToArgb() == expected.ToArgb(), "NativeTextAreaHasNoSeparatePinkBlock");
                    var oldBounds = pair.Frame.Bounds;
                    CheckOutline(normal, SystemInformation.HighContrast ? SystemColors.WindowText : Theme.Border, Theme.ControlRadius * scale, "Normal");
                    pair.Editor.Focus(); form.Refresh(); Application.DoEvents();
                    using var focused = Draw(pair.Frame);
                    Check(pair.Editor.Focused && pair.Frame.Bounds == oldBounds, "NativeFocusDoesNotResizeTextFrame");
                    Check(Interior(focused, pair.Frame).ToArgb() == expected.ToArgb() && NativeInterior(focused, pair.Editor).ToArgb() == expected.ToArgb(),
                        "FocusChangesOnlyOutlineAndLeavesNeutralSurface");
                    CheckOutline(focused, SystemInformation.HighContrast ? SystemColors.WindowText : Theme.Accent, Theme.ControlRadius * scale, "Focused");
                    sink.Focus(); pair.Editor.Enabled = false; form.Refresh(); Application.DoEvents();
                    using var disabled = Draw(pair.Frame);
                    Check(pair.Editor.BackColor == Surface(pair.Editor) && Interior(disabled, pair.Frame).ToArgb() == Surface(pair.Editor).ToArgb(),
                        "DisabledInputKeepsConsistentNeutralSurface");
                    CheckOutline(disabled, SystemInformation.HighContrast ? SystemColors.GrayText : Theme.Border, Theme.ControlRadius * scale, "Disabled");
                    pair.Editor.Enabled = true;
                }

                form.BackgroundImage = replacement; form.Invalidate(true); form.Refresh(); Application.DoEvents();
                using (var first = Draw(pathFrame))
                using (var second = Draw(searchFrame))
                {
                    Check(path.BackColor == search.BackColor && path.BackColor == Surface(path), "BackgroundChangeDoesNotRetintTextInputs");
                    Check(Interior(first, pathFrame) == Interior(second, searchFrame), "DifferentWallpaperPositionsKeepMatchingInputSurfaces");
                }
                using (var painted = Draw(comboFrame))
                {
                    var expected = SystemInformation.HighContrast ? SystemColors.Window :
                        Wallpaper.SampleSurfaceColor(comboFrame, combo.Bounds, WallpaperSurface.Chrome);
                    Check(combo.BackColor == expected, "ComboRetainsItsExistingWallpaperSurfacePolicy");
                    CheckOutline(painted, SystemInformation.HighContrast ? SystemColors.WindowText : Theme.Border, Theme.ControlRadius * scale, "Combo");
                }
                using (var painted = Draw(card))
                    CheckOutline(painted, SystemInformation.HighContrast ? SystemColors.WindowText : Theme.Border, Theme.CardRadius * scale, "StatCard");
                foreach (var rounded in new Control[] { pathFrame, searchFrame, comboFrame, card })
                {
                    using var graphics = rounded.CreateGraphics();
                    Check(rounded.Region != null && !rounded.Region.IsVisible(0, 0, graphics) &&
                        rounded.Region.IsVisible(rounded.Width / 2, rounded.Height / 2, graphics), "NativeRegionKeepsRoundedExteriorAndVisibleCentre");
                }
                form.Close();

                void CheckOutline(Bitmap pixels, Color color, float radius, string role)
                {
                    // Search only the narrow expected stroke band, not content
                    // that could accidentally resemble the border colour.
                    int band = Math.Max(3, S(3));
                    Point[] centres = [new(pixels.Width / 2, 0), new(pixels.Width / 2, pixels.Height - 1),
                        new(0, pixels.Height / 2), new(pixels.Width - 1, pixels.Height / 2)];
                    foreach (var centre in centres)
                        Check(Nearest(pixels, centre, band, color) <= 70, role + "OutlineCoversEveryStraightEdge");
                    int near = (int)Math.Round(radius * (1 - 1 / Math.Sqrt(2)));
                    Point[] corners = [new(near, near), new(pixels.Width - 1 - near, near),
                        new(near, pixels.Height - 1 - near), new(pixels.Width - 1 - near, pixels.Height - 1 - near)];
                    foreach (var point in corners)
                        Check(Nearest(pixels, point, Math.Max(2, S(2)), color) <= 70, role + "OutlineContinuesThroughEveryRoundedCorner");
                }
                void Check(bool condition, string code)
                {
                    if (!condition) throw new CheckFailure(code);
                    passed++;
                }
            }
            catch (CheckFailure error) { failure = error.Code; }
            catch { failure = "OwnedInputFixtureFailure"; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(20))) failure = "InputFixtureTimeout";
        if (failure != "None") { Console.Error.WriteLine("FAIL input frame surface code=" + failure + "; checks=" + passed); return 1; }
        Console.WriteLine("PASS input frame surface checks=" + passed + "; owned_fixture_only=True; desktop_review_separate=True");
        return 0;
    }

    private static Color Surface(Control input) => SystemInformation.HighContrast
        ? input.Enabled ? SystemColors.Window : SystemColors.Control : Theme.Surface;
    private static Bitmap Draw(Control control)
    {
        var image = new Bitmap(control.Width, control.Height);
        image.SetResolution(control.DeviceDpi, control.DeviceDpi);
        control.DrawToBitmap(image, control.ClientRectangle); return image;
    }
    private static Color Interior(Bitmap image, Control frame) => image.GetPixel(frame.Width / 2, Math.Max(3, (int)(5 * frame.DeviceDpi / 96f)));
    private static Color NativeInterior(Bitmap image, Control input) => image.GetPixel(input.Right - 3, input.Top + input.Height / 2);
    private static int Nearest(Bitmap image, Point centre, int range, Color expected)
    {
        int best = int.MaxValue;
        for (int y = Math.Max(0, centre.Y - range); y <= Math.Min(image.Height - 1, centre.Y + range); y++)
        for (int x = Math.Max(0, centre.X - range); x <= Math.Min(image.Width - 1, centre.X + range); x++)
        {
            Color color = image.GetPixel(x, y);
            best = Math.Min(best, Math.Abs(color.R - expected.R) + Math.Abs(color.G - expected.G) + Math.Abs(color.B - expected.B));
        }
        return best;
    }
    private static Bitmap Paper(Color left, Color right)
    {
        var image = new Bitmap(80, 50);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(left);
        using var fill = new SolidBrush(right); graphics.FillRectangle(fill, 40, 0, 40, 50);
        return image;
    }
    private sealed class CheckFailure(string code) : Exception("InputFrameSurface") { internal string Code { get; } = code; }
}
