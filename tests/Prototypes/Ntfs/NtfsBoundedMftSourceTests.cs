using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

// Memory-backed streams and an inert handle only. The test factory can never
// mint native-owned evidence, regardless of its simulated success.
internal static class NtfsBoundedMftSourceTests
{
    private sealed class Fixture : NtfsBoundedMftSource.IFastBackend, NtfsRawVolumeLease.INative
    {
        private const int MftOffset = 10 * 4096;
        private readonly byte[] image;
        private readonly bool fragmented;
        private readonly SafeFileHandle volume = new(new IntPtr(12345), false);
        private long offset;
        private bool disposed;
        internal int Locks, Unlocks, Reads, Allocations, Frees, Captures, Verifies, Queries, Disposes;
        internal bool LockFails, UnlockFails, PostFails, FactsChange, DisposeFails, FreeFails;
        internal int FailReadAt, ShortReadAt;
        internal Action<int>? AfterRead;
        internal Action? OnDispose, OnFree, OnUnlock;
        internal Action<bool>? OnQuery;
        internal Exception? PostError;
        internal bool ScannerMismatch, FastVerifyFails;
        internal int FastPreparations, Scans, FastVerifies;
        internal ushort WitnessSequence = 7;
        internal bool DuplicateWitnessSlot;
        internal Action? OnScan;
        private readonly bool fast;
        private readonly FileStorageInfo rootStorage = new(new(41, NtfsRecordFixtures.Reference(31, 5), 0), 0, 1, false, -1, new DateTime(2020, 1, 1));
        internal List<(long Offset, int Length)> Windows { get; } = [];
        internal List<NtfsRawTarget> Targets { get; } = [];
        internal NtfsRawVolumeFacts Facts { get; set; }
        public SafeFileHandle Volume => volume;

        internal Fixture(int slots = 64, bool fragmented = false, byte[][]? bitmapAttributes = null, bool fast = false, bool residentPrefix = false)
        {
            this.fast = fast;
            this.fragmented = fragmented;
            long valid = slots * 1024L;
            int clusters = checked((int)((valid + 4095) / 4096));
            int imageBytes = Math.Max(1024 * 1024, MftOffset + clusters * 4096);
            image = new byte[imageBytes];
            Facts = new(new(512, 4096, 1024, imageBytes, imageBytes / 4096, 10, valid), 41, 4096);
            var seed = new List<byte[]> { NtfsRecordFixtures.Name("$MFT"), Data(valid, clusters * 4096L, clusters, 0, fragmented) };
            if (bitmapAttributes != null) seed.AddRange(bitmapAttributes);
            else if (fast)
            {
                var prefix = new byte[(slots + 7) / 8];
                foreach (int used in Enumerable.Range(0, 22).Concat(Enumerable.Range(31, 22))) prefix[used / 8] |= (byte)(1 << (used % 8));
                if (residentPrefix) seed.Add(NtfsRecordFixtures.Resident(0xb0, prefix));
                else
                {
                    var bitmap = new byte[72]; NtfsRecordFixtures.U32(bitmap, 0, 0xb0); NtfsRecordFixtures.U32(bitmap, 4, 72); bitmap[8] = 1;
                    NtfsRecordFixtures.U64(bitmap, 24, 1); NtfsRecordFixtures.U16(bitmap, 32, 64);
                    NtfsRecordFixtures.U64(bitmap, 40, 8192); NtfsRecordFixtures.U64(bitmap, 48, 8192); NtfsRecordFixtures.U64(bitmap, 56, 8192);
                    bitmap[64] = 0x21; bitmap[65] = 2; bitmap[66] = 200; bitmap[67] = 0; seed.Add(bitmap);
                    prefix.CopyTo(image, 200 * 4096);
                }
            }
            Put(0, NtfsRecordFixtures.Record(seed));
            int index = 0;
            foreach (var item in NtfsKernelMatrix.Cases)
            {
                int slot = ++index;
                ulong parent = NtfsRecordFixtures.Reference((ulong)(31 + index), 5);
                var attributes = new List<byte[]> { NtfsRecordFixtures.Name(NtfsKernelMatrix.FileName(item.NameLength), 1, parent) };
                if (item.HardLink) attributes.Add(NtfsRecordFixtures.Name("link.bin", 1, parent));
                ushort flags = item.Kind == "Compressed" ? (ushort)1 : item.Kind == "Sparse" ? (ushort)0x8000 : (ushort)0;
                attributes.Add(Data(item.LogicalBytes, item.Kind == "Ordinary" ? 131072 : 8192, item.Kind == "Ordinary" ? 32 : 2, flags));
                if (item.NamedStream) attributes.Add(NtfsRecordFixtures.Data(1, "meta"));
                Put(slot, NtfsRecordFixtures.Record(attributes, links: item.HardLink ? (ushort)2 : (ushort)1));
                var storage = new FileStorageInfo(new(41, NtfsRecordFixtures.Reference((ulong)slot, 3), 0),
                    item.Kind == "Ordinary" ? 131072 : 4096, item.HardLink ? 2u : 1u, false, item.LogicalBytes, new DateTime(2020, 1, 1));
                string directory = @"X:\Synthetic\" + item.CaseId;
                var attrib = FileAttributes.Archive | (item.Kind == "Compressed" ? FileAttributes.Compressed : item.Kind == "Sparse" ? FileAttributes.SparseFile : 0);
                Targets.Add(new(item.CaseId, Path.Combine(directory, NtfsKernelMatrix.FileName(item.NameLength)), directory,
                    storage, new(new(41, parent, 0), 0, 1, false, -1, new DateTime(2020, 1, 1)), attrib));
            }
            Put(22, NtfsRecordFixtures.Record([], inUse: false));
            var unsupported = NtfsRecordFixtures.File(); NtfsRecordFixtures.U16(unsupported, 22, 5); Put(23, unsupported);
            if (fast)
            {
                Put(22, NtfsRecordFixtures.Record([], sequence: 8, inUse: false)); Put(23, NtfsRecordFixtures.Record([], sequence: 8, inUse: false));
                Put(31, NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("Synthetic"), NtfsRecordFixtures.Data(0)], sequence: 5, directory: true));
                int directory = 32;
                foreach (var descriptor in NtfsKernelMatrix.Cases)
                    Put(directory++, NtfsRecordFixtures.Record([NtfsRecordFixtures.Name(descriptor.CaseId, parent: rootStorage.Identity.Low),
                        NtfsRecordFixtures.Data(0)], sequence: 5, directory: true));
            }
        }

        private static byte[] Data(long logical, long allocated, int clusters, ushort flags, bool fragmented = false)
        {
            int header = flags == 0 ? 64 : 72;
            var data = new byte[header + 16];
            NtfsRecordFixtures.U32(data, 0, 0x80); NtfsRecordFixtures.U32(data, 4, (uint)data.Length); data[8] = 1;
            NtfsRecordFixtures.U16(data, 12, flags); NtfsRecordFixtures.U64(data, 24, (ulong)(clusters - 1));
            NtfsRecordFixtures.U16(data, 32, (ushort)header); NtfsRecordFixtures.U16(data, 34, flags == 1 ? (ushort)4 : (ushort)0);
            NtfsRecordFixtures.U64(data, 40, (ulong)allocated); NtfsRecordFixtures.U64(data, 48, (ulong)logical); NtfsRecordFixtures.U64(data, 56, (ulong)logical);
            if (header == 72) NtfsRecordFixtures.U64(data, 64, (ulong)allocated);
            if (fragmented)
            {
                data[header] = 0x11; data[header + 1] = 8; data[header + 2] = 10;
                data[header + 3] = 0x11; data[header + 4] = 8; data[header + 5] = unchecked((byte)-8);
            }
            else
            {
                data[header] = 0x12; NtfsRecordFixtures.U16(data, header + 1, checked((ushort)clusters)); data[header + 3] = 10;
            }
            return data;
        }
        private int Location(int slot) => fragmented && slot >= 32 ? 2 * 4096 + (slot - 32) * 1024 : MftOffset + slot * 1024;
        private void Put(int slot, byte[] data) => data.CopyTo(image, Location(slot));
        internal void Corrupt(int slot, int position, byte value) => image[Location(slot) + position] = value;
        internal void SetPrefixBit(int slot, bool set)
        {
            int position = 200 * 4096 + slot / 8; byte bit = (byte)(1 << (slot % 8));
            image[position] = set ? (byte)(image[position] | bit) : (byte)(image[position] & ~bit);
        }
        public IReadOnlyList<NtfsRawTarget> CaptureFixedTargets(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Captures++; return Array.AsReadOnly(Targets.ToArray()); }
        public NtfsRawVolumeFacts QueryFacts(bool reopen, CancellationToken token)
        {
            Queries++; OnQuery?.Invoke(reopen); token.ThrowIfCancellationRequested();
            return reopen && FactsChange ? Facts with { Geometry = Facts.Geometry with { MftValidDataLength = Facts.Geometry.MftValidDataLength + 1024 } } : Facts;
        }
        public void VerifyFixedTargets(IReadOnlyList<NtfsRawTarget> baseline, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Verifies++; if (PostError != null) throw PostError; if (PostFails) throw new IOException("SyntheticPostFailure"); }
        public NtfsFixtureFastContext PrepareFastContext(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); FastPreparations++;
            if (!fast) throw new IOException("SyntheticWrongFastBackend");
            return new(rootStorage, Array.AsReadOnly(new[] { new NtfsFixtureReleasedWitness("Released-1", new(new(41, NtfsRecordFixtures.Reference(22, WitnessSequence), 0), 0, 1, false, 0, new DateTime(2020,1,1))),
                new NtfsFixtureReleasedWitness("Released-2", new(new(41, NtfsRecordFixtures.Reference(DuplicateWitnessSlot ? 22UL : 23UL, WitnessSequence), 0), 0, 1, false, 0, new DateTime(2020,1,1))) }));
        }
        public void VerifyFastContext(NtfsFixtureFastContext baseline, CancellationToken token)
        { token.ThrowIfCancellationRequested(); FastVerifies++; if (FastVerifyFails) throw new IOException("SyntheticFastVerify"); }
        public ScanResult ScanFixture(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Scans++; OnScan?.Invoke(); token.ThrowIfCancellationRequested();
            if (Unlocks != 1 || Disposes != 0 || Verifies != 0 || FastVerifies != 0) throw new IOException("SyntheticScanLifecycle");
            var root = new DiskEntry(@"X:\Synthetic", true) { Storage = rootStorage };
            foreach (var descriptor in NtfsKernelMatrix.Cases)
            {
                var target = Targets.Single(item => item.CaseId == descriptor.CaseId);
                var directory = new DiskEntry(target.ParentPath, true, root) { Storage = target.ParentStorage };
                root.MutableChildren.Add(directory);
                var file = new DiskEntry(target.Path, false, directory) { Storage = target.Storage, Size = target.Storage.LogicalBytes, FileCount = 1 };
                directory.MutableChildren.Add(file);
                if (descriptor.HardLink) directory.MutableChildren.Add(new DiskEntry(Path.Combine(target.ParentPath,"link.bin"), false, directory)
                    { Storage = target.Storage, Size = target.Storage.LogicalBytes, FileCount = 1 });
                directory.FileCount = directory.Children.Count; directory.Size = directory.Children.Sum(child => child.Size);
                root.FileCount += directory.FileCount; root.Size += directory.Size; root.FolderCount++;
            }
            StorageMetrics.Calculate(root, token); if (ScannerMismatch) root.Size++;
            return new(root, TimeSpan.Zero, 0, []);
        }
        public NtfsRawVolumeLease.NativeResult Lock(SafeFileHandle _) { Locks++; return new(!LockFails, LockFails ? 5 : 0); }
        public NtfsRawVolumeLease.NativeResult Unlock(SafeFileHandle _) { Unlocks++; OnUnlock?.Invoke(); return new(!UnlockFails, UnlockFails ? 5 : 0); }
        public NtfsRawVolumeLease.NativeResult Seek(SafeFileHandle _, long value) { offset = value; return new(true, 0); }
        public NtfsRawVolumeLease.NativeReadResult Read(SafeFileHandle _, IntPtr buffer, int length)
        {
            Reads++; Windows.Add((offset, length));
            if (Reads == FailReadAt) return new(false, 0, 5);
            int count = Reads == ShortReadAt ? length - 512 : length;
            Marshal.Copy(image, checked((int)offset), buffer, count); AfterRead?.Invoke(Reads); return new(true, count, 0);
        }
        public IntPtr Allocate(int bytes) { Allocations++; return Marshal.AllocHGlobal(bytes); }
        public void Free(IntPtr allocation) { Frees++; Marshal.FreeHGlobal(allocation); OnFree?.Invoke(); if (FreeFails) throw new IOException("SyntheticFreeFailure"); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Disposes++; volume.Dispose(); OnDispose?.Invoke();
            if (DisposeFails) throw new IOException("SyntheticCleanupFailure");
        }
    }

    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS bounded source check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS bounded source " + name);
        }
        Exception? Catch(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
        foreach (int slots in new[] { 64, 70, 4096 })
        {
            using var fixture = new Fixture(slots); using var source = NtfsBoundedMftSource.OpenForTests(fixture, fixture);
            Check(fixture.Locks == 1 && fixture.Reads == 1 && fixture.Allocations == 1 && fixture.Frees == 0,
                "opening acquires one lock and reusable bootstrap buffer");
            var report = source.RunEnumeration(); var enumeration = report.Enumeration;
            Check(report is { Revision: "bounded-mft-source-v2", EvidenceKind: "SyntheticFixture", GuardOwned: false,
                TokenBound: true, MapBound: true, Closed: true, RejectedReads: 0 }, "fake factory never mints native-owned source evidence");
            Check(enumeration.ValidBytes == slots * 1024L && enumeration.ExpectedSlots == slots && enumeration.VisitedSlots == slots &&
                enumeration.Decoded == 22 && enumeration.Free == 1 && enumeration.Unsupported == 1 && enumeration.Invalid == slots - 24 && enumeration.CompletedCursor,
                "full range retains all four strict classifications including non-target invalid records");
            Check(enumeration.LookupRequired == 16 && enumeration.UnknownLogical == 0 && enumeration.UnknownAllocated == 14 &&
                enumeration.MatchedObjects == 21 && enumeration.UnknownAllocationObjects == 14 && enumeration.Cases.Count == 21,
                "fixed targets match while special allocation remains unknown without global totals");
            Check(enumeration.AllZeroInvalid == slots - 24 && enumeration.CodeCounts.SequenceEqual(new[] {
                new NtfsMftCodeCount("Decoded", "Decoded", 22), new("Free", "FreeRecord", 1),
                new("Invalid", "Signature", slots - 24), new("Unsupported", "RecordFlags", 1) }) &&
                enumeration.CodeCounts.Sum(row => row.Count) == enumeration.VisitedSlots,
                "closed v2 evidence exposes only bounded aggregate reasons while zero records stay invalid");
            int pages = (slots * 1024 + 65535) / 65536;
            Check(enumeration.Pages == pages && enumeration.ReadCalls == pages + 1 && enumeration.RequestedBytes == slots * 1024L + 1024 &&
                fixture.Reads == pages + 1 && fixture.Windows.Skip(1).Sum(item => (long)item.Length) == slots * 1024L,
                "range requests exactly its valid bytes plus bootstrap without EOF padding");
            Check(fixture.Allocations == 1 && fixture.Frees == 1 && fixture.Unlocks == 1 && fixture.Verifies == 1 && fixture.Queries == 2 && fixture.Disposes == 1,
                "successful source frees one native buffer unlocks post-verifies and closes once");
            Check(Catch(() => source.RunEnumeration()) is NtfsFixtureSourceException { Code: "SourceNotActive" } && fixture.Reads == pages + 1,
                "closed source cannot reuse its former cursor map or token");
            string json = JsonSerializer.Serialize(report);
            Check(!json.Contains("X:", StringComparison.Ordinal) && !json.Contains("Reference", StringComparison.Ordinal) &&
                !json.Contains("TokenValue", StringComparison.Ordinal), "public report contains only controlled counts and case labels");
        }
        {
            using var fixture = new Fixture(fragmented: true); using var source = NtfsBoundedMftSource.OpenForTests(fixture, fixture);
            var report = source.RunEnumeration().Enumeration;
            Check(report.MftRuns == 2 && report.Pages == 1 && report.ReadCalls == 3 && report.RequestedBytes == 66560 && report.MatchedObjects == 21,
                "one logical page spans two mapped physical runs including a negative LCN delta");
            Check(fixture.Windows.SequenceEqual(new[] { (40960L, 1024), (40960L, 32768), (8192L, 32768) }),
                "fragmented callback reads only each exact bounded physical slice");
        }
        foreach (string fault in new[] { "Large", "Unaligned", "Missing", "Duplicate", "Foreign", "Size", "Links", "Writable", "High" })
        {
            using var fixture = new Fixture();
            if (fault == "Large") fixture.Facts = fixture.Facts with { Geometry = fixture.Facts.Geometry with { MftValidDataLength = 4097 * 1024 } };
            else if (fault == "Unaligned") fixture.Facts = fixture.Facts with { Geometry = fixture.Facts.Geometry with { MftValidDataLength = 65537 } };
            else if (fault == "Missing") fixture.Targets.RemoveAt(20);
            else if (fault == "Duplicate") fixture.Targets[20] = fixture.Targets[0];
            else
            {
                var target = fixture.Targets[0]; var storage = target.Storage;
                storage = fault switch
                {
                    "Foreign" => storage with { Identity = storage.Identity with { Volume = 42 } },
                    "Size" => storage with { LogicalBytes = storage.LogicalBytes + 1 },
                    "Links" => storage with { LinkCount = 99 },
                    "Writable" => storage with { WritableHandleOpen = true },
                    _ => storage with { Identity = storage.Identity with { High = 1 } }
                };
                fixture.Targets[0] = target with { Storage = storage };
            }
            Check(Catch(() => NtfsBoundedMftSource.OpenForTests(fixture, fixture)) is NtfsFixtureSourceException && fixture.Locks == 0 &&
                fixture.Reads == 0 && fixture.Disposes == 1, "oversized or invalid native scope fails before any lock or raw read");
        }
        foreach (string fault in new[] { "Lock", "Bootstrap", "ShortBootstrap" })
        {
            using var fixture = new Fixture();
            if (fault == "Lock") fixture.LockFails = true;
            else if (fault == "Bootstrap") fixture.Corrupt(0, 0, (byte)'B');
            else fixture.ShortReadAt = 1;
            Check(Catch(() => NtfsBoundedMftSource.OpenForTests(fixture, fixture)) is NtfsFixtureSourceException && fixture.Disposes == 1 &&
                fixture.Unlocks == (fault == "Lock" ? 0 : 1) && fixture.Allocations == fixture.Frees,
                "failed bootstrap construction cannot leak a lock buffer or source");
        }
        foreach (string fault in new[] { "Read", "ShortRead", "Missing", "Sequence", "Logical", "NativeAllocation" })
        {
            using var fixture = new Fixture(); using var source = NtfsBoundedMftSource.OpenForTests(fixture, fixture);
            if (fault == "Read") fixture.FailReadAt = 2;
            else if (fault == "ShortRead") fixture.ShortReadAt = 2;
            else if (fault == "Missing") fixture.Corrupt(1, 0, (byte)'B');
            else if (fault == "Sequence") fixture.Corrupt(1, 16, 4);
            else if (fault == "Logical") fixture.Targets[0] = fixture.Targets[0] with { Storage = fixture.Targets[0].Storage with { LogicalBytes = 1 } };
            else fixture.Targets[0] = fixture.Targets[0] with { Storage = fixture.Targets[0].Storage with { AllocatedBytes = 1 } };
            // Captured targets are immutable snapshots; list replacement after
            // opening must not retarget the live source or alter its baseline.
            if (fault is "Logical" or "NativeAllocation")
            {
                Check(source.RunEnumeration().Enumeration.MatchedObjects == 21, "caller list replacement cannot mutate the private captured target snapshot");
                continue;
            }
            Check(Catch(() => source.RunEnumeration()) is NtfsFixtureSourceException && fixture.Unlocks == 1 && fixture.Frees == 1 && fixture.Disposes == 1,
                "read sequence or missing strict target prevents any partial source summary");
            Check(Catch(() => source.RunEnumeration()) is NtfsFixtureSourceException { Code: "SourceNotActive" },
                "failed range invalidates its owner instead of permitting a partial retry");
        }
        foreach (string fault in new[] { "Unlock", "Free", "Post", "Facts", "Cleanup" })
        {
            using var fixture = new Fixture(); using var source = NtfsBoundedMftSource.OpenForTests(fixture, fixture);
            fixture.UnlockFails = fault == "Unlock"; fixture.FreeFails = fault == "Free"; fixture.PostFails = fault == "Post";
            fixture.FactsChange = fault == "Facts"; fixture.DisposeFails = fault == "Cleanup";
            Check(Catch(() => source.RunEnumeration()) is IOException && fixture.Disposes == 1 && fixture.Unlocks == 1 && fixture.Frees == 1,
                "buffer unlock post-facts or cleanup failure never publishes classified evidence");
        }
        foreach (bool shortRead in new[] { false, true })
        {
            using var fixture = new Fixture(70); using var source = NtfsBoundedMftSource.OpenForTests(fixture, fixture);
            if (shortRead) fixture.ShortReadAt = 3; else fixture.FailReadAt = 3;
            var error = Catch(() => source.RunEnumeration());
            Check(error is NtfsFixtureSourceException { Code: "SourceNativeFailed" } native &&
                native.NativeCode == (shortRead ? "ShortRead" : "ReadFailed") && native.NativeErrorCode == (shortRead ? 0 : 5) &&
                fixture.Reads == 3 && fixture.Unlocks == 1 && fixture.Frees == 1 && fixture.Disposes == 1 && fixture.Verifies == 0,
                "second-page failure preserves typed native diagnostics and discards earlier successful page coverage");
        }
        {
            using var fixture = new Fixture();
            fixture.Targets[0] = fixture.Targets[0] with { Storage = fixture.Targets[0].Storage with { AllocatedBytes = 1 } };
            using var source = NtfsBoundedMftSource.OpenForTests(fixture, fixture);
            Check(Catch(() => source.RunEnumeration()) is NtfsFixtureSourceException { Code: "SourceAllocationMismatch" } &&
                fixture.Unlocks == 1 && fixture.Frees == 1 && fixture.Disposes == 1,
                "known allocation mismatch against the captured native baseline cannot become range success");
        }
        foreach (string stage in new[] { "Open", "Bootstrap", "Page", "Free", "Unlock", "PostFacts", "Dispose", "DisposeFailure" })
        {
            using var cts = new CancellationTokenSource(); using var fixture = new Fixture();
            if (stage == "Open") cts.Cancel();
            if (stage == "Bootstrap") fixture.AfterRead = _ => cts.Cancel();
            if (stage is "Open" or "Bootstrap")
            {
                Check(Catch(() => NtfsBoundedMftSource.OpenForTests(fixture, fixture, cts.Token)) is OperationCanceledException && fixture.Disposes == 1 &&
                    fixture.Unlocks == (stage == "Bootstrap" ? 1 : 0) && fixture.Allocations == fixture.Frees,
                    "construction cancellation releases only owned resources before returning");
                continue;
            }
            using var source = NtfsBoundedMftSource.OpenForTests(fixture, fixture);
            if (stage == "Page") fixture.AfterRead = _ => cts.Cancel();
            if (stage == "Free") fixture.OnFree = cts.Cancel;
            if (stage == "Unlock") fixture.OnUnlock = cts.Cancel;
            if (stage == "PostFacts") fixture.OnQuery = reopen => { if (reopen) cts.Cancel(); };
            if (stage is "Dispose" or "DisposeFailure") fixture.OnDispose = cts.Cancel;
            fixture.DisposeFails = stage == "DisposeFailure";
            Check(Catch(() => source.RunEnumeration(cts.Token)) is OperationCanceledException && fixture.Disposes == 1 && fixture.Unlocks == 1 && fixture.Frees == 1,
                "cancellation across reading native cleanup and final owner disposal cannot publish a report");
        }
        {
            using var cts = new CancellationTokenSource(); using var fixture = new Fixture();
            using var source = NtfsBoundedMftSource.OpenForTests(fixture, fixture);
            var primary = new IOException("SyntheticPrimary"); fixture.PostError = primary; fixture.OnDispose = cts.Cancel; fixture.DisposeFails = true;
            Check(ReferenceEquals(Catch(() => source.RunEnumeration(cts.Token)), primary) && fixture.Unlocks == 1 && fixture.Disposes == 1,
                "primary verification failure survives disposal cancellation and cleanup failure");
        }
        return passed;
    }

    private static byte[] NonresidentBitmap(long allocated = 4096, long logical = 8)
    {
        var attribute = new byte[72];
        NtfsRecordFixtures.U32(attribute, 0, 0xb0); NtfsRecordFixtures.U32(attribute, 4, 72); attribute[8] = 1;
        NtfsRecordFixtures.U64(attribute, 24, 0); NtfsRecordFixtures.U16(attribute, 32, 64);
        NtfsRecordFixtures.U64(attribute, 40, unchecked((ulong)allocated)); NtfsRecordFixtures.U64(attribute, 48, unchecked((ulong)logical));
        NtfsRecordFixtures.U64(attribute, 56, unchecked((ulong)logical)); attribute[64] = 0x11; attribute[65] = 1; attribute[66] = 10;
        return attribute;
    }

    internal static int RunBitmapObservationTests()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS bitmap source check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS bitmap source " + name);
        }
        Exception? Catch(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
        foreach ((byte[][]? attributes, string status, string code, int bytes, int coverage) in new (byte[][]?,string,string,int,int)[] {
            (null,"Missing","BitmapMissing",0,0),
            ([NtfsRecordFixtures.Resident(0xb0,[])],"ResidentPartial","BitmapResidentPartial",0,0),
            ([NtfsRecordFixtures.Resident(0xb0,new byte[1])],"ResidentPartial","BitmapResidentPartial",1,8),
            ([NtfsRecordFixtures.Resident(0xb0,new byte[8])],"ResidentCapacityOnly","BitmapResidentComplete",8,64),
            ([NtfsRecordFixtures.Resident(0xb0,new byte[512])],"ResidentCapacityOnly","BitmapResidentComplete",512,64),
            ([NtfsRecordFixtures.Resident(0xb0,new byte[513])],"Deferred","BitmapResidentPayloadBudget",0,0),
            ([NonresidentBitmap(logical:513)],"Deferred","BitmapNonresidentPayloadBudget",0,0),
            ([NtfsRecordFixtures.Resident(0xb0,new byte[8],"named")],"Deferred","BitmapNamed",0,0),
            ([NtfsRecordFixtures.Resident(0xb0,new byte[8]),NtfsRecordFixtures.Resident(0xb0,new byte[8])],"Deferred","BitmapMultiple",0,0),
            ([NtfsRecordFixtures.Resident(0xb0,new byte[8],flags:1)],"Deferred","BitmapFlags",0,0),
            ([NonresidentBitmap()],"Deferred","BitmapNonresident",0,0) })
        {
            using var fixture = new Fixture(bitmapAttributes: attributes);
            using var source = NtfsBoundedMftSource.OpenBitmapObservationForTests(fixture, fixture);
            Check(fixture.Reads == 1 && fixture.Allocations == 1 && fixture.Frees == 0 && fixture.Locks == 1,
                "bitmap shape uses the already-read bootstrap without another native read or buffer");
            var report = source.RunBitmapObservation(); var bitmap = report.Bitmap; var range = report.Range;
            Check(report is { Revision: "bitmap-bootstrap-source-v1", Stage: "ObservedShapeOnly" } &&
                range is { Revision: "bounded-mft-source-v2", EvidenceKind: "SyntheticFixture", GuardOwned: false, TokenBound: true, MapBound: true, Closed: true },
                "synthetic profile cannot turn locator observations into native source or verified allocation evidence");
            Check(bitmap.Status == status && bitmap.Code == code && bitmap.ExpectedSlots == 64 && bitmap.PayloadBytes == bytes && bitmap.CoveredSlots == coverage,
                "missing partial resident-capacity and deferred shapes preserve their distinct controlled metadata");
            Check(code == "BitmapNonresident" ? bitmap.NonresidentMetadata is { LogicalBytes: 8, InitializedBytes: 8, AllocatedBytes: 4096 } :
                bitmap.NonresidentMetadata == null, "only deferred nonresident shape exposes three bounded lengths and no addressing metadata");
            Check(range.Enumeration is { ReadCalls: 2, RequestedBytes: 66560, MatchedObjects: 21, UnknownAllocationObjects: 14,
                ExpectedSlots: 64, Invalid: 40, AllZeroInvalid: 40 } && fixture.Reads == 2,
                "observation leaves range classifications fixed targets unknown allocation and request budgets unchanged");
            Check(fixture.Frees == 1 && fixture.Unlocks == 1 && fixture.Verifies == 1 && fixture.Queries == 2 && fixture.Disposes == 1,
                "one shared lifecycle closes the source only after range comparison and post-verification");
            string json = JsonSerializer.Serialize(report);
            Check(!json.Contains("X:", StringComparison.Ordinal) && !json.Contains("Reference", StringComparison.Ordinal) &&
                !json.Contains("RawRecord", StringComparison.Ordinal) && !json.Contains("CopyPayload", StringComparison.Ordinal) &&
                !json.Contains("\"Status\":\"ResidentComplete\"", StringComparison.Ordinal), "published shape report carries no raw body or allocation-complete claim");
            Check(Catch(() => source.RunBitmapObservation()) is NtfsFixtureSourceException { Code: "SourceNotActive" } && fixture.Reads == 2,
                "a closed observation profile cannot reuse its former cached bitmap or reading epoch");
        }
        foreach (bool bitmapProfile in new[] { false, true })
        {
            using var fixture = new Fixture(); using var source = bitmapProfile ?
                NtfsBoundedMftSource.OpenBitmapObservationForTests(fixture, fixture) : NtfsBoundedMftSource.OpenForTests(fixture, fixture);
            var error = bitmapProfile ? Catch(() => source.RunEnumeration()) : Catch(() => source.RunBitmapObservation());
            Check(error is NtfsFixtureSourceException { Code: "SourceProfileMismatch" } && fixture.Reads == 1 && fixture.Unlocks == 1 &&
                fixture.Frees == 1 && fixture.Disposes == 1 && fixture.Verifies == 0,
                "wrong profile entry closes the owner before any range reads or partial success");
            var retry = bitmapProfile ? Catch(() => source.RunBitmapObservation()) : Catch(() => source.RunEnumeration());
            Check(retry is NtfsFixtureSourceException { Code: "SourceNotActive" } && fixture.Reads == 1,
                "wrong-profile failure cannot be retried through the formerly correct entry point");
        }
        {
            using var fixture = new Fixture(bitmapAttributes: [NonresidentBitmap(1)]);
            using var source = NtfsBoundedMftSource.OpenForTests(fixture, fixture);
            Check(source.RunEnumeration().Enumeration.MatchedObjects == 21 && fixture.Reads == 2,
                "legacy range-only mode never invokes the new locator even for a locator-invalid unrelated attribute");
        }
        foreach (var attribute in new[] { NtfsRecordFixtures.Resident(0xb0, new byte[513]), NonresidentBitmap(logical: 513) })
        {
            using var fixture = new Fixture(bitmapAttributes: [attribute]); using var source = NtfsBoundedMftSource.OpenForTests(fixture, fixture);
            Check(source.RunEnumeration().Enumeration is { ReadCalls: 2, RequestedBytes: 66560, MatchedObjects: 21 } && fixture.Reads == 2,
                "legacy range-only output stays independent of either bitmap capacity-budget form");
        }
        {
            using var fixture = new Fixture(bitmapAttributes: [NonresidentBitmap(1)]);
            Check(Catch(() => NtfsBoundedMftSource.OpenBitmapObservationForTests(fixture, fixture)) is NtfsFixtureSourceException { Code: "SourceBitmapInvalid" } &&
                fixture.Reads == 1 && fixture.Unlocks == 1 && fixture.Frees == 1 && fixture.Disposes == 1,
                "invalid locator output aborts initialization and releases its map lock buffer and metadata");
        }
        foreach (string fault in new[] { "Read", "Short", "Target", "Unlock", "Free", "Facts", "Post", "Dispose" })
        {
            using var fixture = new Fixture(bitmapAttributes: [NtfsRecordFixtures.Resident(0xb0, new byte[8])]);
            using var source = NtfsBoundedMftSource.OpenBitmapObservationForTests(fixture, fixture);
            if (fault == "Read") fixture.FailReadAt = 2;
            if (fault == "Short") fixture.ShortReadAt = 2;
            if (fault == "Target") fixture.Corrupt(1, 0, (byte)'B');
            fixture.UnlockFails = fault == "Unlock"; fixture.FreeFails = fault == "Free"; fixture.FactsChange = fault == "Facts";
            fixture.PostFails = fault == "Post"; fixture.DisposeFails = fault == "Dispose";
            NtfsBitmapBootstrapSourceReport? partial = null;
            Check(Catch(() => partial = source.RunBitmapObservation()) is IOException && partial == null && fixture.Unlocks == 1 &&
                fixture.Frees == 1 && fixture.Disposes == 1, "range failure target mismatch or close failure cannot publish cached bootstrap metadata alone");
        }
        foreach (string stage in new[] { "Open", "Bootstrap", "Page", "Free", "Unlock", "Facts", "Dispose", "DisposeFailure" })
        {
            using var cancellation = new CancellationTokenSource(); using var fixture = new Fixture(bitmapAttributes: [NtfsRecordFixtures.Resident(0xb0, new byte[8])]);
            if (stage == "Open") cancellation.Cancel();
            if (stage == "Bootstrap") fixture.AfterRead = _ => cancellation.Cancel();
            if (stage is "Open" or "Bootstrap")
            {
                Check(Catch(() => NtfsBoundedMftSource.OpenBitmapObservationForTests(fixture, fixture, cancellation.Token)) is OperationCanceledException &&
                    fixture.Disposes == 1 && fixture.Unlocks == (stage == "Bootstrap" ? 1 : 0) && fixture.Frees == fixture.Allocations,
                    "observation opening cancellation returns no source metadata and releases only acquired resources");
                continue;
            }
            using var source = NtfsBoundedMftSource.OpenBitmapObservationForTests(fixture, fixture);
            if (stage == "Page") fixture.AfterRead = _ => cancellation.Cancel();
            if (stage == "Free") fixture.OnFree = cancellation.Cancel;
            if (stage == "Unlock") fixture.OnUnlock = cancellation.Cancel;
            if (stage == "Facts") fixture.OnQuery = reopen => { if (reopen) cancellation.Cancel(); };
            if (stage is "Dispose" or "DisposeFailure") fixture.OnDispose = cancellation.Cancel;
            fixture.DisposeFails = stage == "DisposeFailure";
            NtfsBitmapBootstrapSourceReport? partial = null;
            Check(Catch(() => partial = source.RunBitmapObservation(cancellation.Token)) is OperationCanceledException && partial == null &&
                fixture.Unlocks == 1 && fixture.Frees == 1 && fixture.Disposes == 1,
                "cancellation through native cleanup and final disposal discards both range and bitmap outputs");
        }
        {
            using var cancellation = new CancellationTokenSource(); using var fixture = new Fixture();
            using var source = NtfsBoundedMftSource.OpenBitmapObservationForTests(fixture, fixture);
            var primary = new IOException("SyntheticPrimaryPost"); fixture.PostError = primary;
            fixture.OnDispose = cancellation.Cancel; fixture.DisposeFails = true;
            Check(ReferenceEquals(Catch(() => source.RunBitmapObservation(cancellation.Token)), primary) && fixture.Disposes == 1 && fixture.Unlocks == 1,
                "an established verification failure keeps priority over disposal cancellation and cleanup failure");
        }
        return passed;
    }

    internal static int RunFastScanTests()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS fixture fast source check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS fixture fast source " + name);
        }
        Exception? Catch(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
        foreach (bool resident in new[] { false, true })
        {
            using var fixture = new Fixture(fast: true, residentPrefix: resident);
            using var source = NtfsBoundedMftSource.OpenFastScanForTests(fixture, fixture);
            Check(fixture.FastPreparations == 1 && fixture.Reads == (resident ? 1 : 2) && fixture.Allocations == 1,
                "fixed fast profile prepares two released witnesses before its single bootstrap and optional prefix read");
            var report = source.RunFastScan();
            Check(report is { Revision: "fixture-fast-scan-v1", Stage: "ControlledFixtureCompared" } &&
                report.Range is { EvidenceKind: "SyntheticFixture", GuardOwned: false, Closed: true },
                "simulated end-to-end flow cannot mint native fixture or production scan authority");
            Check(report.Bitmap.Form == (resident ? "Resident" : "Nonresident") && report.Bitmap is { PrefixBytes: 8, CoveredSlots: 64,
                TargetsAllocated: 21, ReleasedUnallocated: 2, ReleasedFreeHeaders: 2, Corroborated: true } &&
                report.Bitmap.ReadCalls == (resident ? 0 : 1) && report.Bitmap.RequestedBytes == (resident ? 0 : 512),
                "actual prefix inputs corroborate used targets and two free headers with incremented sequences");
            Check(report.Range.Enumeration is { ReadCalls: 2, RequestedBytes: 66560, Decoded: 44, Free: 2, Invalid: 18, Unsupported: 0 } &&
                report.Resources.ReadCalls == (resident ? 2 : 3) && report.Resources.RequestedBytes == (resident ? 66560 : 67072) && report.Resources.Closed,
                "range v2 accounting excludes the separate prefix while total resources include every native request");
            Check(report.Index is { FileObjects: 21, DirectoryObjects: 21, FileEntries: 27, RecordKnownAllocatedObjects: 7,
                NativeFallbackAllocatedObjects: 14, UnknownLogicalObjects: 0, UnknownAllocatedObjects: 0, LookupObjects: 16, CompleteWithinScope: true } &&
                report.Comparison is { ScannerComplete: true, EntriesMatched: true, CountsMatched: true, LogicalMatched: true, AllocatedMatched: true, Matched: true },
                "scoped index preserves six hard-link aliases and reconciles both size metrics against the scanner");
            Check(fixture.Unlocks == 1 && fixture.Scans == 1 && fixture.FastVerifies == 1 && fixture.Verifies == 1 && fixture.Disposes == 1 && fixture.Frees == 1,
                "scanner comparison occurs after unlocking and before final native verification and closure");
            Check(Catch(() => source.RunFastScan()) is NtfsFixtureSourceException { Code: "SourceNotActive" },
                "closed fast source cannot reuse its private bitmap decoded records or previous epoch");
            string json = JsonSerializer.Serialize(report);
            Check(!json.Contains("X:",StringComparison.Ordinal) && !json.Contains("HeaderReference",StringComparison.Ordinal) &&
                !json.Contains("SourceToken",StringComparison.Ordinal) && !json.Contains("CopyPrefix",StringComparison.Ordinal),
                "safe end-to-end report exports aggregate evidence rather than names identities bitmap or raw addresses");
        }
        foreach (string fault in new[] { "TargetBit", "ReleasedBit", "OldSequence", "WrongSequence", "TargetRecord", "Scanner", "Post", "FastPost", "Facts", "Unlock", "Free", "Dispose" })
        {
            using var fixture = new Fixture(fast: true);
            if (fault == "TargetBit") fixture.SetPrefixBit(1, false);
            if (fault == "ReleasedBit") fixture.SetPrefixBit(22, true);
            using var source = NtfsBoundedMftSource.OpenFastScanForTests(fixture, fixture);
            if (fault == "OldSequence") fixture.Corrupt(22, 16, 7);
            if (fault == "WrongSequence") fixture.Corrupt(22, 16, 9);
            if (fault == "TargetRecord") fixture.Corrupt(1, 0, (byte)'B');
            fixture.ScannerMismatch = fault == "Scanner"; fixture.PostFails = fault == "Post"; fixture.FastVerifyFails = fault == "FastPost";
            fixture.FactsChange = fault == "Facts"; fixture.UnlockFails = fault == "Unlock"; fixture.FreeFails = fault == "Free"; fixture.DisposeFails = fault == "Dispose";
            NtfsFixtureFastScanReport? partial = null; var error = Catch(() => partial = source.RunFastScan());
            Check(error is IOException && partial == null && fixture.Unlocks == 1 && fixture.Frees == 1 && fixture.Disposes == 1,
                "bit sequence target comparison or cleanup failure prevents any complete fast-scan report");
            if (fault is "TargetBit" or "ReleasedBit" or "OldSequence" or "WrongSequence")
                Check(error is NtfsFixtureSourceException { Code: "SourceBitmapCorroborationFailed" } && fixture.Scans == 0,
                    "corroboration rejects unchanged or unexpected released sequence rather than guessing wrap or reuse");
        }
        foreach (string stage in new[] { "Bootstrap", "Prefix", "Page", "Scanner", "Dispose", "DisposeFailure" })
        {
            using var cts = new CancellationTokenSource(); using var fixture = new Fixture(fast: true);
            if (stage is "Bootstrap" or "Prefix")
            {
                fixture.AfterRead = read => { if (read == (stage == "Bootstrap" ? 1 : 2)) cts.Cancel(); };
                Check(Catch(() => NtfsBoundedMftSource.OpenFastScanForTests(fixture, fixture, cts.Token)) is OperationCanceledException &&
                    fixture.Unlocks == 1 && fixture.Frees == 1 && fixture.Disposes == 1, "initialization cancellation cannot publish a private prefix or active source");
                continue;
            }
            using var source = NtfsBoundedMftSource.OpenFastScanForTests(fixture, fixture);
            if (stage == "Page") fixture.AfterRead = _ => cts.Cancel();
            if (stage == "Scanner") fixture.OnScan = cts.Cancel;
            if (stage is "Dispose" or "DisposeFailure") fixture.OnDispose = cts.Cancel;
            fixture.DisposeFails = stage == "DisposeFailure";
            NtfsFixtureFastScanReport? partial = null;
            Check(Catch(() => partial = source.RunFastScan(cts.Token)) is OperationCanceledException && partial == null && fixture.Unlocks == 1 &&
                fixture.Frees == 1 && fixture.Disposes == 1, "cancellation through indexing scanner comparison or disposal discards the entire fast result");
        }
        foreach (bool fastProfile in new[] { false, true })
        {
            using var fixture = new Fixture(fast: true); using var source = fastProfile ? NtfsBoundedMftSource.OpenFastScanForTests(fixture, fixture) :
                NtfsBoundedMftSource.OpenForTests(fixture, fixture);
            int prior = fixture.Reads; var error = fastProfile ? Catch(() => source.RunEnumeration()) : Catch(() => source.RunFastScan());
            Check(error is NtfsFixtureSourceException { Code: "SourceProfileMismatch" } && fixture.Reads == prior && fixture.Unlocks == 1 && fixture.Disposes == 1,
                "fast and older range entries cannot exchange profiles or perform a late extra read");
        }
        foreach (bool duplicate in new[] { false, true })
        {
            using var fixture = new Fixture(fast: true) { DuplicateWitnessSlot = duplicate, WitnessSequence = duplicate ? (ushort)7 : ushort.MaxValue };
            Check(Catch(() => NtfsBoundedMftSource.OpenFastScanForTests(fixture, fixture)) is NtfsFixtureSourceException { Code: "SourceWitnessInvalid" } &&
                fixture.Locks == 0 && fixture.Reads == 0 && fixture.Disposes == 1,
                "duplicate released slots and unimplemented sequence wrap fail before any raw-volume lock or read");
        }
        {
            using var fixture = new Fixture(fast: true, bitmapAttributes: []);
            Check(Catch(() => NtfsBoundedMftSource.OpenFastScanForTests(fixture, fixture)) is NtfsFixtureSourceException { Code: "SourceFastBitmapFailed" } &&
                fixture.Reads == 1 && fixture.Unlocks == 1 && fixture.Disposes == 1, "missing bitmap prefix evidence cannot be replaced with zero-filled allocation");
        }
        return passed;
    }
}
