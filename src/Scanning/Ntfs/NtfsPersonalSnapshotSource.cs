using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed class NtfsPersonalSnapshotOpenException(string code, int nativeError, bool cleanupConfirmed) : IOException(code)
{
    internal string Code { get; } = code;
    internal int NativeError { get; } = nativeError;
    internal bool CleanupConfirmed { get; } = cleanupConfirmed;
}

// Only the elevated, authenticated worker calls this factory. Source names,
// snapshot IDs and the device returned by VSS remain private to this owner.
// No caller can supply an existing snapshot/device or mint a validated source.
internal sealed class NtfsPersonalSnapshotSource : IDisposable
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly object gate = new();
    private readonly NtfsVssNativeSession session = new();
    private readonly List<IDisposable> cursors = new();
    private readonly NtfsSnapshotVolumeFacts liveFacts;
    private string snapshotRoot = "", snapshotDataRoot = "";
    private FileStorageInfo? rootStorage;
    private NtfsSnapshotVolumeReader? reader;
    private bool readerIssued, closed, cleanupConfirmed;

    private NtfsPersonalSnapshotSource(NtfsSnapshotVolumeFacts facts)
    { liveFacts = facts; }

    internal DateTime SnapshotCreatedUtc { get { lock (gate) { Active(); return session.SnapshotCreatedUtc; } } }
    internal string SnapshotRoot { get { lock (gate) { Active(); return snapshotRoot; } } }
    internal string SnapshotDataRoot { get { lock (gate) { Active(); return snapshotDataRoot; } } }
    internal FileStorageInfo RootStorage { get { lock (gate) { Active(); return rootStorage ?? throw new NtfsVssProbeException("VssSnapshotNative"); } } }

    internal static string NormalizeSelectedPath(string path)
    {
        Require(path is { Length: >= 3 and <= 32760 } && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\', "VssPersonalPath");
        Require(!path.Any(char.IsControl) && !path.Contains('/'), "VssPersonalPath");
        string suffix = path[3..].TrimEnd('\\');
        if (suffix.Length != 0)
            foreach (string part in suffix.Split('\\'))
                Require(part.Length is >= 1 and <= 255 && part is not ("." or "..") && part[^1] is not ('.' or ' ') &&
                    !part.Any(c => c is ':' or '<' or '>' or '"' or '|' or '?' or '*'), "VssPersonalPath");
        return char.ToUpperInvariant(path[0]) + ":\\" + suffix;
    }

    internal static NtfsPersonalSnapshotSource Create(string selectedPath, CancellationToken token = default)
    {
        NtfsPersonalSnapshotSource? source = null;
        var pins = new List<SafeFileHandle>();
        var pinnedIdentities = new List<FileIdentity>();
        try
        {
            token.ThrowIfCancellationRequested();
            string path = NormalizeSelectedPath(selectedPath), drive = path[..3];
            using var identity = WindowsIdentity.GetCurrent();
            Require(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), "VssPersonalElevation");
            Require(GetDriveTypeW(drive) == 3, "VssPersonalVolume");
            string volume = VolumeName(drive);
            string suffix = path.Length == 3 ? "" : path[3..];
            string current = drive, canonical = volume;
            var root = Pin(current, canonical, null, token); pins.Add(root.Handle);
            pinnedIdentities.Add(root.Storage.Identity);
            ulong serial = root.Storage.Identity.Volume;
            FileStorageInfo selected = root.Storage;
            if (suffix.Length != 0)
                foreach (string part in suffix.Split('\\'))
                {
                    current = Path.Combine(current, part); canonical = canonical.TrimEnd('\\') + "\\" + part;
                    var next = Pin(current, canonical, serial, token); pins.Add(next.Handle); selected = next.Storage;
                    pinnedIdentities.Add(next.Storage.Identity);
                }
            var facts = ReadVolumeFacts(volume, serial, token);
            source = new(facts);
            source.snapshotRoot = source.session.Create(volume, token);
            source.snapshotDataRoot = source.snapshotRoot + "\\" + suffix;
            if (suffix.Length == 0) source.snapshotDataRoot = source.snapshotRoot + "\\";
            // An original ancestor might acquire a reparse tag while its name
            // remains pinned. Recheck every component in the immutable source
            // before allowing an operation through the selected snapshot path.
            string snapshotPath = source.snapshotRoot + "\\";
            Require(NtfsSnapshotMetadata.Read(snapshotPath, true, serial, token).Storage.Identity == pinnedIdentities[0], "VssSnapshotBinding");
            if (suffix.Length != 0)
            {
                int index = 1;
                foreach (string part in suffix.Split('\\'))
                {
                    snapshotPath = snapshotPath.TrimEnd('\\') + "\\" + part;
                    Require(NtfsSnapshotMetadata.Read(snapshotPath, true, serial, token).Storage.Identity == pinnedIdentities[index++], "VssSnapshotBinding");
                }
            }
            var snapshot = source.ReadNativeSize("", true, token);
            Require(snapshot.Storage.Identity == selected.Identity && !snapshot.DeletePending, "VssSnapshotBinding");
            source.rootStorage = snapshot.Storage;
            token.ThrowIfCancellationRequested(); return source;
        }
        catch (Exception error)
        {
            Func<bool>? close = source == null ? null : source.Cleanup;
            throw WrapOpenFailure(error, close);
        }
        finally { foreach (var handle in pins) handle.Dispose(); }
    }

    // Reporting-only seam, not a source factory: no path, handle or caller
    // declaration can become a validated snapshot through this method.
    internal static NtfsPersonalSnapshotOpenException WrapOpenFailure(Exception error, Func<bool>? close)
    {
        bool cleanup = close == null;
        if (close != null) { try { cleanup = close(); } catch { cleanup = false; } }
        string code = error is OperationCanceledException ? "VssCancelled" : error is NtfsVssProbeException controlled ? controlled.Code : "VssPersonalNative";
        int number = error is NtfsVssProbeException failure ? failure.Error : 0;
        return new NtfsPersonalSnapshotOpenException(code, number, cleanup);
    }

    internal NtfsSnapshotVolumeReader OpenReader(CancellationToken token = default)
    {
        lock (gate) { Active(); Require(ownerThread == Environment.CurrentManagedThreadId && !readerIssued, "VssRawOpen"); return NtfsSnapshotVolumeReader.OpenPersonal(this, token); }
    }
    internal sealed record ReadBinding(string Device, ulong Serial, long VolumeBytes);
    internal ReadBinding TakeReadBinding()
    {
        lock (gate)
        {
            Active(); Require(ownerThread == Environment.CurrentManagedThreadId && !readerIssued, "VssRawOpen"); readerIssued = true;
            return new(snapshotRoot, liveFacts.Serial, liveFacts.Geometry.VolumeBytes);
        }
    }
    internal void TrackReader(NtfsSnapshotVolumeReader value)
    { lock (gate) { Active(); Require(readerIssued && reader == null, "VssRawOpen"); reader = value; } }

    internal NtfsSnapshotNamespace.IBackend CreateNamespaceBackend()
    { lock (gate) { Active(); return new NamespaceBackend(this); } }
    private sealed class NamespaceBackend(NtfsPersonalSnapshotSource owner) : NtfsSnapshotNamespace.IBackend
    {
        public FileIdentity RootIdentity => owner.RootStorage.Identity;
        public DateTime? RootModified => owner.RootStorage.Modified;
        public NtfsSnapshotNamespace.ICursor OpenDirectory(NtfsSnapshotNamespaceEntry entry, string relativePath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Require(entry.Directory && !entry.Reparse && entry.Identity.Volume == RootIdentity.Volume, "VssSnapshotNative");
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
    internal FileStorageInfo ReadNative(string relative, bool directory, CancellationToken token = default) => ReadNativeSize(relative, directory, token).Storage;
    internal NtfsSnapshotNativeSize LookupSize(NtfsSnapshotNamespaceResult inventory, NtfsSnapshotSizeRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Require(inventory.RootIdentity == RootStorage.Identity && request.EntryIndex >= 0 && request.EntryIndex < inventory.Entries.Count, "VssSnapshotNative");
        var entry = inventory.Entries[request.EntryIndex];
        Require(entry.Identity == request.Identity && entry.Directory == request.Directory && !entry.Reparse, "VssSnapshotNative");
        var value = ReadNativeSize(inventory.RelativePath(request.EntryIndex), request.Directory, token);
        Require(value.Storage.Identity == request.Identity && (!request.ExpectedLogicalBytes.HasValue || value.Storage.LogicalBytes == request.ExpectedLogicalBytes.Value), "VssSnapshotNative");
        return value;
    }
    private NtfsSnapshotNativeSize ReadNativeSize(string relative, bool directory, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested(); string path = Resolve(relative, directory);
            var result = NtfsSnapshotMetadata.Read(path, directory, liveFacts.Serial, token);
            lock (gate) Active(); token.ThrowIfCancellationRequested(); return result;
        }
        catch (NtfsVssProbeException) { throw; }
        catch { token.ThrowIfCancellationRequested(); throw new NtfsVssProbeException("VssSnapshotNative"); }
    }
    private string Resolve(string relative, bool directory)
    {
        string root; lock (gate) { Active(); root = snapshotDataRoot; }
        string[] parts = RelativeSegments(relative, directory);
        string current = root.TrimEnd('\\');
        Require((File.GetAttributes(current + "\\") & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory, "VssSnapshotNative");
        for (int index = 0; index < parts.Length; index++)
        {
            current += "\\" + parts[index];
            var attributes = File.GetAttributes(current);
            Require((attributes & FileAttributes.ReparsePoint) == 0 && (index == parts.Length - 1 || (attributes & FileAttributes.Directory) != 0), "VssSnapshotNative");
        }
        return parts.Length == 0 ? root : current;
    }
    internal static string[] RelativeSegments(string relative, bool directory)
    {
        Require(relative != null && relative.Length <= 32768 && !Path.IsPathRooted(relative), "VssSnapshotNative");
        if (relative.Length == 0) { Require(directory, "VssSnapshotNative"); return []; }
        string[] parts = relative.Split('/');
        foreach (string part in parts) Require(part.Length is >= 1 and <= 255 && part is not ("." or "..") &&
            !part.Any(c => char.IsControl(c) || c is ':' or '/' or '\\'), "VssSnapshotNative");
        return parts;
    }
    private void Active() => Require(!closed, "VssCleanup");
    internal bool Cleanup()
    {
        lock (gate)
        {
            if (closed) return cleanupConfirmed;
            if (ownerThread != Environment.CurrentManagedThreadId) return false;
            closed = true; bool handlesClosed = true;
            try { reader?.Dispose(); if (reader != null) handlesClosed &= reader.BufferReleased; } catch { handlesClosed = false; }
            foreach (var cursor in cursors) { try { cursor.Dispose(); } catch { handlesClosed = false; } }
            cursors.Clear();
            bool sessionClosed;
            try { sessionClosed = session.Cleanup(); } catch { sessionClosed = false; }
            cleanupConfirmed = sessionClosed && handlesClosed; return cleanupConfirmed;
        }
    }
    public void Dispose() { _ = Cleanup(); }

    private static void Require([DoesNotReturnIf(false)] bool condition, string code, int error = 0) => NtfsVssNativeSession.Require(condition, code, error);
    private static (SafeFileHandle Handle, FileStorageInfo Storage) Pin(string path, string canonical, ulong? serial, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var handle = NativeStorage.CreateFileW(NativeStorage.LongPath(path), 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        try
        {
            Require(!handle.IsInvalid, "VssPersonalPath", Marshal.GetLastWin32Error());
            var final = new StringBuilder(32768);
            uint used = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 1);
            Require(used > 0 && used < final.Capacity && string.Equals(final.ToString().TrimEnd('\\'), canonical.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase), "VssPersonalPath");
            var storage = NtfsSnapshotMetadata.ReadHandle(handle, true, serial, token).Storage;
            return (handle, storage);
        }
        catch { handle.Dispose(); throw; }
    }
    private static string VolumeName(string drive)
    {
        var output = new StringBuilder(64);
        Require(GetVolumeNameForVolumeMountPointW(drive, output, (uint)output.Capacity), "VssPersonalVolume", Marshal.GetLastWin32Error());
        string result = output.ToString(); Require(NtfsVssNativeSession.IsVolumeGuid(result), "VssPersonalVolume");
        var format = new StringBuilder(32);
        Require(GetVolumeInformationW(result, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, format, (uint)format.Capacity) && format.ToString() == "NTFS", "VssPersonalVolume");
        return result;
    }
    private static NtfsSnapshotVolumeFacts ReadVolumeFacts(string volume, ulong serial, CancellationToken token)
    {
        using var handle = NativeStorage.CreateFileW(volume.TrimEnd('\\'), 0x80000000, NativeStorage.ShareAll, IntPtr.Zero, 3, 0, IntPtr.Zero);
        Require(!handle.IsInvalid, "VssRawOpen", Marshal.GetLastWin32Error()); token.ThrowIfCancellationRequested();
        byte[] bytes = new byte[128];
        Require(NativeStorage.DeviceIoControl(handle, 0x90064, null, 0, bytes, bytes.Length, out int used, IntPtr.Zero), "VssRawGeometry", Marshal.GetLastWin32Error());
        var safe = NtfsVolumePreflight.ParseNative(bytes, used);
        Require(safe.Status == "MetadataAvailable" && safe.SectorBytes == 512 && safe.ClusterBytes == 4096 && safe.RecordBytes == 1024 &&
            used >= 104 && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(100)) == 3 && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(102)) == 1 &&
            BinaryPrimitives.ReadUInt64LittleEndian(bytes) == serial, "VssRawGeometry");
        long clusters = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)), lcn = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(64));
        Require(lcn >= 0 && lcn < clusters, "VssRawGeometry"); token.ThrowIfCancellationRequested();
        return new(new(512, 4096, 1024, safe.VolumeBytes!.Value, clusters, lcn, safe.MftValidDataBytes!.Value), serial);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder output, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumeInformationW(string root, IntPtr label, uint labelSize, IntPtr serial, IntPtr maximumComponentLength, IntPtr flags, StringBuilder fileSystem, uint fileSystemSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder output, uint length, uint flags);
}
