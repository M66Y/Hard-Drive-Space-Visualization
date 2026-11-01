namespace DiskAtlas;

public enum ScanMode { Automatic, LowImpact, HighSpeed }
public enum DiskKind { Unknown, SolidState, Mechanical, Network }
public enum SizeMetric { Logical, Allocated }
public record ScanOptions(ScanMode Mode = ScanMode.Automatic);
public record ScanProfile(int Workers, int BatchDelayMilliseconds, string Description);
public record ScanStatistics(ScanMode Mode, int Workers, bool Incremental, long ReadDirectories, long ReusedFiles, string Reason, long VerifiedDirectories = 0, long VerifiedFiles = 0);

public static class ScanPolicy
{
    public static ScanProfile Resolve(ScanMode mode, DiskKind kind) => mode switch
    {
        ScanMode.LowImpact => new(1, 3, "低占用 · 单路扫描并适度让出时间"),
        ScanMode.HighSpeed => new(8, 0, "高速 · 最多 8 路扫描"),
        _ => kind switch
        {
            DiskKind.SolidState => new(8, 0, "自动 · 固态硬盘，最多 8 路"),
            DiskKind.Mechanical => new(1, 0, "自动 · 机械硬盘，单路读取"),
            DiskKind.Network => new(2, 0, "自动 · 网络目录，最多 2 路"),
            _ => new(4, 0, "自动 · 类型未知，最多 4 路")
        }
    };
}
