using DiskAtlas;
using System.Security.Cryptography;

internal static class StageThreeModelTests
{
    internal static int Run()
    {
        int checks = 0; string? failure = null;
        string directory = Path.Combine(Path.GetTempPath(), "AsukaLens-stage3-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var root = Fixture();
            var request = new ViewRequest(root, ViewMode.LargestFiles);
            var all = ViewQuery.Build(request, default);
            Check(all.Rows.Count == 500 && all.MatchedCount == 1103, "UnfilteredTop500AndFullMatchCount");
            var zip = ViewQuery.Build(request with { ExtensionFilter = ".ZIP", MinimumLogicalSize = 1000 }, default);
            var expected = root.Descendants().Where(x => !x.IsDirectory && x.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) && x.Size >= 1000)
                .OrderByDescending(x => x.Size).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Take(500).ToArray();
            Check(zip.Rows.Select(x => x.Entry).SequenceEqual(expected), "FilterWholeScopeBeforeTop500");
            Check(zip.MatchedCount == 550 && zip.Rows.Count == 500, "MatchedCountNotTruncatedToDisplayLimit");
            var needle = ViewQuery.Build(request with { Search = "below-top", ExtensionFilter = ".bin", MinimumLogicalSize = 1000 }, default);
            Check(needle.Rows.Count == 1 && needle.Rows[0].Name == "below-top.bin", "FileOutsideOriginalTop500CanBecomeMatch");
            var boundary = ViewQuery.Build(request with { Search = "boundary", MinimumLogicalSize = 1000 }, default);
            Check(boundary.MatchedCount == 1 && boundary.Rows.Single().Entry!.Size == 1000, "MinimumIsInclusiveAndRejectsOneByteBelow");
            var occupied = ViewQuery.Build(request with { Search = "below-top", Metric = SizeMetric.Allocated, MinimumLogicalSize = 1000 }, default);
            Check(occupied.Rows.Count == 1 && occupied.Rows[0].Size == 0 && occupied.Rows[0].UnknownFiles == 1 && occupied.MapItems.Count == 0,
                "LogicalThresholdIndependentOfUnknownAllocatedMetric");
            Check(ViewQuery.Build(request with { MinimumLogicalSize = long.MaxValue }, default).Rows.Count == 0, "ExtremeMinimumProducesEmptyResult");
            Check(ViewQuery.Build(request with { Mode = ViewMode.Folder, MinimumLogicalSize = long.MaxValue }, default).Rows.Count == root.Children.Count, "LargestMinimumDoesNotFilterFolderMode");
            bool cancelled = false; using(var cancellation = new CancellationTokenSource())
            { cancellation.Cancel(); try { ViewQuery.Build(request, cancellation.Token); } catch(OperationCanceledException) { cancelled = true; } }
            Check(cancelled, "FilterHonorsCancellation");
            string display = Path.Combine(directory, "display.json"), background = Path.Combine(directory, "background.dat");
            Check(DisplayPreferencesStore.Load(display) == DisplayPreferences.Default, "MissingPreferencesUseExactExistingDefaults");
            var preference = new DisplayPreferences(65, CategoryPalette.Clearer, ReadingDensity.Compact);
            DisplayPreferencesStore.Save(display, preference);
            Check(DisplayPreferencesStore.Load(display) == preference, "DisplayRoundTrip");
            Check(new FileInfo(display).Length < DisplayPreferencesStore.MaxBytes, "DisplayStorageBounded");
            string content = File.ReadAllText(display);
            Check(!content.Contains(directory) && !content.Contains("path", StringComparison.OrdinalIgnoreCase), "NoPathsOrFileListsPersisted");
            string[] invalid = ["{}", "[]", "{\"version\":2,\"backgroundStrength\":100,\"palette\":\"original\",\"density\":\"comfortable\"}",
                "{\"version\":1,\"backgroundStrength\":39,\"palette\":\"original\",\"density\":\"comfortable\"}",
                "{\"version\":1,\"backgroundStrength\":101,\"palette\":\"original\",\"density\":\"comfortable\"}",
                "{\"version\":1,\"backgroundStrength\":100,\"palette\":\"other\",\"density\":\"comfortable\"}",
                "{\"version\":1,\"backgroundStrength\":100,\"palette\":\"original\",\"density\":\"comfortable\",\"version\":1}", new string('x', 4097)];
            foreach(string bad in invalid)
            {
                File.WriteAllText(display, bad); bool rejected = false;
                try { DisplayPreferencesStore.Load(display); } catch(Exception e) when(DisplayPreferencesStore.IsReadError(e)) { rejected = true; }
                Check(rejected, "InvalidDisplayPreferencesRejected");
            }
            DisplayPreferencesStore.Save(display, preference);
            File.WriteAllBytes(background, [1, 2, 3, 4]);
            byte[] oldImage = File.ReadAllBytes(background), oldDisplay = File.ReadAllBytes(display);
            using var choice = new BackgroundChoice(new Bitmap(2, 2), BackgroundView.Default, Theme.DefaultAccent);
            bool failed = false;
            try { AppearanceSettingsCommit.Save(background, display, choice, false, DisplayPreferences.Default, _ => throw new IOException("SyntheticCommitFailure")); }
            catch(IOException) { failed = true; }
            Check(failed && File.ReadAllBytes(background).SequenceEqual(oldImage) && File.ReadAllBytes(display).SequenceEqual(oldDisplay), "FailedPairCommitRestoresBothPreviousFiles");
            Check(Directory.GetFiles(directory).Length == 2, "FailedCommitCleansStagesAndBackups");
            failed = false;
            try { AppearanceSettingsCommit.Save(background, display, choice, false, DisplayPreferences.Default,
                index => { if (index == 1) throw new IOException("SyntheticSecondCommitFailure"); }); }
            catch (IOException) { failed = true; }
            Check(failed && File.ReadAllBytes(background).SequenceEqual(oldImage) && File.ReadAllBytes(display).SequenceEqual(oldDisplay),
                "SecondCommitFailureRestoresBothPreviousFiles");
            Check(Directory.GetFiles(directory).Length == 2, "SecondFailureCleansStagesAndBackups");
            var stamp = File.GetLastWriteTimeUtc(background);
            AppearanceSettingsCommit.Save(background, display, null, false, DisplayPreferences.Default);
            Check(File.ReadAllBytes(background).SequenceEqual(oldImage) && File.GetLastWriteTimeUtc(background) == stamp, "DisplayOnlyDoesNotRewriteImage");
            Check(DisplayPreferencesStore.Load(display) == DisplayPreferences.Default, "DisplayOnlySaved");
            AppearanceSettingsCommit.Save(background, display, choice, false, preference);
            using(var loaded = BackgroundSettings.Load(background)) Check(loaded != null && loaded.View == BackgroundView.Default, "ImageAndDisplayApplyTogether");
            Check(DisplayPreferencesStore.Load(display) == preference, "PairedDisplaySaved");
            AppearanceSettingsCommit.Save(background, display, null, true, null);
            Check(!File.Exists(background) && DisplayPreferencesStore.Load(display) == preference, "ImageResetDoesNotResetDisplay");
            Check(Theme.Categories.All(c => DisplayPreferences.Default.CategoryColor(c.Name) == c.Color), "OriginalPaletteExactlyRetained");
            Check(Theme.Categories.Any(c => preference.CategoryColor(c.Name) != c.Color), "AlternativePaletteDistinct");
        }
        catch(Failure e) { failure = e.Code; }
        catch(Exception e) { failure = "Unexpected_" + e.GetType().Name; }
        finally
        {
            string absolute = Path.GetFullPath(directory), temp = Path.GetFullPath(Path.GetTempPath());
            if (absolute.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("AsukaLens-stage3-model-", StringComparison.Ordinal) &&
                !Directory.EnumerateFileSystemEntries(absolute).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) Directory.Delete(absolute, true);
        }
        if(failure != null) { Console.Error.WriteLine($"FAIL stage-three model code={failure}; checks={checks}"); return 1; }
        Console.WriteLine($"PASS stage-three model checks={checks}; synthetic_and_owned_temp_only=True"); return 0;
        void Check(bool condition,string code) { checks++; if(!condition)throw new Failure(code); }
    }
    internal static DiskEntry Fixture()
    {
        var root = new DiskEntry(Path.Combine(Path.GetTempPath(), "AsukaLens-stage3-synthetic"), true);
        for(int i=0;i<1100;i++)
            root.MutableChildren.Add(new DiskEntry(Path.Combine(root.Path,$"item-{i:0000}"+(i%2==0?".zip":".mp4")),false,root)
                { Size = 2000+i, AllocatedSize = 4000+i, FileCount = 1 });
        foreach(var pair in new[]{("below-top.bin",1000L),("boundary-low.bin",999L),("boundary-equal.bin",1000L)})
            root.MutableChildren.Add(new DiskEntry(Path.Combine(root.Path,pair.Item1),false,root){Size=pair.Item2,UnknownAllocationFiles=1,FileCount=1});
        root.Size=root.Children.Sum(x=>x.Size);root.FileCount=root.Children.Count;root.AllocatedSize=root.Children.Sum(x=>x.AllocatedSize);root.UnknownAllocationFiles=3;
        return root;
    }
    private sealed class Failure(string code):Exception { internal string Code=>code; }
}
