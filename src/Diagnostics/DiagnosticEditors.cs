namespace DiskAtlas;

internal sealed class DiagnosticExportDialog : Form
{
    private bool saving;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken token;
    internal string Preview { get; }
    public DiagnosticExportDialog(string preview)
    {
        token = lifetime.Token;
        Preview = preview; Text = "预览诊断导出"; Font = Theme.Font(9); AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96); ClientSize = new Size(760, 530); MinimumSize = new Size(580, 400);
        StartPosition = FormStartPosition.CenterParent;
        var root = DiagnosticsDialog.Rows(SizeType.AutoSize, 0, SizeType.Percent, 100, SizeType.AutoSize, 0); root.Padding = new Padding(12);
        var status = DiagnosticsDialog.Caption("这是固定快照，只包含勾选的诊断；问题记录和之后的新事件不在其中。");
        status.MaximumSize = new Size(700, 0); root.Controls.Add(status, 0, 0);
        root.Controls.Add(new TextBox { Name = "exportPreview", Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both, WordWrap = false, Text = preview.Replace("\n", "\r\n", StringComparison.Ordinal) }, 0, 1);
        var save = DiagnosticsDialog.Command("保存到文件…", "saveExport");
        var cancel = DiagnosticsDialog.Command("取消", "cancelExport"); cancel.DialogResult = DialogResult.Cancel;
        root.Controls.Add(DiagnosticsDialog.Flow(save, cancel), 0, 2); Controls.Add(root); CancelButton = cancel;
        save.Click += async (_, _) =>
        {
            using var picker = new SaveFileDialog { Filter = "诊断文本|*.txt", FileName = "AsukaLens-diagnostics.txt", OverwritePrompt = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            saving = true; save.Enabled = cancel.Enabled = false; status.Text = "正在保存固定快照…";
            try
            {
                await DiagnosticService.ExportAsync(picker.FileName, Preview, token);
                if (!IsDisposed && !token.IsCancellationRequested) { saving = false; DialogResult = DialogResult.OK; Close(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (DiagnosticStore.IsStorageError(ex))
            { if (!IsDisposed) status.Text = "保存失败，已有目标未被替换。可以换一个位置重试；预览内容保持不变。"; }
            finally { saving = false; if (!IsDisposed) save.Enabled = cancel.Enabled = true; }
        };
        FormClosing += (_, e) =>
        {
            if (saving && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; status.Text = "正在保存，请稍候。"; }
            else lifetime.Cancel();
        };
        DiagnosticsDialog.ApplyPalette(this);
        Shown += (_, _) => { ((TextBox)Controls.Find("exportPreview", true).Single()).Select(0, 0); save.Focus(); };
    }
    protected override void Dispose(bool disposing) { if (disposing && !IsDisposed) { lifetime.Cancel(); lifetime.Dispose(); } base.Dispose(disposing); }
}

internal sealed class ProblemNoteDialog : Form
{
    private bool saving;
    internal readonly TextBox TitleInput = new() { Name = "noteTitle", MaxLength = 100, Dock = DockStyle.Fill };
    internal readonly TextBox BodyInput = new() { Name = "noteBody", MaxLength = 2000, Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    public ProblemNoteDialog(DiagnosticService service, ProblemNote? previous)
    {
        Text = previous == null ? "新增问题记录" : "编辑问题记录";
        Font = Theme.Font(9); AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new(96, 96);
        ClientSize = new Size(650, 470); MinimumSize = new Size(520, 360); StartPosition = FormStartPosition.CenterParent;
        var root = DiagnosticsDialog.Rows(SizeType.AutoSize, 0, SizeType.AutoSize, 0, SizeType.AutoSize, 0, SizeType.Percent, 100, SizeType.AutoSize, 0, SizeType.AutoSize, 0);
        root.Padding = new Padding(12); Controls.Add(root);
        root.Controls.Add(DiagnosticsDialog.Caption("标题（最多 100 字符）"), 0, 0); root.Controls.Add(TitleInput, 0, 1);
        root.Controls.Add(DiagnosticsDialog.Caption("现象、复现步骤、备注（合计最多 2,000 字符）"), 0, 2); root.Controls.Add(BodyInput, 0, 3);
        var status = DiagnosticsDialog.Caption("仅在本机保存，不自动附加路径或截图，也不纳入诊断导出。"); status.MaximumSize = new Size(580, 0);
        root.Controls.Add(status, 0, 4);
        TitleInput.Text = previous?.Title ?? ""; BodyInput.Text = previous?.Body ?? "";
        var save = DiagnosticsDialog.Command("保存", "saveNote"); var cancel = DiagnosticsDialog.Command("取消", "cancelNote");
        cancel.DialogResult = DialogResult.Cancel; CancelButton = cancel;
        root.Controls.Add(DiagnosticsDialog.Flow(save, cancel), 0, 5);
        save.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(TitleInput.Text)) { status.Text = "请填写问题标题。"; TitleInput.Focus(); return; }
            saving = true; save.Enabled = cancel.Enabled = TitleInput.Enabled = BodyInput.Enabled = false;
            try
            {
                await service.SaveNoteAsync(previous, TitleInput.Text, BodyInput.Text);
                if (!IsDisposed) { saving = false; DialogResult = DialogResult.OK; Close(); }
            }
            catch (Exception ex) when (DiagnosticStore.IsStorageError(ex))
            {
                if (!IsDisposed) status.Text = ex is NoteConflictException ? "原记录已在其他窗口改变。请保留当前文字，关闭后刷新再编辑。" :
                    ex is NoteLimitException ? "已达到条数或大小限制，请先整理问题记录；当前内容仍保留。" :
                    "保存失败。请检查存储空间或文件占用；当前内容仍保留。";
            }
            finally { saving = false; if (!IsDisposed) save.Enabled = cancel.Enabled = TitleInput.Enabled = BodyInput.Enabled = true; }
        };
        FormClosing += (_, e) => { if (saving && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; status.Text = "正在保存，请稍候。"; } };
        DiagnosticsDialog.ApplyPalette(this);
    }
}
