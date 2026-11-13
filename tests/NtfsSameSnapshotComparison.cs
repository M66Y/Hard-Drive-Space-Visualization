using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DiskAtlas.NtfsPrototype;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas;

internal enum NtfsSameSnapshotOutcome { Matched, Different, Incomplete, Cancelled, Failed }
internal enum NtfsSameSnapshotCode
{
    None, InvalidScope, BudgetExceeded, SnapshotUnavailable, TraversalFailed,
    ComparisonIncomplete, NativeProbeIncomplete, SourceChanged, Cancelled, InternalFailure, CleanupUnconfirmed
}
internal enum NtfsSameSnapshotProbeBranch { None, Directory, Normal, Compressed, Sparse, CompressedAndSparse, Mixed }

// Every public field is a closed category or nullable equality observation.
// Private paths, identities, hashes and measurements never enter this type.
internal sealed record NtfsSameSnapshotReport
{
    public NtfsSameSnapshotOutcome Outcome { get; init; } = NtfsSameSnapshotOutcome.Failed;
    public NtfsSameSnapshotCode Code { get; init; } = NtfsSameSnapshotCode.InternalFailure;
    public bool CleanupConfirmed { get; init; }
    public bool SameSourceVerified { get; init; }
    public bool BudgetRespected { get; init; }
    public bool ComparisonComplete { get; init; }
    public bool? IncludedScopeEqual { get; init; }
    public bool? TypesEqual { get; init; }
    public bool? IdentitiesEqual { get; init; }
    public bool? FileLogicalEqual { get; init; }
    public bool? FileAllocationEqual { get; init; }
    public bool? FileLinksEqual { get; init; }
    public bool? AllocationKnown { get; init; }
    public bool? FastRootLogicalValid { get; init; }
    public bool? FastRootAllocationValid { get; init; }
    public bool? DirectoryRootLogicalValid { get; init; }
    public bool? DirectoryRootAllocationValid { get; init; }
    public bool? FastRootCountsValid { get; init; }
    public bool? DirectoryRootCountsValid { get; init; }
    public bool? ExclusionsEqual { get; init; }
    public bool? ExclusionDetailsComplete { get; init; }
    public bool NativeProbeRequired { get; init; }
    public bool NativeProbeAttempted { get; init; }
    public bool NativeProbeComplete { get; init; }
    public NtfsSameSnapshotProbeBranch ProbeBranch { get; init; }
    public bool? FastMatchesNativeIdentity { get; init; }
    public bool? DirectoryMatchesNativeIdentity { get; init; }
    public bool? FastMatchesNativeLogical { get; init; }
    public bool? DirectoryMatchesNativeLogical { get; init; }
    public bool? FastMatchesNativeAllocation { get; init; }
    public bool? DirectoryMatchesNativeAllocation { get; init; }
    public bool? FastMatchesNativeLinks { get; init; }
    public bool? DirectoryMatchesNativeLinks { get; init; }
    public bool? FastMatchesNativeStandardAllocation { get; init; }
    public bool? DirectoryMatchesNativeStandardAllocation { get; init; }
    public bool? NativeMetadataConsistent { get; init; }
    public bool? NativeCompressionObserved { get; init; }
}

internal static class NtfsSameSnapshotComparison
{
    internal static int MaximumNativeObjects => 1024;

    internal static NtfsSameSnapshotReport Execute(string scope, CancellationToken token, Action<FastScanWireStage> stage)
    {
        bool cleaned = false, budgetFailed = false, callbackCompleted = false, sourceMismatch = false, sourceVerified = false;
        NtfsSameSnapshotReport? pending = null; ExclusionSet? directoryExclusions = null;
        NtfsSameSnapshotCode? callbackFailure = null;
        Dictionary<FileIdentity, Key>? fastDirectories = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            try { scope = FastScanWire.NormalizeRoot(scope); }
            catch { return Failure(NtfsSameSnapshotOutcome.Failed, NtfsSameSnapshotCode.InvalidScope, true); }
            var result = NtfsPersonalScan.Run(scope, stage, timeout.Token, (source, tree, cancellation) =>
            {
                try
                {
                var fast = Capture(tree, cancellation); fastDirectories = fast.Directories;
                var traversal = new ScanTraversalBudget(maxEntries: 1048576, maxNameCharacters: 64L * 1024 * 1024,
                    maxPathCharacters: 64L * 1024 * 1024, maxQueuedDirectories: 1048576);
                ScanResult directory;
                try { directory = new DiskScanner(8, traversal).Scan(source.SnapshotDataRoot, null, cancellation, options: null, refresh: null); }
                catch { budgetFailed = traversal.Failure != ScanTraversalLimitKind.None; throw; }
                if (directory.Root.Storage?.Identity != source.RootStorage.Identity || tree.Storage?.Identity != source.RootStorage.Identity)
                { sourceMismatch = true; throw new InvalidDataException("SameSnapshotBinding"); }
                var ordinary = Capture(directory.Root, cancellation);
                directoryExclusions = OrdinaryExclusions(directory, ordinary, cancellation);
                pending = CompareIncluded(fast, ordinary, (relative, isDirectory, probeToken) =>
                {
                    // This owner validates every component and rejects links.
                    // The independent handle then observes the same bound leaf.
                    var bound = source.ReadNative(relative, isDirectory, probeToken);
                    string path = relative.Length == 0 ? source.SnapshotDataRoot : Path.Join(source.SnapshotDataRoot, relative.Replace('/', '\\'));
                    return NtfsSameSnapshotNativeProbe.Read(path, isDirectory, bound, probeToken);
                }, cancellation);
                if (source.ReadNative("", true, cancellation).Identity != source.RootStorage.Identity ||
                    directory.Root.Storage?.Identity != source.RootStorage.Identity || tree.Storage?.Identity != source.RootStorage.Identity)
                { sourceMismatch = true; throw new InvalidDataException("SameSnapshotBinding"); }
                sourceVerified = true;
                callbackCompleted = true;
                }
                catch (ComparisonFailure error) { callbackFailure = error.Code; throw; }
            });
            cleaned = true; timeout.Token.ThrowIfCancellationRequested();
            if (!callbackCompleted || pending == null || fastDirectories == null || directoryExclusions == null)
                return Failure(NtfsSameSnapshotOutcome.Failed, NtfsSameSnapshotCode.TraversalFailed, true);
            // A's declaration is obtained only from Run's real return, after
            // cleanup. B's skipped entries can never stand in for this value.
            var actualFast = FastExclusions(result.ExcludedReparseEntries, result.Exclusions, fastDirectories, timeout.Token);
            return Finish(pending, actualFast, directoryExclusions, true) with { SameSourceVerified = sourceVerified };
        }
        catch (FastScanUnavailableException error)
        {
            if (!error.CleanupConfirmed) return Failure(NtfsSameSnapshotOutcome.Failed, NtfsSameSnapshotCode.CleanupUnconfirmed, false);
            if (timeout.IsCancellationRequested || error.Code == FastScanWireError.Cancelled)
                return Failure(NtfsSameSnapshotOutcome.Cancelled, NtfsSameSnapshotCode.Cancelled, true);
            if (budgetFailed || error.Code == FastScanWireError.LimitExceeded)
                return Failure(NtfsSameSnapshotOutcome.Incomplete, NtfsSameSnapshotCode.BudgetExceeded, true);
            if (callbackFailure is { } fixedCode) return Failure(NtfsSameSnapshotOutcome.Incomplete, fixedCode, true);
            return Failure(NtfsSameSnapshotOutcome.Failed, sourceMismatch ? NtfsSameSnapshotCode.SourceChanged :
                error.Code is FastScanWireError.LimitExceeded ? NtfsSameSnapshotCode.BudgetExceeded :
                error.Code is FastScanWireError.SourceChanged ? NtfsSameSnapshotCode.SourceChanged :
                error.Code is FastScanWireError.SnapshotFailed or FastScanWireError.PermissionDenied or FastScanWireError.Unsupported ? NtfsSameSnapshotCode.SnapshotUnavailable :
                callbackCompleted ? NtfsSameSnapshotCode.ComparisonIncomplete : NtfsSameSnapshotCode.TraversalFailed, true);
        }
        // Run throws cancellation only after its cleanup check succeeds.
        catch (OperationCanceledException) { return Failure(NtfsSameSnapshotOutcome.Cancelled, NtfsSameSnapshotCode.Cancelled, true); }
        catch (ComparisonFailure error) { return Failure(NtfsSameSnapshotOutcome.Incomplete, error.Code, cleaned); }
        catch { return Failure(NtfsSameSnapshotOutcome.Failed, NtfsSameSnapshotCode.InternalFailure, cleaned); }
    }

    // Pure seam: caller values cannot prove a native owner or cleanup. Only
    // Execute changes CleanupConfirmed after the real source's Run returns.
    internal static NtfsSameSnapshotReport Compare(DiskEntry fastRoot, long fastExcluded,
        IReadOnlyList<NtfsReparseBoundary> fastExclusions, ScanResult directory,
        Func<string, bool, CancellationToken, NtfsSameSnapshotNativeObservation>? native = null, CancellationToken token = default)
    {
        try
        {
            var fast = Capture(fastRoot, token); var ordinary = Capture(directory.Root, token);
            var report = CompareIncluded(fast, ordinary, native, token);
            return Finish(report, FastExclusions(fastExcluded, fastExclusions, fast.Directories, token), OrdinaryExclusions(directory, ordinary, token), false);
        }
        catch (OperationCanceledException) { return Failure(NtfsSameSnapshotOutcome.Cancelled, NtfsSameSnapshotCode.Cancelled, false); }
        catch (ComparisonFailure error) { return Failure(NtfsSameSnapshotOutcome.Incomplete, error.Code, false); }
        catch { return Failure(token.IsCancellationRequested ? NtfsSameSnapshotOutcome.Cancelled : NtfsSameSnapshotOutcome.Failed,
            token.IsCancellationRequested ? NtfsSameSnapshotCode.Cancelled : NtfsSameSnapshotCode.ComparisonIncomplete, false); }
    }

    private static NtfsSameSnapshotReport Failure(NtfsSameSnapshotOutcome outcome, NtfsSameSnapshotCode code, bool cleanup) =>
        new() { Outcome = outcome, Code = code, CleanupConfirmed = cleanup };
    private sealed class ComparisonFailure(NtfsSameSnapshotCode code) : Exception("SameSnapshotComparison")
    { internal NtfsSameSnapshotCode Code { get; } = code; }
    private static void Require([DoesNotReturnIf(false)] bool value, NtfsSameSnapshotCode code = NtfsSameSnapshotCode.ComparisonIncomplete)
    { if (!value) throw new ComparisonFailure(code); }

    private readonly record struct Key(ulong A, ulong B, ulong C, ulong D) : IComparable<Key>
    {
        internal static Key Read(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));
        public int CompareTo(Key other)
        { int c = A.CompareTo(other.A); if (c != 0) return c; c = B.CompareTo(other.B); if (c != 0) return c;
          c = C.CompareTo(other.C); return c != 0 ? c : D.CompareTo(other.D); }
        internal void Write(Span<byte> bytes)
        { BinaryPrimitives.WriteUInt64LittleEndian(bytes, A); BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], B);
          BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], C); BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], D); }
    }
    private sealed record Entry(Key Key, DiskEntry Node, bool Directory, FileStorageInfo? Storage, long Logical, bool Known);
    private sealed record Index(DiskEntry Root, List<Entry> Entries, Dictionary<FileIdentity, Key> Directories,
        bool Known, bool RootLogical, bool RootAllocation, bool RootCounts);
    private sealed record ExclusionSet(long Count, HashSet<Key> Paths, bool Complete, bool Valid);
    private static Key Child(Key parent, string name)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        return Child(parent, name, hash);
    }
    private static Key Child(Key parent, string name, IncrementalHash hash)
    {
        Span<byte> header = stackalloc byte[36]; parent.Write(header); BinaryPrimitives.WriteInt32LittleEndian(header[32..], name.Length);
        hash.AppendData(header); hash.AppendData(Encoding.UTF8.GetBytes(name)); return Key.Read(hash.GetHashAndReset());
    }
    private static readonly Key RootKey = Child(default, "");
    private static bool StorageKnown(DiskEntry node, FileStorageInfo? storage, ulong? volume) => storage != null &&
        storage.Identity.High == 0 && storage.Identity.Low >> 48 != 0 && storage.Identity.Volume == volume && storage.LinkCount > 0 &&
        node.UnknownAllocationFiles == 0 && (node.IsDirectory || storage.LogicalBytes >= 0 && storage.AllocatedBytes >= 0 && node.Size == storage.LogicalBytes);
    private static Index Capture(DiskEntry root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Require(root != null && root.IsDirectory && root.Parent == null);
        var nodes = new List<Entry>(); var directories = new Dictionary<FileIdentity, Key>();
        var seen = new HashSet<DiskEntry>(ReferenceEqualityComparer.Instance);
        var objects = new Dictionary<FileIdentity, (FileStorageInfo Storage, long Aliases)>();
        var queue = new Queue<(DiskEntry Node, Key Parent, int Depth, int ParentLength)>(); queue.Enqueue((root, default, 0, 0));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        bool known = true, logicalKnown = true, allocationKnown = true; long names = 0, paths = 0, logical = 0, allocated = 0, files = 0, folders = 0;
        ulong? volume = root.Storage?.Identity.Volume;
        while (queue.TryDequeue(out var work))
        {
            token.ThrowIfCancellationRequested(); var node = work.Node;
            Require(node != null && seen.Add(node)); Require(nodes.Count < FastScanWire.MaximumNodes && work.Depth <= FastScanWire.MaximumDepth, NtfsSameSnapshotCode.BudgetExceeded);
            string name = node == root ? "" : node.Name; Require(node == root || FastScanWire.ValidName(name));
            int length = node == root ? 0 : checked(work.ParentLength + (work.ParentLength == 0 ? 0 : 1) + name.Length);
            names = checked(names + name.Length); if (node.IsDirectory) paths = checked(paths + length);
            Require(length <= 32767 && names <= FastScanWire.MaximumNameCharacters && paths <= FastScanWire.MaximumNameCharacters, NtfsSameSnapshotCode.BudgetExceeded);
            Key key = node == root ? RootKey : Child(work.Parent, name, hash);
            var storage = node.Storage; bool good = StorageKnown(node, storage, volume); known &= good;
            nodes.Add(new(key, node, node.IsDirectory, storage, node.Size, good));
            if (node.IsDirectory)
            {
                if (node != root) folders = checked(folders + 1);
                if (storage != null && !directories.TryAdd(storage.Identity, key)) known = false;
            }
            else
            {
                files = checked(files + 1);
                if (node.Size < 0) logicalKnown = false; else logical = checked(logical + node.Size);
                if (!good || storage == null) allocationKnown = false;
                else if (objects.TryGetValue(storage.Identity, out var previous))
                {
                    long aliases = checked(previous.Aliases + 1);
                    objects[storage.Identity] = (previous.Storage, aliases);
                    if (previous.Storage.AllocatedBytes != storage.AllocatedBytes || previous.Storage.LogicalBytes != storage.LogicalBytes ||
                        previous.Storage.LinkCount != storage.LinkCount || aliases > storage.LinkCount)
                    { known = false; allocationKnown = false; }
                }
                else { objects.Add(storage.Identity, (storage, 1)); allocated = checked(allocated + storage.AllocatedBytes); }
            }
            Require(node.IsDirectory || node.Children.Count == 0);
            Require(node.Children.Count <= FastScanWire.MaximumNodes - nodes.Count - queue.Count, NtfsSameSnapshotCode.BudgetExceeded);
            foreach (var child in node.Children)
            { Require(child != null && ReferenceEquals(child.Parent, node)); queue.Enqueue((child, key, work.Depth + 1, length)); }
        }
        nodes.Sort(static (left, right) => left.Key.CompareTo(right.Key));
        for (int i = 1; i < nodes.Count; i++) { token.ThrowIfCancellationRequested(); Require(nodes[i - 1].Key != nodes[i].Key); }
        return new(root, nodes, directories, known, logicalKnown && root.Size == logical, allocationKnown && root.AllocatedSize == allocated,
            root.FileCount == files && root.FolderCount == folders);
    }

    private static IEnumerable<(Entry? Fast, Entry? Directory)> Pairs(Index fast, Index ordinary)
    {
        int a = 0, b = 0;
        while (a < fast.Entries.Count || b < ordinary.Entries.Count)
        {
            int order = a == fast.Entries.Count ? 1 : b == ordinary.Entries.Count ? -1 : fast.Entries[a].Key.CompareTo(ordinary.Entries[b].Key);
            if (order < 0) yield return (fast.Entries[a++], null);
            else if (order > 0) yield return (null, ordinary.Entries[b++]);
            else yield return (fast.Entries[a++], ordinary.Entries[b++]);
        }
    }
    private static bool Different(Entry? a, Entry? b) => a == null || b == null || !a.Known || !b.Known || a.Directory != b.Directory ||
        a.Storage?.Identity != b.Storage?.Identity || !a.Directory && !b.Directory &&
        (a.Logical != b.Logical || a.Storage?.LogicalBytes != b.Storage?.LogicalBytes || a.Storage?.AllocatedBytes != b.Storage?.AllocatedBytes || a.Storage?.LinkCount != b.Storage?.LinkCount);
    private static NtfsSameSnapshotReport CompareIncluded(Index fast, Index ordinary,
        Func<string, bool, CancellationToken, NtfsSameSnapshotNativeObservation>? native, CancellationToken token)
    {
        bool scope = true, types = true, identities = true, logical = true, allocation = true, links = true, difference = false;
        foreach (var (a, b) in Pairs(fast, ordinary))
        {
            token.ThrowIfCancellationRequested();
            if (a == null || b == null) { scope = types = identities = logical = allocation = links = false; difference = true; continue; }
            types &= a.Directory == b.Directory; identities &= a.Storage?.Identity == b.Storage?.Identity && a.Storage != null && b.Storage != null;
            if (a.Directory != b.Directory) logical = allocation = links = false;
            if (!a.Directory && !b.Directory)
            {
                logical &= a.Logical == b.Logical && a.Storage?.LogicalBytes == b.Storage?.LogicalBytes;
                allocation &= a.Storage?.AllocatedBytes == b.Storage?.AllocatedBytes;
                links &= a.Storage?.LinkCount == b.Storage?.LinkCount;
            }
            difference |= Different(a, b);
        }
        difference |= !fast.RootLogical || !fast.RootAllocation || !ordinary.RootLogical || !ordinary.RootAllocation ||
            !fast.RootCounts || !ordinary.RootCounts || !fast.Known || !ordinary.Known;
        var report = new NtfsSameSnapshotReport { IncludedScopeEqual = scope, TypesEqual = types, IdentitiesEqual = identities,
            FileLogicalEqual = logical, FileAllocationEqual = allocation, FileLinksEqual = links, AllocationKnown = fast.Known && ordinary.Known,
            FastRootLogicalValid = fast.RootLogical, FastRootAllocationValid = fast.RootAllocation,
            DirectoryRootLogicalValid = ordinary.RootLogical, DirectoryRootAllocationValid = ordinary.RootAllocation,
            FastRootCountsValid = fast.RootCounts, DirectoryRootCountsValid = ordinary.RootCounts, NativeProbeRequired = difference, BudgetRespected = true };
        if (!difference) return report;
        var probes = new ProbeAccumulator(); var spent = new HashSet<ProbeKey>(); var cache = new Dictionary<ProbeKey, NtfsSameSnapshotNativeObservation>();
        bool sawPair = false, completed = native != null, limitExceeded = false, probeFailure = native == null;
        void Probe(Entry? a, Entry? b)
        {
            sawPair = true; token.ThrowIfCancellationRequested(); var chosen = b ?? a!;
            foreach (var entry in new[] { a, b })
            {
                if (entry == null) continue;
                var candidate = ProbeKey.Of(entry);
                if (!spent.Contains(candidate))
                { if (spent.Count == MaximumNativeObjects) { completed = false; limitExceeded = true; return; } spent.Add(candidate); }
            }
            if (native == null) { completed = false; return; }
            var key = ProbeKey.Of(chosen);
            try
            {
                if (!cache.TryGetValue(key, out var value))
                {
                    string root = b != null ? ordinary.Root.Path : fast.Root.Path;
                    string relative = Path.GetRelativePath(root, chosen.Node.Path).Replace('\\', '/');
                    if (relative == ".") relative = "";
                    _ = NtfsPersonalSnapshotSource.RelativeSegments(relative, chosen.Directory);
                    probes.Attempted = true; value = native(relative, chosen.Directory, token); token.ThrowIfCancellationRequested();
                    Require(value != null); cache.Add(key, value);
                }
                probes.Add(a, b, value);
                if (!value.Consistent) completed = false;
            }
            catch { token.ThrowIfCancellationRequested(); completed = false; probeFailure = true; }
        }
        foreach (var pair in Pairs(fast, ordinary)) { token.ThrowIfCancellationRequested(); if (Different(pair.Fast, pair.Directory)) Probe(pair.Fast, pair.Directory); }
        if (!sawPair) Probe(fast.Entries.First(entry => ReferenceEquals(entry.Node, fast.Root)), ordinary.Entries.First(entry => ReferenceEquals(entry.Node, ordinary.Root)));
        return probes.Apply(report, completed && probes.Attempted) with { BudgetRespected = !limitExceeded && !probeFailure,
            Code = limitExceeded ? NtfsSameSnapshotCode.BudgetExceeded : NtfsSameSnapshotCode.NativeProbeIncomplete };
    }
    private readonly record struct ProbeKey(FileIdentity Identity, Key Path)
    {
        internal static ProbeKey Of(Entry entry) => entry.Storage is { } value && (value.Identity.Low != 0 || value.Identity.High != 0)
            ? new(value.Identity, default) : new(default, entry.Key);
    }
    private sealed class ProbeAccumulator
    {
        internal bool Attempted; private NtfsSameSnapshotProbeBranch branch;
        private bool? ai, bi, al, bl, aa, ba, ak, bk, ast, bst, consistent, compression;
        private static void And(ref bool? value, bool next) => value = (value ?? true) && next;
        internal void Add(Entry? a, Entry? b, NtfsSameSnapshotNativeObservation value)
        {
            branch = branch == NtfsSameSnapshotProbeBranch.None ? value.Branch : branch == value.Branch ? branch : NtfsSameSnapshotProbeBranch.Mixed;
            And(ref ai, a?.Directory == value.Directory && a?.Storage?.Identity == value.Identity);
            And(ref bi, b?.Directory == value.Directory && b?.Storage?.Identity == value.Identity);
            And(ref consistent, value.Consistent); compression = (compression ?? false) || value.Compressed || value.Sparse;
            if (value.Directory) return;
            And(ref al, a is { Directory: false } && a.Logical == value.Logical && a.Storage?.LogicalBytes == value.Logical);
            And(ref bl, b is { Directory: false } && b.Logical == value.Logical && b.Storage?.LogicalBytes == value.Logical);
            And(ref aa, a is { Directory: false } && a.Storage?.AllocatedBytes == value.Allocated);
            And(ref ba, b is { Directory: false } && b.Storage?.AllocatedBytes == value.Allocated);
            And(ref ak, a is { Directory: false } && a.Storage?.LinkCount == value.Links);
            And(ref bk, b is { Directory: false } && b.Storage?.LinkCount == value.Links);
            And(ref ast, a is { Directory: false } && a.Storage?.AllocatedBytes == value.StandardAllocation);
            And(ref bst, b is { Directory: false } && b.Storage?.AllocatedBytes == value.StandardAllocation);
        }
        internal NtfsSameSnapshotReport Apply(NtfsSameSnapshotReport report, bool complete) => report with
        {
            NativeProbeAttempted = Attempted, NativeProbeComplete = complete, ProbeBranch = branch,
            FastMatchesNativeIdentity = complete ? ai : null, DirectoryMatchesNativeIdentity = complete ? bi : null,
            FastMatchesNativeLogical = complete ? al : null, DirectoryMatchesNativeLogical = complete ? bl : null,
            FastMatchesNativeAllocation = complete ? aa : null, DirectoryMatchesNativeAllocation = complete ? ba : null,
            FastMatchesNativeLinks = complete ? ak : null, DirectoryMatchesNativeLinks = complete ? bk : null,
            FastMatchesNativeStandardAllocation = complete ? ast : null, DirectoryMatchesNativeStandardAllocation = complete ? bst : null,
            NativeMetadataConsistent = consistent, NativeCompressionObserved = compression
        };
    }

    private static Key RelativeKey(string relative)
    {
        Require(relative.Length is >= 1 and <= 32767 && !Path.IsPathRooted(relative));
        var parts = relative.Replace('\\', '/').Split('/'); Require(parts.Length <= FastScanWire.MaximumDepth);
        Key key = RootKey;
        foreach (string part in parts) { Require(FastScanWire.ValidName(part)); key = Child(key, part); }
        return key;
    }
    private static ExclusionSet FastExclusions(long count, IReadOnlyList<NtfsReparseBoundary> details,
        IReadOnlyDictionary<FileIdentity, Key> directories, CancellationToken token)
    {
        Require(count is >= 0 and <= FastScanWire.MaximumNodes && details != null && details.Count == Math.Min(count, 2000));
        var paths = new HashSet<Key>(); bool valid = true;
        for (int i = 0; i < details.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var item = details[i];
            Require(item != null && FastScanWire.ValidName(item.Name));
            if (!directories.TryGetValue(item.ParentIdentity, out var parent)) { valid = false; continue; }
            valid &= paths.Add(Child(parent, item.Name));
        }
        return new(count, paths, count == paths.Count, valid);
    }
    private static ExclusionSet OrdinaryExclusions(ScanResult result, Index ordinary, CancellationToken token)
    {
        Require(result.Skipped >= 0 && result.Issues.Count <= 2000 && result.AllocationIssues.Count <= 2000);
        var paths = new HashSet<Key>(); bool valid = result.AllocationIssues.Count == 0; long chars = 0;
        for (int i = 0; i < result.Issues.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var issue = result.Issues[i];
            if (issue.Reason != "跳过符号链接、联接点或其他重解析点") { valid = false; continue; }
            string relative = Path.GetRelativePath(ordinary.Root.Path, issue.Path).Replace('\\', '/');
            chars = checked(chars + relative.Length); Require(chars <= FastScanWire.MaximumNameCharacters, NtfsSameSnapshotCode.BudgetExceeded);
            valid &= paths.Add(RelativeKey(relative));
        }
        return new(result.Skipped, paths, result.Skipped == paths.Count, valid);
    }
    private static NtfsSameSnapshotReport Finish(NtfsSameSnapshotReport report, ExclusionSet fast, ExclusionSet ordinary, bool cleanup)
    {
        bool details = fast.Complete && ordinary.Complete && fast.Valid && ordinary.Valid;
        bool exclusions = details && fast.Count == ordinary.Count && fast.Paths.SetEquals(ordinary.Paths);
        bool core = report.IncludedScopeEqual == true && report.TypesEqual == true && report.IdentitiesEqual == true && report.FileLogicalEqual == true &&
            report.FileAllocationEqual == true && report.FileLinksEqual == true && report.FastRootLogicalValid == true && report.FastRootAllocationValid == true &&
            report.DirectoryRootLogicalValid == true && report.DirectoryRootAllocationValid == true && report.FastRootCountsValid == true && report.DirectoryRootCountsValid == true;
        bool equal = core && report.AllocationKnown == true && exclusions;
        bool required = report.NativeProbeRequired || !equal;
        bool complete = details && report.AllocationKnown == true && report.BudgetRespected && (!required || report.NativeProbeComplete);
        return report with { CleanupConfirmed = cleanup, ExclusionsEqual = exclusions, ExclusionDetailsComplete = details, NativeProbeRequired = required,
            ComparisonComplete = complete, Outcome = equal ? NtfsSameSnapshotOutcome.Matched : complete ? NtfsSameSnapshotOutcome.Different : NtfsSameSnapshotOutcome.Incomplete,
            Code = equal || complete ? NtfsSameSnapshotCode.None : report.Code == NtfsSameSnapshotCode.BudgetExceeded ? NtfsSameSnapshotCode.BudgetExceeded :
                !details || report.AllocationKnown != true ? NtfsSameSnapshotCode.ComparisonIncomplete :
                required && !report.NativeProbeComplete ? NtfsSameSnapshotCode.NativeProbeIncomplete : NtfsSameSnapshotCode.ComparisonIncomplete };
    }
}
