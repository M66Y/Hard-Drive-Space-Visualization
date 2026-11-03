using DiskAtlas;
using System.Buffers.Binary;

internal static class JournalRegression
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action, JournalState state)
    {
        try { action(); throw new InvalidOperationException("Expected safe journal rejection."); }
        catch (JournalException ex) { Check(ex.Status.State == state, "Wrong rejection category."); }
    }
    internal static byte[] Header(ulong id = 9, long next = 20, long first = 0)
    {
        var b = new byte[56]; BinaryPrimitives.WriteUInt64LittleEndian(b, id);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), first);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16), next); return b;
    }
    internal static byte[] Record(long usn, int version = 2, ulong low = 123, ulong parent = 456)
    {
        var b = new byte[version == 2 ? 64 : 80]; BinaryPrimitives.WriteInt32LittleEndian(b, b.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)version);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), low);
        if (version == 2) BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), parent);
        else { BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), 8); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), parent); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), 7); }
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(version == 2 ? 24 : 40), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(version == 2 ? 40 : 56), 2); return b;
    }
    internal static JournalIoResult Reply(byte[] output, long next, params byte[][] records)
    {
        BinaryPrimitives.WriteInt64LittleEndian(output, next); int offset = 8;
        foreach (var record in records) { record.CopyTo(output, offset); offset += record.Length; }
        return new(true, offset);
    }
    private static JournalIoResult QueryReply(byte[] output, ulong id = 9, long next = 20, long first = 0)
    { var b = Header(id, next, first); b.CopyTo(output, 0); return new(true, b.Length); }

    public static int Run()
    {
        string parent = Path.GetFullPath("artifacts"), root = Path.Combine(parent, "journal-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); int passed = 0;
        void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
        try
        {
            Test("V1 request advertises supported formats and does not wait for close or future data", () =>
            {
                var b = NtfsChangeJournal.CreateReadInput(10, 9, true);
                Check(b.Length == 48 && BinaryPrimitives.ReadInt64LittleEndian(b) == 10 && BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(32)) == 9, "Wrong V1 layout.");
                Check(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)) == uint.MaxValue && b.AsSpan(12, 20).IndexOfAnyExcept((byte)0) == -1, "Request hides open writers or blocks at EOF.");
                Check(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(40)) == 2 && BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(42)) == 3, "Wrong accepted record versions.");
                Check(NtfsChangeJournal.CreateReadInput(10, 9, false).Length == 40, "V0 fallback layout missing.");
            });
            Test("native diagnostics keep categories and numeric errors without exception paths", () =>
            {
                Check(JournalStatus.FromNative(5).State == JournalState.AccessDenied && JournalStatus.FromNative(1).State == JournalState.UnsupportedOperation, "Native errors collapsed.");
                Check(JournalStatus.FromNative(1179).State == JournalState.NotActive && JournalStatus.FromNative(1181).State == JournalState.Discontinuous, "Journal lifecycle errors collapsed.");
                var s = new JournalStatus(JournalState.UnsupportedOperation, 1, 5);
                Check(s.Description.Contains("1") && s.Description.Contains("5") && !s.Description.Contains(Path.DirectorySeparatorChar), "Public diagnostic leaks paths or drops attempts.");
            });
            Test("query failures and invalid headers cannot create a usable checkpoint", () =>
            {
                using var denied = new NtfsChangeJournal((_, _, _) => new(false, 0, 5));
                Reject(() => denied.Capture(), JournalState.AccessDenied);
                using var shortHeader = new NtfsChangeJournal((_, _, _) => new(true, 8));
                Reject(() => shortHeader.Capture(), JournalState.InvalidData);
                using var backwards = new NtfsChangeJournal((_, _, output) => QueryReply(output, next: 2, first: 3));
                Reject(() => backwards.Capture(), JournalState.InvalidData);
            });
            Test("V1 unprivileged read decodes 128-bit IDs and clips to the captured interval", () =>
            {
                using var journal = new NtfsChangeJournal((code, input, output) =>
                {
                    if (code == NtfsChangeJournal.QueryControl) return QueryReply(output, next: 22);
                    Check(code == NtfsChangeJournal.UnprivilegedReadControl && input?.Length == 48, "V1 not attempted first.");
                    return Reply(output, 22, Record(11, 3), Record(20));
                }, 12);
                var records = journal.Read(new(9, 10, 0, 12), new(9, 20, 0, 12), CancellationToken.None);
                Check(records.Count == 1 && records[0].IdHigh == 8 && records[0].ParentHigh == 7, "Bounds or V3 IDs lost.");
            });
            Test("V0 compatibility fallback reuses the successful reader across chunks", () =>
            {
                int failed = 0, reads = 0;
                using var journal = new NtfsChangeJournal((code, input, output) =>
                {
                    if (code == NtfsChangeJournal.QueryControl) return QueryReply(output);
                    if (input!.Length == 48) { failed++; return new(false, 0, 87); }
                    reads++; long cursor = BinaryPrimitives.ReadInt64LittleEndian(input);
                    return Reply(output, cursor == 10 ? 15 : 20, Record(cursor + 1));
                });
                Check(journal.Read(new(9, 10, 0), new(9, 20, 0), CancellationToken.None).Count == 2 && failed == 1 && reads == 2, "Reader negotiation repeated or skipped records.");
            });
            Test("standard read fallback stays on the existing handle and reports failures", () =>
            {
                using var journal = new NtfsChangeJournal((code, _, output) => code == NtfsChangeJournal.QueryControl ? QueryReply(output) :
                    code == NtfsChangeJournal.UnprivilegedReadControl ? new(false, 0, 1) : Reply(output, 20, Record(11)));
                Check(journal.Read(new(9, 10, 0), new(9, 20, 0), CancellationToken.None).Count == 1, "Standard fallback unavailable.");
                using var denied = new NtfsChangeJournal((_, _, _) => new(false, 0, 5));
                Reject(() => denied.Read(new(9, 10, 0), new(9, 20, 0), CancellationToken.None), JournalState.AccessDenied);
            });
            Test("journal gaps, rollback and volume replacement are rejected before reading", () =>
            {
                using var journal = new NtfsChangeJournal((_, _, _) => throw new InvalidOperationException("Read after invalid cursor."));
                foreach (var pair in new[] { (new JournalCheckpoint(8, 10, 0), new JournalCheckpoint(9, 20, 0)),
                    (new JournalCheckpoint(9, 10, 0), new JournalCheckpoint(9, 20, 11)),
                    (new JournalCheckpoint(9, 21, 0), new JournalCheckpoint(9, 20, 0)),
                    (new JournalCheckpoint(9, 10, 0, 12), new JournalCheckpoint(9, 20, 0, 13)) })
                    Reject(() => journal.Read(pair.Item1, pair.Item2, CancellationToken.None), JournalState.Discontinuous);
            });
            Test("stalled cursors and malformed or unknown records never become an empty change set", () =>
            {
                using var stalled = new NtfsChangeJournal((_, _, output) => Reply(output, 10));
                Reject(() => stalled.Read(new(9, 10, 0), new(9, 20, 0), CancellationToken.None), JournalState.InvalidData);
                foreach (var b in new[] { Record(11, 4), Record(-1), Record(21), Record(12).Concat(Record(11)).ToArray() })
                    Reject(() => NtfsChangeJournal.Parse(b, 20, [], 10, 20), JournalState.InvalidData);
                using var truncated = new NtfsChangeJournal((_, _, _) => new(true, 7));
                Reject(() => truncated.Read(new(9, 10, 0), new(9, 20, 0), CancellationToken.None), JournalState.InvalidData);
            });
            Test("post-read journal reset is detected and change lists stay bounded", () =>
            {
                using var journal = new NtfsChangeJournal((code, _, output) => code == NtfsChangeJournal.QueryControl ? QueryReply(output, id: 10) : Reply(output, 20, Record(11)));
                Reject(() => journal.Read(new(9, 10, 0), new(9, 20, 0), CancellationToken.None), JournalState.Discontinuous);
                var records = Enumerable.Repeat(default(JournalChange), NtfsChangeJournal.ChangeLimit).ToList();
                Reject(() => NtfsChangeJournal.Parse(Record(11), 20, records), JournalState.TooManyChanges);
            });
            Test("cancelled native reads do not return successful cached changes", () =>
            {
                using var token = new CancellationTokenSource();
                using var journal = new NtfsChangeJournal((_, _, output) => { token.Cancel(); return Reply(output, 20, Record(11)); });
                try { journal.Read(new(9, 10, 0), new(9, 20, 0), token.Token); throw new InvalidOperationException("Cancelled read returned success."); }
                catch (OperationCanceledException) { }
            });

            string scope = Path.Combine(root, "scope"); Directory.CreateDirectory(Path.Combine(scope, "nested"));
            File.WriteAllBytes(Path.Combine(scope, "nested", "sample.bin"), new byte[123]);
            var options = new ScanOptions(ScanMode.HighSpeed);
            var unavailable = new ScanCoordinator(_ => null);
            var baseline = unavailable.Scan(scope, null, options, null, CancellationToken.None);
            Test("capture errors use full metadata verification and are not mislabeled journal incrementals", () =>
            {
                var broken = new ControlledJournal { CaptureError = new JournalStatus(JournalState.UnsupportedOperation, 1, 5) };
                var after = new ScanCoordinator(_ => broken).Scan(scope, baseline, options, null, CancellationToken.None);
                Check(after.Statistics is { Method: RefreshMethod.MetadataVerification, VerifiedDirectories: 2, VerifiedFiles: 1 }, "Capture failure skipped verification.");
                Check(after.Statistics!.Journal is { NativeError: 1, ReadOnlyOpenError: 5 } && after.Checkpoint == null, "Failure details/checkpoint incorrect.");
                Check(RefreshPresentation.Summary(after.Statistics).StartsWith("元信息核验") && !RefreshPresentation.Summary(after.Statistics).Contains("日志增量"), "Reuse mislabeled as journal acceleration.");
            });
            Test("missing checkpoint verifies once, then continuous no-change journal avoids enumeration", () =>
            {
                var source = new ControlledJournal(); var coordinator = new ScanCoordinator(_ => source);
                var verified = coordinator.Scan(scope, baseline, options, null, CancellationToken.None);
                Check(verified.Statistics?.Method == RefreshMethod.MetadataVerification && verified.Checkpoint != null, "Missing baseline cursor ignored.");
                var fast = coordinator.Scan(scope, verified, options, null, CancellationToken.None);
                Check(fast.Statistics is { Method: RefreshMethod.ChangeJournal, VerifiedFiles: 0, ReadDirectories: 0, ReusedFiles: 1 }, "Continuous no-change refresh enumerated files.");
                Check(RefreshPresentation.Details(fast.Statistics!).Contains("处理记录 0"), "Change count missing.");
            });
            Test("read failure or lost continuity invalidates the plan and performs a full scan", () =>
            {
                var source = new ControlledJournal(); var coordinator = new ScanCoordinator(_ => source);
                var before = coordinator.Scan(scope, null, options, null, CancellationToken.None);
                source.ReadError = new(JournalState.Discontinuous);
                File.WriteAllBytes(Path.Combine(scope, "new.bin"), new byte[456]);
                var after = coordinator.Scan(scope, before, options, null, CancellationToken.None);
                Check(after.Statistics is { Method: RefreshMethod.FullScan, Incremental: false } && after.Root.Size == 579 && after.Statistics.Journal?.State == JournalState.Discontinuous, "Gap reused stale data.");
            });
            Test("foreign-volume checkpoint cannot authorize reuse", () =>
            {
                var source = new ControlledJournal(); var coordinator = new ScanCoordinator(_ => source);
                var before = coordinator.Scan(scope, null, options, null, CancellationToken.None);
                source.Volume = before.Root.Storage!.Identity.Volume ^ 1;
                var after = coordinator.Scan(scope, before, options, null, CancellationToken.None);
                Check(source.Reads == 0 && after.Statistics?.Method == RefreshMethod.MetadataVerification && after.Statistics.Journal?.State == JournalState.VolumeMismatch && after.Checkpoint == null, "Wrong volume authorized cached content.");
            });
            Test("unusable cached identities fall back without misreporting a journal read failure", () =>
            {
                var source = new ControlledJournal(); var coordinator = new ScanCoordinator(_ => source);
                var before = coordinator.Scan(scope, null, options, null, CancellationToken.None);
                before.Root.Descendants().First(e => !e.IsDirectory).Storage = null;
                var after = coordinator.Scan(scope, before, options, null, CancellationToken.None);
                Check(after.Statistics is { Method: RefreshMethod.FullScan, Journal.State: JournalState.Available } &&
                    after.Statistics.Reason.Contains("旧快照") && after.Root.Size == before.Root.Size,
                    "Snapshot failure was hidden as a journal read failure or reused.");
            });
            Test("root replacement cannot reuse old identities and force-full remains explicit", () =>
            {
                var source = new ControlledJournal(); var coordinator = new ScanCoordinator(_ => source);
                var before = coordinator.Scan(scope, null, options, null, CancellationToken.None);
                Directory.Move(scope, Path.Combine(root, "old-scope")); Directory.CreateDirectory(scope);
                File.WriteAllBytes(Path.Combine(scope, "replacement.bin"), new byte[17]);
                var after = coordinator.Scan(scope, before, options, null, CancellationToken.None);
                Check(after.Root.Size == 17 && after.Statistics?.Method == RefreshMethod.FullScan && source.Reads == 0, "Root replacement reused old tree.");
                var forced = coordinator.Scan(scope, after, options, null, CancellationToken.None, forceFull: true);
                Check(forced.Statistics?.Reason.Contains("强制") == true && forced.Statistics.Method == RefreshMethod.FullScan, "Forced full scan ignored.");
            });
            Console.WriteLine($"All {passed} journal checks passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL journal regression: " + ex.GetType().Name + " / " + ex.Message); return 1; }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar + "journal-tests-", StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup.");
            Directory.Delete(root, true);
        }
    }

    internal sealed class ControlledJournal : IChangeJournal
    {
        public JournalStatus? CaptureError, ReadError;
        public ulong? Volume;
        public int Reads;
        public JournalCheckpoint Capture() => CaptureError != null ? throw new JournalException(CaptureError) : new(9, 20, 0, Volume);
        public IReadOnlyList<JournalChange> Read(JournalCheckpoint from, JournalCheckpoint to, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Reads++; if (ReadError != null) throw new JournalException(ReadError); return []; }
        public void Dispose() { }
    }
}
