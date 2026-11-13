using System.IO.Pipes;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas;

// Local, same-user pipes and synthetic metadata only. Invalid worker arguments
// return before a worker thread, source, elevated process or volume can open.
internal static class FastScanBridgeContractTests
{
    private const string Prefix = "AsukaLens.Ntfs.";
    private const string RootPath = @"B:\AsukaLens-Synthetic";
    private static string Name() => Prefix + Guid.NewGuid().ToString("N");
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Fast scan bridge contract failed: " + name); passed++; Console.WriteLine("PASS fast scan bridge " + name); }
        string valid = Name();
        Check(FastScanBridge.ValidPipeName(valid), "generated private pipe accepted");
        Check(FastScanBridge.ValidPipeName(Prefix + new string('A', 32)), "hex pipe suffix accepted");
        foreach (string invalid in new[] { "", Prefix, Prefix + new string('a', 31), Prefix + new string('a', 33), Prefix + new string('g', 32),
            @"\\.\pipe\" + valid, "asukalens.Ntfs." + new string('a', 32), Prefix + new string('a', 31) + " ", Prefix + new string('a', 31) + "/" })
            Check(!FastScanBridge.ValidPipeName(invalid), "unexpected pipe name rejected");
        Check(!FastScanBridge.IsWorkerRequest([]) && !FastScanBridge.IsWorkerRequest(["--other"]) &&
            !FastScanBridge.IsWorkerRequest(["--other", FastScanBridge.WorkerArgument]), "ordinary args not worker mode");
        Check(FastScanBridge.IsWorkerRequest([FastScanBridge.WorkerArgument]) &&
            FastScanBridge.IsWorkerRequest([FastScanBridge.WorkerArgument, valid, "1", "1"]), "worker mode routed before application setup");
        foreach (string[] args in new string[][]
        {
            [], [FastScanBridge.WorkerArgument], [FastScanBridge.WorkerArgument, valid],
            [FastScanBridge.WorkerArgument, "bad", "1", "1"], ["--wrong-verb", valid, "1", "1"],
            [FastScanBridge.WorkerArgument, valid, "0", "1"], [FastScanBridge.WorkerArgument, valid, "-1", "1"],
            [FastScanBridge.WorkerArgument, valid, "not-number", "1"], [FastScanBridge.WorkerArgument, valid, "2147483648", "1"],
            [FastScanBridge.WorkerArgument, valid, "1", "0"], [FastScanBridge.WorkerArgument, valid, "1", "-1"],
            [FastScanBridge.WorkerArgument, valid, "1", "bad"], [FastScanBridge.WorkerArgument, valid, "1", "9223372036854775808"],
            [FastScanBridge.WorkerArgument, valid, "1", "1", "extra"]
        }) Check(FastScanBridge.RunWorker(args) == 2, "malformed worker invocation rejected before launch");

        using (var server = FastScanBridge.CreatePipe(Name()))
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var sid = identity.User!; var security = server.GetAccessControl();
            Check(security.AreAccessRulesProtected, "private pipe does not inherit broad ACL");
            Check(Equals(security.GetOwner(typeof(SecurityIdentifier)), sid), "private pipe owned by calling user");
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
            Check(rules.Length == 1 && rules[0].IdentityReference.Equals(sid) && rules[0].AccessControlType == AccessControlType.Allow &&
                (rules[0].PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl, "private pipe grants only current user");
        }
        WithPair((server, client, token) =>
        {
            Check(FastScanBridge.GetNamedPipeClientProcessId(server.SafePipeHandle, out uint clientId) && clientId == Environment.ProcessId, "server observes actual client PID");
            Check(FastScanBridge.GetNamedPipeServerProcessId(client.SafePipeHandle, out uint serverId) && serverId == Environment.ProcessId, "client observes actual server PID");
            var worker = Task.Run(() =>
            {
                string request = FastScanWire.ReadRequest(client, token);
                if (request != RootPath) throw new InvalidOperationException("Synthetic request mismatch.");
                FastScanWire.WriteStatus(client, FastScanWireStage.Building, token);
                var time = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var root = new DiskEntry(RootPath, true) { Storage = new(new(41, 3UL << 48 | 5, 0), 0, 1, false, -1, time), Modified = time.ToLocalTime() };
                var file = DiskEntry.FromName("synthetic.bin", false, root, ".bin"); file.Storage = new(new(41, 3UL << 48 | 6, 0), 4096, 1, false, 7, time);
                file.Size = 7; file.FileCount = 1; file.Modified = time.ToLocalTime(); root.MutableChildren.Add(file); root.Size = 7; root.FileCount = 1;
                StorageMetrics.Calculate(root, token); FastScanWire.WriteResult(client, root, TimeSpan.FromMilliseconds(10), new(time), token);
            }, token);
            try
            {
                FastScanWire.WriteRequest(server, RootPath, token); var reader = new FastScanWireReader(server, RootPath);
                Check(reader.Read(token) is { Kind: FastScanWireEventKind.Status, Stage: FastScanWireStage.Building }, "real pipe carries fixed stage");
                var result = reader.Read(token);
                Check(result is { Kind: FastScanWireEventKind.Completed, CleanupConfirmed: true } && result.Result?.Root is { Size: 7, FileCount: 1, AllocatedSize: 4096 },
                    "real pipe carries complete synthetic tree");
                worker.WaitAsync(token).GetAwaiter().GetResult(); Check(worker.IsCompletedSuccessfully, "real pipe synthetic worker settles");
            }
            finally { client.Dispose(); server.Dispose(); try { worker.Wait(TimeSpan.FromSeconds(1)); } catch { } }
        });
        WithPair((server, client, token) =>
        {
            var worker = Task.Run(() =>
            {
                _ = FastScanWire.ReadRequest(client, token); using var cancelled = new CancellationTokenSource();
                var commandReader = Task.Run(() => { try { if (client.ReadByte() == 0x43) cancelled.Cancel(); } catch { cancelled.Cancel(); } });
                try
                {
                    FastScanWire.WriteStatus(client, FastScanWireStage.Reading, token);
                    if (!cancelled.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(3))) throw new TimeoutException("Synthetic cancel not received.");
                    FastScanWire.WriteFailure(client, FastScanWireError.Cancelled, true, token);
                }
                finally { client.Dispose(); commandReader.WaitAsync(token).GetAwaiter().GetResult(); }
            }, token);
            try
            {
                FastScanWire.WriteRequest(server, RootPath, token); var reader = new FastScanWireReader(server, RootPath);
                Check(reader.Read(token).Kind == FastScanWireEventKind.Status, "cancellation worker begins");
                server.WriteByte(0x43); server.Flush();
                Check(reader.Read(token) is { Kind: FastScanWireEventKind.Failure, Error: FastScanWireError.Cancelled, CleanupConfirmed: true }, "full duplex cancellation receives closed terminal");
                worker.WaitAsync(token).GetAwaiter().GetResult(); Check(worker.IsCompletedSuccessfully, "cancellation listener settles");
            }
            finally { client.Dispose(); server.Dispose(); try { worker.Wait(TimeSpan.FromSeconds(1)); } catch { } }
        });
        WithPair((server, client, token) =>
        {
            using var entered = new ManualResetEventSlim();
            var listener = Task.Run(() => { entered.Set(); try { _ = client.ReadByte(); } catch (IOException) { } catch (ObjectDisposedException) { } });
            try
            {
                entered.Wait(token); Thread.Sleep(50); client.Dispose();
                listener.WaitAsync(token).GetAwaiter().GetResult(); Check(listener.IsCompletedSuccessfully, "disposing pipe unblocks command listener");
            }
            finally { server.Dispose(); client.Dispose(); try { listener.Wait(TimeSpan.FromSeconds(1)); } catch { } }
        });
        // Validate that a PIPE_REJECT_REMOTE_CLIENTS native handle can be
        // wrapped by the same managed async stream API. No remote peer exists
        // in this test, so it does not claim an end-to-end network rejection.
        WithPair((server, client, token) =>
        {
            Check(FastScanBridge.GetNamedPipeClientProcessId(server.SafePipeHandle, out uint clientId) && clientId == Environment.ProcessId &&
                FastScanBridge.GetNamedPipeServerProcessId(client.SafePipeHandle, out uint serverId) && serverId == Environment.ProcessId,
                "native local-only handle supports PID binding");
            Check(server.GetAccessControl().AreAccessRulesProtected, "native local-only handle retains protected ACL");
            server.WriteByte(0x5A); server.Flush();
            byte[] first = new byte[1]; Check(client.ReadAsync(first, token).AsTask().GetAwaiter().GetResult() == 1 && first[0] == 0x5A, "native local-only managed async read");
            client.WriteByte(0xA5); client.Flush(); Check(server.ReadByte() == 0xA5, "native local-only managed reverse read");
        }, CreateNativeLocalPipe);
        {
            string name = Name(); using var first = CreateNativeLocalPipe(name); bool rejected = false;
            try { using var duplicate = CreateNativeLocalPipe(name); }
            catch (Win32Exception error) { rejected = error.NativeErrorCode is 5 or 231; }
            Check(rejected, "native first-instance collision rejected");
        }
        Console.WriteLine($"Fast scan bridge contract: {passed} checks passed."); return passed;
    }
    private static void WithPair(Action<NamedPipeServerStream, NamedPipeClientStream, CancellationToken> action,
        Func<string, NamedPipeServerStream>? factory = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); string name = Name();
        using var server = (factory ?? FastScanBridge.CreatePipe)(name);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var close = timeout.Token.Register(() => { try { client.Dispose(); } catch { } try { server.Dispose(); } catch { } });
        var waiting = server.WaitForConnectionAsync(timeout.Token);
        client.ConnectAsync(timeout.Token).GetAwaiter().GetResult(); waiting.GetAwaiter().GetResult(); action(server, client, timeout.Token);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maximumInstances,
        uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes security);
    private static NamedPipeServerStream CreateNativeLocalPipe(string name)
    {
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!; var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false); security.SetOwner(sid);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        byte[] descriptor = security.GetSecurityDescriptorBinaryForm(); var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        SafePipeHandle? handle = null;
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject(), Inherit = false };
            handle = CreateNamedPipeW(@"\\.\pipe\" + name, 3u | 0x40000000u | 0x00080000u | 0x00040000u,
                0x00000008u, 1, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var stream = new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); handle = null; return stream;
        }
        finally { handle?.Dispose(); pinned.Free(); }
    }
}
