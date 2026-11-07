using System.Text;

namespace DiskAtlas;

internal sealed class DiagnosticService : IDisposable
{
    internal const int QueueLimit = 128;
    private readonly DiagnosticStore store;
    private readonly object gate = new(), drainGate = new();
    private readonly Queue<(Guid Generation, DiagnosticRecord Record)> queue = new();
    private readonly SemaphoreSlim changeGate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Guid session = Guid.NewGuid();
    private readonly Task worker;
    private DiagnosticPolicy policy = DiagnosticPolicy.Unavailable;
    private bool paused, stopping, failed;
    private long epoch, scanSequence, dropped, unreportedDropped;
    public Task Ready { get; }
    public bool Enabled { get { lock (gate) return policy.Enabled; } }
    public bool SettingsHealthy { get { lock (gate) return policy.Healthy; } }
    public bool StorageFailed { get { lock (gate) return failed; } }
    public long Dropped { get { lock (gate) return dropped; } }
    internal int Pending { get { lock (gate) return queue.Count; } }

    public DiagnosticService(string? root = null, DiagnosticStore? store = null)
    {
        this.store = store ?? new DiagnosticStore(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AsukaLens"));
        Ready = Task.Run(() =>
        {
            try { var initial = this.store.Initialize(); lock (gate) policy = initial; }
            catch (Exception ex) when (DiagnosticStore.IsStorageError(ex)) { lock (gate) failed = true; }
            Record(new() { Kind = DiagnosticKind.Started });
        });
        worker = Task.Run(async () =>
        {
            await Ready.ConfigureAwait(false);
            int idle = 0;
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    Drain();
                    if (++idle == 10) { RefreshPolicy(); idle = 0; }
                    await Task.Delay(200, lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public long NextScan() => Interlocked.Increment(ref scanSequence);
    public void Record(DiagnosticRecord record)
    {
        lock (gate)
        {
            if (stopping || paused || !policy.Enabled) return;
            if (queue.Count == QueueLimit) { dropped++; unreportedDropped++; return; }
            queue.Enqueue((policy.Generation, record with { Session = session, TimeUtc = DateTimeOffset.UtcNow }));
        }
    }
    public void Failure(DiagnosticKind kind, Exception error, long? scan = null) => Record(new()
    {
        Kind = kind, Scan = scan, Category = DiagnosticCodec.Classify(error), ErrorCode = error.HResult
    });

    private void ApplyPolicy(DiagnosticPolicy current, long expectedEpoch)
    {
        lock (gate)
        {
            if (expectedEpoch != epoch) return;
            if (policy.Generation != current.Generation) { queue.Clear(); unreportedDropped = 0; }
            policy = current;
        }
    }
    private void RefreshPolicy()
    {
        long version; lock (gate) { if (paused || stopping) return; version = epoch; }
        try { ApplyPolicy(store.Policy(), version); }
        catch (Exception ex) when (DiagnosticStore.IsStorageError(ex)) { lock (gate) failed = true; }
    }
    private void Drain()
    {
        lock (drainGate)
        {
            List<(Guid Generation, DiagnosticRecord Record)> batch; long version, missed;
            lock (gate)
            {
                if (paused || !policy.Enabled || queue.Count == 0 && unreportedDropped == 0) return;
                version = epoch; missed = unreportedDropped; unreportedDropped = 0;
                batch = new(queue); queue.Clear();
                if (missed > 0) batch.Add((policy.Generation, new()
                {
                    Kind = DiagnosticKind.QueueOverflow, Dropped = missed, TimeUtc = DateTimeOffset.UtcNow, Session = session
                }));
            }
            try { ApplyPolicy(store.Append(batch), version); }
            catch (Exception ex) when (DiagnosticStore.IsStorageError(ex))
            {
                // The write may have partially succeeded. Do not retry a batch and invent duplicate events.
                lock (gate) { failed = true; unreportedDropped += missed; }
            }
        }
    }

    public async Task FlushAsync() { await Ready.ConfigureAwait(false); await Task.Run(Drain).ConfigureAwait(false); }
    public async Task<DiagnosticSnapshot> SnapshotAsync()
    {
        await FlushAsync().ConfigureAwait(false);
        long version; lock (gate) version = epoch;
        var snapshot = await Task.Run(store.Read).ConfigureAwait(false);
        ApplyPolicy(snapshot.Policy, version); return snapshot;
    }

    private async Task<T> ChangeAsync<T>(Func<T> change, Func<T, DiagnosticPolicy> getPolicy)
    {
        await Ready.ConfigureAwait(false);
        await changeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (gate) { paused = true; epoch++; }
            var result = await Task.Run(change).ConfigureAwait(false);
            lock (gate) { policy = getPolicy(result); queue.Clear(); unreportedDropped = 0; }
            return result;
        }
        finally { lock (gate) paused = false; changeGate.Release(); }
    }
    public Task<DiagnosticPolicy> SetEnabledAsync(bool value) => ChangeAsync(() => store.SetEnabled(value), p => p);
    public Task<DiagnosticClearResult> ClearAsync() => ChangeAsync(store.Clear, p => p.Policy);
    public async Task<IReadOnlyList<ProblemNote>> NotesAsync() { await Ready.ConfigureAwait(false); return await Task.Run(store.Notes).ConfigureAwait(false); }
    public Task<ProblemNote> SaveNoteAsync(ProblemNote? previous, string title, string body) => Task.Run(() => store.SaveNote(previous, title, body));
    public Task DeleteNoteAsync(ProblemNote previous) => Task.Run(() => store.DeleteNote(previous));

    public async Task<ErrorReportResult> ReportErrorAsync(Exception error)
    {
        await Ready.ConfigureAwait(false);
        Failure(DiagnosticKind.UnhandledError, error);
        try { return await Task.Run(() => store.ReportError(error)).ConfigureAwait(false); }
        catch (Exception ex) when (DiagnosticStore.IsStorageError(ex))
        {
            lock (gate) failed = true;
            return new(DiagnosticCodec.Classify(error).ToString(), $"0x{error.HResult:X8}", false, null);
        }
    }

    public static Task ExportAsync(string path, string preview, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        DiagnosticStore.AtomicWrite(path, Encoding.UTF8.GetBytes(preview), token, export: true);
    }, token);

    public void Dispose()
    {
        lock (gate)
        {
            if (stopping) return;
            if (policy.Enabled && !paused && queue.Count < QueueLimit)
                queue.Enqueue((policy.Generation, new() { Kind = DiagnosticKind.Stopped, TimeUtc = DateTimeOffset.UtcNow, Session = session }));
            stopping = true;
        }
        lifetime.Cancel();
        var finish = Task.Run(async () => { await Ready.ConfigureAwait(false); await worker.ConfigureAwait(false); Drain(); });
        // Background tasks cannot hold the application open; abnormal termination may lose the last events.
        try { finish.Wait(1500); } catch (AggregateException) { }
        _ = finish.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
    }
}
