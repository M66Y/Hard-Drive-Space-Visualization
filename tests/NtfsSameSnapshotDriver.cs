using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DiskAtlas;

// Test-only transport. The scope uses the existing bounded private request;
// only fixed categories and booleans cross the separate summary protocol.
internal static class NtfsSameSnapshotDriver
{
    internal const string Argument = "--ntfs-same-snapshot-comparison";
    internal const string WorkerArgument = "--ntfs-same-snapshot-worker";
    private const byte CancelByte = 0x43;
    internal static bool ValidHost(string? executable, string? assembly) => FastScanBridge.IsTimingHost(executable, assembly);
    internal static bool IsParentRequest(string[] args) => args.Length > 0 && args[0] == Argument;
    internal static bool IsWorkerRequest(string[] args) => args.Length > 0 && args[0] == WorkerArgument;
    internal static bool ValidParentArguments(string[] args) => args.Length == 1 && args[0] == Argument;
    internal static bool TryWorkerArguments(string[] args, out int pid, out long ticks)
    {
        pid = 0; ticks = 0;
        try
        {
            return args.Length == 4 && args[0] == WorkerArgument && FastScanBridge.ValidPipeName(args[1]) &&
                int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out pid) && pid > 0 &&
                long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) && ticks > 0 && ticks <= DateTime.MaxValue.Ticks;
        }
        catch { pid = 0; ticks = 0; return false; }
    }
    internal static int ExitCode(NtfsSameSnapshotReport report)
    {
        NtfsSameSnapshotProtocol.Validate(report);
        return report.Outcome switch { NtfsSameSnapshotOutcome.Matched => 0, NtfsSameSnapshotOutcome.Different => 3, _ => 1 };
    }

    internal static int RunParent(string[] args)
    {
        bool requested = false;
        if (!ValidParentArguments(args) || !ValidHost(Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name))
            return DriverFailure("InvalidInvocation", false, true);
        try
        {
            string scope = NtfsPrototype.NtfsPersonalAcceptanceDriver.ReadScope(Console.In);
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
            ConsoleCancelEventHandler handler = (_, value) => { value.Cancel = true; cancelled.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                string name = "AsukaLens.Ntfs." + Guid.NewGuid().ToString("N");
                using var server = FastScanBridge.CreatePipe(name);
                using var stopRead = deadline.Token.Register(() => { try { server.Dispose(); } catch { } });
                using var parent = Process.GetCurrentProcess(); Process? child = null;
                try
                {
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                    start.ArgumentList.Add(WorkerArgument); start.ArgumentList.Add(name);
                    start.ArgumentList.Add(parent.Id.ToString(CultureInfo.InvariantCulture));
                    start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
                    var launch = Task.Run(() => Process.Start(start));
                    try { child = launch.WaitAsync(cancelled.Token).GetAwaiter().GetResult(); }
                    catch
                    {
                        _ = launch.ContinueWith(task => { _ = task.Exception; if (task.Status == TaskStatus.RanToCompletion) task.Result?.Dispose(); }, TaskScheduler.Default);
                        throw;
                    }
                    if (child == null) return DriverFailure("LaunchFailed", false, true);
                    using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token, deadline.Token);
                    connect.CancelAfter(TimeSpan.FromMinutes(2));
                    while (true)
                    {
                        var waiting = server.WaitForConnectionAsync(connect.Token);
                        while (!waiting.IsCompleted)
                        {
                            // No scope has been sent, so this process cannot
                            // have started a source operation on our behalf.
                            if (child.HasExited) return DriverFailure("WorkerExitedBeforeRequest", false, true);
                            Task.WhenAny(waiting, Task.Delay(100, connect.Token)).GetAwaiter().GetResult(); connect.Token.ThrowIfCancellationRequested();
                        }
                        waiting.GetAwaiter().GetResult();
                        if (FastScanBridge.GetNamedPipeClientProcessId(server.SafePipeHandle, out uint actual) && actual == child.Id) break;
                        server.Disconnect();
                    }
                    cancelled.Token.ThrowIfCancellationRequested(); requested = true;
                    FastScanWire.WriteRequest(server, scope, cancelled.Token);
                    using var cancellation = cancelled.Token.Register(() => ThreadPool.QueueUserWorkItem(_ =>
                    { try { server.WriteByte(CancelByte); server.Flush(); } catch { } }));
                    var reader = new NtfsSameSnapshotProtocol.Reader(server);
                    while (true)
                    {
                        var message = reader.Read(deadline.Token);
                        if (message.Stage is { } stage)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(new { revision = "same-snapshot-driver-v1", kind = "stage", stage = stage.ToString() })); Console.Out.Flush();
                            continue;
                        }
                        var report = message.Report ?? throw new NtfsSameSnapshotProtocolException();
                        report = RespectCancellation(report, cancelled.IsCancellationRequested);
                        WriteSummary(report, "None", true); return ExitCode(report);
                    }
                }
                finally { child?.Dispose(); }
            }
            finally { Console.CancelKeyPress -= handler; }
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223) { return DriverFailure("ElevationDenied", requested, !requested); }
        catch (OperationCanceledException) { return DriverFailure("Cancelled", requested, !requested); }
        catch (FastScanWireException) { return DriverFailure("InvalidScopeOrRequest", requested, !requested); }
        catch (NtfsSameSnapshotProtocolException) { return DriverFailure("ProtocolError", requested, !requested); }
        catch { return DriverFailure("DriverFailed", requested, !requested); }
    }

    internal static NtfsSameSnapshotReport RespectCancellation(NtfsSameSnapshotReport report, bool cancelled) => cancelled && report.CleanupConfirmed
        ? new() { Outcome = NtfsSameSnapshotOutcome.Cancelled, Code = NtfsSameSnapshotCode.Cancelled, CleanupConfirmed = true } : report;

    internal static int RunWorker(string[] args)
    {
        if (!TryWorkerArguments(args, out int pid, out long ticks) ||
            !ValidHost(Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name)) return 2;
        int result = 2; var thread = new Thread(() => result = Worker(args[1], pid, ticks));
        thread.SetApartmentState(ApartmentState.MTA); thread.Start(); thread.Join(); return result;
    }
    private static int Worker(string name, int pid, long ticks)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            if (parent.StartTime.ToUniversalTime().Ticks != ticks || !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 2;
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            pipe.Connect(30000);
            if (!FastScanBridge.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actual) || actual != pid) return 2;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
            using var stopRead = deadline.Token.Register(() => { try { pipe.Dispose(); } catch { } });
            string scope = FastScanWire.ReadRequest(pipe, deadline.Token);
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var command = Task.Run(() => { try { _ = pipe.ReadByte(); } catch { } cancelled.Cancel(); });
            try
            {
                var writer = new NtfsSameSnapshotProtocol.Writer(pipe);
                NtfsSameSnapshotReport report;
                try { report = NtfsSameSnapshotComparison.Execute(scope, cancelled.Token, stage => writer.Stage(stage, cancelled.Token)); }
                catch { report = new() { Outcome = NtfsSameSnapshotOutcome.Failed, Code = NtfsSameSnapshotCode.InternalFailure, CleanupConfirmed = false }; }
                report = RespectCancellation(report, cancelled.IsCancellationRequested);
                // Execute owns and settles every source operation. Neither a
                // process exit nor an exception is used as proof of cleanup.
                writer.Report(report, CancellationToken.None); return ExitCode(report);
            }
            finally { pipe.Dispose(); try { command.GetAwaiter().GetResult(); } catch { } }
        }
        catch { return 2; }
    }
    private static int DriverFailure(string code, bool requested, bool cleanup)
    {
        var report = new NtfsSameSnapshotReport { Outcome = NtfsSameSnapshotOutcome.Failed, Code = NtfsSameSnapshotCode.InternalFailure, CleanupConfirmed = cleanup };
        WriteSummary(report, code, false); return 1;
    }
    internal static void WriteSummary(NtfsSameSnapshotReport r, string driverCode, bool received)
    {
        NtfsSameSnapshotProtocol.Validate(r);
        if (driverCode is not ("None" or "InvalidInvocation" or "LaunchFailed" or "WorkerExitedBeforeRequest" or "ElevationDenied" or
            "Cancelled" or "InvalidScopeOrRequest" or "ProtocolError" or "DriverFailed")) throw new NtfsSameSnapshotProtocolException();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            revision = "same-snapshot-driver-v1", kind = "summary", outcome = r.Outcome.ToString(), code = r.Code.ToString(), driver_code = driverCode,
            worker_summary_received = received, cleanup_confirmed = r.CleanupConfirmed, comparison_complete = r.ComparisonComplete,
            same_source_verified = r.SameSourceVerified, budget_respected = r.BudgetRespected,
            included_scope_equal = r.IncludedScopeEqual, types_equal = r.TypesEqual, identities_equal = r.IdentitiesEqual,
            file_logical_equal = r.FileLogicalEqual, file_allocation_equal = r.FileAllocationEqual, file_links_equal = r.FileLinksEqual,
            allocation_known = r.AllocationKnown, fast_root_logical_valid = r.FastRootLogicalValid, fast_root_allocation_valid = r.FastRootAllocationValid,
            directory_root_logical_valid = r.DirectoryRootLogicalValid, directory_root_allocation_valid = r.DirectoryRootAllocationValid,
            fast_root_counts_valid = r.FastRootCountsValid, directory_root_counts_valid = r.DirectoryRootCountsValid,
            exclusions_equal = r.ExclusionsEqual, exclusion_details_complete = r.ExclusionDetailsComplete,
            native_probe_required = r.NativeProbeRequired, native_probe_attempted = r.NativeProbeAttempted, native_probe_complete = r.NativeProbeComplete,
            probe_branch = r.ProbeBranch.ToString(), fast_matches_native_identity = r.FastMatchesNativeIdentity, directory_matches_native_identity = r.DirectoryMatchesNativeIdentity,
            fast_matches_native_logical = r.FastMatchesNativeLogical, directory_matches_native_logical = r.DirectoryMatchesNativeLogical,
            fast_matches_native_allocation = r.FastMatchesNativeAllocation, directory_matches_native_allocation = r.DirectoryMatchesNativeAllocation,
            fast_matches_native_links = r.FastMatchesNativeLinks, directory_matches_native_links = r.DirectoryMatchesNativeLinks,
            fast_matches_native_standard_allocation = r.FastMatchesNativeStandardAllocation, directory_matches_native_standard_allocation = r.DirectoryMatchesNativeStandardAllocation,
            native_metadata_consistent = r.NativeMetadataConsistent, native_compression_observed = r.NativeCompressionObserved,
            product_end_to_end_measured = false, past_live_difference_explained = false, files_written = false
        })); Console.Out.Flush();
    }
}

internal sealed class NtfsSameSnapshotProtocolException : IOException
{ internal NtfsSameSnapshotProtocolException() : base("SameSnapshotProtocol") { } }

internal static class NtfsSameSnapshotProtocol
{
    internal const int MaximumBytes = 16384;
    private const uint Magic = 0x31535341;
    private const ushort Version = 1;
    private const ushort StageKind = 1, ReportKind = 2;
    internal sealed record Message(FastScanWireStage? Stage = null, NtfsSameSnapshotReport? Report = null);
    private static void Need([DoesNotReturnIf(false)] bool value) { if (!value) throw new NtfsSameSnapshotProtocolException(); }
    private static bool?[] MainFields(NtfsSameSnapshotReport r) => [r.IncludedScopeEqual, r.TypesEqual, r.IdentitiesEqual, r.FileLogicalEqual, r.FileAllocationEqual,
        r.FileLinksEqual, r.AllocationKnown, r.FastRootLogicalValid, r.FastRootAllocationValid, r.DirectoryRootLogicalValid, r.DirectoryRootAllocationValid,
        r.ExclusionsEqual, r.ExclusionDetailsComplete, r.FastRootCountsValid, r.DirectoryRootCountsValid];
    private static bool?[] Native(NtfsSameSnapshotReport r) => [r.FastMatchesNativeIdentity, r.DirectoryMatchesNativeIdentity, r.FastMatchesNativeLogical,
        r.DirectoryMatchesNativeLogical, r.FastMatchesNativeAllocation, r.DirectoryMatchesNativeAllocation, r.FastMatchesNativeLinks, r.DirectoryMatchesNativeLinks,
        r.FastMatchesNativeStandardAllocation, r.DirectoryMatchesNativeStandardAllocation, r.NativeMetadataConsistent, r.NativeCompressionObserved];
    internal static void Validate(NtfsSameSnapshotReport r)
    {
        Need(r != null && Enum.IsDefined(r.Outcome) && Enum.IsDefined(r.Code) && Enum.IsDefined(r.ProbeBranch));
        var main = MainFields(r); var native = Native(r);
        Need(!r.NativeProbeAttempted || r.NativeProbeRequired);
        Need(!r.NativeProbeComplete || r.NativeProbeAttempted && r.ProbeBranch != NtfsSameSnapshotProbeBranch.None);
        if (!r.NativeProbeAttempted) Need(!r.NativeProbeComplete && r.ProbeBranch == NtfsSameSnapshotProbeBranch.None && native.All(value => !value.HasValue));
        switch (r.Outcome)
        {
            case NtfsSameSnapshotOutcome.Matched:
                Need(r.Code == NtfsSameSnapshotCode.None && r.CleanupConfirmed && r.ComparisonComplete && r.SameSourceVerified && r.BudgetRespected && main.All(value => value == true) && !r.NativeProbeRequired); break;
            case NtfsSameSnapshotOutcome.Different:
                Need(r.Code == NtfsSameSnapshotCode.None && r.CleanupConfirmed && r.ComparisonComplete && r.SameSourceVerified && r.BudgetRespected && main.All(value => value.HasValue) && main.Any(value => value == false) &&
                    r.NativeProbeRequired && r.NativeProbeAttempted && r.NativeProbeComplete && native.Any(value => value.HasValue)); break;
            case NtfsSameSnapshotOutcome.Incomplete:
                Need(r.CleanupConfirmed && !r.ComparisonComplete && r.Code is NtfsSameSnapshotCode.BudgetExceeded or NtfsSameSnapshotCode.ComparisonIncomplete or NtfsSameSnapshotCode.NativeProbeIncomplete); break;
            case NtfsSameSnapshotOutcome.Cancelled:
                Need(r.Code == NtfsSameSnapshotCode.Cancelled && !r.SameSourceVerified && !r.ComparisonComplete && !r.NativeProbeRequired && main.All(value => !value.HasValue) && native.All(value => !value.HasValue)); break;
            case NtfsSameSnapshotOutcome.Failed:
                Need(r.Code is NtfsSameSnapshotCode.InvalidScope or NtfsSameSnapshotCode.SnapshotUnavailable or NtfsSameSnapshotCode.TraversalFailed or
                    NtfsSameSnapshotCode.SourceChanged or NtfsSameSnapshotCode.InternalFailure or NtfsSameSnapshotCode.CleanupUnconfirmed);
                Need(!r.SameSourceVerified && !r.ComparisonComplete && !r.NativeProbeRequired && main.All(value => !value.HasValue) && native.All(value => !value.HasValue));
                Need(r.Code != NtfsSameSnapshotCode.CleanupUnconfirmed || !r.CleanupConfirmed); break;
            default: throw new NtfsSameSnapshotProtocolException();
        }
    }
    private static void Bool(BinaryWriter w, bool value) => w.Write(value);
    private static bool Bool(BinaryReader r) { byte value = r.ReadByte(); Need(value <= 1); return value == 1; }
    private static bool? Nullable(BinaryReader r) { byte value = r.ReadByte(); Need(value <= 2); return value == 0 ? null : value == 2; }
    internal static byte[] EncodeReport(NtfsSameSnapshotReport r)
    {
        Validate(r); using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((byte)r.Outcome); writer.Write((byte)r.Code); Bool(writer, r.CleanupConfirmed); Bool(writer, r.ComparisonComplete);
        Bool(writer, r.SameSourceVerified); Bool(writer, r.BudgetRespected);
        foreach (var value in MainFields(r)) writer.Write(value.HasValue ? value.Value ? (byte)2 : (byte)1 : (byte)0);
        Bool(writer, r.NativeProbeRequired); Bool(writer, r.NativeProbeAttempted); Bool(writer, r.NativeProbeComplete); writer.Write((byte)r.ProbeBranch);
        foreach (var value in Native(r)) writer.Write(value.HasValue ? value.Value ? (byte)2 : (byte)1 : (byte)0);
        return stream.ToArray();
    }
    internal static NtfsSameSnapshotReport DecodeReport(byte[] payload)
    {
        try
        {
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream, Encoding.UTF8, true);
            var outcome = (NtfsSameSnapshotOutcome)r.ReadByte(); var code = (NtfsSameSnapshotCode)r.ReadByte(); bool cleanup = Bool(r), comparison = Bool(r);
            bool sameSource = Bool(r), budget = Bool(r);
            bool?[] main = new bool?[15], native = new bool?[12]; for (int i = 0; i < main.Length; i++) main[i] = Nullable(r);
            bool required = Bool(r), attempted = Bool(r), complete = Bool(r); var branch = (NtfsSameSnapshotProbeBranch)r.ReadByte();
            for (int i = 0; i < native.Length; i++) native[i] = Nullable(r); Need(stream.Position == stream.Length);
            var report = new NtfsSameSnapshotReport
            {
                Outcome = outcome, Code = code, CleanupConfirmed = cleanup, ComparisonComplete = comparison,
                SameSourceVerified = sameSource, BudgetRespected = budget,
                IncludedScopeEqual = main[0], TypesEqual = main[1], IdentitiesEqual = main[2], FileLogicalEqual = main[3], FileAllocationEqual = main[4],
                FileLinksEqual = main[5], AllocationKnown = main[6], FastRootLogicalValid = main[7], FastRootAllocationValid = main[8],
                DirectoryRootLogicalValid = main[9], DirectoryRootAllocationValid = main[10], ExclusionsEqual = main[11], ExclusionDetailsComplete = main[12],
                FastRootCountsValid = main[13], DirectoryRootCountsValid = main[14],
                NativeProbeRequired = required, NativeProbeAttempted = attempted, NativeProbeComplete = complete, ProbeBranch = branch,
                FastMatchesNativeIdentity = native[0], DirectoryMatchesNativeIdentity = native[1], FastMatchesNativeLogical = native[2], DirectoryMatchesNativeLogical = native[3],
                FastMatchesNativeAllocation = native[4], DirectoryMatchesNativeAllocation = native[5], FastMatchesNativeLinks = native[6], DirectoryMatchesNativeLinks = native[7],
                FastMatchesNativeStandardAllocation = native[8], DirectoryMatchesNativeStandardAllocation = native[9], NativeMetadataConsistent = native[10], NativeCompressionObserved = native[11]
            }; Validate(report); return report;
        }
        catch { throw new NtfsSameSnapshotProtocolException(); }
    }
    internal sealed class Writer(Stream stream)
    {
        private readonly object gate = new(); private int bytes; private bool terminal;
        internal void Stage(FastScanWireStage value, CancellationToken token = default)
        { Need(Enum.IsDefined(value)); Send(StageKind, [(byte)value], false, token); }
        internal void Report(NtfsSameSnapshotReport value, CancellationToken token = default) => Send(ReportKind, EncodeReport(value), true, token);
        private void Send(ushort kind, byte[] payload, bool last, CancellationToken token)
        {
            lock (gate)
            {
                token.ThrowIfCancellationRequested(); Need(!terminal && payload.Length <= MaximumBytes - 12 && bytes <= MaximumBytes - payload.Length - 12);
                Span<byte> header = stackalloc byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(header, Magic); BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
                BinaryPrimitives.WriteUInt16LittleEndian(header[6..], kind); BinaryPrimitives.WriteInt32LittleEndian(header[8..], payload.Length);
                stream.Write(header); stream.Write(payload); stream.Flush(); bytes += header.Length + payload.Length; terminal = last;
            }
        }
    }
    internal sealed class Reader(Stream stream)
    {
        private int bytes; private bool terminal;
        internal Message Read(CancellationToken token = default)
        {
            try
            {
                token.ThrowIfCancellationRequested(); Need(!terminal); Span<byte> header = stackalloc byte[12]; stream.ReadExactly(header);
                int size = BinaryPrimitives.ReadInt32LittleEndian(header[8..]); ushort kind = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
                Need(BinaryPrimitives.ReadUInt32LittleEndian(header) == Magic && BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) == Version &&
                    kind is StageKind or ReportKind && size is >= 0 and <= MaximumBytes - 12 && bytes <= MaximumBytes - size - 12);
                byte[] payload = new byte[size]; stream.ReadExactly(payload); bytes += size + 12; token.ThrowIfCancellationRequested();
                if (kind == StageKind) { Need(payload.Length == 1 && Enum.IsDefined((FastScanWireStage)payload[0])); return new(Stage: (FastScanWireStage)payload[0]); }
                var report = DecodeReport(payload); terminal = true; return new(Report: report);
            }
            catch { terminal = true; token.ThrowIfCancellationRequested(); throw new NtfsSameSnapshotProtocolException(); }
        }
    }
}
