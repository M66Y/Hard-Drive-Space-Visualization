using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed class NtfsVssProbeException(string code, int error = 0) : IOException(code)
{ internal string Code { get; } = code; internal int Error { get; } = error; }
internal sealed record NtfsVssFixtureEvidence(string Revision, string Stage, int CreatedSnapshots, int DeletedSnapshots,
    int FilesCompared, int RawRecordsCompared, int RawReadCalls, long RawRequestedBytes, bool PropertiesBound,
    bool SnapshotFilesStable, bool LiveChangesObserved, bool GeometryVerified, bool RawRecordsMatched, bool CleanupConfirmed);

// Dedicated requester for one new, guarded fixture. It never accepts an arbitrary
// volume, snapshot ID/device, Native verifier, or source-proof boolean from CLI.
internal static class NtfsVssFixtureProbe
{
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
    internal static void Require(bool value, string code, int error = 0)
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

    internal static int Run(string manifest, CancellationToken token = default)
    {
        int checks = 0, nativeError = 0; string code = "None"; bool cleanup = false;
        string? summary = null; NtfsVssFixtureEvidence? evidence = null; NativeSession? session = null;
        NtfsVerifiedFixtureContext? context = null;
        void Check(bool condition, string failure) { Require(condition, failure); checks++; }
        try
        {
            context = NtfsVerifiedFixtureContext.CreateForVss(manifest, token);
            summary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifest))!, "live-summary.json");
            Check(!File.Exists(summary) && !Directory.Exists(context.DataRoot), "VssFixtureCreate");
            var origin = context.QueryFacts(token: token);
            Directory.CreateDirectory(context.DataRoot);
            Check((File.GetAttributes(context.DataRoot) & FileAttributes.ReparsePoint) == 0, "VssFixtureCreate");
            // Pin this exact new directory without delete sharing throughout all
            // file creation/mutation. Later writers open the file itself without
            // following reparse points and recheck its bound identity first.
            using var directoryPin = NativeStorage.CreateFileW(context.DataRoot, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            Check(!directoryPin.IsInvalid, "VssFixtureCreate");
            var directoryIdentity = BoundIdentity(directoryPin, directory: true);
            Check(directoryIdentity.Volume == origin.Serial, "VssFixtureCreate");
            string[] names = ["s0000001.bin", "s0000002.bin", "s0000003.bin"];
            for (int i = 0; i < 3; i++)
            {
                token.ThrowIfCancellationRequested();
                using var file = new FileStream(Path.Combine(context.DataRoot, names[i]), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(new byte[(i + 1) * 8192]); file.Flush(true);
            }
            session = new NativeSession(Path.Combine(Path.GetDirectoryName(summary)!, "vss-owner.json"));
            string snapshotRoot = session.Create(context.OriginVolume, token);
            string rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(context.DataRoot));
            string snapshotData = snapshotRoot + "\\" + rootName;
            FileStorageInfo SnapshotInfo(string name, bool directory = false)
            {
                token.ThrowIfCancellationRequested(); string path = directory ? snapshotData : snapshotData + "\\" + name;
                var attributes = File.GetAttributes(path);
                FileStorageInfo? storage = null;
                Check((attributes & FileAttributes.ReparsePoint) == 0 && NativeStorage.TryRead(path, directory, attributes, out storage, out _) && storage != null,
                    "VssSnapshotNative");
                return storage!;
            }
            var parent = SnapshotInfo("", true);
            Check(parent.Identity == directoryIdentity, "VssSnapshotNative");
            var before = names.Select(name => SnapshotInfo(name)).ToArray();
            void CheckSnapshotContent(string name)
            {
                token.ThrowIfCancellationRequested();
                using var file = new FileStream(snapshotData + "\\" + name, FileMode.Open, FileAccess.Read, FileShare.Read);
                var prefix = new byte[32]; file.ReadExactly(prefix); Check(prefix.All(value => value == 0), "VssSnapshotNative");
            }
            foreach (string name in names) CheckSnapshotContent(name);
            Check(before.All(item => item.Identity.Volume == origin.Serial && item.Identity.High == 0 && item.Identity.Low >> 48 != 0 &&
                item.LinkCount == 1 && !item.WritableHandleOpen), "VssSnapshotNative");
            for (int i = 0; i < 3; i++)
            {
                token.ThrowIfCancellationRequested(); string path = Path.Combine(context.DataRoot, names[i]);
                using (var writer = NativeStorage.CreateFileW(path, 0x40000000 | 0x80, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                {
                    Check(!writer.IsInvalid && BoundIdentity(writer, directory: false) == before[i].Identity &&
                        BoundIdentity(directoryPin, directory: true) == directoryIdentity, "VssLiveMutation");
                    using var file = new FileStream(writer, FileAccess.Write);
                    file.WriteByte((byte)(0x40 + i)); file.SetLength((i + 1) * 8192 + 4096); file.Flush(true);
                }
                Check(NativeStorage.TryRead(path, false, File.GetAttributes(path), out var changed, out _) && changed != null &&
                    changed.Identity == before[i].Identity && changed.LogicalBytes == before[i].LogicalBytes + 4096, "VssLiveMutation");
            }
            for (int i = 0; i < 3; i++) { Check(SnapshotInfo(names[i]) == before[i], "VssSnapshotNative"); CheckSnapshotContent(names[i]); }
            int rawCalls, rawRecords = 1; long rawBytes;
            using (var reader = new SnapshotReader(snapshotRoot))
            {
                var facts = reader.Facts();
                Check(facts.Serial == origin.Serial && facts.Geometry is { SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024 } &&
                    facts.Geometry.VolumeBytes == origin.Geometry.VolumeBytes, "VssRawGeometry");
                long start = checked(facts.Geometry.MftStartLcn * 4096);
                var mapped = NtfsMftMap.Parse(reader.Read(start, 1024, token), facts.Geometry, token);
                Check(mapped.Status == "Mapped" && mapped.Map != null, "VssRawMap");
                var bootstrap = mapped.Map!.PlanRecord(0, token);
                Check(bootstrap.Count == 1 && bootstrap[0].VolumeOffset == start && bootstrap[0].Length == 1024 && bootstrap[0].RecordOffset == 0,
                    "VssRawMap");
                for (int i = 0; i < 3; i++)
                {
                    var native = before[i]; ulong slot = native.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber;
                    var raw = new byte[1024]; int copied = 0;
                    foreach (var slice in mapped.Map!.PlanRecord(slot, token))
                    {
                        Check(slice.RecordOffset == copied, "VssRawCompare"); var bytes = reader.Read(slice.VolumeOffset, slice.Length, token);
                        bytes.CopyTo(raw, copied); copied += bytes.Length;
                    }
                    Check(copied == 1024, "VssRawCompare");
                    var decoded = NtfsRecordDecoder.Decode(raw, 512, token: token, recordNumber: slot, clusterSize: 4096);
                    Check(decoded.Status == NtfsDecodeStatus.Decoded && decoded.Record != null, "VssRawCompare");
                    var record = decoded.Record!; var visible = record.Names.Where(name => name.Namespace != 2).ToArray();
                    Check(record.Reference == native.Identity.Low && record.BaseReference == 0 && !record.IsDirectory &&
                        record.LogicalBytes == native.LogicalBytes && record.AllocatedBytes == native.AllocatedBytes && record.LookupReasons == NtfsLookupReason.None &&
                        NtfsRawComparison.MatchesLinks(record, native.LinkCount) && visible.Length == 1 && visible[0].Name == names[i] &&
                        record.Names.All(name => name.ParentReference == parent.Identity.Low), "VssRawCompare");
                    rawRecords++;
                }
                for (int i = 0; i < 3; i++) Check(SnapshotInfo(names[i]) == before[i], "VssSnapshotNative");
                rawCalls = reader.ReadCalls; rawBytes = reader.RequestedBytes;
            }
            token.ThrowIfCancellationRequested();
            cleanup = session.Cleanup();
            Check(cleanup, "VssCleanup"); session.Dispose(); session = null;
            token.ThrowIfCancellationRequested();
            evidence = new("fixture-vss-v1", "SnapshotReadVerified", 1, 1, 3, rawRecords, rawCalls, rawBytes,
                true, true, true, true, true, true);
        }
        catch (OperationCanceledException) { code = "VssCancelled"; }
        catch (NtfsVssProbeException error) { code = error.Code; nativeError = error.Error; }
        catch (NtfsFixtureGuardException error) { code = "VssFixtureGuard"; nativeError = error.NativeErrorCode; }
        catch { code = "VssUnexpected"; }
        finally
        {
            if (session != null) { cleanup = session.Cleanup(); session.Dispose(); }
            else if (context != null) cleanup = true; // No requester was created, or it was already explicitly cleaned.
            try { context?.Dispose(); } catch { cleanup = false; }
            if (!cleanup) { if (code == "None") code = "VssCleanup"; evidence = null; }
        }
        if (summary != null)
        {
            var value = new { schema = 1, status = evidence != null && code == "None" ? "vss_verified" : "failed",
                stage = evidence != null && code == "None" ? "SnapshotReadVerified" : code, checks, failures = evidence != null && code == "None" ? 0 : 1,
                code, native_error = nativeError, cleanup_confirmed = cleanup, vss_evidence = evidence };
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
                using var file = new FileStream(summary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(bytes); file.Flush(true);
            }
            catch { Console.WriteLine("RESULT VSS failed"); return 1; }
        }
        Console.WriteLine("RESULT VSS " + (evidence != null && code == "None" ? "verified" : "failed"));
        return evidence != null && code == "None" ? 0 : 1;
    }

    [StructLayout(LayoutKind.Sequential)] private struct IdInfo { internal ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeInfo { internal uint Attributes, Tag; }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadIdentity(SafeFileHandle handle, int kind, out IdInfo value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadAttributes(SafeFileHandle handle, int kind, out AttributeInfo value, uint size);
    private static FileIdentity BoundIdentity(SafeFileHandle handle, bool directory)
    {
        Require(ReadAttributes(handle, 9, out var attributes, 8) && (attributes.Attributes & (uint)FileAttributes.ReparsePoint) == 0 &&
            ((attributes.Attributes & (uint)FileAttributes.Directory) != 0) == directory, "VssFixtureCreate");
        Require(ReadIdentity(handle, 18, out var identity, 24) && identity.High == 0 && identity.Low != 0 && identity.Low >> 48 != 0, "VssFixtureCreate");
        return new(identity.Volume, identity.Low, identity.High);
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

        internal string Create(string original, CancellationToken token)
        {
            Hr(CoInitializeEx(IntPtr.Zero, 0), "VssComInitialize"); com = true;
            Hr(CoInitializeSecurity(IntPtr.Zero, -1, IntPtr.Zero, IntPtr.Zero, 6, 2, IntPtr.Zero, 0, IntPtr.Zero), "VssComSecurity");
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

    private sealed class SnapshotReader : IDisposable
    {
        private readonly SafeFileHandle handle;
        private IntPtr allocation, buffer;
        private long volumeBytes;
        internal int ReadCalls { get; private set; }
        internal long RequestedBytes { get; private set; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long position, uint origin);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, int count, out int returned, IntPtr overlapped);
        internal SnapshotReader(string device)
        {
            handle = NativeStorage.CreateFileW(device, 0x80000000, NativeStorage.ShareAll, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new NtfsVssProbeException("VssRawOpen", error); }
        }
        internal NtfsRawVolumeFacts Facts()
        {
            var bytes = new byte[128];
            Require(NativeStorage.DeviceIoControl(handle, 0x90064, null, 0, bytes, bytes.Length, out int used, IntPtr.Zero), "VssRawGeometry", Marshal.GetLastWin32Error());
            Require(used is >= 104 and <= 128 && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(100)) == 3 &&
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(102)) == 1, "VssRawGeometry");
            long sectors = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)), clusters = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16));
            int sector = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)), cluster = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(44)), record = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(48));
            Require(sector == 512 && cluster == 4096 && record == 1024 && sectors > 0 && sectors <= 1024L * 1024 * 1024 / 512,
                "VssRawGeometry"); volumeBytes = checked(sectors * 512);
            var geometry = new NtfsMftGeometry(sector, cluster, record, volumeBytes, clusters,
                BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(64)), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(56)));
            return new(geometry, BinaryPrimitives.ReadUInt64LittleEndian(bytes), 65536);
        }
        internal byte[] Read(long offset, int length, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Require(offset >= 0 && offset % 512 == 0 && length is > 0 and <= 16384 &&
                length % 512 == 0 && offset <= volumeBytes - length && ReadCalls < 16 && RequestedBytes <= 16384 - length, "VssRawRead");
            if (allocation == IntPtr.Zero)
            {
                allocation = Marshal.AllocHGlobal(131071); buffer = (IntPtr)(((nuint)allocation + 65535) & ~((nuint)65535));
            }
            Require(SetFilePointerEx(handle, offset, out long position, 0) && position == offset, "VssRawRead", Marshal.GetLastWin32Error());
            token.ThrowIfCancellationRequested(); ReadCalls++; RequestedBytes += length;
            Require(ReadFile(handle, buffer, length, out int returned, IntPtr.Zero) && returned == length, "VssRawRead", Marshal.GetLastWin32Error());
            token.ThrowIfCancellationRequested(); var bytes = new byte[length]; Marshal.Copy(buffer, bytes, 0, length); token.ThrowIfCancellationRequested(); return bytes;
        }
        public void Dispose() { if (allocation != IntPtr.Zero) { Marshal.FreeHGlobal(allocation); allocation = buffer = IntPtr.Zero; } handle.Dispose(); }
    }
}
