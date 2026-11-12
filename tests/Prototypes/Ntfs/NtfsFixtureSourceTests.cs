using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

// Deterministic in-memory record bytes, inert handles and fake native operations.
// This is not a formatted filesystem and never opens a volume, file or snapshot.
internal static class NtfsFixtureSourceTests
{
    private sealed class Fixture : NtfsFixtureSource.IBackend, NtfsRawVolumeLease.INative
    {
        internal const int MftOffset = 10 * 4096;
        private readonly byte[] image = new byte[1024 * 1024];
        private readonly SafeFileHandle volume = new(new IntPtr(12345), false);
        private long offset;
        private bool disposed;
        internal int Locks, Unlocks, Reads, Allocations, Frees, Captures, Verifies, Queries, Disposes;
        internal bool LockFails, UnlockFails, PostFails, FactsChange, DisposeFails;
        internal int FailReadAt, ShortReadAt;
        internal Action<int>? AfterRead;
        internal Action<bool>? OnQuery;
        internal Action? OnDispose;
        internal Exception? PostError;
        internal List<NtfsRawTarget> Targets { get; } = [];
        internal NtfsRawVolumeFacts Facts { get; set; } = new(
            new(512, 4096, 1024, 1024 * 1024, 256, 10, 65536), 41, 4096);
        public SafeFileHandle Volume => volume;

        internal Fixture()
        {
            Put(0, NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("$MFT"), Data(65536, 65536, 16, 0)]));
            int index = 0;
            foreach (var item in NtfsKernelMatrix.Cases)
            {
                int slot = ++index;
                ulong parent = NtfsRecordFixtures.Reference((ulong)(31 + index), 5);
                var attributes = new List<byte[]> { NtfsRecordFixtures.Name(NtfsKernelMatrix.FileName(item.NameLength), 1, parent) };
                if (item.HardLink) attributes.Add(NtfsRecordFixtures.Name("link.bin", 1, parent));
                ushort flags = item.Kind == "Compressed" ? (ushort)1 : item.Kind == "Sparse" ? (ushort)0x8000 : (ushort)0;
                attributes.Add(Data(item.LogicalBytes, item.Kind == "Ordinary" ? 131072 : 8192,
                    item.Kind == "Ordinary" ? (byte)32 : (byte)2, flags));
                if (item.NamedStream) attributes.Add(NtfsRecordFixtures.Data(1, "meta"));
                Put(slot, NtfsRecordFixtures.Record(attributes, links: item.HardLink ? (ushort)2 : (ushort)1));
                var storage = new FileStorageInfo(new(41, NtfsRecordFixtures.Reference((ulong)slot, 3), 0),
                    item.Kind == "Ordinary" ? 131072 : 4096, item.HardLink ? 2u : 1u, false, item.LogicalBytes,
                    new DateTime(2020, 1, 1));
                string directory = @"X:\Synthetic\" + item.CaseId;
                FileAttributes attrib = FileAttributes.Archive | (item.Kind == "Compressed" ? FileAttributes.Compressed :
                    item.Kind == "Sparse" ? FileAttributes.SparseFile : 0);
                Targets.Add(new(item.CaseId, Path.Combine(directory, NtfsKernelMatrix.FileName(item.NameLength)), directory,
                    storage, new(new(41, parent, 0), 0, 1, false, -1, new DateTime(2020, 1, 1)), attrib));
            }
        }
        private static byte[] Data(long logical, long allocated, byte clusters, ushort flags)
        {
            int header = flags == 0 ? 64 : 72;
            var data = new byte[header + 8];
            NtfsRecordFixtures.U32(data, 0, 0x80); NtfsRecordFixtures.U32(data, 4, (uint)data.Length); data[8] = 1;
            NtfsRecordFixtures.U16(data, 12, flags); NtfsRecordFixtures.U64(data, 24, (ulong)(clusters - 1));
            NtfsRecordFixtures.U16(data, 32, (ushort)header); NtfsRecordFixtures.U16(data, 34, flags == 1 ? (ushort)4 : (ushort)0);
            NtfsRecordFixtures.U64(data, 40, (ulong)allocated); NtfsRecordFixtures.U64(data, 48, (ulong)logical);
            NtfsRecordFixtures.U64(data, 56, (ulong)logical);
            if (header == 72) NtfsRecordFixtures.U64(data, 64, (ulong)allocated);
            data[header] = 0x11; data[header + 1] = clusters; data[header + 2] = 10;
            return data;
        }
        private void Put(int slot, byte[] data) => data.CopyTo(image, MftOffset + slot * 1024);
        internal void Corrupt(int slot, int position, byte value) => image[MftOffset + slot * 1024 + position] = value;
        public IReadOnlyList<NtfsRawTarget> CaptureFixedTargets(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Captures++; return Array.AsReadOnly(Targets.ToArray()); }
        public NtfsRawVolumeFacts QueryFacts(bool reopen, CancellationToken token)
        {
            Queries++; OnQuery?.Invoke(reopen); token.ThrowIfCancellationRequested();
            return reopen && FactsChange ? Facts with { Geometry = Facts.Geometry with { MftValidDataLength = 66560 } } : Facts;
        }
        public void VerifyFixedTargets(IReadOnlyList<NtfsRawTarget> baseline, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Verifies++; if (PostError != null) throw PostError; if (PostFails) throw new IOException("SyntheticPostFailure"); }
        public NtfsRawVolumeLease.NativeResult Lock(SafeFileHandle _) { Locks++; return new(!LockFails, LockFails ? 5 : 0); }
        public NtfsRawVolumeLease.NativeResult Unlock(SafeFileHandle _) { Unlocks++; return new(!UnlockFails, UnlockFails ? 5 : 0); }
        public NtfsRawVolumeLease.NativeResult Seek(SafeFileHandle _, long value) { offset = value; return new(true, 0); }
        public NtfsRawVolumeLease.NativeReadResult Read(SafeFileHandle _, IntPtr buffer, int length)
        {
            Reads++;
            if (Reads == FailReadAt) return new(false, 0, 5);
            int count = Reads == ShortReadAt ? length - 512 : length;
            Marshal.Copy(image, checked((int)offset), buffer, count);
            AfterRead?.Invoke(Reads);
            return new(true, count, 0);
        }
        public IntPtr Allocate(int bytes) { Allocations++; return Marshal.AllocHGlobal(bytes); }
        public void Free(IntPtr allocation) { Frees++; Marshal.FreeHGlobal(allocation); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Disposes++; volume.Dispose();
            OnDispose?.Invoke();
            if (DisposeFails) throw new IOException("SyntheticCleanupFailure");
        }
    }

    private static NtfsKernelMatrixReport Control()
    {
        var rows = new List<NtfsKernelMatrixRow>(63);
        foreach (var item in NtfsKernelMatrix.Cases)
            foreach (var stage in NtfsKernelMatrix.ReadStages)
                rows.Add(new(item.CaseId + "-" + stage, stage, item.Kind, item.NameLength, "Decoded", "Decoded", 2, 0, 2, 512, item.HardLink ? 2 : 1));
        return new(NtfsKernelMatrix.Revision, 21, 63, 63, 0, 0, 0, 0, rows.AsReadOnly());
    }

    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS fixture source check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS fixture source " + name);
        }
        Exception? Catch(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
        void Rejected(Action action, string code, string name) =>
            Check(Catch(action) is NtfsFixtureSourceException error && error.Code == code, name);
        var first = NtfsKernelMatrix.Cases[0];
        {
            using var fixture = new Fixture(); using var source = NtfsFixtureSource.OpenForTests(fixture, fixture);
            Check(fixture.Locks == 1 && fixture.Reads == 1 && fixture.Captures == 1 && fixture.Queries == 1,
                "opening a synthetic source acquires one lock and a strict record-zero map");
            var result = source.ReadKnown(source.Token, first.CaseId);
            source.ValidateResult(source.Token, result);
            Check(result.Value.LogicalBytes == first.LogicalBytes && fixture.Reads == 2,
                "known record envelope is bound to the active source and strict decoded value");
            Check(ReferenceEquals(source.ReadKnown(source.Token, first.CaseId), result) && fixture.Reads == 2,
                "same-source repeated reads reuse the same bound envelope without consuming more I/O");
            var report = source.RunFixedComparison(Control());
            Check(report is { Revision: "fixture-source-v1", EvidenceKind: "SyntheticFixture", GuardOwned: false,
                TokenBound: true, MapBound: true, Closed: true, RejectedReads: 0 },
                "even perfect fake operations cannot mint native-owned fixture evidence");
            Check(report.Comparison is { RawRecords: 22, RawDecoded: 22, MatchedObjects: 21, UnknownAllocationObjects: 14,
                ReadCalls: 22, RequestedBytes: 22528, UnlockSucceeded: true, PostNativeStable: true },
                "fixed-scope output preserves strict counts and unknown special allocation");
            Check(fixture.Unlocks == 1 && fixture.Verifies == 1 && fixture.Disposes == 1 && fixture.Allocations == fixture.Frees,
                "successful closure verifies after unlocking and releases its owned backend once");
            Rejected(() => _ = result.Value, "SourceNotActive", "an envelope cannot be unpacked after its owner closes");
            Rejected(() => source.ReadKnown(source.Token, first.CaseId), "SourceNotActive", "closed sources cannot reopen their old map");
            string json = JsonSerializer.Serialize(report);
            Check(!json.Contains("X:", StringComparison.Ordinal) && !json.Contains("SourceStamp", StringComparison.Ordinal) &&
                !json.Contains("\"Reference\"", StringComparison.Ordinal), "controlled report carries no paths full references or source stamps");
        }
        {
            using var left = new Fixture(); using var right = new Fixture();
            using var a = NtfsFixtureSource.OpenForTests(left, left); using var b = NtfsFixtureSource.OpenForTests(right, right);
            var other = b.ReadKnown(b.Token, first.CaseId);
            Rejected(() => a.ReadKnown(b.Token, first.CaseId), "SourceTokenMismatch", "identical geometry and references do not permit a foreign source token");
            Rejected(() => a.ValidateResult(a.Token, other), "SourceResultMismatch", "a foreign envelope cannot become current by using a local token");
            Rejected(() => a.ReadKnown(new NtfsFixtureSourceToken(), first.CaseId), "SourceTokenMismatch", "a caller-created token has no authority");
            var own = a.ReadKnown(a.Token, first.CaseId);
            Rejected(() => a.ValidateResult(a.Token, new NtfsFixtureSourceReadResult(a, first.CaseId)), "SourceResultMismatch",
                "a forged envelope cannot replace the registered read instance");
            Check(left.Reads == 2 && own.Value.LogicalBytes == first.LogicalBytes,
                "rejected requests neither read additional records nor damage the valid owner");
            Check(a.RunFixedComparison(Control()).RejectedReads == 4, "only controlled active-source rejection counts enter the report");
        }
        foreach (string fault in new[] { "Missing", "Duplicate", "ForeignVolume", "WrongSize", "WrongLinks", "HighIdentity" })
        {
            using var fixture = new Fixture();
            if (fault == "Missing") fixture.Targets.RemoveAt(20);
            else if (fault == "Duplicate") fixture.Targets[20] = fixture.Targets[0];
            else
            {
                var target = fixture.Targets[0]; var storage = target.Storage;
                storage = fault switch
                {
                    "ForeignVolume" => storage with { Identity = storage.Identity with { Volume = 42 } },
                    "WrongSize" => storage with { LogicalBytes = storage.LogicalBytes + 1 },
                    "WrongLinks" => storage with { LinkCount = 99 },
                    _ => storage with { Identity = storage.Identity with { High = 1 } }
                };
                fixture.Targets[0] = target with { Storage = storage };
            }
            Check(Catch(() => NtfsFixtureSource.OpenForTests(fixture, fixture)) is NtfsFixtureSourceException &&
                fixture.Locks == 0 && fixture.Reads == 0 && fixture.Disposes == 1,
                "invalid target scope or native context fails before locking and releases its owner");
        }
        foreach (string fault in new[] { "Lock", "Bootstrap", "ShortBootstrap" })
        {
            using var fixture = new Fixture();
            if (fault == "Lock") fixture.LockFails = true;
            else if (fault == "Bootstrap") fixture.Corrupt(0, 0, (byte)'B');
            else fixture.ShortReadAt = 1;
            var error = Catch(() => NtfsFixtureSource.OpenForTests(fixture, fixture));
            Check(error is NtfsFixtureSourceException && fixture.Disposes == 1 &&
                fixture.Unlocks == (fault == "Lock" ? 0 : 1) && fixture.Allocations == fixture.Frees,
                "failed source construction releases only acquired resources and returns no owner");
        }
        foreach (string fault in new[] { "Read", "ShortRead", "Strict", "Sequence" })
        {
            using var fixture = new Fixture(); using var source = NtfsFixtureSource.OpenForTests(fixture, fixture);
            var earlier = source.ReadKnown(source.Token, first.CaseId);
            string next = NtfsKernelMatrix.Cases[1].CaseId;
            if (fault == "Read") fixture.FailReadAt = 3;
            else if (fault == "ShortRead") fixture.ShortReadAt = 3;
            else if (fault == "Strict") fixture.Corrupt(2, 0, (byte)'B');
            else fixture.Corrupt(2, 16, 4);
            Check(Catch(() => source.ReadKnown(source.Token, next)) is NtfsFixtureSourceException && fixture.Unlocks == 1 &&
                fixture.Disposes == 1 && fixture.Allocations == fixture.Frees, "native strict or identity failure invalidates the entire source");
            Rejected(() => _ = earlier.Value, "SourceNotActive", "previous envelopes become unusable after a later source failure");
        }
        foreach (string fault in new[] { "Unlock", "Post", "Facts", "Cleanup" })
        {
            using var fixture = new Fixture(); using var source = NtfsFixtureSource.OpenForTests(fixture, fixture);
            if (fault == "Unlock") fixture.UnlockFails = true;
            if (fault == "Post") fixture.PostFails = true;
            if (fault == "Facts") fixture.FactsChange = true;
            if (fault == "Cleanup") fixture.DisposeFails = true;
            Check(Catch(() => source.RunFixedComparison(Control())) is IOException && fixture.Disposes == 1 && fixture.Unlocks == 1,
                "unlock post-verification fact or cleanup failure never returns a successful report");
            Check(fixture.Allocations == fixture.Frees, "failed closure leaves no owned native buffer");
        }
        foreach (string location in new[] { "Open", "Bootstrap", "Known", "Cached", "FinalFacts" })
        {
            using var cts = new CancellationTokenSource(); using var fixture = new Fixture();
            if (location == "Open")
            {
                cts.Cancel();
                Check(Catch(() => NtfsFixtureSource.OpenForTests(fixture, fixture, cts.Token)) is OperationCanceledException &&
                    fixture.Locks == 0 && fixture.Disposes == 1, "cancelled opening never acquires a volume lock");
                continue;
            }
            if (location == "Bootstrap") fixture.AfterRead = _ => cts.Cancel();
            if (location == "Bootstrap")
            {
                Check(Catch(() => NtfsFixtureSource.OpenForTests(fixture, fixture, cts.Token)) is OperationCanceledException &&
                    fixture.Unlocks == 1 && fixture.Disposes == 1 && fixture.Allocations == fixture.Frees,
                    "cancellation after bootstrap I/O waits for completion then releases every resource");
                continue;
            }
            using var source = NtfsFixtureSource.OpenForTests(fixture, fixture);
            if (location == "Cached") _ = source.ReadKnown(source.Token, first.CaseId);
            if (location == "FinalFacts") fixture.OnQuery = reopen => { if (reopen) cts.Cancel(); };
            else cts.Cancel();
            Exception? error = location == "FinalFacts" ? Catch(() => source.RunFixedComparison(Control(), cts.Token)) :
                Catch(() => source.ReadKnown(source.Token, first.CaseId, cts.Token));
            Check(error is OperationCanceledException && fixture.Unlocks == 1 && fixture.Disposes == 1 && fixture.Allocations == fixture.Frees,
                "cancellation at cached read or final metadata query cannot produce a usable source or report");
        }
        foreach (string fault in new[] { "Count", "Stage", "Duplicate", "Kind", "Code", "Retained" })
        {
            using var fixture = new Fixture(); using var source = NtfsFixtureSource.OpenForTests(fixture, fixture);
            var control = Control(); var rows = control.Rows.ToArray();
            if (fault == "Count") control = control with { Requests = 62 };
            if (fault == "Stage") rows[0] = rows[0] with { Stage = "Other" };
            if (fault == "Duplicate") rows[1] = rows[0];
            if (fault == "Kind") rows[0] = rows[0] with { Kind = "Other" };
            if (fault == "Code") rows[0] = rows[0] with { Code = "Injected" };
            if (fault == "Retained") control = control with { Captured = 1 };
            control = control with { Rows = rows };
            Rejected(() => source.RunFixedComparison(control), "SourceControlInvalid", "partial forged or retained control output cannot claim a fixed-scope comparison");
            Check(fixture.Reads == 1 && fixture.Unlocks == 1 && fixture.Disposes == 1,
                "invalid comparison metadata fails before object reads and closes the source");
        }
        foreach (bool cleanupFails in new[] { false, true })
        {
            using var cts = new CancellationTokenSource(); using var fixture = new Fixture();
            using var source = NtfsFixtureSource.OpenForTests(fixture, fixture);
            var earlier = source.ReadKnown(source.Token, first.CaseId);
            fixture.OnDispose = cts.Cancel; fixture.DisposeFails = cleanupFails;
            Check(Catch(() => source.RunFixedComparison(Control(), cts.Token)) is OperationCanceledException &&
                fixture.Unlocks == 1 && fixture.Disposes == 1 && fixture.Allocations == fixture.Frees,
                "cancellation delivered by synchronous owner disposal prevents final evidence even if cleanup also fails");
            Rejected(() => _ = earlier.Value, "SourceNotActive", "dispose-time cancellation leaves no active result envelope");
        }
        {
            using var cts = new CancellationTokenSource(); using var fixture = new Fixture();
            using var source = NtfsFixtureSource.OpenForTests(fixture, fixture);
            var primary = new IOException("SyntheticPrimaryPostFailure");
            fixture.PostError = primary; fixture.OnDispose = cts.Cancel; fixture.DisposeFails = true;
            Check(ReferenceEquals(Catch(() => source.RunFixedComparison(Control(), cts.Token)), primary) &&
                fixture.Unlocks == 1 && fixture.Disposes == 1 && fixture.Allocations == fixture.Frees,
                "an existing verification error survives simultaneous disposal cancellation and cleanup failure");
        }
        {
            using var fixture = new Fixture(); using var source = NtfsFixtureSource.OpenForTests(fixture, fixture);
            int rejected = 0;
            for (int i = 0; i < 130; i++)
                if (Catch(() => source.ReadKnown(new NtfsFixtureSourceToken(), first.CaseId)) is NtfsFixtureSourceException { Code: "SourceTokenMismatch" }) rejected++;
            Check(rejected == 130 && fixture.Reads == 1 && source.RunFixedComparison(Control()).RejectedReads == 128,
                "rejected-request evidence saturates without unbounded work or extra native reads");
        }
        return passed;
    }
}
