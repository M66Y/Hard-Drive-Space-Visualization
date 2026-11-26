using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DiskAtlas;

internal static class NtfsNamespaceAncestorDriver
{
    internal const string Argument = "--ntfs-namespace-ancestor-live", WorkerArgument = "--ntfs-namespace-ancestor-worker";
    internal static bool Requested(string[] args) => args.Length > 0 && args[0] is Argument or WorkerArgument;
    internal static bool ValidParent(string[] args) => args.Length == 1 && args[0] == Argument;
    internal static bool ParseWorker(string[] args, out int pid, out long ticks)
    {
        pid = 0; ticks = 0;
        return args.Length == 4 && args[0] == WorkerArgument && FastScanBridge.ValidPipeName(args[1]) &&
            int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out pid) && pid > 0 &&
            long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) && ticks > 0 && ticks <= DateTime.MaxValue.Ticks;
    }
    internal static int Run(string[] args)
    {
        if (!FastScanBridge.IsTimingHost(Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name)) return 2;
        if (args[0] == WorkerArgument)
        {
            if (!ParseWorker(args, out int pid, out long ticks)) return 2;
            int result = 2;
            var worker = new Thread(() => result = Worker(args[1], pid, ticks));
            worker.SetApartmentState(ApartmentState.MTA); worker.Start(); worker.Join(); return result;
        }
        return Parent(args);
    }
    internal static NamespaceAncestorReport RespectCancellation(NamespaceAncestorReport report, bool canceled) => canceled && report.CleanupConfirmed ?
        report with { Outcome = NamespaceAncestorOutcome.Cancelled, Code = NamespaceAncestorCode.Cancelled, SameSourceVerified = false,
            ComparisonComplete = false, Checks = NamespaceAncestorChecks.None } : report;
    private static int Exit(NamespaceAncestorReport report) => report.Outcome switch { NamespaceAncestorOutcome.Matched => 0, NamespaceAncestorOutcome.Different => 3, _ => 1 };
    private static int Parent(string[] args)
    {
        bool requested = false;
        if (!ValidParent(args)) return Failure("InvalidInvocation", false);
        try
        {
            string scope = NtfsPrototype.NtfsPersonalAcceptanceDriver.ReadScope(Console.In);
            using var canceled = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; canceled.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                string name = "AsukaLens.Ntfs." + Guid.NewGuid().ToString("N");
                using var server = FastScanBridge.CreatePipe(name);
                using var parent = Process.GetCurrentProcess(); Process? child = null;
                try
                {
                    Console.Error.WriteLine("Elevation");
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                    start.ArgumentList.Add(WorkerArgument); start.ArgumentList.Add(name);
                    start.ArgumentList.Add(parent.Id.ToString(CultureInfo.InvariantCulture));
                    start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
                    using var launchStop = CancellationTokenSource.CreateLinkedTokenSource(canceled.Token);
                    launchStop.CancelAfter(TimeSpan.FromMinutes(5));
                    var launch = Task.Run(() => Process.Start(start));
                    try { child = launch.WaitAsync(launchStop.Token).GetAwaiter().GetResult(); }
                    catch
                    {
                        _ = launch.ContinueWith(t => { _ = t.Exception; if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose(); }, TaskScheduler.Default);
                        throw;
                    }
                    if (child == null) return Failure("LaunchFailed", false);
                    using var connectStop = CancellationTokenSource.CreateLinkedTokenSource(canceled.Token);
                    connectStop.CancelAfter(TimeSpan.FromMinutes(2));
                    using var stopConnect = connectStop.Token.Register(() => { try { server.Dispose(); } catch { } });
                    while (true)
                    {
                        var waiting = server.WaitForConnectionAsync(connectStop.Token);
                        while (!waiting.IsCompleted)
                        {
                            if (child.HasExited) return Failure("WorkerExited", false);
                            Task.WhenAny(waiting, Task.Delay(100, connectStop.Token)).GetAwaiter().GetResult(); connectStop.Token.ThrowIfCancellationRequested();
                        }
                        waiting.GetAwaiter().GetResult();
                        if (FastScanBridge.GetNamedPipeClientProcessId(server.SafePipeHandle, out uint actual) && actual == child.Id) break;
                        server.Disconnect();
                    }
                    // Reading budgets start after elevation and peer binding.
                    // A late/abandoned elevation never receives a scope.
                    canceled.CancelAfter(TimeSpan.FromMinutes(10));
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
                    using var stopRead = deadline.Token.Register(() => { try { server.Dispose(); } catch { } });
                    canceled.Token.ThrowIfCancellationRequested(); requested = true;
                    FastScanWire.WriteRequest(server, scope, canceled.Token);
                    using var sendCancel = canceled.Token.Register(() => ThreadPool.QueueUserWorkItem(_ => { try { server.WriteByte(0x43); server.Flush(); } catch { } }));
                    var reader = new NamespaceAncestorProtocol.Reader(server);
                    while (true)
                    {
                        var message = reader.Read(deadline.Token);
                        if (message.Stage is { } stage) { Console.Error.WriteLine(stage); continue; }
                        var report = RespectCancellation(message.Report ?? throw new InvalidDataException(), canceled.IsCancellationRequested);
                        Summary(report, "None", true); return Exit(report);
                    }
                }
                finally { child?.Dispose(); }
            }
            finally { Console.CancelKeyPress -= handler; }
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { return Failure("ElevationDenied", requested); }
        catch (OperationCanceledException) { return Failure("Cancelled", requested); }
        catch (FastScanWireException) { return Failure("InvalidScopeOrRequest", requested); }
        catch { return Failure("DriverFailed", requested); }
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
            using var canceled = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var command = Task.Run(() => { try { _ = pipe.ReadByte(); } catch { } canceled.Cancel(); });
            try
            {
                var writer = new NamespaceAncestorProtocol.Writer(pipe);
                var report = NtfsNamespaceAncestorComparison.Execute(scope, canceled.Token, stage => writer.Stage(stage, canceled.Token));
                report = RespectCancellation(report, canceled.IsCancellationRequested);
                // Source owner has completed cleanup before any terminal result.
                writer.Report(report, CancellationToken.None); return Exit(report);
            }
            finally { pipe.Dispose(); try { command.GetAwaiter().GetResult(); } catch { } }
        }
        catch { return 2; }
    }
    private static int Failure(string code, bool requested)
    {
        Summary(new() { CleanupConfirmed = !requested }, code, false); return 1;
    }
    internal static void Summary(NamespaceAncestorReport r, string driverCode, bool received)
    {
        NamespaceAncestorProtocol.Validate(r);
        if (driverCode is not ("None" or "InvalidInvocation" or "LaunchFailed" or "WorkerExited" or "ElevationDenied" or "Cancelled" or "InvalidScopeOrRequest" or "DriverFailed"))
            throw new InvalidDataException();
        if (received != (driverCode == "None") || !received && r.Outcome != NamespaceAncestorOutcome.Failed)
            throw new InvalidDataException();
        // This output is private. Public evidence must omit timings and counts.
        Console.WriteLine(JsonSerializer.Serialize(new { revision = "namespace-ancestor-live-v1", kind = "summary", outcome = r.Outcome.ToString(), code = r.Code.ToString(),
            driver_code = driverCode, worker_summary_received = received, cleanup_confirmed = r.CleanupConfirmed,
            same_source_verified = r.SameSourceVerified, comparison_complete = r.ComparisonComplete, checks = (byte)r.Checks,
            snapshot_ms = r.SnapshotMilliseconds, original_ms = r.Original?.Milliseconds, candidate_ms = r.Candidate?.Milliseconds,
            comparison_ms = r.ComparisonMilliseconds, cleanup_ms = r.CleanupMilliseconds,
            original = Measurement(r.Original), candidate = Measurement(r.Candidate), raw_read = false, special_lookup = false, product_end_to_end = false }));
    }
    private static object? Measurement(NamespaceAncestorMeasurement? m) => m == null ? null : new
    { entries = m.Entries, opened = m.Opened, completed = m.Completed, pages = m.Pages, queries = m.Queries, handles_settled = m.HandlesSettled };
}

internal static class NamespaceAncestorProtocol
{
    internal const int MaximumBytes = 4096;
    private const uint Magic = 0x31414e41;
    internal sealed record Message(NamespaceAncestorStage? Stage = null, NamespaceAncestorReport? Report = null);
    private static void Need(bool condition) { if (!condition) throw new InvalidDataException("NamespaceAncestorProtocol"); }
    internal static void Validate(NamespaceAncestorReport r)
    {
        Need(Enum.IsDefined(r.Outcome) && Enum.IsDefined(r.Code) && (r.Checks & ~NamespaceAncestorChecks.All) == 0);
        foreach (long value in new[] { r.SnapshotMilliseconds, r.ComparisonMilliseconds, r.CleanupMilliseconds }) Need(value is >= 0 and <= 720000);
        foreach (var m in new[] { r.Original, r.Candidate }) if (m != null)
            Need(m.Milliseconds is >= 0 and <= 720000 && m.Entries is >= 1 and <= 1048576 && m.Opened >= 1 && m.Opened <= m.Entries &&
                m.Completed == m.Opened && m.Pages >= 0 && m.Queries >= m.Completed && m.Pages <= m.Queries && m.Queries <= 4194305 &&
                (long)m.Pages + m.Completed == m.Queries && m.HandlesSettled);
        bool complete = r.Outcome is NamespaceAncestorOutcome.Matched or NamespaceAncestorOutcome.Different;
        if (complete) Need(r.Code == NamespaceAncestorCode.None && r.CleanupConfirmed && r.SameSourceVerified && r.ComparisonComplete &&
            r.Original != null && r.Candidate != null && (r.Outcome == NamespaceAncestorOutcome.Matched ? r.Checks == NamespaceAncestorChecks.All : r.Checks != NamespaceAncestorChecks.All));
        else Need(!r.SameSourceVerified && !r.ComparisonComplete && r.Checks == NamespaceAncestorChecks.None &&
            (r.Outcome == NamespaceAncestorOutcome.Cancelled ? r.Code == NamespaceAncestorCode.Cancelled && r.CleanupConfirmed : r.Code is not (NamespaceAncestorCode.None or NamespaceAncestorCode.Cancelled)));
        if (r.Code == NamespaceAncestorCode.CleanupUnconfirmed) Need(!r.CleanupConfirmed);
        if (complete && r.Original != null && r.Candidate != null)
        {
            if ((r.Checks & (NamespaceAncestorChecks.Paths | NamespaceAncestorChecks.Counts)) != 0) Need(r.Original.Entries == r.Candidate.Entries);
            if ((r.Checks & NamespaceAncestorChecks.Coverage) != 0) Need(r.Original.Opened == r.Candidate.Opened);
        }
    }
    private static bool Bool(BinaryReader reader) { byte b = reader.ReadByte(); Need(b <= 1); return b == 1; }
    internal static byte[] Encode(NamespaceAncestorReport r)
    {
        Validate(r); using var stream = new MemoryStream(); using var w = new BinaryWriter(stream, Encoding.UTF8, true);
        w.Write((byte)r.Outcome); w.Write((byte)r.Code); w.Write(r.CleanupConfirmed); w.Write(r.SameSourceVerified); w.Write(r.ComparisonComplete); w.Write((byte)r.Checks);
        w.Write(r.SnapshotMilliseconds); w.Write(r.ComparisonMilliseconds); w.Write(r.CleanupMilliseconds);
        foreach (var m in new[] { r.Original, r.Candidate })
        {
            w.Write(m != null); if (m == null) continue;
            w.Write(m.Milliseconds); w.Write(m.Entries); w.Write(m.Opened); w.Write(m.Completed); w.Write(m.Pages); w.Write(m.Queries); w.Write(m.HandlesSettled);
        }
        return stream.ToArray();
    }
    internal static NamespaceAncestorReport Decode(byte[] payload)
    {
        try
        {
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream, Encoding.UTF8, true);
            var outcome = (NamespaceAncestorOutcome)r.ReadByte(); var code = (NamespaceAncestorCode)r.ReadByte();
            bool cleanup = Bool(r), source = Bool(r), complete = Bool(r); var checks = (NamespaceAncestorChecks)r.ReadByte();
            long snapshot = r.ReadInt64(), compare = r.ReadInt64(), closing = r.ReadInt64();
            NamespaceAncestorMeasurement? ReadMeasurement() => !Bool(r) ? null : new(r.ReadInt64(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), Bool(r));
            var report = new NamespaceAncestorReport { Outcome = outcome, Code = code, CleanupConfirmed = cleanup, SameSourceVerified = source, ComparisonComplete = complete, Checks = checks,
                SnapshotMilliseconds = snapshot, ComparisonMilliseconds = compare, CleanupMilliseconds = closing, Original = ReadMeasurement(), Candidate = ReadMeasurement() };
            Need(stream.Position == stream.Length); Validate(report); return report;
        }
        catch { throw new InvalidDataException("NamespaceAncestorProtocol"); }
    }
    internal sealed class Writer(Stream stream)
    {
        private int bytes; private bool terminal;
        internal void Stage(NamespaceAncestorStage stage, CancellationToken token = default) { token.ThrowIfCancellationRequested(); Need(Enum.IsDefined(stage)); Send(1, [(byte)stage], false, token); }
        internal void Report(NamespaceAncestorReport report, CancellationToken token = default) { token.ThrowIfCancellationRequested(); Send(2, Encode(report), true, token); }
        private void Send(ushort kind, byte[] payload, bool last, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Need(!terminal && payload.Length <= MaximumBytes - 12 && bytes <= MaximumBytes - payload.Length - 12);
            Span<byte> header = stackalloc byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 1); BinaryPrimitives.WriteUInt16LittleEndian(header[6..], kind); BinaryPrimitives.WriteInt32LittleEndian(header[8..], payload.Length);
            stream.Write(header); stream.Write(payload); stream.Flush(); bytes += payload.Length + 12; terminal = last;
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
                Need(BinaryPrimitives.ReadUInt32LittleEndian(header) == Magic && BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) == 1 && kind is 1 or 2 &&
                    size >= 0 && size <= MaximumBytes - 12 && bytes <= MaximumBytes - size - 12);
                byte[] payload = new byte[size]; stream.ReadExactly(payload); token.ThrowIfCancellationRequested(); bytes += size + 12;
                if (kind == 1) { Need(payload.Length == 1 && Enum.IsDefined((NamespaceAncestorStage)payload[0])); return new(Stage: (NamespaceAncestorStage)payload[0]); }
                var report = Decode(payload); terminal = true; return new(Report: report);
            }
            catch { terminal = true; token.ThrowIfCancellationRequested(); throw new InvalidDataException("NamespaceAncestorProtocol"); }
        }
    }
}
