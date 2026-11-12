using DiskAtlas;
using System.Collections.Concurrent;
using System.Diagnostics;

internal static class DiskActivityMonitorTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Disk activity monitor check failed: " + name);
            passed++; Console.WriteLine("PASS disk monitor " + name);
        }
        void Until(Func<bool> ready)
        {
            var watch = Stopwatch.StartNew();
            while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(4)) Thread.Sleep(5);
            if (!ready()) throw new InvalidOperationException("Monitor test timeout");
        }
        var updates = new ConcurrentQueue<DiskActivityUpdate>();
        int created = 0, released = 0, calls = 0, resolverCalls = 0;
        using (var monitor = new DiskActivityMonitor(updates.Enqueue,
            (_, _) => { Interlocked.Increment(ref resolverCalls); return new(0, "磁盘 0"); },
            _ => [new(0, "磁盘 0"), new(1, "磁盘 1")],
            disk => { Interlocked.Increment(ref created); return new FakeSource(disk, () => Interlocked.Increment(ref calls), () => Interlocked.Increment(ref released)); },
            TimeSpan.FromMilliseconds(20)))
        {
            long revision = monitor.Update("synthetic", null, true);
            Until(() => updates.Any(update => update.Sample?.Status == DiskActivityStatus.Available));
            Check(created == 1 && resolverCalls == 1, "automatic selection creates one background source");
            Check(updates.Where(update => update.Sample != null).First().Sample!.Status == DiskActivityStatus.Waiting,
                "initial sample remains waiting rather than fake zero");
            Check(monitor.Update("synthetic", null, true) == revision && created == 1,
                "identical requests do not restart or create queues");
            long paused = monitor.Update("synthetic", null, false);
            Until(() => released == 1 && updates.Any(update => update.Revision == paused));
            Check(!monitor.IsCurrent(revision) && monitor.IsCurrent(paused), "paused generation rejects queued stale results");
            int atPause = calls;
            Thread.Sleep(70);
            Check(calls == atPause && created == 1, "paused monitor releases source and does not sample");
            long resumed = monitor.Update("synthetic", 1, true);
            Until(() => updates.Any(update => update.Revision == resumed && update.Sample?.DiskNumber == 1));
            Check(created == 2 && resolverCalls == 1, "manual selection uses only verified discovery targets");
            Check(updates.First(update => update.Revision == resumed && update.Sample != null).Sample!.Status == DiskActivityStatus.Waiting,
                "resume uses a fresh interval baseline");
            monitor.InvalidateTarget();
            Until(() => created == 3 && updates.Any(update => update.Revision > resumed && update.Sample != null));
            Check(!monitor.IsCurrent(resumed) && released == 2, "device invalidation releases and reconnects the source");
            Check(updates.First(update => update.Revision > resumed && update.Sample != null).Sample!.Status == DiskActivityStatus.Waiting,
                "device reconnection requires a new interval baseline");
            monitor.Dispose();
            Check(!monitor.IsCurrent(resumed) && monitor.Update(null, null, true) == -1, "disposed monitor rejects requests and stale publication");
            Check(monitor.Completion.Wait(4000) && released == created, "close eventually releases every owned source");
        }

        using var entered = new ManualResetEventSlim();
        using var unblock = new ManualResetEventSlim();
        updates.Clear(); created = released = 0;
        var disks = new ConcurrentQueue<int>();
        using (var monitor = new DiskActivityMonitor(updates.Enqueue, (_, _) => new(0, "磁盘 0"),
            _ => [new(0, "磁盘 0"), new(1, "磁盘 1"), new(2, "磁盘 2")], disk =>
            {
                Interlocked.Increment(ref created); disks.Enqueue(disk);
                return new FakeSource(disk, () => { if (disk == 0) { entered.Set(); if (!unblock.Wait(4000)) throw new TimeoutException(); } },
                    () => Interlocked.Increment(ref released));
            }, TimeSpan.FromMilliseconds(20)))
        {
            long old = monitor.Update("synthetic", 0, true);
            Check(entered.Wait(4000), "blocking provider entered on background worker");
            monitor.Update("synthetic", 1, true);
            long latest = monitor.Update("synthetic", 2, true);
            Check(created == 1, "rapid changes do not overlap a blocked native query");
            unblock.Set();
            Until(() => updates.Any(update => update.Revision == latest && update.Sample != null));
            Check(disks.SequenceEqual([0, 2]), "intermediate pending selection is replaced by the latest request");
            Check(!updates.Any(update => update.Revision == old && update.Sample != null), "late blocked-provider result is discarded");
            monitor.Dispose();
            Check(monitor.Completion.Wait(4000) && released == created, "switch and close dispose all providers serially");
        }
        updates.Clear();
        using (var monitor = new DiskActivityMonitor(updates.Enqueue, (_, _) => null, _ => [],
            _ => throw new InvalidOperationException("must not create source")))
        {
            long revision = monitor.Update("synthetic", null, true);
            Until(() => updates.Any(update => update.Revision == revision && update.Message.Contains("无法可靠映射")));
            Check(!updates.Any(update => update.Sample != null), "unsupported mapping has no invented metrics");
            monitor.Dispose(); Check(monitor.Completion.Wait(4000), "unavailable state remains cancellable");
        }
        var closing = new List<Task>();
        for (int i = 0; i < 50; i++)
        {
            var monitor = new DiskActivityMonitor(_ => { }, (_, _) => new(0, "磁盘 0"), _ => [new(0, "磁盘 0")],
                disk => new FakeSource(disk, () => { }, () => { }), TimeSpan.FromMilliseconds(5));
            monitor.Update("synthetic", null, true);
            monitor.Dispose();
            closing.Add(monitor.Completion);
        }
        Check(Task.WaitAll(closing.ToArray(), 5000) && closing.All(task => task.IsCompletedSuccessfully),
            "rapid immediate close completes without cancellation-source disposal races");
        return passed;
    }

    private sealed class FakeSource(int disk, Action collect, Action dispose) : IDiskActivitySource
    {
        private int calls;
        public DiskActivitySample Collect(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); collect();
            return new(DateTimeOffset.UtcNow, disk, calls == 0 ? null : 30, 1024, 2048, .2,
                calls++ == 0 ? DiskActivityStatus.Waiting : DiskActivityStatus.Available);
        }
        public void Dispose() => dispose();
    }
}
