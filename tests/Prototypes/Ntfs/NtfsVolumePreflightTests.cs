using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal static class NtfsVolumePreflightTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("Preflight check " + name); passed++; Console.WriteLine("PASS preflight " + name); }
        var fake = new Fake(); var result = NtfsVolumePreflight.InspectForTests("d:\\", fake);
        Check(result is { Status: "MetadataAvailable", Code: "None", FileSystem: "NTFS", OpenedWithZeroAccess: true,
            SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024, VolumeBytes: 1073741824, MftValidDataBytes: 67108864, MftSlots: 65536 },
            "safe filesystem geometry and MFT length are retained");
        Check(fake.Roots.SequenceEqual(new[] { "D:\\", "D:\\" }) && fake.OpenCount == 1 && fake.ControlCount == 1 && fake.LastBuffer == 128 && fake.Handle.IsClosed,
            "canonical root is opened once for one bounded metadata control and then closed");
        Check(fake.ZeroAccessModes.SequenceEqual(new[] { true }), "default mode never silently requests read permission");
        var explicitRead = new Fake(); var explicitResult = NtfsVolumePreflight.InspectForTests("D:\\", explicitRead, readMetadataHandle: true);
        Check(explicitResult.Status == "MetadataAvailable" && !explicitResult.OpenedWithZeroAccess && explicitRead.ZeroAccessModes.SequenceEqual(new[] { false }) &&
            explicitRead.ControlCount == 1 && explicitRead.LastBuffer == 128 && explicitRead.Handle.IsClosed,
            "explicit read-permission handle still performs exactly one bounded metadata query and closes");
        Check(typeof(NtfsVolumePreflight.INative).GetMethods().All(method => method.Name is "FileSystem" or "OpenMetadataRoot" or "Metadata"),
            "preflight adapter has no raw read record enumeration or snapshot operation");
        var readDenied = new Fake { FailStep = "open" }; var deniedResult = NtfsVolumePreflight.InspectForTests("D:\\", readDenied, readMetadataHandle: true);
        Check(deniedResult.Code == "PreflightOpen" && !deniedResult.OpenedWithZeroAccess && deniedResult.MftSlots == null && readDenied.OpenCount == 1 && readDenied.ControlCount == 0,
            "explicit read denial does not elevate retry or invent metadata");
        var limited = new Fake { ReadHandleRequired = true }; var limitedResult = NtfsVolumePreflight.InspectForTests("D:\\", limited);
        Check(limitedResult.Code == "PreflightQuery" && limitedResult.NativeError == 1 && limitedResult.OpenedWithZeroAccess && limited.OpenCount == 1 &&
            limited.ZeroAccessModes.SequenceEqual(new[] { true }), "metadata-control denial never silently retries with wider access");
        var allowedRead = new Fake { ReadHandleRequired = true }; var readResult = NtfsVolumePreflight.InspectForTests("D:\\", allowedRead, readMetadataHandle: true);
        Check(readResult.Status == "MetadataAvailable" && !readResult.OpenedWithZeroAccess && allowedRead.OpenCount == 1 && allowedRead.ControlCount == 1,
            "explicit permission mode can query metadata without introducing any read operation");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Check(json.RootElement.EnumerateObject().Count() == 12 && !JsonSerializer.Serialize(result).Contains("private", StringComparison.Ordinal) &&
            !json.RootElement.EnumerateObject().Any(value => value.Name.Contains("Serial", StringComparison.OrdinalIgnoreCase) ||
                value.Name.Contains("Lcn", StringComparison.OrdinalIgnoreCase) || value.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) || value.Name.Contains("Identity", StringComparison.OrdinalIgnoreCase)),
            "report does not carry root volume identity physical locations or private text");
        byte[] original = fake.Bytes.ToArray();
        _ = NtfsVolumePreflight.ParseNative(fake.Bytes, 104);
        Check(fake.Bytes.SequenceEqual(original), "parser leaves caller metadata buffer unchanged");
        var large = fake.Bytes.ToArray(); W64(large, 8, 16L * 1024 * 1024 * 1024 * 1024 / 512); W64(large, 16, 16L * 1024 * 1024 * 1024 * 1024 / 4096);
        W64(large, 56, 512L * 1024 * 1024); var largeReport = NtfsVolumePreflight.ParseNative(large, 96);
        Check(largeReport.Status == "MetadataAvailable" && largeReport.MftSlots == 524288 && largeReport.VolumeBytes == 16L * 1024 * 1024 * 1024 * 1024,
            "metadata can describe volumes beyond prototype raw budgets without reading or approving those records");
        var changedPrivate = fake.Bytes.ToArray(); Array.Fill(changedPrivate, (byte)0xff, 0, 8); Array.Fill(changedPrivate, (byte)0xfe, 64, 24);
        Check(NtfsVolumePreflight.ParseNative(changedPrivate, 104) == result, "serial and physical location bytes cannot change safe metadata report");
        foreach (string root in new[] { "D:", "D:/", "D:\\private", @"\\server\share\", @"\\.\D:", "1:\\", "Ｄ:\\", "", "D:\\\\" })
        {
            var invalid = new Fake(); var rejected = NtfsVolumePreflight.InspectForTests(root, invalid);
            Check(rejected.Code == "PreflightRoot" && invalid.Roots.Count == 0 && invalid.OpenCount == 0, "non-root paths are rejected before native calls");
        }
        foreach (string format in new[] { "ReFS", "exFAT", "FAT32", "private-label" })
        {
            var unsupported = new Fake { Format = format }; var report = NtfsVolumePreflight.InspectForTests("D:\\", unsupported);
            Check(report.Status == "Unsupported" && report.Code == "PreflightNotNtfs" && unsupported.OpenCount == 0 &&
                (format != "private-label" || report.FileSystem == "Unknown"), "only known filesystem spelling is exposed and unsupported volumes are not queried");
        }
        foreach (string step in new[] { "format", "open", "control" })
        {
            var broken = new Fake { FailStep = step }; var report = NtfsVolumePreflight.InspectForTests("D:\\", broken);
            Check(report.Status == "Unavailable" && report.NativeError == 5 && report.MftSlots == null &&
                (step == "format" || broken.Handle.IsClosed), "native denial stays unavailable with no invented zero sizes");
        }
        foreach (string step in new[] { "format", "open", "control" })
        foreach (bool forgedCancel in new[] { false, true })
        {
            var broken = new Fake { ThrowStep = step, ForgedCancel = forgedCancel }; var report = NtfsVolumePreflight.InspectForTests("D:\\", broken);
            Check(report.Code == "PreflightNative" && report.NativeError == 0 && !JsonSerializer.Serialize(report).Contains("private", StringComparison.Ordinal),
                "external exceptions and unrelated cancellation cannot leak private messages");
        }
        foreach (int used in new[] { -1, 0, 64, 95, 129, int.MaxValue }) Check(NtfsVolumePreflight.ParseNative(fake.Bytes, used).Code == "PreflightPayload", "metadata return byte count is bounded");
        Check(NtfsVolumePreflight.ParseNative(fake.Bytes.AsSpan(0, 127), 104).Code == "PreflightPayload", "caller metadata buffer must have exact fixed capacity");
        foreach (var alteration in new (int Offset, long Value, bool Wide)[] { (8, -1, true), (8, long.MaxValue, true), (16, 0, true),
            (16, long.MaxValue, true), (40, 0, false), (40, 1000, false), (40, 131072, false), (44, 513, false), (44, 256, false),
            (44, 4194304, false), (48, 0, false), (48, 768, false), (48, 131072, false), (16, 99999999, true) })
        {
            byte[] bytes = fake.Bytes.ToArray();
            if (alteration.Wide) W64(bytes, alteration.Offset, alteration.Value); else W32(bytes, alteration.Offset, checked((int)alteration.Value));
            Check(NtfsVolumePreflight.ParseNative(bytes, 104).Code == "PreflightGeometry", "invalid and overflowing geometry is rejected before length calculations");
        }
        foreach (long valid in new[] { -1, 0, 1025, long.MaxValue, 1073742848 })
        { byte[] bytes = fake.Bytes.ToArray(); W64(bytes, 56, valid); Check(NtfsVolumePreflight.ParseNative(bytes, 104).Code == "PreflightMftLength", "invalid or partial-record MFT length is not a usable slot count"); }
        foreach (string step in new[] { "format", "open", "control" })
        {
            using var cancellation = new CancellationTokenSource(); var cancelled = new Fake { Callback = at => { if (at == step) cancellation.Cancel(); } }; bool caught = false;
            try { _ = NtfsVolumePreflight.InspectForTests("D:\\", cancelled, cancellation.Token); }
            catch (OperationCanceledException error) { caught = error.CancellationToken == cancellation.Token && !error.Message.Contains("private", StringComparison.Ordinal); }
            Check(caught && (step == "format" || cancelled.Handle.IsClosed), "caller cancellation wins and closes already acquired metadata handles");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); var cancelled = new Fake(); bool caught = false;
            try { _ = NtfsVolumePreflight.InspectForTests("D:\\", cancelled, cancellation.Token); } catch (OperationCanceledException) { caught = true; }
            Check(caught && cancelled.Roots.Count == 0, "pre-cancelled probe performs no native call");
        }
        return passed;
    }
    private sealed class Fake : NtfsVolumePreflight.INative
    {
        internal readonly byte[] Bytes = new byte[128];
        internal readonly List<string> Roots = new();
        internal readonly List<bool> ZeroAccessModes = new();
        internal SafeFileHandle Handle = new((IntPtr)71, false);
        internal string Format = "NTFS", FailStep = "", ThrowStep = "";
        internal bool ForgedCancel, ReadHandleRequired;
        internal int OpenCount, ControlCount, LastBuffer;
        internal Action<string>? Callback;
        internal Fake()
        {
            W64(Bytes, 0, 81985529216486895); W64(Bytes, 8, 1073741824 / 512); W64(Bytes, 16, 1073741824 / 4096);
            W32(Bytes, 40, 512); W32(Bytes, 44, 4096); W32(Bytes, 48, 1024); W64(Bytes, 56, 67108864); W64(Bytes, 64, 91234);
        }
        private void Before(string step)
        { Callback?.Invoke(step); if (ThrowStep == step) { if (ForgedCancel) throw new OperationCanceledException("private-marker"); throw new IOException("private-marker"); } }
        public NtfsVolumePreflight.FormatResult FileSystem(string root)
        { Roots.Add(root); Before("format"); return new(FailStep != "format", Format, 5); }
        public NtfsVolumePreflight.OpenResult OpenMetadataRoot(string root, bool zeroAccess)
        { Roots.Add(root); ZeroAccessModes.Add(zeroAccess); OpenCount++; Before("open"); if (FailStep == "open") { Handle.Dispose(); Handle = new SafeFileHandle((IntPtr)(-1), false); } return new(Handle, FailStep == "open" ? 5 : 0); }
        public NtfsVolumePreflight.MetadataResult Metadata(SafeFileHandle handle, byte[] output)
        {
            ControlCount++; LastBuffer = output.Length; Before("control"); Bytes.CopyTo(output, 0);
            bool requiresAccess = ReadHandleRequired && ZeroAccessModes.Last(); return new(FailStep != "control" && !requiresAccess, 104, requiresAccess ? 1 : 5);
        }
    }
    private static void W32(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void W64(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
}
