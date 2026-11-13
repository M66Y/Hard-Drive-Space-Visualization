using DiskAtlas;
using System.Reflection;

// In-process rendering checks on a real MainForm and a nonce-owned directory.
// Bitmaps are assertions only; they are not desktop screenshots or visual sign-off.
internal static class VisualSurfaceRegression
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run(bool virtualized96Dpi = false)
    {
        int passed = 0;
        string failureCode = "None";
        var fixture = new Fixture();
        bool finished = true;
        try
        {
            fixture.Create();
            var thread = new Thread(() =>
            {
                try
                {
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                    Application.SetHighDpiMode(virtualized96Dpi ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    foreach (string variant in new[] { "a", "b" }) CheckForm(variant);
                }
                catch (CheckFailure error) { failureCode = error.Code; }
                catch { failureCode = "UnexpectedUiFailure"; }
                finally { Theme.SetAccent(null); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            finished = thread.Join(TimeSpan.FromSeconds(50));
            if (!finished) failureCode = "UiWorkerStillActive";

            void CheckForm(string variant)
            {
                using var form = new MainForm(null, loadSavedBackground: false,
                    backgroundSettingsPath: Path.Combine(fixture.Settings, "background.dat"));
                form.ApplyStageOnePreview(variant);
                Exception? failure = null;
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        await (Task)Invoke(form, "ScanFolder", fixture.Scope)!;
                        await (Task)Invoke(form, "UpdateViewAsync", false)!;
                        var grid = Field<DataGridView>(form, "grid");
                        Check(grid.RowCount == 3 && grid.Enabled, "OwnedDirectoryScan");
                        grid.ClearSelection(); grid.CurrentCell = null;
                        for (int background = 0; background < 3; background++)
                        {
                            var image = Background(background);
                            try { Invoke(form, "ApplyBackground", image, BackgroundView.Default, Theme.DefaultAccent, true); }
                            catch { image.Dispose(); throw; }
                            // ApplyBackground now owns image and releases the previous one.
                            form.PerformLayout(); form.Refresh();
                            await Task.Delay(30);
                            CheckGridSurface(grid);
                            CheckTextSurfaces(form);
                            CheckReadableGeometry(form);
                        }
                        float scale = form.DeviceDpi / 96f;
                        var workArea = Screen.FromControl(form).WorkingArea;
                        form.Size = new Size(Math.Min((int)(1120 * scale), workArea.Width),
                            Math.Min((int)(780 * scale), workArea.Height));
                        form.PerformLayout(); form.Refresh();
                        await Task.Delay(30);
                        CheckMinimumWidth(form, grid);
                    }
                    catch (Exception error) { failure = error; }
                    finally { form.Close(); }
                };
                Application.Run(form);
                if (failure is CheckFailure checkFailure) throw checkFailure;
                if (failure != null) throw new CheckFailure("UnexpectedRenderFailure");
            }

            void Check(bool value, string code)
            {
                if (!value) throw new CheckFailure(code);
                passed++;
            }

            void CheckGridSurface(DataGridView grid)
            {
                Check(grid.ClientSize.Width > 30 && grid.ClientSize.Height > 30, "GridViewportAvailable");
                var last = grid.GetRowDisplayRectangle(grid.RowCount - 1, true);
                Check(last.Height > 0 && last.Bottom + 8 < grid.ClientSize.Height, "GridBlankRegionAvailable");
                using var pixels = Draw(grid);
                int emptyY = (last.Bottom + grid.ClientSize.Height - 8) / 2;
                foreach (int x in new[] { grid.ClientSize.Width / 4, grid.ClientSize.Width * 3 / 4 })
                    Check(Same(pixels.GetPixel(x, emptyY), grid.BackgroundColor), "GridBlankSurfaceStable");

                var cell = grid.GetCellDisplayRectangle(0, 0, true);
                int inset = Math.Max(3, (int)(3 * grid.DeviceDpi / 96f));
                var actual = pixels.GetPixel(cell.Left + inset, cell.Top + cell.Height / 2);
                Check(Same(actual, grid.Rows[0].InheritedStyle.BackColor), "GridRowSurfaceStable");
            }

            void CheckTextSurfaces(MainForm form)
            {
                foreach (var chrome in Descendants(form).OfType<WallpaperTable>()
                    .Where(control => control.Visible && control.Width > 0 && control.Height > 0 && control.Surface == WallpaperSurface.Chrome))
                {
                    using var finalSurface = new Bitmap(chrome.Width, chrome.Height);
                    using (var graphics = Graphics.FromImage(finalSurface))
                        Wallpaper.Paint(chrome, graphics, chrome.Wash, chrome.Surface);
                    double captionMinimum = double.MaxValue, bodyMinimum = double.MaxValue;
                    for (int y = 0; y < finalSurface.Height; y += Math.Max(1, finalSurface.Height / 7))
                    for (int x = 0; x < finalSurface.Width; x += Math.Max(1, finalSurface.Width / 9))
                    {
                        var background = finalSurface.GetPixel(x, y);
                        captionMinimum = Math.Min(captionMinimum, Contrast(Theme.Muted, background));
                        bodyMinimum = Math.Min(bodyMinimum, Contrast(Theme.Text, background));
                    }
                    Check(captionMinimum >= 4.5 && bodyMinimum >= 4.5, "ChromeCompositedContrast");
                }

                foreach (var card in Descendants(form).OfType<StatCard>().Where(control => control.Visible))
                {
                    using var rendered = Draw(card);
                    float scale = card.DeviceDpi / 96f;
                    int x = Math.Clamp((int)(8 * scale), 1, rendered.Width - 2);
                    double minimum = double.MaxValue;
                    for (int y = Math.Max(2, (int)(16 * scale)); y < rendered.Height - 16 * scale; y += Math.Max(1, (int)(8 * scale)))
                        minimum = Math.Min(minimum, Contrast(Theme.Muted, rendered.GetPixel(x, y)));
                    Check(minimum != double.MaxValue && minimum >= 4.5, "CardCaptionCompositedContrast");
                }
            }

            void CheckReadableGeometry(MainForm form)
            {
                foreach (string name in new[] { "title", "mapTitle", "listTitle" })
                {
                    var label = Field<Label>(form, name);
                    var desired = TextRenderer.MeasureText(label.Text, label.Font, new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    Check(label.ClientSize.Height >= desired.Height && label.ClientSize.Width >= desired.Width, "PrimaryTitleFits");
                }
                foreach (string name in new[] { "subtitle", "mapCaption", "listCaption", "status", "detailTitle", "detailPath" })
                {
                    var label = Field<Label>(form, name);
                    Check(label.ClientSize.Height >= label.Font.GetHeight(), "BodyTextVerticalFit");
                }
                foreach (var input in new Control[] { Field<ComboBox>(form, "strategyCombo"), Field<ComboBox>(form, "metricCombo"),
                    Field<TextBox>(form, "pathBox"), Field<TextBox>(form, "search") })
                {
                    var frame = input.Parent;
                    Check(frame is InputFrame && input.Left > 0 && input.Top > 0 && input.Right < frame.ClientSize.Width &&
                        input.Bottom <= frame.ClientSize.Height && input.Height >= input.Font.GetHeight(), "NativeInputFontFits");
                }
                var grid = Field<DataGridView>(form, "grid");
                float scale = grid.DeviceDpi / 96f;
                var rowFont = grid.Rows[0].InheritedStyle.Font ?? grid.Font;
                var headerFont = grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font;
                Check(grid.Rows[0].Height >= rowFont.GetHeight() + 16 * scale, "ComfortableRowVerticalSpace");
                Check(grid.ColumnHeadersHeight >= headerFont.GetHeight() + 12 * scale, "ReadableHeaderVerticalSpace");
            }

            void CheckMinimumWidth(MainForm form, DataGridView grid)
            {
                Check(grid.Parent != null && grid.Left >= 0 && grid.Right <= grid.Parent.ClientSize.Width + 1,
                    "MinimumGridContainedByParent");
                int scrollbarWidth = grid.Controls.OfType<VScrollBar>().Where(bar => bar.Visible).Sum(bar => bar.Width);
                int viewportRight = grid.ClientSize.Width - scrollbarWidth;
                var columns = grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).ToArray();
                Check(columns.Length == 3, "MinimumDirectoryColumnsAvailable");
                foreach (var column in columns)
                {
                    var bounds = grid.GetColumnDisplayRectangle(column.Index, cutOverflow: false);
                    Check(bounds.Width > 0 && bounds.Left >= 0 && bounds.Right <= viewportRight + 1,
                        "MinimumDirectoryColumnFullyVisible");
                }
                foreach (var card in Descendants(form).OfType<StatCard>().Where(control => control.Visible))
                {
                    string original = card.Value;
                    try
                    {
                        using var graphics = card.CreateGraphics();
                        // The painted value area has the card's 18-DIP inset on
                        // each side; assertions use actual DPI and live width.
                        int inset = (int)(18 * card.DeviceDpi / 96f);
                        int available = card.ClientSize.Width - 2 * inset;
                        foreach (string value in new[] { "27.55 MB", "102.45 GB + 未知", "1,048,576" })
                        {
                            card.Value = value;
                            using var font = Theme.Font(card.ValueFontSize(graphics), FontStyle.Bold);
                            var measured = TextRenderer.MeasureText(graphics, value, font, new Size(int.MaxValue, int.MaxValue),
                                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                            Check(available > 0 && measured.Width <= available, "MinimumCardValueAndUnitFit");
                        }
                    }
                    finally { card.Value = original; }
                }
            }
        }
        catch (CheckFailure error) { failureCode = error.Code; }
        catch { failureCode = "UnexpectedFixtureFailure"; }
        finally
        {
            if (finished)
            {
                try { fixture.Cleanup(); }
                catch { failureCode = "OwnedFixtureCleanupFailed"; }
            }
            Console.WriteLine("CLEANUP visual surface completed=" + (finished && !Directory.Exists(fixture.Root)));
        }
        if (failureCode != "None")
        {
            Console.Error.WriteLine("FAIL visual surface code=" + failureCode + "; no private details emitted");
            return 1;
        }
        Console.WriteLine("PASS visual surface checks=" + passed + "; composited assertions only, desktop review separate");
        return 0;
    }

    private sealed class CheckFailure(string code) : Exception("VisualSurface") { internal string Code { get; } = code; }
    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, Fields)!.GetValue(form)!;
    private static object? Invoke(MainForm form, string name, params object[] args) => typeof(MainForm).GetMethod(name, Fields)!.Invoke(form, args);
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (var child in Descendants(control)) yield return child;
        }
    }
    private static Bitmap Draw(Control control)
    {
        var image = new Bitmap(control.Width, control.Height);
        try { control.DrawToBitmap(image, control.ClientRectangle); return image; }
        catch { image.Dispose(); throw; }
    }
    private static Bitmap Background(int kind)
    {
        var image = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(kind == 0 ? Color.Black : Color.White);
        if (kind == 2)
            for (int y = 0; y < 64; y += 8)
            for (int x = 0; x < 64; x += 8)
                if ((x + y) % 16 == 0) graphics.FillRectangle(Brushes.Black, x, y, 8, 8);
        return image;
    }
    private static bool Same(Color left, Color right) => Math.Abs(left.R - right.R) <= 1 && Math.Abs(left.G - right.G) <= 1 && Math.Abs(left.B - right.B) <= 1;
    private static double Contrast(Color foreground, Color background)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte value) => value / 255d <= .04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + .055) / 1.055, 2.4);
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        double a = Luminance(foreground), b = Luminance(background);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private sealed class Fixture
    {
        private readonly string nonce = Guid.NewGuid().ToString("N");
        private readonly string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private bool owned;
        internal string Root => Path.Combine(temp, "AsukaLens-visual-surface-" + nonce);
        internal string Scope => Path.Combine(Root, "scope");
        internal string Settings => Path.Combine(Root, "settings");
        internal void Create()
        {
            if (Directory.Exists(Root) || File.Exists(Root)) throw new CheckFailure("FixtureCollision");
            Directory.CreateDirectory(Root); owned = true;
            File.WriteAllText(Path.Combine(Root, ".owner"), nonce);
            Directory.CreateDirectory(Scope); Directory.CreateDirectory(Settings);
            for (int index = 0; index < 3; index++) File.WriteAllBytes(Path.Combine(Scope, "f" + index + ".bin"), new byte[31 + index]);
        }
        internal void Cleanup()
        {
            if (!owned) return;
            string full = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(full) != temp || Path.GetFileName(full) != "AsukaLens-visual-surface-" + nonce ||
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(Path.Combine(full, ".owner")) != nonce)
                throw new CheckFailure("FixtureOwnership");
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".owner", "scope", "settings", "scope/f0.bin", "scope/f1.bin", "scope/f2.bin" };
            var pending = new Stack<string>(); pending.Push(full);
            while (pending.TryPop(out string? directory))
            foreach (string item in Directory.EnumerateFileSystemEntries(directory))
            {
                string relative = Path.GetRelativePath(full, item).Replace('\\', '/');
                var attributes = File.GetAttributes(item);
                if (!allowed.Remove(relative) || (attributes & FileAttributes.ReparsePoint) != 0) throw new CheckFailure("FixtureChanged");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(item);
            }
            for (int index = 0; index < 3; index++) File.Delete(Path.Combine(Scope, "f" + index + ".bin"));
            Directory.Delete(Scope); Directory.Delete(Settings); File.Delete(Path.Combine(full, ".owner")); Directory.Delete(full);
        }
    }
}
