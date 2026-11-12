namespace DiskAtlas.NtfsPrototype;

// Pure aggregate-model tests. No record bytes, paths, Native calls or real
// source tokens are constructed; bit encoding below belongs to this model only.
internal static class NtfsSyntheticBitmapModelTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Synthetic bitmap check failed: " + name);
            passed++; Console.WriteLine("PASS synthetic bitmap " + name);
        }
        void Rejected(Action action, string code, string name)
        {
            bool rejected = false;
            try { action(); }
            catch (NtfsSyntheticBitmapException error) { rejected = error.Code == code && error.Message == code && error.InnerException == null; }
            Check(rejected, name);
        }
        var binding = Binding(12);
        var observations = Observations(12);
        byte[] bits = [0x0f];
        var input = new NtfsSyntheticBitmapInput(binding, NtfsSyntheticBitmapPresence.Provided, 8, bits);
        var result = NtfsSyntheticBitmapModel.Evaluate(binding, binding, observations, input);
        Check(result is { Revision: "synthetic-mft-bitmap-v1", EvidenceKind: "Synthetic", Slots: 12, CoveredSlots: 8,
            BitmapCoverageComplete: false, Allocated: 4, Unallocated: 4, Unknown: 4 },
            "explicit partial coverage preserves three separate allocation declarations");
        Check(result is { Decoded: 3, Free: 3, Invalid: 3, Unsupported: 3, HeaderCompared: 4, HeaderDisagreements: 2 },
            "header disagreements are aggregate observations without rewriting any strict category");
        Check(result.CrossCounts.Count == 12 && result.CrossCounts.All(row => row.Count == 1) &&
            result.CrossCounts.Sum(row => row.Count) == result.Slots,
            "all twelve allocation and decode combinations are independently conserved");
        Check(result.CrossCounts.Select(row => row.Allocation).Distinct().SequenceEqual(new[] { "Allocated", "Unallocated", "Unknown" }) &&
            result.CrossCounts.Take(4).Select(row => row.Status).SequenceEqual(new[] { "Decoded", "Free", "Invalid", "Unsupported" }),
            "cross counts have fixed allocation and decoder enum order including zero buckets");
        var missing = NtfsSyntheticBitmapModel.Evaluate(binding, binding, observations,
            new(binding, NtfsSyntheticBitmapPresence.Missing, 0, ReadOnlyMemory<byte>.Empty));
        Check(missing is { Allocated: 0, Unallocated: 0, Unknown: 12, HeaderCompared: 0, HeaderDisagreements: 0,
            BitmapCoverageComplete: false } && missing.Decoded == result.Decoded && missing.Free == result.Free &&
            missing.Invalid == result.Invalid && missing.Unsupported == result.Unsupported,
            "missing bitmap never converts unknown declarations into unallocated or changes decode results");
        var zeroBinding = Binding(3);
        NtfsSyntheticSlotObservation[] zeroObservations = [
            new(0, NtfsDecodeStatus.Invalid, null, true), new(1, NtfsDecodeStatus.Invalid, null, true),
            new(2, NtfsDecodeStatus.Invalid, null, true)];
        var zeros = NtfsSyntheticBitmapModel.Evaluate(zeroBinding, zeroBinding, zeroObservations,
            new(zeroBinding, NtfsSyntheticBitmapPresence.Provided, 2, new byte[] { 1 }));
        Check(zeros is { Invalid: 3, Free: 0, AllZeroInvalid: 3, AllZeroAllocated: 1, AllZeroUnallocated: 1, AllZeroUnknown: 1,
            Allocated: 1, Unallocated: 1, Unknown: 1, HeaderCompared: 0 },
            "all-zero declarations can be allocated unallocated or unknown while remaining strictly invalid");
        var fullBinding = Binding(9);
        var fullObservations = Observations(9);
        var full = NtfsSyntheticBitmapModel.Evaluate(fullBinding, fullBinding, fullObservations,
            new(fullBinding, NtfsSyntheticBitmapPresence.Provided, 9, new byte[] { 0x80, 0x01 }));
        Check(full is { BitmapCoverageComplete: true, Allocated: 2, Unallocated: 7, Unknown: 0 },
            "LSB-first model encoding crosses the seven and eight slot boundary");
        var padding = NtfsSyntheticBitmapModel.Evaluate(fullBinding, fullBinding, fullObservations,
            new(fullBinding, NtfsSyntheticBitmapPresence.Provided, 9, new byte[] { 0x80, 0xff }));
        Check(SameAggregates(full, padding), "unused high padding bits never create extra known slots or counts");
        var partialPadding = NtfsSyntheticBitmapModel.Evaluate(fullBinding, fullBinding, fullObservations,
            new(fullBinding, NtfsSyntheticBitmapPresence.Provided, 7, new byte[] { 0x80 }));
        Check(partialPadding is { Allocated: 0, Unallocated: 7, Unknown: 2 },
            "padding set bits beyond declared coverage do not remove unknown state");
        var maximumBinding = Binding(4096);
        var maximumObservations = Observations(4096);
        byte[] maximumBits = new byte[512]; maximumBits[^1] = 0x80;
        var maximum = NtfsSyntheticBitmapModel.Evaluate(maximumBinding, maximumBinding, maximumObservations,
            new(maximumBinding, NtfsSyntheticBitmapPresence.Provided, 4096, maximumBits));
        Check(maximum is { Slots: 4096, Allocated: 1, Unallocated: 4095, Unknown: 0, BitmapCoverageComplete: true } &&
            maximum.CrossCounts.Sum(row => row.Count) == 4096,
            "the exact slot and byte budgets retain the final four-thousand-ninety-fifth bit");
        var one = Binding(1);
        var single = NtfsSyntheticBitmapModel.Evaluate(one, one, Observations(1),
            new(one, NtfsSyntheticBitmapPresence.Provided, 1, new byte[] { 0 }));
        Check(single is { Slots: 1, Decoded: 1, Unallocated: 1, HeaderDisagreements: 1 } &&
            single.CrossCounts.Count == 12 && single.CrossCounts.Count(row => row.Count == 0) == 11,
            "the smallest valid range still publishes all twelve aggregate buckets");
        var shuffled = NtfsSyntheticBitmapModel.Evaluate(binding, binding, observations.Reverse().ToArray(), input);
        Check(SameAggregates(result, shuffled), "complete unique observations may arrive in any order");
        var sameIdentity = new NtfsSyntheticBitmapBinding(binding.SourceToken, binding.MapEpoch, binding.Slots);
        Check(SameAggregates(result, NtfsSyntheticBitmapModel.Evaluate(binding, sameIdentity, observations,
            input with { Binding = sameIdentity })), "a new binding container preserves the same exact simulated source and epoch objects");
        byte[] originalBits = bits.ToArray(); var originalObservations = observations.ToArray();
        NtfsSyntheticBitmapModel.Evaluate(binding, binding, observations, input);
        Check(bits.SequenceEqual(originalBits) && observations.SequenceEqual(originalObservations),
            "evaluation never changes caller bitmap or observation arrays");
        bits[0] = 0xff; observations[0] = new(0, NtfsDecodeStatus.Invalid, null);
        Check(result.Allocated == 4 && result.Decoded == 3 && result.CrossCounts.All(row => row.Count == 1),
            "later caller input changes cannot alter a published aggregate result");
        bool immutable = false;
        try { ((IList<NtfsSyntheticBitmapCrossCount>)result.CrossCounts).Clear(); } catch (NotSupportedException) { immutable = true; }
        Check(immutable, "published cross counts are immutable and retain neither input arrays nor tokens");
        string[] fields = typeof(NtfsSyntheticBitmapReport).GetProperties(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Select(property => property.Name).ToArray();
        Check(fields.All(field => field is not "Binding" and not "SourceToken" and not "MapEpoch" and not "Bytes" and not "Observations" and not "Slot"),
            "result schema has no bitmap tokens source identities or per-slot observations");

        foreach (int badSlots in new[] { -1, 0, 4097, int.MaxValue })
        {
            var bad = Binding(badSlots);
            Rejected(() => NtfsSyntheticBitmapModel.Evaluate(bad, bad, [], new(bad, NtfsSyntheticBitmapPresence.Missing, 0, default)),
                "BitmapModelSlotBudget", "invalid slot budgets are rejected before input copies");
        }
        foreach (var badInput in new[] {
            new NtfsSyntheticBitmapInput(binding, NtfsSyntheticBitmapPresence.Provided, 8, ReadOnlyMemory<byte>.Empty),
            new NtfsSyntheticBitmapInput(binding, NtfsSyntheticBitmapPresence.Provided, 8, new byte[2]),
            new NtfsSyntheticBitmapInput(binding, NtfsSyntheticBitmapPresence.Provided, 13, new byte[2]),
            new NtfsSyntheticBitmapInput(binding, NtfsSyntheticBitmapPresence.Provided, 0, ReadOnlyMemory<byte>.Empty),
            new NtfsSyntheticBitmapInput(binding, NtfsSyntheticBitmapPresence.Missing, 1, ReadOnlyMemory<byte>.Empty),
            new NtfsSyntheticBitmapInput(binding, NtfsSyntheticBitmapPresence.Missing, 0, new byte[1]) })
            Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, Observations(12), badInput),
                "BitmapModelCoverage", "truncated excess or contradictory coverage is not intentional partial coverage");
        Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, Observations(12), input with { Bytes = new byte[513] }),
            "BitmapModelByteBudget", "oversized bitmap input cannot expand the declared model budget");
        Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, Observations(12), input with { Presence = (NtfsSyntheticBitmapPresence)99 }),
            "BitmapModelPresence", "unknown bitmap presence enum values are rejected");
        var foreignSource = binding with { SourceToken = new object() };
        var foreignMap = binding with { MapEpoch = new object() };
        foreach (var foreign in new[] { foreignSource, foreignMap, binding with { Slots = 11 } })
        {
            Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, foreign, Observations(12), input),
                "BitmapModelBinding", "observation source map or range mismatch is rejected");
            Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, Observations(12), input with { Binding = foreign }),
                "BitmapModelBinding", "bitmap source map or range mismatch is rejected");
        }
        Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, Observations(11), input),
            "BitmapModelObservationBudget", "missing observations cannot be silently filled with unknown or free");
        Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, Observations(13), input),
            "BitmapModelObservationBudget", "excess observations cannot expand a declared range");
        foreach (var badObservation in new NtfsSyntheticSlotObservation?[] {
            null, new(0, NtfsDecodeStatus.Free, false, true), new(0, NtfsDecodeStatus.Invalid, false, true),
            new(0, NtfsDecodeStatus.Decoded, false), new(0, NtfsDecodeStatus.Free, true) })
        {
            var altered = Observations(12); altered[0] = badObservation!;
            Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, altered, input),
                "BitmapModelObservationShape", "self-contradictory zero or header observations are rejected without inference");
        }
        foreach (int slot in new[] { -1, 1, 12, int.MaxValue })
        {
            var altered = Observations(12); altered[0] = altered[0] with { Slot = slot };
            Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, altered, input),
                "BitmapModelObservationSlot", "negative duplicate missing or overflowing slot identities are rejected");
        }
        var badStatus = Observations(12); badStatus[0] = badStatus[0] with { Status = (NtfsDecodeStatus)99 };
        Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, badStatus, input), "BitmapModelDecodeStatus",
            "unknown strict classification enum values cannot enter cross counts");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool canceled = false;
        try { NtfsSyntheticBitmapModel.Evaluate(binding, binding, Observations(12), input, cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "pre-cancellation publishes no aggregate result");
        return passed;
    }

    private static NtfsSyntheticBitmapBinding Binding(int slots) => new(new object(), new object(), slots);
    private static NtfsSyntheticSlotObservation[] Observations(int slots) => Enumerable.Range(0, slots)
        .Select(slot => new NtfsSyntheticSlotObservation(slot, (NtfsDecodeStatus)(slot % 4), (slot % 4) switch { 0 => true, 1 => false, _ => (bool?)null }))
        .ToArray();
    private static bool SameAggregates(NtfsSyntheticBitmapReport first, NtfsSyntheticBitmapReport second) =>
        (first with { CrossCounts = second.CrossCounts }) == second && first.CrossCounts.SequenceEqual(second.CrossCounts);
}
