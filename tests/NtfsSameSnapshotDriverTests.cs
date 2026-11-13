using System.Buffers.Binary;
using System.Text.Json;

namespace DiskAtlas;

// Pure transport/state contracts. No process, pipe, source, Native or disk I/O.
internal static class NtfsSameSnapshotDriverTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("SameSnapshotContract"); passed++; }
        void Reject(Action action)
        {
            try { action(); }
            catch (NtfsSameSnapshotProtocolException error) { Check(error.Message == "SameSnapshotProtocol" && error.InnerException == null); return; }
            throw new InvalidOperationException("SameSnapshotRejectionMissing");
        }
        var matched = Matched();
        var different = matched with
        {
            Outcome = NtfsSameSnapshotOutcome.Different, FileLogicalEqual = false,
            NativeProbeRequired = true, NativeProbeAttempted = true, NativeProbeComplete = true,
            ProbeBranch = NtfsSameSnapshotProbeBranch.Normal, FastMatchesNativeLogical = true,
            DirectoryMatchesNativeLogical = false, NativeMetadataConsistent = true, NativeCompressionObserved = false
        };
        var incomplete = new NtfsSameSnapshotReport
        {
            Outcome = NtfsSameSnapshotOutcome.Incomplete, Code = NtfsSameSnapshotCode.NativeProbeIncomplete,
            CleanupConfirmed = true, SameSourceVerified = true, BudgetRespected = true,
            IncludedScopeEqual = true, FileLogicalEqual = false, NativeProbeRequired = true
        };
        var cancelled = new NtfsSameSnapshotReport { Outcome = NtfsSameSnapshotOutcome.Cancelled, Code = NtfsSameSnapshotCode.Cancelled, CleanupConfirmed = true };
        var failed = new NtfsSameSnapshotReport { Outcome = NtfsSameSnapshotOutcome.Failed, Code = NtfsSameSnapshotCode.SnapshotUnavailable, CleanupConfirmed = true };
        foreach (var report in new[] { matched, different, incomplete, cancelled, cancelled with { CleanupConfirmed = false }, failed,
            failed with { CleanupConfirmed = false, Code = NtfsSameSnapshotCode.CleanupUnconfirmed }, incomplete with { BudgetRespected = false } })
        {
            byte[] encoded = NtfsSameSnapshotProtocol.EncodeReport(report);
            Check(encoded.Length == 37);
            Check(NtfsSameSnapshotProtocol.DecodeReport(encoded) == report);
            using var stream = new MemoryStream(); var writer = new NtfsSameSnapshotProtocol.Writer(stream);
            writer.Stage(FastScanWireStage.Preparing); writer.Stage(FastScanWireStage.Cleanup); writer.Report(report);
            stream.Position = 0; var reader = new NtfsSameSnapshotProtocol.Reader(stream);
            Check(reader.Read().Stage == FastScanWireStage.Preparing);
            Check(reader.Read().Stage == FastScanWireStage.Cleanup);
            Check(reader.Read().Report == report);
            Reject(() => reader.Read()); Reject(() => writer.Stage(FastScanWireStage.Reading));
        }
        foreach (var invalid in new[]
        {
            matched with { CleanupConfirmed = false }, matched with { SameSourceVerified = false }, matched with { BudgetRespected = false },
            matched with { FastRootCountsValid = null }, matched with { DirectoryRootCountsValid = false }, matched with { NativeProbeRequired = true },
            matched with { NativeProbeAttempted = true }, matched with { ProbeBranch = NtfsSameSnapshotProbeBranch.Normal },
            matched with { Outcome = (NtfsSameSnapshotOutcome)255 }, matched with { Code = (NtfsSameSnapshotCode)255 },
            matched with { ProbeBranch = (NtfsSameSnapshotProbeBranch)255 }, different with { FileLogicalEqual = true },
            different with { NativeProbeComplete = false }, different with { NativeProbeRequired = false },
            different with { SameSourceVerified = false }, different with { BudgetRespected = false },
            different with { IncludedScopeEqual = null }, different with { FastMatchesNativeLogical = null, DirectoryMatchesNativeLogical = null,
                NativeMetadataConsistent = null, NativeCompressionObserved = null },
            incomplete with { CleanupConfirmed = false }, incomplete with { ComparisonComplete = true }, incomplete with { Code = NtfsSameSnapshotCode.None },
            cancelled with { Code = NtfsSameSnapshotCode.None }, cancelled with { SameSourceVerified = true }, cancelled with { FileLogicalEqual = true },
            failed with { Code = NtfsSameSnapshotCode.None }, failed with { FileLogicalEqual = false }, failed with { NativeProbeRequired = true },
            failed with { SameSourceVerified = true }, failed with { Code = NtfsSameSnapshotCode.CleanupUnconfirmed }
        }) Reject(() => NtfsSameSnapshotProtocol.EncodeReport(invalid));
        byte[] good = NtfsSameSnapshotProtocol.EncodeReport(matched);
        foreach (int offset in new[] { 0, 1, 2, 3, 4, 5, 6, 20, 21, 22, 23, 24, 25, 36 })
        {
            byte[] altered = good.ToArray(); altered[offset] = 255;
            Reject(() => NtfsSameSnapshotProtocol.DecodeReport(altered));
        }
        Reject(() => NtfsSameSnapshotProtocol.DecodeReport(good[..^1]));
        Reject(() => NtfsSameSnapshotProtocol.DecodeReport([.. good, 0]));
        Reject(() => NtfsSameSnapshotProtocol.DecodeReport("{\"path\":\"private\"}"u8.ToArray()));
        foreach (var bytes in new[]
        {
            Frame(1, [255]), Frame(1, []), Frame(1, [0, 0]), Frame(3, [0]), Frame(2, []), Frame(2, [.. good, 0]),
            Frame(1, [0], magic: 0), Frame(1, [0], version: 2), Frame(1, [0], size: -1),
            Frame(1, [0], size: NtfsSameSnapshotProtocol.MaximumBytes), new byte[11]
        }) Reject(() => new NtfsSameSnapshotProtocol.Reader(new MemoryStream(bytes)).Read());
        Reject(() => new NtfsSameSnapshotProtocol.Reader(new FailingStream()).Read());
        using (var stream = new MemoryStream())
        {
            var writer = new NtfsSameSnapshotProtocol.Writer(stream);
            for (int i = 0; i < NtfsSameSnapshotProtocol.MaximumBytes / 13; i++) writer.Stage(FastScanWireStage.Preparing);
            Reject(() => writer.Stage(FastScanWireStage.Preparing)); Check(stream.Length <= NtfsSameSnapshotProtocol.MaximumBytes);
        }
        using (var stream = new MemoryStream())
        {
            byte[] stage = Frame(1, [0]); int fit = NtfsSameSnapshotProtocol.MaximumBytes / stage.Length;
            for (int i = 0; i <= fit; i++) stream.Write(stage); stream.Position = 0;
            var reader = new NtfsSameSnapshotProtocol.Reader(stream);
            for (int i = 0; i < fit; i++) Check(reader.Read().Stage.HasValue);
            Reject(() => reader.Read());
        }
        using (var source = new CancellationTokenSource())
        {
            source.Cancel(); using var stream = new MemoryStream(Frame(1, [0]));
            try { new NtfsSameSnapshotProtocol.Reader(stream).Read(source.Token); Check(false); } catch (OperationCanceledException) { Check(stream.Position == 0); }
            try { new NtfsSameSnapshotProtocol.Writer(stream).Stage(FastScanWireStage.Reading, source.Token); Check(false); } catch (OperationCanceledException) { Check(stream.Position == 0); }
        }
        Check(NtfsSameSnapshotDriver.ExitCode(matched) == 0); Check(NtfsSameSnapshotDriver.ExitCode(different) == 3);
        Check(NtfsSameSnapshotDriver.ExitCode(incomplete) == 1); Check(NtfsSameSnapshotDriver.ExitCode(cancelled) == 1);
        Reject(() => NtfsSameSnapshotDriver.ExitCode(matched with { CleanupConfirmed = false }));
        Check(NtfsSameSnapshotDriver.RespectCancellation(matched, false) == matched);
        Check(NtfsSameSnapshotDriver.RespectCancellation(matched, true) == cancelled);
        var uncertain = failed with { CleanupConfirmed = false };
        Check(NtfsSameSnapshotDriver.RespectCancellation(uncertain, true) == uncertain);
        Check(NtfsSameSnapshotDriver.ValidHost(@"C:\synthetic\DiskAtlas.Tests.exe", "DiskAtlas.Tests"));
        Check(!NtfsSameSnapshotDriver.ValidHost(@"C:\synthetic\AsukaLens.exe", "DiskAtlas.Tests"));
        Check(!NtfsSameSnapshotDriver.ValidHost(@"C:\synthetic\DiskAtlas.Tests.exe", "DiskAtlas"));
        Check(!NtfsSameSnapshotDriver.ValidHost(null, null));
        Check(NtfsSameSnapshotDriver.ValidParentArguments([NtfsSameSnapshotDriver.Argument]));
        Check(!NtfsSameSnapshotDriver.ValidParentArguments([NtfsSameSnapshotDriver.Argument, "private"]));
        Check(!NtfsSameSnapshotDriver.ValidParentArguments([]));
        string pipe = "AsukaLens.Ntfs." + new string('a', 32);
        Check(NtfsSameSnapshotDriver.TryWorkerArguments([NtfsSameSnapshotDriver.WorkerArgument, pipe, "123", "456"], out int pid, out long ticks) && pid == 123 && ticks == 456);
        foreach (var args in new[]
        {
            Array.Empty<string>(), new[] { NtfsSameSnapshotDriver.WorkerArgument },
            new[] { NtfsSameSnapshotDriver.WorkerArgument, "../pipe", "123", "456" },
            new[] { NtfsSameSnapshotDriver.WorkerArgument, pipe, "0", "456" },
            new[] { NtfsSameSnapshotDriver.WorkerArgument, pipe, "-1", "456" },
            new[] { NtfsSameSnapshotDriver.WorkerArgument, pipe, " 123", "456" },
            new[] { NtfsSameSnapshotDriver.WorkerArgument, pipe, "2147483648", "456" },
            new[] { NtfsSameSnapshotDriver.WorkerArgument, pipe, "123", "0" },
            new[] { NtfsSameSnapshotDriver.WorkerArgument, pipe, "123", "3155378976000000000" },
            new[] { NtfsSameSnapshotDriver.WorkerArgument, pipe, "123", "456", "extra" },
            new[] { "--other-worker", pipe, "123", "456" }
        }) { Check(!NtfsSameSnapshotDriver.TryWorkerArguments(args, out _, out _)); Check(NtfsSameSnapshotDriver.RunWorker(args) == 2); }
        var original = Console.Out;
        try
        {
            using var text = new StringWriter(); Console.SetOut(text);
            NtfsSameSnapshotDriver.WriteSummary(different, "None", true);
            using var document = JsonDocument.Parse(text.ToString()); var root = document.RootElement;
            var names = new HashSet<string>(("revision kind outcome code driver_code worker_summary_received cleanup_confirmed comparison_complete same_source_verified budget_respected " +
                "included_scope_equal types_equal identities_equal file_logical_equal file_allocation_equal file_links_equal allocation_known fast_root_logical_valid fast_root_allocation_valid " +
                "directory_root_logical_valid directory_root_allocation_valid fast_root_counts_valid directory_root_counts_valid exclusions_equal exclusion_details_complete " +
                "native_probe_required native_probe_attempted native_probe_complete probe_branch fast_matches_native_identity directory_matches_native_identity " +
                "fast_matches_native_logical directory_matches_native_logical fast_matches_native_allocation directory_matches_native_allocation fast_matches_native_links directory_matches_native_links " +
                "fast_matches_native_standard_allocation directory_matches_native_standard_allocation native_metadata_consistent native_compression_observed product_end_to_end_measured past_live_difference_explained files_written").Split(' '));
            Check(names.SetEquals(root.EnumerateObject().Select(property => property.Name)));
            var strings = new HashSet<string>(["revision", "kind", "outcome", "code", "driver_code", "probe_branch"]);
            foreach (var property in root.EnumerateObject()) Check(strings.Contains(property.Name)
                ? property.Value.ValueKind == JsonValueKind.String : property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null);
            Check(!root.GetProperty("product_end_to_end_measured").GetBoolean()); Check(!root.GetProperty("past_live_difference_explained").GetBoolean());
            Reject(() => NtfsSameSnapshotDriver.WriteSummary(matched, @"C:\private\exception", true));
        }
        finally { Console.SetOut(original); }
        return passed;
    }

    private static NtfsSameSnapshotReport Matched() => new()
    {
        Outcome = NtfsSameSnapshotOutcome.Matched, Code = NtfsSameSnapshotCode.None, CleanupConfirmed = true, ComparisonComplete = true,
        SameSourceVerified = true, BudgetRespected = true, IncludedScopeEqual = true, TypesEqual = true, IdentitiesEqual = true,
        FileLogicalEqual = true, FileAllocationEqual = true, FileLinksEqual = true, AllocationKnown = true,
        FastRootLogicalValid = true, FastRootAllocationValid = true, DirectoryRootLogicalValid = true, DirectoryRootAllocationValid = true,
        ExclusionsEqual = true, ExclusionDetailsComplete = true, FastRootCountsValid = true, DirectoryRootCountsValid = true
    };
    private static byte[] Frame(ushort kind, byte[] payload, uint magic = 0x31535341, ushort version = 1, int? size = null)
    {
        byte[] data = new byte[12 + payload.Length]; BinaryPrimitives.WriteUInt32LittleEndian(data, magic);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), version); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), kind);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), size ?? payload.Length); payload.CopyTo(data, 12); return data;
    }
    private sealed class FailingStream : MemoryStream
    {
        public override int Read(Span<byte> buffer) => throw new IOException(@"C:\private\message");
    }
}
