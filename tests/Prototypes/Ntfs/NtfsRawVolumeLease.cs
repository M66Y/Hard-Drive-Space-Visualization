using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed class NtfsRawVolumeException : IOException
{
    internal string Code { get; }
    internal int NativeErrorCode { get; }
    internal NtfsRawVolumeException(string code, int nativeErrorCode = 0) : base(code)
    { Code = code; NativeErrorCode = nativeErrorCode; }
}

// Test-only. The caller proves VHD ownership and geometry before passing its
// already-open read-only volume handle. This type never opens a volume or writes.
internal sealed class NtfsRawVolumeLease : IDisposable
{
    internal readonly record struct NativeResult(bool Success, int Error);
    internal readonly record struct NativeReadResult(bool Success, int BytesRead, int Error);
    internal interface INative
    {
        NativeResult Lock(SafeFileHandle volume);
        NativeResult Unlock(SafeFileHandle volume);
        NativeResult Seek(SafeFileHandle volume, long offset);
        NativeReadResult Read(SafeFileHandle volume, IntPtr buffer, int length);
        IntPtr Allocate(int bytes);
        void Free(IntPtr allocation);
    }

    internal const int MaximumReadLength = 65536, MaximumReadCalls = 128;
    internal const long MaximumRequestedBytes = 2 * 1024 * 1024;
    internal const int BoundedMftMaximumReadCalls = 256;
    internal const long BoundedMftMaximumRequestedBytes = 4 * 1024 * 1024 + 1024;
    internal const int BitmapPrefixMaximumReadCalls = 256;
    internal const long BitmapPrefixMaximumRequestedBytes = 4 * 1024 * 1024 + 1024 + 512;
    internal const int ScaleMaximumReadCalls = 8192;
    internal const long ScaleMaximumRequestedBytes = 256L * 1024 * 1024 + 65536;
    private enum ReadProfile { RecordArray, BoundedMft, BitmapPrefix, Scale }
    private readonly object gate = new();
    private readonly SafeFileHandle volume;
    private readonly long volumeBytes;
    private readonly int logicalSector, physicalSector;
    private readonly INative native;
    private readonly ReadProfile profile;
    private bool referenceAdded, locked, disposed;
    private int readCalls;
    private long requestedBytes;
    private string unlockCode = "NotLocked";
    private int unlockNativeErrorCode;
    private IntPtr reusableAllocation, reusableBuffer;
    private bool bufferReleaseSucceeded = true;

    private NtfsRawVolumeLease(SafeFileHandle borrowedVolume, long bytes, int logical, int physical, INative adapter, ReadProfile profile)
    {
        volume = borrowedVolume; volumeBytes = bytes; logicalSector = logical; physicalSector = physical; native = adapter;
        this.profile = profile;
    }

    internal bool UnlockSucceeded { get { lock (gate) return unlockCode == "Unlocked"; } }
    internal string UnlockCode { get { lock (gate) return unlockCode; } }
    internal int UnlockNativeErrorCode { get { lock (gate) return unlockNativeErrorCode; } }
    internal int ReadCalls { get { lock (gate) return readCalls; } }
    internal long RequestedBytes { get { lock (gate) return requestedBytes; } }
    internal bool BufferReleaseSucceeded { get { lock (gate) return bufferReleaseSucceeded; } }

    internal static NtfsRawVolumeLease Acquire(SafeFileHandle borrowedVolume, long volumeBytes,
        int logicalSector, int physicalSector, CancellationToken token = default) =>
        AcquireForTests(borrowedVolume, volumeBytes, logicalSector, physicalSector, Native.Instance, token);

    // The same orchestration is exercised with synthetic operations and an inert
    // non-owning handle. Tests do not invoke Lock/Read on any real volume.
    internal static NtfsRawVolumeLease AcquireForTests(SafeFileHandle borrowedVolume, long volumeBytes,
        int logicalSector, int physicalSector, INative adapter, CancellationToken token = default) =>
        AcquireCore(borrowedVolume, volumeBytes, logicalSector, physicalSector, adapter, ReadProfile.RecordArray, token);

    internal static NtfsRawVolumeLease AcquireBoundedMft(SafeFileHandle borrowedVolume, long volumeBytes,
        int logicalSector, int physicalSector, CancellationToken token = default) =>
        AcquireCore(borrowedVolume, volumeBytes, logicalSector, physicalSector, Native.Instance, ReadProfile.BoundedMft, token);

    internal static NtfsRawVolumeLease AcquireBoundedMftForTests(SafeFileHandle borrowedVolume, long volumeBytes,
        int logicalSector, int physicalSector, INative adapter, CancellationToken token = default) =>
        AcquireCore(borrowedVolume, volumeBytes, logicalSector, physicalSector, adapter, ReadProfile.BoundedMft, token);

    internal static NtfsRawVolumeLease AcquireBitmapPrefix(SafeFileHandle borrowedVolume, long volumeBytes,
        int logicalSector, int physicalSector, CancellationToken token = default) =>
        AcquireCore(borrowedVolume, volumeBytes, logicalSector, physicalSector, Native.Instance, ReadProfile.BitmapPrefix, token);

    internal static NtfsRawVolumeLease AcquireBitmapPrefixForTests(SafeFileHandle borrowedVolume, long volumeBytes,
        int logicalSector, int physicalSector, INative adapter, CancellationToken token = default) =>
        AcquireCore(borrowedVolume, volumeBytes, logicalSector, physicalSector, adapter, ReadProfile.BitmapPrefix, token);

    internal static NtfsRawVolumeLease AcquireScale(SafeFileHandle borrowedVolume, long volumeBytes,
        int logicalSector, int physicalSector, CancellationToken token = default) =>
        AcquireCore(borrowedVolume, volumeBytes, logicalSector, physicalSector, Native.Instance, ReadProfile.Scale, token);

    internal static NtfsRawVolumeLease AcquireScaleForTests(SafeFileHandle borrowedVolume, long volumeBytes,
        int logicalSector, int physicalSector, INative adapter, CancellationToken token = default) =>
        AcquireCore(borrowedVolume, volumeBytes, logicalSector, physicalSector, adapter, ReadProfile.Scale, token);

    private static NtfsRawVolumeLease AcquireCore(SafeFileHandle borrowedVolume, long volumeBytes,
        int logicalSector, int physicalSector, INative adapter, ReadProfile profile, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(borrowedVolume);
        ArgumentNullException.ThrowIfNull(adapter);
        if (borrowedVolume.IsClosed || borrowedVolume.IsInvalid) throw new InvalidDataException("RawVolumeHandle");
        if (logicalSector != 512 || physicalSector is < 512 or > 65536 ||
            (physicalSector & (physicalSector - 1)) != 0 || physicalSector < logicalSector ||
            volumeBytes < logicalSector || volumeBytes % logicalSector != 0) throw new InvalidDataException("RawVolumeGeometry");
        token.ThrowIfCancellationRequested();
        var lease = new NtfsRawVolumeLease(borrowedVolume, volumeBytes, logicalSector, physicalSector, adapter, profile);
        try
        {
            borrowedVolume.DangerousAddRef(ref lease.referenceAdded);
            token.ThrowIfCancellationRequested();
            var result = adapter.Lock(borrowedVolume);
            // Record ownership before the post-call cancellation check.
            if (result.Success) { lease.locked = true; lease.unlockCode = "Pending"; }
            token.ThrowIfCancellationRequested();
            if (!result.Success) throw NativeFailure("LockFailed", result.Error);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal byte[] Read(long volumeOffset, int length, CancellationToken token = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                token.ThrowIfCancellationRequested();
                if (profile != ReadProfile.RecordArray) throw new InvalidDataException("RawReadProfile");
                if (!locked) throw new InvalidDataException("RawVolumeNotLocked");
                if (volumeOffset < 0 || volumeOffset % logicalSector != 0 || length is < 1 or > MaximumReadLength ||
                    length % logicalSector != 0 || volumeOffset > volumeBytes - length)
                    throw new InvalidDataException("RawReadBounds");
                if (readCalls >= MaximumReadCalls || length > MaximumRequestedBytes - requestedBytes)
                    throw new InvalidDataException("RawReadBudget");
                return ReadCore(volumeOffset, length, token);
            }
            catch
            {
                // ReadCore has completed any synchronous call and released its
                // buffer before a failed/canceled lease relinquishes the lock.
                Finish();
                throw;
            }
        }
    }

    internal int ReadInto(long volumeOffset, Memory<byte> destination, CancellationToken token = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                token.ThrowIfCancellationRequested();
                if (profile == ReadProfile.RecordArray) throw new InvalidDataException("RawReadProfile");
                int length = destination.Length;
                if (!locked || volumeOffset < 0 || volumeOffset % logicalSector != 0 || length is < 1 or > MaximumReadLength ||
                    length % logicalSector != 0 || volumeOffset > volumeBytes - length)
                    throw new InvalidDataException("RawReadBounds");
                int maximumCalls = profile == ReadProfile.Scale ? ScaleMaximumReadCalls :
                    profile == ReadProfile.BitmapPrefix ? BitmapPrefixMaximumReadCalls : BoundedMftMaximumReadCalls;
                long maximumBytes = profile == ReadProfile.Scale ? ScaleMaximumRequestedBytes :
                    profile == ReadProfile.BitmapPrefix ? BitmapPrefixMaximumRequestedBytes : BoundedMftMaximumRequestedBytes;
                if (readCalls >= maximumCalls || length > maximumBytes - requestedBytes)
                    throw new InvalidDataException("RawReadBudget");
                if (!MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination, out var target) || target.Array == null)
                    throw new InvalidDataException("RawReadTarget");
                if (reusableAllocation == IntPtr.Zero)
                {
                    token.ThrowIfCancellationRequested();
                    reusableAllocation = native.Allocate(checked(MaximumReadLength + physicalSector - 1));
                    if (reusableAllocation == IntPtr.Zero) throw new NtfsRawVolumeException("AllocationFailed");
                    token.ThrowIfCancellationRequested();
                    nuint address = checked((nuint)reusableAllocation + (nuint)(physicalSector - 1));
                    reusableBuffer = (IntPtr)(address & ~((nuint)physicalSector - 1));
                }
                token.ThrowIfCancellationRequested();
                var seek = native.Seek(volume, volumeOffset);
                token.ThrowIfCancellationRequested();
                if (!seek.Success) throw NativeFailure("SeekFailed", seek.Error);
                token.ThrowIfCancellationRequested();
                readCalls++; requestedBytes = checked(requestedBytes + length);
                var read = native.Read(volume, reusableBuffer, length);
                token.ThrowIfCancellationRequested();
                if (!read.Success) throw NativeFailure("ReadFailed", read.Error);
                if (read.BytesRead != length) throw new NtfsRawVolumeException("ShortRead");
                Marshal.Copy(reusableBuffer, target.Array, target.Offset, length);
                token.ThrowIfCancellationRequested();
                return length;
            }
            catch { Finish(); throw; }
        }
    }

    private byte[] ReadCore(long offset, int length, CancellationToken token)
    {
        IntPtr allocation = IntPtr.Zero;
        bool primaryFailure = false;
        try
        {
            token.ThrowIfCancellationRequested();
            allocation = native.Allocate(checked(length + physicalSector - 1));
            if (allocation == IntPtr.Zero) throw new NtfsRawVolumeException("AllocationFailed");
            token.ThrowIfCancellationRequested();
            nuint address = checked((nuint)allocation + (nuint)(physicalSector - 1));
            address &= ~((nuint)physicalSector - 1);
            var aligned = (IntPtr)address;
            token.ThrowIfCancellationRequested();
            var seek = native.Seek(volume, offset);
            token.ThrowIfCancellationRequested();
            if (!seek.Success) throw NativeFailure("SeekFailed", seek.Error);
            token.ThrowIfCancellationRequested();
            // Budget counts native Read attempts, including failed/short calls;
            // it is not a physical-I/O count.
            readCalls++;
            requestedBytes = checked(requestedBytes + length);
            var read = native.Read(volume, aligned, length);
            token.ThrowIfCancellationRequested();
            if (!read.Success) throw NativeFailure("ReadFailed", read.Error);
            if (read.BytesRead != length) throw new NtfsRawVolumeException("ShortRead");
            var bytes = new byte[length];
            Marshal.Copy(aligned, bytes, 0, length);
            token.ThrowIfCancellationRequested();
            return bytes;
        }
        catch { primaryFailure = true; throw; }
        finally
        {
            // Never free or reuse an address while a synchronous Read is active.
            // A failed release cannot replace an existing primary exception.
            if (allocation != IntPtr.Zero)
            {
                try { native.Free(allocation); }
                catch { if (!primaryFailure) throw new NtfsRawVolumeException("BufferReleaseFailed"); }
            }
        }
    }

    private static NtfsRawVolumeException NativeFailure(string code, int error) => new(code, error);

    private void Finish()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (reusableAllocation != IntPtr.Zero)
            {
                IntPtr allocation = reusableAllocation;
                reusableAllocation = reusableBuffer = IntPtr.Zero;
                try { native.Free(allocation); }
                catch { bufferReleaseSucceeded = false; }
            }
            if (locked)
            {
                try
                {
                    var result = native.Unlock(volume);
                    unlockNativeErrorCode = result.Success ? 0 : result.Error;
                    unlockCode = result.Success ? "Unlocked" : "UnlockFailed";
                }
                catch { unlockCode = "UnlockFailed"; }
                finally { locked = false; }
            }
            else unlockCode = "NotLocked";
        }
        finally
        {
            if (referenceAdded)
            {
                referenceAdded = false;
                try { volume.DangerousRelease(); }
                catch { unlockCode = "UnlockFailed"; }
            }
        }
    }

    public void Dispose() { lock (gate) Finish(); }

    private sealed class Native : INative
    {
        internal static readonly Native Instance = new();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long position, uint method);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, uint bytes, out uint read, IntPtr overlapped);

        public NativeResult Lock(SafeFileHandle handle) => Control(handle, 0x90018);
        public NativeResult Unlock(SafeFileHandle handle) => Control(handle, 0x9001c);
        private static NativeResult Control(SafeFileHandle handle, uint code)
        {
            bool success = NativeStorage.DeviceIoControl(handle, code, null, 0, [], 0, out _, IntPtr.Zero);
            return new(success, success ? 0 : Marshal.GetLastWin32Error());
        }
        public NativeResult Seek(SafeFileHandle handle, long offset)
        {
            bool success = SetFilePointerEx(handle, offset, out long position, 0);
            int error = success ? 0 : Marshal.GetLastWin32Error();
            return success && position != offset ? new(false, 87) : new(success, error);
        }
        public NativeReadResult Read(SafeFileHandle handle, IntPtr buffer, int length)
        {
            bool success = ReadFile(handle, buffer, (uint)length, out uint read, IntPtr.Zero);
            return new(success, checked((int)read), success ? 0 : Marshal.GetLastWin32Error());
        }
        public IntPtr Allocate(int bytes) => Marshal.AllocHGlobal(bytes);
        public void Free(IntPtr allocation) => Marshal.FreeHGlobal(allocation);
    }
}
