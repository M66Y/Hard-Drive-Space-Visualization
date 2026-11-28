using System.Text.Json;
using DiskAtlas.NtfsPrototype;

namespace DiskAtlas;

// In-memory samples only. The clock, object reasons and binary reports are synthetic.
internal static class NtfsScanDiagnosticsTests
{
    private const int ObjectLimit = 1_048_576;
    private const string ErrorCode = "ScanDiagnosticInvalid";

    internal static int Run()
    {
        int checks = 0;
        void Check(bool passed, string label)
        {
            checks++;
            if (!passed) throw new InvalidOperationException(label);
        }
        void Reject(Action action, string label)
        {
            try { action(); }
            catch (InvalidDataException ex) when (ex.Message == ErrorCode) { Check(true, label); return; }
            Check(false, label);
        }

        try
        {
            double now = 10;
            var model = new NtfsScanDiagnostics(() => now);
            var empty = model.Snapshot();
            Check(empty.PhaseMilliseconds.Length == 5 && empty.PhaseMilliseconds.All(value => value is null), "absent phases");
            Check(empty.ReasonCombinations.Length == 128 && empty.ReasonCombinations.All(n => n == 0)
                && empty.SupplementObjects == 0, "absent supplements");
            empty.Validate();
            Reject(() => empty.Validate(requireComplete: true), "complete report requires every phase");
            Reject(() => Encode(empty), "partial report cannot be encoded");
            Reject(() => NtfsPersonalTimingDriver.DiagnosticJson(new([0, 0, null, 0, 0], 0, new int[128])),
                "details JSON requires every phase");
            Reject(() => model.Measure((NtfsDiagnosticPhase)99), "undefined phase");

            var phases = new[] { NtfsDiagnosticPhase.SourceValidation, NtfsDiagnosticPhase.VssSetup, NtfsDiagnosticPhase.VssCreation,
                NtfsDiagnosticPhase.SnapshotValidation, NtfsDiagnosticPhase.IndexFinalization };
            for (int i = 0; i < phases.Length; i++)
            {
                var phase = phases[i];
                var measuring = model.Measure(phase);
                Check(model.Snapshot().PhaseMilliseconds[(int)phase] is null, "active phase remains unreported");
                Reject(() => model.Measure(phase), "overlapping measurement");
                now += i * 0.5;
                measuring.Dispose();
                now += 10;
                measuring.Dispose();
                Check(model.Snapshot().PhaseMilliseconds[(int)phase] == i * 0.5, "zero and positive durations captured once");
                Reject(() => model.Measure(phase), "completed phase cannot be replaced");
            }

            foreach (double invalidEnd in new[] { 5d, double.NaN, double.PositiveInfinity })
            {
                now = 10;
                var timer = new NtfsScanDiagnostics(() => now).Measure(NtfsDiagnosticPhase.VssCreation);
                now = invalidEnd;
                Reject(timer.Dispose, "invalid clock on completion");
            }
            var invalidStart = new NtfsScanDiagnostics(() => double.NaN);
            Reject(() => invalidStart.Measure(NtfsDiagnosticPhase.VssCreation), "invalid clock on entry");

            // Each object contributes to exactly one combination even with multiple reasons.
            for (int mask = 1; mask < 128; mask++) model.RecordSupplement((NtfsSupplementReason)mask);
            model.RecordSupplement((NtfsSupplementReason)3);
            foreach (int invalid in new[] { 0, 128, 129 })
                Reject(() => model.RecordSupplement((NtfsSupplementReason)invalid), "invalid reason mask");
            var full = model.Snapshot();
            full.Validate(requireComplete: true);
            CheckDetailedOutput(full, Check);
            Check(full.SupplementObjects == 128 && full.ReasonCombinations[0] == 0 && full.ReasonCombinations[3] == 2,
                "overlapping categories form one bucket per object");
            Check(Enumerable.Range(1, 127).All(mask => full.ReasonCombinations[mask] == (mask == 3 ? 2 : 1)),
                "all seven reason bits preserved");

            var data = Encode(full);
            var restored = Decode(data);
            Check(restored.PhaseMilliseconds.SequenceEqual(full.PhaseMilliseconds)
                && restored.SupplementObjects == full.SupplementObjects && restored.ReasonCombinations.SequenceEqual(full.ReasonCombinations),
                "complete binary round trip");
            full.PhaseMilliseconds[0] = 500;
            full.ReasonCombinations[1] = 0;
            var unchanged = model.Snapshot();
            Check(unchanged.PhaseMilliseconds[0] == 0 && unchanged.ReasonCombinations[1] == 1, "snapshot arrays isolated");
            Check(empty.PhaseMilliseconds.All(value => value is null) && empty.ReasonCombinations.All(n => n == 0), "prior snapshot remains immutable from model");

            var maxModel = new NtfsScanDiagnostics(() => 0);
            for (int i = 0; i < ObjectLimit; i++) maxModel.RecordSupplement((NtfsSupplementReason)1);
            Reject(() => maxModel.RecordSupplement((NtfsSupplementReason)1), "object cap");
            var max = maxModel.Snapshot();
            Check(max.SupplementObjects == ObjectLimit && max.ReasonCombinations[1] == ObjectLimit, "overflow preserves previous counts");

            void InvalidReport(NtfsScanDiagnosticReport report, string label)
            {
                Reject(() => report.Validate(), label);
                Reject(() => Encode(report), "encode " + label);
            }
            var zeroTimes = new double?[] { 0, 0, 0, 0, 0 };
            foreach (int length in new[] { 4, 6 })
                InvalidReport(new(new double?[length], 0, new int[128]), "phase dimension");
            foreach (int length in new[] { 127, 129 })
                InvalidReport(new(zeroTimes, 0, new int[length]), "bucket dimension");
            foreach (double value in new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 780001d })
                InvalidReport(new([0, 0, value, 0, 0], 0, new int[128]), "invalid phase duration");
            foreach (int count in new[] { -1, ObjectLimit + 1 })
                InvalidReport(new(zeroTimes, count, new int[128]), "object count out of range");
            foreach (var (bucket, count, total) in new[] { (0, 1, 1), (1, -1, 0), (2, 1, 0), (3, 0, 1), (7, int.MaxValue, ObjectLimit) })
            {
                int[] counts = new int[128]; counts[bucket] = count;
                InvalidReport(new(zeroTimes, total, counts), "invalid bucket or total");
            }
            Check(Decode(Encode(new([780000d, 0, 0, 0, 0], ObjectLimit, max.ReasonCombinations))).SupplementObjects == ObjectLimit,
                "duration and count at limit");

            // Locate the changing bytes with two valid reports instead of depending on field offsets.
            var countsA = new int[128]; countsA[10] = 1;
            var countsB = new int[128]; countsB[11] = 1;
            byte[] encodedA = Encode(new(zeroTimes, 1, countsA)), encodedB = Encode(new(zeroTimes, 1, countsB));
            Check(encodedA.Length == encodedB.Length, "fixed report size");
            foreach (int length in new[] { 0, encodedA.Length - 1 })
            {
                bool truncated = false;
                try { Decode(encodedA[..length]); }
                catch (EndOfStreamException) { truncated = true; }
                catch (InvalidDataException ex) when (ex.Message == ErrorCode) { truncated = true; }
                Check(truncated, "truncated report rejected");
            }
            int bucketByte = Enumerable.Range(0, encodedA.Length).First(i => encodedA[i] == 1 && encodedB[i] == 0);
            encodedA[bucketByte] = 0;
            Reject(() => Decode(encodedA), "wire count sum mismatch");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL NTFS scan diagnostics checks={checks}: {ex.Message}");
            return 1;
        }
        Console.WriteLine($"PASS NTFS scan diagnostics checks={checks}; synthetic_only=True");
        return 0;
    }

    private static byte[] Encode(NtfsScanDiagnosticReport value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        NtfsScanDiagnosticReport.Write(writer, value);
        writer.Flush();
        return stream.ToArray();
    }

    private static void CheckDetailedOutput(NtfsScanDiagnosticReport report, Action<bool, string> check)
    {
        const string verb = "--ntfs-personal-timing-details-ui";
        check(NtfsPersonalTimingDriver.ValidDetailedUiInvocation([verb]), "explicit detailed verb");
        check(!NtfsPersonalTimingDriver.ValidDetailedUiInvocation([]) &&
              !NtfsPersonalTimingDriver.ValidDetailedUiInvocation([NtfsPersonalTimingDriver.UiArgument]) &&
              !NtfsPersonalTimingDriver.ValidUiInvocation([verb]), "detailed and ordinary verbs isolated");
        check(!NtfsPersonalTimingDriver.ValidDetailedUiInvocation([verb, @"B:\Synthetic"]) &&
              !NtfsPersonalTimingDriver.ValidDetailedUiInvocation([verb, verb]), "detailed verb rejects extra arguments");

        using var json = JsonDocument.Parse(NtfsPersonalTimingDriver.DiagnosticJson(report));
        var row = json.RootElement;
        static bool ExactFields(JsonElement obj, params string[] expected) =>
            obj.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal));
        check(ExactFields(row, "revision", "kind", "mode", "phases_ms", "supplement_objects", "reason_bits", "reason_combinations",
                  "helper_measured", "multiple_reasons_per_object", "combination_counts_disjoint", "no_names_or_identities"),
            "fixed top-level schema excludes paths and object names");
        check(row.GetProperty("revision").GetString() == "ntfs-scan-details-v1" &&
              row.GetProperty("kind").GetString() == "details" && row.GetProperty("mode").GetString() == "F", "fixed trace identity");
        string[] phaseKeys = ["SourceValidation", "VssSetup", "VssCreation", "SnapshotValidation", "IndexFinalization"];
        var durations = row.GetProperty("phases_ms");
        check(ExactFields(durations, phaseKeys) && phaseKeys.Select(key => durations.GetProperty(key).GetDouble())
            .SequenceEqual(report.PhaseMilliseconds.Select(ms => ms!.Value)), "five helper phase values");
        string[] reasonKeys = ["ResidentAllocation", "Compressed", "Sparse", "IncompleteMetadata", "IncompleteLinks", "ModifiedConflict", "UnsupportedRecord"];
        var reasonBits = row.GetProperty("reason_bits");
        check(ExactFields(reasonBits, reasonKeys) && reasonKeys.Select((key, bit) => reasonBits.GetProperty(key).GetInt32() == (1 << bit)).All(ok => ok),
            "seven stable reason names and bit values");
        int[] bins = row.GetProperty("reason_combinations").EnumerateArray().Select(bucket => bucket.GetInt32()).ToArray();
        check(bins.Length == 128 && bins.SequenceEqual(report.ReasonCombinations) && bins[0] == 0 &&
            bins.Sum() == row.GetProperty("supplement_objects").GetInt32() && bins.Sum() == report.SupplementObjects,
            "disjoint JSON counts sum to unique objects");
        check(row.GetProperty("helper_measured").GetBoolean() && row.GetProperty("multiple_reasons_per_object").GetBoolean() &&
            row.GetProperty("combination_counts_disjoint").GetBoolean() && row.GetProperty("no_names_or_identities").GetBoolean(), "diagnostic interpretation flags");
    }

    private static NtfsScanDiagnosticReport Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        return NtfsScanDiagnosticReport.Read(reader);
    }
}
