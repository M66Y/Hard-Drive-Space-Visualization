using System.Text.Json;

namespace DiskAtlas;

internal static class NtfsProductTimingTests
{
    internal static int Run()
    {
        int checks = 0; string? failure = null;
        string directory = Path.Combine(Path.GetTempPath(), "AsukaLens-product-timing-" + Guid.NewGuid().ToString("N"));
        var original = Console.Out;
        try
        {
            Check(NtfsPersonalTimingDriver.ValidUiInvocation([NtfsPersonalTimingDriver.UiArgument]), "ExplicitUiVerb");
            Check(!NtfsPersonalTimingDriver.ValidUiInvocation([NtfsPersonalTimingDriver.UiArgument, @"B:\private"]), "ScopeNeverInArguments");
            Check(!NtfsPersonalTimingDriver.ValidUiInvocation([NtfsPersonalTimingDriver.Argument]), "HeadlessEntryDistinct");
            double now = 0;
            var timeline = new NtfsTimingTimeline(() => now);
            timeline.Mark("UiStart"); now = 2; timeline.Mark("Bridge_LaunchStarting"); now = 7; timeline.Mark("Bridge_LaunchReturned");
            now = 10; timeline.Mark("Fast_Snapshot"); now = 20; timeline.Mark("Fast_Cleanup"); now = 28; timeline.Mark("UiHandoff"); now = 31; timeline.Finish();
            Check(timeline.Intervals.Values.Sum() == 31, "DisjointIntervalsCoverFullWindow");
            Check(timeline.Intervals["Bridge_LaunchStarting"] == 5, "ConsentAndStartupWindowSeparate");
            Check(timeline.Intervals["Fast_Cleanup"] == 8, "CleanupTransferCombinedAndNotLost");
            Check(timeline.Intervals["UiHandoff"] == 3, "FinalQueryAndPaintIncluded");
            int transitions = timeline.Transitions.Count; timeline.Finish();
            Check(timeline.Intervals.Values.Sum() == 31 && timeline.Transitions.Count == transitions, "FinishIdempotent");
            foreach (var stage in Enum.GetNames<FastScanWireStage>()) Check(NtfsTimingTimeline.Allowed("Fast_" + stage), "FixedWorkerPhaseAllowed");
            Check(!NtfsTimingTimeline.Allowed(directory) && !NtfsTimingTimeline.Allowed("Fast_999"), "NoPrivateOrUndefinedPhaseNames");
            bool invalid = false; try { timeline.Mark(directory); } catch (InvalidDataException) { invalid = true; }
            Check(invalid, "InvalidTimelineInputRejected");
            now = 0; var bounded = new NtfsTimingTimeline(() => now);
            for (int i = 0; i < 200; i++) { bounded.Mark(i % 2 == 0 ? "UiStart" : "UiHandoff"); now++; }
            bounded.Finish();
            Check(bounded.Transitions.Count == 128 && bounded.DroppedTransitions == 72, "TransitionDetailsBoundedWithoutFailingScan");
            Check(bounded.Intervals.Values.Sum() == 200, "AllDurationsRetainedAfterDetailLimit");
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                using (var borrowed = new NtfsTimingResources(process)) { borrowed.Start(); borrowed.Stop(); borrowed.Stop(); }
                Check(!process.HasExited, "SamplerDoesNotDisposeBorrowedHelperHandle");
            }
            Directory.CreateDirectory(directory);
            for (int i = 0; i < 8; i++) File.WriteAllText(Path.Combine(directory, $"synthetic-{i}.txt"), "Owned test data");
            string? previous = null;
            // Two calls prove that UI initialization and resource ownership are
            // reusable within the same FO/OF process, without accessing a volume.
            for (int pass = 0; pass < 2; pass++)
            {
                using var output = new StringWriter(); Console.SetOut(output);
                var value = NtfsPersonalTimingDriver.RunVisibleOne(directory, 'O', default, _ => { });
                Console.SetOut(original);
                string text = output.ToString();
                Check(!text.Contains(directory) && !text.Contains("synthetic-"), "PublicOutputContainsNoScopeOrFileName");
                var sampleLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single(x => x.Contains("\"kind\":\"sample\""));
                using var sample = JsonDocument.Parse(sampleLine);
                var sampleRoot = sample.RootElement;
                Check(sampleRoot.GetProperty("ui_drawing_included").GetBoolean() && sampleRoot.GetProperty("synchronous_final_paint_included").GetBoolean(), "RealMainFormPublicationIncluded");
                Check(!sampleRoot.GetProperty("dwm_presentation_measured").GetBoolean() && !sampleRoot.GetProperty("human_wait_separately_measurable").GetBoolean(), "MeasurementLimitsExplicit");
                double total = sampleRoot.GetProperty("total_ms").GetDouble();
                double intervals = sampleRoot.GetProperty("intervals").EnumerateObject().Sum(x => x.Value.GetDouble());
                Check(Math.Abs(intervals - total) < 1, "ActualUiIntervalsAccountForMeasuredWindow");
                Check(sampleRoot.GetProperty("ui_handoff_query_and_final_paint_ms").GetDouble() >= 0, "PostScanUiTimeMeasured");
                Check(sampleRoot.GetProperty("ui_resources").GetProperty("samples").GetInt32() >= 1, "OwnedHostResourcesSampled");
                string scopeMarker = JsonSerializer.Serialize(new { matched = value.Compare(value).AllEqual });
                if (previous != null) Check(previous == scopeMarker, "SecondUiSessionWorksInSameProcess");
                previous = scopeMarker;
            }
            bool propagated = false;
            using (var output = new StringWriter())
            {
                Console.SetOut(output);
                try { NtfsPersonalTimingDriver.RunVisibleOne(directory, 'O', default, _ => { }, (_, _, _, _) =>
                    throw new FastScanUnavailableException(FastScanWireError.CleanupUnconfirmed, false)); }
                catch (FastScanUnavailableException error) { propagated = !error.CleanupConfirmed; }
                finally { Console.SetOut(original); }
            }
            Check(propagated, "UiDoesNotHideUnknownCleanupOrRetryScan");
        }
        catch (Exception error) { failure = error is TestFailure f ? f.Code : error.GetType().Name; }
        finally
        {
            Console.SetOut(original);
            string absolute = Path.GetFullPath(directory);
            if (Directory.Exists(absolute) && Path.GetDirectoryName(absolute) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
                Path.GetFileName(absolute).StartsWith("AsukaLens-product-timing-") && !Directory.EnumerateFileSystemEntries(absolute).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
                Directory.Delete(absolute, true);
        }
        if (failure != null) { Console.Error.WriteLine($"FAIL product timing code={failure}; checks={checks}"); return 1; }
        Console.WriteLine($"PASS product timing checks={checks}; owned_temp_and_synthetic_only=True"); return 0;
        void Check(bool passed, string code) { checks++; if (!passed) throw new TestFailure(code); }
    }
    private sealed class TestFailure(string code) : Exception { internal string Code => code; }
}
