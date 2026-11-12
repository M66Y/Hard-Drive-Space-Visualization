using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal readonly record struct NtfsSnapshotNamespaceIo(bool Success, int Error);
internal sealed record NtfsSnapshotNamespaceEntry(int Index, int ParentIndex, string Name, FileIdentity Identity,
    FileAttributes Attributes, bool Reparse)
{ internal bool Directory => (Attributes & FileAttributes.Directory) != 0; }
internal sealed record NtfsSnapshotNamespaceReport(string Revision, int Entries, int FileEntries, int Directories,
    int UniqueFileObjects, int ReparseEntries, int OpenedDirectories, int Pages, int Queries, int NameCharacters,
    int CompletedDirectories, bool CompleteWithinBoundary);
internal sealed record NtfsSnapshotNamespacePageEntry(string Name, ulong Low, ulong High, FileAttributes Attributes);
internal sealed class NtfsSnapshotNamespaceResult(NtfsSnapshotNamespaceReport report,
    IReadOnlyList<NtfsSnapshotNamespaceEntry> entries, IReadOnlyList<string> paths, FileIdentity rootIdentity)
{
    internal NtfsSnapshotNamespaceReport Report { get; } = report;
    internal IReadOnlyList<NtfsSnapshotNamespaceEntry> Entries { get; } = entries;
    internal FileIdentity RootIdentity { get; } = rootIdentity;
    internal string RelativePath(int index) => index >= 0 && index < paths.Count ? paths[index] :
        throw new NtfsSnapshotNamespaceException("NamespaceEntryIndex");
}
internal sealed class NtfsSnapshotNamespaceException(string code) : IOException(code)
{ internal string Code { get; } = code; }

// Bounded namespace traversal, not a source factory. A complete cursor does not
// prove a snapshot exists: the real owner must bind and retain the immutable
// source. Cached directory EOF/allocation fields are deliberately never read.
internal static class NtfsSnapshotNamespace
{
    internal const int PageBytes = 65536, HeaderBytes = 88, MaximumEntries = 262144;
    internal const int MaximumNameCharacters = 32 * 1024 * 1024, MaximumQueries = MaximumEntries * 4 + 1;
    internal const int NoMoreFiles = 18, ContinueClass = 19, RestartClass = 20;
    private static readonly UnicodeEncoding Unicode = new(false, false, true);
    internal interface IBackend
    {
        FileIdentity RootIdentity { get; }
        ICursor OpenDirectory(NtfsSnapshotNamespaceEntry directory, string relativePath, CancellationToken token);
    }
    internal interface ICursor : IDisposable
    {
        FileIdentity Identity { get; }
        FileAttributes Attributes { get; }
        NtfsSnapshotNamespaceIo Read(bool restart, Memory<byte> destination, CancellationToken token);
    }
    private static void Require([DoesNotReturnIf(false)] bool condition, string code)
    { if (!condition) throw new NtfsSnapshotNamespaceException(code); }
    private static T External<T>(Func<T> access, CancellationToken token)
    {
        T value;
        try { value = access(); }
        catch (Exception) { token.ThrowIfCancellationRequested(); throw new NtfsSnapshotNamespaceException("NamespaceInput"); }
        token.ThrowIfCancellationRequested(); return value;
    }
    private static bool IdentityValid(FileIdentity identity) => identity.High == 0 && identity.Low >> 48 != 0 &&
        (identity.Low & NtfsRecordDecoder.MaximumRecordNumber) < MaximumEntries;
    private static bool NameValid(string name) => name.Length is >= 1 and <= 255 &&
        !name.Any(character => char.IsControl(character) || character is '/' or '\\' or ':');

    internal static IReadOnlyList<NtfsSnapshotNamespacePageEntry> ParsePage(ReadOnlySpan<byte> page, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Require(page.Length >= HeaderBytes + 2 && page.Length <= PageBytes, "NamespacePageBounds");
        // Parse a private bounded source. Cached sizes and caller buffer state
        // are never retained in the resulting namespace inventory.
        byte[] owned = page.ToArray(); page = owned;
        token.ThrowIfCancellationRequested();
        var result = new List<NtfsSnapshotNamespacePageEntry>();
        int offset = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Require(offset >= 0 && offset <= page.Length - HeaderBytes, "NamespacePageBounds");
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(page[offset..]);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(page[(offset + 60)..]);
            Require(length is >= 2 and <= 510 && length % 2 == 0 && length <= page.Length - offset - HeaderBytes,
                "NamespaceNameBounds");
            int bytes = checked(HeaderBytes + (int)length);
            if (next != 0) Require(next >= bytes && next % 8 == 0 && next <= page.Length - offset - HeaderBytes - 2,
                "NamespaceNextOffset");
            string name;
            try { name = Unicode.GetString(page.Slice(offset + HeaderBytes, (int)length)); }
            catch (DecoderFallbackException) { throw new NtfsSnapshotNamespaceException("NamespaceNameEncoding"); }
            Require(NameValid(name), "NamespaceName");
            var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(page[(offset + 56)..]);
            ulong low = BinaryPrimitives.ReadUInt64LittleEndian(page[(offset + 72)..]);
            ulong high = BinaryPrimitives.ReadUInt64LittleEndian(page[(offset + 80)..]);
            Require(IdentityValid(new(0, low, high)), "NamespaceIdentity");
            result.Add(new(name, low, high, attributes));
            if (next == 0) break;
            offset = checked(offset + (int)next);
        }
        token.ThrowIfCancellationRequested(); return result.AsReadOnly();
    }

    internal static NtfsSnapshotNamespaceResult Read(IBackend backend, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); Require(backend != null, "NamespaceInput");
        FileIdentity rootIdentity = External(() => backend.RootIdentity, token);
        Require(IdentityValid(rootIdentity), "NamespaceIdentity");
        var root = new NtfsSnapshotNamespaceEntry(0, -1, "", rootIdentity, FileAttributes.Directory, false);
        var entries = new List<NtfsSnapshotNamespaceEntry> { root };
        var paths = new List<string> { "" };
        var queue = new Queue<int>(); queue.Enqueue(0);
        var seenDirectories = new HashSet<FileIdentity> { rootIdentity };
        var objectKinds = new Dictionary<FileIdentity, (bool Directory, bool Reparse)> { [rootIdentity] = (true, false) };
        var seenSlots = new Dictionary<ulong, FileIdentity> { [rootIdentity.Low & NtfsRecordDecoder.MaximumRecordNumber] = rootIdentity };
        var files = new HashSet<FileIdentity>();
        var page = new byte[PageBytes];
        int fileEntries = 0, directories = 0, reparse = 0, opened = 0, pages = 0, queries = 0, completed = 0;
        long characters = 0, pathCharacters = 0;
        while (queue.TryDequeue(out int directoryIndex))
        {
            token.ThrowIfCancellationRequested();
            var directory = entries[directoryIndex];
            var cursor = External(() => backend.OpenDirectory(directory, paths[directoryIndex], token), token);
            Require(cursor != null, "NamespaceCursor");
            try
            {
                Require(External(() => cursor.Identity, token) == directory.Identity, "NamespaceDirectoryIdentity");
                var attributes = External(() => cursor.Attributes, token);
                Require((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0, "NamespaceDirectoryType");
                opened++;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var dots = new HashSet<string>(StringComparer.Ordinal);
                bool restart = true;
                while (true)
                {
                    token.ThrowIfCancellationRequested(); Require(queries < MaximumQueries, "NamespaceQueryBudget");
                    page.AsSpan().Clear();
                    var io = External(() => cursor.Read(restart, page, token), token); queries++;
                    if (!io.Success)
                    {
                        Require(io.Error == NoMoreFiles, "NamespaceReadFailed");
                        completed++; break; // A validated directory handle also proves an empty first cursor.
                    }
                    Require(io.Error == 0, "NamespaceReadFailed");
                    var rows = ParsePage(page, token); pages++; restart = false;
                    foreach (var row in rows)
                    {
                        token.ThrowIfCancellationRequested();
                        var identity = new FileIdentity(rootIdentity.Volume, row.Low, row.High);
                        bool isDirectory = (row.Attributes & FileAttributes.Directory) != 0;
                        bool isReparse = (row.Attributes & FileAttributes.ReparsePoint) != 0;
                        if (row.Name is "." or "..")
                        {
                            Require(isDirectory && !isReparse && dots.Add(row.Name), "NamespaceDotEntry");
                            if (row.Name == ".") Require(identity == directory.Identity, "NamespaceDotEntry");
                            else if (directory.ParentIndex >= 0) Require(identity == entries[directory.ParentIndex].Identity, "NamespaceDotEntry");
                            continue;
                        }
                        Require(names.Add(row.Name), "NamespaceDuplicateName");
                        if (objectKinds.TryGetValue(identity, out var kind)) Require(kind == (isDirectory, isReparse), "NamespaceObjectType");
                        else objectKinds.Add(identity, (isDirectory, isReparse));
                        ulong slot = identity.Low & NtfsRecordDecoder.MaximumRecordNumber;
                        if (seenSlots.TryGetValue(slot, out var previous)) Require(previous == identity, "NamespaceSlotReused");
                        else seenSlots.Add(slot, identity);
                        Require(entries.Count < MaximumEntries, "NamespaceEntryBudget");
                        Require(characters <= MaximumNameCharacters - row.Name.Length, "NamespaceNameBudget");
                        characters += row.Name.Length;
                        string prefix = paths[directoryIndex];
                        int added = prefix.Length + (prefix.Length == 0 ? 0 : 1) + row.Name.Length;
                        Require(pathCharacters <= MaximumNameCharacters - added, "NamespacePathBudget");
                        string path = prefix.Length == 0 ? row.Name : prefix + "/" + row.Name;
                        pathCharacters += path.Length;
                        int index = entries.Count;
                        entries.Add(new(index, directoryIndex, row.Name, identity, row.Attributes, isReparse)); paths.Add(path);
                        if (isReparse) reparse++;
                        if (isDirectory)
                        {
                            Require(seenDirectories.Add(identity), "NamespaceDirectoryCycle"); directories++;
                            if (!isReparse) queue.Enqueue(index);
                        }
                        else { fileEntries++; files.Add(identity); }
                    }
                }
            }
            finally { External(() => { cursor.Dispose(); return true; }, token); }
        }
        Require(opened == completed && entries.Count == 1 + fileEntries + directories, "NamespaceCoverage");
        token.ThrowIfCancellationRequested();
        var report = new NtfsSnapshotNamespaceReport("snapshot-namespace-v1", entries.Count, fileEntries, directories, files.Count,
            reparse, opened, pages, queries, checked((int)characters), completed, true);
        return new(report, entries.AsReadOnly(), paths.AsReadOnly(), rootIdentity);
    }

    // This is only a read-only handle adapter. Its use is restricted by the
    // caller's source owner; opening a directory never mints snapshot evidence.
    internal static ICursor OpenNativeDirectory(string path, FileIdentity expected, CancellationToken token = default) =>
        new NativeCursor(path, expected, token);
    private sealed class NativeCursor : ICursor
    {
        [StructLayout(LayoutKind.Sequential)] private struct IdentityInfo { internal ulong Volume, Low, High; }
        [StructLayout(LayoutKind.Sequential)] private struct AttributeInfo { internal uint Attributes, ReparseTag; }
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool IdentityCall(SafeFileHandle handle, int info, out IdentityInfo value, uint size);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool AttributeCall(SafeFileHandle handle, int info, out AttributeInfo value, uint size);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool DirectoryCall(SafeFileHandle handle, int info, [Out] byte[] value, uint size);
        private readonly SafeFileHandle handle;
        internal NativeCursor(string path, FileIdentity expected, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            handle = NativeStorage.CreateFileW(path, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            try
            {
                Require(!handle.IsInvalid, "NamespaceDirectoryOpen");
                Require(IdentityCall(handle, 18, out var identity, 24), "NamespaceDirectoryIdentity");
                Identity = new(identity.Volume, identity.Low, identity.High);
                Require(Identity == expected && IdentityValid(Identity), "NamespaceDirectoryIdentity");
                Require(AttributeCall(handle, 9, out var attributes, 8), "NamespaceDirectoryType");
                Attributes = (FileAttributes)attributes.Attributes;
                Require((Attributes & FileAttributes.Directory) != 0 && (Attributes & FileAttributes.ReparsePoint) == 0, "NamespaceDirectoryType");
                token.ThrowIfCancellationRequested();
            }
            catch { handle.Dispose(); throw; }
        }
        public FileIdentity Identity { get; }
        public FileAttributes Attributes { get; }
        public NtfsSnapshotNamespaceIo Read(bool restart, Memory<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Require(!handle.IsClosed && !handle.IsInvalid && destination.Length == PageBytes, "NamespaceBuffer");
            bool arrayBacked = MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination, out ArraySegment<byte> segment);
            Require(arrayBacked && segment.Array != null && segment.Offset == 0 && segment.Count == PageBytes, "NamespaceBuffer");
            bool success = DirectoryCall(handle, restart ? RestartClass : ContinueClass, segment.Array!, PageBytes);
            int error = success ? 0 : Marshal.GetLastWin32Error();
            token.ThrowIfCancellationRequested(); return new(success, error);
        }
        public void Dispose() => handle.Dispose();
    }
}
