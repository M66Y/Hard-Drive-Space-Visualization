namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsLeaseTiming(int Round, string Order, double DoubleMilliseconds, double LeaseMilliseconds,
    double DoubleNativeMilliseconds, double LeaseNativeMilliseconds);
internal sealed record NtfsLeaseExperimentReport(int Objects, int ResidentObjects, int DirectObjects, int UnsupportedExcluded,
    int DoubleReadCalls, int LeaseCalls, int LeaseHandles, int LeaseSnapshots, IReadOnlyList<NtfsLeaseTiming> Samples);

// Warm-cache micro-experiment over already decoded synthetic objects. This does
// not time MFT enumeration, a complete scanner, or physical I/O. Counts describe
// one verified batch; reference reads and warm-up runs are excluded.
internal static class NtfsLeaseExperiment
{
    private sealed record LeaseRun(NtfsLookupBatch Batch, int Calls, int Handles, int Snapshots, bool AllSuccessful);

    internal static NtfsLeaseExperimentReport Run(IReadOnlyList<NtfsLookupItem> all, Action<bool, string> check)
    {
        void Require(bool condition, string code)
        {
            check(condition, code);
            if (!condition) throw new InvalidOperationException(code);
        }
        Require(all.Count <= NtfsSizeLookup.MaximumObjects, "LeaseExperimentInputBound");
        var candidates = all.Where(item => item.Record.LookupReasons is NtfsLookupReason.None or NtfsLookupReason.ResidentAllocation).ToArray();
        Require(candidates.Length is > 0 and <= NtfsSizeLookup.MaximumObjects, "LeaseExperimentBound");
        int resident = candidates.Count(item => item.Record.LookupReasons == NtfsLookupReason.ResidentAllocation);
        FileStorageInfo? Read(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return NativeStorage.TryRead(path, false, File.GetAttributes(path), out var storage, out _) ? storage : null;
        }
        var reference = new Dictionary<(ulong, ulong), (long Logical, long Allocated)>();
        foreach (var item in candidates)
        {
            var storage = Read(item.Path, default);
            Require(storage != null && storage.Identity == new FileIdentity(item.Volume, item.Record.Reference, 0) &&
                storage.LogicalBytes >= 0 && storage.AllocatedBytes >= 0 && !storage.WritableHandleOpen,
                "LeaseReferenceIdentity");
            Require(reference.TryAdd((item.Volume, item.Record.Reference), (storage!.LogicalBytes, storage.AllocatedBytes)),
                "LeaseReferenceUnique");
        }
        LeaseRun RunLease()
        {
            int calls = 0, handles = 0, snapshots = 0;
            bool successful = true;
            var batch = NtfsSizeLookup.ResolveWithLease(candidates, (item, token) =>
            {
                calls++;
                var result = NtfsResidentLease.Read(item.Volume, item.Record, item.Path, token);
                handles += result.HandlesOpened;
                snapshots += result.SnapshotReads;
                successful &= result.Success && result.HandlesOpened == 1 && result.SnapshotReads == 2;
                return result;
            });
            return new(batch, calls, handles, snapshots, successful);
        }
        void Validate(NtfsLookupBatch batch, bool lease)
        {
            Require(batch.Complete && batch.Items.Count == candidates.Length && batch.Metrics.Objects == candidates.Length,
                "LeaseBatchComplete");
            foreach (var item in batch.Items)
            {
                Require(reference.TryGetValue((item.Volume, item.Reference), out var expected) &&
                    item.LogicalBytes == expected.Logical && item.AllocatedBytes == expected.Allocated,
                    "LeaseSizesMatchReference");
            }
            Require(batch.Metrics.EligibleObjects == resident && batch.Metrics.ResolvedObjects == resident &&
                batch.Metrics.FailedObjects == 0 && batch.Metrics.UnsupportedObjects == 0, "LeaseNoHiddenFailures");
            Require(batch.Metrics.NativeCalls == resident * (lease ? 1 : 2), "LeaseCallBudget");
            Require(double.IsFinite(batch.Metrics.ElapsedMilliseconds) && batch.Metrics.ElapsedMilliseconds >= 0 &&
                double.IsFinite(batch.Metrics.NativeMilliseconds) && batch.Metrics.NativeMilliseconds >= 0 &&
                batch.Metrics.NativeMilliseconds <= batch.Metrics.ElapsedMilliseconds, "LeaseTimingValid");
        }
        void ValidateLease(LeaseRun run)
        {
            Validate(run.Batch, true);
            Require(run.AllSuccessful && run.Calls == resident && run.Handles == resident && run.Snapshots == resident * 2 &&
                run.Batch.Metrics.NativeCalls == run.Calls, "LeaseObservedWorkBudget");
        }
        // Each path is warmed once. All correctness checks happen after Resolve's timer stops.
        Validate(NtfsSizeLookup.Resolve(candidates, Read), false);
        ValidateLease(RunLease());
        var samples = new List<NtfsLeaseTiming>(6);
        NtfsLookupBatch? lastDouble = null;
        LeaseRun? lastLease = null;
        for (int round = 0; round < 6; round++)
        {
            NtfsLookupBatch doubleRead;
            LeaseRun lease;
            if (round % 2 == 0)
            { doubleRead = NtfsSizeLookup.Resolve(candidates, Read); lease = RunLease(); }
            else
            { lease = RunLease(); doubleRead = NtfsSizeLookup.Resolve(candidates, Read); }
            Validate(doubleRead, false);
            ValidateLease(lease);
            samples.Add(new(round + 1, round % 2 == 0 ? "double-first" : "lease-first",
                doubleRead.Metrics.ElapsedMilliseconds, lease.Batch.Metrics.ElapsedMilliseconds,
                doubleRead.Metrics.NativeMilliseconds, lease.Batch.Metrics.NativeMilliseconds));
            lastDouble = doubleRead;
            lastLease = lease;
        }
        return new(candidates.Length, resident, candidates.Length - resident, all.Count - candidates.Length,
            lastDouble!.Metrics.NativeCalls, lastLease!.Calls, lastLease.Handles, lastLease.Snapshots, samples.AsReadOnly());
    }
}
