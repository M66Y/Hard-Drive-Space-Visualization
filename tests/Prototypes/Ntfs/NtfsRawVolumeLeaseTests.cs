using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

// Inert non-owning handles plus injected native operations only. No actual
// volume lock, disk open, file creation or raw read is performed by these tests.
internal static class NtfsRawVolumeLeaseTests
{
    private sealed class FakeNative : NtfsRawVolumeLease.INative
    {
        internal int Locks, Unlocks, Seeks, Reads, Allocations, Frees;
        internal int Alignment = 4096;
        internal bool Aligned = true, FreeWhileReading, AllocateZero;
        internal long Position;
        internal NtfsRawVolumeLease.NativeResult LockResult = new(true, 0), UnlockResult = new(true, 0), SeekResult = new(true, 0);
        internal NtfsRawVolumeLease.NativeReadResult? ReadResult;
        internal Action? OnLock, OnUnlock, OnAllocate, OnSeek, OnRead, OnFree;
        internal List<string> Events { get; } = [];
        private bool reading;
        public NtfsRawVolumeLease.NativeResult Lock(SafeFileHandle _) { Locks++; Events.Add("Lock"); OnLock?.Invoke(); return LockResult; }
        public NtfsRawVolumeLease.NativeResult Unlock(SafeFileHandle _) { Unlocks++; Events.Add("Unlock"); OnUnlock?.Invoke(); return UnlockResult; }
        public NtfsRawVolumeLease.NativeResult Seek(SafeFileHandle _, long offset)
        { Seeks++; Position = offset; Events.Add("Seek"); OnSeek?.Invoke(); return SeekResult; }
        public NtfsRawVolumeLease.NativeReadResult Read(SafeFileHandle _, IntPtr buffer, int length)
        {
            Reads++; reading = true; Events.Add("ReadStart");
            try
            {
                Aligned &= (nuint)buffer % (nuint)Alignment == 0;
                OnRead?.Invoke();
                var result = ReadResult ?? new(true, length, 0);
                if (result.Success && result.BytesRead is >= 0 && result.BytesRead <= length)
                {
                    byte[] bytes = Enumerable.Range(0, result.BytesRead).Select(index => (byte)((Position % 251 + index) % 251)).ToArray();
                    Marshal.Copy(bytes, 0, buffer, bytes.Length);
                }
                return result;
            }
            finally { reading = false; Events.Add("ReadEnd"); }
        }
        public IntPtr Allocate(int bytes)
        { Allocations++; Events.Add("Allocate"); OnAllocate?.Invoke(); return AllocateZero ? IntPtr.Zero : Marshal.AllocHGlobal(bytes); }
        public void Free(IntPtr allocation)
        {
            Frees++; FreeWhileReading |= reading; Events.Add("Free");
            Marshal.FreeHGlobal(allocation);
            OnFree?.Invoke();
        }
    }

    internal static int Run()
    {
        int passed = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("NTFS raw lease check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS raw lease " + name);
        }
        Exception? Catch(Action action)
        {
            try { action(); return null; }
            catch (Exception error) { return error; }
        }
        SafeFileHandle Handle() => new(new IntPtr(12345), ownsHandle: false);
        NtfsRawVolumeLease Acquire(FakeNative native, SafeFileHandle handle, CancellationToken token = default,
            long bytes = 256 * 1024 * 1024, int logical = 512, int physical = 4096) =>
            NtfsRawVolumeLease.AcquireForTests(handle, bytes, logical, physical, native, token);

        using (var handle = Handle())
        {
            var native = new FakeNative();
            var lease = Acquire(native, handle);
            Check(lease.UnlockCode == "Pending" && !lease.UnlockSucceeded && native.Locks == 1, "successful acquisition records its single pending lock");
            byte[] bytes = lease.Read(512, 1024);
            Check(bytes.SequenceEqual(Enumerable.Range(0, 1024).Select(index => (byte)((512 + index) % 251))), "read returns exact native bytes without an IOCTL header");
            Check(native.Aligned && native.Reads == 1 && native.Seeks == 1, "native read uses an explicitly aligned buffer and one seek");
            Check(lease.ReadCalls == 1 && lease.RequestedBytes == 1024 && native.Frees == 1, "completed work counts requests and frees its buffer");
            lease.Dispose(); lease.Dispose();
            Check(lease.UnlockSucceeded && lease.UnlockCode == "Unlocked" && native.Unlocks == 1, "repeated disposal unlocks exactly once");
            Check(!handle.IsClosed, "lease disposal preserves the caller-owned handle");
            Check(Catch(() => lease.Read(0, 512)) is ObjectDisposedException && native.Reads == 1, "disposed lease performs no additional I/O");
        }
        foreach (int alignment in new[] { 512, 4096, 65536 })
        {
            using var handle = Handle(); var native = new FakeNative { Alignment = alignment };
            using var lease = Acquire(native, handle, physical: alignment);
            _ = lease.Read(0, 512);
            Check(native.Aligned && native.Allocations == 1 && native.Frees == 1, "supported physical alignment endpoints are honored");
        }
        foreach (var geometry in new[] { (0L,512,4096), (513L,512,4096), (4096L,0,4096), (4096L,4096,4096),
            (4096L,512,0), (4096L,512,256), (4096L,512,513), (4096L,512,131072) })
        {
            using var handle = Handle(); var native = new FakeNative();
            Check(Catch(() => Acquire(native, handle, bytes: geometry.Item1, logical: geometry.Item2, physical: geometry.Item3)) is InvalidDataException && native.Locks == 0,
                "invalid geometry fails before any native lock");
        }
        using (var handle = new SafeFileHandle(IntPtr.Zero, false))
        {
            var native = new FakeNative();
            Check(Catch(() => Acquire(native, handle)) is InvalidDataException && native.Locks == 0, "invalid handle cannot reach native operations");
        }
        using (var handle = Handle())
        {
            handle.Dispose(); var native = new FakeNative();
            Check(Catch(() => Acquire(native, handle)) is InvalidDataException && native.Locks == 0, "closed handle cannot acquire a lock");
        }
        using (var handle = Handle())
        {
            var native = new FakeNative { LockResult = new(false, 32) };
            var error = Catch(() => Acquire(native, handle));
            Check(error is NtfsRawVolumeException { Code: "LockFailed", NativeErrorCode: 32 } && native.Reads == 0 && native.Unlocks == 0,
                "failed lock exposes a typed numeric error without pretending to own a lock");
            Check(!handle.IsClosed, "failed acquisition releases only its own handle reference");
        }
        using (var handle = Handle())
        {
            var native = new FakeNative(); var lease = Acquire(native, handle);
            handle.Dispose();
            _ = lease.Read(0, 512);
            lease.Dispose();
            Check(handle.IsClosed && native.Reads == 1 && native.Unlocks == 1,
                "borrowed handle reference survives caller disposal until lease cleanup releases it");
        }
        foreach (var bounds in new[] { (-512L,512), (1L,512), (0L,0), (0L,-512), (0L,513), (0L,65537), (65536L,512), (long.MaxValue-511,1024) })
        {
            using var handle = Handle(); var native = new FakeNative();
            using var lease = Acquire(native, handle, bytes: 65536);
            Check(Catch(() => lease.Read(bounds.Item1, bounds.Item2)) is InvalidDataException && native.Reads == 0 && native.Unlocks == 1,
                "invalid or overflowing ranges fail closed before allocating or reading");
        }
        using (var handle = Handle())
        {
            long length = long.MaxValue - 511; var native = new FakeNative();
            using var lease = Acquire(native, handle, bytes: length);
            _ = lease.Read(length - 512, 512);
            Check(native.Reads == 1 && native.Position == length - 512, "subtraction-based bounds accept a valid range near the signed limit");
        }

        void Fault(Action<FakeNative> configure, Func<Exception?, bool> expected, int reads, int frees, string name)
        {
            using var handle = Handle(); var native = new FakeNative(); configure(native);
            using var lease = Acquire(native, handle);
            var error = Catch(() => lease.Read(0, 512));
            Check(expected(error) && native.Reads == reads && native.Frees == frees && native.Unlocks == 1, name);
            Check(!native.FreeWhileReading && lease.ReadCalls == reads && lease.RequestedBytes == reads * 512L,
                "faulted work retains exact counters and never frees an active synchronous buffer");
            lease.Dispose();
            Check(native.Unlocks == 1, "faulted lease disposal does not unlock twice");
        }
        Fault(x => x.OnAllocate = () => throw new OutOfMemoryException("SyntheticAllocation"), x => x is OutOfMemoryException, 0, 0,
            "allocation failure releases the acquired lock without a phantom buffer");
        Fault(x => x.AllocateZero = true, x => x is NtfsRawVolumeException { Code: "AllocationFailed" }, 0, 0,
            "null allocation is rejected without attempting to free an unowned pointer");
        Fault(x => x.SeekResult = new(false, 87), x => x is NtfsRawVolumeException { Code: "SeekFailed", NativeErrorCode: 87 }, 0, 1,
            "seek failure frees the buffer and preserves its typed native error");
        Fault(x => x.ReadResult = new(false, 0, 5), x => x is NtfsRawVolumeException { Code: "ReadFailed", NativeErrorCode: 5 }, 1, 1,
            "failed read counts the actual request and frees its buffer");
        foreach (int returned in new[] { -1, 0, 511, 513 })
            Fault(x => x.ReadResult = new(true, returned, 0), x => x is NtfsRawVolumeException { Code: "ShortRead", NativeErrorCode: 0 }, 1, 1,
                "non-exact successful native counts are rejected without zero padding");
        var primary = new IOException("SyntheticPrimary");
        Fault(x => { x.OnRead = () => throw primary; x.OnFree = () => throw new IOException("SyntheticFree"); x.OnUnlock = () => throw new IOException("SyntheticUnlock"); },
            x => ReferenceEquals(x, primary), 1, 1, "free and unlock failures cannot replace the primary read exception");
        Fault(x => x.OnFree = () => throw new IOException("SyntheticFree"), x => x is NtfsRawVolumeException { Code: "BufferReleaseFailed" }, 1, 1,
            "a standalone buffer release failure cannot return a successful read");

        using (var handle = Handle())
        {
            var native = new FakeNative { UnlockResult = new(false, 5) };
            var lease = Acquire(native, handle); lease.Dispose(); lease.Dispose();
            Check(!lease.UnlockSucceeded && lease.UnlockCode == "UnlockFailed" && lease.UnlockNativeErrorCode == 5 && native.Unlocks == 1,
                "unlock failure is visible without throwing from Dispose");
            Check(!handle.IsClosed, "failed unlock still preserves caller handle ownership");
        }
        foreach (string stage in new[] { "BeforeAcquire", "Lock", "BeforeRead", "Allocate", "Seek", "Read" })
        {
            using var cts = new CancellationTokenSource(); using var handle = Handle(); var native = new FakeNative();
            if (stage == "BeforeAcquire") cts.Cancel();
            if (stage == "Lock") native.OnLock = cts.Cancel;
            if (stage is "BeforeAcquire" or "Lock")
            {
                var error = Catch(() => Acquire(native, handle, cts.Token));
                Check(error is OperationCanceledException && native.Reads == 0 && native.Unlocks == (stage == "Lock" ? 1 : 0),
                    "cancellation around acquisition never leaks a successful lock");
                continue;
            }
            using var lease = Acquire(native, handle);
            if (stage == "BeforeRead") cts.Cancel();
            if (stage == "Allocate") native.OnAllocate = cts.Cancel;
            if (stage == "Seek") native.OnSeek = cts.Cancel;
            if (stage == "Read") native.OnRead = cts.Cancel;
            var canceled = Catch(() => lease.Read(0, 512, cts.Token));
            Check(canceled is OperationCanceledException && native.Unlocks == 1 && native.Frees == (stage == "BeforeRead" ? 0 : 1),
                "cancellation at native boundaries releases all acquired resources");
            Check(!native.FreeWhileReading && native.Reads == (stage == "Read" ? 1 : 0),
                "cancellation waits for the synchronous operation before freeing its buffer");
        }
        using (var handle = Handle())
        {
            var native = new FakeNative(); using var lease = Acquire(native, handle);
            for (int i = 0; i < 128; i++) _ = lease.Read(0, 512);
            Check(Catch(() => lease.Read(0, 512)) is InvalidDataException && lease.ReadCalls == 128 && native.Unlocks == 1,
                "call budget rejects the next operation before additional native work");
        }
        using (var handle = Handle())
        {
            var native = new FakeNative(); using var lease = Acquire(native, handle);
            for (int i = 0; i < 32; i++) _ = lease.Read(0, 65536);
            Check(Catch(() => lease.Read(0, 512)) is InvalidDataException && lease.RequestedBytes == 2 * 1024 * 1024 && native.Reads == 32,
                "actual requested bytes enforce the aggregate budget independently from call count");
        }
        using (var handle = Handle())
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var disposing = new ManualResetEventSlim())
        {
            var native = new FakeNative { OnRead = () => { entered.Set(); if (!release.Wait(5000)) throw new IOException("SyntheticWait"); } };
            var lease = Acquire(native, handle);
            var reading = Task.Run(() => lease.Read(0, 512));
            try
            {
                Check(entered.Wait(2000), "concurrent disposal fixture reaches its synchronous read");
                var disposal = Task.Run(() => { disposing.Set(); lease.Dispose(); });
                Check(disposing.Wait(2000) && native.Frees == 0 && native.Unlocks == 0, "Dispose cannot free or unlock while a synchronous read is active");
                release.Set();
                Check(Task.WaitAll([reading, disposal], 5000), "read and concurrent Dispose finish within the test deadline");
                Check(native.Frees == 1 && native.Unlocks == 1 && !native.FreeWhileReading &&
                    native.Events.IndexOf("ReadEnd") < native.Events.IndexOf("Free") && native.Events.IndexOf("Free") < native.Events.IndexOf("Unlock"),
                    "synchronous completion precedes exactly one free and unlock");
            }
            finally { release.Set(); _ = reading.Wait(5000); lease.Dispose(); }
        }
        using (var handle = Handle())
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var secondStarted = new ManualResetEventSlim())
        {
            var native = new FakeNative();
            native.OnRead = () => { if (native.Reads == 1) { entered.Set(); if (!release.Wait(5000)) throw new IOException("SyntheticWait"); } };
            using var lease = Acquire(native, handle);
            var first = Task.Run(() => lease.Read(0, 512));
            Task<byte[]>? second = null;
            try
            {
                Check(entered.Wait(2000), "parallel read fixture reaches the first native call");
                second = Task.Run(() => { secondStarted.Set(); return lease.Read(512, 512); });
                Check(secondStarted.Wait(2000) && native.Seeks == 1 && native.Reads == 1,
                    "second caller cannot move the shared file pointer during the first native read");
                release.Set();
                Check(Task.WaitAll([first, second], 5000), "parallel callers finish within the test deadline");
                Check(first.Result.SequenceEqual(Enumerable.Range(0, 512).Select(index => (byte)(index % 251))) &&
                    second.Result.SequenceEqual(Enumerable.Range(0, 512).Select(index => (byte)((512 + index) % 251))) &&
                    native.Reads == 2 && native.Seeks == 2 && native.Frees == 2,
                    "each serialized seek and read preserves its own bytes and buffer ownership");
            }
            finally { release.Set(); _ = first.Wait(5000); if (second != null) _ = second.Wait(5000); }
        }
        using (var handle = Handle())
        {
            var native = new FakeNative(); var page = Enumerable.Repeat((byte)0xcc, 65568).ToArray();
            var lease = NtfsRawVolumeLease.AcquireBoundedMftForTests(handle, 16 * 1024 * 1024, 512, 4096, native);
            Check(lease.ReadInto(0, page.AsMemory(16, 1024)) == 1024 && lease.ReadInto(4096, page.AsMemory(16, 65536)) == 65536,
                "bounded profile fills caller-owned page slices without returning another page");
            Check(native.Allocations == 1 && native.Frees == 0 && native.Aligned && page.Take(16).All(value => value == 0xcc) &&
                page.Skip(65552).All(value => value == 0xcc), "bounded reads reuse one aligned native allocation and preserve target margins");
            Check(page.AsSpan(16, 65536).SequenceEqual(Enumerable.Range(0, 65536).Select(index => (byte)((4096 + index) % 251)).ToArray()),
                "ReadInto copies exactly the native bytes into the requested managed slice");
            lease.Dispose(); lease.Dispose();
            Check(native.Frees == 1 && native.Unlocks == 1 && lease.BufferReleaseSucceeded && lease.UnlockSucceeded,
                "bounded profile frees its reusable buffer exactly once before final unlock");
        }
        using (var handle = Handle())
        {
            var native = new FakeNative(); using var lease = Acquire(native, handle);
            Check(Catch(() => lease.ReadInto(0, new byte[512])) is InvalidDataException && native.Reads == 0,
                "legacy profile cannot accidentally use the expanded bulk path");
        }
        using (var handle = Handle())
        {
            var native = new FakeNative(); using var lease = NtfsRawVolumeLease.AcquireBoundedMftForTests(handle, 16 * 1024 * 1024, 512, 4096, native);
            Check(Catch(() => lease.Read(0, 512)) is InvalidDataException && native.Reads == 0,
                "bounded profile cannot fall back to allocating arrays through legacy Read");
        }
        using (var handle = Handle())
        {
            var native = new FakeNative(); var page = new byte[65536];
            using var lease = NtfsRawVolumeLease.AcquireBoundedMftForTests(handle, 16 * 1024 * 1024, 512, 4096, native);
            _ = lease.ReadInto(0, page.AsMemory(0, 1024));
            for (int i = 0; i < 64; i++) _ = lease.ReadInto(0, page);
            Check(Catch(() => lease.ReadInto(0, page.AsMemory(0, 512))) is InvalidDataException &&
                lease.RequestedBytes == 4 * 1024 * 1024 + 1024 && lease.ReadCalls == 65 && native.Allocations == 1 && native.Frees == 1,
                "bounded aggregate budget includes bootstrap and rejects any byte beyond four MiB plus one record");
        }
        using (var handle = Handle())
        {
            var native = new FakeNative(); var page = new byte[512];
            using var lease = NtfsRawVolumeLease.AcquireBoundedMftForTests(handle, 16 * 1024 * 1024, 512, 4096, native);
            for (int i = 0; i < 256; i++) _ = lease.ReadInto(0, page);
            Check(Catch(() => lease.ReadInto(0, page)) is InvalidDataException && lease.ReadCalls == 256 && native.Reads == 256 && native.Frees == 1,
                "bounded profile has a fixed call ceiling independent from its byte budget");
        }
        foreach (string fault in new[] { "Seek", "Read", "Short", "CancelAllocate", "CancelRead" })
        {
            using var handle = Handle(); using var cts = new CancellationTokenSource();
            var native = new FakeNative(); var page = Enumerable.Repeat((byte)0xcc, 1024).ToArray();
            using var lease = NtfsRawVolumeLease.AcquireBoundedMftForTests(handle, 16 * 1024 * 1024, 512, 4096, native);
            if (fault == "Seek") native.SeekResult = new(false, 87);
            if (fault == "Read") native.ReadResult = new(false, 0, 5);
            if (fault == "Short") native.ReadResult = new(true, 512, 0);
            if (fault == "CancelAllocate") native.OnAllocate = cts.Cancel;
            if (fault == "CancelRead") native.OnRead = cts.Cancel;
            var error = Catch(() => lease.ReadInto(0, page, cts.Token));
            Check((fault.StartsWith("Cancel", StringComparison.Ordinal) ? error is OperationCanceledException : error is NtfsRawVolumeException) &&
                native.Frees == 1 && native.Unlocks == 1 && !native.FreeWhileReading && page.All(value => value == 0xcc),
                "bounded failure or cancellation releases the reusable buffer without publishing a partial managed page");
        }
        using (var handle = Handle())
        {
            var native = new FakeNative { OnFree = () => throw new IOException("SyntheticFree") };
            var lease = NtfsRawVolumeLease.AcquireBoundedMftForTests(handle, 16 * 1024 * 1024, 512, 4096, native);
            _ = lease.ReadInto(0, new byte[512]); lease.Dispose(); lease.Dispose();
            Check(!lease.BufferReleaseSucceeded && lease.UnlockSucceeded && native.Frees == 1 && native.Unlocks == 1,
                "native buffer release failure is visible separately from successful unlock");
        }
        using (var handle = Handle())
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var disposing = new ManualResetEventSlim())
        {
            var native = new FakeNative { OnRead = () => { entered.Set(); if (!release.Wait(5000)) throw new IOException("SyntheticWait"); } };
            var lease = NtfsRawVolumeLease.AcquireBoundedMftForTests(handle, 16 * 1024 * 1024, 512, 4096, native);
            var page = new byte[1024];
            var reading = Task.Run(() => lease.ReadInto(0, page));
            Task? disposal = null;
            try
            {
                Check(entered.Wait(2000), "bounded concurrent disposal reaches its synchronous reusable-buffer read");
                disposal = Task.Run(() => { disposing.Set(); lease.Dispose(); });
                Check(disposing.Wait(2000) && native.Frees == 0 && native.Unlocks == 0,
                    "bounded disposal cannot release the reusable native buffer while a read owns it");
                release.Set();
                Check(Task.WaitAll([reading, disposal], 5000) && reading.Result == 1024 && native.Allocations == 1 && native.Frees == 1 &&
                    native.Unlocks == 1 && !native.FreeWhileReading && native.Events.IndexOf("ReadEnd") < native.Events.IndexOf("Free") &&
                    native.Events.IndexOf("Free") < native.Events.IndexOf("Unlock"),
                    "bounded read finishes before its sole buffer release and unlock");
            }
            finally { release.Set(); _ = reading.Wait(5000); if (disposal != null) _ = disposal.Wait(5000); lease.Dispose(); }
        }
        using (var handle = Handle())
        {
            var native = new FakeNative(); var page = new byte[65536];
            using var lease = NtfsRawVolumeLease.AcquireBitmapPrefixForTests(handle, 16 * 1024 * 1024, 512, 4096, native);
            _ = lease.ReadInto(0, page.AsMemory(0, 1024));
            for (int i = 0; i < 64; i++) _ = lease.ReadInto(0, page);
            Check(lease.ReadInto(4096, page.AsMemory(0, 512)) == 512 && lease.RequestedBytes == 4195840 && lease.ReadCalls == 66 &&
                native.Allocations == 1 && native.Frees == 0 && native.Aligned,
                "fixed bitmap profile admits exactly one aligned prefix beyond the original maximum MFT and bootstrap work");
            Check(Catch(() => lease.ReadInto(0, page.AsMemory(0, 512))) is InvalidDataException && lease.RequestedBytes == 4195840 &&
                native.Reads == 66 && native.Frees == 1 && native.Unlocks == 1,
                "bitmap byte ceiling rejects the next request and releases the reusable buffer without widening older profiles");
        }
        using (var handle = Handle())
        {
            var native = new FakeNative(); using var lease = NtfsRawVolumeLease.AcquireBitmapPrefixForTests(handle, 16 * 1024 * 1024, 512, 4096, native);
            var page = new byte[512];
            for (int i = 0; i < 256; i++) _ = lease.ReadInto(0, page);
            Check(Catch(() => lease.ReadInto(0, page)) is InvalidDataException && lease.ReadCalls == 256 && native.Reads == 256 &&
                native.Frees == 1 && native.Unlocks == 1,
                "bitmap profile preserves the independent two-hundred-fifty-six attempt ceiling");
        }
        using (var handle = Handle())
        {
            var native = new FakeNative(); using var lease = NtfsRawVolumeLease.AcquireBitmapPrefixForTests(handle, 16 * 1024 * 1024, 512, 4096, native);
            Check(Catch(() => lease.Read(0, 512)) is InvalidDataException && native.Reads == 0 && native.Allocations == 0 && native.Unlocks == 1,
                "bitmap profile cannot bypass its reuse and budget contract through legacy array-returning reads");
        }
        foreach (string fault in new[] { "Read", "Short", "Cancel" })
        {
            using var handle = Handle(); using var cts = new CancellationTokenSource(); var native = new FakeNative();
            var page = Enumerable.Repeat((byte)0xcc, 512).ToArray();
            using var lease = NtfsRawVolumeLease.AcquireBitmapPrefixForTests(handle, 16 * 1024 * 1024, 512, 4096, native);
            if (fault == "Read") native.ReadResult = new(false, 0, 5);
            if (fault == "Short") native.ReadResult = new(true, 256, 0);
            if (fault == "Cancel") native.OnRead = cts.Cancel;
            var error = Catch(() => lease.ReadInto(0, page, cts.Token));
            Check((fault == "Cancel" ? error is OperationCanceledException : error is NtfsRawVolumeException) &&
                native.Reads == 1 && lease.RequestedBytes == 512 && native.Frees == 1 && native.Unlocks == 1 && page.All(value => value == 0xcc),
                "bitmap prefix failure short read and cancellation preserve request accounting and no partial publication");
        }
        return passed;
    }

    internal static int RunScale()
    {
        int passed = 0;
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("Raw scale lease check failed: " + name); passed++; Console.WriteLine("PASS raw scale lease " + name); }
        Exception? Catch(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
        SafeFileHandle Handle() => new(new IntPtr(12345), false);
        using (var handle = Handle())
        {
            var native = new FakeNative(); var page = new byte[65536];
            using var lease = NtfsRawVolumeLease.AcquireScaleForTests(handle, 1024L * 1024 * 1024, 512, 4096, native);
            for (int i = 0; i < 4097; i++) _ = lease.ReadInto(0, page);
            Check(lease.RequestedBytes == 268500992 && lease.ReadCalls == 4097 && native.Allocations == 1 && native.Frees == 0,
                "scale byte ceiling includes exactly two-hundred-fifty-six MiB plus sixty-four KiB with one reusable buffer");
            Check(Catch(() => lease.ReadInto(0, page.AsMemory(0, 512))) is InvalidDataException && native.Reads == 4097 &&
                native.Frees == 1 && native.Unlocks == 1, "one extra sector fails closed before another scale native read");
        }
        using (var handle = Handle())
        {
            var native = new FakeNative(); var page = new byte[512];
            using var lease = NtfsRawVolumeLease.AcquireScaleForTests(handle, 1024L * 1024 * 1024, 512, 4096, native);
            for (int i = 0; i < 8192; i++) _ = lease.ReadInto(0, page);
            Check(Catch(() => lease.ReadInto(0, page)) is InvalidDataException && lease.ReadCalls == 8192 && native.Reads == 8192 &&
                native.Allocations == 1 && native.Frees == 1 && native.Unlocks == 1, "scale call ceiling is independent from total requested bytes");
        }
        using (var handle = Handle())
        {
            var native = new FakeNative(); using var lease = NtfsRawVolumeLease.AcquireScaleForTests(handle, 1024L * 1024 * 1024, 512, 4096, native);
            Check(Catch(() => lease.Read(0, 512)) is InvalidDataException && native.Reads == 0 && native.Unlocks == 1,
                "scale factory cannot bypass its profile through the legacy array read API");
        }
        foreach (string fault in new[] { "Read", "Short", "Cancel" })
        {
            using var handle = Handle(); using var cancellation = new CancellationTokenSource(); var native = new FakeNative();
            var page = Enumerable.Repeat((byte)0xcc, 512).ToArray();
            using var lease = NtfsRawVolumeLease.AcquireScaleForTests(handle, 1024L * 1024 * 1024, 512, 4096, native);
            if (fault == "Read") native.ReadResult = new(false, 0, 5);
            if (fault == "Short") native.ReadResult = new(true, 256, 0);
            if (fault == "Cancel") native.OnRead = cancellation.Cancel;
            var error = Catch(() => lease.ReadInto(0, page, cancellation.Token));
            Check((fault == "Cancel" ? error is OperationCanceledException : error is NtfsRawVolumeException) && native.Frees == 1 &&
                native.Unlocks == 1 && lease.ReadCalls == 1 && lease.RequestedBytes == 512 && page.All(value => value == 0xcc),
                "scale failure short read and cancellation preserve accounting and discard partial bytes");
        }
        return passed;
    }
}
