namespace DiskAtlas;

public enum RefreshMethod { FullScan, ChangeJournal, MetadataVerification }
public enum JournalState
{
    Available, Unavailable, UnsupportedPath, NotNtfs, VolumeUnknown, VolumeMismatch,
    AccessDenied, NotActive, UnsupportedOperation, QueryFailed, ReadFailed,
    Discontinuous, InvalidData, TooManyChanges
}

// Only stable categories/codes: no paths, file names, volume IDs or raw exception text.
public record JournalStatus(JournalState State, int NativeError = 0, int ReadOnlyOpenError = 0)
{
    public string Description => (State switch
    {
        JournalState.Available => "NTFS 变化日志可用",
        JournalState.UnsupportedPath => "此路径形式或挂载方式未启用卷日志读取",
        JournalState.NotNtfs => "文件系统不是本地 NTFS",
        JournalState.VolumeUnknown => "无法确认目录所属卷",
        JournalState.VolumeMismatch => "目录与日志卷不一致",
        JournalState.AccessDenied => "当前权限无法读取卷日志",
        JournalState.NotActive => "卷变化日志未启用或正在删除",
        JournalState.UnsupportedOperation => "卷不支持当前日志查询或读取",
        JournalState.QueryFailed => "无法查询卷变化日志",
        JournalState.ReadFailed => "无法读取连续变化记录",
        JournalState.Discontinuous => "变化日志已重建、截断或游标失效",
        JournalState.InvalidData => "变化日志数据或记录格式无效",
        JournalState.TooManyChanges => "变化记录超出本次处理上限",
        _ => "没有可用的变化日志"
    }) + (NativeError != 0 ? $"（错误码 {NativeError}）" : "") +
        (ReadOnlyOpenError != 0 ? $"；只读句柄尝试失败（错误码 {ReadOnlyOpenError}）" : "");

    internal static JournalStatus FromNative(int error, bool reading = false) => new(error switch
    {
        5 => JournalState.AccessDenied,
        1178 or 1179 => JournalState.NotActive,
        1181 => JournalState.Discontinuous,
        1 or 50 => JournalState.UnsupportedOperation,
        _ => reading ? JournalState.ReadFailed : JournalState.QueryFailed
    }, error);
}

internal sealed class JournalException(JournalStatus status) : IOException(status.Description)
{
    internal JournalStatus Status { get; } = status;
}

internal sealed record JournalOpenResult(IChangeJournal? Journal, JournalStatus Status, JournalCheckpoint? InitialCheckpoint = null);
internal readonly record struct JournalIoResult(bool Success, int Bytes, int Error = 0);
internal delegate JournalIoResult JournalControl(uint code, byte[]? input, byte[] output);

public static class RefreshPresentation
{
    public static string MethodName(RefreshMethod method) => method switch
    {
        RefreshMethod.ChangeJournal => "日志增量", RefreshMethod.MetadataVerification => "元信息核验", _ => "完整扫描"
    };

    public static string Summary(ScanStatistics stats) =>
        $"{MethodName(stats.Method)} · 核验目录 {stats.VerifiedDirectories:N0} / 文件 {stats.VerifiedFiles:N0} · 重扫目录 {stats.ReadDirectories:N0} · 复用文件 {stats.ReusedFiles:N0}";

    public static string Details(ScanStatistics stats) =>
        $"{Summary(stats)}\n原因：{stats.Reason}\n日志：{stats.Journal?.Description ?? "未提供诊断"}；处理记录 {stats.JournalRecords:N0} 条。\n" +
        $"准备与核验 {stats.PreparationMilliseconds:N1} ms；读取与整理 {stats.ScanMilliseconds:N1} ms。\n" +
        $"核验峰值 {stats.PeakVerificationWorkers}；目录峰值 {stats.PeakDirectoryWorkers}/{stats.Workers}。任务峰值含等待，不表示 CPU 核数或瞬时磁盘 I/O。\n" +
        (stats.FileReaderLimit > 0
            ? $"文件读取调用峰值 {stats.PeakFileReaders}/{stats.FileReaderLimit}；并行批次读取 {stats.ParallelFileReads:N0} 个文件；待发布文件缓冲峰值 {stats.PeakBufferedFiles}/{FileReadBudget.BufferLimit}。"
            : "文件元信息在各目录任务内逐项读取。此扫描未启用目录内并行批次。");
}
