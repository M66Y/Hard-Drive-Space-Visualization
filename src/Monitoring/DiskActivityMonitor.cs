namespace DiskAtlas;

internal sealed record DiskActivityUpdate(long Revision, DiskActivitySample? Sample, string Message,
    IReadOnlyList<DiskActivityTarget>? Targets = null);

// One background owner and a capacity-one wake-up signal. Rapid changes replace
// the desired state; no native query or task is queued for each UI interaction.
internal sealed class DiskActivityMonitor : IDisposable
{
    private sealed record Request(string? Path, int? Disk, bool Enabled, long Revision);
    private readonly object gate = new();
    private readonly SemaphoreSlim changed = new(0, 1);
    private readonly CancellationTokenSource stopped = new();
    private readonly Action<DiskActivityUpdate> publish;
    private readonly Func<string, CancellationToken, DiskActivityTarget?> resolve;
    private readonly Func<CancellationToken, IReadOnlyList<DiskActivityTarget>> discover;
    private readonly Func<int, IDiskActivitySource> create;
    private readonly TimeSpan interval;
    private readonly Task worker;
    private Request desired = new(null, null, false, 0);
    private long revision;
    private int disposed;

    internal DiskActivityMonitor(Action<DiskActivityUpdate> publish,
        Func<string, CancellationToken, DiskActivityTarget?>? resolve = null,
        Func<CancellationToken, IReadOnlyList<DiskActivityTarget>>? discover = null,
        Func<int, IDiskActivitySource>? create = null, TimeSpan? interval = null)
    {
        this.publish = publish;
        this.resolve = resolve ?? DiskActivityResolver.Resolve;
        this.discover = discover ?? DiskActivityResolver.Discover;
        this.create = create ?? (disk => new PdhDiskActivitySource(disk));
        this.interval = interval ?? TimeSpan.FromSeconds(1);
        if (this.interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        worker = Task.Run(Work);
    }

    internal Task Completion => worker;
    internal bool IsCurrent(long value) => Volatile.Read(ref disposed) == 0 && value == Volatile.Read(ref revision);

    internal long Update(string? path, int? disk, bool enabled)
    {
        lock (gate)
        {
            if (disposed != 0) return -1;
            if (desired.Path == path && desired.Disk == disk && desired.Enabled == enabled) return desired.Revision;
            desired = new(path, disk, enabled, Interlocked.Increment(ref revision));
            try { changed.Release(); } catch (SemaphoreFullException) { }
            return desired.Revision;
        }
    }

    private Request Current() { lock (gate) return desired; }
    internal void InvalidateTarget()
    {
        lock (gate)
        {
            if (disposed != 0) return;
            desired = desired with { Revision = Interlocked.Increment(ref revision) };
            try { changed.Release(); } catch (SemaphoreFullException) { }
        }
    }
    private void Publish(Request request, DiskActivitySample? sample, string message,
        IReadOnlyList<DiskActivityTarget>? targets = null)
    {
        if (IsCurrent(request.Revision)) publish(new(request.Revision, sample, message, targets));
    }

    private async Task Work()
    {
        IDiskActivitySource? source = null;
        long activeRevision = -1;
        var token = stopped.Token;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var request = Current();
                while (changed.Wait(0)) { }
                if (activeRevision != request.Revision)
                {
                    source?.Dispose(); source = null;
                    activeRevision = request.Revision;
                    if (request.Enabled)
                    {
                        Publish(request, null, "正在识别磁盘…");
                        try
                        {
                            var targets = discover(token);
                            if (!IsCurrent(request.Revision)) continue;
                            Publish(request, null, "", targets);
                            var target = request.Disk.HasValue
                                ? targets.FirstOrDefault(item => item.DiskNumber == request.Disk.Value)
                                : string.IsNullOrWhiteSpace(request.Path) ? null : resolve(request.Path, token);
                            if (!IsCurrent(request.Revision)) continue;
                            if (target == null)
                                Publish(request, null, request.Disk.HasValue || !string.IsNullOrWhiteSpace(request.Path)
                                    ? "无法可靠映射物理磁盘 · 可选择其他磁盘" : "选择扫描目录或手动选择磁盘");
                            else
                            {
                                source = create(target.DiskNumber);
                                Publish(request, null, "等待首个有效采样间隔…");
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch { Publish(request, null, "磁盘监测不可用"); }
                    }
                    else Publish(request, null, "已暂停 · 展开面板后采样");
                }
                if (source != null && request.Enabled && IsCurrent(request.Revision))
                {
                    try
                    {
                        var sample = source.Collect(token);
                        Publish(request, sample, sample.Status switch
                        {
                            DiskActivityStatus.Waiting => "等待首个有效采样间隔…",
                            DiskActivityStatus.Unavailable => "磁盘计数器不可用",
                            _ => ""
                        });
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch
                    {
                        source.Dispose(); source = null;
                        Publish(request, null, "磁盘监测不可用 · 收起再展开可重试");
                    }
                }
                if (!IsCurrent(request.Revision)) continue;
                if (request.Enabled && source != null) await changed.WaitAsync(interval, token);
                else await changed.WaitAsync(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            source?.Dispose();
            // Serialize final CTS disposal with the UI's Cancel call.
            lock (gate) { changed.Dispose(); stopped.Dispose(); }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Interlocked.Increment(ref revision);
            stopped.Cancel();
        }
        // Native calls may finish later. The worker owns their final disposal;
        // the UI never waits for a synchronous provider call during close.
    }
}
