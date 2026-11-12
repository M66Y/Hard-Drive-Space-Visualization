using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed class NtfsVssFixtureOpenException(string code, int nativeError, bool cleanupConfirmed) : IOException(code)
{
    internal string Code { get; } = code;
    internal int NativeError { get; } = nativeError;
    internal bool CleanupConfirmed { get; } = cleanupConfirmed;
}

// Real factories accept only a fresh, fully guarded owned-fixture context. No
// caller snapshot path, identifier, volume string or validated boolean is trusted.
internal sealed class NtfsVssFixtureOwner : IDisposable
{
    // COM security outlives individual CoInitializeEx/CoUninitialize pairs.
    // Only our own successful initialization authorizes subsequent owners to
    // skip this process-wide call; an external RPC_E_TOO_LATE is still failure.
    private static readonly SecurityOnce ProcessSecurity = new();
    internal sealed class SecurityOnce
    {
        private readonly object securityGate = new();
        private bool initialized;
        internal void Ensure(Func<int> initialize)
        {
            lock (securityGate)
            {
                if (initialized) return;
                int result;
                try { result = initialize(); } catch { throw new NtfsVssProbeException("VssComSecurity"); }
                if (result != 0) throw new NtfsVssProbeException("VssComSecurity", result);
                initialized = true;
            }
        }
    }
    internal static readonly Guid SystemProvider = new("b5946137-7b9f-4925-af80-51abd60b20d5");
    internal const int InitializeSlot = 5, ContextSlot = 35, StartSlot = 36, AddSlot = 37,
        SnapshotSlot = 38, DeleteSlot = 39, PropertiesSlot = 42, SupportedSlot = 44;
    internal const int ObjectNotFound = unchecked((int)0x80042308), AsyncPending = 0x42309,
        AsyncFinished = 0x4230a, AsyncCancelled = 0x4230b;
    [StructLayout(LayoutKind.Sequential)] internal struct SnapshotProperties
    {
        internal Guid Snapshot, Set;
        internal int Count;
        internal IntPtr Device, Original, OriginatingMachine, ServiceMachine, ExposedName, ExposedPath;
        internal Guid Provider;
        internal int Attributes;
        internal long Created;
        internal int State;
    }
    internal static void Require([DoesNotReturnIf(false)] bool value, string code, int error = 0)
    { if (!value) throw new NtfsVssProbeException(code, error); }
    internal static Guid AdoptGuid(int result, Guid value, string code)
    { if (result < 0) throw new NtfsVssProbeException(code, result); Require(value != Guid.Empty, code); return value; }
    internal static bool BindingValid(Guid snapshot, Guid set, Guid returnedSnapshot, Guid returnedSet, int count,
        Guid provider, int attributes, int state, string original, string expectedOriginal, string device) =>
        snapshot != Guid.Empty && set != Guid.Empty && snapshot == returnedSnapshot && set == returnedSet && count == 1 &&
        provider == SystemProvider && (attributes & 0x10) != 0 && (attributes & (1 | 8)) == 0 && state == 12 &&
        string.Equals(original, expectedOriginal, StringComparison.OrdinalIgnoreCase) &&
        device != null && System.Text.RegularExpressions.Regex.IsMatch(device,
            @"^\\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private readonly NtfsFixtureSnapshotScope scope;
    private readonly NativeSession session;
    private readonly bool scale;
    private readonly int ownerThread;
    private readonly object gate = new();
    private readonly List<SafeFileHandle> directories = new();
    private readonly List<IDisposable> cursors = new();
    private NtfsSnapshotVolumeReader? reader;
    private bool readerIssued, closed, cleanupConfirmed;
    private string snapshotRoot = "", snapshotDataRoot = "";
    private FileStorageInfo? snapshotStorage;
    private NtfsVssFixtureOwner(NtfsFixtureSnapshotScope scope, bool scale, NativeSession? syntheticSession = null)
    {
        this.scope = scope; this.scale = scale; ownerThread = Environment.CurrentManagedThreadId;
        session = syntheticSession ?? new NativeSession(Path.Combine(scope.Workspace, "vss-owner.json"));
    }
    internal static NtfsVssFixtureOwner ForTests(NativeSession syntheticSession, bool scale = false)
    {
        var root = new FileStorageInfo(new(41, NtfsRecordFixtures.Reference(40, 1), 0), 0, 1, false, -1);
        var scope = new NtfsFixtureSnapshotScope("SyntheticVolume", "SyntheticWorkspace", "SyntheticData", root,
            new(new(512, 4096, 1024, 1024 * 1024, 256, 2, 65536), 41, 4096), true);
        return new(scope, scale, syntheticSession) { snapshotStorage = root };
    }
    internal NtfsSnapshotVolumeReader OpenReaderForTests(NtfsSnapshotVolumeReader.INative adapter)
    {
        lock (gate)
        {
            Active(); Require(scope.IsSynthetic && ownerThread == Environment.CurrentManagedThreadId && !readerIssued, "VssRawOpen");
            readerIssued = true; reader = NtfsSnapshotVolumeReader.ForTests(adapter, scope.Facts.Serial, scope.Facts.Geometry.VolumeBytes, scale); return reader;
        }
    }
    internal static NtfsVssFixtureOwner CreateProbe(NtfsVerifiedFixtureContext context, CancellationToken token = default) => Create(context, false, token);
    internal static NtfsVssFixtureOwner CreateScale(NtfsVerifiedFixtureContext context, CancellationToken token = default) => Create(context, true, token);
    private static NtfsVssFixtureOwner Create(NtfsVerifiedFixtureContext context, bool scale, CancellationToken token)
    {
        NtfsVssFixtureOwner? owner = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var scope = context.CaptureSnapshotScope(scale, token);
            Require(!scope.IsSynthetic, "VssFixtureGuard");
            owner = new(scope, scale);
            owner.snapshotRoot = owner.session.Create(owner, token);
            owner.snapshotDataRoot = owner.snapshotRoot + "\\" + Path.GetFileName(Path.TrimEndingDirectorySeparator(scope.DataRoot));
            var root = owner.ReadNative("", true, token);
            Require(root.Identity == scope.RootStorage.Identity, "VssSnapshotBinding");
            owner.snapshotStorage = root;
            token.ThrowIfCancellationRequested(); return owner;
        }
        catch (Exception error)
        {
            bool confirmed = owner?.Cleanup() ?? true;
            string code = error is OperationCanceledException ? "VssCancelled" : error is NtfsVssProbeException controlled ? controlled.Code :
                error is NtfsFixtureGuardException ? "VssFixtureGuard" : "VssUnexpected";
            int number = error is NtfsVssProbeException failure ? failure.Error : error is NtfsFixtureGuardException guard ? guard.NativeErrorCode : 0;
            throw new NtfsVssFixtureOpenException(code, number, confirmed);
        }
    }
    private void Active() { Require(!closed, "VssCleanup"); }
    internal string SnapshotRoot { get { lock (gate) { Active(); return snapshotRoot; } } }
    internal string SnapshotDataRoot { get { lock (gate) { Active(); return snapshotDataRoot; } } }
    internal FileStorageInfo RootStorage { get { lock (gate) { Active(); return snapshotStorage ?? throw new NtfsVssProbeException("VssSnapshotNative"); } } }
    internal NtfsSnapshotVolumeReader OpenReader(CancellationToken token = default)
    {
        lock (gate)
        {
            Active(); Require(ownerThread == Environment.CurrentManagedThreadId && !readerIssued, "VssRawOpen");
            return NtfsSnapshotVolumeReader.OpenBound(this, token);
        }
    }
    internal sealed record ReadBinding(string Device, ulong Serial, long VolumeBytes, bool Scale);
    internal ReadBinding TakeReadBinding()
    {
        lock (gate)
        {
            Active(); Require(ownerThread == Environment.CurrentManagedThreadId && !readerIssued && !scope.IsSynthetic, "VssRawOpen");
            readerIssued = true; return new(snapshotRoot, scope.Facts.Serial, scope.Facts.Geometry.VolumeBytes, scale);
        }
    }
    internal void TrackReader(NtfsSnapshotVolumeReader value)
    { lock (gate) { Active(); Require(readerIssued && reader == null, "VssRawOpen"); reader = value; } }
    private string Resolve(string relative, bool directory)
    {
        string root; lock (gate) { Active(); Require(!scope.IsSynthetic, "VssSnapshotNative"); root = snapshotDataRoot; }
        string[] parts = RelativeSegments(relative, directory);
        if (parts.Length == 0) return root;
        string current = root;
        Require((File.GetAttributes(current) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == FileAttributes.Directory, "VssSnapshotNative");
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            Require(part.Length is >= 1 and <= 255 && part is not ("." or "..") && !part.Any(value => value < 32 || value is ':' or '/' or '\\'), "VssSnapshotNative");
            current += "\\" + part;
            var attributes = File.GetAttributes(current);
            Require((attributes & FileAttributes.ReparsePoint) == 0 && (i == parts.Length - 1 || (attributes & FileAttributes.Directory) != 0), "VssSnapshotNative");
        }
        return current;
    }
    internal static string[] RelativeSegments(string relative, bool directory)
    {
        Require(relative != null && relative.Length <= 32768 && !Path.IsPathRooted(relative), "VssSnapshotNative");
        if (relative.Length == 0) { Require(directory, "VssSnapshotNative"); return []; }
        // Namespace.Read constructs its private relative paths with '/'. Do not
        // accept a second spelling or silently normalize mixed separators.
        string[] parts = relative.Split('/');
        foreach (string part in parts) Require(part.Length is >= 1 and <= 255 && part is not ("." or "..") &&
            !part.Any(value => char.IsControl(value) || value is ':' or '/' or '\\'), "VssSnapshotNative");
        return parts;
    }
    internal FileStorageInfo ReadNative(string relative, bool directory, CancellationToken token = default)
        => ReadNativeSize(relative, directory, token).Storage;
    private NtfsSnapshotNativeSize ReadNativeSize(string relative, bool directory, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested(); string path = Resolve(relative, directory);
            var value = ReadMetadata(path, directory, token);
            lock (gate) { Active(); }
            token.ThrowIfCancellationRequested(); return value;
        }
        catch (NtfsVssProbeException) { throw; }
        catch { token.ThrowIfCancellationRequested(); throw new NtfsVssProbeException("VssSnapshotNative"); }
    }
    internal NtfsSnapshotNativeSize LookupSize(NtfsSnapshotNamespaceResult inventory, NtfsSnapshotSizeRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Require(inventory != null && request != null && inventory.RootIdentity == RootStorage.Identity && request.EntryIndex >= 0 &&
            request.EntryIndex < inventory.Entries.Count, "VssSnapshotNative");
        var entry = inventory.Entries[request.EntryIndex];
        Require(entry.Identity == request.Identity && entry.Directory == request.Directory && !entry.Reparse, "VssSnapshotNative");
        var value = ReadNativeSize(inventory.RelativePath(request.EntryIndex), request.Directory, token);
        Require(value.Storage.Identity == request.Identity && (!request.ExpectedLogicalBytes.HasValue ||
            value.Storage.LogicalBytes == request.ExpectedLogicalBytes.Value), "VssSnapshotNative"); return value;
    }
    internal NtfsSnapshotNamespace.IBackend CreateNamespaceBackend()
    { lock (gate) { Active(); Require(!scope.IsSynthetic, "VssSnapshotNative"); return new NamespaceBackend(this); } }
    private sealed class NamespaceBackend(NtfsVssFixtureOwner owner) : NtfsSnapshotNamespace.IBackend
    {
        public FileIdentity RootIdentity => owner.RootStorage.Identity;
        public NtfsSnapshotNamespace.ICursor OpenDirectory(NtfsSnapshotNamespaceEntry entry, string relativePath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Require(entry != null && entry.Directory && !entry.Reparse && entry.Identity.Volume == RootIdentity.Volume,
                "VssSnapshotNative");
            string path = owner.Resolve(relativePath, true);
            var cursor = NtfsSnapshotNamespace.OpenNativeDirectory(path, entry.Identity, token);
            lock (owner.gate)
            {
                try { owner.Active(); Require(owner.cursors.Count < NtfsSnapshotNamespace.MaximumEntries, "VssSnapshotNative"); owner.cursors.Add(cursor); }
                catch { cursor.Dispose(); throw; }
            }
            return cursor;
        }
    }
    internal SafeFileHandle OpenDirectory(string relative, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested(); string path = Resolve(relative, true);
            var handle = NativeStorage.CreateFileW(path, 0x81, NativeStorage.ShareAll, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new NtfsVssProbeException("VssSnapshotNative", error); }
            directories.Add(handle); token.ThrowIfCancellationRequested(); return handle;
        }
    }
    internal bool Cleanup()
    {
        lock (gate)
        {
            if (closed) return cleanupConfirmed;
            // No COM call, Release, or CoUninitialize may migrate to a task continuation.
            if (ownerThread != Environment.CurrentManagedThreadId) return false;
            closed = true; bool readersClosed = true;
            try { reader?.Dispose(); if (reader != null) readersClosed &= reader.BufferReleased; } catch { readersClosed = false; }
            foreach (var cursor in cursors) { try { cursor.Dispose(); } catch { readersClosed = false; } }
            cursors.Clear();
            foreach (var directory in directories) { try { directory.Dispose(); } catch { readersClosed = false; } }
            directories.Clear();
            cleanupConfirmed = session.Cleanup() && readersClosed; return cleanupConfirmed;
        }
    }
    public void Dispose() { _ = Cleanup(); }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { internal long Creation, Access, Write, Change; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo { internal long Allocation, End; internal uint Links; internal byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] private struct IdInfo { internal ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct CompressionInfo
    { internal long Size; internal ushort Format; internal byte Unit, Chunk, Cluster, Reserved1, Reserved2, Reserved3; }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool Basic(SafeFileHandle handle, int kind, out BasicInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool Standard(SafeFileHandle handle, int kind, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool Identity(SafeFileHandle handle, int kind, out IdInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool Compression(SafeFileHandle handle, int kind, out CompressionInfo info, uint size);
    private NtfsSnapshotNativeSize ReadMetadata(string path, bool directory, CancellationToken token)
    {
        using var handle = NativeStorage.CreateFileW(path, 0x81, NativeStorage.ShareAll, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Require(!handle.IsInvalid, "VssSnapshotNative", Marshal.GetLastWin32Error()); token.ThrowIfCancellationRequested();
        BasicInfo basic = default; StandardInfo standard = default; IdInfo identity = default;
        Require(Basic(handle, 0, out basic, (uint)Marshal.SizeOf<BasicInfo>()) && Standard(handle, 1, out standard, (uint)Marshal.SizeOf<StandardInfo>()) &&
            Identity(handle, 18, out identity, (uint)Marshal.SizeOf<IdInfo>()), "VssSnapshotNative", Marshal.GetLastWin32Error());
        Require((basic.Attributes & (uint)FileAttributes.ReparsePoint) == 0 && ((basic.Attributes & (uint)FileAttributes.Directory) != 0) == directory &&
            (standard.Directory != 0) == directory && standard.DeletePending == 0 && identity.Volume == scope.Facts.Serial && identity.High == 0 && identity.Low >> 48 != 0 && standard.Links > 0,
            "VssSnapshotNative");
        long allocated = directory ? 0 : standard.Allocation;
        if (!directory && ((FileAttributes)basic.Attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
        {
            Require(Compression(handle, 8, out var compression, (uint)Marshal.SizeOf<CompressionInfo>()), "VssSnapshotNative", Marshal.GetLastWin32Error());
            allocated = compression.Size;
        }
        Require(allocated >= 0 && (directory || standard.End >= 0), "VssSnapshotNative");
        var storage = new FileStorageInfo(new(identity.Volume, identity.Low, identity.High), allocated, standard.Links, false,
            directory ? -1 : standard.End, DateTime.FromFileTimeUtc(basic.Write).ToLocalTime());
        token.ThrowIfCancellationRequested(); return new(storage, directory, standard.DeletePending != 0);
    }
    internal sealed class NativeSession : IDisposable
    {
        private IntPtr backup, async;
        private Guid set, snapshot;
        private bool com, creating, released, cleanupConfirmed;
        private int cleanupPolls = 40;
        private readonly string? ownerPath;
        private FileStream? owner;
        internal NativeSession(string? ownerPath = null) { this.ownerPath = ownerPath; }
        // The synthetic seam owns only inert COM tables supplied by the tests.
        // It does not initialize COM, create a requester, or mint snapshot access.
        internal static NativeSession ForTests(IntPtr backup, IntPtr async, Guid snapshot, bool creating = false) =>
            new() { backup = backup, async = async, snapshot = snapshot, creating = creating, cleanupPolls = 1 };
        private const int Context = 0x10;
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OnePointer(IntPtr self, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OneInt(IntPtr self, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutGuid(IntPtr self, out Guid value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddVolume(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string volume, Guid provider, out Guid id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Supported(IntPtr self, Guid provider, [MarshalAs(UnmanagedType.LPWStr)] string volume, out int supported);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutPointer(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Properties(IntPtr self, Guid id, out SnapshotProperties properties);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Delete(IntPtr self, Guid id, int type, int force, out int count, out Guid remaining);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int QueryAsync(IntPtr self, out int status, IntPtr reserved);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int NoArgs(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate uint Release(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Wait(IntPtr self, int milliseconds);
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] private static extern int CoInitializeSecurity(IntPtr descriptor, int count, IntPtr services, IntPtr reserved,
            uint level, uint impersonation, IntPtr auth, uint capabilities, IntPtr reserved2);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("vssapi.dll", EntryPoint = "CreateVssBackupComponentsInternal")] private static extern int CreateBackup(out IntPtr value);
        [DllImport("vssapi.dll", EntryPoint = "VssFreeSnapshotPropertiesInternal")] private static extern void FreeProperties(ref SnapshotProperties properties);
        private static T Method<T>(IntPtr value, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(value), slot * IntPtr.Size));
        private static void Hr(int value, string code) { if (value < 0) throw new NtfsVssProbeException(code, value); }

        internal string Create(NtfsVssFixtureOwner owner, CancellationToken token)
        {
            Require(ReferenceEquals(owner.session, this) && !owner.scope.IsSynthetic && owner.ownerThread == Environment.CurrentManagedThreadId,
                "VssFixtureGuard");
            string original = owner.scope.Volume;
            Hr(CoInitializeEx(IntPtr.Zero, 0), "VssComInitialize"); com = true;
            ProcessSecurity.Ensure(() => CoInitializeSecurity(IntPtr.Zero, -1, IntPtr.Zero, IntPtr.Zero, 6, 2, IntPtr.Zero, 0, IntPtr.Zero));
            Hr(CreateBackup(out IntPtr returnedBackup), "VssCreateBackup"); Require(returnedBackup != IntPtr.Zero, "VssCreateBackup"); backup = returnedBackup;
            Hr(Method<OnePointer>(backup, InitializeSlot)(backup, IntPtr.Zero), "VssInitializeBackup");
            Hr(Method<OneInt>(backup, ContextSlot)(backup, Context), "VssContext");
            Hr(Method<Supported>(backup, SupportedSlot)(backup, SystemProvider, original, out int supported), "VssVolumeUnsupported");
            Require(supported != 0, "VssVolumeUnsupported"); token.ThrowIfCancellationRequested();
            int started = Method<OutGuid>(backup, StartSlot)(backup, out Guid returnedSet);
            set = AdoptGuid(started, returnedSet, "VssStartSet");
            SaveOwner(original);
            int added = Method<AddVolume>(backup, AddSlot)(backup, original, SystemProvider, out Guid returnedSnapshot);
            snapshot = AdoptGuid(added, returnedSnapshot, "VssAddVolume");
            SaveOwner(original);
            creating = true; Hr(Method<OutPointer>(backup, SnapshotSlot)(backup, out IntPtr returnedAsync), "VssCreateSnapshot");
            Require(returnedAsync != IntPtr.Zero, "VssCreateSnapshot"); async = returnedAsync;
            var watch = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Hr(Method<OneInt>(async, 4)(async, 500), "VssAsyncWait");
                Hr(Method<QueryAsync>(async, 5)(async, out int state, IntPtr.Zero), "VssAsyncStatus");
                if (state == AsyncFinished) { creating = false; break; }
                if (state < 0) throw new NtfsVssProbeException("VssCreateSnapshot", state);
                Require(state == AsyncPending, "VssAsyncStatus", state);
                Require(watch.Elapsed < TimeSpan.FromMinutes(2), "VssAsyncTimeout");
            }
            token.ThrowIfCancellationRequested();
            Hr(Method<Properties>(backup, PropertiesSlot)(backup, snapshot, out var properties), "VssProperties");
            try
            {
                string device = Marshal.PtrToStringUni(properties.Device) ?? "", source = Marshal.PtrToStringUni(properties.Original) ?? "";
                Require(BindingValid(snapshot, set, properties.Snapshot, properties.Set, properties.Count, properties.Provider, properties.Attributes,
                    properties.State, source, original, device), "VssSnapshotBinding"); return device;
            }
            finally { FreeProperties(ref properties); }
        }

        private void SaveOwner(string original)
        {
            if (ownerPath == null) return;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, set_id = set, snapshot_id = snapshot, volume = original });
            Require(bytes.Length <= 1024, "VssFixtureCreate");
            owner ??= new FileStream(ownerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            owner.Position = 0; owner.SetLength(0); owner.Write(bytes); owner.Flush(true);
        }

        internal bool Cleanup()
        {
            if (released) return cleanupConfirmed;
            bool gone = snapshot == Guid.Empty && !creating;
            try
            {
                if (creating && async != IntPtr.Zero)
                {
                    _ = Method<NoArgs>(async, 3)(async);
                    var watch = Stopwatch.StartNew();
                    int attempts = 0;
                    while (attempts++ < cleanupPolls && watch.Elapsed < TimeSpan.FromSeconds(20))
                    {
                        _ = Method<Wait>(async, 4)(async, 500);
                        int result = Method<QueryAsync>(async, 5)(async, out int state, IntPtr.Zero);
                        if (result >= 0 && (state is AsyncFinished or AsyncCancelled || state < 0)) { creating = false; break; }
                    }
                }
                if (!creating && snapshot != Guid.Empty && backup != IntPtr.Zero)
                {
                    _ = Method<Delete>(backup, DeleteSlot)(backup, snapshot, 3, 0, out _, out _);
                    int result = Method<Properties>(backup, PropertiesSlot)(backup, snapshot, out var properties);
                    if (result >= 0) FreeProperties(ref properties);
                    gone = result == ObjectNotFound;
                }
            }
            catch { gone = false; }
            finally
            {
                try { if (async != IntPtr.Zero) { _ = Method<Release>(async, 2)(async); async = IntPtr.Zero; } } catch { gone = false; }
                try { if (backup != IntPtr.Zero) { _ = Method<Release>(backup, 2)(backup); backup = IntPtr.Zero; } } catch { gone = false; }
                try { if (com) { CoUninitialize(); com = false; } } catch { gone = false; }
                try
                {
                    if (owner != null) { owner.Dispose(); owner = null; if (gone && !creating) File.Delete(ownerPath!); }
                }
                catch { gone = false; }
                released = true;
            }
            cleanupConfirmed = gone && !creating; return cleanupConfirmed;
        }
        public void Dispose() { _ = Cleanup(); }
    }


}
