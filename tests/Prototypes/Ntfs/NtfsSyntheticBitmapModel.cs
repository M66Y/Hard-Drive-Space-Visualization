using System.Diagnostics.CodeAnalysis;

namespace DiskAtlas.NtfsPrototype;

internal enum NtfsSyntheticBitmapPresence { Missing, Provided }
internal enum NtfsSyntheticAllocationState { Allocated, Unallocated, Unknown }
internal sealed record NtfsSyntheticBitmapBinding(object SourceToken, object MapEpoch, int Slots);
internal sealed record NtfsSyntheticBitmapInput(NtfsSyntheticBitmapBinding Binding,
    NtfsSyntheticBitmapPresence Presence, int CoveredSlots, ReadOnlyMemory<byte> Bytes);
internal sealed record NtfsSyntheticSlotObservation(int Slot, NtfsDecodeStatus Status, bool? HeaderInUse, bool AllZero = false);
internal sealed record NtfsSyntheticBitmapCrossCount(string Allocation, string Status, int Count);
internal sealed record NtfsSyntheticBitmapReport(string Revision, string EvidenceKind, int Slots, int CoveredSlots,
    bool BitmapCoverageComplete, int Allocated, int Unallocated, int Unknown, int Decoded, int Free, int Invalid, int Unsupported,
    int HeaderCompared, int HeaderDisagreements, int AllZeroInvalid, int AllZeroAllocated, int AllZeroUnallocated,
    int AllZeroUnknown, IReadOnlyList<NtfsSyntheticBitmapCrossCount> CrossCounts);
internal sealed class NtfsSyntheticBitmapException(string code) : IOException(code)
{
    internal string Code { get; } = code;
}

// Pure synthetic protocol only. The tokens are in-memory simulation identities,
// not a validated volume, snapshot, bitmap attribute or reading lease. LSB-first
// is this model's byte encoding and does not certify any real NTFS extraction.
internal static class NtfsSyntheticBitmapModel
{
    internal const int MaximumSlots = 4096;
    internal const int MaximumBitmapBytes = 512;

    private static void Require([DoesNotReturnIf(false)] bool condition, string code)
    { if (!condition) throw new NtfsSyntheticBitmapException(code); }

    private static T ReadExternal<T>(Func<T> access, CancellationToken token)
    {
        T value;
        try { value = access(); }
        catch (Exception)
        {
            // External accessors can impersonate either of our exception types.
            // Only the supplied cancellation token and our own fixed codes count.
            token.ThrowIfCancellationRequested();
            throw new NtfsSyntheticBitmapException("BitmapModelInput");
        }
        token.ThrowIfCancellationRequested();
        return value;
    }

    internal static NtfsSyntheticBitmapReport Evaluate(NtfsSyntheticBitmapBinding expectedBinding,
        NtfsSyntheticBitmapBinding observationBinding, IReadOnlyList<NtfsSyntheticSlotObservation> observations,
        NtfsSyntheticBitmapInput bitmap, CancellationToken token = default)
    {
        try { return EvaluateCore(expectedBinding, observationBinding, observations, bitmap, token); }
        catch (OperationCanceledException) { throw; }
        catch (NtfsSyntheticBitmapException) { throw; }
        catch (Exception)
        {
            // A failing external memory/list accessor must not publish private
            // messages. A cancellation triggered by that accessor takes priority.
            token.ThrowIfCancellationRequested();
            throw new NtfsSyntheticBitmapException("BitmapModelInput");
        }
    }

    private static NtfsSyntheticBitmapReport EvaluateCore(NtfsSyntheticBitmapBinding expectedBinding,
        NtfsSyntheticBitmapBinding observationBinding, IReadOnlyList<NtfsSyntheticSlotObservation> observations,
        NtfsSyntheticBitmapInput bitmap, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Require(expectedBinding != null && observationBinding != null && observations != null && bitmap != null,
            "BitmapModelInput");
        Require(expectedBinding.Slots is >= 1 and <= MaximumSlots, "BitmapModelSlotBudget");
        Require(expectedBinding.SourceToken != null && expectedBinding.MapEpoch != null, "BitmapModelBinding");
        bool SameBinding(NtfsSyntheticBitmapBinding? candidate) => candidate != null &&
            ReferenceEquals(expectedBinding.SourceToken, candidate.SourceToken) &&
            ReferenceEquals(expectedBinding.MapEpoch, candidate.MapEpoch) && candidate.Slots == expectedBinding.Slots;
        Require(SameBinding(observationBinding) && SameBinding(bitmap.Binding), "BitmapModelBinding");
        int slots = expectedBinding.Slots;
        int byteLength = bitmap.Bytes.Length;
        Require(byteLength is >= 0 and <= MaximumBitmapBytes, "BitmapModelByteBudget");
        Require(bitmap.Presence is NtfsSyntheticBitmapPresence.Missing or NtfsSyntheticBitmapPresence.Provided, "BitmapModelPresence");
        if (bitmap.Presence == NtfsSyntheticBitmapPresence.Missing)
            Require(bitmap.CoveredSlots == 0 && byteLength == 0, "BitmapModelCoverage");
        else
            Require(bitmap.CoveredSlots >= 1 && bitmap.CoveredSlots <= slots &&
                byteLength == (bitmap.CoveredSlots + 7) / 8, "BitmapModelCoverage");
        int observationCount = ReadExternal(() => observations.Count, token);
        Require(observationCount == slots && observationCount is >= 1 and <= MaximumSlots, "BitmapModelObservationBudget");

        // All declared budgets and shapes are checked before either copy. An
        // explicit partial coverage is distinct from a truncated Provided input.
        byte[] privateBits = ReadExternal(() => bitmap.Bytes.ToArray(), token);
        var privateObservations = new NtfsSyntheticSlotObservation[slots];
        var seen = new bool[slots];
        for (int i = 0; i < slots; i++)
        {
            token.ThrowIfCancellationRequested();
            var observation = ReadExternal(() => observations[i], token);
            Require(observation != null, "BitmapModelObservationShape");
            Require(observation.Slot >= 0 && observation.Slot < slots && !seen[observation.Slot], "BitmapModelObservationSlot");
            Require(observation.Status is NtfsDecodeStatus.Decoded or NtfsDecodeStatus.Free or NtfsDecodeStatus.Invalid or NtfsDecodeStatus.Unsupported,
                "BitmapModelDecodeStatus");
            // These are only caller-supplied synthetic observations. Full zero
            // bytes have no verified header, and cannot claim a Free decoder result.
            Require(!observation.AllZero || observation.Status == NtfsDecodeStatus.Invalid && !observation.HeaderInUse.HasValue,
                "BitmapModelObservationShape");
            Require(!observation.HeaderInUse.HasValue ||
                observation.Status != NtfsDecodeStatus.Decoded && observation.Status != NtfsDecodeStatus.Free ||
                observation.Status == NtfsDecodeStatus.Decoded && observation.HeaderInUse == true ||
                observation.Status == NtfsDecodeStatus.Free && observation.HeaderInUse == false,
                "BitmapModelObservationShape");
            seen[observation.Slot] = true;
            privateObservations[i] = observation;
        }

        int[] allocations = new int[3], statuses = new int[4], cross = new int[12], zero = new int[3];
        int headerCompared = 0, headerDisagreements = 0, allZeroInvalid = 0;
        foreach (var observation in privateObservations)
        {
            token.ThrowIfCancellationRequested();
            var allocation = NtfsSyntheticAllocationState.Unknown;
            if (bitmap.Presence == NtfsSyntheticBitmapPresence.Provided && observation.Slot < bitmap.CoveredSlots)
                allocation = (privateBits[observation.Slot / 8] & (1 << (observation.Slot % 8))) != 0
                    ? NtfsSyntheticAllocationState.Allocated : NtfsSyntheticAllocationState.Unallocated;
            int a = (int)allocation, s = (int)observation.Status;
            allocations[a]++; statuses[s]++; cross[a * 4 + s]++;
            if (allocation != NtfsSyntheticAllocationState.Unknown && observation.HeaderInUse.HasValue)
            {
                headerCompared++;
                if ((allocation == NtfsSyntheticAllocationState.Allocated) != observation.HeaderInUse.Value) headerDisagreements++;
            }
            if (observation.AllZero) { allZeroInvalid++; zero[a]++; }
        }
        Require(allocations.Sum() == slots && statuses.Sum() == slots && cross.Sum() == slots &&
            zero.Sum() == allZeroInvalid && allZeroInvalid <= statuses[(int)NtfsDecodeStatus.Invalid] &&
            headerDisagreements <= headerCompared, "BitmapModelAggregation");
        var rows = new List<NtfsSyntheticBitmapCrossCount>(12);
        foreach (var allocation in Enum.GetValues<NtfsSyntheticAllocationState>())
            foreach (var status in Enum.GetValues<NtfsDecodeStatus>())
                rows.Add(new(allocation.ToString(), status.ToString(), cross[(int)allocation * 4 + (int)status]));
        token.ThrowIfCancellationRequested();
        return new("synthetic-mft-bitmap-v1", "Synthetic", slots, bitmap.CoveredSlots,
            bitmap.Presence == NtfsSyntheticBitmapPresence.Provided && bitmap.CoveredSlots == slots,
            allocations[0], allocations[1], allocations[2], statuses[0], statuses[1], statuses[2], statuses[3],
            headerCompared, headerDisagreements, allZeroInvalid, zero[0], zero[1], zero[2], rows.AsReadOnly());
    }
}
