using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsSpecialObservation(FileIdentity Identity, long LogicalBytes, long AllocatedBytes,
    uint Links, bool DeletePending, bool Directory, uint Attributes, long LastWrite, long ChangeTime,
    ushort CompressionFormat, byte UnitShift, byte ChunkShift, byte ClusterShift);

// Test-only, default-stream lookup for the two explicitly supported reasons.
// Read sharing is not an oplock, a residency detector, or a volume snapshot.
internal static class NtfsSpecialLease
{
    private const uint ReadAttributesAndData = 0x81, ShareRead = 1;
    private const uint BackupAndOpenReparse = 0x02000000 | 0x00200000;
    private const uint Compressed = 0x800, Sparse = 0x200;
    private const uint UnsupportedAttributes = 0x10 | 0x40 | 0x400 | 0x1000 | 0x4000 | 0x40000 | 0x400000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        public long Creation, Access, Write, Change;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IdInfo { public ulong Volume, Low, High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long Allocation, EndOfFile;
        public uint Links;
        public byte DeletePending, Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompressionInfo
    {
        public long Size;
        public ushort Format;
        public byte UnitShift, ChunkShift, ClusterShift, Reserved1, Reserved2, Reserved3;
    }

    internal static (int Basic, int Id, int Standard, int Compression) LayoutSizes =>
        (Marshal.SizeOf<BasicInfo>(), Marshal.SizeOf<IdInfo>(), Marshal.SizeOf<StandardInfo>(), Marshal.SizeOf<CompressionInfo>());

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool Basic(SafeFileHandle handle, int infoClass, out BasicInfo value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool Identity(SafeFileHandle handle, int infoClass, out IdInfo value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool Standard(SafeFileHandle handle, int infoClass, out StandardInfo value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool Compression(SafeFileHandle handle, int infoClass, out CompressionInfo value, uint size);

    private static string? RecordProblem(NtfsRecord record)
    {
        if (record == null || record.IsDirectory || record.BaseReference != 0 || (record.Reference >> 48) == 0 ||
            record.LookupReasons is not (NtfsLookupReason.Compressed or NtfsLookupReason.Sparse) ||
            record.LogicalBytes is not >= 0 || record.AllocatedBytes != null || record.Names == null ||
            record.Names.Count is < 1 or > 256 || record.LinkCount != record.Names.Count)
            return "UnsupportedObject";
        foreach (var name in record.Names)
            if (name == null || name.Namespace > 3 || (name.ParentReference >> 48) == 0 ||
                name.Name is not { Length: > 0 and <= 255 } || name.Name is "." or ".." ||
                name.Name.IndexOfAny(['\0', '\\', '/', ':']) >= 0)
                return "InvalidName";
        return VisibleNames(record) == 0 ? "MissingVisibleNames" : null;
    }

    private static int VisibleNames(NtfsRecord record) => record.Names.Where(name => name.Namespace != 2)
        .Select(name => (name.ParentReference, name.Name)).Distinct().Count();

    internal static string? Preflight(NtfsRecord record, string path)
    {
        string? problem = RecordProblem(record);
        if (problem != null) return problem;
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path)) return "InvalidPath";
        return record.Names.Any(name => name.Namespace != 2 && name.Name == Path.GetFileName(path)) ? null : "NameMismatch";
    }

    internal static string? Validate(ulong volume, NtfsRecord record, NtfsSpecialObservation first,
        NtfsSpecialObservation second)
    {
        string? problem = RecordProblem(record);
        if (problem != null) return problem;
        int names = VisibleNames(record);
        string? ObservationProblem(NtfsSpecialObservation sample)
        {
            if (sample == null) return "InvalidSnapshot";
            if (sample.Identity != new FileIdentity(volume, record.Reference, 0)) return "IdentityChanged";
            if (sample.LogicalBytes != record.LogicalBytes) return "LogicalChanged";
            if (sample.AllocatedBytes < 0 || sample.Links != names || sample.DeletePending || sample.Directory)
                return "InvalidSnapshot";
            if ((sample.Attributes & UnsupportedAttributes) != 0) return "UnsupportedAttributes";
            uint expected = record.LookupReasons == NtfsLookupReason.Compressed ? Compressed : Sparse;
            if ((sample.Attributes & (Compressed | Sparse)) != expected) return "AttributeMismatch";
            ushort expectedFormat = record.LookupReasons == NtfsLookupReason.Compressed ? (ushort)2 : (ushort)0;
            if (sample.CompressionFormat != expectedFormat) return "UnsupportedCompressionFormat";
            if (sample.UnitShift > 63 || sample.ChunkShift > 63 || sample.ClusterShift > 63)
                return "InvalidCompressionDescription";
            if (!ValidTime(sample.LastWrite) || !ValidTime(sample.ChangeTime)) return "InvalidTimestamp";
            return null;
        }
        problem = ObservationProblem(first) ?? ObservationProblem(second);
        if (problem != null) return problem;
        return first == second ? null : "SnapshotChanged";
    }

    private static bool ValidTime(long value)
    {
        if (value < 0) return false;
        try { _ = DateTime.FromFileTimeUtc(value); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    internal static NtfsLeaseResult Read(ulong volume, NtfsRecord record, string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        int opened = 0, reads = 0;
        NtfsLeaseResult Fail(string code) => new(false, null, code, opened, reads);
        string? problem = Preflight(record, path);
        if (problem != null) return Fail(problem);
        if (!OperatingSystem.IsWindows()) return Fail("UnsupportedPlatform");

        using var handle = NativeStorage.CreateFileW(NativeStorage.LongPath(path), ReadAttributesAndData, ShareRead,
            IntPtr.Zero, 3, BackupAndOpenReparse, IntPtr.Zero);
        int openError = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (!handle.IsInvalid) opened = 1;
        token.ThrowIfCancellationRequested();
        if (handle.IsInvalid) return Fail("OpenError-" + openError);

        NtfsSpecialObservation? Capture(out string code)
        {
            token.ThrowIfCancellationRequested();
            reads++;
            code = "NativeReadFailed";
            bool read = Basic(handle, 0, out var before, (uint)Marshal.SizeOf<BasicInfo>());
            token.ThrowIfCancellationRequested();
            if (!read) return null;
            token.ThrowIfCancellationRequested();
            read = Identity(handle, 18, out var id, (uint)Marshal.SizeOf<IdInfo>());
            token.ThrowIfCancellationRequested();
            if (!read) return null;
            token.ThrowIfCancellationRequested();
            read = Standard(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>());
            token.ThrowIfCancellationRequested();
            if (!read) return null;
            token.ThrowIfCancellationRequested();
            read = Compression(handle, 8, out var compression, (uint)Marshal.SizeOf<CompressionInfo>());
            token.ThrowIfCancellationRequested();
            if (!read) return null;
            token.ThrowIfCancellationRequested();
            read = Basic(handle, 0, out var after, (uint)Marshal.SizeOf<BasicInfo>());
            token.ThrowIfCancellationRequested();
            if (!read) return null;
            if (before.Write != after.Write || before.Change != after.Change || before.Attributes != after.Attributes)
            { code = "SnapshotChanged"; return null; }
            if (standard.Allocation < 0) { code = "InvalidSnapshot"; return null; }
            code = "None";
            return new(new(id.Volume, id.Low, id.High), standard.EndOfFile, compression.Size, standard.Links,
                standard.DeletePending != 0, standard.Directory != 0, after.Attributes, after.Write, after.Change,
                compression.Format, compression.UnitShift, compression.ChunkShift, compression.ClusterShift);
        }

        var first = Capture(out string code);
        if (first == null) return Fail(code);
        problem = Validate(volume, record, first, first);
        token.ThrowIfCancellationRequested();
        if (problem != null) return Fail(problem);
        var second = Capture(out code);
        if (second == null) return Fail(code);
        problem = Validate(volume, record, first, second);
        token.ThrowIfCancellationRequested();
        if (problem != null) return Fail(problem);
        var modified = DateTime.FromFileTimeUtc(first.LastWrite).ToLocalTime();
        token.ThrowIfCancellationRequested();
        var storage = new FileStorageInfo(first.Identity, first.AllocatedBytes, first.Links, false, first.LogicalBytes, modified);
        return new(true, storage, "SpecialLease", opened, reads);
    }
}
