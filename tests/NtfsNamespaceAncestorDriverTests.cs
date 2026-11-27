using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace DiskAtlas;

// Pure bounded transport and invocation contracts. These checks only use
// memory buffers: no real process, pipe, directory, volume or VSS is opened.
internal static class NtfsNamespaceAncestorDriverTests
{
    private const string Marker = "SyntheticPrivateTransportMarker";
    private static NamespaceAncestorMeasurement Measurement => new(10, 6, 3, 3, 4, 7, true);
    private static NamespaceAncestorReport Matched => new()
    {
        Outcome = NamespaceAncestorOutcome.Matched, Code = NamespaceAncestorCode.None,
        CleanupConfirmed = true, SameSourceVerified = true, ComparisonComplete = true,
        Checks = NamespaceAncestorChecks.All, Original = Measurement, Candidate = Measurement with { Milliseconds = 8 },
        SnapshotMilliseconds = 20, ComparisonMilliseconds = 2, CleanupMilliseconds = 3
    };
    private static byte[] Frame(ushort kind, byte[] payload, uint magic = 0x31414e41, ushort version = 1, int? size = null)
    {
        byte[] bytes = new byte[12 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), kind);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), size ?? payload.Length);
        payload.CopyTo(bytes, 12); return bytes;
    }
    internal static int Run()
    {
        int passed = 0;
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("NamespaceAncestorDriverContract"); passed++; Console.WriteLine("PASS namespace ancestor driver " + name); }
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidDataException error) { rejected = error.InnerException == null && !error.Message.Contains(Marker, StringComparison.Ordinal); }
            Check(rejected, name);
        }
        void Cancel(Action action, CancellationToken token, string name)
        {
            bool canceled = false;
            try { action(); }
            catch (OperationCanceledException error) { canceled = error.CancellationToken == token; }
            Check(canceled, name);
        }
        var matched = Matched;
        var different = matched with { Outcome = NamespaceAncestorOutcome.Different, Checks = NamespaceAncestorChecks.All & ~NamespaceAncestorChecks.Times };
        var failed = new NamespaceAncestorReport { Outcome = NamespaceAncestorOutcome.Failed, Code = NamespaceAncestorCode.SnapshotUnavailable, CleanupConfirmed = true };
        var uncertain = failed with { Code = NamespaceAncestorCode.CleanupUnconfirmed, CleanupConfirmed = false };
        var canceled = failed with { Outcome = NamespaceAncestorOutcome.Cancelled, Code = NamespaceAncestorCode.Cancelled };
        var minimum = new NamespaceAncestorMeasurement(0, 1, 1, 1, 0, 1, true);
        var maximum = new NamespaceAncestorMeasurement(720000, 1048576, 1048576, 1048576, 3145729, 4194305, true);
        foreach (var report in new[] { matched, different, failed, uncertain, canceled,
            failed with { Original = Measurement }, failed with { Original = Measurement, Candidate = Measurement },
            matched with { Original = minimum, Candidate = minimum, SnapshotMilliseconds = 0, ComparisonMilliseconds = 0, CleanupMilliseconds = 0 },
            matched with { Original = maximum, Candidate = maximum, SnapshotMilliseconds = 720000, ComparisonMilliseconds = 720000, CleanupMilliseconds = 720000 } })
        {
            byte[] payload = NamespaceAncestorProtocol.Encode(report);
            Check(NamespaceAncestorProtocol.Decode(payload) == report, "fixed reports roundtrip without new fields");
            using var stream = new MemoryStream(); var writer = new NamespaceAncestorProtocol.Writer(stream);
            writer.Stage(NamespaceAncestorStage.Snapshot); writer.Stage(NamespaceAncestorStage.Cleanup); writer.Report(report);
            stream.Position = 0; var reader = new NamespaceAncestorProtocol.Reader(stream);
            Check(reader.Read().Stage == NamespaceAncestorStage.Snapshot && reader.Read().Stage == NamespaceAncestorStage.Cleanup && reader.Read().Report == report,
                "stage and terminal report frames roundtrip");
            Reject(() => reader.Read(), "reader rejects data after terminal");
            Reject(() => writer.Stage(NamespaceAncestorStage.Original), "writer rejects stage after terminal");
            Reject(() => writer.Report(report), "writer rejects duplicate terminal report");
        }
        foreach (var report in new[]
        {
            matched with { Outcome = (NamespaceAncestorOutcome)255 }, matched with { Code = (NamespaceAncestorCode)255 },
            matched with { Checks = (NamespaceAncestorChecks)255 }, matched with { CleanupConfirmed = false },
            matched with { SameSourceVerified = false }, matched with { ComparisonComplete = false },
            matched with { Checks = NamespaceAncestorChecks.None }, matched with { Code = NamespaceAncestorCode.EnumerationFailed },
            matched with { Original = null }, matched with { Candidate = null }, different with { Checks = NamespaceAncestorChecks.All },
            different with { CleanupConfirmed = false }, different with { SameSourceVerified = false },
            failed with { Code = NamespaceAncestorCode.None }, failed with { Code = NamespaceAncestorCode.Cancelled },
            failed with { SameSourceVerified = true }, failed with { ComparisonComplete = true },
            failed with { Checks = NamespaceAncestorChecks.Paths }, failed with { Code = NamespaceAncestorCode.CleanupUnconfirmed },
            canceled with { CleanupConfirmed = false }, canceled with { Code = NamespaceAncestorCode.InternalFailure },
            canceled with { SameSourceVerified = true }, canceled with { ComparisonComplete = true },
            matched with { SnapshotMilliseconds = -1 }, matched with { ComparisonMilliseconds = -1 }, matched with { CleanupMilliseconds = -1 },
            matched with { SnapshotMilliseconds = 720001 }, matched with { ComparisonMilliseconds = 720001 }, matched with { CleanupMilliseconds = 720001 },
            matched with { Candidate = Measurement with { Entries = 7 } },
            matched with { Candidate = Measurement with { Opened = 2, Completed = 2, Pages = 5 } }
        }) Reject(() => NamespaceAncestorProtocol.Encode(report), "inconsistent report state rejected");
        foreach (var m in new[]
        {
            Measurement with { Milliseconds = -1 }, Measurement with { Milliseconds = 720001 },
            Measurement with { Entries = 0 }, Measurement with { Entries = 1048577 },
            Measurement with { Opened = 0 }, Measurement with { Opened = 7, Completed = 7, Queries = 11 },
            Measurement with { Completed = 0 }, Measurement with { Completed = 4 },
            Measurement with { Pages = -1 }, Measurement with { Pages = 8 },
            Measurement with { Queries = 2 }, Measurement with { Queries = 4194306 },
            Measurement with { Queries = 5 }, Measurement with { HandlesSettled = false }
        }) Reject(() => NamespaceAncestorProtocol.Encode(matched with { Original = m }), "measurement bounds and page EOF identity enforced");

        byte[] good = NamespaceAncestorProtocol.Encode(matched);
        Check(good.Length == 90, "report has fixed bounded binary shape");
        foreach (int offset in new[] { 0, 1, 2, 3, 4, 5, 30, 59, 60, 89 })
        {
            byte[] bytes = good.ToArray(); bytes[offset] = 255;
            Reject(() => NamespaceAncestorProtocol.Decode(bytes), "invalid enum boolean or flags byte rejected");
        }
        foreach (int offset in new[] { 6, 14, 22, 31, 61 })
        {
            byte[] bytes = good.ToArray(); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), -1);
            Reject(() => NamespaceAncestorProtocol.Decode(bytes), "negative encoded time rejected");
        }
        foreach (int offset in new[] { 39, 43, 47, 51, 55, 69, 73, 77, 81, 85 })
        {
            byte[] bytes = good.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), int.MaxValue);
            Reject(() => NamespaceAncestorProtocol.Decode(bytes), "encoded counter overflow rejected");
        }
        for (int length = 0; length < good.Length; length++)
            Reject(() => NamespaceAncestorProtocol.Decode(good[..length]), "every truncated report boundary rejected");
        Reject(() => NamespaceAncestorProtocol.Decode([.. good, 0]), "trailing report data rejected");
        Reject(() => NamespaceAncestorProtocol.Decode("{\"path\":\"SyntheticPrivateTransportMarker\"}"u8.ToArray()), "arbitrary JSON cannot enter binary report");

        var balanced = matched with
        {
            Balanced = true,
            OriginalRepeat = Measurement with { Milliseconds = 11 },
            CandidateRepeat = Measurement with { Milliseconds = 7 }
        };
        byte[] balancedPayload = NamespaceAncestorProtocol.Encode(balanced);
        Check(NamespaceAncestorProtocol.Decode(balancedPayload, balanced: true) == balanced,
            "balanced payload preserves both measurements of each method");
        Reject(() => NamespaceAncestorProtocol.Decode(balancedPayload), "v1 decoder rejects balanced payload");
        Reject(() => NamespaceAncestorProtocol.Decode(good, balanced: true), "balanced decoder rejects v1 payload");
        foreach (var report in new[]
        {
            balanced with { OriginalRepeat = null },
            balanced with { CandidateRepeat = null },
            balanced with { Balanced = false },
            balanced with { OriginalRepeat = Measurement with { Entries = 7 } },
            balanced with { CandidateRepeat = Measurement with { HandlesSettled = false } },
            balanced with { CandidateRepeat = Measurement with { Queries = 6 } }
        }) Reject(() => NamespaceAncestorProtocol.Encode(report), "balanced success rejects missing or contradictory measurements");
        byte[] corrupted = balancedPayload.ToArray();
        BinaryPrimitives.WriteInt64LittleEndian(corrupted.AsSpan(corrupted.Length - 29), -1);
        Reject(() => NamespaceAncestorProtocol.Decode(corrupted, balanced: true), "balanced decoder rejects damaged repeat measurement");
        var balancedMismatch = different with { Balanced = true };
        Check(NamespaceAncestorProtocol.Decode(NamespaceAncestorProtocol.Encode(balancedMismatch), balanced: true) == balancedMismatch,
            "balanced first mismatch permits only the two completed rounds");
        using (var stream = new MemoryStream())
        {
            var writer = new NamespaceAncestorProtocol.Writer(stream, balanced: true);
            writer.Stage(NamespaceAncestorStage.Candidate);
            writer.Report(balanced);
            stream.Position = 0;
            var reader = new NamespaceAncestorProtocol.Reader(stream, balanced: true);
            Check(reader.Read().Stage == NamespaceAncestorStage.Candidate && reader.Read().Report == balanced,
                "balanced frame sequence roundtrips");
            stream.Position = 0;
            Reject(() => new NamespaceAncestorProtocol.Reader(stream).Read(), "v1 reader rejects v2 frame");
        }
        Reject(() => new NamespaceAncestorProtocol.Reader(new MemoryStream(Frame(2, good)), balanced: true).Read(),
            "balanced reader rejects v1 frame");
        using (var stream = new MemoryStream())
        {
            Reject(() => new NamespaceAncestorProtocol.Writer(stream, balanced: true).Report(matched), "balanced writer rejects v1 report");
            Reject(() => new NamespaceAncestorProtocol.Writer(stream).Report(balanced), "v1 writer rejects balanced report");
            Check(stream.Length == 0, "mode mismatch publishes no frame");
        }

        foreach (var frame in new[]
        {
            Frame(1, [255]), Frame(1, []), Frame(1, [0, 0]), Frame(0, [0]), Frame(3, [0]),
            Frame(2, []), Frame(2, [.. good, 0]), Frame(1, [0], magic: 0), Frame(1, [0], version: 2),
            Frame(1, [0], size: -1), Frame(1, [0], size: NamespaceAncestorProtocol.MaximumBytes),
            Frame(2, good)[..^1], new byte[11]
        }) Reject(() => new NamespaceAncestorProtocol.Reader(new MemoryStream(frame)).Read(), "corrupt truncated or oversized frame rejected");
        Reject(() => new NamespaceAncestorProtocol.Reader(new FailingStream()).Read(), "reader sanitizes external stream exception");
        using (var stream = new MemoryStream())
        {
            var writer = new NamespaceAncestorProtocol.Writer(stream);
            int fit = NamespaceAncestorProtocol.MaximumBytes / 13;
            for (int i = 0; i < fit; i++) writer.Stage(NamespaceAncestorStage.Snapshot);
            Reject(() => writer.Stage(NamespaceAncestorStage.Snapshot), "writer enforces cumulative byte budget");
            Check(stream.Length <= NamespaceAncestorProtocol.MaximumBytes, "writer never publishes over budget");
            Reject(() => writer.Stage((NamespaceAncestorStage)255), "writer refuses unrecognized stage");
        }
        using (var stream = new MemoryStream())
        {
            byte[] stage = Frame(1, [0]); int fit = NamespaceAncestorProtocol.MaximumBytes / stage.Length;
            for (int i = 0; i <= fit; i++) stream.Write(stage); stream.Position = 0;
            var reader = new NamespaceAncestorProtocol.Reader(stream); bool all = true;
            for (int i = 0; i < fit; i++) all &= reader.Read().Stage == NamespaceAncestorStage.Snapshot;
            Check(all, "reader accepts complete stages within cumulative budget");
            Reject(() => reader.Read(), "reader stops at cumulative byte budget");
            Reject(() => reader.Read(), "reader remains terminal after rejection");
        }
        using (var stop = new CancellationTokenSource())
        using (var stream = new MemoryStream(Frame(1, [0])))
        {
            stop.Cancel(); var reader = new NamespaceAncestorProtocol.Reader(stream); var writer = new NamespaceAncestorProtocol.Writer(stream);
            Cancel(() => reader.Read(stop.Token), stop.Token, "pre-canceled reader preserves caller token");
            Check(stream.Position == 0, "canceled reader consumes no header");
            Cancel(() => writer.Stage(NamespaceAncestorStage.Original, stop.Token), stop.Token, "pre-canceled stage consumes no data");
            Cancel(() => writer.Stage((NamespaceAncestorStage)255, stop.Token), stop.Token, "cancellation precedes invalid stage validation");
            Cancel(() => writer.Report(matched, stop.Token), stop.Token, "pre-canceled report preserves caller token");
            Cancel(() => writer.Report(matched with { Checks = (NamespaceAncestorChecks)255 }, stop.Token), stop.Token, "cancellation precedes invalid report validation");
            Check(stream.Position == 0, "canceled writes publish no bytes");
        }

        Check(NtfsNamespaceAncestorDriver.RespectCancellation(matched, false) == matched, "no cancellation preserves result");
        var observedCancel = NtfsNamespaceAncestorDriver.RespectCancellation(matched, true);
        Check(observedCancel.Outcome == NamespaceAncestorOutcome.Cancelled && observedCancel.Code == NamespaceAncestorCode.Cancelled && observedCancel.CleanupConfirmed &&
            !observedCancel.SameSourceVerified && !observedCancel.ComparisonComplete && observedCancel.Checks == NamespaceAncestorChecks.None,
            "late cancellation cannot publish matched result");
        NamespaceAncestorProtocol.Validate(observedCancel);
        Check(NtfsNamespaceAncestorDriver.RespectCancellation(uncertain, true) == uncertain, "cancellation cannot hide unknown cleanup");
        Check(NtfsNamespaceAncestorDriver.ValidParent([NtfsNamespaceAncestorDriver.Argument]), "parent accepts explicit test entry only");
        foreach (var args in new[] { Array.Empty<string>(), new[] { "other" }, new[] { NtfsNamespaceAncestorDriver.WorkerArgument },
            new[] { NtfsNamespaceAncestorDriver.Argument, "X:\\SyntheticPrivateTransportMarker" }, new[] { NtfsNamespaceAncestorDriver.Argument, "--extra" } })
            Check(!NtfsNamespaceAncestorDriver.ValidParent(args), "scope or extra arguments cannot enter parent command line");
        Check(NtfsNamespaceAncestorDriver.Requested([NtfsNamespaceAncestorDriver.Argument]) && NtfsNamespaceAncestorDriver.Requested([NtfsNamespaceAncestorDriver.WorkerArgument]),
            "legacy parent and worker entry points remain routed");
        Check(NtfsNamespaceAncestorDriver.ValidParent([NtfsNamespaceAncestorDriver.BalancedArgument]) &&
            NtfsNamespaceAncestorDriver.Requested([NtfsNamespaceAncestorDriver.BalancedArgument]) &&
            NtfsNamespaceAncestorDriver.Requested([NtfsNamespaceAncestorDriver.BalancedWorkerArgument]) &&
            !NtfsNamespaceAncestorDriver.ValidParent([NtfsNamespaceAncestorDriver.BalancedArgument, "extra"]),
            "balanced mode uses explicit entry points without extra parent arguments");
        Check(!NtfsNamespaceAncestorDriver.Requested([]) && !NtfsNamespaceAncestorDriver.Requested(["other"]) &&
            !NtfsNamespaceAncestorDriver.Requested([NtfsNamespaceAncestorDriver.Argument + "extra"]), "ordinary invocations do not route to driver");
        string pipe = "AsukaLens.Ntfs." + new string('1', 32);
        Check(NtfsNamespaceAncestorDriver.ParseWorker([NtfsNamespaceAncestorDriver.WorkerArgument, pipe, "17", "1"], out int pid, out long ticks) && pid == 17 && ticks == 1,
            "worker binds a positive parent identity");
        Check(NtfsNamespaceAncestorDriver.ParseWorker([NtfsNamespaceAncestorDriver.BalancedWorkerArgument, pipe, "17", "1"], out pid, out ticks) &&
            pid == 17 && ticks == 1, "balanced worker binds the same parent identity");
        Check(NtfsNamespaceAncestorDriver.ParseWorker([NtfsNamespaceAncestorDriver.WorkerArgument, pipe, int.MaxValue.ToString(CultureInfo.InvariantCulture),
            DateTime.MaxValue.Ticks.ToString(CultureInfo.InvariantCulture)], out _, out _), "worker accepts bounded identity upper endpoints");
        foreach (var args in new[] { Array.Empty<string>(), new[] { "other", pipe, "17", "1" },
            new[] { NtfsNamespaceAncestorDriver.WorkerArgument, "X:\\SyntheticPrivateTransportMarker", "17", "1" },
            new[] { NtfsNamespaceAncestorDriver.WorkerArgument, pipe, "17", "1", "extra" } })
            Check(!NtfsNamespaceAncestorDriver.ParseWorker(args, out _, out _), "worker rejects path pipe or malformed invocation");
        foreach (string value in new[] { "0", "-1", "+1", " 1", "1 ", "2147483648", "not-number" })
            Check(!NtfsNamespaceAncestorDriver.ParseWorker([NtfsNamespaceAncestorDriver.WorkerArgument, pipe, value, "1"], out _, out _), "worker rejects invalid parent pid");
        foreach (string value in new[] { "0", "-1", "+1", " 1", "1 ", "3155378976000000000", "9223372036854775807", "not-number" })
            Check(!NtfsNamespaceAncestorDriver.ParseWorker([NtfsNamespaceAncestorDriver.WorkerArgument, pipe, "17", value], out _, out _), "worker rejects invalid parent start ticks");
        foreach (string bad in new[] { "", "AsukaLens.Ntfs." + new string('g', 32), "AsukaLens.Ntfs." + new string('1', 31), pipe + "x", "Other." + new string('1', 32) })
            Check(!NtfsNamespaceAncestorDriver.ParseWorker([NtfsNamespaceAncestorDriver.WorkerArgument, bad, "17", "1"], out _, out _), "worker refuses unbound pipe names");

        var previous = Console.Out;
        using var captured = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(captured); NtfsNamespaceAncestorDriver.Summary(matched, "None", true);
        }
        finally { Console.SetOut(previous); }
        using (var json = JsonDocument.Parse(captured.ToString()))
        {
            var root = json.RootElement;
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "revision", "kind", "outcome", "code", "driver_code", "worker_summary_received", "cleanup_confirmed",
                "same_source_verified", "comparison_complete", "checks", "snapshot_ms", "original_ms", "candidate_ms", "comparison_ms", "cleanup_ms", "original", "candidate",
                "raw_read", "special_lookup", "product_end_to_end" };
            Check(root.EnumerateObject().All(p => allowed.Remove(p.Name)) && allowed.Count == 0, "summary contains only explicit white-listed fields");
            Check(!root.GetProperty("raw_read").GetBoolean() && !root.GetProperty("special_lookup").GetBoolean() && !root.GetProperty("product_end_to_end").GetBoolean(),
                "summary cannot claim raw lookup or complete product timing");
            Check(!captured.ToString().Contains(Marker, StringComparison.Ordinal) && root.GetProperty("worker_summary_received").GetBoolean(), "summary includes provenance without private text");
        }
        using (var output = new StringWriter(CultureInfo.InvariantCulture))
        {
            try
            {
                Console.SetOut(output);
                NtfsNamespaceAncestorDriver.Summary(balanced, "None", true);
            }
            finally { Console.SetOut(previous); }
            using var json = JsonDocument.Parse(output.ToString());
            var root = json.RootElement;
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "revision", "kind", "outcome", "code", "driver_code", "worker_summary_received", "cleanup_confirmed",
                "same_source_verified", "comparison_complete", "checks", "snapshot_ms", "original_ms", "candidate_ms",
                "comparison_ms", "cleanup_ms", "original", "candidate", "raw_read", "special_lookup", "product_end_to_end",
                "traversal_order", "original_repeat_ms", "candidate_repeat_ms", "original_repeat", "candidate_repeat"
            };
            Check(root.EnumerateObject().All(p => allowed.Remove(p.Name)) && allowed.Count == 0,
                "balanced summary contains only explicit white-listed fields");
            Check(root.GetProperty("revision").GetString() == "namespace-ancestor-balanced-live-v2" &&
                root.GetProperty("traversal_order").EnumerateArray().Select(v => v.GetString()).SequenceEqual(
                    new[] { "Candidate", "Original", "Original", "Candidate" }) &&
                root.GetProperty("original_repeat_ms").GetInt64() == 11 && root.GetProperty("candidate_repeat_ms").GetInt64() == 7,
                "balanced summary labels ordered repeats and fixed protocol revision");
        }
        foreach (var tuple in new[] { (Report: matched, Code: Marker, Received: true), (Report: matched, Code: "DriverFailed", Received: false),
            (Report: matched, Code: "None", Received: false), (Report: failed, Code: "DriverFailed", Received: true) })
        {
            using var output = new StringWriter(CultureInfo.InvariantCulture); bool rejected = false;
            try
            {
                Console.SetOut(output);
                try { NtfsNamespaceAncestorDriver.Summary(tuple.Report, tuple.Code, tuple.Received); }
                catch (InvalidDataException error) { rejected = !error.Message.Contains(Marker, StringComparison.Ordinal); }
            }
            finally { Console.SetOut(previous); }
            Check(rejected && output.ToString().Length == 0, "contradictory or private summary input publishes nothing");
        }
        return passed;
    }
    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new IOException(Marker);
        public override long Position { get => throw new IOException(Marker); set => throw new IOException(Marker); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException(Marker);
        public override int Read(Span<byte> buffer) => throw new IOException(Marker);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
