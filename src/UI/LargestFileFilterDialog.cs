namespace DiskAtlas;

internal sealed class LargestFileFilterDialog : Form
{
    private sealed record Limit(string Name, long Bytes) { public override string ToString() => Name; }
    private readonly ComboBox minimum = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox extension = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill, MaxLength = 255 };
    private readonly Label error = new() { AutoSize = true, ForeColor = Theme.Accent, Dock = DockStyle.Fill };
    internal long MinimumLogicalSize => ((Limit)minimum.SelectedItem!).Bytes;
    internal string? Extension => extension.Text.Trim() is "" or "全部扩展名" ? null : extension.Text.Trim();
    internal LargestFileFilterDialog(long size, string? type, IReadOnlyList<string> extensions)
    {
        Text = "大文件筛选"; StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        Font = Theme.Font(Theme.BodyFontSize); BackColor = Theme.Surface; ForeColor = Theme.Text;
        ClientSize = new(340, 245); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new(SizeType.Absolute, 25)); layout.RowStyles.Add(new(SizeType.Absolute, 35));
        layout.RowStyles.Add(new(SizeType.Absolute, 28)); layout.RowStyles.Add(new(SizeType.Absolute, 35));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 36));
        minimum.AccessibleName = "文件大小至少"; extension.AccessibleName = "扩展名（可输入）";
        minimum.Items.AddRange([new Limit("不限", 0), new Limit("100 MiB", 100L * 1024 * 1024),
            new Limit("1 GiB", 1L << 30), new Limit("5 GiB", 5L << 30)]);
        minimum.SelectedIndex = Math.Max(0, minimum.Items.Cast<Limit>().ToList().FindIndex(x => x.Bytes == size));
        extension.Items.Add("全部扩展名"); extension.Items.AddRange(extensions.Take(4096).Cast<object>().ToArray());
        extension.Text = type ?? "全部扩展名";
        layout.Controls.Add(new Label { Text = "文件大小至少", AutoSize = true }, 0, 0); layout.Controls.Add(minimum, 0, 1);
        layout.Controls.Add(new Label { Text = "扩展名（可输入）", AutoSize = true }, 0, 2); layout.Controls.Add(extension, 0, 3);
        layout.Controls.Add(error, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var apply = new Button { Text = "应用", AutoSize = true };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var clear = new Button { Text = "恢复不限", AutoSize = true };
        clear.Click += (_, _) => { minimum.SelectedIndex = 0; extension.Text = "全部扩展名"; };
        apply.Click += (_, _) =>
        {
            string? value = Extension;
            if (value != null && value != "无扩展名" && (!value.StartsWith('.') || value.Length < 2 || value.IndexOfAny(['\\','/',':','*','?','"','<','>','|']) >= 0))
            { error.Text = "请输入如 .zip 的扩展名，或选择全部。"; return; }
            DialogResult = DialogResult.OK;
        };
        actions.Controls.AddRange([apply, cancel, clear]); layout.Controls.Add(actions, 0, 5); Controls.Add(layout);
        AcceptButton = apply; CancelButton = cancel;
    }
    internal static IReadOnlyList<string> Extensions(DiskEntry directory, CancellationToken token)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in directory.Descendants())
        {
            token.ThrowIfCancellationRequested();
            if (!entry.IsDirectory && values.Count < 4096) values.Add(entry.Extension);
        }
        return values.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
