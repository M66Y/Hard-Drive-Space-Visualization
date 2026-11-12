using System.Buffers;
using System.Collections;
using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// Independent model counterexamples. Every token, bitmap and observation is
// synthetic; no filesystem, device, raw record, path or native call is used.
internal static class NtfsSyntheticBitmapAdversarialTests
{
    private sealed class EqualToken
    {
        public override bool Equals(object? other) => other is EqualToken;
        public override int GetHashCode() => 1;
    }

    private sealed class CallbackList(IReadOnlyList<NtfsSyntheticSlotObservation> values) : IReadOnlyList<NtfsSyntheticSlotObservation>
    {
        internal Action? OnCount;
        internal Action<int>? OnIndex;
        internal bool ThrowCount, ThrowIndex;
        internal Exception? CountError, IndexError;
        public int Count
        {
            get
            {
                OnCount?.Invoke();
                if (CountError != null) throw CountError;
                if (ThrowCount) throw new InvalidOperationException("SyntheticPrivateCount");
                return values.Count;
            }
        }
        public NtfsSyntheticSlotObservation this[int index]
        {
            get
            {
                OnIndex?.Invoke(index);
                if (IndexError != null) throw IndexError;
                if (ThrowIndex) throw new InvalidOperationException("SyntheticPrivateIndex");
                return values[index];
            }
        }
        public IEnumerator<NtfsSyntheticSlotObservation> GetEnumerator()
        { for (int index = 0; index < Count; index++) yield return this[index]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CallbackMemory(Exception error, Action? onAccess) : MemoryManager<byte>
    {
        internal ReadOnlyMemory<byte> View => CreateMemory(1);
        public override Span<byte> GetSpan() { onAccess?.Invoke(); throw error; }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException("SyntheticPin");
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Synthetic bitmap adversarial check failed: " + name);
            passed++; Console.WriteLine("PASS synthetic bitmap adversarial " + name);
        }
        Exception? Catch(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
        void Rejected(Action action, string code, string name) =>
            Check(Catch(action) is NtfsSyntheticBitmapException error && error.Code == code && error.Message == code && error.InnerException == null, name);
        NtfsSyntheticBitmapBinding Binding(int slots) => new(new object(), new object(), slots);
        NtfsSyntheticSlotObservation[] Observations(int count) => Enumerable.Range(0, count).Select(slot =>
        {
            var status = (NtfsDecodeStatus)(slot % 4);
            bool? inUse = status == NtfsDecodeStatus.Decoded ? true : status == NtfsDecodeStatus.Free ? false : null;
            return new NtfsSyntheticSlotObservation(slot, status, inUse, status == NtfsDecodeStatus.Invalid);
        }).ToArray();

        var binding = Binding(12); var observations = Observations(12); byte[] bits = [0x0f];
        var bitmap = new NtfsSyntheticBitmapInput(binding, NtfsSyntheticBitmapPresence.Provided, 8, bits);
        var report = NtfsSyntheticBitmapModel.Evaluate(binding, binding, observations, bitmap);
        Check(report is { Revision: "synthetic-mft-bitmap-v1", EvidenceKind: "Synthetic", Slots: 12, CoveredSlots: 8,
            BitmapCoverageComplete: false, Allocated: 4, Unallocated: 4, Unknown: 4, Decoded: 3, Free: 3, Invalid: 3, Unsupported: 3 },
            "deliberately partial evidence retains three allocation states and all four strict categories");
        Check(report.CrossCounts.Count == 12 && report.CrossCounts.All(row => row.Count == 1) && report.CrossCounts.Sum(row => row.Count) == 12,
            "the complete three-by-four matrix conserves observations without losing zero or unknown categories");
        Check(report is { AllZeroInvalid: 3, AllZeroAllocated: 1, AllZeroUnallocated: 1, AllZeroUnknown: 1, HeaderCompared: 4, HeaderDisagreements: 2 },
            "zero-byte observations do not imply allocation and header disagreements remain observations");
        Check(report.CrossCounts.Select(row => row.Allocation + "/" + row.Status).SequenceEqual(new[] {
            "Allocated/Decoded", "Allocated/Free", "Allocated/Invalid", "Allocated/Unsupported",
            "Unallocated/Decoded", "Unallocated/Free", "Unallocated/Invalid", "Unallocated/Unsupported",
            "Unknown/Decoded", "Unknown/Free", "Unknown/Invalid", "Unknown/Unsupported" }),
            "cross counts use a deterministic complete state and status order");
        var beforeBits = bits.ToArray(); var beforeObservations = observations.ToArray();
        var shuffled = NtfsSyntheticBitmapModel.Evaluate(binding, binding, observations.Reverse().ToArray(), bitmap);
        Check(JsonSerializer.Serialize(report) == JsonSerializer.Serialize(shuffled) && bits.SequenceEqual(beforeBits) && observations.SequenceEqual(beforeObservations),
            "observation order does not change the aggregate or mutate bitmap and observation inputs");
        bool immutable = false;
        try { ((IList<NtfsSyntheticBitmapCrossCount>)report.CrossCounts).Clear(); } catch (NotSupportedException) { immutable = true; }
        Check(immutable, "the result matrix cannot be edited after successful evaluation");

        var missing = NtfsSyntheticBitmapModel.Evaluate(binding, binding, observations,
            new(binding, NtfsSyntheticBitmapPresence.Missing, 0, ReadOnlyMemory<byte>.Empty));
        Check(missing is { Allocated: 0, Unallocated: 0, Unknown: 12, CoveredSlots: 0, BitmapCoverageComplete: false,
            AllZeroInvalid: 3, AllZeroUnknown: 3, HeaderCompared: 0, HeaderDisagreements: 0 } && missing.CrossCounts.Count == 12,
            "missing bitmap evidence never becomes implicit unallocated or known-zero records");
        byte[] allAllocated = [0xff, 0xff];
        var allocated = NtfsSyntheticBitmapModel.Evaluate(binding, binding, observations, new(binding, NtfsSyntheticBitmapPresence.Provided, 12, allAllocated));
        Check(allocated is { Allocated: 12, Unallocated: 0, Unknown: 0, AllZeroAllocated: 3, Invalid: 3, Free: 3, BitmapCoverageComplete: true },
            "zero invalid records can be declared allocated while strict Free counts remain unchanged");
        var one = Binding(1);
        var nonzeroInvalid = NtfsSyntheticBitmapModel.Evaluate(one, one,
            [new(0, NtfsDecodeStatus.Invalid, false)], new(one, NtfsSyntheticBitmapPresence.Provided, 1, new byte[] { 0 }));
        Check(nonzeroInvalid is { Unallocated: 1, Invalid: 1, Free: 0, AllZeroInvalid: 0, HeaderCompared: 1, HeaderDisagreements: 0 },
            "nonzero invalid declared unallocated remains invalid rather than changing the raw classification");

        var maximum = Binding(4096); var maximumObservations = Observations(4096); var edgeBits = new byte[512];
        edgeBits[0] = 0x80; edgeBits[1] = 0x01; edgeBits[511] = 0x80;
        var edges = NtfsSyntheticBitmapModel.Evaluate(maximum, maximum, maximumObservations,
            new(maximum, NtfsSyntheticBitmapPresence.Provided, 4096, edgeBits));
        Check(edges is { Slots: 4096, CoveredSlots: 4096, Allocated: 3, Unallocated: 4093, Unknown: 0, BitmapCoverageComplete: true } &&
            edges.CrossCounts.Sum(row => row.Count) == 4096,
            "LSB-first bits seven eight and 4095 stay distinct at byte and maximum boundaries");
        var nine = Binding(12); var twelveObservations = Observations(12);
        var padded = NtfsSyntheticBitmapModel.Evaluate(nine, nine, twelveObservations,
            new(nine, NtfsSyntheticBitmapPresence.Provided, 9, new byte[] { 0x80, 0xff }));
        var cleanPadding = NtfsSyntheticBitmapModel.Evaluate(nine, nine, twelveObservations,
            new(nine, NtfsSyntheticBitmapPresence.Provided, 9, new byte[] { 0x80, 0x01 }));
        Check(padded is { CoveredSlots: 9, Allocated: 2, Unallocated: 7, Unknown: 3, BitmapCoverageComplete: false } &&
            JsonSerializer.Serialize(padded) == JsonSerializer.Serialize(cleanPadding),
            "padding bits beyond explicit coverage cannot allocate unknown slots");

        foreach (bool replaceSource in new[] { true, false })
        {
            var equal = new NtfsSyntheticBitmapBinding(new EqualToken(), new EqualToken(), 12);
            var foreign = equal with { SourceToken = replaceSource ? new EqualToken() : equal.SourceToken, MapEpoch = replaceSource ? equal.MapEpoch : new EqualToken() };
            Rejected(() => NtfsSyntheticBitmapModel.Evaluate(equal, foreign, observations,
                new(equal, NtfsSyntheticBitmapPresence.Missing, 0, ReadOnlyMemory<byte>.Empty)), "BitmapModelBinding",
                "equal-valued tokens cannot impersonate another source or mapping epoch");
            Rejected(() => NtfsSyntheticBitmapModel.Evaluate(equal, equal, observations,
                new(foreign, NtfsSyntheticBitmapPresence.Provided, 8, bits)), "BitmapModelBinding",
                "bitmap binding mismatch cannot be hidden by otherwise valid observation binding");
        }
        var wrongSlots = binding with { Slots = 11 };
        Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, wrongSlots, observations, bitmap), "BitmapModelBinding",
            "matching tokens with another expected slot count still fail binding");
        foreach (int slots in new[] { 0, -1, 4097, int.MaxValue })
        {
            var bad = Binding(slots);
            Rejected(() => NtfsSyntheticBitmapModel.Evaluate(bad, bad, observations,
                new(bad, NtfsSyntheticBitmapPresence.Missing, 0, ReadOnlyMemory<byte>.Empty)), "BitmapModelSlotBudget",
                "invalid or oversized slot declarations fail before model iteration");
        }
        foreach (var badObservations in new[] { observations[..11], observations.Concat([observations[0]]).ToArray() })
            Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, badObservations, bitmap), "BitmapModelObservationBudget",
                "missing and excess observations cannot pretend to cover the declared model");
        foreach (int badSlot in new[] { -1, 12, int.MaxValue, 0 })
        {
            var bad = observations.ToArray(); bad[11] = bad[11] with { Slot = badSlot };
            Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, bad, bitmap), "BitmapModelObservationSlot",
                "negative out-of-range and duplicate slots cannot hide an omitted observation");
        }
        foreach (int covered in new[] { 0, -1, 13, int.MaxValue })
            Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, observations,
                new(binding, NtfsSyntheticBitmapPresence.Provided, covered, bits)), "BitmapModelCoverage",
                "provided bitmap coverage must be a positive bounded declaration");
        foreach (byte[] bytes in new[] { Array.Empty<byte>(), new byte[2], new byte[513] })
            Check(Catch(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, observations,
                new(binding, NtfsSyntheticBitmapPresence.Provided, 8, bytes))) is NtfsSyntheticBitmapException error &&
                error.Code is "BitmapModelCoverage" or "BitmapModelByteBudget", "truncation extra bytes and oversize are rejected rather than treated as intentional partial coverage");
        var badStatus = observations.ToArray(); badStatus[0] = badStatus[0] with { Status = (NtfsDecodeStatus)99 };
        Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, badStatus, bitmap), "BitmapModelDecodeStatus", "undefined strict status cannot create another model category");
        foreach (var badObservation in new[] { new NtfsSyntheticSlotObservation(0, NtfsDecodeStatus.Decoded, false),
            new(0, NtfsDecodeStatus.Free, true), new(0, NtfsDecodeStatus.Decoded, true, true), new(0, NtfsDecodeStatus.Invalid, false, true) })
        {
            var bad = observations.ToArray(); bad[0] = badObservation;
            Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, bad, bitmap), "BitmapModelObservationShape",
                "contradictory header or zero-byte observations cannot claim valid synthetic shape");
        }
        foreach (string stage in new[] { "Before", "Count", "Index", "ThrowCount", "ThrowIndex" })
        {
            using var cancellation = new CancellationTokenSource(); var callbacks = new CallbackList(observations);
            if (stage == "Before") cancellation.Cancel();
            if (stage == "Count") callbacks.OnCount = cancellation.Cancel;
            if (stage == "Index") callbacks.OnIndex = _ => cancellation.Cancel();
            if (stage == "ThrowCount") { callbacks.OnCount = cancellation.Cancel; callbacks.ThrowCount = true; }
            if (stage == "ThrowIndex") { callbacks.OnIndex = _ => cancellation.Cancel(); callbacks.ThrowIndex = true; }
            NtfsSyntheticBitmapReport? partial = null;
            var error = Catch(() => partial = NtfsSyntheticBitmapModel.Evaluate(binding, binding, callbacks, bitmap, cancellation.Token));
            Check(error is OperationCanceledException && partial == null && bits.SequenceEqual(beforeBits),
                "cancellation at input callback boundaries publishes no partial aggregate or mutated bitmap");
        }
        foreach (bool atCount in new[] { false, true })
        {
            var callbacks = new CallbackList(observations) { ThrowCount = atCount, ThrowIndex = !atCount };
            Rejected(() => NtfsSyntheticBitmapModel.Evaluate(binding, binding, callbacks, bitmap), "BitmapModelInput",
                "input accessor exceptions become fixed model codes without private callback messages");
        }
        foreach (string boundary in new[] { "Count", "Index", "Memory" })
        foreach (bool forgeModelError in new[] { false, true })
        foreach (bool cancelAtBoundary in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            const string marker = "SyntheticPrivateAccessor";
            Exception forged = forgeModelError ? new NtfsSyntheticBitmapException(marker) : new OperationCanceledException(marker);
            Action? cancel = cancelAtBoundary ? cancellation.Cancel : null;
            using var memory = new CallbackMemory(forged, cancel);
            var callbacks = new CallbackList(observations);
            var input = bitmap;
            if (boundary == "Count") { callbacks.CountError = forged; callbacks.OnCount = cancel; }
            if (boundary == "Index") { callbacks.IndexError = forged; callbacks.OnIndex = _ => cancel?.Invoke(); }
            if (boundary == "Memory") input = new(binding, NtfsSyntheticBitmapPresence.Provided, 8, memory.View);
            NtfsSyntheticBitmapReport? partial = null;
            var error = Catch(() => partial = NtfsSyntheticBitmapModel.Evaluate(binding, binding, callbacks, input, cancellation.Token));
            bool safe = cancelAtBoundary ? error is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token &&
                !canceled.Message.Contains(marker, StringComparison.Ordinal) :
                error is NtfsSyntheticBitmapException model && model.Code == "BitmapModelInput" && model.Message == "BitmapModelInput" && model.InnerException == null;
            Check(safe && partial == null, "forged accessor cancellation or model codes cannot bypass fixed input errors or caller cancellation priority");
        }
        string json = JsonSerializer.Serialize(report);
        Check(!json.Contains("SourceToken", StringComparison.Ordinal) && !json.Contains("MapEpoch", StringComparison.Ordinal) &&
            !json.Contains("HeaderInUse", StringComparison.Ordinal) && !json.Contains("Bytes", StringComparison.Ordinal),
            "synthetic result exposes aggregate claims without tokens raw bitmap or per-slot observations");
        return passed;
    }
}
