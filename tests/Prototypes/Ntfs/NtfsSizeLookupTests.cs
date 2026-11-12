using DiskAtlas;

namespace DiskAtlas.NtfsPrototype;

// In-memory observations only: no paths are opened and no native API is called.
internal static class NtfsSizeLookupTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS lookup check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS lookup " + name);
        }
        const ulong volume = 31;
        ulong reference = NtfsRecordFixtures.Reference(42, 3);
        ulong parent = NtfsRecordFixtures.Reference(5, 7);
        var record = new NtfsRecord(reference, 0, false, 1, [new(parent, "fixture.bin", 1)],
            17, null, NtfsLookupReason.ResidentAllocation);
        var item = new NtfsLookupItem(volume, record, "fixture.bin");
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var native = new FileStorageInfo(new(volume, reference, 0), 24, 1, false, 17, stamp);

        NtfsLookupBatch Resolve(NtfsRecord candidate, Func<string, CancellationToken, FileStorageInfo?> reader) =>
            NtfsSizeLookup.Resolve([item with { Record = candidate }], reader);
        void FailedSample(FileStorageInfo? sample, string name, bool second = false)
        {
            int calls = 0;
            var result = Resolve(record, (_, _) => ++calls == 1 && second ? native : sample);
            var output = result.Items.Single();
            Check(!result.Complete && output.LogicalBytes == 17 && output.LogicalSource == NtfsSizeSource.Record &&
                output.AllocatedBytes is null && output.AllocatedSource == NtfsSizeSource.Unknown &&
                result.Metrics.EligibleObjects == 1 && result.Metrics.FailedObjects == 1 &&
                result.Metrics.ResolvedObjects == 0 && result.Metrics.NativeCalls == calls && calls == (second ? 2 : 1), name);
        }
        void Unsupported(NtfsRecord candidate, string name)
        {
            int calls = 0;
            var result = Resolve(candidate, (_, _) => { calls++; return native; });
            Check(!result.Complete && calls == 0 && result.Metrics.UnsupportedObjects == 1 &&
                result.Metrics.EligibleObjects == 0 && result.Items.Single().AllocatedBytes is null &&
                result.Items.Single().AllocatedSource == NtfsSizeSource.Unknown, name);
        }

        int readCalls = 0;
        var completeRecord = record with { AllocatedBytes = 4096, LookupReasons = NtfsLookupReason.None };
        var direct = Resolve(completeRecord, (_, _) => { readCalls++; return native; });
        Check(direct.Complete && readCalls == 0 && direct.Metrics.NativeCalls == 0 && direct.Metrics.EligibleObjects == 0 &&
            direct.Metrics.ResolvedObjects == 0 && direct.Items.Single().AllocatedSource == NtfsSizeSource.Record &&
            direct.Items.Single().AllocatedBytes == 4096, "known record sizes avoid native lookup");
        var resident = Resolve(record, (_, _) => { readCalls++; return native; });
        Check(resident.Complete && readCalls == 2 && resident.Metrics.NativeCalls == 2 && resident.Metrics.ResolvedObjects == 1 &&
            resident.Metrics.EligibleObjects == 1 && resident.Items.Single().AllocatedBytes == 24 &&
            resident.Items.Single().LogicalSource == NtfsSizeSource.Record && resident.Items.Single().AllocatedSource == NtfsSizeSource.HandleLookup,
            "resident allocation requires two stable native observations");
        Check(record.AllocatedBytes is null && record.LookupReasons == NtfsLookupReason.ResidentAllocation,
            "lookup does not rewrite decoded source evidence");
        Check(double.IsFinite(resident.Metrics.ElapsedMilliseconds) && resident.Metrics.ElapsedMilliseconds >= 0 &&
            double.IsFinite(resident.Metrics.NativeMilliseconds) && resident.Metrics.NativeMilliseconds >= 0,
            "timing fields are finite and separate from call counts");
        var zero = Resolve(record, (_, _) => native with { AllocatedBytes = 0 });
        Check(zero.Complete && zero.Items.Single().AllocatedBytes == 0 && zero.Items.Single().AllocatedSource == NtfsSizeSource.HandleLookup,
            "native zero allocation is distinct from unknown");
        var rawNames = record with { LinkCount = 2, Names = [new(parent, "fixture.bin", 1), new(parent, "FIXTUR~1.BIN", 2)] };
        Check(Resolve(rawNames, (_, _) => native).Complete, "DOS alias does not increase native visible link count");
        var twoLinks = record with { LinkCount = 3, Names = [new(parent, "fixture.bin", 1), new(parent, "second.bin", 1), new(parent, "FIXTUR~1.BIN", 2)] };
        Check(Resolve(twoLinks, (_, _) => native with { LinkCount = 2 }).Complete, "two visible hard links are validated separately from raw references");

        FailedSample(null, "first read failure preserves unknown allocation");
        FailedSample(null, "second read failure rejects first sample", second: true);
        FailedSample(native with { Identity = native.Identity with { Volume = volume + 1 } }, "wrong volume rejected");
        FailedSample(native with { Identity = native.Identity with { Low = reference + 1 } }, "wrong file slot rejected");
        FailedSample(native with { Identity = native.Identity with { Low = NtfsRecordFixtures.Reference(42, 4) } }, "reused file sequence rejected");
        FailedSample(native with { Identity = native.Identity with { High = 1 } }, "unexpected high identity rejected");
        FailedSample(native with { Identity = native.Identity with { Low = reference + 1 } }, "identity change between samples rejected", second: true);
        FailedSample(native with { LogicalBytes = 18 }, "logical mismatch against MFT record rejected");
        FailedSample(native with { LogicalBytes = 18 }, "logical change between reads rejected", second: true);
        FailedSample(native with { AllocatedBytes = -1 }, "negative allocation rejected");
        FailedSample(native with { AllocatedBytes = 32 }, "allocation change between reads rejected", second: true);
        FailedSample(native with { LinkCount = 2 }, "visible link count mismatch rejected");
        FailedSample(native with { LinkCount = 2 }, "link count change between reads rejected", second: true);
        FailedSample(native with { Modified = null }, "missing modification timestamp rejected");
        FailedSample(native with { Modified = stamp.AddTicks(1) }, "timestamp change between reads rejected", second: true);
        FailedSample(native with { WritableHandleOpen = true }, "already open writer rejected");
        FailedSample(native with { WritableHandleOpen = true }, "writer opened between observations rejected", second: true);
        foreach (Exception error in new Exception[] { new IOException("private synthetic message"), new UnauthorizedAccessException("private synthetic message") })
        {
            var failure = Resolve(record, (_, _) => throw error);
            Check(!failure.Complete && failure.Metrics.FailedObjects == 1 && failure.Items.Single().Code == "ReadFailed" &&
                !failure.Items.Single().Code.Contains("private", StringComparison.Ordinal), "expected read exception has fixed path-free result");
        }
        bool unexpected = false;
        try { Resolve(record, (_, _) => throw new InvalidOperationException("synthetic unexpected exception")); }
        catch (InvalidOperationException) { unexpected = true; }
        Check(unexpected, "unexpected callback failure propagates instead of becoming success");
        bool nativeCancelled = false;
        try { Resolve(record, (_, _) => throw new OperationCanceledException()); }
        catch (OperationCanceledException) { nativeCancelled = true; }
        Check(nativeCancelled, "callback cancellation is not swallowed as read failure");

        foreach (var reason in new[] { NtfsLookupReason.Compressed, NtfsLookupReason.Sparse, NtfsLookupReason.AttributeList,
            NtfsLookupReason.NamedData, NtfsLookupReason.ReparsePoint, NtfsLookupReason.Encrypted, NtfsLookupReason.LinkNamesIncomplete,
            NtfsLookupReason.MissingData, NtfsLookupReason.MissingStandardInformation,
            NtfsLookupReason.ResidentAllocation | NtfsLookupReason.NamedData })
            Unsupported(record with { LookupReasons = reason }, "unsupported reason does not launch native work: " + reason);
        Unsupported(record with { IsDirectory = true }, "directory record does not use resident file lookup");
        Unsupported(record with { BaseReference = NtfsRecordFixtures.Reference(77, 2) }, "extension record cannot resolve as standalone file");
        Unsupported(record with { Reference = 42 }, "missing sequence rejected before native work");
        Unsupported(record with { Names = [] }, "missing visible names rejected before native work");
        Unsupported(record with { LinkCount = 2 }, "incomplete raw name references rejected before native work");
        Unsupported(record with { LogicalBytes = -1 }, "negative decoded logical size cannot be repaired implicitly");
        Unsupported(record with { LogicalBytes = null }, "unknown decoded logical size stays unsupported");
        Unsupported(record with { AllocatedBytes = 24 }, "inconsistent resident lookup flag does not override known evidence");
        var noPath = NtfsSizeLookup.Resolve([item with { Path = " " }], (_, _) => throw new InvalidOperationException("must not read"));
        Check(!noPath.Complete && noPath.Metrics.NativeCalls == 0 && noPath.Metrics.UnsupportedObjects == 1, "empty lookup path rejected");

        readCalls = 0;
        var duplicate = NtfsSizeLookup.Resolve([item, item with { Path = "another.bin" }], (_, _) => { readCalls++; return native; });
        Check(!duplicate.Complete && duplicate.Code == "DuplicateObject" && duplicate.Items.Count == 0 && readCalls == 0,
            "duplicate object batch rejected before any native work");
        foreach (int limit in new[] { 0, -1, 4097 })
        {
            var oversized = NtfsSizeLookup.Resolve([item], (_, _) => throw new InvalidOperationException("must not read"), limit: limit);
            Check(!oversized.Complete && oversized.Code == "RecordLimit" && oversized.Items.Count == 0 && oversized.Metrics.NativeCalls == 0,
                "invalid batch budget rejected before native work");
        }
        var reusedSlot = item with { Record = record with { Reference = NtfsRecordFixtures.Reference(42, 4) }, Path = "reused.bin" };
        var reusedBatch = NtfsSizeLookup.Resolve([item, reusedSlot], (_, _) => throw new InvalidOperationException("must not read"));
        Check(!reusedBatch.Complete && reusedBatch.Code == "SlotReused" && reusedBatch.Items.Count == 0 && reusedBatch.Metrics.NativeCalls == 0,
            "two sequences for one slot invalidate batch before native work");
        var pathCollision = item with { Record = record with { Reference = NtfsRecordFixtures.Reference(43, 3) } };
        var collisionBatch = NtfsSizeLookup.Resolve([item, pathCollision], (_, _) => throw new InvalidOperationException("must not read"));
        Check(!collisionBatch.Complete && collisionBatch.Code == "PathCollision" && collisionBatch.Items.Count == 0 && collisionBatch.Metrics.NativeCalls == 0,
            "two identities for one path invalidate batch before native work");
        var manyNames = record with { Names = Enumerable.Range(0, 257).Select(i => new NtfsName(parent, "name" + i, 1)).ToArray() };
        var oversizedNames = Resolve(manyNames, (_, _) => throw new InvalidOperationException("must not read"));
        Check(!oversizedNames.Complete && oversizedNames.Code == "NameLimit" && oversizedNames.Items.Count == 0 && oversizedNames.Metrics.NativeCalls == 0,
            "name budget rejects oversized record before native work");
        var secondItem = item with { Record = record with { Reference = NtfsRecordFixtures.Reference(43, 3) }, Path = "second.bin" };
        var overCount = NtfsSizeLookup.Resolve([item, secondItem], (_, _) => throw new InvalidOperationException("must not read"), limit: 1);
        Check(!overCount.Complete && overCount.Code == "RecordLimit" && overCount.Items.Count == 0, "object count cannot exceed caller budget");
        var sameSlotOtherVolume = item with { Volume = volume + 1, Path = "second-volume.bin" };
        var volumes = NtfsSizeLookup.Resolve([item, sameSlotOtherVolume], (path, _) => path == "second-volume.bin"
            ? native with { Identity = native.Identity with { Volume = volume + 1 } } : native);
        Check(volumes.Complete && volumes.Metrics.NativeCalls == 4 && volumes.Metrics.ResolvedObjects == 2, "volume participates in object uniqueness");
        var empty = NtfsSizeLookup.Resolve([], (_, _) => throw new InvalidOperationException("must not read"));
        Check(!empty.Complete && empty.Items.Count == 0 && empty.Metrics.NativeCalls == 0, "empty batch does not claim completed analysis");
        readCalls = 0;
        Resolve(record, (_, _) => { readCalls++; return native; });
        var nextBatch = Resolve(record, (_, _) => { readCalls++; return native with { AllocatedBytes = 32 }; });
        Check(readCalls == 4 && nextBatch.Items.Single().AllocatedBytes == 32, "separate batches do not reuse stale cached allocation");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool stopped = false; readCalls = 0;
            try { NtfsSizeLookup.Resolve([item], (_, _) => { readCalls++; return native; }, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && readCalls == 0, "pre-cancelled batch publishes no results and performs no native calls");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            bool stopped = false; readCalls = 0;
            try
            {
                NtfsSizeLookup.Resolve([item], (_, _) => { readCalls++; cancelled.Cancel(); return native; }, cancelled.Token);
            }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && readCalls == 1, "cancellation after first read does not publish a one-sample result");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            bool stopped = false; readCalls = 0;
            try
            {
                NtfsSizeLookup.Resolve([item, secondItem], (_, _) =>
                {
                    readCalls++;
                    if (readCalls == 3) cancelled.Cancel();
                    return native;
                }, cancelled.Token);
            }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && readCalls == 3, "mid-batch cancellation discards earlier resolved objects");
        }
        Console.WriteLine("PASS NTFS resident lookup checks: " + passed + "; native callbacks: synthetic only");
        return passed;
    }
}
