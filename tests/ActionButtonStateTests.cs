using DiskAtlas;
using System.Reflection;

// Native WinForms controls in one STA. This checks bounded motion and real
// font geometry, not frame rate or desktop appearance. No files or scans.
internal static class ActionButtonStateTests
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
                using var button = new ActionButton("测试操作");
                using var quiet = new ActionButton("辅助操作") { Subtle = true, MotionEnabled = false };
                using var card = new StatCard { Title = "统计值", Value = "102.45 GB + 未知", Caption = "统计说明" };
                using var input = new TextBox { Text = "输入内容", BorderStyle = BorderStyle.None, Font = Theme.Font(Theme.BodyFontSize) };
                using var frame = new InputFrame(input) { Dock = DockStyle.None };
                using var strategy = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = Theme.Font(Theme.BodyFontSize) };
                strategy.Items.AddRange(["自动", "低占用", "高速"]); strategy.SelectedIndex = 1;
                using var captioned = new InputFrame(strategy, "扫描策略") { Dock = DockStyle.None };
                using var toggle = new RoundedToggle { Font = Theme.Font(Theme.BodyFontSize) };
                form.Controls.AddRange([button, quiet, card, frame, captioned, toggle]);
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        float scale = form.DeviceDpi / 96f;
                        int S(float value) => (int)Math.Round(value * scale);
                        form.ClientSize = new Size(S(520), S(300));
                        button.Bounds = new Rectangle(S(20), S(20), S(180), S(42));
                        quiet.Bounds = new Rectangle(S(220), S(20), S(180), S(42));
                        card.Bounds = new Rectangle(S(20), S(85), S(240), S(104));
                        frame.Bounds = new Rectangle(S(20), S(220), S(240), S(40));
                        captioned.Bounds = new Rectangle(S(280), S(220), S(220), S(40));
                        toggle.Bounds = new Rectangle(S(280), S(85), S(170), S(38));
                        form.PerformLayout(); form.Refresh();
                        var fixedBounds = button.Bounds;
                        var fixedRegion = RegionBounds(button);
                        Check(!button.IsMotionRunning && Timer(button) == null, "MotionTimerIsLazy");
                        Check(!quiet.IsMotionRunning && Timer(quiet) == null, "ReducedMotionDoesNotAllocateTimer");
                        button.Focus();
                        quiet.Enabled = false; quiet.Enabled = true;
                        Invoke(quiet, "OnMouseLeave", EventArgs.Empty);
                        CheckSubtleBackground(quiet);
                        quiet.Enabled = false;
                        CheckSubtleBackground(quiet);
                        quiet.Enabled = true;
                        using (var context = toggle.CreateGraphics())
                        {
                            var off = toggle.LayoutParts(context);
                            var text = TextRenderer.MeasureText(context, toggle.Text, toggle.Font,
                                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                            Check(toggle.ClientRectangle.Contains(off.Label) && toggle.ClientRectangle.Contains(off.Badge) &&
                                toggle.ClientRectangle.Contains(Rectangle.Ceiling(off.Track)) && off.Label.Right <= off.Badge.Left &&
                                off.Badge.Right <= off.Track.Left, "NativeTogglePartsRemainContainedAndSeparate");
                            Check(text.Width <= off.Label.Width && text.Height <= off.Label.Height, "NativeToggleFullLabelFitsAtSettingsWidth");
                            Check(toggle.Region != null && !toggle.Region.IsVisible(0, 0, context) &&
                                toggle.Region.IsVisible(toggle.Width / 2, toggle.Height / 2, context), "NativeToggleHasCompleteRoundedRegion");
                            var bounds = toggle.Bounds;
                            int changes = 0; toggle.CheckedChanged += (_, _) => changes++;
                            toggle.Checked = true;
                            var on = toggle.LayoutParts(context);
                            Check(changes == 1 && on.Thumb.Left > off.Thumb.Left && on.Track == off.Track && toggle.Bounds == bounds,
                                "NativeToggleCheckedStateMovesOnlyThumb");
                            Check(toggle.AccessibilityObject.Role == AccessibleRole.CheckButton &&
                                (toggle.AccessibilityObject.State & AccessibleStates.Checked) != 0, "NativeToggleExposesCheckedAccessibilityState");
                            toggle.Checked = false;
                            Check(changes == 2 && toggle.LayoutParts(context).Thumb == off.Thumb, "NativeToggleUncheckedStateRestoresThumb");
                        }

                        button.Focus();
                        Check(button.Focused, "ButtonReceivesNativeFocus");
                        Invoke(button, "OnMouseEnter", EventArgs.Empty);
                        Invoke(button, "OnMouseDown", Mouse());
                        Check(!button.KeyboardFocusVisible, "PointerFocusHasNoPersistentInnerRing");
                        Check(button.Bounds == fixedBounds && RegionBounds(button) == fixedRegion, "PressPreservesBoundsAndHitRegion");
                        if (!SystemInformation.HighContrast)
                            Check(button.IsMotionRunning && button.PressVisual > 0, "PressStartsBoundedFeedback");
                        Invoke(button, "OnMouseUp", Mouse());
                        await Settled(button);
                        Check(Math.Abs(button.PressVisual) < .001f && !button.IsMotionRunning, "ReleaseSettlesWithoutIdleTimer");
                        Check(button.Bounds == fixedBounds && RegionBounds(button) == fixedRegion, "ReleasePreservesBoundsAndHitRegion");

                        button.Focus();
                        Invoke(button, "OnKeyDown", new KeyEventArgs(Keys.Left));
                        ShowKeyboardCues(button);
                        Check(button.KeyboardFocusVisible, "KeyboardFocusKeepsVisibleCue");
                        Invoke(button, "OnKeyDown", new KeyEventArgs(Keys.Space));
                        Check(button.PressVisual > 0, "SpacePressHasFeedback");
                        Invoke(button, "OnKeyUp", new KeyEventArgs(Keys.Space));
                        await Settled(button);

                        Invoke(quiet, "OnMouseEnter", EventArgs.Empty);
                        Invoke(quiet, "OnMouseDown", Mouse());
                        Check(!quiet.IsMotionRunning && quiet.PressVisual == 1 && Timer(quiet) == null, "ReducedMotionUsesImmediatePressedState");
                        Invoke(quiet, "OnMouseUp", Mouse());
                        Check(!quiet.IsMotionRunning && quiet.PressVisual == 0, "ReducedMotionReleaseIsImmediate");

                        StartPress(); button.MotionEnabled = false;
                        Check(!button.IsMotionRunning, "PreferenceStopsActiveMotion");
                        Invoke(button, "OnMouseUp", Mouse()); button.MotionEnabled = true;
                        StartPress(); button.Enabled = false;
                        Check(!button.IsMotionRunning && button.PressVisual == 0, "DisableSettlesAndStopsTimer");
                        button.Enabled = true;
                        StartPress(); button.Visible = false;
                        Check(!button.IsMotionRunning && button.PressVisual == 0, "HideSettlesAndStopsTimer");
                        button.Visible = true;
                        StartPress(); Invoke(button, "OnLostFocus", EventArgs.Empty);
                        Check(!button.IsMotionRunning && button.PressVisual == 0, "LostFocusSettlesAndStopsTimer");
                        StartPress(); button.Capture = false; Invoke(button, "OnMouseCaptureChanged", EventArgs.Empty);
                        Check(!button.IsMotionRunning && button.PressVisual == 0, "CaptureCancellationSettles");
                        Check(button.Bounds == fixedBounds, "AllInputStatesKeepLayoutStable");
                        await Task.Delay(35);
                        Check(!button.IsMotionRunning, "StoppedMotionDoesNotRestartAtIdle");

                        Check(card.ShowCaption && card.TextBands.Caption.Height > 0, "LegacyCardCaptionRemainsAvailable");
                        card.ShowCaption = false; card.Height = S(80);
                        Check(card.TextBands.Caption == Rectangle.Empty, "CompactCardDoesNotPretendCaptionIsVisible");
                        var bands = card.TextBands;
                        Check(card.ClientRectangle.Contains(bands.Title) && card.ClientRectangle.Contains(bands.Value) &&
                            bands.Title.Bottom <= bands.Value.Top, "CompactCardBandsAreContainedAndSeparate");
                        using (var cardGraphics = card.CreateGraphics())
                        using (var titleFont = Theme.Font(StatCard.TitleFontSize, FontStyle.Bold))
                        using (var valueFont = Theme.Font(card.ValueFontSize(cardGraphics), FontStyle.Bold))
                        {
                            var title = Measure(cardGraphics, card.Title, titleFont);
                            var value = Measure(cardGraphics, card.Value, valueFont);
                            Check(title.Width <= bands.Title.Width && title.Height <= bands.Title.Height, "CompactCardTitleFontFits");
                            Check(value.Width <= bands.Value.Width && value.Height <= bands.Value.Height, "CompactCardValueAndUnitFit");
                        }
                        Check(input.Parent == frame && input.Width > 0 && input.Height >= input.Font.GetHeight() &&
                            frame.ClientRectangle.Contains(input.Bounds), "NativeInputStillFitsItsFont");
                        foreach (int logicalWidth in new[] { 204, 220 })
                        {
                            captioned.Width = S(logicalWidth); captioned.PerformLayout(); captioned.Refresh();
                            int prefixWidth = (int)typeof(InputFrame).GetField("captionWidth", Hidden)!.GetValue(captioned)!;
                            int prefixLeft = input.Left;
                            using (var context = captioned.CreateGraphics())
                            {
                                var prefix = TextRenderer.MeasureText(context, "扫描策略", strategy.Font,
                                    new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                                var value = TextRenderer.MeasureText(context, strategy.Text, strategy.Font,
                                    new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                                Check(prefixWidth >= prefix.Width && prefix.Height <= captioned.ClientSize.Height &&
                                    prefixLeft + prefixWidth < strategy.Left, "CaptionAndNativeValueHaveSeparateVisibleAreas");
                                int nativeChrome = SystemInformation.VerticalScrollBarWidth + 2 * SystemInformation.Border3DSize.Width;
                                Check(captioned.ClientRectangle.Contains(strategy.Bounds) && strategy.Width - nativeChrome >= value.Width &&
                                    strategy.Height >= strategy.Font.GetHeight(context), "CaptionedInputFitsLongestRealValueAtMinimumWidths");
                            }
                            using var painted = new Bitmap(captioned.Width, captioned.Height);
                            captioned.DrawToBitmap(painted, captioned.ClientRectangle);
                            bool ink = false;
                            for (int y = Math.Max(0, strategy.Top); !ink && y < Math.Min(painted.Height, strategy.Bottom); y++)
                            for (int x = prefixLeft; !ink && x < prefixLeft + prefixWidth; x++)
                            {
                                var pixel = painted.GetPixel(x, y);
                                ink = Math.Abs(pixel.R - strategy.ForeColor.R) + Math.Abs(pixel.G - strategy.ForeColor.G) +
                                    Math.Abs(pixel.B - strategy.ForeColor.B) < 40;
                            }
                            Check(ink, "CaptionActuallyPaintsInReservedPrefixArea");
                        }
                        StartPress(); button.Dispose();
                        Check(!button.IsMotionRunning && Timer(button) == null, "DisposeReleasesMotionTimer");

                        void StartPress()
                        {
                            Invoke(button, "OnMouseEnter", EventArgs.Empty);
                            Invoke(button, "OnMouseDown", Mouse());
                        }
                    }
                    catch (Failure error) { failure = error.Code; }
                    catch { failure = "UnexpectedStateFailure"; }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (Failure error) { failure = error.Code; }
            catch { failure = "UnexpectedUiFailure"; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) failure = "UiWorkerStillActive";
        if (failure != null)
        {
            Console.Error.WriteLine("FAIL action button state code=" + failure + "; no private details emitted");
            return 1;
        }
        Console.WriteLine("PASS action button state checks=" + passed + "; owned controls, no scan or performance claim");
        return 0;

        void Check(bool condition, string code) { if (!condition) throw new Failure(code); passed++; }
        async Task Settled(ActionButton control)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (control.IsMotionRunning)
            {
                if (DateTime.UtcNow >= deadline) throw new Failure("BoundedMotionDidNotSettle");
                await Task.Delay(16);
            }
        }
        void CheckSubtleBackground(ActionButton control)
        {
            if (SystemInformation.HighContrast) return;
            using var actual = new Bitmap(control.Width, control.Height);
            using var expected = new Bitmap(control.Width, control.Height);
            control.DrawToBitmap(actual, control.ClientRectangle);
            using (var context = Graphics.FromImage(expected)) Wallpaper.Paint(control, context, 42, WallpaperSurface.Chrome);
            int inset = Math.Max(4, (int)(6 * control.DeviceDpi / 96f)), y = control.Height / 2;
            var fill = actual.GetPixel(inset, y); var surface = expected.GetPixel(inset, y);
            Check(Difference(fill, surface) > 2 && Theme.Luminance(fill) > .65 && fill.R >= fill.B,
                "SubtleButtonHasDistinctLightWarmFill");
            double foregroundLuminance = Theme.Luminance(control.Enabled ? Theme.Text : Theme.Muted), fillLuminance = Theme.Luminance(fill);
            Check((Math.Max(foregroundLuminance, fillLuminance) + .05) / (Math.Min(foregroundLuminance, fillLuminance) + .05) >= 4.5,
                "SubtleButtonFillKeepsReadableText");
            int reach = Math.Max(3, (int)Math.Ceiling(3 * control.DeviceDpi / 96f));
            Check(HasEdge(i => new Point(i, y)) && HasEdge(i => new Point(control.Width - 1 - i, y)) &&
                HasEdge(i => new Point(control.Width / 2, i)) &&
                HasEdge(i => new Point(control.Width / 2, control.Height - 1 - i)), "SubtleButtonHasCompleteFourSideBorder");

            bool HasEdge(Func<int, Point> point)
            {
                for (int i = 0; i <= reach; i++)
                {
                    Point location = point(i);
                    if (Difference(actual.GetPixel(location.X, location.Y), Theme.Border) <= 45) return true;
                }
                return false;
            }
            static int Difference(Color first, Color second) => Math.Abs(first.R - second.R) +
                Math.Abs(first.G - second.G) + Math.Abs(first.B - second.B);
        }
    }

    private static MouseEventArgs Mouse() => new(MouseButtons.Left, 1, 8, 8, 0);
    private static RectangleF? RegionBounds(ActionButton button)
    {
        using var graphics = button.CreateGraphics();
        return button.Region?.GetBounds(graphics);
    }
    private static Size Measure(Graphics graphics, string text, Font font) => TextRenderer.MeasureText(graphics, text, font,
        new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Timer(ActionButton button) => typeof(ActionButton).GetField("motionTimer", Hidden)!.GetValue(button);
    private static void Invoke(ActionButton button, string method, object argument) => typeof(ActionButton).GetMethod(method, Hidden)!.Invoke(button, [argument]);
    private static void ShowKeyboardCues(ActionButton button)
    {
        // Clear HIDEFOCUS on this owned control only; this is not a global
        // keyboard-cue or accessibility setting change.
        object[] message = [Message.Create(button.Handle, 0x0128, new IntPtr(0x00010002), IntPtr.Zero)];
        typeof(Control).GetMethod("WndProc", Hidden)!.Invoke(button, message);
    }
    private sealed class Failure(string code) : Exception("ActionButtonState") { internal string Code { get; } = code; }
}
