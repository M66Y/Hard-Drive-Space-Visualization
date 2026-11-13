using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using DiskAtlas.NtfsPrototype;

namespace DiskAtlas;

// The elevated process accepts one bounded request from its verified, same-binary parent.
// Paths, identities and result nodes travel only through the private in-memory pipe.
internal static class FastScanBridge
{
    internal const string WorkerArgument = "--ntfs-scan-worker";
    internal const string TimingWorkerArgument = "--ntfs-timing-worker";
    private const string PipePrefix = "AsukaLens.Ntfs.";
    private const byte CancelCommand = 0x43;
    private static int cleanupUnconfirmed;

    internal static ScanResult Scan(string path, IProgress<ScanProgress>? progress, CancellationToken token) =>
        ScanCore(path, progress, token, false, null);

    internal static ScanResult ScanForTiming(string path, IProgress<ScanProgress>? progress, CancellationToken token,
        Action<FastScanWireStage>? observeStage = null)
    {
        if (!IsTimingHost(Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name))
            throw new FastScanUnavailableException(FastScanWireError.Unsupported, true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        return ScanCore(path, progress, timeout.Token, true, observeStage);
    }

    internal static bool IsTimingHost(string? executable, string? assembly) =>
        string.Equals(Path.GetFileName(executable), "DiskAtlas.Tests.exe", StringComparison.OrdinalIgnoreCase) && assembly == "DiskAtlas.Tests";

    private static ScanResult ScanCore(string path, IProgress<ScanProgress>? progress, CancellationToken token, bool timing,
        Action<FastScanWireStage>? observeStage)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref cleanupUnconfirmed) != 0)
            throw new FastScanUnavailableException(FastScanWireError.CleanupUnconfirmed, false);
        try { path = FastScanWire.NormalizeRoot(path); }
        catch (FastScanWireException) { throw new FastScanUnavailableException(FastScanWireError.Unsupported, true); }
        string executable = Environment.ProcessPath ?? throw new FastScanUnavailableException(FastScanWireError.Unsupported, true);
        if (!Path.GetFileName(executable).Equals("DiskAtlas.exe", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(executable).Equals("AsukaLens.exe", StringComparison.OrdinalIgnoreCase) &&
            !(Path.GetFileName(executable).Equals("DiskAtlas.Tests.exe", StringComparison.OrdinalIgnoreCase) &&
                System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "DiskAtlas.Tests"))
            throw new FastScanUnavailableException(FastScanWireError.Unsupported, true);
        string pipeName = PipePrefix + Guid.NewGuid().ToString("N");
        using var parent = Process.GetCurrentProcess();
        using var server = OpenServer(pipeName);
        Process? child = null;
        bool requested = false;
        bool terminalCleanupConfirmed = false;
        var timer = Stopwatch.StartNew();
        // The scan cancels after ten minutes; keep the existing two-minute
        // transport grace so its cleanup acknowledgement can still arrive.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var close = deadline.Token.Register(() => { try { server.Dispose(); } catch { } });
        try
        {
            progress?.Report(new(0, 0, 0, "等待管理员授权", ScanPhase.Preparing, Message: "仅本次快扫使用短时管理员助手。"));
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add(timing ? TimingWorkerArgument : WorkerArgument); start.ArgumentList.Add(pipeName);
            start.ArgumentList.Add(parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                if (!timing) child = Process.Start(start);
                else
                {
                    // Test timing must not wait indefinitely for the consent UI.
                    // An abandoned launch receives no scope: its private server
                    // closes here, so a late helper cannot start a source.
                    var launch = Task.Run(() => Process.Start(start));
                    try { child = launch.WaitAsync(token).GetAwaiter().GetResult(); }
                    catch
                    {
                        _ = launch.ContinueWith(task => { _ = task.Exception; if (task.Status == TaskStatus.RanToCompletion) task.Result?.Dispose(); },
                            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                        throw;
                    }
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            { token.ThrowIfCancellationRequested(); throw new FastScanUnavailableException(FastScanWireError.PermissionDenied, true); }
            if (child == null) throw new FastScanUnavailableException(FastScanWireError.PermissionDenied, true);
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            connection.CancelAfter(TimeSpan.FromMinutes(2));
            while (true)
            {
                var waiting = server.WaitForConnectionAsync(connection.Token);
                while (!waiting.IsCompleted)
                {
                    if (child.HasExited) throw new FastScanUnavailableException(FastScanWireError.ProtocolError, true);
                    Task.WhenAny(waiting, Task.Delay(100, connection.Token)).GetAwaiter().GetResult();
                    connection.Token.ThrowIfCancellationRequested();
                }
                waiting.GetAwaiter().GetResult();
                if (GetNamedPipeClientProcessId(server.SafePipeHandle, out uint pid) && pid == child.Id) break;
                server.Disconnect();
            }
            token.ThrowIfCancellationRequested();
            requested = true;
            FastScanWire.WriteRequest(server, path, token);
            server.Flush();
            using var cancel = token.Register(() =>
            {
                ThreadPool.QueueUserWorkItem(_ => { try { server.WriteByte(CancelCommand); server.Flush(); } catch { } });
            });
            var reader = new FastScanWireReader(server, path);
            while (true)
            {
                var message = reader.Read(deadline.Token);
                if (message.Kind == FastScanWireEventKind.Status && message.Stage is { } stage)
                { observeStage?.Invoke(stage); ReportStage(progress, stage); }
                else if (message.Kind == FastScanWireEventKind.Progress && message.Progress != null)
                    progress?.Report(message.Progress);
                else if (message.Kind == FastScanWireEventKind.Failure)
                {
                    if (!message.CleanupConfirmed) throw new FastScanUnavailableException(FastScanWireError.CleanupUnconfirmed, false);
                    terminalCleanupConfirmed = true;
                    token.ThrowIfCancellationRequested();
                    if (message.Error == FastScanWireError.Cancelled) throw new OperationCanceledException();
                    throw new FastScanUnavailableException(message.Error ?? FastScanWireError.ProtocolError, true, message.Context);
                }
                else if (message.Kind == FastScanWireEventKind.Completed && message.Result is { } result)
                {
                    terminalCleanupConfirmed = true;
                    token.ThrowIfCancellationRequested();
                    return FromWireResult(result, timer.Elapsed);
                }
            }
        }
        catch (FastScanUnavailableException ex)
        {
            if (!ex.CleanupConfirmed) Volatile.Write(ref cleanupUnconfirmed, 1);
            throw;
        }
        catch (OperationCanceledException) when (!requested || terminalCleanupConfirmed) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or OperationCanceledException or InvalidDataException or ObjectDisposedException)
        {
            if (requested) Volatile.Write(ref cleanupUnconfirmed, 1);
            throw new FastScanUnavailableException(FastScanWireError.ProtocolError, !requested);
        }
        finally { child?.Dispose(); }
    }

    internal static ScanResult FromWireResult(FastScanWireResult result, TimeSpan elapsed) =>
        new(result.Root, elapsed, result.ExcludedReparseEntries, result.Issues)
        { Source = new(result.CapturedAt, result.ExcludedReparseEntries) };

    private static NamedPipeServerStream OpenServer(string name)
    {
        try { return CreatePipe(name); }
        catch { throw new FastScanUnavailableException(FastScanWireError.ProtocolError, true); }
    }

    internal static NamedPipeServerStream CreatePipe(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new UnauthorizedAccessException();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(sid);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Explicit SID ACL permits this user's elevated token too. CurrentUserOnly also
        // compares elevation on Windows and would reject our intended peer.
        if (!ValidPipeName(name)) throw new ArgumentException("Invalid private pipe name", nameof(name));
        byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        SafePipeHandle? handle = null;
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
            // DUPLEX | OVERLAPPED | FIRST_PIPE_INSTANCE | WRITE_DAC. Mode 8 is
            // PIPE_REJECT_REMOTE_CLIENTS, independent of the current-user ACL.
            handle = CreateNamedPipeW(@"\\.\pipe\" + name, 3u | 0x40000000u | 0x00080000u | 0x00040000u,
                8, 1, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var stream = new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
            handle = null; return stream;
        }
        finally { handle?.Dispose(); pinned.Free(); }
    }

    internal static bool IsWorkerRequest(string[] args) => args.Length > 0 && args[0] == WorkerArgument;
    internal static bool IsTimingWorkerRequest(string[] args) => args.Length > 0 && args[0] == TimingWorkerArgument;

    internal static int RunTimingWorker(string[] args)
    {
        if (!IsTimingHost(Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name) ||
            args.Length != 4 || args[0] != TimingWorkerArgument) return 2;
        string[] invocation = args.ToArray(); invocation[0] = WorkerArgument;
        return RunWorker(invocation); // Deliberately no test-only full Native audit.
    }

    internal static int RunWorker(string[] args,
        Action<NtfsPersonalSnapshotSource, DiskEntry, CancellationToken>? beforeCleanupValidation = null)
    {
        if (args.Length != 4 || args[0] != WorkerArgument || !ValidPipeName(args[1]) || !int.TryParse(args[2], out int parentId) || parentId <= 0 ||
            !long.TryParse(args[3], out long parentTicks) || parentTicks <= 0) return 2;
        int exit = 2;
        var thread = new Thread(() => exit = WorkerCore(args[1], parentId, parentTicks, beforeCleanupValidation));
        thread.SetApartmentState(ApartmentState.MTA); thread.Start(); thread.Join();
        return exit;
    }

    private static int WorkerCore(string pipeName, int parentId, long parentTicks,
        Action<NtfsPersonalSnapshotSource, DiskEntry, CancellationToken>? beforeCleanupValidation)
    {
        try
        {
            using var parent = Process.GetProcessById(parentId);
            string own = Environment.ProcessPath ?? "";
            if (parent.StartTime.ToUniversalTime().Ticks != parentTicks ||
                !string.Equals(parent.MainModule?.FileName, own, StringComparison.OrdinalIgnoreCase)) return 2;
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            pipe.Connect(30000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != parentId) return 2;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var close = timeout.Token.Register(() => { try { pipe.Dispose(); } catch { } });
            string root = FastScanWire.ReadRequest(pipe, timeout.Token);
            using var cancellation = new CancellationTokenSource();
            var listening = Task.Run(() =>
            {
                try { _ = pipe.ReadByte(); } catch { }
                cancellation.Cancel();
            });
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, timeout.Token);
            bool sourceClosed = false;
            try
            {
                var result = NtfsPersonalScan.Run(root, stage => FastScanWire.WriteStatus(pipe, stage, linked.Token), linked.Token, beforeCleanupValidation);
                sourceClosed = true;
                // The scan method returns only after confirmed source cleanup.
                FastScanWire.WriteResult(pipe, result.Root, result.Elapsed, result.CapturedAt, CancellationToken.None,
                    result.ExcludedReparseEntries, result.Exclusions);
                pipe.Flush(); return 0;
            }
            catch (FastScanUnavailableException ex)
            {
                FastScanWire.WriteFailure(pipe, ex.Code, ex.CleanupConfirmed, CancellationToken.None, ex.Context);
                pipe.Flush(); return 1;
            }
            catch (OperationCanceledException)
            {
                FastScanWire.WriteFailure(pipe, FastScanWireError.Cancelled, true, CancellationToken.None);
                pipe.Flush(); return 1;
            }
            catch
            {
                FastScanWire.WriteFailure(pipe, FastScanWireError.InternalError, sourceClosed, CancellationToken.None);
                pipe.Flush(); return 1;
            }
            finally
            {
                // Unblock the command reader without leaving a thread or user data behind.
                pipe.Dispose();
                try { listening.GetAwaiter().GetResult(); } catch { }
            }
        }
        catch { return 2; } // No private exception text reaches a dialog, stdout or a log.
    }

    internal static bool ValidPipeName(string value) => value.Length == PipePrefix.Length + 32 &&
        value.StartsWith(PipePrefix, StringComparison.Ordinal) && value[PipePrefix.Length..].All(Uri.IsHexDigit);

    internal static void ReportStage(IProgress<ScanProgress>? progress, FastScanWireStage stage)
    {
        string message = stage switch
        {
            FastScanWireStage.Snapshot => "建立只读快照",
            FastScanWireStage.Discovering => "核对目录范围",
            FastScanWireStage.Reading => "读取 NTFS 元信息",
            FastScanWireStage.Supplementing => "补查特殊文件大小",
            FastScanWireStage.Building => "整理空间分布",
            FastScanWireStage.Cleanup => "清理本次快照",
            _ => "准备 NTFS 快速扫描"
        };
        progress?.Report(new(0, 0, 0, message,
            stage is FastScanWireStage.Building or FastScanWireStage.Cleanup ? ScanPhase.Finalizing : ScanPhase.Preparing,
            Message: message + "；结果为快照时点，完成前保留原结果。"));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maximumInstances,
        uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes security);
}

internal sealed class FastScanUnavailableException(FastScanWireError code, bool cleanupConfirmed, FastScanFailureContext? context = null) : IOException(
    cleanupConfirmed ? FastScanCoordinator.Reason(code, context) : FastScanCoordinator.Reason(FastScanWireError.CleanupUnconfirmed))
{
    internal FastScanWireError Code { get; } = code;
    internal bool CleanupConfirmed { get; } = cleanupConfirmed;
    internal FastScanFailureContext? Context { get; } = context;
}
