using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DiskAtlas.NtfsPrototype;

// Memory-backed metadata and reads only. No volume/COM/file APIs are invoked.
internal static class NtfsSnapshotVolumeReaderTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("Snapshot reader " + name); passed++; Console.WriteLine("PASS snapshot reader " + name); }
        void Rejected(Action action, string code, string name)
        {
            bool rejected = false;
            try { action(); } catch (NtfsVssProbeException error) { rejected = error.Code == code && error.Message == code && error.InnerException == null; }
            Check(rejected, name);
        }
        var fake = new Fake();
        using (var reader = NtfsSnapshotVolumeReader.ForTests(fake, 41, 1024 * 1024))
        {
            var facts = reader.Facts();
            Check(reader.IsSynthetic && facts.Serial == 41 && facts.Geometry is { SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024, VolumeBytes: 1048576 },
                "snapshot metadata binds the selected source without inventing physical-sector facts");
            Check(reader.Facts() == facts && fake.FactsCalls == 1, "immutable metadata is queried once per reader");
            byte[] target = Enumerable.Repeat((byte)0x17, 2048).ToArray();
            Check(reader.ReadInto(4096, target.AsMemory(512, 1024)) == 1024 && target[..512].All(value => value == 0x17) &&
                target[512..1536].All(value => value == 0x62) && target[1536..].All(value => value == 0x17), "aligned read copies only the requested destination segment");
            Check(reader.ReadCalls == 1 && reader.RequestedBytes == 1024 && fake.SeekCalls == 1 && fake.ReadCalls == 1 && fake.Allocations == 1,
                "budget counts actual read attempts and reuses one private aligned buffer");
            reader.Read(8192, 1024); Check(fake.Allocations == 1 && reader.ReadCalls == 2, "subsequent reads cannot reset the attempt budget");
        }
        Check(fake.Closed && fake.Frees == 1 && fake.Disposes == 1, "reader releases its private allocation and snapshot handle exactly once");
        foreach (var args in new (long Offset, int Length)[] { (-512, 512), (1, 512), (0, 0), (0, 513), (0, 16896), (1048576, 512), (long.MaxValue, 512) })
        {
            var bad = new Fake(); using var reader = NtfsSnapshotVolumeReader.ForTests(bad, 41, 1024 * 1024);
            Rejected(() => reader.ReadInto(args.Offset, new byte[args.Length]), "VssRawRead", "invalid range fails before seek allocation or raw read");
            Check(bad.ReadCalls == 0 && bad.SeekCalls == 0 && bad.Allocations == 0, "invalid read cannot consume native I/O");
        }
        using (var reader = NtfsSnapshotVolumeReader.ForTests(new Fake(), 41, 1024 * 1024))
        {
            for (int i = 0; i < 16; i++) reader.Read(0, 512);
            Rejected(() => reader.Read(0, 512), "VssRawRead", "old probe keeps its sixteen-call cap");
        }
        using (var reader = NtfsSnapshotVolumeReader.ForTests(new Fake(), 41, 1024 * 1024))
        {
            reader.Read(0, 16384); Rejected(() => reader.Read(0, 512), "VssRawRead", "old probe keeps its sixteen-KiB aggregate cap");
        }
        using (var reader = NtfsSnapshotVolumeReader.ForTests(new Fake(), 41, 1024 * 1024, scale: true))
        {
            byte[] block = new byte[512]; for (int i = 0; i < 8192; i++) reader.ReadInto(0, block);
            Rejected(() => reader.ReadInto(0, block), "VssRawRead", "scale stops after exactly 8192 attempts");
            Check(reader.ReadCalls == 8192 && reader.RequestedBytes == 8192L * 512, "scale call cap has no hidden retries or reset");
        }
        using (var reader = NtfsSnapshotVolumeReader.ForTests(new Fake(), 41, 1024 * 1024, scale: true))
        {
            byte[] block = new byte[65536]; for (int i = 0; i < 4097; i++) reader.ReadInto(0, block);
            Check(reader.RequestedBytes == 256L * 1024 * 1024 + 65536, "scale consumes only its explicitly larger aggregate profile");
            Rejected(() => reader.ReadInto(0, new byte[512]), "VssRawRead", "scale cannot exceed 256 MiB plus 64 KiB");
        }
        foreach (var alteration in new (int Offset, long Value, bool Wide)[] { (0, 42, true), (8, 999, true), (16, 0, true), (16, 9999999, true),
            (40, 4096, false), (44, 8192, false), (48, 2048, false), (56, 0, true), (56, 1025, true), (56, 2097152, true),
            (64, -1, true), (64, 256, true) })
        {
            var bad = new Fake(); if (alteration.Wide) W64(bad.Metadata, alteration.Offset, alteration.Value); else W32(bad.Metadata, alteration.Offset, checked((int)alteration.Value));
            using var reader = NtfsSnapshotVolumeReader.ForTests(bad, 41, 1024 * 1024);
            Rejected(() => reader.Facts(), "VssRawGeometry", "snapshot serial geometry MFT length and bootstrap location remain bound");
            Check(bad.ReadCalls == 0, "bad metadata cannot authorize a raw read");
        }
        foreach (int count in new[] { 0, 103, 129 })
        { using var reader = NtfsSnapshotVolumeReader.ForTests(new Fake { Used = count }, 41, 1024 * 1024); Rejected(() => reader.Facts(), "VssRawGeometry", "metadata response length is exact bounded"); }
        foreach (string stage in new[] { "facts", "allocate", "seek", "read" })
        foreach (bool forgedCancel in new[] { false, true })
        {
            var broken = new Fake { ThrowAt = stage, ForgedCancel = forgedCancel }; using var reader = NtfsSnapshotVolumeReader.ForTests(broken, 41, 1024 * 1024);
            Rejected(() => reader.Read(0, 512), stage == "facts" ? "VssRawGeometry" : "VssRawRead", "native exceptions cannot impersonate controlled error codes or caller cancellation");
        }
        foreach (string stage in new[] { "facts", "allocate", "seek", "read" })
        {
            using var cancellation = new CancellationTokenSource(); var cancelled = new Fake { Callback = step => { if (step == stage) cancellation.Cancel(); } };
            using var reader = NtfsSnapshotVolumeReader.ForTests(cancelled, 41, 1024 * 1024); byte[] destination = Enumerable.Repeat((byte)7, 512).ToArray(); bool caught = false;
            try { reader.ReadInto(0, destination, cancellation.Token); } catch (OperationCanceledException error) { caught = error.CancellationToken == cancellation.Token; }
            Check(caught && destination.All(value => value == 7), "cancellation after each native boundary publishes no caller bytes");
            reader.Dispose(); Check(cancelled.Frees == cancelled.Allocations && cancelled.Closed, "cancelled allocation is still adopted and freed before source cleanup");
        }
        var failed = new Fake { ShortRead = true }; using (var reader = NtfsSnapshotVolumeReader.ForTests(failed, 41, 1024 * 1024))
        {
            Rejected(() => reader.Read(0, 512), "VssRawRead", "short raw read is never silently accepted");
            Check(reader.ReadCalls == 1 && reader.RequestedBytes == 512, "short read still consumes its one attempted request");
        }
        var freeFailure = new Fake { FreeFailure = true }; var cleanupReader = NtfsSnapshotVolumeReader.ForTests(freeFailure, 41, 1024 * 1024); cleanupReader.Read(0, 512);
        Rejected(cleanupReader.Dispose, "VssCleanup", "buffer cleanup failure is visible");
        cleanupReader.Dispose(); Check(!cleanupReader.BufferReleased && freeFailure.Disposes == 1, "late dispose cannot erase a known cleanup failure");
        return passed;
    }
    private sealed class Fake : NtfsSnapshotVolumeReader.INative
    {
        internal readonly byte[] Metadata = new byte[128], Data = Enumerable.Repeat((byte)0x62, 65536).ToArray();
        internal int Used = 104, FactsCalls, SeekCalls, ReadCalls, Allocations, Frees, Disposes;
        internal bool Closed, ForgedCancel, ShortRead, FreeFailure;
        internal string ThrowAt = "";
        internal Action<string>? Callback;
        internal Fake()
        {
            W64(Metadata, 0, 41); W64(Metadata, 8, 1048576 / 512); W64(Metadata, 16, 1048576 / 4096);
            W32(Metadata, 40, 512); W32(Metadata, 44, 4096); W32(Metadata, 48, 1024); W64(Metadata, 56, 65536); W64(Metadata, 64, 2);
            BinaryPrimitives.WriteUInt16LittleEndian(Metadata.AsSpan(100), 3); BinaryPrimitives.WriteUInt16LittleEndian(Metadata.AsSpan(102), 1);
        }
        private void Before(string stage)
        { Callback?.Invoke(stage); if (ThrowAt == stage) { if (ForgedCancel) throw new OperationCanceledException("private-marker"); throw new NtfsVssProbeException("private-marker"); } }
        public NtfsSnapshotVolumeReader.ControlResult Facts(byte[] output)
        { FactsCalls++; Before("facts"); Metadata.CopyTo(output, 0); return new(true, Used, 0); }
        public NtfsSnapshotVolumeReader.Result Seek(long offset) { SeekCalls++; Before("seek"); return new(true, 0); }
        public NtfsSnapshotVolumeReader.ReadResult Read(IntPtr buffer, int length)
        { ReadCalls++; Before("read"); Marshal.Copy(Data, 0, buffer, length); return new(true, ShortRead ? length - 512 : length, 0); }
        public IntPtr Allocate(int bytes)
        { if (ThrowAt == "allocate") Before("allocate"); IntPtr value = Marshal.AllocHGlobal(bytes); Allocations++; Callback?.Invoke("allocate"); return value; }
        public void Free(IntPtr buffer) { Frees++; Marshal.FreeHGlobal(buffer); if (FreeFailure) throw new IOException("private-marker"); }
        public void Dispose() { Disposes++; Closed = true; }
    }
    private static void W32(byte[] value, int offset, int number) => BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(offset), number);
    private static void W64(byte[] value, int offset, long number) => BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(offset), number);
}
