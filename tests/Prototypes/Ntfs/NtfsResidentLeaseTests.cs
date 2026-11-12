using System.Runtime.InteropServices;
using DiskAtlas;

namespace DiskAtlas.NtfsPrototype;

// Only newly created files below this run's random temporary directory are opened.
// These tests do not open a volume or claim to establish MFT residency.
internal static class NtfsResidentLeaseTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string name, string existing, IntPtr security);

    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS resident lease check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS resident lease " + name);
        }
        var parent = NtfsRecordFixtures.Reference(5, 1);
        var synthetic = new NtfsRecord(NtfsRecordFixtures.Reference(42, 3), 0, false, 1,
            [new(parent, "lease.bin", 1)], 17, null, NtfsLookupReason.ResidentAllocation);
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        string root = Path.GetFullPath(Path.Combine(tempRoot, "AsukaLens-resident-lease-" + Guid.NewGuid().ToString("N")));
        string file = Path.Combine(root, "lease.bin");

        void RejectedBeforeOpen(NtfsRecord record, string name, string? suppliedPath = null)
        {
            var result = NtfsResidentLease.Read(1, record, suppliedPath ?? file);
            Check(!result.Success && result.Storage == null && result.HandlesOpened == 0 && result.SnapshotReads == 0,
                name);
        }
        foreach (var reason in Enum.GetValues<NtfsLookupReason>().Where(reason => reason != NtfsLookupReason.ResidentAllocation))
            RejectedBeforeOpen(synthetic with { LookupReasons = reason }, "unsupported reason skips native work: " + reason);
        RejectedBeforeOpen(synthetic with { LookupReasons = NtfsLookupReason.ResidentAllocation | NtfsLookupReason.NamedData },
            "combined lookup reasons skip native work");
        RejectedBeforeOpen(synthetic with { IsDirectory = true }, "directory record skips native work");
        RejectedBeforeOpen(synthetic with { BaseReference = 1 }, "extension record skips native work");
        RejectedBeforeOpen(synthetic with { Reference = 42 }, "missing sequence skips native work");
        RejectedBeforeOpen(synthetic with { LogicalBytes = -1 }, "negative logical length skips native work");
        RejectedBeforeOpen(synthetic with { LogicalBytes = null }, "unknown logical length skips native work");
        RejectedBeforeOpen(synthetic with { AllocatedBytes = 0 }, "already known allocation skips native work");
        RejectedBeforeOpen(synthetic with { LinkCount = 2 }, "incomplete raw names skip native work");
        RejectedBeforeOpen(synthetic with { Names = [] }, "missing names skip native work");
        RejectedBeforeOpen(synthetic with { Names = [new(parent, "DOSNAM~1.BIN", 2)] }, "DOS-only names skip native work");
        foreach (string name in new[] { "", "bad/name", "bad\\name", "bad:name", "bad\0name", new string('x', 256) })
            RejectedBeforeOpen(synthetic with { Names = [new(parent, name, 1)] }, "invalid name skips native work");
        RejectedBeforeOpen(synthetic with { Names = [new(5, "lease.bin", 1)] }, "unbound parent name skips native work");
        RejectedBeforeOpen(synthetic with { Names = [new(parent, "lease.bin", 4)] }, "unknown namespace skips native work");
        RejectedBeforeOpen(synthetic, "relative path skips native work", "lease.bin");
        RejectedBeforeOpen(synthetic, "empty path skips native work", " ");
        RejectedBeforeOpen(synthetic, "nul path skips native work", file + '\0');
        RejectedBeforeOpen(synthetic, "unrelated basename skips native work", Path.Combine(root, "other.bin"));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool stopped = false;
            try { NtfsResidentLease.Read(1, synthetic, file, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && !Directory.Exists(root), "pre-cancellation performs no file work");
        }
        passed += RunIntegrationChecks();

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP NTFS resident lease native checks: Windows required");
            return passed;
        }
        if (!string.Equals(new DriveInfo(Path.GetPathRoot(root)!).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("SKIP NTFS resident lease native checks: temporary directory is not on NTFS");
            return passed;
        }
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(file, new byte[17]);
            if (!NativeStorage.TryRead(file, false, File.GetAttributes(file), out var native, out _) ||
                native == null || native.Identity.High != 0 || (native.Identity.Low >> 48) == 0)
            {
                Console.WriteLine("SKIP NTFS resident lease native checks: required full NTFS identity unavailable");
                return passed;
            }
            var record = synthetic with { Reference = native.Identity.Low, LogicalBytes = native.LogicalBytes };
            ulong volume = native.Identity.Volume;

            NtfsLeaseResult Read(NtfsRecord? candidate = null, ulong? candidateVolume = null) =>
                NtfsResidentLease.Read(candidateVolume ?? volume, candidate ?? record, file);
            void Successful(NtfsLeaseResult result, FileStorageInfo expected, string name) =>
                Check(result.Success && result.Storage == expected && result.Code == "ResidentLease" &&
                    result.HandlesOpened == 1 && result.SnapshotReads == 2, name);
            void FailedAfterOpen(NtfsLeaseResult result, string code, string name) =>
                Check(!result.Success && result.Storage == null && result.Code == code &&
                    result.HandlesOpened == 1 && result.SnapshotReads == 1, name);
            void ExclusiveReopen(string name)
            {
                using var reopened = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Check(reopened.Length == record.LogicalBytes, name);
            }

            Successful(Read(), native, "one handle and two snapshots match ordinary small-file NativeStorage values");
            ExclusiveReopen("success releases its handle for an exclusive writer");
            FailedAfterOpen(Read(candidateVolume: volume ^ 1), "IdentityChanged", "wrong volume stays unknown");
            ushort sequence = (ushort)(record.Reference >> 48);
            ulong nextReference = (record.Reference & NtfsRecordDecoder.MaximumRecordNumber) |
                ((ulong)(sequence == ushort.MaxValue ? 1 : sequence + 1) << 48);
            FailedAfterOpen(Read(record with { Reference = nextReference }), "IdentityChanged", "reused sequence stays unknown");
            FailedAfterOpen(Read(record with { Reference = record.Reference ^ 1 }), "IdentityChanged", "wrong file slot stays unknown");
            FailedAfterOpen(Read(record with { LogicalBytes = native.LogicalBytes + 1 }), "LogicalChanged", "wrong decoded EOF stays unknown");
            ExclusiveReopen("failure after opening releases its handle");

            var dosAlias = record with { LinkCount = 2, Names = [record.Names[0], new(parent, "LEASE~1.BIN", 2)] };
            Successful(Read(dosAlias), native, "raw DOS name does not inflate visible native links");
            var extraLink = record with { LinkCount = 2, Names = [record.Names[0], new(parent, "missing-link.bin", 1)] };
            FailedAfterOpen(Read(extraLink), "InvalidSnapshot", "unmatched visible link count stays unknown");
            using (var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                Successful(Read(), native, "ordinary shared reader is allowed");
            using (var writer = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                var blocked = Read();
                Check(!blocked.Success && blocked.Storage == null && blocked.Code == "OpenError-32" &&
                    blocked.HandlesOpened == 0 && blocked.SnapshotReads == 0,
                    "existing writer is rejected without a permissive retry");
            }
            using (var deleteHandle = NativeStorage.CreateFileW(NativeStorage.LongPath(file), 0x10000, NativeStorage.ShareAll,
                IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero))
            {
                Check(!deleteHandle.IsInvalid, "owned fixture supports an existing delete-access handle");
                var blocked = Read();
                Check(!blocked.Success && blocked.Storage == null && blocked.Code == "OpenError-32" &&
                    blocked.HandlesOpened == 0 && blocked.SnapshotReads == 0,
                    "existing delete access is rejected without share-delete retry");
            }
            Successful(Read(), native, "writer and delete-handle refusal does not retain a handle");

            string hardLink = Path.Combine(root, "hardlink.bin");
            if (CreateHardLinkW(hardLink, file, IntPtr.Zero))
            {
                try
                {
                    FailedAfterOpen(Read(), "InvalidSnapshot", "new visible hard link invalidates the old record");
                    Check(NativeStorage.TryRead(file, false, File.GetAttributes(file), out var linked, out _) && linked?.LinkCount == 2,
                        "hard-link fixture has two native links");
                    var linkedRecord = record with { LinkCount = 2, Names = [record.Names[0], new(parent, "hardlink.bin", 1)] };
                    Successful(Read(linkedRecord), linked!, "complete visible hard links retain native allocation values");
                }
                finally { File.Delete(hardLink); }
            }
            else Console.WriteLine("SKIP NTFS resident lease hard-link fixture: native error " + Marshal.GetLastWin32Error());

            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None)) stream.SetLength(65536);
            FailedAfterOpen(Read(), "LogicalChanged", "file growth cannot reuse the old resident-length evidence");
            Check(NativeStorage.TryRead(file, false, File.GetAttributes(file), out var grown, out _) && grown != null,
                "grown ordinary fixture has readable native storage");
            Successful(Read(record with { LogicalBytes = grown!.LogicalBytes }), grown,
                "helper reports allocation and does not assert residency from candidate flags");

            string replacement = Path.Combine(root, "replacement.bin");
            File.WriteAllBytes(replacement, new byte[65536]);
            File.Move(replacement, file, true);
            FailedAfterOpen(Read(record with { LogicalBytes = grown.LogicalBytes }), "IdentityChanged",
                "same-size replacement cannot pass full identity binding");
            File.Delete(file);
            Check(!File.Exists(file), "all paths release handles so the owned file can be deleted");

            Check(NativeStorage.TryRead(root, true, File.GetAttributes(root), out var directory, out _) && directory != null,
                "owned directory has readable native identity");
            var directoryCandidate = record with
            {
                Reference = directory!.Identity.Low, LogicalBytes = 0,
                Names = [new(parent, Path.GetFileName(root), 1)]
            };
            FailedAfterOpen(NtfsResidentLease.Read(directory.Identity.Volume, directoryCandidate, root),
                "UnsupportedAttributes", "actual directory is rejected even when a candidate claims ordinary file");

            string sparsePath = Path.Combine(root, "sparse.bin");
            File.WriteAllBytes(sparsePath, new byte[65536]);
            bool sparse;
            using (var stream = new FileStream(sparsePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                sparse = NativeStorage.DeviceIoControl(stream.SafeFileHandle, 0x000900c4, null, 0, [], 0, out _, IntPtr.Zero);
            if (sparse)
            {
                Check(NativeStorage.TryRead(sparsePath, false, File.GetAttributes(sparsePath), out var sparseInfo, out _) && sparseInfo != null,
                    "owned sparse fixture has readable native storage");
                var sparseCandidate = record with
                {
                    Reference = sparseInfo!.Identity.Low, LogicalBytes = sparseInfo.LogicalBytes,
                    Names = [new(parent, "sparse.bin", 1)]
                };
                FailedAfterOpen(NtfsResidentLease.Read(sparseInfo.Identity.Volume, sparseCandidate, sparsePath),
                    "UnsupportedAttributes", "actual sparse attribute cannot be hidden by an ordinary candidate flag");
            }
            else Console.WriteLine("SKIP NTFS resident lease sparse fixture: native feature unavailable");
        }
        finally
        {
            // Only this exact run-owned directory may be recursively removed.
            string resolved = Path.GetFullPath(root);
            string expectedParent = Path.TrimEndingDirectorySeparator(tempRoot);
            if (!string.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("AsukaLens-resident-lease-", StringComparison.Ordinal) ||
                (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0 ||
                Directory.EnumerateFileSystemEntries(resolved, "*", SearchOption.TopDirectoryOnly)
                    .Any(path => (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0))
                throw new InvalidOperationException("Refused unexpected resident lease fixture cleanup target");
            Directory.Delete(resolved, true);
        }
        Console.WriteLine("PASS NTFS resident lease checks: " + passed + "; native files: owned temporary fixtures only");
        return passed;
    }

    private static int RunIntegrationChecks()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS lease integration check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS lease integration " + name);
        }
        const ulong volume = 31;
        ulong parent = NtfsRecordFixtures.Reference(5, 1), reference = NtfsRecordFixtures.Reference(42, 3);
        var record = new NtfsRecord(reference, 0, false, 1, [new(parent, "lease.bin", 1)],
            17, null, NtfsLookupReason.ResidentAllocation);
        var item = new NtfsLookupItem(volume, record, "lease.bin");
        var storage = new FileStorageInfo(new(volume, reference, 0), 24, 1, false, 17,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local));
        var success = new NtfsLeaseResult(true, storage, "ResidentLease", 1, 2);
        int calls = 0;
        NtfsLookupBatch Resolve(NtfsLeaseResult result) => NtfsSizeLookup.ResolveWithLease([item], (_, _) => { calls++; return result; });
        var direct = NtfsSizeLookup.ResolveWithLease([item with
        {
            Record = record with { LookupReasons = NtfsLookupReason.None, AllocatedBytes = 4096 }
        }], (_, _) => throw new InvalidOperationException("must not read direct sizes"));
        Check(direct.Complete && direct.Metrics.NativeCalls == 0 &&
            direct.Items.Single().AllocatedSource == NtfsSizeSource.Record && direct.Items.Single().AllocatedBytes == 4096,
            "known direct record sizes avoid lease callbacks");
        var resolved = Resolve(success);
        Check(resolved.Complete && calls == 1 && resolved.Metrics.NativeCalls == 1 && resolved.Metrics.ResolvedObjects == 1 &&
            resolved.Items.Single().AllocatedBytes == 24 && resolved.Items.Single().AllocatedSource == NtfsSizeSource.HandleLookup,
            "one resident lease callback resolves a validated allocation");

        void Failed(NtfsLeaseResult result, string name)
        {
            calls = 0;
            var batch = Resolve(result);
            Check(!batch.Complete && calls == 1 && batch.Metrics.NativeCalls == 1 && batch.Metrics.FailedObjects == 1 &&
                batch.Items.Single().AllocatedBytes == null && batch.Items.Single().AllocatedSource == NtfsSizeSource.Unknown, name);
        }
        Failed(success with { Success = false }, "failure flag cannot publish accompanying storage");
        Failed(success with { Storage = null }, "missing storage remains unknown");
        Failed(success with { HandlesOpened = 0 }, "zero-handle claim cannot count as a lease");
        Failed(success with { HandlesOpened = 2 }, "two-handle claim cannot count as a lease");
        Failed(success with { SnapshotReads = 1 }, "one-snapshot claim cannot count as a lease");
        Failed(success with { SnapshotReads = 3 }, "unexpected snapshot count cannot count as a lease");
        Failed(success with { Storage = storage with { Identity = storage.Identity with { Volume = volume + 1 } } },
            "callback success cannot bypass volume binding");
        Failed(success with { Storage = storage with { Identity = storage.Identity with { Low = reference + 1 } } },
            "callback success cannot bypass record binding");
        Failed(success with { Storage = storage with { Identity = storage.Identity with { High = 1 } } },
            "callback success cannot bypass full identity binding");
        Failed(success with { Storage = storage with { LogicalBytes = 18 } }, "callback success cannot bypass EOF binding");
        Failed(success with { Storage = storage with { AllocatedBytes = -1 } }, "negative allocation remains unknown");
        Failed(success with { Storage = storage with { Modified = null } }, "missing write timestamp remains unknown");
        Failed(success with { Storage = storage with { LinkCount = 2 } }, "unexpected native link count remains unknown");
        Failed(success with { Storage = storage with { WritableHandleOpen = true } }, "writer flag cannot publish allocation");

        foreach (var error in new Exception[] { new IOException("private fixture error"), new UnauthorizedAccessException("private fixture error") })
        {
            var batch = NtfsSizeLookup.ResolveWithLease([item], (_, _) => throw error);
            Check(!batch.Complete && batch.Items.Single().AllocatedBytes == null && batch.Items.Single().Code == "LeaseReadFailed",
                "expected callback failure has a fixed path-free result");
        }
        bool unexpected = false;
        try { NtfsSizeLookup.ResolveWithLease([item], (_, _) => throw new InvalidOperationException("synthetic")); }
        catch (InvalidOperationException) { unexpected = true; }
        Check(unexpected, "unexpected callback exception propagates");
        bool cancelledCallback = false;
        try { NtfsSizeLookup.ResolveWithLease([item], (_, _) => throw new OperationCanceledException()); }
        catch (OperationCanceledException) { cancelledCallback = true; }
        Check(cancelledCallback, "callback cancellation propagates");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); calls = 0; bool stopped = false;
            try { NtfsSizeLookup.ResolveWithLease([item], (_, _) => { calls++; return success; }, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && calls == 0, "pre-cancelled batch never invokes lease reader");
        }
        var secondItem = item with { Record = record with { Reference = NtfsRecordFixtures.Reference(43, 3) }, Path = "second.bin" };
        using (var cancelled = new CancellationTokenSource())
        {
            calls = 0; bool stopped = false;
            try
            {
                NtfsSizeLookup.ResolveWithLease([item, secondItem], (input, _) =>
                {
                    calls++;
                    if (calls == 2) cancelled.Cancel();
                    return success with { Storage = storage with { Identity = storage.Identity with { Low = input.Record.Reference } } };
                }, cancelled.Token);
            }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && calls == 2, "mid-batch cancellation publishes no partial batch");
        }
        calls = 0;
        var budget = NtfsSizeLookup.ResolveWithLease([item, secondItem], (_, _) => { calls++; return success; }, limit: 1);
        Check(!budget.Complete && budget.Code == "RecordLimit" && budget.Items.Count == 0 && calls == 0,
            "budget rejects the whole batch before native work");
        var duplicates = NtfsSizeLookup.ResolveWithLease([item, item], (_, _) => throw new InvalidOperationException("must not read duplicates"));
        Check(!duplicates.Complete && duplicates.Code == "DuplicateObject" && duplicates.Items.Count == 0,
            "duplicate identities reject the batch before native work");
        return passed;
    }
}
