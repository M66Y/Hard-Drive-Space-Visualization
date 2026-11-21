using DiskAtlas;
using System.Reflection;
using System.Diagnostics;

internal static class DibMainFormTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static int Run(bool dpi96, bool cacheOnly = false)
    {
        int checks = 0; string? failure = null; Action? close = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                using var form = new MainForm(null, dibCandidate: true);
                close = () => { try { if(form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke((Action)(() => form.Close())); } catch(InvalidOperationException) { } };
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        await Task.Delay(80);
                        if (cacheOnly)
                        {
                            typeof(UiRegression).GetMethod("CheckRenderCache", BindingFlags.NonPublic | BindingFlags.Static)!
                                .Invoke(null, [form, (Action<bool, string>)Check, CancellationToken.None]);
                            return;
                        }
                        var session = form.Rendering!;
                        Check(session.Active && session.Bytes > 0 && session.Copies > 0, "DibActiveAndUsed");
                        var grid = Field<WallpaperGrid>(form, "grid");
                        var map = Field<TreemapControl>(form, "map");
                        var rows = Enumerable.Range(0, 150).Select(i => new MapItem("合成测试 " + i, 1000 + i, null, "文档")).ToList();
                        typeof(MainForm).GetMethod("PopulateGrid", Hidden)!.Invoke(form, [rows]);
                        map.SetItems(rows);
                        await Task.Delay(60);
                        form.Refresh();
                        Check(Field<PixelSurface>(map, "staticPixels").Native && Field<PixelSurface>(grid, "backdropPixels").Native,
                            "RealMapAndGridShareNativeStorage");
                        using (var reference = new MainForm(null))
                        {
                            reference.Size = form.Size; reference.Show();
                            var referenceMap = Field<TreemapControl>(reference, "map");
                            referenceMap.SetItems(rows);
                            typeof(MainForm).GetMethod("PopulateGrid", Hidden)!.Invoke(reference, [rows]);
                            Field<CheckBox>(reference,"reduceMotion").Checked = true;
                            Field<CheckBox>(form,"reduceMotion").Checked = true;
                            reference.Refresh(); form.Refresh();
                            foreach (var pair in new[] { (Old:(Control)referenceMap, New:(Control)map),
                                (Old:(Control)Field<WallpaperGrid>(reference,"grid"), New:(Control)grid) })
                            {
                                using var a = new Bitmap(pair.Old.Width, pair.Old.Height);
                                using var b = new Bitmap(pair.New.Width, pair.New.Height);
                                pair.Old.DrawToBitmap(a,pair.Old.ClientRectangle);
                                pair.New.DrawToBitmap(b,pair.New.ClientRectangle);
                                Check(a.Size == b.Size, "DibDoesNotChangeSurfaceGeometry");
                                long differences = 0;
                                for(int y=0;y<a.Height;y++) for(int x=0;x<a.Width;x++)
                                    if(a.GetPixel(x,y).ToArgb()!=b.GetPixel(x,y).ToArgb()) differences++;
                                Console.WriteLine($"DIB_SURFACE type={pair.New.GetType().Name}; compared={a.Width*a.Height}; different={differences}");
                                Check(differences == 0,"DibMatchesExistingSurfacePixels");
                            }
                            reference.Close();
                        }
                        for (int i = 0; i < 12; i++)
                        {
                            form.Size = i % 2 == 0 ? new Size(1550, 980) : form.MinimumSize;
                            await Task.Delay(40);
                            Check(session.Active && session.Bytes <= RenderSession.MaxBytes, "ResizeKeepsBudgetAndNativePath");
                        }
                        grid.FirstDisplayedScrollingRowIndex = 30;
                        var coverage = Field<DibWindowCoverage>(form,"dibCoverage");
                        Check(coverage.EarlyCopies > 0 && coverage.ExposedCopies > 0,"NativeClientCoverageActuallyExecuted");
                        await Task.Delay(50);
                        Check(grid.FirstDisplayedScrollingRowIndex == 30, "NativeScrollPreserved");
                        foreach(var combo in new[]{Field<ComboBox>(form,"strategyCombo"),Field<ComboBox>(form,"metricCombo")})
                        { combo.DroppedDown = true; await Task.Delay(30); combo.DroppedDown = false; }
                        var old = form.BackgroundImage;
                        var image = new Bitmap(180, 120); using(var g = Graphics.FromImage(image)) g.Clear(Color.SteelBlue);
                        form.BackgroundImage = image; old?.Dispose(); form.Invalidate(true); form.Refresh();
                        Check(ReferenceEquals(form.WallpaperRaster.Get(image, form.ClientSize), form.WallpaperRaster.Get(image, form.ClientSize)), "NewBackgroundCacheReused");
                        form.WindowState = FormWindowState.Minimized; await Task.Delay(80);
                        form.WindowState = FormWindowState.Normal; await Task.Delay(100);
                        Check(session.Active && session.Copies > 0, "RestoreRetainsCandidate");
                        using(var rejected = PixelSurface.Create(new Size(32768,32768),96,session))
                            Check(rejected == null && session.Bytes <= RenderSession.MaxBytes, "BudgetRejectsBeforeAllocation");
                        var fallback = new RenderSession(true); fallback.Failed(); fallback.Failed(); fallback.Failed();
                        using(var surface=PixelSurface.Create(new Size(32,32),96,fallback))
                            Check(surface != null && !surface.Native && !fallback.Active, "RepeatedFailureUsesBitmap");
                        Check(fallback.Bytes == 0, "FallbackStorageReleased");
                        form.Dispose(); Check(session.Bytes == 0, "MainFormReleasesAllOwnedPixels");
                        Console.WriteLine($"DIB_INTEGRATION copies={session.Copies}; fallback={session.Fallbacks}; peak_pixel_bytes={session.PeakBytes}");
                        foreach (var pair in session.CopyFailureCounts) Console.WriteLine($"DIB_FALLBACK code={pair.Key}; count={pair.Value}");
                    }
                    catch(Exception error) { failure = error is Failure known ? known.Code : error is TargetInvocationException {InnerException: Failure inner} ? inner.Code : "UnexpectedIntegration"; }
                    finally { if(!form.IsDisposed)form.Close(); }
                };
                Application.Run(form);
            }
            catch { failure = "DibWorkerFailed"; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(45))) { close?.Invoke(); thread.Join(TimeSpan.FromSeconds(3)); Console.WriteLine("FAIL DIB integration deadline"); return 1; }
        Console.WriteLine($"{(failure == null ? "PASS" : "FAIL")} DIB integration checks={checks}; code={failure ?? "None"}; screen_acceptance=separate");
        return failure == null ? 0 : 1;
        void Check(bool condition,string code) { if(!condition)throw new Failure(code); checks++; }
    }
    private static T Field<T>(object owner,string name) => (T)owner.GetType().GetField(name,Hidden)!.GetValue(owner)!;
    private sealed class Failure(string code) : Exception { internal string Code { get; } = code; }
}
