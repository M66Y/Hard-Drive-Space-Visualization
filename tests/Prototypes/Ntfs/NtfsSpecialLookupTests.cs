using DiskAtlas;

namespace DiskAtlas.NtfsPrototype;

// Synthetic callback evidence only. No native call or file-system access occurs.
internal static class NtfsSpecialLookupTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS special lookup check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS special lookup " + name);
        }
        const ulong volume = 51;
        ulong reference = NtfsRecordFixtures.Reference(64, 3), parent = NtfsRecordFixtures.Reference(5, 1);
        var record = new NtfsRecord(reference, 0, false, 1, [new(parent, "fixture.bin", 1)],
            17, null, NtfsLookupReason.Compressed);
        var item = new NtfsLookupItem(volume, record, @"C:\Synthetic\fixture.bin");
        var storage = new FileStorageInfo(new(volume, reference, 0), 24, 1, false, 17,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local));
        var success = new NtfsLeaseResult(true, storage, "SpecialLease", 1, 2);
        int calls = 0;
        NtfsLookupBatch Resolve(NtfsLeaseResult result, NtfsRecord? candidate = null) =>
            NtfsSizeLookup.ResolveSpecialWithLease([item with { Record = candidate ?? record }], (_, _) => { calls++; return result; });
        foreach (var reason in new[] { NtfsLookupReason.Compressed, NtfsLookupReason.Sparse })
        {
            calls = 0;
            var output = Resolve(success, record with { LookupReasons = reason });
            Check(output.Complete && calls == 1 && output.Metrics.NativeCalls == 1 && output.Metrics.EligibleObjects == 1 &&
                output.Metrics.ResolvedObjects == 1 && output.Metrics.FailedObjects == 0 && output.Metrics.UnsupportedObjects == 0 &&
                output.Items.Single().AllocatedBytes == 24 && output.Items.Single().AllocatedSource == NtfsSizeSource.HandleLookup &&
                output.Items.Single().LogicalSource == NtfsSizeSource.Record && output.Items.Single().Code == "SpecialLookup",
                "one eligible special callback resolves current allocation: " + reason);
        }
        Check(record.LookupReasons == NtfsLookupReason.Compressed && record.AllocatedBytes == null,
            "lookup leaves decoded source record unchanged");
        var direct = NtfsSizeLookup.ResolveSpecialWithLease([item with
        {
            Record = record with { LookupReasons = NtfsLookupReason.None, AllocatedBytes = 4096 }
        }], (_, _) => throw new InvalidOperationException("must not invoke direct record callback"));
        Check(direct.Complete && direct.Metrics.NativeCalls == 0 && direct.Metrics.EligibleObjects == 0 &&
            direct.Items.Single().AllocatedSource == NtfsSizeSource.Record && direct.Items.Single().AllocatedBytes == 4096,
            "known direct record sizes bypass native work");
        var zero = Resolve(success with { Storage = storage with { AllocatedBytes = 0 } }, record with { LookupReasons = NtfsLookupReason.Sparse });
        Check(zero.Complete && zero.Items.Single().AllocatedBytes == 0 && zero.Items.Single().AllocatedSource == NtfsSizeSource.HandleLookup,
            "measured zero allocation does not become unknown");
        var larger = Resolve(success with { Storage = storage with { AllocatedBytes = 4096 } });
        Check(larger.Complete && larger.Items.Single().AllocatedBytes!.Value > larger.Items.Single().LogicalBytes!.Value,
            "allocation may legitimately exceed the logical stream length");
        Check(double.IsFinite(larger.Metrics.ElapsedMilliseconds) && larger.Metrics.ElapsedMilliseconds >= 0 &&
            double.IsFinite(larger.Metrics.NativeMilliseconds) && larger.Metrics.NativeMilliseconds >= 0,
            "elapsed fields are finite callback timing rather than physical IO counts");
        var privateCode = Resolve(success with { Code = "synthetic private callback text" });
        Check(privateCode.Complete && privateCode.Items.Single().Code == "SpecialLookup",
            "result code never relays arbitrary callback text");

        void Unsupported(NtfsRecord candidate, string name, string? path = null)
        {
            calls = 0;
            var output = NtfsSizeLookup.ResolveSpecialWithLease([item with { Record = candidate, Path = path ?? item.Path }],
                (_, _) => { calls++; return success; });
            Check(!output.Complete && calls == 0 && output.Metrics.NativeCalls == 0 && output.Metrics.EligibleObjects == 0 &&
                output.Metrics.UnsupportedObjects == 1 && output.Items.Single().AllocatedBytes == null &&
                output.Items.Single().AllocatedSource == NtfsSizeSource.Unknown, name);
        }
        foreach (var reason in Enum.GetValues<NtfsLookupReason>().Where(reason => reason is not (NtfsLookupReason.Compressed or NtfsLookupReason.Sparse)))
            Unsupported(record with { LookupReasons = reason }, "unsupported single reason never invokes callback: " + reason);
        foreach (var reason in new[] { NtfsLookupReason.Compressed | NtfsLookupReason.Sparse,
            NtfsLookupReason.Compressed | NtfsLookupReason.NamedData, NtfsLookupReason.Sparse | NtfsLookupReason.NamedData,
            NtfsLookupReason.Compressed | NtfsLookupReason.ResidentAllocation, NtfsLookupReason.Sparse | NtfsLookupReason.AttributeList,
            NtfsLookupReason.Compressed | NtfsLookupReason.ExtensionRecord, NtfsLookupReason.Sparse | NtfsLookupReason.Encrypted })
            Unsupported(record with { LookupReasons = reason }, "combined reason remains unknown without native work");
        Unsupported(record with { LookupReasons = (NtfsLookupReason)(1 << 20) }, "unknown reason bit does not broaden support");
        Unsupported(record with { IsDirectory = true }, "directory is not a special file lookup");
        Unsupported(record with { BaseReference = 1 }, "extension base is rejected");
        Unsupported(record with { Reference = 64 }, "missing sequence is rejected");
        Unsupported(record with { LogicalBytes = null }, "missing logical length is not silently zero");
        Unsupported(record with { LogicalBytes = -1 }, "negative logical length is rejected");
        Unsupported(record with { AllocatedBytes = 0 }, "special lookup does not overwrite known allocation");
        Unsupported(record with { Names = [] }, "missing names remain unsupported");
        Unsupported(record with { LinkCount = 2 }, "incomplete raw name set remains unsupported");
        Unsupported(record, "relative path is rejected by special preflight", "fixture.bin");
        Unsupported(record, "empty path is rejected", " ");
        Unsupported(record, "wrong basename is rejected by special preflight", @"C:\Synthetic\other.bin");
        Unsupported(record with { Names = [new(5, "fixture.bin", 1)] }, "unbound parent sequence is rejected before callback");
        Unsupported(record with { Names = [new(parent, "fixture.bin", 4)] }, "unknown filename namespace is rejected before callback");
        foreach (var reason in new[] { NtfsLookupReason.Compressed, NtfsLookupReason.Sparse })
        {
            calls = 0;
            var old = NtfsSizeLookup.ResolveWithLease([item with { Record = record with { LookupReasons = reason } }],
                (_, _) => { calls++; return success; });
            Check(!old.Complete && calls == 0 && old.Metrics.UnsupportedObjects == 1,
                "existing resident-only entry retains its contract: " + reason);
        }

        void Failed(NtfsLeaseResult result, string name)
        {
            calls = 0;
            var output = Resolve(result);
            Check(!output.Complete && calls == 1 && output.Metrics.NativeCalls == 1 && output.Metrics.FailedObjects == 1 &&
                output.Metrics.ResolvedObjects == 0 && output.Items.Single().AllocatedBytes == null &&
                output.Items.Single().AllocatedSource == NtfsSizeSource.Unknown && output.Items.Single().LogicalBytes == 17, name);
        }
        Failed(success with { Success = false }, "callback failure flag cannot publish accompanying storage");
        Failed(success with { Storage = null }, "missing storage remains unknown");
        Failed(success with { HandlesOpened = 0 }, "zero successful handles cannot claim a lease");
        Failed(success with { HandlesOpened = 2 }, "two handles violate the single-handle contract");
        Failed(success with { SnapshotReads = 1 }, "one snapshot is insufficient");
        Failed(success with { SnapshotReads = 3 }, "unexpected snapshot count is rejected");
        Failed(success with { Storage = storage with { Identity = storage.Identity with { Volume = volume + 1 } } }, "callback must match full volume identity");
        Failed(success with { Storage = storage with { Identity = storage.Identity with { Low = reference + 1 } } }, "callback must match record slot");
        Failed(success with { Storage = storage with { Identity = storage.Identity with { Low = NtfsRecordFixtures.Reference(64, 4) } } }, "callback must match reused sequence");
        Failed(success with { Storage = storage with { Identity = storage.Identity with { High = 1 } } }, "callback must match high identity bits");
        Failed(success with { Storage = storage with { LogicalBytes = 18 } }, "callback EOF must match decoded evidence");
        Failed(success with { Storage = storage with { AllocatedBytes = -1 } }, "negative callback allocation remains unknown");
        Failed(success with { Storage = storage with { LinkCount = 2 } }, "unexpected native links remain unknown");
        Failed(success with { Storage = storage with { Modified = null } }, "missing callback timestamp remains unknown");
        Failed(success with { Storage = storage with { WritableHandleOpen = true } }, "writer evidence cannot claim stable allocation");
        foreach (var error in new Exception[] { new IOException("synthetic private exception"), new UnauthorizedAccessException("synthetic private exception") })
        {
            var failure = NtfsSizeLookup.ResolveSpecialWithLease([item], (_, _) => throw error);
            Check(!failure.Complete && failure.Metrics.FailedObjects == 1 && failure.Items.Single().Code == "LeaseReadFailed" &&
                failure.Items.Single().AllocatedBytes == null, "expected reader errors retain fixed path-free failure code");
        }
        bool unexpected = false;
        try { NtfsSizeLookup.ResolveSpecialWithLease([item], (_, _) => throw new InvalidOperationException("synthetic")); }
        catch (InvalidOperationException) { unexpected = true; }
        Check(unexpected, "unexpected callback programming error is not hidden");

        var second = item with { Record = record with { Reference = NtfsRecordFixtures.Reference(65, 3) }, Path = @"C:\Synthetic\second\fixture.bin" };
        void Rejected(IReadOnlyList<NtfsLookupItem> inputs, string code, string name, int limit = NtfsSizeLookup.MaximumObjects)
        {
            calls = 0;
            var output = NtfsSizeLookup.ResolveSpecialWithLease(inputs, (_, _) => { calls++; return success; }, limit: limit);
            Check(!output.Complete && output.Code == code && output.Items.Count == 0 && calls == 0 && output.Metrics.NativeCalls == 0, name);
        }
        foreach (int limit in new[] { 0, -1, NtfsSizeLookup.MaximumObjects + 1 })
            Rejected([item], "RecordLimit", "invalid configured capacity is rejected before native work", limit);
        Rejected([item, second], "RecordLimit", "caller capacity rejects whole batch", 1);
        Rejected(Enumerable.Repeat(item, NtfsSizeLookup.MaximumObjects + 1).ToArray(), "RecordLimit", "object capacity preempts any queued callback");
        Rejected([item, item], "DuplicateObject", "duplicate complete identity preempts native work");
        Rejected([item, second with { Record = record with { Reference = NtfsRecordFixtures.Reference(64, 4) } }], "SlotReused", "two sequences for one volume slot reject whole batch");
        Rejected([item, second with { Path = item.Path }], "PathCollision", "one path cannot stand for two object identities");
        Rejected([item with { Record = record with { LinkCount = 257, Names = Enumerable.Repeat(record.Names[0], 257).ToArray() } }],
            "NameLimit", "oversized name set is rejected before callback");
        var names = record with { LinkCount = 2, Names = [record.Names[0], new(parent, "FIXTUR~1.BIN", 2)] };
        Check(Resolve(success, names).Complete, "DOS alias does not inflate the visible callback link count");
        var hardLinks = record with { LinkCount = 2, Names = [record.Names[0], new(parent, "alias.bin", 1)] };
        Check(Resolve(success with { Storage = storage with { LinkCount = 2 } }, hardLinks).Complete, "multiple visible links require matching native count");
        var otherVolume = second with { Volume = volume + 1, Record = record };
        var distinctVolumes = NtfsSizeLookup.ResolveSpecialWithLease([item, otherVolume], (input, _) =>
            success with { Storage = storage with { Identity = new(input.Volume, input.Record.Reference, 0) } });
        Check(distinctVolumes.Complete && distinctVolumes.Metrics.NativeCalls == 2, "volume identity participates in object and slot uniqueness");
        var maximum = Enumerable.Range(0, NtfsSizeLookup.MaximumObjects).Select(index => item with
        {
            Record = record with { Reference = NtfsRecordFixtures.Reference((ulong)index + 100, 3) },
            Path = @"C:\Synthetic\entry-" + index + @"\fixture.bin"
        }).ToArray();
        var bounded = NtfsSizeLookup.ResolveSpecialWithLease(maximum, (input, _) => success with
        { Storage = storage with { Identity = new(input.Volume, input.Record.Reference, 0) } });
        Check(bounded.Complete && bounded.Items.Count == NtfsSizeLookup.MaximumObjects && bounded.Metrics.NativeCalls == NtfsSizeLookup.MaximumObjects,
            "maximum unique batch resolves without a queue or extra callbacks");
        var empty = NtfsSizeLookup.ResolveSpecialWithLease([], (_, _) => throw new InvalidOperationException("must not read empty batch"));
        Check(!empty.Complete && empty.Items.Count == 0 && empty.Metrics.NativeCalls == 0, "empty batch does not claim full completion");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); calls = 0; bool stopped = false;
            try { NtfsSizeLookup.ResolveSpecialWithLease([item], (_, _) => { calls++; return success; }, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && calls == 0, "pre-cancellation performs no callback");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            calls = 0; NtfsLookupBatch? published = null; bool stopped = false;
            try
            {
                published = NtfsSizeLookup.ResolveSpecialWithLease([item, second], (input, _) =>
                {
                    calls++;
                    if (calls == 2) cancelled.Cancel();
                    return success with { Storage = storage with { Identity = new(input.Volume, input.Record.Reference, 0) } };
                }, cancelled.Token);
            }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && calls == 2 && published == null, "mid-batch cancellation publishes no partial result");
        }
        bool callbackCancelled = false;
        try { NtfsSizeLookup.ResolveSpecialWithLease([item], (_, _) => throw new OperationCanceledException()); }
        catch (OperationCanceledException) { callbackCancelled = true; }
        Check(callbackCancelled, "callback cancellation is not swallowed as unknown");
        calls = 0;
        var initial = NtfsSizeLookup.ResolveSpecialWithLease([item], (_, _) => { calls++; return success; });
        var fresh = NtfsSizeLookup.ResolveSpecialWithLease([item], (_, _) => { calls++; return success with { Storage = storage with { AllocatedBytes = 8192 } }; });
        Check(initial.Complete && fresh.Complete && calls == 2 && initial.Items.Single().AllocatedBytes == 24 && fresh.Items.Single().AllocatedBytes == 8192,
            "separate batches never reuse prior allocation observations");
        Console.WriteLine("PASS NTFS special lookup checks: " + passed + "; native callbacks: synthetic only");
        return passed;
    }
}
