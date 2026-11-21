namespace DiskAtlas;

internal static class FastScanOptIn
{
    internal const string Explanation = "NTFS 快扫是可选实验功能。\n\n" +
        "• 每次扫描另行申请管理员授权，创建短时只读快照，结束后清理。\n" +
        "• 读取的 NTFS 记录页可能含相邻对象记录和驻留小文件内容片段；只解析元信息，不保存或上传原始页、文件或清单。\n" +
        "• 结果对应快照时点；不跟随链接，跳过项会明确显示。未支持或超限且确认清理后，转普通扫描。\n" +
        "• 快照期间系统写入可能增加临时磁盘占用。\n\n" +
        "是否开启？开启本身不会开始扫描。";

    internal static bool Request(IWin32Window owner) => MessageBox.Show(owner, Explanation,
        "AsukaLens · 开启 NTFS 快扫", MessageBoxButtons.OKCancel, MessageBoxIcon.Information,
        MessageBoxDefaultButton.Button2) == DialogResult.OK;
}
