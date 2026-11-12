namespace DiskAtlas.NtfsPrototype;

internal enum NtfsReadSourceKind { ControlledFixture, ValidatedImmutable, LiveUnproven }
internal enum NtfsReadSessionState { Ready, Collecting, NeedsRestart, Rejected, Completed, Cancelled }
internal enum NtfsReadChange { MapMoved, MftGrew, JournalGap, WriterObserved, SourceInvalidated }

// These are simulated assertions, not a verifier for VSS, a live volume, or an
// actual immutable source. SourceStamp is a monotonically increasing simulated
// revalidation epoch, NOT a stable volume identifier. Stamps stay in memory.
internal sealed record NtfsReadProof(NtfsReadSourceKind Source, long SourceStamp, long MapStamp,
    long ValidBytes, int RecordBytes, bool SourceValid = true, bool HasWriteHandle = false,
    bool JournalContinuous = true, bool NoNewUsn = false);
internal sealed record NtfsReadRecord(ulong Reference, ulong ParentReference, string NameToken,
    long? LogicalBytes, long? AllocatedBytes);
internal sealed record NtfsReadBatch(int Generation, int BatchId, long SourceStamp, long MapStamp,
    long ValidBytes, IReadOnlyList<NtfsReadRecord> Records);
internal sealed record NtfsReadSessionReport(string Status, int Generation, int Restarts, int Records,
    int AcceptedWork, long KnownLogicalBytes, long KnownAllocatedBytes, int UnknownLogicalRecords,
    int UnknownAllocatedRecords, long? TotalLogicalBytes, long? TotalAllocatedBytes);

// A bounded pure state model. It performs no file access, reads, tree building,
// background work or provenance verification. All public operations serialize.
internal sealed class NtfsReadSession
{
    internal const int MaxRecords = 4096, MaxBatchRecords = 256, MaxRestarts = 2;
    private readonly object gate = new();
    private NtfsReadProof proof;
    private NtfsReadSessionState state = NtfsReadSessionState.Ready;
    private int generation = 1, restarts, acceptedWork, nextBatch = 1;
    private string lastCode = "ReadySynthetic";
    private NtfsReadSessionReport? report;
    private Dictionary<ulong, NtfsReadRecord> records = new();
    private Dictionary<ulong, ulong> observedSlots = new();
    private Dictionary<(ulong Parent, string Name), ulong> names = new();
    private long logical, allocated;
    private int unknownLogical, unknownAllocated;
    private bool needsNewSource, needsNewMap, needsGrowth;
    private long observedSourceStamp, observedMapStamp, observedValidBytes;
    private bool copyingBatch;

    internal NtfsReadSessionState State { get { lock (gate) return state; } }
    internal int Generation { get { lock (gate) return generation; } }
    internal int Restarts { get { lock (gate) return restarts; } }
    internal int AcceptedWork { get { lock (gate) return acceptedWork; } }
    internal int StagedCount { get { lock (gate) return records.Count; } }
    internal string LastCode { get { lock (gate) return lastCode; } }
    internal NtfsReadSessionReport? Report { get { lock (gate) return report; } }

    internal NtfsReadSession(NtfsReadProof initialProof)
    {
        proof = initialProof;
        if (!ProofShape(initialProof)) Reject("ProofInvalid");
        else if (!ProofAvailable(initialProof)) Reject("ProofUnavailable");
        else ResetObservedProof(initialProof);
    }

    internal string Accept(NtfsReadBatch batch)
    {
        lock (gate)
        {
            if (Terminal) return "IgnoredTerminal";
            if (copyingBatch) return "BatchInProgress";
            if (batch == null) return Reject("BatchInvalid");
            // An obsolete/foreign callback cannot change this generation, its
            // current diagnostic, its retry count or its global work budget.
            if (batch.Generation < generation) return "IgnoredStaleGeneration";
            if (batch.Generation > generation) return "IgnoredFutureGeneration";
            if (state == NtfsReadSessionState.NeedsRestart) return "RestartRequired";
            if (batch.BatchId != nextBatch) return Invalidate("BatchOrderChanged", newSource: false);
            if (batch.SourceStamp != proof.SourceStamp || batch.MapStamp != proof.MapStamp || batch.ValidBytes != proof.ValidBytes)
                return Reject("BatchBindingChanged");
            if (batch.Records == null) return Reject("BatchInvalid");
            NtfsReadRecord[] incoming;
            copyingBatch = true;
            try
            {
                int count = batch.Records.Count;
                string? interrupted = BatchCallbackGate(batch.Generation);
                if (interrupted != null) return interrupted;
                if (count is < 1 or > MaxBatchRecords) return Reject("BatchLimit");
                if (count > MaxRecords - acceptedWork) return Reject("RecordBudgetExceeded");
                incoming = new NtfsReadRecord[count];
                for (int i = 0; i < count; i++)
                {
                    incoming[i] = batch.Records[i];
                    interrupted = BatchCallbackGate(batch.Generation);
                    if (interrupted != null) return interrupted;
                }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            { return BatchCallbackGate(batch.Generation) ?? Reject("BatchInvalid"); }
            finally { copyingBatch = false; }
            string? changed = BatchCallbackGate(batch.Generation);
            if (changed != null) return changed;
            // Count/indexers are externally supplied code and Monitor is
            // reentrant: they may call session APIs, cancel or invalidate it.
            if (batch.BatchId != nextBatch) return Invalidate("BatchOrderChanged", newSource: false);
            if (batch.SourceStamp != proof.SourceStamp || batch.MapStamp != proof.MapStamp || batch.ValidBytes != proof.ValidBytes)
                return Reject("BatchBindingChanged");
            if (incoming.Length > MaxRecords - acceptedWork) return Reject("RecordBudgetExceeded");

            // Build a private draft. No prefix of an invalid/overflowed batch is
            // staged, charged to the budget, or visible through a report.
            var nextRecords = new Dictionary<ulong, NtfsReadRecord>(records);
            var nextSlots = new Dictionary<ulong, ulong>(observedSlots);
            var nextNames = new Dictionary<(ulong Parent, string Name), ulong>(names);
            long nextLogical = logical, nextAllocated = allocated;
            int nextUnknownLogical = unknownLogical, nextUnknownAllocated = unknownAllocated;
            foreach (var item in incoming)
            {
                if (item == null || !ValidReference(item.Reference) || !ValidReference(item.ParentReference) ||
                    !NameToken(item.NameToken) || item.LogicalBytes < 0 || item.AllocatedBytes < 0)
                    return Reject("RecordInvalid");
                ulong slot = item.Reference & NtfsRecordDecoder.MaximumRecordNumber;
                if (nextRecords.TryGetValue(slot, out var previous))
                    return Invalidate(previous.Reference != item.Reference ? "SlotReused" : previous == item ? "DuplicateReference" : "RecordChanged",
                        newSource: previous.Reference != item.Reference || previous != item);
                if (!ObserveReference(nextSlots, item.Reference) || !ObserveReference(nextSlots, item.ParentReference))
                    return Invalidate("ParentSequenceChanged");
                var entry = (item.ParentReference, item.NameToken);
                if (nextNames.TryGetValue(entry, out ulong owner) && owner != item.Reference)
                    return Invalidate("NameCollision");
                try
                {
                    if (item.LogicalBytes.HasValue) nextLogical = checked(nextLogical + item.LogicalBytes.Value);
                    else nextUnknownLogical++;
                    if (item.AllocatedBytes.HasValue) nextAllocated = checked(nextAllocated + item.AllocatedBytes.Value);
                    else nextUnknownAllocated++;
                }
                catch (OverflowException) { return Reject("TotalOverflow"); }
                nextRecords.Add(slot, item with { });
                nextNames.Add(entry, item.Reference);
            }
            records = nextRecords; observedSlots = nextSlots; names = nextNames;
            logical = nextLogical; allocated = nextAllocated;
            unknownLogical = nextUnknownLogical; unknownAllocated = nextUnknownAllocated;
            acceptedWork += incoming.Length;
            nextBatch++;
            state = NtfsReadSessionState.Collecting;
            return lastCode = "Accepted";
        }
    }

    internal string Notify(int expectedGeneration, NtfsReadChange change)
    {
        lock (gate)
        {
            if (Terminal) return "IgnoredTerminal";
            if (expectedGeneration < generation) return "IgnoredStaleGeneration";
            if (expectedGeneration > generation) return "IgnoredFutureGeneration";
            if (!Enum.IsDefined(change)) return Reject("ChangeInvalid");
            string code = change switch
            {
                NtfsReadChange.MapMoved => "MapMoved",
                NtfsReadChange.MftGrew => "MftGrew",
                NtfsReadChange.JournalGap => "JournalGap",
                NtfsReadChange.WriterObserved => "WriterObserved",
                _ => "SourceInvalidated"
            };
            bool mapChanged = change is NtfsReadChange.MapMoved or NtfsReadChange.MftGrew;
            if (state == NtfsReadSessionState.NeedsRestart)
            {
                RememberRestartRequirements(true, mapChanged, change == NtfsReadChange.MftGrew);
                return "RestartRequired";
            }
            return Invalidate(code, newMap: mapChanged, grown: change == NtfsReadChange.MftGrew);
        }
    }

    internal string Restart(int expectedGeneration, NtfsReadProof renewedProof)
    {
        lock (gate)
        {
            if (Terminal) return "IgnoredTerminal";
            if (expectedGeneration < generation) return "IgnoredStaleGeneration";
            if (expectedGeneration > generation) return "IgnoredFutureGeneration";
            if (copyingBatch) return "BatchInProgress";
            if (state != NtfsReadSessionState.NeedsRestart) return "RestartNotRequested";
            if (restarts >= MaxRestarts) return Reject("RestartLimit");
            if (acceptedWork >= MaxRecords) return Reject("RecordBudgetExceeded");
            if (!ProofShape(renewedProof)) return Reject("ProofInvalid");
            if (!ProofAvailable(renewedProof)) return Reject("ProofUnavailable");
            if (renewedProof.SourceStamp < proof.SourceStamp || renewedProof.MapStamp < proof.MapStamp ||
                renewedProof.SourceStamp < observedSourceStamp || renewedProof.MapStamp < observedMapStamp ||
                (!SameBinding(proof, renewedProof) && renewedProof.SourceStamp <= proof.SourceStamp) ||
                (needsNewSource && renewedProof.SourceStamp <= proof.SourceStamp) ||
                (needsNewMap && renewedProof.MapStamp <= proof.MapStamp) ||
                (needsGrowth && (renewedProof.ValidBytes <= proof.ValidBytes || renewedProof.ValidBytes < observedValidBytes)))
                return Reject("RestartProofStale");
            proof = renewedProof;
            restarts++; generation++;
            needsNewSource = needsNewMap = needsGrowth = false;
            ResetObservedProof(renewedProof);
            state = NtfsReadSessionState.Ready; nextBatch = 1;
            return lastCode = "RestartedSynthetic";
        }
    }

    // This is a simulated caller declaration that its batches ended. It does not
    // prove coverage of all MFT slots, a tree, a real snapshot or a live source.
    internal string Complete(int expectedGeneration, NtfsReadProof finalProof)
    {
        lock (gate)
        {
            if (Terminal) return "IgnoredTerminal";
            if (expectedGeneration < generation) return "IgnoredStaleGeneration";
            if (expectedGeneration > generation) return "IgnoredFutureGeneration";
            if (copyingBatch) return "BatchInProgress";
            if (state == NtfsReadSessionState.NeedsRestart)
            {
                if (!ProofShape(finalProof) || !ProofAvailable(finalProof) || !SameBinding(proof, finalProof))
                {
                    RememberObservedProof(finalProof);
                    RememberRestartRequirements(true, false, false);
                }
                return "RestartRequired";
            }
            if (!ProofShape(finalProof) || !ProofAvailable(finalProof))
            { RememberObservedProof(finalProof); return Invalidate("CompletionProofUnavailable"); }
            if (!SameBinding(proof, finalProof))
            { RememberObservedProof(finalProof); return Invalidate("CompletionProofChanged"); }
            if (proof.Source == NtfsReadSourceKind.LiveUnproven) return Reject("SourceUnproven");
            if (state != NtfsReadSessionState.Collecting || records.Count == 0) return Reject("EmptySession");
            report = new("CompletedSyntheticSession", generation, restarts, records.Count, acceptedWork,
                logical, allocated, unknownLogical, unknownAllocated,
                unknownLogical == 0 ? logical : null, unknownAllocated == 0 ? allocated : null);
            state = NtfsReadSessionState.Completed;
            return lastCode = "CompletedSynthetic";
        }
    }

    internal string Cancel()
    {
        lock (gate)
        {
            if (Terminal) return "IgnoredTerminal";
            ClearGeneration(); state = NtfsReadSessionState.Cancelled;
            return lastCode = "CancelledSynthetic";
        }
    }

    private bool Terminal => state is NtfsReadSessionState.Rejected or NtfsReadSessionState.Completed or NtfsReadSessionState.Cancelled;
    private string Reject(string code)
    {
        ClearGeneration(); state = NtfsReadSessionState.Rejected;
        return lastCode = code;
    }
    private string Invalidate(string code, bool newSource = true, bool newMap = false, bool grown = false)
    {
        RememberRestartRequirements(newSource, newMap, grown);
        ClearGeneration();
        state = restarts >= MaxRestarts ? NtfsReadSessionState.Rejected : NtfsReadSessionState.NeedsRestart;
        return lastCode = restarts >= MaxRestarts ? "RestartLimit" : code;
    }
    private void RememberRestartRequirements(bool newSource, bool newMap, bool grown)
    { needsNewSource |= newSource; needsNewMap |= newMap; needsGrowth |= grown; }
    private void RememberObservedProof(NtfsReadProof observation)
    {
        if (!ProofShape(observation)) return;
        observedSourceStamp = Math.Max(observedSourceStamp, observation.SourceStamp);
        observedMapStamp = Math.Max(observedMapStamp, observation.MapStamp);
        observedValidBytes = Math.Max(observedValidBytes, observation.ValidBytes);
        RememberRestartRequirements(true, observation.MapStamp != proof.MapStamp, observation.ValidBytes > proof.ValidBytes);
    }
    private void ResetObservedProof(NtfsReadProof current)
    { observedSourceStamp = current.SourceStamp; observedMapStamp = current.MapStamp; observedValidBytes = current.ValidBytes; }
    private string? BatchCallbackGate(int expectedGeneration)
    {
        if (Terminal) return "IgnoredTerminal";
        if (expectedGeneration < generation) return "IgnoredStaleGeneration";
        if (expectedGeneration > generation) return "IgnoredFutureGeneration";
        return state == NtfsReadSessionState.NeedsRestart ? "RestartRequired" : null;
    }
    private void ClearGeneration()
    {
        records.Clear(); observedSlots.Clear(); names.Clear(); report = null;
        logical = allocated = 0; unknownLogical = unknownAllocated = 0; nextBatch = 1;
    }
    private bool ValidReference(ulong reference)
    {
        if (reference >> 48 == 0) return false;
        ulong slot = reference & NtfsRecordDecoder.MaximumRecordNumber;
        // Divide first: even an extreme reference must never overflow a product.
        return slot < (ulong)(proof.ValidBytes / proof.RecordBytes);
    }
    private static bool ObserveReference(Dictionary<ulong, ulong> slots, ulong reference)
    {
        ulong slot = reference & NtfsRecordDecoder.MaximumRecordNumber;
        if (slots.TryGetValue(slot, out ulong previous)) return previous == reference;
        slots.Add(slot, reference); return true;
    }
    private static bool ProofShape(NtfsReadProof value) => value != null && Enum.IsDefined(value.Source) &&
        value.SourceStamp > 0 && value.MapStamp > 0 && value.RecordBytes is >= 1024 and <= 65536 &&
        value.RecordBytes % 512 == 0 && value.ValidBytes >= value.RecordBytes && value.ValidBytes % value.RecordBytes == 0;
    private static bool ProofAvailable(NtfsReadProof value) => value.SourceValid && !value.HasWriteHandle && value.JournalContinuous;
    private static bool SameBinding(NtfsReadProof left, NtfsReadProof right) => left.Source == right.Source &&
        left.SourceStamp == right.SourceStamp && left.MapStamp == right.MapStamp &&
        left.ValidBytes == right.ValidBytes && left.RecordBytes == right.RecordBytes;
    private static bool NameToken(string name) => name != null && name.Length is >= 1 and <= 40 &&
        char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
