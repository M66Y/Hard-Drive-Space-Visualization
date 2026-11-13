using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsSnapshotVolumeFacts(NtfsMftGeometry Geometry, ulong Serial);

// Buffered reads from the exact device returned by this owner. Never locks or
// writes a volume. One reader consumes the owner's non-resetting attempt budget.
internal sealed partial class NtfsSnapshotVolumeReader : IDisposable
{
    internal readonly record struct Result(bool Success, int Error);
    internal readonly record struct ControlResult(bool Success, int Used, int Error);
    internal readonly record struct ReadResult(bool Success, int Used, int Error);
    internal interface INative : IDisposable
    {
        ControlResult Facts(byte[] output);
        Result Seek(long offset);
        ReadResult Read(IntPtr buffer, int length);
        IntPtr Allocate(int bytes);
        void Free(IntPtr buffer);
    }
    internal const int ProbeMaximumCalls = 16, ProbeMaximumBytes = 16384;
    internal const int ScaleMaximumCalls = 8192;
    internal const long ScaleMaximumBytes = 256L * 1024 * 1024 + 65536;
    internal const long PersonalMaximumBytes = 256L * 1024 * 1024 + 1024 * 1024;
    internal const long LargePersonalMaximumBytes = 1024L * 1024 * 1024 + 2L * 1024 * 1024;
    internal const int LargePersonalMaximumCalls = 65536;
    private readonly object gate = new();
    private readonly INative native;
    private readonly ulong serial;
    private readonly long volumeBytes;
    private readonly bool scale, personal, largePersonal;
    private IntPtr allocation, aligned;
    private NtfsSnapshotVolumeFacts? facts;
    private int readCalls;
    private long requestedBytes;
    private bool disposed, bufferReleased = true;
    internal bool IsSynthetic { get; }
    internal int ReadCalls { get { lock (gate) return readCalls; } }
    internal long RequestedBytes { get { lock (gate) return requestedBytes; } }
    internal bool BufferReleased { get { lock (gate) return bufferReleased; } }
    private NtfsSnapshotVolumeReader(INative native, ulong serial, long volumeBytes, bool scale, bool synthetic, bool personal = false, bool largePersonal = false)
    { this.native = native; this.serial = serial; this.volumeBytes = volumeBytes; this.scale = scale; this.personal = personal; this.largePersonal = personal && largePersonal; IsSynthetic = synthetic; }
    internal static NtfsSnapshotVolumeReader OpenPersonal(NtfsPersonalSnapshotSource owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var binding = owner.TakeReadBinding();
        var native = new Native(binding.Device);
        var reader = new NtfsSnapshotVolumeReader(native, binding.Serial, binding.VolumeBytes, true, false, true, ReferenceEquals(binding.Limits, NtfsScanLimits.Large));
        try { owner.TrackReader(reader); token.ThrowIfCancellationRequested(); return reader; }
        catch { reader.Dispose(); throw; }
    }
    internal static NtfsSnapshotVolumeReader ForPersonalTests(INative native, ulong serial, long volumeBytes, bool large = false) =>
        new(native, serial, volumeBytes, true, true, true, large);
    // This seam never opens a device and can never be passed into the real owner.
    internal static NtfsSnapshotVolumeReader ForTests(INative native, ulong serial, long volumeBytes, bool scale = false) =>
        new(native, serial, volumeBytes, scale, true);
    private static void Require(bool value, string code, int error = 0)
    { if (!value) throw new NtfsVssProbeException(code, error); }
    private static T External<T>(Func<T> call, CancellationToken token)
    { try { return call(); } catch { token.ThrowIfCancellationRequested(); throw new NtfsVssProbeException("VssRawRead"); } }
    internal NtfsSnapshotVolumeFacts Facts(CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested(); Require(!disposed, "VssRawGeometry");
            if (facts != null) return facts;
            byte[] bytes = new byte[128]; ControlResult result;
            try { result = native.Facts(bytes); }
            catch { token.ThrowIfCancellationRequested(); throw new NtfsVssProbeException("VssRawGeometry"); }
            token.ThrowIfCancellationRequested();
            Require(result.Success && result.Used is >= 104 and <= 128, "VssRawGeometry", result.Error);
            Require(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(100)) == 3 && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(102)) == 1,
                "VssRawGeometry");
            long sectors = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)), clusters = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16));
            long valid = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(56)), lcn = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(64));
            int sector = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)), cluster = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(44)), record = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(48));
            Require(BinaryPrimitives.ReadUInt64LittleEndian(bytes) == serial && sector == 512 && cluster == 4096 && record == 1024 &&
                volumeBytes > 0 && (personal || volumeBytes <= 1024L * 1024 * 1024) && volumeBytes % 512 == 0 && sectors == volumeBytes / 512 &&
                clusters > 0 && clusters <= volumeBytes / 4096 && lcn >= 0 && lcn < clusters && valid >= 1024 && valid <= volumeBytes && valid % 1024 == 0 &&
                (!scale || personal || valid / 1024 <= 262144), "VssRawGeometry");
            facts = new(new(512, 4096, 1024, volumeBytes, clusters, lcn, valid), serial); return facts;
        }
    }
    internal int ReadInto(long offset, Memory<byte> destination, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested(); Require(!disposed, "VssRawRead");
            _ = Facts(token);
            int length = destination.Length, maximumLength = scale ? 65536 : ProbeMaximumBytes;
            long maximumBytes = largePersonal ? LargePersonalMaximumBytes : personal ? PersonalMaximumBytes : scale ? ScaleMaximumBytes : ProbeMaximumBytes;
            int maximumCalls = largePersonal ? LargePersonalMaximumCalls : scale ? ScaleMaximumCalls : ProbeMaximumCalls;
            Require(offset >= 0 && offset % 512 == 0 && length is > 0 && length <= maximumLength && length % 512 == 0 &&
                offset <= volumeBytes - length, "VssRawRead");
            Require(readCalls < maximumCalls && requestedBytes <= maximumBytes - length, personal ? "VssRawBudget" : "VssRawRead");
            ArraySegment<byte> segment;
            try { Require(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination, out segment) && segment.Array != null, "VssRawRead"); }
            catch { token.ThrowIfCancellationRequested(); throw new NtfsVssProbeException("VssRawRead"); }
            try
            {
                if (allocation == IntPtr.Zero)
                {
                    allocation = External(() => native.Allocate(131071), token); Require(allocation != IntPtr.Zero, "VssRawRead");
                    aligned = (IntPtr)(((nuint)allocation + 65535) & ~((nuint)65535)); bufferReleased = false;
                }
                token.ThrowIfCancellationRequested(); var seek = External(() => native.Seek(offset), token); Require(seek.Success, "VssRawRead", seek.Error);
                token.ThrowIfCancellationRequested(); readCalls++; requestedBytes += length;
                var read = External(() => native.Read(aligned, length), token); Require(read.Success && read.Used == length, "VssRawRead", read.Error);
                token.ThrowIfCancellationRequested(); Marshal.Copy(aligned, segment.Array!, segment.Offset, length);
                token.ThrowIfCancellationRequested(); return length;
            }
            catch (NtfsVssProbeException) { throw; }
            catch { token.ThrowIfCancellationRequested(); throw new NtfsVssProbeException("VssRawRead"); }
        }
    }
    internal byte[] Read(long offset, int length, CancellationToken token = default)
    { Require(length is > 0 && length <= (scale ? 65536 : ProbeMaximumBytes), "VssRawRead"); var bytes = new byte[length]; ReadInto(offset, bytes, token); return bytes; }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true; bool released = true;
            try { if (allocation != IntPtr.Zero) { native.Free(allocation); allocation = aligned = IntPtr.Zero; } }
            catch { released = false; }
            finally { try { native.Dispose(); } catch { released = false; } }
            bufferReleased = released;
            Require(released, "VssCleanup");
        }
    }
    private sealed class Native : INative
    {
        private readonly SafeFileHandle handle;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long position, uint origin);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, int count, out int returned, IntPtr overlapped);
        internal Native(string device)
        {
            handle = NativeStorage.CreateFileW(device, 0x80000000, NativeStorage.ShareAll, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new NtfsVssProbeException("VssRawOpen", error); }
        }
        public ControlResult Facts(byte[] output)
        {
            bool success = NativeStorage.DeviceIoControl(handle, 0x90064, null, 0, output, output.Length, out int used, IntPtr.Zero);
            return new(success, used, success ? 0 : Marshal.GetLastWin32Error());
        }
        public Result Seek(long offset)
        { bool success = SetFilePointerEx(handle, offset, out long position, 0); return new(success && position == offset, success ? 0 : Marshal.GetLastWin32Error()); }
        public ReadResult Read(IntPtr buffer, int length)
        { bool success = ReadFile(handle, buffer, length, out int used, IntPtr.Zero); return new(success, used, success ? 0 : Marshal.GetLastWin32Error()); }
        public IntPtr Allocate(int bytes) => Marshal.AllocHGlobal(bytes);
        public void Free(IntPtr buffer) => Marshal.FreeHGlobal(buffer);
        public void Dispose() => handle.Dispose();
    }
}
