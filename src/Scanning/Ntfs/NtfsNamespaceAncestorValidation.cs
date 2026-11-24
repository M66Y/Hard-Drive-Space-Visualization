using System.Diagnostics.CodeAnalysis;

namespace DiskAtlas.NtfsPrototype;

// A source-local validation policy, not a snapshot factory. Only the private
// VSS owner supplies the open/active callbacks. Synthetic callbacks prove the
// policy, never the existence or immutability of a real source.
internal sealed class NtfsNamespaceAncestorValidation : IDisposable
{
    internal const int MaximumStates = 8192;
    internal delegate NtfsSnapshotNamespace.ICursor Open(NtfsSnapshotNamespaceEntry entry,
        string relativePath, bool ancestorsValidated, CancellationToken token);
    private sealed record Proof(NtfsSnapshotNamespaceEntry Entry, string Path, Proof? Parent);
    private readonly object gate = new(), generation;
    private readonly FileIdentity rootIdentity;
    private readonly Action active;
    private readonly Open open;
    private readonly int capacity;
    private readonly CancellationToken lifetime;
    private readonly CancellationTokenRegistration cancellation;
    private readonly Dictionary<int, Proof> states = new();
    private bool retired;
    private int opening;

    internal NtfsNamespaceAncestorValidation(object generation, FileIdentity rootIdentity, Action active,
        Open open, CancellationToken lifetime = default, int capacity = MaximumStates)
    {
        Require(generation != null && active != null && open != null && capacity is >= 0 and <= MaximumStates);
        this.generation = generation; this.rootIdentity = rootIdentity; this.active = active; this.open = open;
        this.capacity = capacity; this.lifetime = lifetime;
        cancellation = lifetime.Register(Invalidate);
    }

    internal int Count { get { lock (gate) return states.Count; } }
    internal bool Retired { get { lock (gate) return retired; } }

    // Kept separate from the full resolver used by size supplementation. This
    // helper is called only after the private backend has a bound parent proof;
    // the injectable attribute accessor also permits pure path-policy tests.
    internal static string ResolveLeaf(string root, string relative, Func<string, FileAttributes> attributes,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string[] parts = NtfsPersonalSnapshotSource.RelativeSegments(relative, true);
        Require(parts.Length > 0);
        string path = root.TrimEnd('\\') + "\\" + string.Join('\\', parts);
        Require((attributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory);
        token.ThrowIfCancellationRequested(); return path;
    }

    internal NtfsSnapshotNamespace.ICursor OpenDirectory(object sourceGeneration, NtfsSnapshotNamespaceEntry entry,
        string path, CancellationToken token)
    {
        // Enumeration is serial. Reject re-entry rather than publish a proof
        // whose parent or source changed while another open was in flight.
        if (Interlocked.CompareExchange(ref opening, 1, 0) != 0) { Invalidate(); throw Failure(); }
        NtfsSnapshotNamespace.ICursor? cursor = null;
        try
        {
            CheckActive(token);
            string[] parts = NtfsPersonalSnapshotSource.RelativeSegments(path, true);
            Require(entry.Directory && !entry.Reparse && (entry.Attributes & FileAttributes.ReparsePoint) == 0 &&
                entry.Identity.Volume == rootIdentity.Volume && entry.Identity.High == 0 && entry.Identity.Low >> 48 != 0);
            bool root = parts.Length == 0;
            Require(root ? entry.Index == 0 && entry.ParentIndex == -1 && entry.Name == "" && entry.Identity == rootIdentity :
                entry.Index > 0 && entry.ParentIndex >= 0 && entry.ParentIndex < entry.Index && entry.Name == parts[^1]);
            Proof? parent = null;
            bool bound;
            bool reuse;
            lock (gate)
            {
                Require(!retired && ReferenceEquals(generation, sourceGeneration));
                if (states.TryGetValue(entry.Index, out var existing))
                    Require(existing.Entry == entry && existing.Path == path);
                bound = root;
                if (!root && states.TryGetValue(entry.ParentIndex, out parent))
                {
                    int slash = path.LastIndexOf('/');
                    // Exact ordinal spelling is intentional. An alias or a
                    // forged parent index cannot borrow a different proof.
                    Require(slash < 0 ? parent.Path.Length == 0 :
                        parent.Path.Length == slash && path.AsSpan(0, slash).SequenceEqual(parent.Path));
                    bound = true;
                }
                reuse = !root && bound && states.Count < capacity;
            }

            // Both paths must still open the real directory with its original
            // permissions and verify the resulting handle's ID and tag.
            cursor = open(entry, path, reuse, token);
            CheckActive(token);
            Require(cursor != null && cursor.Identity == entry.Identity &&
                (cursor.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory);
            lock (gate)
            {
                Require(!retired && ReferenceEquals(generation, sourceGeneration));
                // Never replace a proof still referenced by a child. One
                // immutable node per index also bounds transitive retention.
                if (bound && states.Count < capacity && !states.ContainsKey(entry.Index))
                    states.Add(entry.Index, new(entry, path, parent));
            }
            var lease = new Cursor(this, cursor, token);
            cursor = null; return lease;
        }
        catch
        {
            Invalidate();
            try { cursor?.Dispose(); } catch { }
            token.ThrowIfCancellationRequested(); lifetime.ThrowIfCancellationRequested(); throw Failure();
        }
        finally { Volatile.Write(ref opening, 0); }
    }

    private void CheckActive(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); lifetime.ThrowIfCancellationRequested();
        lock (gate) Require(!retired);
        active();
    }
    private sealed class Cursor(NtfsNamespaceAncestorValidation owner, NtfsSnapshotNamespace.ICursor inner,
        CancellationToken openedWith) : NtfsSnapshotNamespace.ICursor
    {
        private int disposed;
        public FileIdentity Identity => inner.Identity;
        public FileAttributes Attributes => inner.Attributes;
        public NtfsSnapshotNamespaceIo Read(bool restart, Memory<byte> destination, CancellationToken token)
        {
            try
            {
                Require(Volatile.Read(ref disposed) == 0);
                openedWith.ThrowIfCancellationRequested(); owner.CheckActive(token);
                var result = inner.Read(restart, destination, token);
                openedWith.ThrowIfCancellationRequested(); owner.CheckActive(token);
                if (!result.Success && result.Error != NtfsSnapshotNamespace.NoMoreFiles) owner.Invalidate();
                return result;
            }
            catch
            {
                owner.Invalidate(); token.ThrowIfCancellationRequested(); openedWith.ThrowIfCancellationRequested();
                owner.lifetime.ThrowIfCancellationRequested(); throw Failure();
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { inner.Dispose(); }
            catch { owner.Invalidate(); throw Failure(); }
        }
    }
    internal void Invalidate() { lock (gate) { retired = true; states.Clear(); } }
    public void Dispose() { Invalidate(); cancellation.Dispose(); }
    private static NtfsVssProbeException Failure() => new("VssSnapshotNative");
    private static void Require([DoesNotReturnIf(false)] bool value) { if (!value) throw Failure(); }
}
