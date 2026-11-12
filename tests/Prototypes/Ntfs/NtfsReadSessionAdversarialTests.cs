namespace DiskAtlas.NtfsPrototype;

// Pure simulated callbacks and metadata. No I/O, real snapshot, volume handle,
// path, name discovery or claim of MFT coverage is involved in these tests.
internal static class NtfsReadSessionAdversarialTests
{
    private sealed class CallbackRecords(NtfsReadRecord[] items) : IReadOnlyList<NtfsReadRecord>
    {
        internal Func<int>? OnCount;
        internal Action<int>? OnIndex;
        internal bool ThrowFromIndex;
        public int Count => OnCount?.Invoke() ?? items.Length;
        public NtfsReadRecord this[int index]
        {
            get
            {
                OnIndex?.Invoke(index);
                if (ThrowFromIndex) throw new InvalidOperationException("SyntheticCopyInterrupted");
                return items[index];
            }
        }
        public IEnumerator<NtfsReadRecord> GetEnumerator() => ((IEnumerable<NtfsReadRecord>)items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS read session adversarial check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS read session adversarial " + name);
        }
        NtfsReadProof Proof(NtfsReadSourceKind kind = NtfsReadSourceKind.ValidatedImmutable) =>
            new(kind, 11, 21, 4096L * 1024, 1024);
        ulong Reference(int slot, ushort sequence = 1) => ((ulong)sequence << 48) | (uint)slot;
        NtfsReadRecord Record(int slot, long? logical = 1, long? allocated = 1, string? name = null,
            ulong? parent = null, ushort sequence = 1) =>
            new(Reference(slot, sequence), parent ?? Reference(4095), name ?? "item-" + slot, logical, allocated);
        NtfsReadBatch Batch(NtfsReadSession session, NtfsReadProof proof, int id, params NtfsReadRecord[] records) =>
            new(session.Generation, id, proof.SourceStamp, proof.MapStamp, proof.ValidBytes, records);
        void NoPublication(NtfsReadSession session, string name) => Check(session.Report == null &&
            session.State != NtfsReadSessionState.Completed, name);
        (NtfsReadSessionState State, int Generation, int Restarts, int Staged, int Work, object? Report) Snapshot(NtfsReadSession session) =>
            (session.State, session.Generation, session.Restarts, session.StagedCount, session.AcceptedWork, session.Report);

        // A valid first half cannot leak into the accepted state if the final
        // entry is invalid. Previously accepted work is not refunded on failure.
        foreach (bool invalidLogical in new[] { true, false })
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            session.Accept(Batch(session, proof, 2, Record(2), Record(3),
                invalidLogical ? Record(4, -1, 0) : Record(4, 0, -1)));
            Check(session.State == NtfsReadSessionState.Rejected && session.StagedCount == 0 && session.AcceptedWork == 1,
                "late invalid size rejects the entire generation without charging its valid prefix");
            NoPublication(session, "invalid batch has no published report");
        }
        foreach (bool overflowLogical in new[] { true, false })
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1, 0, 0)));
            session.Accept(Batch(session, proof, 2, Record(2, overflowLogical ? long.MaxValue : 0,
                overflowLogical ? 0 : long.MaxValue), Record(3, 1, 1)));
            Check(session.State == NtfsReadSessionState.Rejected && session.StagedCount == 0 && session.AcceptedWork == 1,
                "late aggregate overflow cannot publish a partially summed batch");
            NoPublication(session, "overflow retains no successful report");
        }
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            var caller = new[] { Record(1, 5, 7), Record(2, 11, 13) };
            session.Accept(new(session.Generation, 1, proof.SourceStamp, proof.MapStamp, proof.ValidBytes, caller));
            caller[0] = Record(1, long.MaxValue, long.MaxValue, "changed-after-accept");
            caller[1] = null!;
            session.Complete(session.Generation, proof);
            Check(session.State == NtfsReadSessionState.Completed && session.Report is
                { Records: 2, KnownLogicalBytes: 16, KnownAllocatedBytes: 20, AcceptedWork: 2 },
                "mutating the caller batch after acceptance cannot mutate committed metadata");
        }
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1, null, 3), Record(2, 0, null), Record(3, 2, 4)));
            session.Complete(session.Generation, proof);
            Check(session.State == NtfsReadSessionState.Completed && session.Report is
                { Records: 3, KnownLogicalBytes: 2, KnownAllocatedBytes: 7, UnknownLogicalRecords: 1,
                  UnknownAllocatedRecords: 1, TotalLogicalBytes: null, TotalAllocatedBytes: null },
                "unknown sizes remain counted unknown with nullable totals after synthetic collection completes");
        }
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1, 0, 0)));
            session.Complete(session.Generation, proof);
            Check(session.Report is { UnknownLogicalRecords: 0, UnknownAllocatedRecords: 0,
                TotalLogicalBytes: 0, TotalAllocatedBytes: 0 }, "a genuinely known zero remains distinct from an unknown total");
        }

        foreach (string change in new[] { "Sequence", "Parent", "Name", "EntryCollision" })
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1, name: "stable-entry")));
            NtfsReadRecord conflicting = change switch
            {
                "Sequence" => Record(1, sequence: 2),
                "Parent" => Record(1, name: "stable-entry", parent: Reference(4)),
                "Name" => Record(1, name: "renamed-entry"),
                _ => Record(2, name: "stable-entry")
            };
            session.Accept(Batch(session, proof, 2, Record(3), conflicting));
            Check((session.State is NtfsReadSessionState.NeedsRestart or NtfsReadSessionState.Rejected) &&
                session.StagedCount == 0 && session.AcceptedWork == 1,
                "cross-batch identity or namespace conflict discards the whole candidate generation");
            NoPublication(session, "conflicting old and new records cannot be published together");
        }
        foreach (string field in new[] { "Source", "Map", "Length" })
        foreach (long delta in new long[] { -1024, -1, 1, 1024 })
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            var bound = Batch(session, proof, 2, Record(2));
            var mismatched = field switch
            {
                "Source" => bound with { SourceStamp = proof.SourceStamp + delta },
                "Map" => bound with { MapStamp = proof.MapStamp + delta },
                _ => bound with { ValidBytes = proof.ValidBytes + delta }
            };
            session.Accept(mismatched);
            Check(session.State == NtfsReadSessionState.Rejected && session.LastCode == "BatchBindingChanged" &&
                session.StagedCount == 0 && session.AcceptedWork == 1 && session.Report == null,
                "a current-generation batch with a different binding cannot be treated as independently verified renewal");
        }

        // Same stamps across a retry make the generation check essential: an old
        // callback must never finish or invalidate the new candidate generation.
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            int old = session.Generation;
            session.Accept(Batch(session, proof, 1, Record(1)));
            // Batch-order loss is explicitly allowed to re-read the same proof;
            // identity/mapping/source invalidation requires fresh evidence.
            session.Accept(Batch(session, proof, 3, Record(9)));
            session.Restart(old, proof);
            Check(session.Generation == old + 1 && session.Restarts == 1 && session.StagedCount == 0 && session.AcceptedWork == 1,
                "restart advances generation without refunding discarded work");
            session.Accept(Batch(session, proof, 1, Record(2)));
            var before = Snapshot(session);
            foreach (int stale in new[] { old, int.MaxValue })
            {
                session.Accept(new(stale, -1, -1, -1, -1, null!));
                session.Notify(stale, NtfsReadChange.SourceInvalidated);
                session.Restart(stale, proof with { SourceValid = false });
                session.Complete(stale, proof);
                Check(Snapshot(session) == before, "old or future callbacks cannot mutate current generation or work budget");
            }
            session.Complete(session.Generation, proof);
            Check(session.Report is { Records: 1, AcceptedWork: 2 } && session.Report.Generation == session.Generation,
                "only current generation records reach the final report while discarded work remains charged");
        }

        foreach (var change in Enum.GetValues<NtfsReadChange>())
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            session.Notify(session.Generation, change);
            Check(session.StagedCount == 0 && session.AcceptedWork == 1, "every modeled source change invalidates all staged records");
            NoPublication(session, "source change never creates a published report");
        }
        foreach (bool growth in new[] { false, true })
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            session.Notify(session.Generation, NtfsReadChange.JournalGap);
            session.Notify(session.Generation, growth ? NtfsReadChange.MftGrew : NtfsReadChange.MapMoved);
            // A later weaker observation must not erase the stronger requirement.
            session.Notify(session.Generation, NtfsReadChange.WriterObserved);
            var insufficient = proof with { SourceStamp = proof.SourceStamp + 1,
                MapStamp = growth ? proof.MapStamp + 1 : proof.MapStamp };
            session.Restart(session.Generation, insufficient);
            Check(session.State == NtfsReadSessionState.Rejected && session.StagedCount == 0,
                "stronger mapping or growth invalidation received while waiting cannot be erased by weaker events");
        }
        foreach (bool alreadyWaiting in new[] { false, true })
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            if (alreadyWaiting) session.Accept(Batch(session, proof, 3, Record(2)));
            var changed = proof with { MapStamp = proof.MapStamp + 1, ValidBytes = proof.ValidBytes + 1024 };
            session.Complete(session.Generation, changed);
            session.Restart(session.Generation, proof with { SourceStamp = proof.SourceStamp + 1 });
            Check(session.State == NtfsReadSessionState.Rejected && session.StagedCount == 0,
                "a final proof revealing mapping growth cannot be followed by renewal of the old mapping");
        }
        foreach (bool alreadyWaiting in new[] { false, true })
        foreach (string field in new[] { "Source", "Map", "Length" })
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            if (alreadyWaiting) session.Accept(Batch(session, proof, 3, Record(2)));
            var observed = proof with { SourceStamp = 14, MapStamp = 29, ValidBytes = proof.ValidBytes + 2048 };
            session.Complete(session.Generation, observed);
            var lower = field switch
            {
                "Source" => observed with { SourceStamp = 13 },
                "Map" => observed with { MapStamp = 28 },
                _ => observed with { ValidBytes = proof.ValidBytes + 1024 }
            };
            session.Restart(session.Generation, lower);
            Check(session.State == NtfsReadSessionState.Rejected && session.StagedCount == 0,
                "renewal cannot retreat below source mapping or length high-water marks already observed by a final proof");
        }
        foreach (bool alreadyWaiting in new[] { false, true })
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            if (alreadyWaiting) session.Accept(Batch(session, proof, 3, Record(2)));
            var observed = proof with { SourceStamp = 14, MapStamp = 29, ValidBytes = proof.ValidBytes + 2048 };
            session.Complete(session.Generation, observed);
            session.Restart(session.Generation, observed);
            Check(session.State == NtfsReadSessionState.Ready && session.Generation == 2,
                "renewal may match the fresh observed binding without an artificial extra stamp increment");
        }
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            int old = session.Generation;
            session.Accept(Batch(session, proof, 3, Record(9)));
            session.Restart(old, proof);
            session.Accept(Batch(session, proof, 1, Record(2)));
            session.Accept(Batch(session, proof, 3, Record(9)));
            // The current generation only lost batch ordering; stale callbacks
            // must not impose fresh mapping/growth requirements on its retry.
            session.Notify(old, NtfsReadChange.MftGrew);
            session.Notify(old, NtfsReadChange.SourceInvalidated);
            int current = session.Generation;
            session.Restart(current, proof);
            Check(session.Generation == current + 1 && session.State == NtfsReadSessionState.Ready && session.Restarts == 2,
                "old-generation strong events cannot upgrade current retry requirements");
        }
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            session.Notify(session.Generation, NtfsReadChange.JournalGap);
            session.Notify(session.Generation, NtfsReadChange.MftGrew);
            var renewed = proof with { SourceStamp = proof.SourceStamp + 1,
                MapStamp = proof.MapStamp + 1, ValidBytes = proof.ValidBytes + 1024 };
            session.Restart(session.Generation, renewed);
            Check(session.State == NtfsReadSessionState.Ready && session.Generation == 2,
                "combined source mapping and growth requirements accept sufficiently fresh renewal");
            session.Accept(Batch(session, renewed, 1, Record(2)));
            session.Accept(Batch(session, renewed, 3, Record(9)));
            session.Restart(session.Generation, renewed);
            Check(session.State == NtfsReadSessionState.Ready && session.Generation == 3,
                "successful renewal clears old strong requirements before a later ordering-only retry");
            session.Accept(Batch(session, renewed, 1, Record(3)));
            session.Complete(session.Generation, renewed);
            Check(session.Report is { Records: 1, AcceptedWork: 3, Restarts: 2 },
                "completed metadata comes only from the final generation after requirements reset");
        }
        foreach (var changed in new[]
        {
            Proof() with { SourceStamp = 12 }, Proof() with { MapStamp = 22 },
            Proof() with { ValidBytes = 4097L * 1024 }, Proof() with { SourceValid = false },
            Proof() with { HasWriteHandle = true }, Proof() with { JournalContinuous = false },
            Proof() with { Source = NtfsReadSourceKind.LiveUnproven }
        })
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            session.Complete(session.Generation, changed);
            Check(session.StagedCount == 0, "changed final proof discards staged metadata instead of publishing an old source");
            NoPublication(session, "invalid or mismatched final proof prevents completion");
        }
        foreach (bool noNewUsn in new[] { false, true })
        {
            var live = Proof(NtfsReadSourceKind.LiveUnproven) with { NoNewUsn = noNewUsn };
            var session = new NtfsReadSession(live);
            session.Accept(Batch(session, live, 1, Record(1)));
            session.Complete(session.Generation, live);
            Check(session.State == NtfsReadSessionState.Rejected && session.Report == null,
                "unchanged stamps and an empty journal cannot promote LiveUnproven to a complete session");
            session.Restart(session.Generation, Proof());
            Check(session.State == NtfsReadSessionState.Rejected && session.Report == null,
                "a late source-kind substitution cannot promote a terminal unproven source");
        }
        foreach (var invalidSource in new[] { (NtfsReadSourceKind)(-1), (NtfsReadSourceKind)int.MaxValue })
        {
            var proof = Proof() with { Source = invalidSource }; var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            session.Complete(session.Generation, proof);
            Check(session.State == NtfsReadSessionState.Rejected && session.Report == null,
                "unknown source enum values cannot act as verified immutable evidence");
        }

        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                session.Accept(Batch(session, proof, 1, Record(attempt + 1)));
                int generation = session.Generation;
                session.Notify(generation, NtfsReadChange.MapMoved);
                proof = proof with { SourceStamp = proof.SourceStamp + 1, MapStamp = proof.MapStamp + 1 };
                session.Restart(generation, proof);
            }
            Check(session.Generation == 3 && session.Restarts == 2 && session.AcceptedWork == 2,
                "two recoveries consume exactly the restart budget");
            session.Accept(Batch(session, proof, 1, Record(3)));
            session.Notify(session.Generation, NtfsReadChange.MftGrew);
            Check(session.State == NtfsReadSessionState.Rejected && session.StagedCount == 0 && session.Restarts == 2,
                "a third change cannot create an unbounded fourth generation");
        }
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            for (int batch = 1; batch <= 12; batch++)
                session.Accept(Batch(session, proof, batch,
                    Enumerable.Range((batch - 1) * 256, 256).Select(slot => Record(slot)).ToArray()));
            int old = session.Generation;
            session.Notify(old, NtfsReadChange.MapMoved);
            proof = proof with { SourceStamp = proof.SourceStamp + 1, MapStamp = proof.MapStamp + 1 };
            session.Restart(old, proof);
            for (int batch = 1; batch <= 4; batch++)
                session.Accept(Batch(session, proof, batch,
                    Enumerable.Range((batch - 1) * 256, 256).Select(slot => Record(slot)).ToArray()));
            Check(session.AcceptedWork == 4096 && session.StagedCount == 1024,
                "discarded generations do not return capacity to the session-wide record budget");
            var before = Snapshot(session);
            session.Accept(new(old, 13, proof.SourceStamp, proof.MapStamp, proof.ValidBytes, [Record(2048)]));
            Check(Snapshot(session) == before, "stale data at the budget endpoint does not consume or invalidate current work");
            session.Accept(Batch(session, proof, 5, Record(2048)));
            Check(session.State == NtfsReadSessionState.Rejected && session.AcceptedWork == 4096 && session.StagedCount == 0,
                "the first record beyond the global limit is rejected atomically across retries");
        }
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Enumerable.Range(0, 257).Select(slot => Record(slot)).ToArray()));
            Check(session.State == NtfsReadSessionState.Rejected && session.AcceptedWork == 0 && session.StagedCount == 0,
                "an oversized first batch cannot commit its first allowed records");
        }

        foreach (string location in new[] { "Count", "Indexer", "ThrowingIndexer" })
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            var incoming = new CallbackRecords([Record(2)]);
            if (location == "Count") incoming.OnCount = () => { session.Cancel(); return 0; };
            else incoming.OnIndex = _ => session.Cancel();
            incoming.ThrowFromIndex = location == "ThrowingIndexer";
            session.Accept(new(session.Generation, 2, proof.SourceStamp, proof.MapStamp, proof.ValidBytes, incoming));
            Check(session.State == NtfsReadSessionState.Cancelled && session.StagedCount == 0 && session.AcceptedWork == 1 && session.Report == null,
                "reentrant cancellation from external collection access cannot be revived or replaced by validation failure");
        }
        foreach (string location in new[] { "Count", "Indexer", "ThrowingIndexer" })
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            var incoming = new CallbackRecords([Record(2)]);
            void Invalidate() { session.Notify(session.Generation, NtfsReadChange.MftGrew); }
            if (location == "Count") incoming.OnCount = () => { Invalidate(); return 1; };
            else incoming.OnIndex = _ => Invalidate();
            incoming.ThrowFromIndex = location == "ThrowingIndexer";
            session.Accept(new(session.Generation, 2, proof.SourceStamp, proof.MapStamp, proof.ValidBytes, incoming));
            Check(session.State == NtfsReadSessionState.NeedsRestart && session.StagedCount == 0 && session.AcceptedWork == 1 && session.Report == null,
                "reentrant source invalidation survives both successful and throwing collection copies");
            session.Restart(session.Generation, proof with { SourceStamp = proof.SourceStamp + 1, MapStamp = proof.MapStamp + 1 });
            Check(session.State == NtfsReadSessionState.Rejected,
                "reentrant MFT growth retains its growth requirement after the interrupted copy");
        }
        foreach (string location in new[] { "Count", "Indexer" })
        foreach (string operation in new[] { "Accept", "Complete", "Restart" })
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            string? nestedCode = null;
            var incoming = new CallbackRecords([Record(2, 2, 3)]);
            void Reenter()
            {
                nestedCode = operation switch
                {
                    "Accept" => session.Accept(Batch(session, proof, 2, Record(3, 99, 99))),
                    "Complete" => session.Complete(session.Generation, proof),
                    _ => session.Restart(session.Generation, proof)
                };
                Check(session.Report == null && session.AcceptedWork == 1,
                    "nested mutation cannot publish or charge work while another batch is being copied");
            }
            if (location == "Count") incoming.OnCount = () => { Reenter(); return 1; };
            else incoming.OnIndex = _ => Reenter();
            session.Accept(new(session.Generation, 2, proof.SourceStamp, proof.MapStamp, proof.ValidBytes, incoming));
            Check(nestedCode == "BatchInProgress" && session.State == NtfsReadSessionState.Collecting &&
                session.StagedCount == 2 && session.AcceptedWork == 2 && session.Report == null,
                "nested accept complete or restart is fenced without corrupting the outer batch");
            session.Complete(session.Generation, proof);
            Check(session.Report is { Records: 2, KnownLogicalBytes: 3, KnownAllocatedBytes: 4 },
                "only the outer batch becomes visible after a fenced reentrant operation");
        }
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            string? nested = null;
            var renewed = proof with { SourceStamp = proof.SourceStamp + 1, MapStamp = proof.MapStamp + 1 };
            var incoming = new CallbackRecords([Record(2)])
            {
                OnIndex = _ =>
                {
                    int current = session.Generation;
                    session.Notify(current, NtfsReadChange.MapMoved);
                    nested = session.Restart(current, renewed);
                }
            };
            session.Accept(new(session.Generation, 2, proof.SourceStamp, proof.MapStamp, proof.ValidBytes, incoming));
            Check(nested == "BatchInProgress" && session.Generation == 1 && session.State == NtfsReadSessionState.NeedsRestart &&
                session.AcceptedWork == 1 && session.StagedCount == 0,
                "reentrant notification may interrupt copying while restart must wait until the copy unwinds");
            session.Restart(session.Generation, renewed);
            Check(session.State == NtfsReadSessionState.Ready && session.Generation == 2,
                "renewal succeeds outside the interrupted collection callback");
        }

        // Every terminal outcome is sticky even for malformed late arguments.
        foreach (string ending in new[] { "Cancelled", "Completed", "Rejected" })
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            if (ending == "Cancelled") session.Cancel();
            else if (ending == "Completed") session.Complete(session.Generation, proof);
            else session.Accept(Batch(session, proof, 2, Record(2, -1, 0)));
            var terminal = Snapshot(session);
            session.Accept(null!);
            session.Notify(session.Generation, (NtfsReadChange)int.MaxValue);
            session.Restart(session.Generation, null!);
            session.Complete(session.Generation, null!);
            session.Cancel();
            Check(Snapshot(session) == terminal, "late callbacks and cancellation cannot overwrite a terminal outcome");
            if (ending == "Cancelled")
                Check(session.State == NtfsReadSessionState.Cancelled && session.StagedCount == 0 && session.Report == null,
                    "cancellation is an independent terminal state with no publishable staging");
        }

        // A few bounded, real-thread interleavings validate mutual exclusion;
        // these are correctness examples, not stress or scheduling benchmarks.
        void Race(Action first, Action second)
        {
            using var ready = new CountdownEvent(2);
            using var go = new ManualResetEventSlim();
            Exception? firstError = null, secondError = null;
            void Execute(Action action, Action<Exception> failed)
            {
                try
                {
                    ready.Signal();
                    if (!go.Wait(3000)) throw new InvalidOperationException("SyntheticRaceStartLimit");
                    action();
                }
                catch (Exception error) { failed(error); }
            }
            var left = new Thread(() => Execute(first, error => firstError = error)) { IsBackground = true };
            var right = new Thread(() => Execute(second, error => secondError = error)) { IsBackground = true };
            bool leftStarted = false, rightStarted = false, bothReady = false, leftDone = false, rightDone = false;
            try
            {
                left.Start(); leftStarted = true;
                right.Start(); rightStarted = true;
                bothReady = ready.Wait(3000);
            }
            finally
            {
                go.Set();
                if (leftStarted) leftDone = left.Join(5000);
                if (rightStarted) rightDone = right.Join(5000);
            }
            Check(bothReady && leftDone && rightDone && firstError == null && secondError == null,
                "bounded competing callbacks finish without an unhandled worker error");
        }
        for (int round = 0; round < 3; round++)
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            var first = Batch(session, proof, 1, Record(1, 7, 9));
            var second = Batch(session, proof, 1, Record(2, 11, 13));
            Race(() => session.Accept(first), () => session.Accept(second));
            Check(session.AcceptedWork == 1 && session.StagedCount == 0 && session.Report == null &&
                session.State == NtfsReadSessionState.NeedsRestart,
                "two threads submitting the same batch identifier cannot double-charge or mix accepted records");
            var candidate = Snapshot(session);
            session.Complete(session.Generation, proof);
            Check(Snapshot(session) == candidate,
                "completion after competing duplicate identifiers cannot publish the discarded generation");
        }
        for (int round = 0; round < 3; round++)
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1, 7, 9)));
            int generation = session.Generation;
            Race(() => session.Complete(generation, proof), () => session.Cancel());
            bool completed = session.State == NtfsReadSessionState.Completed && session.Report is
                { Records: 1, AcceptedWork: 1, KnownLogicalBytes: 7, KnownAllocatedBytes: 9 };
            bool cancelled = session.State == NtfsReadSessionState.Cancelled && session.Report == null && session.StagedCount == 0;
            Check((completed || cancelled) && session.AcceptedWork == 1,
                "completion and cancellation choose one legal terminal outcome without combining their states");
            var terminal = Snapshot(session);
            session.Accept(Batch(session, proof, 2, Record(2)));
            session.Complete(generation, proof);
            session.Cancel();
            Check(Snapshot(session) == terminal,
                "a terminal outcome selected by competing threads remains stable under late callbacks");
        }
        foreach (bool stopInCount in new[] { false, true })
        {
            var proof = Proof(); var session = new NtfsReadSession(proof);
            session.Accept(Batch(session, proof, 1, Record(1)));
            using var copying = new ManualResetEventSlim();
            using var releaseCopy = new ManualResetEventSlim();
            using var cancelStarting = new ManualResetEventSlim();
            Exception? copyError = null, cancelError = null;
            void PauseCopy()
            {
                copying.Set();
                if (!releaseCopy.Wait(5000)) throw new InvalidOperationException("SyntheticCopyBarrierLimit");
            }
            var records = new CallbackRecords([Record(2)]);
            if (stopInCount) records.OnCount = () => { PauseCopy(); return 1; };
            else records.OnIndex = _ => PauseCopy();
            var batch = new NtfsReadBatch(session.Generation, 2, proof.SourceStamp, proof.MapStamp, proof.ValidBytes, records);
            var reader = new Thread(() => { try { session.Accept(batch); } catch (Exception error) { copyError = error; } }) { IsBackground = true };
            var cancel = new Thread(() =>
            {
                try { cancelStarting.Set(); session.Cancel(); }
                catch (Exception error) { cancelError = error; }
            }) { IsBackground = true };
            bool readerStarted = false, cancelStarted = false, copyEntered = false, cancellationStarted = false;
            bool readerDone = false, cancelDone = false;
            try
            {
                reader.Start(); readerStarted = true;
                copyEntered = copying.Wait(3000);
                if (copyEntered)
                {
                    cancel.Start(); cancelStarted = true;
                    cancellationStarted = cancelStarting.Wait(3000);
                }
            }
            finally
            {
                releaseCopy.Set();
                if (readerStarted) readerDone = reader.Join(5000);
                if (cancelStarted) cancelDone = cancel.Join(5000);
            }
            Check(copyEntered && cancellationStarted && readerDone && cancelDone && copyError == null && cancelError == null,
                "copying and cancellation threads leave their bounded barriers and finish safely");
            Check(session.State == NtfsReadSessionState.Cancelled && session.Report == null && session.StagedCount == 0 &&
                session.AcceptedWork is 1 or 2,
                "another thread cancelling during collection copying eventually clears every staged record");
            var terminal = Snapshot(session);
            session.Accept(batch);
            session.Complete(session.Generation, proof);
            session.Notify(session.Generation, NtfsReadChange.MftGrew);
            Check(Snapshot(session) == terminal,
                "late collection completion and invalidation cannot revive a concurrently cancelled session");
        }
        return passed;
    }
}
