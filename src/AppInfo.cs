using System.Runtime.InteropServices;

namespace DiskAtlas;

// Read only fixed assembly/runtime metadata; never expose build paths or arbitrary
// informational-version text in the UI or copyable product identification.
internal static class AppInfo
{
    public static string VersionLabel { get; } = typeof(AppInfo).Assembly.GetName().Version is { } version
        ? $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}" : "版本未知";
    public static string BuildId { get; } = typeof(AppInfo).Module.ModuleVersionId.ToString("N")[..12];
    public static string WindowTitle => $"AsukaLens {VersionLabel} · 磁盘空间可视化";
    public static string Summary => $"AsukaLens {VersionLabel}\r\n构建标识：{BuildId}\r\n" +
        $"运行环境：{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture}\r\n";
}
