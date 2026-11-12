using System.Diagnostics;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsSpecialCostSample(int Round, string Order, double ReferenceMilliseconds, double LeaseMilliseconds);
internal sealed record NtfsSpecialCostReport(int Objects, int ReferenceCalls, int LeaseCalls, int HandlesOpened,
    int SnapshotReads, IReadOnlyList<NtfsSpecialCostSample> Samples);

// Warm-cache cost observation over four already decoded, known fixture objects.
// The reference is today's one-snapshot NativeStorage read; the lease applies
// stricter identity/metadata stability checks. Their guarantees are not equivalent.
// These intervals exclude MFT acquisition, creation, indexing, UI, external result verification and
// warm-up. Callback/handle/group counts are not Win32-call or physical-I/O counts.
internal static class NtfsSpecialCostExperiment
{
    private sealed record ReferenceRun(FileStorageInfo?[] Values, int Calls, double Milliseconds);
    private sealed record LeaseRun(NtfsLookupBatch Batch, IReadOnlyList<(NtfsLookupItem Item, NtfsLeaseResult Result)> Raw,
        int Calls, int Handles, int Snapshots, bool AllSuccessful, double Milliseconds);

    internal static NtfsSpecialCostReport Run(IReadOnlyList<NtfsLookupItem> items, Action<bool, string> check)
    {
        void Require(bool condition, string code)
        {
            check(condition, code);
            if (!condition) throw new InvalidOperationException(code);
        }
        Require(items.Count <= NtfsSizeLookup.MaximumObjects, "SpecialCostInputBound");
        var candidates = items.Where(item => item.Record.LookupReasons is NtfsLookupReason.Compressed or NtfsLookupReason.Sparse).ToArray();
        Require(candidates.Length == 4, "SpecialCostFixtureBound");
        var expected = new Dictionary<(ulong Volume, ulong Reference), (long Logical, long Allocated)>();

        FileStorageInfo? Read(NtfsLookupItem item) =>
            NativeStorage.TryRead(item.Path, false, FileAttributes.Normal, out var storage, out _) ? storage : null;
        bool Bound(NtfsLookupItem input, FileStorageInfo? observed)
        {
            int links = input.Record.Names.Where(name => name.Namespace != 2)
                .Select(name => (name.ParentReference, name.Name)).Distinct().Count();
            return observed != null && observed.Identity == new FileIdentity(input.Volume, input.Record.Reference, 0) &&
                observed.LogicalBytes == input.Record.LogicalBytes && observed.LogicalBytes >= 0 && observed.AllocatedBytes >= 0 &&
                !observed.WritableHandleOpen && observed.Modified.HasValue && observed.LinkCount == links;
        }
        foreach (var input in candidates)
        {
            Require(NtfsSpecialLease.Preflight(input.Record, input.Path) == null, "SpecialCostEligibleInput");
            var native = Read(input);
            Require(Bound(input, native), "SpecialCostInitialReference");
            Require(expected.TryAdd((input.Volume, input.Record.Reference), (native!.LogicalBytes, native.AllocatedBytes)),
                "SpecialCostUniqueObject");
        }

        ReferenceRun RunReference()
        {
            var clock = Stopwatch.StartNew();
            int calls = 0;
            var values = new FileStorageInfo?[candidates.Length];
            for (int i = 0; i < candidates.Length; i++)
            {
                calls++;
                values[i] = Read(candidates[i]);
            }
            clock.Stop();
            return new(values, calls, clock.Elapsed.TotalMilliseconds);
        }
        LeaseRun RunLease()
        {
            var clock = Stopwatch.StartNew();
            int calls = 0, handles = 0, snapshots = 0;
            bool allSuccessful = true;
            var raw = new List<(NtfsLookupItem, NtfsLeaseResult)>(candidates.Length);
            var batch = NtfsSizeLookup.ResolveSpecialWithLease(candidates, (item, token) =>
            {
                calls++;
                var result = NtfsSpecialLease.Read(item.Volume, item.Record, item.Path, token);
                handles = checked(handles + result.HandlesOpened);
                snapshots = checked(snapshots + result.SnapshotReads);
                allSuccessful &= result.Success;
                raw.Add((item, result));
                return result;
            });
            clock.Stop();
            return new(batch, raw.AsReadOnly(), calls, handles, snapshots, allSuccessful, clock.Elapsed.TotalMilliseconds);
        }
        bool ValidTime(double value) => double.IsFinite(value) && value >= 0;
        void ValidateReference(ReferenceRun run)
        {
            Require(run.Calls == candidates.Length && run.Values.Length == candidates.Length, "SpecialCostReferenceBudget");
            for (int i = 0; i < candidates.Length; i++)
            {
                var input = candidates[i];
                var native = run.Values[i];
                var value = expected[(input.Volume, input.Record.Reference)];
                Require(Bound(input, native) && native!.LogicalBytes == value.Logical && native.AllocatedBytes == value.Allocated,
                    "SpecialCostReferenceValues");
            }
            Require(ValidTime(run.Milliseconds), "SpecialCostReferenceTime");
        }
        void ValidateLease(LeaseRun run)
        {
            Require(run.Batch.Complete && run.Batch.Code == "ResolvedBatch" && run.Batch.Items.Count == candidates.Length &&
                run.Batch.Metrics.Objects == candidates.Length && run.Batch.Metrics.EligibleObjects == candidates.Length &&
                run.Batch.Metrics.ResolvedObjects == candidates.Length && run.Batch.Metrics.FailedObjects == 0 &&
                run.Batch.Metrics.UnsupportedObjects == 0, "SpecialCostLeaseComplete");
            Require(run.AllSuccessful && run.Calls == candidates.Length && run.Handles == candidates.Length &&
                run.Snapshots == candidates.Length * 2 && run.Raw.Count == candidates.Length &&
                run.Batch.Metrics.NativeCalls == candidates.Length, "SpecialCostLeaseWorkBudget");
            foreach (var (input, result) in run.Raw)
            {
                var value = expected[(input.Volume, input.Record.Reference)];
                Require(result.Success && result.HandlesOpened == 1 && result.SnapshotReads == 2 && Bound(input, result.Storage) &&
                    result.Storage!.LogicalBytes == value.Logical && result.Storage.AllocatedBytes == value.Allocated,
                    "SpecialCostNativeLeaseValues");
            }
            Require(run.Batch.Items.Select(item => (item.Volume, item.Reference)).Distinct().Count() == candidates.Length,
                "SpecialCostLeaseUniqueOutput");
            foreach (var output in run.Batch.Items)
            {
                Require(expected.TryGetValue((output.Volume, output.Reference), out var value) &&
                    output.LogicalBytes == value.Logical && output.AllocatedBytes == value.Allocated &&
                    output.LogicalSource == NtfsSizeSource.Record && output.AllocatedSource == NtfsSizeSource.HandleLookup &&
                    output.Code == "SpecialLookup", "SpecialCostPublishedValues");
            }
            Require(ValidTime(run.Milliseconds), "SpecialCostLeaseTime");
        }

        // Pre-read reference acquisition above and one warm-up per strategy are
        // outside the six timed rounds and their reported per-batch counts.
        ValidateReference(RunReference());
        ValidateLease(RunLease());
        var samples = new List<NtfsSpecialCostSample>(6);
        ReferenceRun? lastReference = null;
        LeaseRun? lastLease = null;
        for (int round = 0; round < 6; round++)
        {
            ReferenceRun reference;
            LeaseRun lease;
            if (round % 2 == 0) { reference = RunReference(); lease = RunLease(); }
            else { lease = RunLease(); reference = RunReference(); }
            ValidateReference(reference);
            ValidateLease(lease);
            samples.Add(new(round + 1, round % 2 == 0 ? "reference-first" : "lease-first", reference.Milliseconds, lease.Milliseconds));
            lastReference = reference;
            lastLease = lease;
        }
        return new(candidates.Length, lastReference!.Calls, lastLease!.Calls, lastLease.Handles, lastLease.Snapshots,
            samples.AsReadOnly());
    }
}
