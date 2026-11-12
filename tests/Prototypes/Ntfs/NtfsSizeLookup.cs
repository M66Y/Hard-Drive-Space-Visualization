using System.Diagnostics;

namespace DiskAtlas.NtfsPrototype;

internal enum NtfsSizeSource { Record, HandleLookup, Unknown }
internal sealed record NtfsLookupItem(ulong Volume, NtfsRecord Record, string Path);
internal sealed record NtfsResolvedSize(ulong Volume, ulong Reference, long? LogicalBytes, long? AllocatedBytes,
    NtfsSizeSource LogicalSource, NtfsSizeSource AllocatedSource, string Code);
internal sealed record NtfsLookupMetrics(int Objects, int EligibleObjects, int NativeCalls, int ResolvedObjects,
    int FailedObjects, int UnsupportedObjects, double ElapsedMilliseconds, double NativeMilliseconds);
internal sealed record NtfsLookupBatch(bool Complete, string Code, IReadOnlyList<NtfsResolvedSize> Items, NtfsLookupMetrics Metrics);

// Test-only, per-batch resident allocation lookup. One synchronous reader, no
// queued work, no persistent cache. Callers own and validate the file scope.
internal static class NtfsSizeLookup
{
    internal const int MaximumObjects = 4096;
    internal static NtfsLookupBatch Resolve(IReadOnlyList<NtfsLookupItem> items,
        Func<string, CancellationToken, FileStorageInfo?> reader, CancellationToken token = default, int limit = MaximumObjects)
        => ResolveCore(items, reader, null, token, limit);

    internal static NtfsLookupBatch ResolveWithLease(IReadOnlyList<NtfsLookupItem> items,
        Func<NtfsLookupItem, CancellationToken, NtfsLeaseResult> reader, CancellationToken token = default, int limit = MaximumObjects)
        => ResolveCore(items, null, reader, token, limit);

    internal static NtfsLookupBatch ResolveSpecialWithLease(IReadOnlyList<NtfsLookupItem> items,
        Func<NtfsLookupItem, CancellationToken, NtfsLeaseResult> reader, CancellationToken token = default, int limit = MaximumObjects)
        => ResolveCore(items, null, reader, token, limit, special: true);

    private static NtfsLookupBatch ResolveCore(IReadOnlyList<NtfsLookupItem> items,
        Func<string, CancellationToken, FileStorageInfo?>? reader,
        Func<NtfsLookupItem, CancellationToken, NtfsLeaseResult>? leaseReader, CancellationToken token, int limit, bool special = false)
    {
        token.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        int eligible = 0, calls = 0, resolved = 0, failed = 0, unsupported = 0;
        long nativeTicks = 0;
        NtfsLookupMetrics Metrics() => new(items.Count, eligible, calls, resolved, failed, unsupported,
            clock.Elapsed.TotalMilliseconds, nativeTicks * 1000.0 / Stopwatch.Frequency);
        NtfsLookupBatch Reject(string code) => new(false, code, [], Metrics());
        if (limit is < 1 or > MaximumObjects || items.Count > limit) return Reject("RecordLimit");
        var identities = new HashSet<(ulong, ulong)>();
        var slots = new HashSet<(ulong, ulong)>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            if (!identities.Add((item.Volume, item.Record.Reference))) return Reject("DuplicateObject");
            if (!slots.Add((item.Volume, item.Record.Reference & NtfsRecordDecoder.MaximumRecordNumber))) return Reject("SlotReused");
            if (!paths.Add(item.Path)) return Reject("PathCollision");
            if (item.Record.Names.Count > 256) return Reject("NameLimit");
        }
        var output = new List<NtfsResolvedSize>(items.Count);
        FileStorageInfo? Read(string path)
        {
            token.ThrowIfCancellationRequested();
            calls++;
            long started = Stopwatch.GetTimestamp();
            try { return reader!(path, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
            finally { nativeTicks += Stopwatch.GetTimestamp() - started; }
        }
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            var record = item.Record;
            long? logical = record.LogicalBytes is >= 0 ? record.LogicalBytes : null;
            long? allocation = record.AllocatedBytes is >= 0 ? record.AllocatedBytes : null;
            NtfsResolvedSize Result(long? occupied, NtfsSizeSource source, string code) =>
                new(item.Volume, record.Reference, logical, occupied, logical.HasValue ? NtfsSizeSource.Record : NtfsSizeSource.Unknown, source, code);
            int visibleNames = record.Names.Where(n => n.Namespace != 2).Select(n => (n.ParentReference, n.Name)).Distinct().Count();
            if (record.IsDirectory || record.BaseReference != 0 || (record.Reference >> 48) == 0 ||
                visibleNames == 0 || record.LinkCount != record.Names.Count || string.IsNullOrWhiteSpace(item.Path))
            {
                unsupported++; output.Add(Result(null, NtfsSizeSource.Unknown, "UnsupportedObject")); continue;
            }
            if (record.LookupReasons == NtfsLookupReason.None && logical.HasValue && allocation.HasValue)
            {
                output.Add(Result(allocation, NtfsSizeSource.Record, "RecordSizes")); continue;
            }
            bool eligibleReason = special ? record.LookupReasons is NtfsLookupReason.Compressed or NtfsLookupReason.Sparse
                : record.LookupReasons == NtfsLookupReason.ResidentAllocation;
            if (!eligibleReason || !logical.HasValue || allocation.HasValue ||
                (special && NtfsSpecialLease.Preflight(record, item.Path) != null))
            {
                unsupported++; output.Add(Result(null, NtfsSizeSource.Unknown, "UnsupportedLookupReason")); continue;
            }
            eligible++;
            string? Problem(FileStorageInfo? sample)
            {
                if (sample == null) return "ReadFailed";
                if (sample.Identity != new FileIdentity(item.Volume, record.Reference, 0)) return "IdentityChanged";
                if (sample.WritableHandleOpen) return "OpenWriter";
                if (sample.LogicalBytes != logical.Value) return "LogicalChanged";
                if (sample.AllocatedBytes < 0 || sample.Modified == null || sample.LinkCount != visibleNames) return "InvalidSnapshot";
                return null;
            }
            FileStorageInfo? first = null;
            string? problem;
            if (leaseReader != null)
            {
                calls++;
                long started = Stopwatch.GetTimestamp();
                NtfsLeaseResult? lease = null;
                try { lease = leaseReader(item, token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                finally { nativeTicks += Stopwatch.GetTimestamp() - started; }
                token.ThrowIfCancellationRequested();
                first = lease?.Storage;
                problem = lease is not { Success: true, HandlesOpened: 1, SnapshotReads: 2 }
                    ? "LeaseReadFailed" : Problem(first);
            }
            else
            {
                first = Read(item.Path);
                token.ThrowIfCancellationRequested();
                problem = Problem(first);
                if (problem == null)
                {
                    var second = Read(item.Path);
                    token.ThrowIfCancellationRequested();
                    problem = Problem(second);
                    if (problem == null && (first!.AllocatedBytes != second!.AllocatedBytes || first.Modified != second.Modified)) problem = "SnapshotChanged";
                }
            }
            if (problem != null)
            {
                failed++; output.Add(Result(null, NtfsSizeSource.Unknown, problem));
            }
            else
            {
                resolved++; output.Add(Result(first!.AllocatedBytes, NtfsSizeSource.HandleLookup, special ? "SpecialLookup" : "ResidentLookup"));
            }
        }
        token.ThrowIfCancellationRequested();
        bool complete = output.Count > 0 && failed == 0 && unsupported == 0 &&
            output.All(item => item.LogicalBytes.HasValue && item.AllocatedBytes.HasValue);
        return new(complete, complete ? "ResolvedBatch" : "IncompleteBatch", output.AsReadOnly(), Metrics());
    }
}
