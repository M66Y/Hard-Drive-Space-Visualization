using System.Diagnostics;
using DiskAtlas.NtfsPrototype;

namespace DiskAtlas;

internal enum NamespaceAncestorOutcome : byte { Matched, Different, Failed, Cancelled }
internal enum NamespaceAncestorCode : byte { None, SnapshotUnavailable, EnumerationFailed, SourceChanged, ComparisonInvalid, Cancelled, CleanupUnconfirmed, InternalFailure }
internal enum NamespaceAncestorStage : byte { Snapshot, Original, Candidate, Compare, Cleanup }
[Flags]
internal enum NamespaceAncestorChecks : byte { None = 0, Paths = 1, Parents = 2, Identities = 4, Attributes = 8, Times = 16, Counts = 32, Coverage = 64, All = 127 }
internal sealed record NamespaceAncestorMeasurement(long Milliseconds, int Entries, int Opened, int Completed, int Pages, int Queries, bool HandlesSettled);
internal sealed record NamespaceAncestorReport
{
    internal NamespaceAncestorOutcome Outcome { get; init; } = NamespaceAncestorOutcome.Failed;
    internal NamespaceAncestorCode Code { get; init; } = NamespaceAncestorCode.InternalFailure;
    internal bool CleanupConfirmed { get; init; }
    internal bool SameSourceVerified { get; init; }
    internal bool ComparisonComplete { get; init; }
    internal NamespaceAncestorChecks Checks { get; init; }
    internal NamespaceAncestorMeasurement? Original { get; init; }
    internal NamespaceAncestorMeasurement? Candidate { get; init; }
    internal long SnapshotMilliseconds { get; init; }
    internal long ComparisonMilliseconds { get; init; }
    internal long CleanupMilliseconds { get; init; }
}

// A test-only directory-stage comparison. It does not call OpenReader,
// LookupSize, ReadNative, the complete fast scan, or the ordinary scanner.
internal static class NtfsNamespaceAncestorComparison
{
    private sealed class SourceBindingFailure : IOException { }
    internal interface ISource : IDisposable
    {
        FileIdentity RootIdentity { get; }
        NtfsSnapshotNamespace.IBackendLease Open(bool reuse, CancellationToken token);
        bool Cleanup();
    }
    private sealed class RealSource(NtfsPersonalSnapshotSource source) : ISource
    {
        public FileIdentity RootIdentity => source.RootStorage.Identity;
        public NtfsSnapshotNamespace.IBackendLease Open(bool reuse, CancellationToken token) => source.CreateNamespaceBackend(reuse, token);
        public bool Cleanup() => source.Cleanup();
        public void Dispose() => source.Dispose();
    }
    internal static NamespaceAncestorReport Execute(string scope, CancellationToken token, Action<NamespaceAncestorStage> stage) =>
        Execute(() => CreateSource(scope, token), token, stage);
    private static ISource CreateSource(string scope, CancellationToken token)
    {
        var owned = NtfsPersonalSnapshotSource.Create(scope, token, NtfsScanLimits.Large);
        try { return new RealSource(owned); }
        catch
        {
            bool cleaned = false;
            try { cleaned = owned.Cleanup(); } catch { }
            try { owned.Dispose(); } catch { cleaned = false; }
            throw new NtfsPersonalSnapshotOpenException("VssSnapshotNative", 0, cleaned);
        }
    }

    internal static NamespaceAncestorReport Execute(Func<ISource> create, CancellationToken token, Action<NamespaceAncestorStage> stage)
    {
        ISource? source = null; bool cleaned = true;
        var report = new NamespaceAncestorReport(); var phase = NamespaceAncestorStage.Snapshot;
        var watch = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested(); stage(phase);
            cleaned = false; source = create();
            report = report with { SnapshotMilliseconds = watch.ElapsedMilliseconds };
            var root = source.RootIdentity;
            phase = NamespaceAncestorStage.Original; stage(phase);
            var original = Read(source, false, token);
            report = report with { Original = original.Measurement };
            if (source.RootIdentity != root || original.Inventory.RootIdentity != root) throw new SourceBindingFailure();
            phase = NamespaceAncestorStage.Candidate; stage(phase);
            var candidate = Read(source, true, token);
            report = report with { Candidate = candidate.Measurement };
            if (source.RootIdentity != root || candidate.Inventory.RootIdentity != root) throw new SourceBindingFailure();
            phase = NamespaceAncestorStage.Compare; stage(phase); watch.Restart();
            var checks = Compare(original.Inventory, candidate.Inventory, token);
            token.ThrowIfCancellationRequested();
            report = report with { Outcome = checks == NamespaceAncestorChecks.All ? NamespaceAncestorOutcome.Matched : NamespaceAncestorOutcome.Different,
                Code = NamespaceAncestorCode.None, Checks = checks, SameSourceVerified = true, ComparisonComplete = true,
                ComparisonMilliseconds = watch.ElapsedMilliseconds };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { report = report with { Outcome = NamespaceAncestorOutcome.Cancelled, Code = NamespaceAncestorCode.Cancelled }; }
        catch (NtfsPersonalSnapshotOpenException error)
        { cleaned = error.CleanupConfirmed; report = report with { Code = NamespaceAncestorCode.SnapshotUnavailable }; }
        catch (SourceBindingFailure)
        { report = report with { Code = NamespaceAncestorCode.SourceChanged }; }
        catch
        {
            report = report with { Outcome = token.IsCancellationRequested ? NamespaceAncestorOutcome.Cancelled : NamespaceAncestorOutcome.Failed,
                Code = token.IsCancellationRequested ? NamespaceAncestorCode.Cancelled : phase switch
                { NamespaceAncestorStage.Snapshot => NamespaceAncestorCode.SnapshotUnavailable, NamespaceAncestorStage.Compare => NamespaceAncestorCode.ComparisonInvalid,
                    _ => NamespaceAncestorCode.EnumerationFailed } };
        }
        finally
        {
            try { stage(NamespaceAncestorStage.Cleanup); } catch { }
            watch.Restart();
            if (source != null)
            {
                try { cleaned = source.Cleanup(); } catch { cleaned = false; }
                try { source.Dispose(); } catch { cleaned = false; }
            }
            report = report with { CleanupMilliseconds = watch.ElapsedMilliseconds, CleanupConfirmed = cleaned };
        }
        if (!cleaned) report = report with { Outcome = NamespaceAncestorOutcome.Failed, Code = NamespaceAncestorCode.CleanupUnconfirmed,
            SameSourceVerified = false, ComparisonComplete = false, Checks = NamespaceAncestorChecks.None };
        else if (token.IsCancellationRequested) report = report with { Outcome = NamespaceAncestorOutcome.Cancelled, Code = NamespaceAncestorCode.Cancelled,
            SameSourceVerified = false, ComparisonComplete = false, Checks = NamespaceAncestorChecks.None };
        return report;
    }

    private static (NtfsSnapshotNamespaceResult Inventory, NamespaceAncestorMeasurement Measurement) Read(ISource source, bool reuse, CancellationToken token)
    {
        var watch = Stopwatch.StartNew(); NtfsSnapshotNamespaceResult inventory;
        var tracked = new TrackedBackend(source.Open(reuse, token));
        using (tracked) inventory = NtfsSnapshotNamespace.Read(tracked, token, NtfsScanLimits.Large);
        watch.Stop(); token.ThrowIfCancellationRequested();
        if (!tracked.Settled || inventory.Report.OpenedDirectories != tracked.Opened) throw new InvalidDataException();
        var r = inventory.Report;
        return (inventory, new(watch.ElapsedMilliseconds, r.Entries, r.OpenedDirectories, r.CompletedDirectories, r.Pages, r.Queries, tracked.Settled));
    }

    private readonly record struct Key(string Parent, string Name);
    private sealed record Value(NtfsSnapshotNamespaceEntry Entry, FileIdentity ParentIdentity)
    { internal bool Seen; }
    internal static NamespaceAncestorChecks Compare(NtfsSnapshotNamespaceResult original, NtfsSnapshotNamespaceResult candidate, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ValidateInventory(original, token); ValidateInventory(candidate, token);
        var flags = NamespaceAncestorChecks.All;
        var left = new Dictionary<Key, Value>();
        Key Locate(NtfsSnapshotNamespaceResult inventory, int index, out FileIdentity parent)
        {
            token.ThrowIfCancellationRequested(); var e = inventory.Entries[index];
            if (e.Index != index || index == 0 && (e.ParentIndex != -1 || e.Name != "" || !e.Directory || e.Reparse) ||
                index != 0 && (e.ParentIndex < 0 || e.ParentIndex >= index || !inventory.Entries[e.ParentIndex].Directory || inventory.Entries[e.ParentIndex].Reparse))
                throw new InvalidDataException();
            parent = index == 0 ? default : inventory.Entries[e.ParentIndex].Identity;
            return new(index == 0 ? "" : inventory.RelativePath(e.ParentIndex), e.Name);
        }
        for (int i = 0; i < original.Entries.Count; i++)
        {
            var key = Locate(original, i, out var parent);
            if (!left.TryAdd(key, new(original.Entries[i], parent))) throw new InvalidDataException();
        }
        int aligned = 0; HashSet<Key>? unmatched = null;
        for (int i = 0; i < candidate.Entries.Count; i++)
        {
            var key = Locate(candidate, i, out var parent);
            if (!left.TryGetValue(key, out var value))
            {
                unmatched ??= [];
                if (!unmatched.Add(key)) throw new InvalidDataException();
                flags &= ~NamespaceAncestorChecks.Paths; continue;
            }
            if (value.Seen) throw new InvalidDataException();
            value.Seen = true; aligned++;
            var a = value.Entry; var b = candidate.Entries[i];
            if (value.ParentIdentity != parent) flags &= ~NamespaceAncestorChecks.Parents;
            if (a.Identity != b.Identity) flags &= ~NamespaceAncestorChecks.Identities;
            if (a.Directory != b.Directory || a.Reparse != b.Reparse || a.Attributes != b.Attributes) flags &= ~NamespaceAncestorChecks.Attributes;
            if (a.Modified != b.Modified) flags &= ~NamespaceAncestorChecks.Times;
        }
        if (aligned != left.Count) flags &= ~NamespaceAncestorChecks.Paths;
        if ((flags & NamespaceAncestorChecks.Paths) == 0)
            flags &= ~(NamespaceAncestorChecks.Parents | NamespaceAncestorChecks.Identities | NamespaceAncestorChecks.Attributes | NamespaceAncestorChecks.Times);
        if (original.RootIdentity != candidate.RootIdentity) flags &= ~NamespaceAncestorChecks.Identities;
        var x = original.Report; var y = candidate.Report;
        if ((x.Entries, x.FileEntries, x.Directories, x.UniqueFileObjects, x.ReparseEntries, x.NameCharacters) !=
            (y.Entries, y.FileEntries, y.Directories, y.UniqueFileObjects, y.ReparseEntries, y.NameCharacters)) flags &= ~NamespaceAncestorChecks.Counts;
        if (!x.CompleteWithinBoundary || !y.CompleteWithinBoundary || x.OpenedDirectories != x.CompletedDirectories ||
            y.OpenedDirectories != y.CompletedDirectories || x.OpenedDirectories != y.OpenedDirectories) flags &= ~NamespaceAncestorChecks.Coverage;
        token.ThrowIfCancellationRequested(); return flags;
    }
    private static void ValidateInventory(NtfsSnapshotNamespaceResult inventory, CancellationToken token)
    {
        var r = inventory.Report; var limit = NtfsScanLimits.Large;
        if (inventory.Entries.Count is < 1 || inventory.Entries.Count > limit.MaximumEntries || r.Entries != inventory.Entries.Count ||
            r.FileEntries < 0 || r.FileEntries >= r.Entries || r.Directories < 0 || r.Directories >= r.Entries ||
            r.UniqueFileObjects < 0 || r.UniqueFileObjects > r.FileEntries || r.ReparseEntries < 0 || r.ReparseEntries >= r.Entries ||
            r.CompletedDirectories < 0 || r.CompletedDirectories > r.OpenedDirectories ||
            r.NameCharacters < 0 || r.NameCharacters > limit.MaximumCharacters || r.Queries < 1 || r.Queries > limit.MaximumEntries * 4 + 1 ||
            r.Pages < 0 || r.Pages > r.Queries || r.OpenedDirectories < 1 || r.OpenedDirectories > r.Entries ||
            inventory.Entries[0].Identity != inventory.RootIdentity || inventory.RelativePath(0) != "") throw new InvalidDataException();
        long names = 0, paths = 0;
        for (int i = 1; i < inventory.Entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = inventory.Entries[i];
            if (entry.Name == null || entry.Name.Length is < 1 or > 255 || entry.Name is "." or ".." ||
                entry.Name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':')) throw new InvalidDataException();
            names += entry.Name.Length;
            if (entry.Directory)
            {
                var path = inventory.RelativePath(i);
                if (path.Length > 32767 || NtfsPersonalSnapshotSource.RelativeSegments(path, true).Length > 512) throw new InvalidDataException();
                paths += path.Length;
            }
            if (names > limit.MaximumCharacters || paths > limit.MaximumCharacters) throw new InvalidDataException();
        }
    }

    private sealed class TrackedBackend(NtfsSnapshotNamespace.IBackendLease inner) : NtfsSnapshotNamespace.IBackend, IDisposable
    {
        private readonly HashSet<IDisposable> cursors = [];
        private int closed; private bool disposed;
        internal int Opened { get; private set; }
        internal bool Settled => disposed && cursors.Count == 0 && Opened == closed;
        public FileIdentity RootIdentity => inner.RootIdentity;
        public DateTime? RootModified => inner.RootModified;
        public NtfsSnapshotNamespace.ICursor OpenDirectory(NtfsSnapshotNamespaceEntry entry, string path, CancellationToken token)
        {
            if (disposed || cursors.Count >= 16) throw new InvalidDataException();
            var raw = inner.OpenDirectory(entry, path, token);
            var cursor = new Cursor(this, raw); cursors.Add(cursor); Opened++; return cursor;
        }
        private sealed class Cursor(TrackedBackend owner, NtfsSnapshotNamespace.ICursor raw) : NtfsSnapshotNamespace.ICursor
        {
            private bool disposed;
            public FileIdentity Identity => raw.Identity;
            public FileAttributes Attributes => raw.Attributes;
            public NtfsSnapshotNamespaceIo Read(bool restart, Memory<byte> buffer, CancellationToken token) => raw.Read(restart, buffer, token);
            public void Dispose()
            {
                if (disposed) return; disposed = true;
                try { raw.Dispose(); owner.closed++; } finally { owner.cursors.Remove(this); }
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            bool cleaned = NtfsPersonalSnapshotSource.CloseTrackedCursorsAndSession(cursors, () => { inner.Dispose(); return true; });
            if (!cleaned || !Settled) throw new InvalidDataException();
        }
    }
}
