using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DiskAtlas;

// Explicit, local-only timing entry. No scope, filename, identity, count, hash,
// exception text or result tree is written to stdout or a file.
internal static class NtfsPersonalTimingDriver
{
    internal const string Argument = "--ntfs-personal-timing";
    internal static bool ValidInvocation(string[] args) => args.Length == 1 && args[0] == Argument;
    internal static bool ValidOrder(string order) => order is "FO" or "OF" or "FOOF";
    internal static (string Order, string Scope) ReadInput(TextReader input)
    {
        try
        {
            Span<char> mode = stackalloc char[5]; int length = 0;
            while (true)
            {
                int value = input.Read();
                if (value is -1 or '\n') break;
                if (length == mode.Length) throw new InvalidDataException();
                mode[length++] = (char)value;
            }
            if (length > 0 && mode[length - 1] == '\r') length--;
            string order = new string(mode[..length]);
            if (order.Length == 0) order = "FO";
            if (!ValidOrder(order)) throw new InvalidDataException();
            return (order, NtfsPrototype.NtfsPersonalAcceptanceDriver.ReadScope(input));
        }
        catch { throw new InvalidDataException("TimingInput"); }
    }

    internal static int Run()
    {
        string stage = "Preparing";
        try
        {
            if (!FastScanBridge.IsTimingHost(Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name))
                return Failure("AppHostRequired", stage, true);
            var input = ReadInput(Console.In);
            using var cancelled = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, args) => { args.Cancel = true; cancelled.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                (char Mode, NtfsTimingFingerprint Value)? previous = null;
                int completed = 0;
                foreach (char mode in input.Order)
                {
                    cancelled.Token.ThrowIfCancellationRequested();
                    // Prior trees are no longer retained. Collection happens
                    // outside the measured scan and before the next run.
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    var current = (Mode: mode, Value: RunOne(input.Scope, mode, cancelled.Token, value => stage = value));
                    completed++;
                    if (previous is not { } left) { previous = current; continue; }
                    var comparison = left.Value.Compare(current.Value, cancelled.Token);
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        revision = "personal-ntfs-timing-v2", kind = "comparison", left = left.Mode.ToString(), right = current.Mode.ToString(),
                        included_scope_equal = comparison.Included, root_metrics_equal = comparison.RootMetrics,
                        exclusions_equal = comparison.Exclusions, exclusion_details_complete = comparison.ExclusionsComparable,
                        other_issues_absent = comparison.NoOtherIssues, allocation_known = comparison.AllocationKnown,
                        all_equal = comparison.AllEqual, changes_or_scope_difference_observed = !comparison.AllEqual,
                        added_entries_observed = comparison.Added, removed_entries_observed = comparison.Removed,
                        entry_type_changes_observed = comparison.TypeChanged, identity_changes_observed = comparison.IdentityChanged,
                        file_logical_changes_observed = comparison.FileLogicalChanged, file_allocation_changes_observed = comparison.FileAllocationChanged,
                        file_link_changes_observed = comparison.FileLinksChanged,
                        snapshot_mtime_check_available = comparison.SnapshotTimeAvailable,
                        changed_entry_mtime_after_snapshot = comparison.ChangedMtimeAfterSnapshot,
                        difference_categories_prove_external_changes = false
                    }));
                    if (!comparison.AllEqual)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new { revision = "personal-ntfs-timing-v2", kind = "complete", order = input.Order,
                            compared = true, all_equal = false, requested_order_finished = completed == input.Order.Length, performance_conclusion_allowed = false, files_written = false }));
                        return 3;
                    }
                    previous = current;
                }
                Console.WriteLine(JsonSerializer.Serialize(new { revision = "personal-ntfs-timing-v2", kind = "complete", order = input.Order,
                    compared = true, all_equal = true, requested_order_finished = true, performance_conclusion_allowed = true, files_written = false }));
                return 0;
            }
            finally { Console.CancelKeyPress -= handler; }
        }
        catch (FastScanUnavailableException error) { return Failure(error.Code.ToString(), error.Context?.Stage.ToString() ?? stage, error.CleanupConfirmed); }
        catch (OperationCanceledException) { return Failure("Cancelled", stage, true); }
        catch (InvalidDataException) { return Failure("TimingInputOrFingerprint", stage, true); }
        catch { return Failure("TimingFailed", stage, false); }
    }

    private static NtfsTimingFingerprint RunOne(string scope, char mode, CancellationToken cancellation, Action<string> observed)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var watch = Stopwatch.StartNew(); TimeSpan? snapshot = null; ScanResult result;
        TimeSpan? bridge = null, publication = null;
        void Stage(string value)
        {
            observed(value);
            Console.WriteLine(JsonSerializer.Serialize(new { revision = "personal-ntfs-timing-v2", kind = "stage", mode = mode.ToString(), stage = value }));
        }
        if (mode == 'F')
        {
            Stage("ElevationAndStartup");
            var measured = MeasureFast(scope, timeout.Token, (path, progress, token) => FastScanBridge.ScanForTiming(path, progress, token, stage =>
            {
                if (stage == FastScanWireStage.Snapshot && snapshot == null) snapshot = watch.Elapsed;
                Stage(stage.ToString());
            }));
            result = measured.Result; bridge = measured.Bridge; publication = measured.PublicationValidation;
            if (result.Source == null || result.FastScanFallback != null || result.Checkpoint != null || snapshot == null)
                throw new InvalidDataException("TimingFastContract");
        }
        else
        {
            Stage("OrdinaryFullAutomatic");
            result = new ScanCoordinator().Scan(scope, null, new(ScanMode.Automatic), null, timeout.Token, null, forceFull: true);
            if (result.Source != null || result.Statistics is not { Mode: ScanMode.Automatic, Method: RefreshMethod.FullScan })
                throw new InvalidDataException("TimingOrdinaryContract");
        }
        watch.Stop(); timeout.Token.ThrowIfCancellationRequested();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            revision = "personal-ntfs-timing-v2", kind = "sample", mode = mode.ToString(), total_ms = watch.Elapsed.TotalMilliseconds,
            bridge_ms = bridge?.TotalMilliseconds, publication_validation_ms = publication?.TotalMilliseconds,
            pre_snapshot_ms = snapshot?.TotalMilliseconds, from_snapshot_ms = snapshot == null ? (double?)null : (watch.Elapsed - snapshot.Value).TotalMilliseconds,
            total_includes_elevation_wait = mode == 'F', snapshot_interval_includes_creation_scan_cleanup_transfer_and_publication_validation = mode == 'F',
            cleanup_confirmed = true, fallback = false, audit_added = false, gc_between_runs = true,
            publication_validation_included = mode == 'F', ui_drawing_included = false,
            compact_diagnostics_retained_between_runs = true
        }));
        // This walk and its hashing are deliberately outside the measured scan.
        Stage("FingerprintOutsideTiming");
        return NtfsTimingFingerprint.Capture(result, timeout.Token);
    }

    internal sealed record FastMeasurement(ScanResult Result, TimeSpan Bridge, TimeSpan PublicationValidation);
    // The real coordinator validates the already-cleaned bridge result. The
    // only injectable operation is the fast scan itself; fallback is always a
    // controlled failure and can never start a second native directory scan.
    internal static FastMeasurement MeasureFast(string scope, CancellationToken token,
        Func<string, IProgress<ScanProgress>?, CancellationToken, ScanResult> fast)
    {
        token.ThrowIfCancellationRequested();
        var total = Stopwatch.StartNew(); TimeSpan bridge = default, bridgeReturned = default;
        bool returned = false; FastScanWireError fallback = FastScanWireError.ProtocolError;
        FastScanFailureContext? context = null;
        var progress = new ImmediateProgress(value =>
        {
            if (value.FastScanFallback is { } code) { fallback = code; context = value.FastScanFallbackContext; }
        });
        var result = FastScanCoordinator.Scan(scope, null, new(ScanMode.Automatic), progress, token, new LiveScanSession(), true,
            (path, _, cancellation) =>
            {
                var clock = Stopwatch.StartNew(); var value = fast(path, null, cancellation); clock.Stop();
                bridge = clock.Elapsed; bridgeReturned = total.Elapsed; returned = true; return value;
            },
            _ => throw new FastScanUnavailableException(fallback, true, context));
        total.Stop();
        if (!returned || result.Source == null || result.FastScanFallback != null || result.Checkpoint != null)
            throw new InvalidDataException("TimingFastContract");
        return new(result, bridge, total.Elapsed - bridgeReturned);
    }
    private sealed class ImmediateProgress(Action<ScanProgress> deliver) : IProgress<ScanProgress>
    { public void Report(ScanProgress value) => deliver(value); }

    private static int Failure(string code, string stage, bool cleanup)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { revision = "personal-ntfs-timing-v2", kind = "failed", code, stage,
            cleanup_confirmed = cleanup, further_runs_started = false, performance_conclusion_allowed = false }));
        return 1;
    }
}

internal sealed class NtfsTimingFingerprint
{
    private static readonly byte[] PathTag = [1], EntryTag = [2], BoundaryTag = [3];
    private readonly byte[] included, exclusions;
    private readonly List<CompactEntry> compact;
    private readonly long? snapshotTicks;
    private readonly long logical, allocated, files, folders, excluded;
    private readonly bool exclusionsComplete, noOtherIssues, allocationKnown;
    private NtfsTimingFingerprint(byte[] included, byte[] exclusions, List<CompactEntry> compact,
        ScanResult result, bool complete, bool noOtherIssues, bool allocationKnown)
    {
        this.included = included; this.exclusions = exclusions; logical = result.Root.Size; allocated = result.Root.AllocatedSize;
        files = result.Root.FileCount; folders = result.Root.FolderCount; excluded = result.Skipped;
        exclusionsComplete = complete; this.noOtherIssues = noOtherIssues; this.allocationKnown = allocationKnown;
        this.compact = compact; snapshotTicks = result.Source?.CapturedAt.UtcTicks;
    }
    internal readonly record struct Comparison(bool Included, bool RootMetrics, bool Exclusions, bool ExclusionsComparable, bool NoOtherIssues, bool AllocationKnown,
        bool Added, bool Removed, bool TypeChanged, bool IdentityChanged, bool FileLogicalChanged, bool FileAllocationChanged, bool FileLinksChanged,
        bool SnapshotTimeAvailable, bool ChangedMtimeAfterSnapshot)
    { internal bool AllEqual => Included && RootMetrics && Exclusions && ExclusionsComparable && NoOtherIssues && AllocationKnown; }
    internal Comparison Compare(NtfsTimingFingerprint other, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        bool added = false, removed = false, type = false, identity = false, fileLogical = false, allocation = false, links = false, later = false;
        // FO/OF uses the fast snapshot's time. If two fast captures are ever
        // compared, use the earlier observation and disclose that it is only a
        // timestamp relation, never independent evidence of an external writer.
        long? snapshot = snapshotTicks is { } own && other.snapshotTicks is { } next ? Math.Min(own, next) : snapshotTicks ?? other.snapshotTicks;
        void ObserveTime(CompactEntry entry)
        { if (snapshot.HasValue && !entry.Directory && entry.ModifiedUtcTicks > snapshot.Value) later = true; }
        int left = 0, right = 0;
        while (left < compact.Count || right < other.compact.Count)
        {
            token.ThrowIfCancellationRequested();
            int order = left == compact.Count ? 1 : right == other.compact.Count ? -1 : compact[left].Key.CompareTo(other.compact[right].Key);
            if (order < 0) { removed = true; ObserveTime(compact[left++]); continue; }
            if (order > 0) { added = true; ObserveTime(other.compact[right++]); continue; }
            var before = compact[left++]; var after = other.compact[right++];
            bool kindChanged = before.Directory != after.Directory;
            bool idChanged = before.HasStorage != after.HasStorage || before.Identity != after.Identity;
            bool logicalChanged = !before.Directory && !after.Directory &&
                (before.NativeLogical != after.NativeLogical || before.NodeLogical != after.NodeLogical);
            bool allocationChanged = !before.Directory && !after.Directory && before.Allocation != after.Allocation;
            bool linksChanged = !before.Directory && !after.Directory && before.Links != after.Links;
            type |= kindChanged; identity |= idChanged; fileLogical |= logicalChanged; allocation |= allocationChanged; links |= linksChanged;
            if (kindChanged || idChanged || logicalChanged || allocationChanged || linksChanged) { ObserveTime(before); ObserveTime(after); }
        }
        token.ThrowIfCancellationRequested();
        return new(included.AsSpan().SequenceEqual(other.included),
            logical == other.logical && allocated == other.allocated && files == other.files && folders == other.folders,
            exclusionsComplete && other.exclusionsComplete && excluded == other.excluded && exclusions.AsSpan().SequenceEqual(other.exclusions),
            exclusionsComplete && other.exclusionsComplete, noOtherIssues && other.noOtherIssues, allocationKnown && other.allocationKnown,
            added, removed, type, identity, fileLogical, allocation, links, snapshot.HasValue, later);
    }
    private readonly record struct PathKey(ulong A, ulong B, ulong C, ulong D) : IComparable<PathKey>
    {
        internal static PathKey From(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));
        public int CompareTo(PathKey other)
        { int value = A.CompareTo(other.A); if (value != 0) return value; value = B.CompareTo(other.B); if (value != 0) return value;
          value = C.CompareTo(other.C); return value != 0 ? value : D.CompareTo(other.D); }
    }
    private readonly record struct CompactEntry(PathKey Key, bool Directory, bool HasStorage, FileIdentity Identity,
        long NativeLogical, long NodeLogical, long Allocation, uint Links, long ModifiedUtcTicks);

    internal static NtfsTimingFingerprint Capture(ScanResult result, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (result.Root.Parent != null || !result.Root.IsDirectory || result.Skipped < 0 || result.Root.Size < 0 || result.Root.AllocatedSize < 0)
                throw new InvalidDataException();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var entries = new Multiset(); var boundaries = new Multiset(); var compact = new List<CompactEntry>();
            var seen = new HashSet<DiskEntry>(ReferenceEqualityComparer.Instance);
            var queue = new Queue<(DiskEntry Entry, byte[] ParentPath, int Depth)>(); queue.Enqueue((result.Root, SHA256.HashData(PathTag), 0));
            bool known = result.Root.UnknownAllocationFiles == 0; long characters = 0; int visited = 0;
            Span<byte> metadata = stackalloc byte[54];
            while (queue.TryDequeue(out var work))
            {
                token.ThrowIfCancellationRequested(); var node = work.Entry;
                if (!seen.Add(node) || ++visited > FastScanWire.MaximumNodes || work.Depth > FastScanWire.MaximumDepth) throw new InvalidDataException();
                string name = node.Parent == null ? "" : node.Name;
                if (node.Parent != null && !FastScanWire.ValidName(name)) throw new InvalidDataException();
                characters = checked(characters + name.Length);
                if (characters > FastScanWire.MaximumNameCharacters) throw new InvalidDataException();
                hash.AppendData(PathTag); hash.AppendData(work.ParentPath); Text(hash, name); byte[] path = hash.GetHashAndReset();
                metadata.Clear(); var storage = node.Storage;
                metadata[0] = node.IsDirectory ? (byte)1 : (byte)0; metadata[1] = storage != null ? (byte)1 : (byte)0;
                if (storage != null)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(metadata[2..], storage.Identity.Volume);
                    BinaryPrimitives.WriteUInt64LittleEndian(metadata[10..], storage.Identity.Low);
                    BinaryPrimitives.WriteUInt64LittleEndian(metadata[18..], storage.Identity.High);
                    // Directory storage fields use different native/raw
                    // representations and do not describe file-data totals.
                    // Their namespace identity is still mandatory and hashed.
                    if (!node.IsDirectory)
                    {
                        BinaryPrimitives.WriteInt64LittleEndian(metadata[26..], storage.LogicalBytes);
                        BinaryPrimitives.WriteInt64LittleEndian(metadata[34..], storage.AllocatedBytes);
                        BinaryPrimitives.WriteUInt32LittleEndian(metadata[42..], storage.LinkCount);
                    }
                }
                BinaryPrimitives.WriteInt64LittleEndian(metadata[46..], node.IsDirectory ? 0 : node.Size);
                known &= storage != null && (storage.Identity.Low != 0 || storage.Identity.High != 0) && node.UnknownAllocationFiles == 0 &&
                    (node.IsDirectory || storage.AllocatedBytes >= 0 && storage.LogicalBytes >= 0 && node.Size >= 0);
                long modified = 0;
                if (!node.IsDirectory)
                {
                    DateTime time = storage?.Modified ?? node.Modified;
                    if (time > DateTime.MinValue) modified = time.ToUniversalTime().Ticks;
                }
                compact.Add(new(PathKey.From(path), node.IsDirectory, storage != null, storage?.Identity ?? default,
                    node.IsDirectory ? 0 : storage?.LogicalBytes ?? -1, node.IsDirectory ? 0 : node.Size,
                    node.IsDirectory ? 0 : storage?.AllocatedBytes ?? -1, node.IsDirectory ? 0 : storage?.LinkCount ?? 0, modified));
                hash.AppendData(EntryTag); hash.AppendData(path); hash.AppendData(metadata); entries.Add(hash.GetHashAndReset());
                if (node.Children.Count > FastScanWire.MaximumNodes - visited - queue.Count || !node.IsDirectory && node.Children.Count != 0) throw new InvalidDataException();
                foreach (var child in node.Children)
                {
                    if (!ReferenceEquals(child.Parent, node)) throw new InvalidDataException();
                    queue.Enqueue((child, path, work.Depth + 1));
                }
            }
            if (result.Issues.Count > FastScanWire.MaximumExclusionDetails || result.AllocationIssues.Count > FastScanWire.MaximumExclusionDetails) throw new InvalidDataException();
            long reparse = 0; bool noOthers = result.AllocationIssues.Count == 0; long issueCharacters = 0;
            for (int i = 0; i < result.Issues.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var issue = result.Issues[i];
                bool link = issue.Reason is "跳过符号链接、联接点或其他重解析点" or NtfsPrototype.NtfsReparsePolicy.Reason;
                noOthers &= link; if (!link) continue;
                string relative = Path.GetRelativePath(result.Root.Path, issue.Path).Replace('\\', '/');
                if (Path.IsPathRooted(relative) || relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal)) throw new InvalidDataException();
                issueCharacters = checked(issueCharacters + relative.Length);
                if (relative.Length > 32767 || issueCharacters > FastScanWire.MaximumNameCharacters) throw new InvalidDataException();
                hash.AppendData(BoundaryTag); Text(hash, relative); boundaries.Add(hash.GetHashAndReset()); reparse++;
            }
            if (result.Source != null && result.Source.ExcludedReparseEntries != result.Skipped) throw new InvalidDataException();
            compact.Sort(static (left, right) => left.Key.CompareTo(right.Key));
            for (int i = 1; i < compact.Count; i++)
            { token.ThrowIfCancellationRequested(); if (compact[i - 1].Key == compact[i].Key) throw new InvalidDataException(); }
            token.ThrowIfCancellationRequested();
            return new(entries.Finish(), boundaries.Finish(), compact, result, reparse == result.Skipped, noOthers, known);
        }
        catch { token.ThrowIfCancellationRequested(); throw new InvalidDataException("TimingFingerprint"); }
    }
    private static void Text(IncrementalHash hash, string text)
    {
        Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, text.Length); hash.AppendData(size);
        hash.AppendData(Encoding.UTF8.GetBytes(text));
    }
    private sealed class Multiset
    {
        // Private, order-independent consistency diagnostic: SHA-256 entry
        // hashes plus both modular sum, XOR and multiplicity. It is not a
        // source proof or verification of file contents.
        private readonly byte[] xor = new byte[32], sum = new byte[32]; private long count;
        internal void Add(byte[] value)
        {
            int carry = 0;
            for (int i = 0; i < 32; i++) { xor[i] ^= value[i]; int added = sum[i] + value[i] + carry; sum[i] = (byte)added; carry = added >> 8; }
            count = checked(count + 1);
        }
        internal byte[] Finish()
        {
            Span<byte> value = stackalloc byte[72]; xor.CopyTo(value); sum.CopyTo(value[32..]); BinaryPrimitives.WriteInt64LittleEndian(value[64..], count);
            return SHA256.HashData(value);
        }
    }
}
