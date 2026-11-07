namespace DiskAtlas;

internal readonly record struct FileReadResult(FileStorageInfo? Storage, string? Error);

// One budget per full scan. A directory buffers at most one batch and awaits its
// completion before enumerating more files; eight directory workers retain <= 2048 items.
internal sealed class FileReadBudget : IDisposable
{
    internal const int LargeDirectoryThreshold = 512;
    internal const int BatchSize = 256;
    internal const int WorkerLimit = 8;
    internal const int BufferLimit = WorkerLimit * BatchSize;
    private readonly SemaphoreSlim permits = new(WorkerLimit, WorkerLimit);
    private readonly ScanConcurrencyMeter reading = new();
    private readonly Func<string, FileAttributes, FileReadResult> read;
    private int buffered, peakBuffered;
    private long parallelFiles;
    public int PeakReaders => reading.Peak;
    public int PeakBufferedFiles => Volatile.Read(ref peakBuffered);
    internal int BufferedFiles => Volatile.Read(ref buffered);
    public long ParallelFiles => Interlocked.Read(ref parallelFiles);

    internal FileReadBudget(Func<string, FileAttributes, FileReadResult>? read = null) => this.read = read ?? ReadNative;

    private static FileReadResult ReadNative(string path, FileAttributes attributes)
    {
        NativeStorage.TryRead(path, false, attributes, out var storage, out var error);
        return new(storage, error);
    }

    public async ValueTask<FileReadResult> ReadAsync(string path, FileAttributes attributes, CancellationToken token)
    {
        await permits.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            reading.Enter();
            try { return read(path, attributes); }
            finally { reading.Exit(); }
        }
        finally { permits.Release(); }
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
