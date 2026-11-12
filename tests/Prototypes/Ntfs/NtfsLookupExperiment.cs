using System.Diagnostics;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsLookupTiming(int Round, string Order, double SelectiveMilliseconds, double AllHandleMilliseconds,
    double SelectiveNativeMilliseconds, double AllHandleNativeMilliseconds);
internal sealed record NtfsResidentExample(long LogicalBytes, long LookupAllocatedBytes);
internal sealed record NtfsLookupExperimentReport(int Objects, int ResidentObjects, int DirectObjects, int UnsupportedExcluded,
    int SelectiveNativeCalls, int AllHandleNativeCalls, IReadOnlyList<NtfsLookupTiming> Samples, IReadOnlyList<NtfsResidentExample> ResidentExamples);

// Micro-experiment on already decoded, known synthetic files. No MFT read,
// creation, or full scanner timing is included in the reported interval.
internal static class NtfsLookupExperiment
{
    internal static NtfsLookupExperimentReport Run(IReadOnlyList<NtfsLookupItem> all, Action<bool, string> check)
    {
        var candidates = all.Where(item => item.Record.LookupReasons is NtfsLookupReason.None or NtfsLookupReason.ResidentAllocation).ToArray();
        check(candidates.Length > 0 && candidates.Length <= NtfsSizeLookup.MaximumObjects, "LookupExperimentBound");
        FileStorageInfo? Read(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return NativeStorage.TryRead(path, false, File.GetAttributes(path), out var storage, out _) ? storage : null;
        }
        // This is a two-read stability-check control, NOT the existing scanner.
        var forceHandle = candidates.Select(item => item with
        {
            Record = item.Record with { AllocatedBytes = null, LookupReasons = NtfsLookupReason.ResidentAllocation }
        }).ToArray();
        var reference = new Dictionary<(ulong, ulong), (long Logical, long Allocated)>();
        foreach (var item in candidates)
        {
            var storage = Read(item.Path, default);
            check(storage != null && storage.Identity == new FileIdentity(item.Volume, item.Record.Reference, 0), "LookupReferenceIdentity");
            reference.Add((item.Volume, item.Record.Reference), (storage!.LogicalBytes, storage.AllocatedBytes));
        }
        void Validate(NtfsLookupBatch batch, bool selective)
        {
            check(batch.Complete && batch.Items.Count == candidates.Length, "LookupBatchComplete");
            foreach (var item in batch.Items)
            {
                var expected = reference[(item.Volume, item.Reference)];
                check(item.LogicalBytes == expected.Logical && item.AllocatedBytes == expected.Allocated, "LookupSizeMatchesReference");
            }
            check(batch.Metrics.FailedObjects == 0 && batch.Metrics.UnsupportedObjects == 0, "LookupNoHiddenFailures");
            check(batch.Metrics.NativeCalls == 2 * (selective ? batch.Metrics.EligibleObjects : candidates.Length), "LookupCallBudget");
        }
        // Warm each path once. Validation is deliberately outside timed Resolve.
        Validate(NtfsSizeLookup.Resolve(candidates, Read), true);
        Validate(NtfsSizeLookup.Resolve(forceHandle, Read), false);
        var samples = new List<NtfsLookupTiming>();
        NtfsLookupBatch? last = null;
        int allHandleCalls = 0;
        for (int round = 0; round < 6; round++)
        {
            NtfsLookupBatch selective, control;
            if (round % 2 == 0)
            { selective = NtfsSizeLookup.Resolve(candidates, Read); control = NtfsSizeLookup.Resolve(forceHandle, Read); }
            else
            { control = NtfsSizeLookup.Resolve(forceHandle, Read); selective = NtfsSizeLookup.Resolve(candidates, Read); }
            Validate(selective, true); Validate(control, false);
            samples.Add(new(round + 1, round % 2 == 0 ? "selective-first" : "handle-first",
                selective.Metrics.ElapsedMilliseconds, control.Metrics.ElapsedMilliseconds,
                selective.Metrics.NativeMilliseconds, control.Metrics.NativeMilliseconds));
            last = selective; allHandleCalls = control.Metrics.NativeCalls;
        }
        var examples = last!.Items.Where(item => item.AllocatedSource == NtfsSizeSource.HandleLookup)
            .Select(item => new NtfsResidentExample(item.LogicalBytes!.Value, item.AllocatedBytes!.Value)).Take(64).ToArray();
        return new(candidates.Length, last.Metrics.EligibleObjects, candidates.Length - last.Metrics.EligibleObjects,
            all.Count - candidates.Length, last.Metrics.NativeCalls, allHandleCalls, samples.AsReadOnly(), examples);
    }
}
