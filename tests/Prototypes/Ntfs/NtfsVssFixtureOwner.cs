using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
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
    private static void Require([DoesNotReturnIf(false)] bool value, string code, int error = 0) => NtfsVssNativeSession.Require(value, code, error);
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
            var value = NtfsSnapshotMetadata.Read(path, directory, scope.Facts.Serial, token);
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
    internal sealed class NativeSession : IDisposable
    {
        private readonly NtfsVssNativeSession inner;
        internal NativeSession(string? ownerPath = null) { inner = new(ownerPath); }
        private NativeSession(NtfsVssNativeSession inner) { this.inner = inner; }
        internal static NativeSession ForTests(IntPtr backup, IntPtr async, Guid snapshot, bool creating = false) =>
            new(NtfsVssNativeSession.ForTests(backup, async, snapshot, creating));
        internal string Create(NtfsVssFixtureOwner owner, CancellationToken token)
        {
            Require(ReferenceEquals(owner.session, this) && !owner.scope.IsSynthetic && owner.ownerThread == Environment.CurrentManagedThreadId, "VssFixtureGuard");
            return inner.Create(owner.scope.Volume, token);
        }
        internal bool Cleanup() => inner.Cleanup();
        public void Dispose() => inner.Dispose();
    }
}
