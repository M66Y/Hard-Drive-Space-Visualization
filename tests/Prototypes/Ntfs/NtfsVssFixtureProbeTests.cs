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
        Check(IntPtr.Size == 8 && Marshal.SizeOf<NtfsVssFixtureProbe.SnapshotProperties>() == 128, "x64 snapshot property ABI size");
        foreach (var pair in new (string Field, int Offset)[] { ("Snapshot", 0), ("Set", 16), ("Count", 32), ("Device", 40),
            ("Original", 48), ("OriginatingMachine", 56), ("ServiceMachine", 64), ("ExposedName", 72), ("ExposedPath", 80),
            ("Provider", 88), ("Attributes", 104), ("Created", 112), ("State", 120) })
            Check(Marshal.OffsetOf<NtfsVssFixtureProbe.SnapshotProperties>(pair.Field).ToInt32() == pair.Offset, "snapshot property field offset");
        Check(NtfsVssFixtureProbe.InitializeSlot == 5 && NtfsVssFixtureProbe.ContextSlot == 35 && NtfsVssFixtureProbe.StartSlot == 36 &&
            NtfsVssFixtureProbe.AddSlot == 37 && NtfsVssFixtureProbe.SnapshotSlot == 38 && NtfsVssFixtureProbe.DeleteSlot == 39 &&
            NtfsVssFixtureProbe.PropertiesSlot == 42 && NtfsVssFixtureProbe.SupportedSlot == 44, "SDK vtable positions include IUnknown");
        Guid snapshot = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), set = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
        string volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\", device = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1";
        bool Binding(Guid? id = null, Guid? returnedSet = null, int count = 1, Guid? provider = null, int attributes = 0x10,
            int state = 12, string? source = null, string? path = null) => NtfsVssFixtureProbe.BindingValid(snapshot, set,
                id ?? snapshot, returnedSet ?? set, count, provider ?? NtfsVssFixtureProbe.SystemProvider, attributes, state,
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
        Check(NtfsVssFixtureProbe.AdoptGuid(0, snapshot, "VssAddVolume") == snapshot, "successful returned identifier becomes owned");
        foreach (var tuple in new[] { (unchecked((int)0x80004005), snapshot), (0, Guid.Empty) })
        {
            bool rejected = false;
            try { _ = NtfsVssFixtureProbe.AdoptGuid(tuple.Item1, tuple.Item2, "VssAddVolume"); }
            catch (NtfsVssProbeException error) { rejected = error.Code == "VssAddVolume" && error.Message == error.Code && error.InnerException == null; }
            Check(rejected, "failed HRESULT out identifier never becomes cleanup authority");
        }

        using (var fake = new Tables(snapshot))
        {
            using var session = NtfsVssFixtureProbe.NativeSession.ForTests(fake.Backup, IntPtr.Zero, snapshot);
            Check(session.Cleanup(), "owned snapshot is confirmed absent before release");
            Check(fake.Deleted == snapshot && fake.Queried == snapshot && fake.DeleteType == 3 && fake.Force == 0,
                "native GUID-by-value callbacks receive only this exact snapshot and nonforce delete");
            Check(string.Join(',', fake.Events) == "delete,properties,backup-release", "delete and absence query precede owner release");
            Check(session.Cleanup() && fake.Events.Count == 3, "repeated cleanup does not delete or release twice");
        }
        using (var fake = new Tables(snapshot) { PropertyResult = unchecked((int)0x80004005) })
        {
            using var session = NtfsVssFixtureProbe.NativeSession.ForTests(fake.Backup, IntPtr.Zero, snapshot);
            Check(!session.Cleanup() && fake.Events.Last() == "backup-release", "query failure releases resources but never confirms cleanup");
        }
        foreach (int state in new[] { NtfsVssFixtureProbe.AsyncFinished, NtfsVssFixtureProbe.AsyncCancelled, unchecked((int)0x80004005) })
        {
            using var fake = new Tables(snapshot) { AsyncState = state };
            using var session = NtfsVssFixtureProbe.NativeSession.ForTests(fake.Backup, fake.Async, snapshot, creating: true);
            Check(session.Cleanup(), "cancelled operation settles its actual terminal state before cleanup");
            Check(string.Join(',', fake.Events) == "cancel,wait,query,delete,properties,async-release,backup-release", "async cancellation settles before owned delete and release");
        }
        foreach (int state in new[] { 0, 1, int.MaxValue, NtfsVssFixtureProbe.AsyncPending })
        {
            using var fake = new Tables(snapshot) { AsyncState = state };
            using var session = NtfsVssFixtureProbe.NativeSession.ForTests(fake.Backup, fake.Async, snapshot, creating: true);
            Check(!session.Cleanup() && fake.Deleted == Guid.Empty && fake.Queried == Guid.Empty,
                "unknown or pending async state cannot authorize deletion or detach");
        }
        using (var fake = new Tables(snapshot) { QueryResult = unchecked((int)0x80004005) })
        {
            using var session = NtfsVssFixtureProbe.NativeSession.ForTests(fake.Backup, fake.Async, snapshot, creating: true);
            Check(!session.Cleanup() && fake.Deleted == Guid.Empty, "failed async status cannot authorize snapshot cleanup");
        }
        using (var fake = new Tables(snapshot))
        {
            using var session = NtfsVssFixtureProbe.NativeSession.ForTests(fake.Backup, IntPtr.Zero, snapshot, creating: true);
            Check(!session.Cleanup() && fake.Deleted == Guid.Empty, "creation started without a valid async owner remains unconfirmed");
        }
        using (var fake = new Tables(snapshot))
        {
            using var session = NtfsVssFixtureProbe.NativeSession.ForTests(fake.Backup, IntPtr.Zero, Guid.Empty);
            Check(session.Cleanup() && fake.Events.SequenceEqual(new[] { "backup-release" }), "requester without an added snapshot releases without any delete");
        }
        var evidence = new NtfsVssFixtureEvidence("fixture-vss-v1", "SnapshotReadVerified", 1, 1, 3, 4, 4, 4096, true, true, true, true, true, true);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(evidence));
        Check(json.RootElement.EnumerateObject().Count() == 14 && !json.RootElement.EnumerateObject().Any(property =>
            property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Id", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Device", StringComparison.OrdinalIgnoreCase)), "public report has only aggregate evidence fields");
        return passed;
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
        internal int AsyncState = NtfsVssFixtureProbe.AsyncFinished, QueryResult, PropertyResult = NtfsVssFixtureProbe.ObjectNotFound;
        internal Tables(Guid expected)
        {
            backupTable = Marshal.AllocHGlobal(45 * IntPtr.Size); asyncTable = Marshal.AllocHGlobal(6 * IntPtr.Size);
            Backup = Marshal.AllocHGlobal(IntPtr.Size); Async = Marshal.AllocHGlobal(IntPtr.Size);
            for (int i = 0; i < 45; i++) Marshal.WriteIntPtr(backupTable, i * IntPtr.Size, IntPtr.Zero);
            for (int i = 0; i < 6; i++) Marshal.WriteIntPtr(asyncTable, i * IntPtr.Size, IntPtr.Zero);
            Marshal.WriteIntPtr(Backup, backupTable); Marshal.WriteIntPtr(Async, asyncTable);
            Set(backupTable, 2, new NtfsVssFixtureProbe.NativeSession.Release(self => { Events.Add("backup-release"); return 0; }));
            Set(asyncTable, 2, new NtfsVssFixtureProbe.NativeSession.Release(self => { Events.Add("async-release"); return 0; }));
            Set(asyncTable, 3, new NtfsVssFixtureProbe.NativeSession.NoArgs(self => { Events.Add("cancel"); return 0; }));
            Set(asyncTable, 4, new NtfsVssFixtureProbe.NativeSession.Wait((self, milliseconds) => { Events.Add("wait"); return 0; }));
            Set(asyncTable, 5, new NtfsVssFixtureProbe.NativeSession.QueryAsync((IntPtr self, out int status, IntPtr reserved) =>
            { Events.Add("query"); status = AsyncState; return QueryResult; }));
            Set(backupTable, NtfsVssFixtureProbe.DeleteSlot, new NtfsVssFixtureProbe.NativeSession.Delete(
                (IntPtr self, Guid id, int type, int force, out int count, out Guid remaining) =>
                { Events.Add("delete"); Deleted = id; DeleteType = type; Force = force; count = id == expected ? 1 : 0; remaining = Guid.Empty; return 0; }));
            Set(backupTable, NtfsVssFixtureProbe.PropertiesSlot, new NtfsVssFixtureProbe.NativeSession.Properties(
                (IntPtr self, Guid id, out NtfsVssFixtureProbe.SnapshotProperties properties) =>
                { Events.Add("properties"); Queried = id; properties = default; return PropertyResult; }));
        }
        private void Set(IntPtr table, int slot, Delegate callback)
        { callbacks.Add(callback); Marshal.WriteIntPtr(table, slot * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(callback)); }
        public void Dispose()
        { GC.KeepAlive(callbacks); Marshal.FreeHGlobal(Backup); Marshal.FreeHGlobal(Async); Marshal.FreeHGlobal(backupTable); Marshal.FreeHGlobal(asyncTable); }
    }
}
