namespace DiskAtlas;

// An immutable local snapshot; it performs no filesystem work and writes no log.
internal sealed class SelectionDetailsDialog : Form
{
    internal readonly ActionButton Primary;
    internal readonly ActionButton CopyPath;
    internal readonly TextBox FullPath;
    private readonly Font bodyFont = Theme.Font(Theme.BodyFontSize);
    private readonly Font captionFont = Theme.Font(Theme.CaptionFontSize);
    private readonly Font nameFont = Theme.Font(12, FontStyle.Bold);
    private bool initialLayoutPrepared;

    internal SelectionDetailsDialog(SelectionDetails details)
    {
        Text = "AsukaLens · 项目详情";
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        Font = bodyFont; BackColor = Theme.Surface; ForeColor = Theme.Text;
        ClientSize = new Size(760, 470); MinimumSize = new Size(560, 390);
        Padding = new Padding(18);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
        foreach (int height in new[] { 50, 105, 23, 98 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 42));
        layout.Controls.Add(new NoHoverLabel { Text = details.Name, Font = nameFont, Dock = DockStyle.Fill,
            AutoEllipsis = false, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        fields.ColumnStyles.Add(new(SizeType.Percent, 50)); fields.ColumnStyles.Add(new(SizeType.Percent, 50));
        fields.RowStyles.Add(new(SizeType.Percent, 50)); fields.RowStyles.Add(new(SizeType.Percent, 50));
        (string Label, string Value)[] values = [("对象类型", details.Kind), ("修改时间", details.Modified),
            ("文件大小", details.LogicalSize), ("实际占用", details.Allocation)];
        for (int i = 0; i < values.Length; i++)
        {
            var cell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            cell.RowStyles.Add(new(SizeType.Absolute, 20)); cell.RowStyles.Add(new(SizeType.Percent, 100));
            cell.Controls.Add(new NoHoverLabel { Text = values[i].Label, Font = captionFont, ForeColor = Theme.Muted, Dock = DockStyle.Fill }, 0, 0);
            cell.Controls.Add(new NoHoverLabel { Text = values[i].Value, Dock = DockStyle.Fill, AutoEllipsis = false }, 0, 1);
            fields.Controls.Add(cell, i % 2, i / 2);
        }
        layout.Controls.Add(fields, 0, 1);
        layout.Controls.Add(new NoHoverLabel { Text = "完整路径", Font = captionFont, ForeColor = Theme.Muted, Dock = DockStyle.Fill }, 0, 2);
        FullPath = new TextBox { ReadOnly = true, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
            Text = details.Path, Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None, AccessibleName = "完整路径", Font = bodyFont };
        layout.Controls.Add(FullPath, 0, 3);
        var notes = new TextBox { ReadOnly = true, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
            Text = string.Join("\r\n\r\n", new[] { details.Note, details.Scope }.Where(s => s.Length > 0)),
            Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Muted, Font = captionFont,
            BorderStyle = BorderStyle.None, AccessibleName = "占用及统计范围" };
        layout.Controls.Add(notes, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty };
        var close = new ActionButton("关闭") { Width = 80, Height = 34, DialogResult = DialogResult.Cancel };
        Primary = new ActionButton(details.ActionText, true) { Width = 118, Height = 34, DialogResult = DialogResult.OK,
            Enabled = details.Action != SelectionAction.None };
        CopyPath = new ActionButton("复制路径") { Width = 96, Height = 34, Enabled = details.Path.Length > 0 };
        CopyPath.Click += (_, _) =>
        {
            try { Clipboard.SetText(details.Path); CopyPath.Text = "已复制"; }
            catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or System.Threading.ThreadStateException)
            { FullPath.Focus(); FullPath.SelectAll(); CopyPath.Text = "请按Ctrl+C"; }
        };
        actions.Controls.Add(close); actions.Controls.Add(Primary); actions.Controls.Add(CopyPath);
        layout.Controls.Add(actions, 0, 5); Controls.Add(layout);
        CancelButton = close;
        Shown += (_, _) => close.Focus();
    }

    protected override void OnLoad(EventArgs e)
    {
        if (!initialLayoutPrepared)
        {
            initialLayoutPrepared = true;
            float scale = DeviceDpi / 96f;
            if (Math.Abs(scale - 1) > .01f) Scale(new SizeF(scale, scale));
        }
        base.OnLoad(e);
        var area = Screen.FromControl(Owner ?? this).WorkingArea;
        MinimumSize = new(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        Size = new(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { bodyFont.Dispose(); captionFont.Dispose(); nameFont.Dispose(); }
    }
}
