namespace DiskAtlas;

public enum ScanMode { Automatic, LowImpact, HighSpeed }
public enum DiskKind { Unknown, SolidState, Mechanical, Network }
public enum SizeMetric { Logical, Allocated }
public enum ScanPhase { Scanning, Verifying, Reusing, Finalizing, Preparing }
public record ScanOptions(ScanMode Mode = ScanMode.Automatic);
public record ScanProfile(int Workers, int BatchDelayMilliseconds, string Description);
public record ScanStatistics(ScanMode Mode, int Workers, bool Incremental, long ReadDirectories, long ReusedFiles, string Reason,
    long VerifiedDirectories = 0, long VerifiedFiles = 0, int PeakDirectoryWorkers = 0, int PeakVerificationWorkers = 0,
    RefreshMethod Method = RefreshMethod.FullScan, JournalStatus? Journal = null, long JournalRecords = 0,
    double PreparationMilliseconds = 0, double ScanMilliseconds = 0)
{
    public RefreshReason? ReasonCode { get; init; }
    public int FileReaderLimit { get; init; }
    public int PeakFileReaders { get; init; }
    public int PeakBufferedFiles { get; init; }
    public long ParallelFileReads { get; init; }
    public int PeakSupplementalReaders { get; init; }
}

public static class ScanPolicy
{
    public static ScanProfile Resolve(ScanMode mode, DiskKind kind) => mode switch
    {
        ScanMode.LowImpact => new(1, 3, "低占用 · 单路，按累计工作项让步"),
        ScanMode.HighSpeed => new(8, 0, "高速 · 最多 8 路扫描"),
        _ => kind switch
        {
            DiskKind.SolidState => new(8, 0, "自动 · 固态硬盘，最多 8 路"),
            DiskKind.Mechanical => new(1, 0, "自动 · 机械硬盘，单路读取"),
            DiskKind.Network => new(2, 0, "自动 · 网络目录，最多 2 路"),
            _ => new(4, 0, "自动 · 类型未知，最多 4 路")
        }
    };

    public static string Explain(ScanMode mode)
    {
        string extra = mode == ScanMode.LowImpact ? "低占用单路执行；扫描与核验均跨目录累计，每 256 个目录任务或目录项让步一次；不足一批不额外等待。" :
            mode == ScanMode.Automatic ? "自动按设备选择目录并发：固态 8、机械 1、网络 2、未知 4；固态盘上目录任务与高速上限相同，目录内逐项读取。" : "高速完整扫描优先处理目录；目录内超过 512 个文件且没有排队目录时，才借用空余额度分批读取。目录和补充读取共享 8 路执行额度。";
        return extra + "并发上限不是实际并发数。自动和低占用仍在各目录内逐项读取；增量刷新及元信息核验沿用原有策略，工作量与完整扫描不同。";
    }
}

// One counter per phase, shared across directory boundaries. Do not tie pacing
// to flushing totals: empty folders and cached subtree publication are not batches.
internal sealed class ScanWorkPacer(ScanProfile profile, Action<int, CancellationToken>? wait = null)
{
    private int completed;
    internal const int BatchSize = 256;
    public void Worked(CancellationToken token)
    {
        if (profile.BatchDelayMilliseconds <= 0 || (Interlocked.Increment(ref completed) & (BatchSize - 1)) != 0) return;
        token.ThrowIfCancellationRequested();
        if (wait != null) wait(profile.BatchDelayMilliseconds, token);
        else if (token.WaitHandle.WaitOne(profile.BatchDelayMilliseconds)) token.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
    }
}

internal sealed class ScanConcurrencyMeter
{
    private int active, peak;
    public int Peak => Volatile.Read(ref peak);
    public void Enter()
    {
        int count = Interlocked.Increment(ref active), observed;
        while (count > (observed = Volatile.Read(ref peak)))
            if (Interlocked.CompareExchange(ref peak, count, observed) == observed) break;
    }
    public void Exit() => Interlocked.Decrement(ref active);
}
