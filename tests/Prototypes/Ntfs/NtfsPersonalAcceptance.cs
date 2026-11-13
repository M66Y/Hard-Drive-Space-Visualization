using System.Diagnostics.CodeAnalysis;
using DiskAtlas;

namespace DiskAtlas.NtfsPrototype;

internal enum NtfsPersonalAcceptanceCode
{
    Input, TreeIncomplete, TreeShape, TreePath, Budget, Identity, Metadata,
    NativeFailure, NativeMismatch, Totals, ArithmeticOverflow, SourceMutated
}

internal sealed class NtfsPersonalAcceptanceException(NtfsPersonalAcceptanceCode code) : IOException(code.ToString())
{ internal NtfsPersonalAcceptanceCode Code { get; } = code; }

// Test-only audit of a tree while its real owner still holds the snapshot.
// The callback and returned booleans do not establish source provenance. The
// production source factory/cursor completion must independently establish it.
internal static class NtfsPersonalAcceptance
{
    internal const int MaximumEntries = 262144, MaximumDepth = 512;
    private const ulong SlotMask = (1UL << 48) - 1;
    private static readonly long FirstFileTimeTicks = DateTime.FromFileTimeUtc(0).Ticks;

    private sealed record Node(DiskEntry Entry, int Parent, int Depth, string? Relative, string Name, bool Directory,
        FileStorageInfo Storage, long Size, long Allocation, long Files, long Folders, DateTime Modified, bool Shared,
        DiskEntry[] Children);
    private sealed class ObjectState(Node first)
    {
        internal readonly Node First = first;
        internal int Aliases = 1;
        internal Node AllocationOwner = first;
        internal FileStorageInfo? Native;
    }
    private static void Require([DoesNotReturnIf(false)] bool value, NtfsPersonalAcceptanceCode code)
    { if (!value) throw new NtfsPersonalAcceptanceException(code); }
    private static long Add(long left, long right)
    {
        Require(left >= 0 && right >= 0, NtfsPersonalAcceptanceCode.Metadata);
        try { return checked(left + right); }
        catch (OverflowException) { throw new NtfsPersonalAcceptanceException(NtfsPersonalAcceptanceCode.ArithmeticOverflow); }
    }
    private static long Time(DateTime value)
    {
        long ticks;
        try { ticks = value.ToUniversalTime().Ticks; }
        catch { throw new NtfsPersonalAcceptanceException(NtfsPersonalAcceptanceCode.Metadata); }
        Require(ticks >= FirstFileTimeTicks, NtfsPersonalAcceptanceCode.Metadata);
        return ticks;
    }
    private static bool Same(FileStorageInfo left, FileStorageInfo right) =>
        left.Identity == right.Identity && left.LogicalBytes == right.LogicalBytes && left.AllocatedBytes == right.AllocatedBytes &&
        left.LinkCount == right.LinkCount && left.WritableHandleOpen == right.WritableHandleOpen && left.Modified.HasValue &&
        right.Modified.HasValue && Time(left.Modified.Value) == Time(right.Modified.Value);
    private static void Metadata(FileStorageInfo value, bool directory)
    {
        Require(value != null && value.LinkCount > 0 && !value.WritableHandleOpen && value.Modified.HasValue &&
            value.AllocatedBytes >= 0 && (directory ? value.LogicalBytes == -1 && value.AllocatedBytes == 0 : value.LogicalBytes >= 0),
            NtfsPersonalAcceptanceCode.Metadata);
        _ = Time(value.Modified.Value);
    }
    private static bool NameValid(string name) => name.Length is >= 1 and <= 255 && name is not ("." or "..") &&
        name[^1] is not ('.' or ' ') && !name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '"' or '<' or '>' or '|' or '?' or '*');

    // Account for actual retained strings, including every stored directory's
    // absolute prefix. A long root repeated across directories is not free.
    // This pure helper also lets tests exercise the bound without huge strings.
    internal static long AddCompactPathCharacters(long retained, int nameCharacters, int relativeCharacters,
        int storedPathCharacters, NtfsScanLimits limits)
    {
        long total = Add(Add(Add(retained, nameCharacters), relativeCharacters), storedPathCharacters);
        Require(total <= limits.MaximumCharacters, NtfsPersonalAcceptanceCode.Budget);
        return total;
    }

    private static string RelativeForRead(Node node, IReadOnlyList<Node> nodes)
    {
        if (node.Relative != null) return node.Relative;
        string parent = nodes[node.Parent].Relative!;
        return parent.Length == 0 ? node.Name : parent + "/" + node.Name;
    }

    internal static (bool NamespaceComplete, bool AllObjectsMatched) Verify(DiskEntry tree,
        Func<string, bool, CancellationToken, FileStorageInfo> sameSnapshotRead, CancellationToken token = default,
        NtfsScanLimits? limits = null)
    {
        token.ThrowIfCancellationRequested();
        try { return VerifyCore(tree, sameSnapshotRead, token, limits ?? NtfsScanLimits.Standard); }
        catch (NtfsPersonalAcceptanceException) { throw; }
        catch { token.ThrowIfCancellationRequested(); throw new NtfsPersonalAcceptanceException(NtfsPersonalAcceptanceCode.Input); }
    }

    private static (bool NamespaceComplete, bool AllObjectsMatched) VerifyCore(DiskEntry tree,
        Func<string, bool, CancellationToken, FileStorageInfo> read, CancellationToken token, NtfsScanLimits limits)
    {
        Require(tree != null && read != null, NtfsPersonalAcceptanceCode.Input);
        Require(tree.IsDirectory && tree.Parent == null, NtfsPersonalAcceptanceCode.TreeShape);
        string rootPath;
        try { rootPath = FastScanWire.NormalizeRoot(tree.Path); }
        catch { throw new NtfsPersonalAcceptanceException(NtfsPersonalAcceptanceCode.TreePath); }
        Require(tree.Storage != null, NtfsPersonalAcceptanceCode.Metadata);
        ulong volume = tree.Storage.Identity.Volume;
        var nodes = new List<Node>();
        var seen = new HashSet<DiskEntry>(ReferenceEqualityComparer.Instance);
        var slots = new Dictionary<ulong, FileIdentity>();
        var objects = new Dictionary<FileIdentity, ObjectState>();
        var pending = new Queue<(DiskEntry Entry, int Parent, int Depth, string? Relative)>();
        pending.Enqueue((tree, -1, 0, ""));
        long pathCharacters = 0;
        while (pending.TryDequeue(out var next))
        {
            token.ThrowIfCancellationRequested();
            var entry = next.Entry;
            Require(entry != null && seen.Add(entry), NtfsPersonalAcceptanceCode.TreeShape);
            Require(nodes.Count < limits.MaximumEntries && next.Depth <= MaximumDepth, NtfsPersonalAcceptanceCode.Budget);
            Require(!entry.Incomplete && entry.UnknownAllocationFiles == 0, NtfsPersonalAcceptanceCode.TreeIncomplete);
            Require(next.Parent < 0 ? entry.Parent == null : ReferenceEquals(entry.Parent, nodes[next.Parent].Entry), NtfsPersonalAcceptanceCode.TreeShape);
            Require(next.Parent < 0 || NameValid(entry.Name), NtfsPersonalAcceptanceCode.TreePath);
            string? relative = next.Relative;
            if (limits.CompactPaths)
            {
                string parent = next.Parent < 0 ? "" : nodes[next.Parent].Relative!;
                long relativeLength = next.Parent < 0 ? 0 : Add(Add(parent.Length, parent.Length == 0 ? 0 : 1), entry.Name.Length);
                Require(relativeLength <= 32767, NtfsPersonalAcceptanceCode.Budget);
                relative = next.Parent < 0 ? "" : entry.IsDirectory ?
                    (parent.Length == 0 ? entry.Name : parent + "/" + entry.Name) : null;
                if (next.Parent < 0)
                    Require(string.Equals(Path.GetFullPath(entry.Path), rootPath, StringComparison.OrdinalIgnoreCase), NtfsPersonalAcceptanceCode.TreePath);
                else
                    Require(entry.HasExpectedParentPath(), NtfsPersonalAcceptanceCode.TreePath);
                pathCharacters = AddCompactPathCharacters(pathCharacters, entry.Name.Length, relative?.Length ?? 0,
                    entry.StoredPathCharacters, limits);
            }
            else
            {
                Require(relative != null && relative.Length <= 32767, NtfsPersonalAcceptanceCode.Budget);
                pathCharacters = Add(pathCharacters, relative.Length);
                Require(pathCharacters <= limits.MaximumCharacters, NtfsPersonalAcceptanceCode.Budget);
                string expected = relative.Length == 0 ? rootPath : Path.Combine(rootPath, relative.Replace('/', '\\'));
                Require(string.Equals(Path.GetFullPath(entry.Path), expected, StringComparison.OrdinalIgnoreCase), NtfsPersonalAcceptanceCode.TreePath);
            }
            var storage = entry.Storage;
            Require(storage != null, NtfsPersonalAcceptanceCode.Metadata); Metadata(storage, entry.IsDirectory);
            Require(storage.Identity.Volume == volume && storage.Identity.High == 0 && storage.Identity.Low >> 48 != 0,
                NtfsPersonalAcceptanceCode.Identity);
            ulong slot = storage.Identity.Low & SlotMask;
            Require(!slots.TryGetValue(slot, out var prior) || prior == storage.Identity, NtfsPersonalAcceptanceCode.Identity);
            slots[slot] = storage.Identity;
            Require(entry.Size >= 0 && entry.AllocatedSize >= 0 && entry.FileCount >= 0 && entry.FolderCount >= 0 &&
                Time(entry.Modified) == Time(storage.Modified!.Value), NtfsPersonalAcceptanceCode.Metadata);
            int childCount = entry.Children.Count;
            Require(childCount <= limits.MaximumEntries - nodes.Count - pending.Count - 1, NtfsPersonalAcceptanceCode.Budget);
            Require(entry.IsDirectory || childCount == 0 && entry.FileCount == 1 && entry.FolderCount == 0 && entry.Size == storage.LogicalBytes,
                NtfsPersonalAcceptanceCode.TreeShape);
            var children = childCount == 0 ? Array.Empty<DiskEntry>() : new DiskEntry[childCount];
            var names = childCount == 0 ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < childCount; c++)
            {
                token.ThrowIfCancellationRequested(); var child = entry.Children[c];
                Require(child != null && names!.Add(child.Name), NtfsPersonalAcceptanceCode.TreeShape);
                children[c] = child;
            }
            var node = new Node(entry, next.Parent, next.Depth, relative, entry.Name, entry.IsDirectory, storage,
                entry.Size, entry.AllocatedSize, entry.FileCount, entry.FolderCount, entry.Modified, entry.SharedAllocation, children);
            if (objects.TryGetValue(storage.Identity, out var state))
            {
                Require(!node.Directory && !state.First.Directory && Same(state.First.Storage, storage), NtfsPersonalAcceptanceCode.Identity);
                state.Aliases++; Require(state.Aliases <= storage.LinkCount, NtfsPersonalAcceptanceCode.Identity);
                if (DiskEntry.ComparePath(node.Entry, state.AllocationOwner.Entry) < 0) state.AllocationOwner = node;
            }
            else objects.Add(storage.Identity, new(node));
            int index = nodes.Count; nodes.Add(node);
            foreach (var child in children)
            {
                string? childRelative = limits.CompactPaths ? null : relative!.Length == 0 ? child.Name : relative + "/" + child.Name;
                pending.Enqueue((child, index, next.Depth + 1, childRelative));
            }
        }

        foreach (var state in objects.Values)
        {
            token.ThrowIfCancellationRequested(); FileStorageInfo observed;
            try { observed = read(RelativeForRead(state.First, nodes), state.First.Directory, token); }
            catch { token.ThrowIfCancellationRequested(); throw new NtfsPersonalAcceptanceException(NtfsPersonalAcceptanceCode.NativeFailure); }
            token.ThrowIfCancellationRequested();
            Require(observed != null, NtfsPersonalAcceptanceCode.NativeMismatch);
            // The owner's reader checks the actual directory bit before returning.
            // A caller-supplied value itself never establishes that provenance.
            Metadata(observed, state.First.Directory);
            Require(Same(state.First.Storage, observed) && (state.First.Directory || state.Aliases <= observed.LinkCount),
                NtfsPersonalAcceptanceCode.NativeMismatch);
            state.Native = observed;
        }

        var logical = new long[nodes.Count]; var allocated = new long[nodes.Count];
        var files = new long[nodes.Count]; var folders = new long[nodes.Count];
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            token.ThrowIfCancellationRequested(); var node = nodes[i]; var state = objects[node.Storage.Identity];
            if (!node.Directory)
            {
                logical[i] = state.Native!.LogicalBytes; files[i] = 1;
                bool owner = ReferenceEquals(node, state.AllocationOwner);
                allocated[i] = owner ? state.Native.AllocatedBytes : 0;
                Require(node.Shared == !owner, NtfsPersonalAcceptanceCode.Totals);
            }
            Require(node.Size == logical[i] && node.Allocation == allocated[i] && node.Files == files[i] && node.Folders == folders[i],
                NtfsPersonalAcceptanceCode.Totals);
            if (node.Parent >= 0)
            {
                logical[node.Parent] = Add(logical[node.Parent], logical[i]);
                allocated[node.Parent] = Add(allocated[node.Parent], allocated[i]);
                files[node.Parent] = Add(files[node.Parent], files[i]);
                folders[node.Parent] = Add(folders[node.Parent], Add(folders[i], node.Directory ? 1 : 0));
            }
        }
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested(); var entry = node.Entry;
            Require(entry.Name == node.Name && entry.IsDirectory == node.Directory && entry.Size == node.Size &&
                entry.AllocatedSize == node.Allocation && entry.FileCount == node.Files && entry.FolderCount == node.Folders &&
                Time(entry.Modified) == Time(node.Modified) && entry.SharedAllocation == node.Shared && !entry.Incomplete &&
                entry.UnknownAllocationFiles == 0 && entry.Storage != null && Same(entry.Storage, node.Storage) &&
                entry.Children.Count == node.Children.Length && entry.Children.SequenceEqual(node.Children, ReferenceEqualityComparer.Instance),
                NtfsPersonalAcceptanceCode.SourceMutated);
        }
        token.ThrowIfCancellationRequested(); return (true, true);
    }

    internal static int RunTests() => NtfsPersonalAcceptanceTests.Run();
}
