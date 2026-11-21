using System.Reflection;
using DiskAtlas;

internal static class WallpaperCellClipTests
{
    internal static int Run(bool dpi96 = false)
    {
        int passed = 0;
        string failure = "None";
        var worker = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new MainForm(null, loadSavedBackground: false);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                form.Show(); Application.DoEvents();
                var rows = new List<MapItem> { new("中文-long-name-crosses-the-partial-cell-boundary.txt", 17, null, "文档") };
                typeof(MainForm).GetMethod("PopulateGrid", flags)!.Invoke(form, [rows]);
                var grid = (WallpaperGrid)typeof(MainForm).GetField("grid", flags)!.GetValue(form)!;
                Application.DoEvents();
                var cell = grid.GetCellDisplayRectangle(0, 0, false);
                int scale = Math.Max(1, grid.DeviceDpi / 96);
                var sentinel = Color.FromArgb(19, 47, 71);
                foreach (bool selected in new[] { false, true })
                foreach (int kind in Enumerable.Range(0, 3))
                {
                    var clip = kind switch
                    {
                        0 => new Rectangle(cell.Left + cell.Width / 3, cell.Top, Math.Max(1, cell.Width / 3), cell.Height),
                        1 => new Rectangle(cell.Left, cell.Top + cell.Height / 2, cell.Width, Math.Max(1, cell.Height / 3)),
                        _ => new Rectangle(cell.Left, cell.Top, Math.Max(1, 5 * scale), Math.Max(1, 5 * scale))
                    };
                    clip.Intersect(grid.ClientRectangle);
                    using var image = new Bitmap(grid.Width, grid.Height);
                    image.SetResolution(grid.DeviceDpi, grid.DeviceDpi);
                    using var graphics = Graphics.FromImage(image);
                    graphics.Clear(sentinel);
                    // Native paint exposes the viewport; the cell callback
                    // itself must honour its narrower dirty/cell rectangle.
                    graphics.SetClip(grid.ClientRectangle);
                    var state = selected ? DataGridViewElementStates.Selected : DataGridViewElementStates.None;
                    var args = new DataGridViewCellPaintingEventArgs(grid, graphics, clip, cell, 0, 0, state,
                        rows[0].Name, rows[0].Name, "", grid.Rows[0].Cells[0].InheritedStyle,
                        grid.AdvancedCellBorderStyle, DataGridViewPaintParts.All);
                    typeof(WallpaperGrid).GetMethod("OnCellPainting", flags)!.Invoke(grid, [args]);
                    if (!args.Handled) throw new InvalidOperationException("NameCallbackNotUsed");
                    for (int y = cell.Top; y < cell.Bottom; y++)
                    for (int x = cell.Left; x < cell.Right; x++)
                        if (!clip.Contains(x, y) && image.GetPixel(x, y).ToArgb() != sentinel.ToArgb())
                            throw new InvalidOperationException("NameOutsideDirtyClip");
                    passed++;
                }
                form.Close();
            }
            catch (Exception error)
            {
                var actual = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
                failure = actual.Message is "NameCallbackNotUsed" or "NameOutsideDirtyClip" ? actual.Message : "OwnedFixtureFailure";
            }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(20))) failure = "WorkerTimeout";
        Console.WriteLine((failure == "None" ? "PASS" : "FAIL") + " wallpaper name clipping checks=" + passed + "; code=" + failure);
        return failure == "None" ? 0 : 1;
    }
}
