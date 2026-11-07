namespace DiskAtlas;

internal sealed class DiagnosticsDialog : Form
{
    private readonly DiagnosticService service;
    private readonly CheckBox enabled = new() { Name = "loggingEnabled", Text = "开启本地运行诊断", AutoSize = true };
    private readonly Label summary = Caption("正在读取本地诊断…");
    private readonly Label message = Caption("只记录必要计数与状态，不记录路径、文件名或异常原文。");
    private readonly ComboBox period = Selector("timeFilter", ["全部时间", "最近 24 小时", "最近 7 天"]);
    private readonly ComboBox severity = Selector("levelFilter", ["全部级别", "信息", "警告", "错误"]);
    private readonly DataGridView records = Grid("diagnosticRecords");
    private readonly TextBox detail = new() { Name = "recordDetails", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly DataGridView notes = Grid("problemNotes");
    private readonly Button refresh = Command("刷新", "refreshDiagnostics");
    private readonly Button clear = Command("清空运行诊断…", "clearDiagnostics");
    private readonly Button export = Command("预览导出…", "previewExport");
    private readonly Button add = Command("新增问题记录", "addNote");
    private readonly Button edit = Command("编辑", "editNote");
    private readonly Button delete = Command("删除…", "deleteNote");
    private DiagnosticSnapshot? snapshot;
    private bool busy, updating;
    private bool closed;
    internal bool IsBusy => busy;
    internal DiagnosticSnapshot? Snapshot => snapshot;

    public DiagnosticsDialog(DiagnosticService service)
    {
        this.service = service;
        Text = "AsukaLens · 诊断"; Name = "diagnosticsWindow";
        Font = Theme.Font(9); AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(900, 640); MinimumSize = new Size(730, 530);
        StartPosition = FormStartPosition.CenterParent;
        var root = Rows(SizeType.AutoSize, 0, SizeType.Percent, 100, SizeType.AutoSize, 0);
        root.Padding = new Padding(14);
        var heading = Flow(enabled, summary); root.Controls.Add(heading, 0, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill, Name = "diagnosticTabs" };
        root.Controls.Add(tabs, 0, 1);
        var runtime = new TabPage("运行诊断"); var user = new TabPage("问题记录"); var dev = new TabPage("开发记录说明");
        tabs.TabPages.AddRange([runtime, user, dev]);
        var runtimeLayout = Rows(SizeType.AutoSize, 0, SizeType.Percent, 100, SizeType.Absolute, 130, SizeType.AutoSize, 0);
        runtimeLayout.Padding = new Padding(8); runtime.Controls.Add(runtimeLayout);
        runtimeLayout.Controls.Add(Flow(Caption("时间"), period, Caption("级别"), severity, refresh), 0, 0);
        records.ReadOnly = false;
        records.Columns.Add(new DataGridViewCheckBoxColumn { Name = "include", HeaderText = "导出", Width = 48, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        AddColumn(records, "time", "本地时间", 28); AddColumn(records, "level", "级别", 12);
        AddColumn(records, "event", "事件", 29); AddColumn(records, "phase", "阶段 / 刷新方式", 31);
        runtimeLayout.Controls.Add(records, 0, 1); runtimeLayout.Controls.Add(detail, 0, 2);
        runtimeLayout.Controls.Add(Flow(clear, export, Caption("先勾选记录，再预览；不会自动上传。")), 0, 3);
        var noteLayout = Rows(SizeType.AutoSize, 0, SizeType.Percent, 100, SizeType.AutoSize, 0);
        noteLayout.Padding = new Padding(8); user.Controls.Add(noteLayout);
        var noteHint = Caption("仅保存你主动填写的内容；最多 100 条。不自动采集，也不纳入运行诊断导出。");
        noteHint.MaximumSize = new Size(680, 0); noteLayout.Controls.Add(noteHint, 0, 0);
        AddColumn(notes, "title", "问题标题", 65); AddColumn(notes, "modified", "最后修改", 35);
        noteLayout.Controls.Add(notes, 0, 1); noteLayout.Controls.Add(Flow(add, edit, delete), 0, 2);
        dev.Controls.Add(new TextBox
        {
            Name = "developmentInfo", AccessibleName = "当前程序与开发记录", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical,
            Text = "开发记录保存在项目 Git 历史与 iterations/ 中。\r\n\r\n" +
                "每轮记录包含需求、决策、关键文件、验证结果、限制和可恢复补丁。此窗口不读取本机开发目录，也不从网络自动获取记录。\r\n\r\n" +
                "当前程序：\r\n" + AppInfo.Summary + "\r\n" +
                "项目仓库：https://github.com/M66Y/Hard-Drive-Space-Visualization\r\n\r\n" +
                "运行诊断与问题记录只保存在本机的软件数据目录中。分享问题时，请先自行核对所选内容。"
        });
        var close = Command("关闭", "closeDiagnostics"); close.Click += (_, _) => Close();
        root.Controls.Add(Flow(message, close), 0, 2); Controls.Add(root);
        message.MaximumSize = new Size(710, 0);
        enabled.CheckedChanged += async (_, _) => { if (!updating) await ToggleAsync(enabled.Checked); };
        refresh.Click += async (_, _) => await RefreshAsync();
        period.SelectedIndexChanged += (_, _) => RenderRecords(); severity.SelectedIndexChanged += (_, _) => RenderRecords();
        records.SelectionChanged += (_, _) => ShowDetail();
        notes.SelectionChanged += (_, _) => UpdateNoteActions();
        records.CurrentCellDirtyStateChanged += (_, _) => { if (records.IsCurrentCellDirty) records.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        clear.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "清空运行诊断和最近异常报告？问题记录、背景与已导出文件会保留。", "清空运行诊断",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) await ClearAsync();
        };
        export.Click += (_, _) =>
        {
            string? text = SelectedPreview();
            if (text == null) { message.Text = "请先勾选需要导出的记录。"; return; }
            using var preview = new DiagnosticExportDialog(text); preview.ShowDialog(this);
        };
        add.Click += async (_, _) => await EditNoteAsync(null);
        edit.Click += async (_, _) => { if (SelectedNote() is { } note) await EditNoteAsync(note); };
        notes.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0 && SelectedNote() is { } note) await EditNoteAsync(note); };
        delete.Click += async (_, _) =>
        {
            if (SelectedNote() is not { } note) return;
            if (MessageBox.Show(this, "删除当前选中的问题记录？此操作无法撤销。", "删除问题记录", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question) != DialogResult.OK) return;
            await WorkAsync(async () => { await service.DeleteNoteAsync(note); await LoadNotesAsync(); return "问题记录已删除。"; });
        };
        Shown += async (_, _) => await RefreshAsync();
        FormClosing += (_, _) => closed = true;
        ApplyTheme();
        UpdateNoteActions();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        Size = new(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        if (Owner != null) Location = new Point(Math.Clamp(Owner.Left + (Owner.Width - Width) / 2, area.Left, area.Right - Width),
            Math.Clamp(Owner.Top + (Owner.Height - Height) / 2, area.Top, area.Bottom - Height));
    }

    internal async Task RefreshAsync() => await WorkAsync(async () =>
    {
        var result = await service.SnapshotAsync();
        if (Gone) return "";
        snapshot = result; RenderPolicy(); RenderRecords();
        await LoadNotesAsync();
        return !result.Policy.Healthy ? "诊断设置不可用，自动记录已暂停；请重新选择开关。" :
            service.StorageFailed || result.MaintenanceFailed ? "部分诊断未保存或未能清理；当前仅显示可读取内容。请检查文件占用后重试。" :
            $"已刷新。拒绝 / 无法读取 {result.Rejected} 条；本进程队列遗漏 {service.Dropped} 条。" +
            (result.Limited ? "仅显示最近 2,000 条。" : "");
    });

    internal async Task ToggleAsync(bool value) => await WorkAsync(async () =>
    {
        await service.SetEnabledAsync(value); RenderPolicy();
        return value ? "运行诊断已开启，仅记录必要状态与计数。" : "运行诊断已关闭，待写队列已丢弃；已有记录仍保留。";
    });

    internal async Task ClearAsync() => await WorkAsync(async () =>
    {
        var result = await service.ClearAsync();
        var current = await service.SnapshotAsync();
        if (!Gone) { snapshot = current; RenderRecords(); RenderPolicy(); }
        return result.Failed == 0 ? "运行诊断已清空；之后发生的新事件仍按开关记录。" :
            $"已清除 {result.Deleted} 个文件，{result.Failed} 个未清除；请解除占用后重试。";
    });

    private bool Gone => IsDisposed || Disposing || closed;
    private async Task WorkAsync(Func<Task<string>> work)
    {
        if (busy || Gone) return;
        busy = true; ControlsEnabled(false); message.Text = "正在处理，请稍候…";
        try { string result = await work(); if (!Gone) message.Text = result; }
        catch (Exception ex) when (DiagnosticStore.IsStorageError(ex))
        {
            if (!Gone) message.Text = ex is NoteConflictException ? "记录已被另一个窗口修改。请刷新后重试。" :
                "操作未完成；请检查存储空间、文件占用或数据是否损坏。已有文件不会被当作空数据覆盖。";
        }
        finally { busy = false; if (!Gone) { RenderPolicy(); ControlsEnabled(true); } }
    }
    private void ControlsEnabled(bool value)
    {
        foreach (Control control in new Control[] { enabled, refresh, clear, export, add, edit, delete, period, severity }) control.Enabled = value;
        UpdateNoteActions();
    }
    private void UpdateNoteActions() => edit.Enabled = delete.Enabled = !busy && SelectedNote() != null;
    private void RenderPolicy()
    {
        if (Gone) return;
        updating = true; enabled.Checked = service.Enabled; updating = false;
        summary.Text = $"运行分段 {(snapshot?.Bytes ?? 0) / 1024d:0.#} KiB / 8 MiB · 保留 14 天";
    }
    private void RenderRecords()
    {
        if (Gone || busy && snapshot == null) return;
        records.Rows.Clear(); detail.Clear();
        if (snapshot == null) return;
        var cutoff = period.SelectedIndex switch { 1 => DateTimeOffset.UtcNow.AddDays(-1), 2 => DateTimeOffset.UtcNow.AddDays(-7), _ => DateTimeOffset.MinValue };
        foreach (var record in snapshot.Records.Where(r => r.TimeUtc >= cutoff && (severity.SelectedIndex == 0 || (int)r.Level == severity.SelectedIndex - 1)))
        {
            int index = records.Rows.Add(false, record.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                new[] { "信息", "警告", "错误" }[(int)record.Level], record.Label,
                record.Phase is { } phase ? ScanProgressControl.PhaseName(phase) : record.Method is { } method ? RefreshPresentation.MethodName(method) : "—");
            records.Rows[index].Tag = record;
        }
        records.ClearSelection();
    }
    private void ShowDetail()
    {
        if (records.CurrentRow?.Tag is not DiagnosticRecord record) { detail.Clear(); return; }
        detail.Text = record.Summary().Replace("\n", "\r\n", StringComparison.Ordinal);
    }
    internal string? SelectedPreview()
    {
        records.EndEdit();
        var selected = records.Rows.Cast<DataGridViewRow>().Where(r => r.Cells[0].Value is true).Select(r => (DiagnosticRecord)r.Tag!).ToArray();
        return selected.Length == 0 ? null : DiagnosticCodec.Preview(selected, service.Dropped, snapshot?.Rejected ?? 0,
            snapshot?.Limited ?? false, service.StorageFailed || snapshot?.MaintenanceFailed == true);
    }
    private ProblemNote? SelectedNote() => notes.SelectedRows.Count == 1 ? notes.SelectedRows[0].Tag as ProblemNote : null;
    private async Task LoadNotesAsync()
    {
        var result = await service.NotesAsync(); if (Gone) return;
        notes.Rows.Clear();
        foreach (var note in result) notes.Rows[notes.Rows.Add(note.Title, note.ModifiedUtc.ToLocalTime().ToString("g"))].Tag = note;
        notes.ClearSelection();
    }
    private async Task EditNoteAsync(ProblemNote? previous)
    {
        using var editor = new ProblemNoteDialog(service, previous);
        if (editor.ShowDialog(this) == DialogResult.OK) await WorkAsync(async () => { await LoadNotesAsync(); return "问题记录已保存，仅保留在本机。"; });
    }

    internal void ApplyTheme() => ApplyPalette(this);
    internal static void ApplyPalette(Control control)
    {
        bool contrast = SystemInformation.HighContrast;
        control.ForeColor = contrast ? SystemColors.WindowText : Theme.Text;
        control.BackColor = contrast ? SystemColors.Window : Theme.Surface;
        if (control is Button button)
        {
            button.FlatStyle = contrast ? FlatStyle.Standard : FlatStyle.Flat;
            button.ForeColor = contrast ? SystemColors.ControlText : Theme.Accent;
            button.FlatAppearance.BorderColor = Theme.Border;
            button.FlatAppearance.MouseOverBackColor = Theme.Selection;
        }
        if (control is DataGridView grid)
        {
            grid.BackgroundColor = control.BackColor; grid.GridColor = contrast ? SystemColors.ControlDark : Theme.Border;
            grid.DefaultCellStyle.BackColor = control.BackColor; grid.DefaultCellStyle.ForeColor = control.ForeColor;
            grid.DefaultCellStyle.SelectionBackColor = contrast ? SystemColors.Highlight : Theme.Selection;
            grid.DefaultCellStyle.SelectionForeColor = contrast ? SystemColors.HighlightText : Theme.Text;
            grid.EnableHeadersVisualStyles = contrast;
            grid.ColumnHeadersDefaultCellStyle.BackColor = control.BackColor; grid.ColumnHeadersDefaultCellStyle.ForeColor = control.ForeColor;
        }
        foreach (Control child in control.Controls) ApplyPalette(child);
    }
    internal static Button Command(string text, string name) => new() { Text = text, Name = name, AutoSize = true, MinimumSize = new Size(64, 30), Padding = new Padding(5, 2, 5, 2) };
    internal static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(4, 8, 8, 8) };
    internal static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
        panel.Controls.AddRange(controls); return panel;
    }
    internal static TableLayoutPanel Rows(params object[] sizes)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = sizes.Length / 2, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < sizes.Length; i += 2) panel.RowStyles.Add(new((SizeType)sizes[i], Convert.ToSingle(sizes[i + 1])));
        return panel;
    }
    private static ComboBox Selector(string name, string[] choices)
    {
        var combo = new ComboBox { Name = name, DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        combo.Items.AddRange(choices); combo.SelectedIndex = 0; return combo;
    }
    private static DataGridView Grid(string name) => new()
    {
        Name = name, Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None
    };
    private static void AddColumn(DataGridView grid, string name, string title, float weight) => grid.Columns.Add(new DataGridViewTextBoxColumn
    { Name = name, HeaderText = title, ReadOnly = true, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
    protected override void Dispose(bool disposing)
    {
        if (disposing) closed = true;
        base.Dispose(disposing);
    }
}
