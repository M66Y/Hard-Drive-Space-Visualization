namespace DiskAtlas;

internal readonly record struct FileReadResult(FileStorageInfo? Storage, string? Error);

// Directory owners and temporary file helpers share eight execution slots.
// Owners never wait for helpers: they borrow only idle slots, with no directory backlog.
internal sealed class FileReadBudget : IDisposable
{
    internal const int LargeDirectoryThreshold = 512;
    internal const int BatchSize = 256;
    internal const int WorkerLimit = 8;
    internal const int BufferLimit = WorkerLimit * BatchSize;
    private readonly SemaphoreSlim permits = new(WorkerLimit, WorkerLimit);
    private readonly ScanConcurrencyMeter reading = new();
    private readonly Func<string, FileAttributes, FileReadResult> read;
    private int buffered, peakBuffered, directories, helpers, peakHelpers;
    private long parallelFiles;
    public int PeakReaders => reading.Peak;
    public int PeakBufferedFiles => Volatile.Read(ref peakBuffered);
    internal int BufferedFiles => Volatile.Read(ref buffered);
    public long ParallelFiles => Interlocked.Read(ref parallelFiles);
    public int PeakHelpers => Volatile.Read(ref peakHelpers);
    internal int ActiveDirectories => Volatile.Read(ref directories);
    internal int AvailableSlots => permits.CurrentCount;

    internal FileReadBudget(Func<string, FileAttributes, FileReadResult>? read = null) => this.read = read ?? ReadNative;

    private static FileReadResult ReadNative(string path, FileAttributes attributes)
    {
        NativeStorage.TryRead(path, false, attributes, out var storage, out var error);
        return new(storage, error);
    }

    public async ValueTask<Lease> EnterDirectoryAsync(CancellationToken token)
    {
        await permits.WaitAsync(token).ConfigureAwait(false);
        Interlocked.Increment(ref directories);
        return new(this, 1, true);
    }

    public bool CanSupplement(long outstandingDirectories) => outstandingDirectories <= ActiveDirectories && permits.CurrentCount > 0;

    public Lease TryRentHelpers(long outstandingDirectories, int requested = WorkerLimit - 1)
    {
        int count = 0;
        // This is a conservative instantaneous hint, not a correctness dependency.
        // Newly discovered directories may wait for the current bounded batch; the
        // shared slots still cap every native call and helpers never wait for slots.
        if (outstandingDirectories <= ActiveDirectories)
            while (count < Math.Clamp(requested, 0, WorkerLimit - 1) && permits.Wait(0)) count++;
        if (count != 0)
        {
            int current = Interlocked.Add(ref helpers, count), observed;
            while (current > (observed = Volatile.Read(ref peakHelpers)))
                if (Interlocked.CompareExchange(ref peakHelpers, current, observed) == observed) break;
        }
        return new(this, count, false);
    }

    // Call only while owning a directory/helper slot; no per-file gate or queue.
    public FileReadResult Read(string path, FileAttributes attributes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); reading.Enter();
        try { return read(path, attributes); }
        finally { reading.Exit(); }
    }

    internal readonly struct Lease(FileReadBudget owner, int count, bool directory) : IDisposable
    {
        public int Count => count;
        // Single-owner lexical using; never copy/dispose the lease twice.
        public void Dispose()
        {
            if (count == 0) return;
            if (directory) Interlocked.Decrement(ref owner.directories);
            else Interlocked.Add(ref owner.helpers, -count);
            owner.permits.Release(count);
        }
    }

    public void BufferAdded()
    {
        int count = Interlocked.Increment(ref buffered), observed;
        while (count > (observed = Volatile.Read(ref peakBuffered)))
            if (Interlocked.CompareExchange(ref peakBuffered, count, observed) == observed) break;
    }
    public void BufferRemoved(int count) => Interlocked.Add(ref buffered, -count);
    public void RecordParallelRead() => Interlocked.Increment(ref parallelFiles);
    // The owning scanner awaits all directory/batch workers before disposing this gate.
    public void Dispose() => permits.Dispose();
}
