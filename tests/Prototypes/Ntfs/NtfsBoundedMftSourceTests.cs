using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

// Memory-backed streams and an inert handle only. The test factory can never
// mint native-owned evidence, regardless of its simulated success.
internal static class NtfsBoundedMftSourceTests
{
    private sealed class Fixture : NtfsFixtureSource.IBackend, NtfsRawVolumeLease.INative
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
        internal List<(long Offset, int Length)> Windows { get; } = [];
        internal List<NtfsRawTarget> Targets { get; } = [];
        internal NtfsRawVolumeFacts Facts { get; set; }
        public SafeFileHandle Volume => volume;

        internal Fixture(int slots = 64, bool fragmented = false)
        {
            this.fragmented = fragmented;
            long valid = slots * 1024L;
            int clusters = checked((int)((valid + 4095) / 4096));
            int imageBytes = Math.Max(1024 * 1024, MftOffset + clusters * 4096);
            image = new byte[imageBytes];
            Facts = new(new(512, 4096, 1024, imageBytes, imageBytes / 4096, 10, valid), 41, 4096);
            Put(0, NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("$MFT"), Data(valid, clusters * 4096L, clusters, 0, fragmented)]));
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
        public IReadOnlyList<NtfsRawTarget> CaptureFixedTargets(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Captures++; return Array.AsReadOnly(Targets.ToArray()); }
        public NtfsRawVolumeFacts QueryFacts(bool reopen, CancellationToken token)
        {
            Queries++; OnQuery?.Invoke(reopen); token.ThrowIfCancellationRequested();
            return reopen && FactsChange ? Facts with { Geometry = Facts.Geometry with { MftValidDataLength = Facts.Geometry.MftValidDataLength + 1024 } } : Facts;
        }
        public void VerifyFixedTargets(IReadOnlyList<NtfsRawTarget> baseline, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Verifies++; if (PostError != null) throw PostError; if (PostFails) throw new IOException("SyntheticPostFailure"); }
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
            Check(report is { Revision: "bounded-mft-source-v1", EvidenceKind: "SyntheticFixture", GuardOwned: false,
                TokenBound: true, MapBound: true, Closed: true, RejectedReads: 0 }, "fake factory never mints native-owned source evidence");
            Check(enumeration.ValidBytes == slots * 1024L && enumeration.ExpectedSlots == slots && enumeration.VisitedSlots == slots &&
                enumeration.Decoded == 22 && enumeration.Free == 1 && enumeration.Unsupported == 1 && enumeration.Invalid == slots - 24 && enumeration.CompletedCursor,
                "full range retains all four strict classifications including non-target invalid records");
            Check(enumeration.LookupRequired == 16 && enumeration.UnknownLogical == 0 && enumeration.UnknownAllocated == 14 &&
                enumeration.MatchedObjects == 21 && enumeration.UnknownAllocationObjects == 14 && enumeration.Cases.Count == 21,
                "fixed targets match while special allocation remains unknown without global totals");
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
}
