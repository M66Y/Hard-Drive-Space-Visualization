using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

internal sealed class NtfsVssNativeSession : IDisposable
{
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

    internal static bool IsVolumeGuid(string volume) => volume is { Length: 49 } &&
        volume.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && volume.EndsWith(@"}\", StringComparison.Ordinal) &&
        Guid.TryParseExact(volume.AsSpan(11, 36), "D", out var value) && value != Guid.Empty;

    private IntPtr backup, async;
    private Guid set, snapshot;
    private bool com, creating, released, cleanupConfirmed;
    private int cleanupPolls = 40;
    private readonly string? ownerPath;
    private FileStream? owner;
    internal NtfsVssNativeSession(string? ownerPath = null) { this.ownerPath = ownerPath; ownerThread = Environment.CurrentManagedThreadId; }
    private readonly int ownerThread;
    internal DateTime SnapshotCreatedUtc { get; private set; }
    // The synthetic seam owns only inert COM tables supplied by the tests.
    // It does not initialize COM, create a requester, or mint snapshot access.
    internal static NtfsVssNativeSession ForTests(IntPtr backup, IntPtr async, Guid snapshot, bool creating = false) =>
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

    internal string Create(string original, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Require(ownerThread == Environment.CurrentManagedThreadId && !released && backup == IntPtr.Zero && IsVolumeGuid(original), "VssSnapshotBinding");
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
                properties.State, source, original, device), "VssSnapshotBinding");
            SnapshotCreatedUtc = DateTime.FromFileTimeUtc(properties.Created); return device;
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
        if (ownerThread != Environment.CurrentManagedThreadId) return false;
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
