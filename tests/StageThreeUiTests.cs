using DiskAtlas;
using System.Reflection;

internal static class StageThreeUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static int Run(bool dpi96)
    {
        string? failure = null; int checks = 0;
        string directory = Path.Combine(Path.GetTempPath(), "AsukaLens-stage3-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string backgroundPath = Path.Combine(directory, "background.dat");
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                int scanBehavior = 0;
                using var form = new MainForm(null, false, backgroundPath, compositionCandidate: true, showCandidateLabel: false)
                {
                    ShowInTaskbar = false,
                    ScanRunner = (path, progress, token, session) =>
                    {
                        if (scanBehavior == 1) throw new OperationCanceledException(token);
                        var next = StageThreeModelTests.Fixture();
                        return new ScanResult(new DiskEntry(path, true) { Size = next.Size, FileCount = next.FileCount }, TimeSpan.Zero, 0, []);
                    }
                };
                DisplayPreferences? committed = null;
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        var root = StageThreeModelTests.Fixture();
                        Set("scan", new ScanResult(root, TimeSpan.FromMilliseconds(5), 0, [])); Set("current", root);
                        Invoke("SetMode", 1); await Query(false);
                        var grid = Field<WallpaperGrid>("grid"); var map = Field<TreemapControl>("map");
                        Check(Field<ActionButton>("largestFilter").Visible && Rows().Count == 500, "LargestFilterEntryAndTop500Visible");
                        Check(Field<Label>("listCaption").Text.Contains("1,103"), "VisibleFullMatchCount");
                        var kept = Rows().First(x => x.Entry!.Extension == ".zip");
                        Invoke("SelectItem", kept, true);
                        Set("largestMinimum", 3000L); Set("typeFilter", ".zip"); Set("largestExtension", ".zip"); await Query(true);
                        Check(Rows().Count == 50 && Rows().All(x => x.Entry!.Size >= 3000 && x.Entry.Extension == ".zip"), "CombinedLargestFiltersApplied");
                        Check(ReferenceEquals(Field<MapItem>("selection").Entry, kept.Entry), "ValidSelectionPreserved");
                        Check(Field<StatCard>("totalCard").Value == SizeFormat.Bytes(root.Size), "FilteringDoesNotReplaceDirectoryStatistic");
                        Invoke("SetMode", 0); await Query(false);
                        Check(Rows().Count == root.Children.Count && Field<string?>("typeFilter") == null, "FolderViewUnaffectedByLargestConditions");
                        Invoke("SetMode", 1); await Query(false);
                        Check(Rows().Count == 50 && Field<long>("largestMinimum") == 3000, "LargestConditionsRestoreWithinSession");
                        Set("largestMinimum", long.MaxValue); await Query(true);
                        Check(Rows().Count == 0 && Field<MapItem?>("selection") == null && !Field<ActionButton>("selectionDetails").Enabled, "EmptyFilterClearsStaleDetails");
                        Set("largestMinimum", 0L); Set("typeFilter", null); Set("largestExtension", null); Task stale = Query(false);
                        Set("largestMinimum", long.MaxValue); Task latest = Query(false); await Task.WhenAll(stale, latest);
                        Check(Rows().Count == 0, "LatestLargestFilterWins");
                        Set("largestMinimum", 0L); await Query(false);
                        var selected = Rows()[0]; Invoke("SelectItem", selected, true);
                        Image? originalImage = form.BackgroundImage; var crop = form.BackgroundCrop;
                        var originalRaster = form.WallpaperRaster.Get(originalImage, form.ClientSize, crop);
                        var font = grid.DefaultCellStyle.Font; int comfortable = grid.RowTemplate.Height;
                        using var before = PaintRoot();
                        form.ApplyDisplayOptions(new DisplayPreferences(60, CategoryPalette.Clearer, ReadingDensity.Compact));
                        await Task.Delay(180);
                        using var after = PaintRoot();
                        Check(ReferenceEquals(originalImage, form.BackgroundImage) && crop == form.BackgroundCrop &&
                            ReferenceEquals(originalRaster, form.WallpaperRaster.Get(originalImage, form.ClientSize, crop)), "DisplayKeepsImageCropAndRawRaster");
                        Check(!Same(before, after), "BackgroundStrengthChangesActualComposite");
                        Check(grid.RowTemplate.Height < comfortable && ReferenceEquals(font, grid.DefaultCellStyle.Font), "CompactKeepsFontAndReducesRows");
                        var bands = FileListPresentation.Bands(new Rectangle(0, 0, 400, grid.RowTemplate.Height), grid.DeviceDpi, true);
                        Check(bands.Name.Height > 0 && bands.Path.Height > 0 && bands.Path.Bottom <= grid.RowTemplate.Height, "CompactKeepsBothNameAndPathLines");
                        Check(ReferenceEquals(Field<MapItem>("selection").Entry, selected.Entry), "DisplayKeepsObjectSelection");
                        Check(form.DisplayOptions.CategoryColor("视频") != Theme.CategoryColor("视频") && map.AnimationsEnabled,
                            "PaletteChangesCategoriesWithoutDisablingMotion");
                        form.ApplyDisplayOptions(DisplayPreferences.Default); await Task.Delay(140);
                        using var restored = PaintRoot();
                        Check(Same(before, restored) && grid.RowTemplate.Height == comfortable, "DisplayDefaultsExactlyRestoreRootAndRowHeight");
                        await Appearance(false, new(65, CategoryPalette.Clearer, ReadingDensity.Compact));
                        Check(form.DisplayOptions == DisplayPreferences.Default && !File.Exists(backgroundPath + ".display.json"), "CancelRestoresLivePreviewWithoutSaving");
                        committed = new(80, CategoryPalette.Clearer, ReadingDensity.Comfortable);
                        await Appearance(true, committed);
                        Check(form.DisplayOptions == committed && DisplayPreferencesStore.Load(backgroundPath + ".display.json") == committed, "ApplyPersistsDisplayAndFinalPreview");
                        Check(ReferenceEquals(originalImage, form.BackgroundImage) && !File.Exists(backgroundPath), "DisplayOnlyApplyDoesNotSaveOrReplaceBackground");
                        byte[] prior = File.ReadAllBytes(backgroundPath + ".display.json");
                        string blocked = Path.Combine(directory, "blocked"); File.WriteAllText(blocked, "synthetic");
                        Set("displaySettingsPath", Path.Combine(blocked, "display.json"));
                        bool sawFailure = await Appearance(true, new(45, CategoryPalette.Original, ReadingDensity.Compact));
                        Check(sawFailure && form.DisplayOptions == committed && File.ReadAllBytes(backgroundPath + ".display.json").SequenceEqual(prior),
                            "SaveFailureIsVisibleAndPreservesCommittedDisplay");
                        Check(!File.Exists(backgroundPath), "SaveFailureDoesNotWriteImage");

                        Set("largestMinimum", 3000L); Set("largestExtension", ".zip"); Set("typeFilter", ".zip");
                        await (Task)Invoke("ScanFolder", root.Path)!;
                        Check(Field<long>("largestMinimum") == 3000 && Field<string?>("largestExtension") == ".zip", "SameRootRefreshKeepsLargestConditions");
                        scanBehavior = 1;
                        await (Task)Invoke("ScanFolder", Path.Combine(directory, "new-synthetic-root"))!;
                        Check(Field<long>("largestMinimum") == 3000 && Field<string?>("largestExtension") == ".zip", "CancelledNewRootRestoresLargestConditions");
                        scanBehavior = 0;
                        await (Task)Invoke("ScanFolder", Path.Combine(directory, "new-synthetic-root"))!;
                        Check(Field<long>("largestMinimum") == 0 && Field<string?>("largestExtension") == null && Field<int>("mode") == 0, "SuccessfulNewRootResetsLargestConditions");

                        async Task<bool> Appearance(bool apply, DisplayPreferences value)
                        {
                            using var timer = new System.Windows.Forms.Timer { Interval = 30 };
                            BackgroundDialog? dialog = null; Task? work = null; int ticks = 0; bool failed = false;
                            timer.Tick += (_, _) =>
                            {
                                dialog ??= Application.OpenForms.OfType<BackgroundDialog>().FirstOrDefault();
                                if (dialog == null) return;
                                ticks++;
                                if (ticks == 1) { dialog.SetDisplayDraft(value); return; }
                                if (ticks < 8) return;
                                if (apply && work == null) { work = dialog.ApplyAsync(); return; }
                                if (work != null && !work.IsCompleted) return;
                                failed = ((Label)typeof(BackgroundDialog).GetField("status", Hidden)!.GetValue(dialog)!).Text.Contains("失败");
                                if (!dialog.IsDisposed && dialog.Visible) dialog.DialogResult = DialogResult.Cancel;
                                timer.Stop();
                            };
                            timer.Start(); Invoke("EditBackground"); timer.Stop();
                            if (work != null) await work;
                            return failed;
                        }
                    }
                    catch(Failure e) { failure = e.Code; }
                    catch(Exception e) { failure = "Unexpected_" + (e is TargetInvocationException { InnerException: { } inner } ? inner.GetType().Name : e.GetType().Name); }
                    finally { form.Close(); }
                };
                Application.Run(form);
                if (failure == null && committed != null)
                {
                    using var restart = new MainForm(null, true, backgroundPath, compositionCandidate: true, showCandidateLabel: false) { ShowInTaskbar = false };
                    restart.Show(); Pump(400);
                    Check(restart.DisplayOptions == committed, "RestartLoadsCommittedDisplayPreferences"); restart.Close();
                }
                T Field<T>(string name) => (T)typeof(MainForm).GetField(name, Hidden)!.GetValue(form)!;
                void Set(string name,object? value) => typeof(MainForm).GetField(name, Hidden)!.SetValue(form,value);
                object? Invoke(string name, params object?[] args) => typeof(MainForm).GetMethod(name, Hidden)!.Invoke(form,args);
                Task Query(bool preserve) => (Task)Invoke("UpdateViewAsync", preserve)!;
                List<MapItem> Rows() => Field<List<MapItem>>("sortedRows");
                Bitmap PaintRoot()
                {
                    var bitmap = new Bitmap(70,70); using var g=Graphics.FromImage(bitmap);
                    Wallpaper.Paint(form,g,nativeCopy:false); return bitmap;
                }
            }
            catch(Failure e) { failure=e.Code; }
            catch(Exception e) { failure="Worker_"+e.GetType().Name; }
        }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        bool finished=thread.Join(TimeSpan.FromSeconds(75)); if(!finished)failure="WorkerDeadline";
        if(finished)
        {
            string absolute=Path.GetFullPath(directory);
            if(absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("AsukaLens-stage3-ui-") &&
                !Directory.EnumerateFileSystemEntries(absolute).Any(p=>(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0))Directory.Delete(absolute,true);
        }
        if(failure!=null){Console.Error.WriteLine($"FAIL stage-three UI code={failure}; checks={checks}; worker_finished={finished}");return 1;}
        Console.WriteLine($"PASS stage-three UI checks={checks}; dpi96={dpi96}; isolated_settings=True");return 0;
        void Check(bool ok,string code){checks++;if(!ok)throw new Failure(code);}
    }
    private static bool Same(Bitmap a,Bitmap b)
    {
        for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++)if(a.GetPixel(x,y)!=b.GetPixel(x,y))return false;
        return true;
    }
    private static void Pump(int ms){var clock=System.Diagnostics.Stopwatch.StartNew();while(clock.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(5);}}
    private sealed class Failure(string code):Exception{internal string Code=>code;}
}
