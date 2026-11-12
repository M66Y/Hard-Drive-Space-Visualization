using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// Synthetic metadata only. No filesystem, source adapter, volume, or snapshot.
internal static class NtfsReadSessionTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS read session check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS read session " + name);
        }
        var proof = new NtfsReadProof(NtfsReadSourceKind.ControlledFixture, 11, 21, 16 * 1024 * 1024, 1024);
        ulong Parent(ushort sequence = 1) => NtfsRecordFixtures.Reference(5, sequence);
        NtfsReadRecord Record(int slot, long? logical = 1, long? allocated = 2, ushort sequence = 1) =>
            new(NtfsRecordFixtures.Reference((ulong)slot, sequence), Parent(), "Entry" + slot, logical, allocated);
        NtfsReadBatch Batch(int generation, int id, NtfsReadProof binding, params NtfsReadRecord[] records) =>
            new(generation, id, binding.SourceStamp, binding.MapStamp, binding.ValidBytes, records);
        void Clean(NtfsReadSession session, NtfsReadSessionState state, string name) =>
            Check(session.State == state && session.StagedCount == 0 && session.Report == null, name);

        var session = new NtfsReadSession(proof);
        Check(session.State == NtfsReadSessionState.Ready && session.Generation == 1 && session.Restarts == 0 &&
            session.AcceptedWork == 0 && session.Report == null, "fresh valid synthetic session is ready without a published result");
        Check(session.Restart(1, proof) == "RestartNotRequested" && session.State == NtfsReadSessionState.Ready,
            "restart cannot silently erase an active generation");
        var caller = new[] { Record(16, 10, 20), Record(17, 30, 40) };
        Check(session.Accept(Batch(1, 1, proof, caller)) == "Accepted" && session.State == NtfsReadSessionState.Collecting &&
            session.StagedCount == 2 && session.AcceptedWork == 2 && session.Report == null, "valid first batch stages atomically without publishing");
        caller[0] = Record(18, 999, 999);
        Check(session.Accept(Batch(1, 2, proof, Record(18, 50, 60))) == "Accepted", "next contiguous batch is accepted");
        Check(session.Complete(1, proof) == "CompletedSynthetic" && session.State == NtfsReadSessionState.Completed,
            "explicit current-generation end declaration completes only a synthetic collection");
        var report = session.Report!;
        Check(report.Status == "CompletedSyntheticSession" && report.Generation == 1 && report.Restarts == 0 &&
            report.Records == 3 && report.AcceptedWork == 3 && report.TotalLogicalBytes == 90 && report.TotalAllocatedBytes == 120,
            "final report uses copied records and exact known sums");
        Check(report.UnknownLogicalRecords == 0 && report.UnknownAllocatedRecords == 0 &&
            report.KnownLogicalBytes == report.TotalLogicalBytes && report.KnownAllocatedBytes == report.TotalAllocatedBytes,
            "fully known totals remain distinct from unknown counters");
        string json = JsonSerializer.Serialize(report);
        Check(!json.Contains("Reference", StringComparison.Ordinal) && !json.Contains("Stamp", StringComparison.Ordinal) &&
            !json.Contains("Entry", StringComparison.Ordinal) && !json.Contains("Path", StringComparison.Ordinal),
            "published synthetic report has no input identities names paths or stamps");
        foreach (string outcome in new[] {
            session.Accept(null!), session.Notify(1, NtfsReadChange.SourceInvalidated), session.Restart(1, proof),
            session.Complete(1, proof), session.Cancel() })
            Check(outcome == "IgnoredTerminal" && session.State == NtfsReadSessionState.Completed && ReferenceEquals(session.Report, report),
                "completed terminal result cannot be replaced by late callbacks or cancellation");

        var unknown = new NtfsReadSession(proof);
        Check(unknown.Accept(Batch(1, 1, proof, Record(16, null, 0), Record(17, 0, null), Record(18, null, null))) == "Accepted",
            "unknown size metadata is retained without inventing zeros");
        Check(unknown.Complete(1, proof) == "CompletedSynthetic", "unknown metadata may finish synthetic collection");
        Check(unknown.Report is { Records: 3, KnownLogicalBytes: 0, KnownAllocatedBytes: 0,
            UnknownLogicalRecords: 2, UnknownAllocatedRecords: 2, TotalLogicalBytes: null, TotalAllocatedBytes: null },
            "unknown totals stay null while genuine known zeros and unknown counts remain visible");
        var maximum = new NtfsReadSession(proof);
        maximum.Accept(Batch(1, 1, proof, Record(16, long.MaxValue, long.MaxValue)));
        maximum.Complete(1, proof);
        Check(maximum.Report is { TotalLogicalBytes: long.MaxValue, TotalAllocatedBytes: long.MaxValue },
            "single int64 maximum size remains exact without floating conversion");
        foreach (bool logicalOverflow in new[] { true, false })
        {
            var overflow = new NtfsReadSession(proof);
            var first = Record(16, logicalOverflow ? long.MaxValue : 0, logicalOverflow ? 0 : long.MaxValue);
            var second = Record(17, logicalOverflow ? 1 : 0, logicalOverflow ? 0 : 1);
            Check(overflow.Accept(Batch(1, 1, proof, first, second)) == "TotalOverflow" && overflow.AcceptedWork == 0,
                "overflow rejects the entire batch before any budget or prefix is accepted");
            Clean(overflow, NtfsReadSessionState.Rejected, "overflow never publishes a partial total");
        }

        foreach (var source in new[] { NtfsReadSourceKind.ControlledFixture, NtfsReadSourceKind.ValidatedImmutable })
        {
            var sourceProof = proof with { Source = source };
            var sourceSession = new NtfsReadSession(sourceProof);
            sourceSession.Accept(Batch(1, 1, sourceProof, Record(16)));
            Check(sourceSession.Complete(1, sourceProof) == "CompletedSynthetic" && sourceSession.Report != null,
                "supported source assertion completes only the pure synthetic model");
        }
        var liveProof = proof with { Source = NtfsReadSourceKind.LiveUnproven, NoNewUsn = true };
        var live = new NtfsReadSession(liveProof);
        live.Accept(Batch(1, 1, liveProof, Record(16)));
        Check(live.Complete(1, liveProof) == "SourceUnproven", "same mapping and no new USN cannot certify a live source");
        Clean(live, NtfsReadSessionState.Rejected, "unproven live source discards staged data and has no completed report");

        foreach (var badProof in new[] {
            proof with { Source = (NtfsReadSourceKind)999 }, proof with { SourceStamp = 0 }, proof with { MapStamp = -1 },
            proof with { ValidBytes = 0 }, proof with { ValidBytes = proof.ValidBytes + 1 },
            proof with { RecordBytes = 0 }, proof with { RecordBytes = 1025 }, proof with { RecordBytes = 66048 },
            proof with { SourceValid = false }, proof with { HasWriteHandle = true }, proof with { JournalContinuous = false } })
            Clean(new NtfsReadSession(badProof), NtfsReadSessionState.Rejected, "invalid or unavailable initial proof cannot start collection");
        Clean(new NtfsReadSession(null!), NtfsReadSessionState.Rejected, "null initial proof is a controlled rejection");
        var empty = new NtfsReadSession(proof);
        Check(empty.Complete(1, proof) == "EmptySession", "an empty end declaration does not create a completed report");
        foreach (var badRecord in new[] {
            Record(16) with { Reference = 16 }, Record(16) with { ParentReference = 5 },
            Record(16) with { Reference = ulong.MaxValue }, Record(16) with { ParentReference = ulong.MaxValue },
            Record(16) with { LogicalBytes = -1 }, Record(16) with { AllocatedBytes = -1 },
            Record(16) with { NameToken = "" }, Record(16) with { NameToken = "bad/name" },
            Record(16) with { NameToken = "bad:name" }, Record(16) with { NameToken = "bad\nname" },
            Record(16) with { NameToken = new string('x', 41) }, Record(16) with { NameToken = null! } })
        {
            var invalid = new NtfsReadSession(proof);
            Check(invalid.Accept(Batch(1, 1, proof, Record(17), badRecord)) == "RecordInvalid" && invalid.AcceptedWork == 0,
                "malformed metadata rejects an entire draft including a valid prefix");
            Clean(invalid, NtfsReadSessionState.Rejected, "invalid batch publishes no complete or staged prefix");
        }
        foreach (var changedBinding in new[] {
            proof with { SourceStamp = proof.SourceStamp + 1 }, proof with { MapStamp = proof.MapStamp + 1 },
            proof with { ValidBytes = proof.ValidBytes + 1024 } })
        {
            var mismatch = new NtfsReadSession(proof);
            Check(mismatch.Accept(Batch(1, 1, changedBinding, Record(16))) == "BatchBindingChanged",
                "untrusted batch binding mismatch is a protocol rejection rather than new proof");
            Clean(mismatch, NtfsReadSessionState.Rejected, "binding mismatch cannot later be hidden by a fresh epoch");
        }

        var generation = new NtfsReadSession(proof);
        generation.Accept(Batch(1, 1, proof, Record(16, 100, 200)));
        Check(generation.Accept(Batch(1, 3, proof, Record(17))) == "BatchOrderChanged", "out-of-order batch invalidates its whole generation");
        Clean(generation, NtfsReadSessionState.NeedsRestart, "out-of-order prefix is discarded before a retry");
        Check(generation.Restart(1, proof) == "RestartedSynthetic" && generation.Generation == 2 && generation.Restarts == 1 &&
            generation.AcceptedWork == 1, "a protocol-only retry may reuse the binding but not refund accepted work");
        generation.Accept(Batch(2, 1, proof, Record(18, 7, 9)));
        string last = generation.LastCode;
        foreach (string outcome in new[] {
            generation.Accept(new(1, 99, -1, -1, -1, null!)), generation.Notify(1, NtfsReadChange.MftGrew),
            generation.Complete(1, proof), generation.Restart(1, proof) })
            Check(outcome == "IgnoredStaleGeneration" && generation.State == NtfsReadSessionState.Collecting &&
                generation.StagedCount == 1 && generation.AcceptedWork == 2 && generation.LastCode == last,
                "obsolete callbacks do not mutate the current generation or its work and diagnostics");
        Check(generation.Complete(2, proof) == "CompletedSynthetic" && generation.Report is { Records: 1, AcceptedWork: 2,
            TotalLogicalBytes: 7, TotalAllocatedBytes: 9, Restarts: 1 }, "final report contains only current-generation records");

        foreach (var change in Enum.GetValues<NtfsReadChange>())
        {
            var changed = new NtfsReadSession(proof);
            changed.Accept(Batch(1, 1, proof, Record(16)));
            changed.Notify(1, change);
            Clean(changed, NtfsReadSessionState.NeedsRestart, "source and mapping events discard the active prefix");
            Check(changed.Restart(1, proof) == "RestartProofStale", "known changes cannot be erased by reusing the old proof");
            Clean(changed, NtfsReadSessionState.Rejected, "stale retry proof cannot become a successful new generation");
        }
        var grown = new NtfsReadSession(proof);
        grown.Notify(1, NtfsReadChange.MftGrew);
        var grownProof = proof with { SourceStamp = 12, MapStamp = 22, ValidBytes = proof.ValidBytes + 1024 };
        Check(grown.Restart(1, grownProof) == "RestartedSynthetic", "growth retry requires a new validation epoch mapping and effective length");
        grown.Accept(Batch(2, 1, grownProof, Record(16)));
        Check(grown.Complete(2, grownProof) == "CompletedSynthetic", "renewed controlled source can collect a fresh generation");
        var observed = new NtfsReadSession(proof);
        observed.Accept(Batch(1, 1, proof, Record(16)));
        var observedProof = proof with { SourceStamp = 13, MapStamp = 23, ValidBytes = proof.ValidBytes + 2048 };
        Check(observed.Complete(1, observedProof) == "CompletionProofChanged", "changed final proof requires recollection");
        Check(observed.Restart(1, grownProof) == "RestartProofStale", "retry cannot roll below an observed epoch mapping or length high-water mark");
        var limit = new NtfsReadSession(proof);
        for (int i = 1; i <= 2; i++)
        {
            limit.Accept(Batch(i, 2, proof, Record(16)));
            Check(limit.Restart(i, proof) == "RestartedSynthetic", "protocol-only retry stays within the explicit restart budget");
        }
        limit.Accept(Batch(3, 2, proof, Record(16)));
        Clean(limit, NtfsReadSessionState.Rejected, "third invalidation rejects rather than permitting unbounded retries");
        var cancelled = new NtfsReadSession(proof);
        cancelled.Accept(Batch(1, 1, proof, Record(16)));
        Check(cancelled.Cancel() == "CancelledSynthetic" && cancelled.AcceptedWork == 1, "cancellation keeps spent work but discards pending data");
        Clean(cancelled, NtfsReadSessionState.Cancelled, "cancel has an independent terminal state");
        Check(cancelled.Accept(Batch(1, 2, proof, Record(17))) == "IgnoredTerminal" && cancelled.Complete(1, proof) == "IgnoredTerminal" &&
            cancelled.State == NtfsReadSessionState.Cancelled, "late batch and completion cannot revive cancellation");
        foreach (bool cancel in new[] { true, false })
        {
            var interrupted = new NtfsReadSession(proof);
            var external = new InterruptingRecords(Record(16), Record(17), () =>
            {
                if (cancel) interrupted.Cancel();
                else interrupted.Notify(1, NtfsReadChange.SourceInvalidated);
            });
            interrupted.Accept(new(1, 1, proof.SourceStamp, proof.MapStamp, proof.ValidBytes, external));
            Check(external.Reads == 1 && interrupted.AcceptedWork == 0 && interrupted.Report == null && interrupted.StagedCount == 0 &&
                interrupted.State == (cancel ? NtfsReadSessionState.Cancelled : NtfsReadSessionState.NeedsRestart),
                "indexer cancellation or invalidation stops before touching the next external item");
        }
        return passed;
    }

    private sealed class InterruptingRecords(NtfsReadRecord first, NtfsReadRecord second, Action interrupt) : IReadOnlyList<NtfsReadRecord>
    {
        private readonly NtfsReadRecord[] items = [first, second];
        internal int Reads { get; private set; }
        public int Count => items.Length;
        public NtfsReadRecord this[int index]
        {
            get
            {
                Reads++;
                if (index == 0) interrupt();
                return items[index];
            }
        }
        public IEnumerator<NtfsReadRecord> GetEnumerator() => ((IEnumerable<NtfsReadRecord>)items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
