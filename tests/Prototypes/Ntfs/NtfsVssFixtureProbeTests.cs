using System.Runtime.InteropServices;
using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// Only inert unmanaged tables and controlled values. No requester, snapshot,
// volume, local configuration or VSS service is opened by these checks.
internal static class NtfsVssFixtureProbeTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("VSS probe check " + name); passed++; Console.WriteLine("PASS VSS probe " + name); }
        Check(IntPtr.Size == 8 && Marshal.SizeOf<NtfsVssNativeSession.SnapshotProperties>() == 128, "x64 snapshot property ABI size");
        foreach (var pair in new (string Field, int Offset)[] { ("Snapshot", 0), ("Set", 16), ("Count", 32), ("Device", 40),
            ("Original", 48), ("OriginatingMachine", 56), ("ServiceMachine", 64), ("ExposedName", 72), ("ExposedPath", 80),
            ("Provider", 88), ("Attributes", 104), ("Created", 112), ("State", 120) })
            Check(Marshal.OffsetOf<NtfsVssNativeSession.SnapshotProperties>(pair.Field).ToInt32() == pair.Offset, "snapshot property field offset");
        Check(NtfsVssNativeSession.InitializeSlot == 5 && NtfsVssNativeSession.ContextSlot == 35 && NtfsVssNativeSession.StartSlot == 36 &&
            NtfsVssNativeSession.AddSlot == 37 && NtfsVssNativeSession.SnapshotSlot == 38 && NtfsVssNativeSession.DeleteSlot == 39 &&
            NtfsVssNativeSession.PropertiesSlot == 42 && NtfsVssNativeSession.SupportedSlot == 44, "SDK vtable positions include IUnknown");
        var security = new NtfsVssNativeSession.SecurityOnce(); int securityCalls = 0;
        for (int owner = 0; owner < 6; owner++) security.Ensure(() => { securityCalls++; return securityCalls == 1 ? 0 : unchecked((int)0x80010119); });
        Check(securityCalls == 1, "six sequential owners share only this component successful process security initialization");
        var foreignSecurity = new NtfsVssNativeSession.SecurityOnce(); int rejectedCalls = 0;
        for (int owner = 0; owner < 2; owner++)
        {
            bool rejected = false;
            try { foreignSecurity.Ensure(() => { rejectedCalls++; return unchecked((int)0x80010119); }); }
            catch (NtfsVssProbeException error) { rejected = error.Code == "VssComSecurity" && error.Error == unchecked((int)0x80010119); }
            Check(rejected, "unproven externally initialized COM security is never silently accepted");
        }
        Check(rejectedCalls == 2, "failed security initialization never mints a reusable successful state");
        Guid snapshot = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), set = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
        string volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\", device = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1";
        bool Binding(Guid? id = null, Guid? returnedSet = null, int count = 1, Guid? provider = null, int attributes = 0x10,
            int state = 12, string? source = null, string? path = null) => NtfsVssNativeSession.BindingValid(snapshot, set,
                id ?? snapshot, returnedSet ?? set, count, provider ?? NtfsVssNativeSession.SystemProvider, attributes, state,
                source ?? volume, volume, path ?? device);
        Check(Binding(), "one system-provider nonpersistent no-writer snapshot binds its exact source");
        Check(Binding(source: volume.ToUpperInvariant(), path: device.ToUpperInvariant()), "Windows device spelling is case insensitive");
        Check(!Binding(id: Guid.NewGuid()) && !Binding(returnedSet: Guid.NewGuid()) && !Binding(id: Guid.Empty), "foreign snapshot or set cannot bind");
        Check(!Binding(count: 0) && !Binding(count: 2) && !Binding(provider: Guid.Empty), "single snapshot and fixed provider required");
        foreach (int attributes in new[] { 0, 1, 8, 0x11, 0x18 }) Check(!Binding(attributes: attributes), "persistent or auto-release-disabled context rejected");
        foreach (int state in new[] { -1, 0, 7, 11, 13, 14, int.MaxValue }) Check(!Binding(state: state), "only created snapshot state accepted");
        foreach (string path in new[] { @"C:\Synthetic", @"\\.\PhysicalDrive7", device + "\\outside", device + ":stream", device + "\nprivate", "" })
            Check(!Binding(path: path), "only returned global shadow device namespace accepted");
        Check(!Binding(source: @"\\?\Volume{00000000-0000-0000-0000-000000000002}\"), "source volume cannot be exchanged");
        Check(NtfsVssNativeSession.AdoptGuid(0, snapshot, "VssAddVolume") == snapshot, "successful returned identifier becomes owned");
        foreach (var tuple in new[] { (unchecked((int)0x80004005), snapshot), (0, Guid.Empty) })
        {
            bool rejected = false;
            try { _ = NtfsVssNativeSession.AdoptGuid(tuple.Item1, tuple.Item2, "VssAddVolume"); }
            catch (NtfsVssProbeException error) { rejected = error.Code == "VssAddVolume" && error.Message == error.Code && error.InnerException == null; }
            Check(rejected, "failed HRESULT out identifier never becomes cleanup authority");
        }

        using (var fake = new Tables(snapshot))
        {
            using var session = NtfsVssFixtureOwner.NativeSession.ForTests(fake.Backup, IntPtr.Zero, snapshot);
            Check(session.Cleanup(), "owned snapshot is confirmed absent before release");
            Check(fake.Deleted == snapshot && fake.Queried == snapshot && fake.DeleteType == 3 && fake.Force == 0,
                "native GUID-by-value callbacks receive only this exact snapshot and nonforce delete");
            Check(string.Join(',', fake.Events) == "delete,properties,backup-release", "delete and absence query precede owner release");
            Check(session.Cleanup() && fake.Events.Count == 3, "repeated cleanup does not delete or release twice");
        }
        using (var fake = new Tables(snapshot) { PropertyResult = unchecked((int)0x80004005) })
        {
            using var session = NtfsVssFixtureOwner.NativeSession.ForTests(fake.Backup, IntPtr.Zero, snapshot);
            Check(!session.Cleanup() && fake.Events.Last() == "backup-release", "query failure releases resources but never confirms cleanup");
        }
        foreach (int state in new[] { NtfsVssNativeSession.AsyncFinished, NtfsVssNativeSession.AsyncCancelled, unchecked((int)0x80004005) })
        {
            using var fake = new Tables(snapshot) { AsyncState = state };
            using var session = NtfsVssFixtureOwner.NativeSession.ForTests(fake.Backup, fake.Async, snapshot, creating: true);
            Check(session.Cleanup(), "cancelled operation settles its actual terminal state before cleanup");
            Check(string.Join(',', fake.Events) == "cancel,wait,query,delete,properties,async-release,backup-release", "async cancellation settles before owned delete and release");
        }
        foreach (int state in new[] { 0, 1, int.MaxValue, NtfsVssNativeSession.AsyncPending })
        {
            using var fake = new Tables(snapshot) { AsyncState = state };
            using var session = NtfsVssFixtureOwner.NativeSession.ForTests(fake.Backup, fake.Async, snapshot, creating: true);
            Check(!session.Cleanup() && fake.Deleted == Guid.Empty && fake.Queried == Guid.Empty,
                "unknown or pending async state cannot authorize deletion or detach");
        }
        using (var fake = new Tables(snapshot) { QueryResult = unchecked((int)0x80004005) })
        {
            using var session = NtfsVssFixtureOwner.NativeSession.ForTests(fake.Backup, fake.Async, snapshot, creating: true);
            Check(!session.Cleanup() && fake.Deleted == Guid.Empty, "failed async status cannot authorize snapshot cleanup");
        }
        using (var fake = new Tables(snapshot))
        {
            using var session = NtfsVssFixtureOwner.NativeSession.ForTests(fake.Backup, IntPtr.Zero, snapshot, creating: true);
            Check(!session.Cleanup() && fake.Deleted == Guid.Empty, "creation started without a valid async owner remains unconfirmed");
        }
        using (var fake = new Tables(snapshot))
        {
            using var session = NtfsVssFixtureOwner.NativeSession.ForTests(fake.Backup, IntPtr.Zero, Guid.Empty);
            Check(session.Cleanup() && fake.Events.SequenceEqual(new[] { "backup-release" }), "requester without an added snapshot releases without any delete");
        }
        var evidence = new NtfsVssFixtureEvidence("fixture-vss-v1", "SnapshotReadVerified", 1, 1, 3, 4, 4, 4096, true, true, true, true, true, true);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(evidence));
        Check(json.RootElement.EnumerateObject().Count() == 14 && !json.RootElement.EnumerateObject().Any(property =>
            property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Id", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Device", StringComparison.OrdinalIgnoreCase)), "public report has only aggregate evidence fields");
        Check(NtfsVssFixtureOwner.RelativeSegments("d0/f.bin", false).SequenceEqual(new[] { "d0", "f.bin" }) &&
            NtfsVssFixtureOwner.RelativeSegments("", true).Length == 0, "owner accepts only namespace relative path spelling and the directory root");
        foreach (string path in new[] { "../f", "d/../f", "d//f", "d/", @"d\f", @"C:\private", "/private", "d/x:stream", "d/\u0085" })
        {
            bool rejected = false; try { NtfsVssFixtureOwner.RelativeSegments(path, false); } catch (NtfsVssProbeException error) { rejected = error.Code == "VssSnapshotNative"; }
            Check(rejected, "rooted traversal alternate separators and control characters are rejected before filesystem access");
        }
        using (var tables = new Tables(snapshot))
        {
            using var owner = NtfsVssFixtureOwner.ForTests(NtfsVssFixtureOwner.NativeSession.ForTests(tables.Backup, IntPtr.Zero, snapshot));
            bool moved = Task.Run(owner.Cleanup).GetAwaiter().GetResult();
            Check(!moved && tables.Events.Count == 0, "COM cleanup never migrates onto a task thread");
            Check(owner.Cleanup() && tables.Events.SequenceEqual(new[] { "delete", "properties", "backup-release" }), "original owner thread can finish cleanup after a rejected continuation");
        }
        using (var tables = new Tables(snapshot))
        {
            using var owner = NtfsVssFixtureOwner.ForTests(NtfsVssFixtureOwner.NativeSession.ForTests(tables.Backup, IntPtr.Zero, snapshot));
            using var reader = owner.OpenReaderForTests(new NoReadNative(tables.Events));
            bool duplicate = false; try { owner.OpenReaderForTests(new NoReadNative(tables.Events)); } catch (NtfsVssProbeException error) { duplicate = error.Code == "VssRawOpen"; }
            Check(duplicate, "one owner can issue only one fixed-budget raw reader");
            Check(owner.Cleanup() && tables.Events.SequenceEqual(new[] { "reader-close", "delete", "properties", "backup-release" }), "snapshot readers close before deletion query and COM release");
            bool late = false; try { _ = owner.SnapshotRoot; } catch (NtfsVssProbeException) { late = true; }
            Check(late, "closed owner cannot authorize late path callbacks");
        }
        return passed;
    }

    private sealed class NoReadNative(List<string> events) : NtfsSnapshotVolumeReader.INative
    {
        public NtfsSnapshotVolumeReader.ControlResult Facts(byte[] output) => throw new InvalidOperationException("UnexpectedFakeRead");
        public NtfsSnapshotVolumeReader.Result Seek(long offset) => throw new InvalidOperationException("UnexpectedFakeRead");
        public NtfsSnapshotVolumeReader.ReadResult Read(IntPtr buffer, int length) => throw new InvalidOperationException("UnexpectedFakeRead");
        public IntPtr Allocate(int bytes) => throw new InvalidOperationException("UnexpectedFakeRead");
        public void Free(IntPtr buffer) => throw new InvalidOperationException("UnexpectedFakeRead");
        public void Dispose() => events.Add("reader-close");
    }

    private sealed class Tables : IDisposable
    {
        internal IntPtr Backup { get; }
        internal IntPtr Async { get; }
        private readonly IntPtr backupTable, asyncTable;
        private readonly List<Delegate> callbacks = new();
        internal readonly List<string> Events = new();
        internal Guid Deleted, Queried;
        internal int DeleteType, Force;
        internal int AsyncState = NtfsVssNativeSession.AsyncFinished, QueryResult, PropertyResult = NtfsVssNativeSession.ObjectNotFound;
        internal Tables(Guid expected)
        {
            backupTable = Marshal.AllocHGlobal(45 * IntPtr.Size); asyncTable = Marshal.AllocHGlobal(6 * IntPtr.Size);
            Backup = Marshal.AllocHGlobal(IntPtr.Size); Async = Marshal.AllocHGlobal(IntPtr.Size);
            for (int i = 0; i < 45; i++) Marshal.WriteIntPtr(backupTable, i * IntPtr.Size, IntPtr.Zero);
            for (int i = 0; i < 6; i++) Marshal.WriteIntPtr(asyncTable, i * IntPtr.Size, IntPtr.Zero);
            Marshal.WriteIntPtr(Backup, backupTable); Marshal.WriteIntPtr(Async, asyncTable);
            Set(backupTable, 2, new NtfsVssNativeSession.Release(self => { Events.Add("backup-release"); return 0; }));
            Set(asyncTable, 2, new NtfsVssNativeSession.Release(self => { Events.Add("async-release"); return 0; }));
            Set(asyncTable, 3, new NtfsVssNativeSession.NoArgs(self => { Events.Add("cancel"); return 0; }));
            Set(asyncTable, 4, new NtfsVssNativeSession.Wait((self, milliseconds) => { Events.Add("wait"); return 0; }));
            Set(asyncTable, 5, new NtfsVssNativeSession.QueryAsync((IntPtr self, out int status, IntPtr reserved) =>
            { Events.Add("query"); status = AsyncState; return QueryResult; }));
            Set(backupTable, NtfsVssNativeSession.DeleteSlot, new NtfsVssNativeSession.Delete(
                (IntPtr self, Guid id, int type, int force, out int count, out Guid remaining) =>
                { Events.Add("delete"); Deleted = id; DeleteType = type; Force = force; count = id == expected ? 1 : 0; remaining = Guid.Empty; return 0; }));
            Set(backupTable, NtfsVssNativeSession.PropertiesSlot, new NtfsVssNativeSession.Properties(
                (IntPtr self, Guid id, out NtfsVssNativeSession.SnapshotProperties properties) =>
                { Events.Add("properties"); Queried = id; properties = default; return PropertyResult; }));
        }
        private void Set(IntPtr table, int slot, Delegate callback)
        { callbacks.Add(callback); Marshal.WriteIntPtr(table, slot * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(callback)); }
        public void Dispose()
        { GC.KeepAlive(callbacks); Marshal.FreeHGlobal(Backup); Marshal.FreeHGlobal(Async); Marshal.FreeHGlobal(backupTable); Marshal.FreeHGlobal(asyncTable); }
    }
}
