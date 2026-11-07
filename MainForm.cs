using System.Diagnostics;

namespace DiskAtlas;

internal sealed class MainForm : Form
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, IProgress<ScanProgress>, CancellationToken, LiveScanSession, ScanResult>? ScanRunner { get; init; }
    internal readonly WallpaperRaster WallpaperRaster = new();
    internal BackgroundView BackgroundCrop { get; private set; } = BackgroundView.Default;
    internal bool HasCustomBackground { get; private set; }
    private readonly string backgroundSettingsPath;
    private Color automaticBackgroundAccent = Theme.DefaultAccent;
    private readonly ActionButton backgroundSettings = new("背景设置") { Dock = DockStyle.Fill };
    private readonly ScanCoordinator coordinator = new();
    private readonly ComboBox strategyCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.Surface, ForeColor = Theme.Text };
    private readonly ComboBox metricCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.Surface, ForeColor = Theme.Text };
    private readonly Label refreshInfo = LabelOf("首次完整扫描，刷新时自动检查变化", 8, false, true);
    private readonly ToolTip scanTip = new() { AutoPopDelay = 20000, InitialDelay = 300 };
    private SizeMetric metric;
    private bool changingMetric, forceFullNext;
    private readonly NavigationPanel navigation = new() { Dock = DockStyle.Fill };
    private readonly TreemapControl map = new() { Dock = DockStyle.Fill };
    private readonly ActionButton choose = new("＋  选择文件夹", true) { Width = 146, Dock = DockStyle.Fill };
    private readonly ActionButton refresh = new("重新扫描") { Width = 96, Dock = DockStyle.Fill, Enabled = false };
    private readonly ActionButton cancel = new("取消扫描") { Width = 96, Dock = DockStyle.Fill, Visible = false };
    private readonly ActionButton up = new("↑  上一级") { Width = 92, Dock = DockStyle.Fill, Enabled = false };
    private readonly ActionButton home = new("扫描根目录") { Width = 108, Dock = DockStyle.Fill, Enabled = false };
    private readonly ActionButton reveal = new("在资源管理器中定位") { Width = 170, Dock = DockStyle.Fill, Enabled = false };
    private readonly ActionButton issues = new("查看未读取项") { Width = 115, Dock = DockStyle.Fill, Visible = false };
    private readonly ActionButton clearType = new("清除类型筛选") { Dock = DockStyle.Fill, Visible = false, Margin = new Padding(0, 14, 12, 12) };
    private ColumnStyle? filterColumn;
    private readonly ActionButton previousPage = new("上一页") { Dock = DockStyle.Fill, Enabled = false };
    private readonly ActionButton nextPage = new("下一页") { Dock = DockStyle.Fill, Enabled = false };
    private readonly Label pageInfo = LabelOf("", 8, false, true);
    private RowStyle? pagingRow;
    private int pageIndex;
    private readonly Label title = LabelOf("文件夹空间，一目了然", 20, true);
    private readonly Label subtitle = LabelOf("选择一个文件夹，发现每一份空间的去向。", 9, false, true);
    private readonly Label mapTitle = LabelOf("空间分布", 11, true);
    private readonly Label mapCaption = LabelOf("矩形面积代表文件大小", 8, false, true);
    private readonly Label listTitle = LabelOf("文件与文件夹", 11, true);
    private readonly Label listCaption = LabelOf("按大小排序", 8, false, true);
    private readonly Label detailTitle = LabelOf("选择矩形或列表项目，查看详情", 10, true);
    private readonly Label detailPath = LabelOf("双击文件夹进入 · 双击文件在资源管理器中定位", 8, false, true);
    private readonly Label status = LabelOf("就绪  ·  仅扫描本地文件，文件不会上传", 8, false, true);
    private readonly TextBox pathBox = new() { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text, Dock = DockStyle.Fill };
    private readonly TextBox search = new() { PlaceholderText = "搜索名称或扩展名…", BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text, Dock = DockStyle.Fill };
    private readonly ScanProgressControl progressBar = new() { Dock = DockStyle.Fill };
    private readonly CheckBox reduceMotion = new() { Text = "减弱动效", AutoSize = true, Anchor = AnchorStyles.Right, BackColor = Color.Transparent };
    private readonly DataGridView grid = new WallpaperGrid();
    private readonly StatCard totalCard = new() { Title = "当前目录大小", Caption = "文件逻辑大小 · 1024 进制", Highlight = true, Dock = DockStyle.Fill };
    private readonly StatCard filesCard = new() { Title = "文件数量", Caption = "包含所有子文件夹", Dock = DockStyle.Fill };
    private readonly StatCard foldersCard = new() { Title = "子文件夹", Caption = "可双击逐层探索", Dock = DockStyle.Fill };
    private readonly StatCard timeCard = new() { Title = "扫描用时", Caption = "后台扫描，不阻塞操作", Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer searchTimer = new() { Interval = 220 };
    private readonly System.Windows.Forms.Timer previewTimer = new() { Interval = 500 };
    private LiveScanSession? liveSession;
    private bool showingPreview;
    private string? previewDisplayedPath;
    private ScanResult? scan;
    private DiskEntry? current;
    private CancellationTokenSource? scanCancellation;
    private CancellationTokenSource? viewCancellation;
    private bool clearingSearch;
    private string? typeFilter;
    private MapItem? selection;
    private int mode;
    private string sortColumn = "size";
    private bool sortAscending;
    private bool updating;

    public MainForm(string? initialPath, bool loadSavedBackground = false, string? backgroundSettingsPath = null)
    {
        this.backgroundSettingsPath = backgroundSettingsPath ?? BackgroundSettings.PathForUser;
        Text = "AsukaLens · 磁盘空间可视化";
        if (Environment.ProcessPath is { } executable) Icon = Icon.ExtractAssociatedIcon(executable);
        Size = new Size(1440, 940);
        MinimumSize = new Size(1120, 780);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        BackgroundImage = Wallpaper.Load();
        ForeColor = Theme.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.Font(9);
        KeyPreview = true;
        DoubleBuffered = true;
        AllowDrop = true;
        BuildInterface();
        reduceMotion.Checked = false;
        reduceMotion.CheckedChanged += (_, _) => ApplyMotionPreference();
        ApplyMotionPreference();
        scanTip.SetToolTip(reduceMotion, "关闭图块回弹和进度往返动画；保留真实进度与静态高亮。本次运行有效。");
        ConfigureGrid();
        strategyCombo.Items.AddRange(["自动", "低占用", "高速"]); strategyCombo.SelectedIndex = 0;
        strategyCombo.SelectedIndexChanged += (_, _) => { if (scanCancellation == null) UpdateStrategyHint(); };
        UpdateStrategyHint();
        metricCombo.Items.AddRange(["文件大小", "实际占用"]); metricCombo.SelectedIndex = 0;
        metricCombo.SelectedIndexChanged += (_, _) => { if (!changingMetric) { metric = (SizeMetric)metricCombo.SelectedIndex; UpdateView(true); } };
        var refreshMenu = Theme.CreateContextMenu();
        refreshMenu.Items.Add("强制完整重新扫描（Ctrl+F5）", null, async (_, _) => await ForceRefresh());
        refreshMenu.Items.Add("最近完成的扫描详情", null, (_, _) => ShowRefreshDetails());
        refresh.ContextMenuStrip = refreshMenu;
        refreshInfo.Click += (_, _) => { if (scanCancellation == null) ShowRefreshDetails(); };
        choose.Click += async (_, _) => await ChooseFolder();
        refresh.Click += async (_, _) => { if (scan != null) await ScanFolder(scan.Root.Path); };
        cancel.Click += (_, _) => { scanCancellation?.Cancel(); cancel.Enabled = false; status.Text = "正在取消扫描…"; };
        up.Click += (_, _) => { if (current?.Parent != null) OpenDirectory(current.Parent); };
        home.Click += (_, _) =>
        {
            if (showingPreview && current != null)
            {
                var root = current; while (root.Parent != null) root = root.Parent; OpenDirectory(root);
            }
            else if (scan != null) OpenDirectory(scan.Root);
        };
        reveal.Click += (_, _) => RevealSelection();
        issues.Click += (_, _) => ShowIssues();
        clearType.Click += (_, _) => { typeFilter = null; UpdateView(); };
        previousPage.Click += (_, _) => { PopulatePage(pageIndex - 1); SelectItem(null, false); };
        nextPage.Click += (_, _) => { PopulatePage(pageIndex + 1); SelectItem(null, false); };
        navigation.Navigate += SetMode;
        map.ItemSelected += item => SelectItem(item, true);
        map.ItemActivated += ActivateItem;
        search.TextChanged += (_, _) => { if (!clearingSearch) { searchTimer.Stop(); searchTimer.Start(); } };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); UpdateView(); };
        previewTimer.Tick += async (_, _) => { if (liveSession != null && viewCancellation == null) await UpdatePreviewAsync(); };
        KeyDown += HandleKeys;
        DragEnter += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1 && Directory.Exists(paths[0])) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) => { if (scanCancellation == null && e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1) await ScanFolder(paths[0]); };
        FormClosing += (_, _) => { scanCancellation?.Cancel(); viewCancellation?.Cancel(); };
        backgroundSettings.Click += (_, _) => EditBackground();
        backgroundSettings.Enabled = !loadSavedBackground;
        Shown += async (_, _) =>
        {
            if (loadSavedBackground) await RestoreSavedBackground();
            if (!IsDisposed && !Disposing && scanCancellation == null && scan == null &&
                !string.IsNullOrWhiteSpace(initialPath)) await ScanFolder(initialPath);
        };
    }

    private void PrepareInitialLayout()
    {
        // The handle has its monitor DPI by Load. Finish scaling before the window paints.
        float scale = DeviceDpi / 96f;
        if (Math.Abs(scale - 1) > .01f) Scale(new SizeF(scale, scale));
        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min((int)(1120 * scale), area.Width), Math.Min((int)(780 * scale), area.Height));
        Size = new Size(Math.Min((int)(1440 * scale), area.Width), Math.Min((int)(940 * scale), area.Height));
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
        WallpaperRaster.Get(BackgroundImage, ClientSize, BackgroundCrop);
    }

    private static Label LabelOf(string text, float size, bool bold = false, bool muted = false) => new()
    {
        Text = text, Font = Theme.Font(size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = muted ? Theme.Muted : Theme.Text,
        Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty
    };

    protected override void OnPaintBackground(PaintEventArgs e) => Wallpaper.Paint(this, e.Graphics);

    private string StrategyName => strategyCombo.SelectedIndex switch { 1 => "低占用", 2 => "高速", _ => "自动" };
    private void UpdateStrategyHint()
    {
        var mode = (ScanMode)Math.Max(0, strategyCombo.SelectedIndex);
        string explanation = ScanPolicy.Explain(mode);
        refreshInfo.Text = mode == ScanMode.Automatic ? "自动 · 按设备选择目录并发" :
            $"{StrategyName} · 目录并发上限 {ScanPolicy.Resolve(mode, DiskKind.Unknown).Workers}";
        scanTip.SetToolTip(strategyCombo, explanation);
        scanTip.SetToolTip(refreshInfo, explanation);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Repaint unchanged child bounds too: all surfaces share window coordinates.
        Invalidate(true);
    }

    private static WallpaperTable Table(int columns, int rows)
    {
        var panel = new WallpaperTable { ColumnCount = columns, RowCount = rows, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent };
        if (rows == 1) panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return panel;
    }

    private void BuildInterface()
    {
        var shell = Table(2, 1);
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 232));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.Controls.Add(navigation, 0, 0);
        var main = Table(1, 8);
        main.Padding = new Padding(28, 22, 28, 12);
        foreach (float height in new float[] { 85, 46, 124, 62 }) main.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        shell.Controls.Add(main, 1, 0);
        Controls.Add(shell);

        var header = Table(2, 1);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 374));
        var heading = Table(1, 2);
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 41));
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        heading.Controls.Add(title, 0, 0); heading.Controls.Add(subtitle, 0, 1);
        header.Controls.Add(heading, 0, 0);
        var actions = Table(3, 1);
        actions.Padding = new Padding(0, 13, 0, 29);
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.Controls.Add(cancel, 0, 0); actions.Controls.Add(refresh, 1, 0); actions.Controls.Add(choose, 2, 0);
        header.Controls.Add(actions, 1, 0);
        main.Controls.Add(header, 0, 0);

        var settings = Table(5, 1); settings.Padding = new Padding(0, 2, 0, 8);
        foreach (float width in new float[] { 68, 138, 82, 148 }) settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.Controls.Add(LabelOf("扫描策略", 9), 0, 0); settings.Controls.Add(new InputFrame(strategyCombo) { Margin = new Padding(0, 0, 20, 0) }, 1, 0);
        settings.Controls.Add(LabelOf("统计口径", 9), 2, 0); settings.Controls.Add(new InputFrame(metricCombo) { Margin = new Padding(0, 0, 15, 0) }, 3, 0);
        settings.ColumnCount = 6;
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        settings.Controls.Add(refreshInfo, 4, 0);
        settings.Controls.Add(backgroundSettings, 5, 0);
        main.Controls.Add(settings, 0, 1);

        var cards = Table(4, 1);
        for (int i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        StatCard[] stats = [totalCard, filesCard, foldersCard, timeCard];
        for (int i = 0; i < stats.Length; i++) { stats[i].Margin = new Padding(0, 0, i == 3 ? 0 : 14, 8); cards.Controls.Add(stats[i], i, 0); }
        main.Controls.Add(cards, 0, 2);

        var toolbar = Table(4, 1);
        toolbar.Padding = new Padding(0, 8, 0, 12);
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        toolbar.Controls.Add(up, 0, 0); toolbar.Controls.Add(home, 1, 0);
        toolbar.Controls.Add(new InputFrame(pathBox) { Margin = new Padding(6, 2, 10, 3) }, 2, 0);
        toolbar.Controls.Add(new InputFrame(search) { Margin = new Padding(0, 2, 0, 3) }, 3, 0);
        pathBox.Text = "未选择文件夹（支持拖入文件夹）";
        main.Controls.Add(toolbar, 0, 3);

        var split = new SplitContainer { Size = new Size(1100, 480), Dock = DockStyle.Fill, BackColor = Theme.Background, SplitterWidth = 14, Panel1MinSize = 300, Panel2MinSize = 290, Margin = Padding.Empty };
        // Set a valid size before setting the splitter distance and minimums at runtime.
        split.Size = new Size(1100, 480);
        split.SplitterDistance = 650;
        var mapPanel = Table(1, 2);
        mapPanel.Wash = 180;
        mapPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        mapPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var mapHeading = Table(3, 1); mapHeading.Wash = 210;
        mapHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterColumn = new ColumnStyle(SizeType.Absolute, 0);
        mapHeading.ColumnStyles.Add(filterColumn);
        mapHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
        mapHeading.Controls.Add(PanelHeader(mapTitle, mapCaption), 0, 0);
        mapHeading.Controls.Add(clearType, 1, 0);
        mapHeading.Controls.Add(reduceMotion, 2, 0);
        mapPanel.Controls.Add(mapHeading, 0, 0);
        mapPanel.Controls.Add(map, 0, 1);
        split.Panel1.Controls.Add(mapPanel);
        var listPanel = Table(1, 3);
        listPanel.Wash = 210;
        listPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        listPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pagingRow = new RowStyle(SizeType.Absolute, 0);
        listPanel.RowStyles.Add(pagingRow);
        listPanel.Controls.Add(PanelHeader(listTitle, listCaption), 0, 0);
        listPanel.Controls.Add(grid, 0, 1);
        var pages = Table(3, 1);
        pages.Padding = new Padding(8, 3, 8, 3);
        pages.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        pages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pages.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        pageInfo.TextAlign = ContentAlignment.MiddleCenter;
        pages.Controls.Add(previousPage, 0, 0); pages.Controls.Add(pageInfo, 1, 0); pages.Controls.Add(nextPage, 2, 0);
        listPanel.Controls.Add(pages, 0, 2);
        split.Panel2.Controls.Add(listPanel);
        main.Controls.Add(split, 0, 4);

        var detail = Table(2, 1);
        detail.Padding = new Padding(14, 12, 10, 8);
        detail.Margin = new Padding(0, 12, 0, 0);
        detail.Wash = 210;
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        var detailText = Table(1, 2); detailText.Wash = 210;
        detailText.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        detailText.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        detailText.Controls.Add(detailTitle, 0, 0); detailText.Controls.Add(detailPath, 0, 1);
        detail.Controls.Add(detailText, 0, 0); detail.Controls.Add(reveal, 1, 0);
        main.Controls.Add(detail, 0, 5);
        main.Controls.Add(progressBar, 0, 6);
        var footer = Table(2, 1);
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        footer.Controls.Add(status, 0, 0); footer.Controls.Add(issues, 1, 0);
        main.Controls.Add(footer, 0, 7);
    }

    private static Control PanelHeader(Label heading, Label caption)
    {
        var panel = Table(1, 2); panel.Wash = 210; panel.Padding = new Padding(16, 10, 12, 4);
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(heading, 0, 0); panel.Controls.Add(caption, 0, 1);
        return panel;
    }

    private void ConfigureGrid()
    {
        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = Theme.Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Color.FromArgb(239, 224, 217);
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeight = 34;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Sidebar, ForeColor = Theme.Text, Font = Theme.Font(8, FontStyle.Bold), SelectionBackColor = Theme.Sidebar, Padding = new Padding(10, 0, 0, 0) };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Surface, ForeColor = Theme.Text, SelectionBackColor = Theme.Selection, SelectionForeColor = Theme.Text, Font = Theme.Font(9), Padding = new Padding(10, 0, 4, 0) };
        grid.RowTemplate.Height = 40;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "名称", FillWeight = 55, MinimumWidth = 100, SortMode = DataGridViewColumnSortMode.Programmatic });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "size", HeaderText = "大小 ↓", FillWeight = 27, MinimumWidth = 85, SortMode = DataGridViewColumnSortMode.Programmatic });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "percent", HeaderText = "占比", FillWeight = 18, MinimumWidth = 65, SortMode = DataGridViewColumnSortMode.Programmatic });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "count", HeaderText = "文件数", FillWeight = 22, MinimumWidth = 70, Visible = false, SortMode = DataGridViewColumnSortMode.Programmatic });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "location", HeaderText = "所在位置", FillWeight = 38, MinimumWidth = 140, Visible = false, SortMode = DataGridViewColumnSortMode.Programmatic });
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(252, 245, 240);
        foreach (string columnName in new[] { "size", "percent", "count" })
        {
            var column = grid.Columns[columnName]!;
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            column.DefaultCellStyle.Padding = new Padding(2, 0, 10, 0);
            column.HeaderCell.Style.Padding = column.DefaultCellStyle.Padding;
        }
        grid.SelectionChanged += (_, _) => { if (!updating) SelectItem(grid.SelectedRows.Count > 0 ? RowItem(grid.SelectedRows[0].Index) : null, false); };
        grid.CellDoubleClick += (_, e) => { if (RowItem(e.RowIndex) is { } item) ActivateItem(item); };
        grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && selection != null) { ActivateItem(selection); e.Handled = e.SuppressKeyPress = true; } };
        grid.ColumnHeaderMouseClick += (_, e) =>
        {
            string next = grid.Columns[e.ColumnIndex].Name;
            if (next == "percent") next = "size";
            sortAscending = sortColumn == next ? !sortAscending : next is "name" or "location";
            sortColumn = next;
            UpdateView(true);
        };
        grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0 || RowItem(e.RowIndex) is not { } item) return;
            e.PaintBackground(e.ClipBounds, true);
            float s = DeviceDpi / 96f;
            using var brush = new SolidBrush(Theme.CategoryColor(item.Category));
            var dot = new Rectangle(e.CellBounds.X + (int)(12 * s), e.CellBounds.Y + e.CellBounds.Height / 2 - (int)(4 * s), (int)(8 * s), (int)(8 * s));
            e.Graphics!.FillRectangle(brush, dot);
            Theme.TextAt(e.Graphics, item.Name, e.CellStyle!.Font ?? grid.Font, Theme.Text, new Rectangle(dot.Right + (int)(8 * s), e.CellBounds.Y, Math.Max(0, e.CellBounds.Right - dot.Right - (int)(14 * s)), e.CellBounds.Height));
            if ((e.State & DataGridViewElementStates.Selected) != 0 && grid.Focused &&
                grid.CurrentCellAddress == new Point(e.ColumnIndex, e.RowIndex))
                e.Paint(e.ClipBounds, DataGridViewPaintParts.Focus);
            e.Handled = true;
        };
        var menu = Theme.CreateContextMenu();
        menu.Items.Add("进入文件夹 / 定位文件", null, (_, _) => { if (selection != null) ActivateItem(selection); });
        menu.Items.Add("在资源管理器中定位", null, (_, _) => RevealSelection());
        menu.Items.Add("复制完整路径", null, (_, _) => { if (selection?.Entry is { } entry) TryAction(() => Clipboard.SetText(entry.Path)); });
        menu.Opening += (_, e) => e.Cancel = selection?.Entry == null;
        grid.ContextMenuStrip = menu;
        grid.CellMouseDown += (_, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0) { grid.ClearSelection(); grid.Rows[e.RowIndex].Selected = true; } };
    }

    private async Task ChooseFolder()
    {
        if (scanCancellation != null) return;
        using var dialog = new FolderBrowserDialog { Description = "选择要分析的文件夹", UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (current != null) dialog.InitialDirectory = current.Path;
        if (dialog.ShowDialog(this) == DialogResult.OK) await ScanFolder(dialog.SelectedPath);
    }

    private async Task ScanFolder(string path)
    {
        if (scanCancellation != null) return;
        var previousDirectory = current;
        int previousMode = mode;
        string? previousType = typeFilter;
        string previousSearch = search.Text, previousSort = sortColumn;
        bool previousAscending = sortAscending;
        var previousMetric = metric;
        var options = new ScanOptions((ScanMode)strategyCombo.SelectedIndex);
        bool forceFull = forceFullNext; forceFullNext = false;
        var previousScan = scan;
        using var cancellation = new CancellationTokenSource();
        scanCancellation = cancellation;
        var session = new LiveScanSession();
        liveSession = session;
        searchTimer.Stop();
        viewCancellation?.Cancel(); viewCancellation = null;
        previewTimer.Start();
        choose.Enabled = refresh.Enabled = false;
        strategyCombo.Enabled = metricCombo.Enabled = false;
        cancel.Visible = cancel.Enabled = true;
        progressBar.BeginScan();
        map.Scanning = scan == null;
        map.Invalidate();
        status.Text = $"开始扫描：{path}";
        refreshInfo.Text = $"{StrategyName} · 准备扫描";
        refreshInfo.Cursor = Cursors.Default;
        scanTip.SetToolTip(refreshInfo, ScanPolicy.Explain(options.Mode));
        RefreshMethod? activeRefreshMethod = null;
        var progress = new Progress<ScanProgress>(p =>
        {
            if (IsDisposed || Disposing || !ReferenceEquals(scanCancellation, cancellation) || cancellation.IsCancellationRequested) return;
            progressBar.Report(p);
            string stage = ScanProgressControl.PhaseName(p.Phase);
            if (p.Method is { } selectedMethod) activeRefreshMethod = selectedMethod;
            if (p.Message != null) scanTip.SetToolTip(refreshInfo, p.Message);
            status.Text = $"{stage} · {SizeFormat.Number(p.Files)} 个文件 · {(p.Phase == ScanPhase.Verifying ? p.CurrentPath : SizeFormat.Bytes(p.Bytes) + " · " + p.CurrentPath)}";
            refreshInfo.Text = $"{(activeRefreshMethod is { } method ? RefreshPresentation.MethodName(method) : StrategyName)} · {stage}" +
                (p.WorkerLimit > 0 ? $" · 并发上限 {p.WorkerLimit}" : "");
            if (scan == null && !showingPreview)
            {
                totalCard.Value = SizeFormat.Bytes(p.Bytes); filesCard.Value = SizeFormat.Number(p.Files); foldersCard.Value = SizeFormat.Number(p.Folders);
                totalCard.Invalidate(); filesCard.Invalidate(); foldersCard.Invalidate();
            }
        });
        try
        {
            var result = await Task.Run(() => ScanRunner != null ? ScanRunner(path, progress, cancellation.Token, session) :
                coordinator.Scan(path, previousScan, options, progress, cancellation.Token, session, forceFull), cancellation.Token);
            if (IsDisposed || Disposing) return;
            cancellation.Token.ThrowIfCancellationRequested();
            bool isRefresh = scan != null && result.Root.Path.TrimEnd('\\', '/').Equals(scan.Root.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            string? desiredPath = showingPreview ? current?.Path : isRefresh ? previousDirectory?.Path : null;
            var restored = desiredPath != null ? result.Root.FindDirectory(desiredPath) : null;
            EndPreview();
            SetMetric(previousMetric);
            scan = result;
            current = restored ?? result.Root;
            navigation.RootName = scan.Root.Name; navigation.RootPath = scan.Root.Path; navigation.Invalidate();
            if (isRefresh)
            {
                RestoreQuery(); UpdateView();
            }
            else SetMode(0);
            issues.Visible = scan.Skipped > 0 || scan.Root.UnknownAllocationFiles > 0;
            if (scan.Statistics is { } stats)
            {
                refreshInfo.Text = $"{RefreshPresentation.MethodName(stats.Method)} · 重扫 {stats.ReadDirectories:N0} 目录 · 复用 {stats.ReusedFiles:N0} 文件 · 点击详情";
                scanTip.SetToolTip(refreshInfo, "点击查看最近完成的扫描详情\n" + RefreshPresentation.Details(stats));
            }
            else refreshInfo.Text = "扫描完成";
            status.Text = $"扫描完成 · {SizeFormat.Number(scan.Root.FileCount)} 个文件 · " + (scan.Statistics?.Reason ?? "") +
                (scan.Skipped > 0 ? $" · {scan.Skipped:N0} 项已跳过" : "") + (scan.Root.UnknownAllocationFiles > 0 ? $" · {scan.Root.UnknownAllocationFiles:N0} 项占用未知" : "");
            progressBar.Finish(scan.Skipped > 0 || scan.Root.Incomplete || scan.Root.UnknownAllocationFiles > 0 ? ScanDisplayState.Incomplete : ScanDisplayState.Completed);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
            {
                RestorePrevious();
                progressBar.Finish(ScanDisplayState.Cancelled);
                status.Text = scan == null ? "扫描已取消。选择文件夹可重新开始。" : "扫描已取消，仍显示上一次完成的结果。";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            if (!IsDisposed)
            {
                RestorePrevious();
                progressBar.Finish(ScanDisplayState.Failed);
                status.Text = "扫描未完成：" + ex.Message;
                MessageBox.Show(this, ex.Message, "无法扫描该文件夹", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        finally
        {
            previewTimer.Stop();
            liveSession = null;
            scanCancellation = null;
            if (!IsDisposed)
            {
                choose.Enabled = true; refresh.Enabled = scan != null; cancel.Visible = false;
                strategyCombo.Enabled = metricCombo.Enabled = true;
                refreshInfo.Cursor = scan?.Statistics != null ? Cursors.Hand : Cursors.Default;
                map.Scanning = false; map.Invalidate();
                if (cancellation.IsCancellationRequested || scan == previousScan) UpdateStrategyHint();
            }
        }

        void RestoreQuery()
        {
            mode = previousMode; typeFilter = previousType; sortColumn = previousSort; sortAscending = previousAscending;
            clearingSearch = true;
            try { search.Text = previousSearch; } finally { clearingSearch = false; }
            navigation.SelectedIndex = mode;
            SetMetric(previousMetric);
        }

        void RestorePrevious()
        {
            EndPreview(); current = previousDirectory; RestoreQuery();
            if (scan != null)
            {
                navigation.RootName = scan.Root.Name; navigation.RootPath = scan.Root.Path; navigation.Invalidate(); UpdateView();
            }
            else
            {
                ResetCards(); PopulateGrid([]); map.SetItems([]); map.LoadingView = false;
                map.Enabled = grid.Enabled = true; mapTitle.Text = "空间分布"; listTitle.Text = "文件与文件夹";
                totalCard.Caption = "文件逻辑大小 · 1024 进制"; timeCard.Caption = "后台扫描，不阻塞操作";
                map.EmptyTitle = "扫描尚未完成"; map.EmptyCaption = "选择文件夹可重新开始";
                pathBox.Text = "未选择文件夹（支持拖入文件夹）";
                title.Text = "文件夹空间，一目了然"; subtitle.Text = "选择一个文件夹，发现每一份空间的去向。";
                mapCaption.Text = "矩形面积代表文件大小"; listCaption.Text = "按大小排序";
                navigation.RootName = "尚未选择文件夹"; navigation.RootPath = "从一个文件夹，开始探索"; navigation.Invalidate();
                up.Enabled = home.Enabled = false;
            }
            issues.Visible = scan?.Skipped > 0 || scan?.Root.UnknownAllocationFiles > 0;
        }
    }

    private void SetMetric(SizeMetric value)
    {
        metric = value; changingMetric = true;
        try { metricCombo.SelectedIndex = (int)value; } finally { changingMetric = false; }
    }

    private void ApplyMotionPreference()
    {
        map.AnimationsEnabled = progressBar.AnimationsEnabled = !reduceMotion.Checked;
    }

    private async Task ForceRefresh()
    {
        if (scanCancellation != null || scan == null) return;
        forceFullNext = true; await ScanFolder(scan.Root.Path);
    }

    private void EndPreview()
    {
        previewTimer.Stop(); liveSession = null; showingPreview = false;
        previewDisplayedPath = null;
        viewCancellation?.Cancel(); viewCancellation = null;
        navigation.Enabled = search.Enabled = true;
    }

    private async Task UpdatePreviewAsync()
    {
        var session = liveSession;
        var scanSource = scanCancellation;
        if (session == null || scanSource == null || scanSource.IsCancellationRequested) return;
        string? requestedPath = showingPreview ? current?.Path : null;
        string? selectedName = showingPreview ? selection?.Entry?.Name : null;
        int firstVisibleRow = requestedPath == previewDisplayedPath ? grid.FirstDisplayedScrollingRowIndex : -1;
        viewCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(scanSource.Token);
        viewCancellation = cancellation;
        var token = cancellation.Token;
        var sort = sortColumn == "name" ? ViewSort.Name : ViewSort.Size;
        bool ascending = sortAscending;
        try
        {
            var result = await Task.Run(() =>
            {
                var snapshot = session.Capture(requestedPath, token) ?? session.Capture(null, token);
                if (snapshot == null) return ((ScanPreview Snapshot, ViewResult View)?)null;
                var view = ViewQuery.Build(new(snapshot.Directory, ViewMode.Folder, Sort: sort, Ascending: ascending), token);
                long remaining = snapshot.Directory.Size - view.VisibleBytes;
                if (remaining > 0) view.MapItems.Add(new MapItem(snapshot.Truncated ? "其余已扫描内容" : "正在汇总的内容", remaining, null, "其他", Math.Max(1, snapshot.DiscoveredChildren - view.Rows.Count), true));
                return (snapshot, view);
            }, token);
            if (result == null || IsDisposed || Disposing || token.IsCancellationRequested ||
                !ReferenceEquals(liveSession, session) || !ReferenceEquals(viewCancellation, cancellation)) return;
            var (preview, view) = result.Value;
            if (!showingPreview)
            {
                SetMetric(SizeMetric.Logical);
                ResetSearch();
                if (sortColumn is "count" or "location") { sortColumn = "size"; sortAscending = false; }
            }
            showingPreview = true; current = preview.Directory; mode = 0; typeFilter = null;
            navigation.SelectedIndex = 0; navigation.Enabled = search.Enabled = false;
            navigation.RootName = preview.Root.Name; navigation.RootPath = preview.Root.Path; navigation.Invalidate();
            clearType.Visible = false; if (filterColumn != null) filterColumn.Width = 0;
            title.Text = "正在扫描 · 实时预览";
            subtitle.Text = $"{current.Name}  /  统计尚未完成，大小会持续更新 · 可双击进入已发现的文件夹";
            pathBox.Text = current.Path;
            totalCard.Value = SizeFormat.Bytes(current.Size); filesCard.Value = SizeFormat.Number(current.FileCount); foldersCard.Value = SizeFormat.Number(current.FolderCount);
            timeCard.Value = $"{preview.Elapsed.TotalSeconds:0.0} s";
            totalCard.Caption = "已读取部分 · 非最终大小";
            totalCard.Title = "当前目录大小";
            timeCard.Caption = preview.Skipped > 0 ? $"仍在扫描 · {preview.Skipped:N0} 项已跳过" : "仍在扫描 · 约每 0.5 秒更新预览";
            foreach (var card in new[] { totalCard, filesCard, foldersCard, timeCard }) card.Invalidate();
            up.Enabled = current.Parent != null; home.Enabled = current != preview.Root;
            mapTitle.Text = "空间分布 · 扫描中"; listTitle.Text = "已发现的文件与文件夹";
            mapCaption.Text = "预览按文件大小展示 · 实际占用将在完成后去重";
            listCaption.Text = preview.Truncated ? $"已发现 {preview.DiscoveredChildren:N0} 项 · 预览前 {LiveScanSession.PreviewLimit:N0} 项，完成后可查看全部" : $"已发现 {preview.DiscoveredChildren:N0} 项 · 非最终统计";
            grid.Columns["count"]!.Visible = grid.Columns["location"]!.Visible = false;
            map.LoadingView = map.Scanning = false; map.ShowNested = false; map.TotalSize = current.Size;
            map.EmptyTitle = view.Rows.Count == 0 ? "这个目录还在读取中" : "已发现项目的大小暂为 0 B";
            map.EmptyCaption = "预览会自动更新；扫描完成后恢复搜索、大文件与文件类型视图";
            map.SetItems(view.MapItems); PopulateGrid(view.Rows);
            var selected = selectedName == null ? null : view.Rows.FirstOrDefault(i => i.Name == selectedName);
            if (selected != null) SelectItem(selected, true);
            if (firstVisibleRow >= 0 && grid.RowCount > 0) grid.FirstDisplayedScrollingRowIndex = Math.Min(firstVisibleRow, grid.RowCount - 1);
            previewDisplayedPath = current.Path;
            issues.Visible = false;
            map.Enabled = grid.Enabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing && ReferenceEquals(liveSession, session))
                status.Text = "实时预览暂未更新，扫描仍在继续：" + ex.Message;
        }
        finally { if (ReferenceEquals(viewCancellation, cancellation)) viewCancellation = null; }
    }

    private void ResetCards()
    {
        foreach (var card in new[] { totalCard, filesCard, foldersCard, timeCard }) { card.Value = "—"; card.Invalidate(); }
    }

    private void SetMode(int index)
    {
        if (showingPreview) return;
        typeFilter = null;
        mode = index; navigation.SelectedIndex = index; navigation.Invalidate();
        ResetSearch();
        sortColumn = "size"; sortAscending = false;
        UpdateView();
    }

    private void OpenDirectory(DiskEntry directory)
    {
        selection = null;
        current = directory; ResetSearch(); mode = 0; typeFilter = null;
        if (sortColumn is "count" or "location") { sortColumn = "size"; sortAscending = false; }
        navigation.SelectedIndex = 0; navigation.Invalidate(); UpdateView();
    }

    private void ResetSearch()
    {
        searchTimer.Stop(); clearingSearch = true;
        try { search.Clear(); } finally { clearingSearch = false; }
    }

    private void UpdateView(bool preserveSelection = false) => _ = UpdateViewAsync(preserveSelection);

    private async Task UpdateViewAsync(bool preserveSelection)
    {
        if (liveSession != null) { await UpdatePreviewAsync(); return; }
        if (current == null || scan == null) return;
        viewCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        viewCancellation = cancellation;
        var token = cancellation.Token;
        var directory = current;
        int requestedMode = mode;
        var previous = preserveSelection ? selection : null;
        var sort = sortColumn switch { "name" => ViewSort.Name, "count" => ViewSort.Count, "location" => ViewSort.Location, _ => ViewSort.Size };
        var request = new ViewRequest(directory, (ViewMode)requestedMode, search.Text, sort, sortAscending, typeFilter, metric);
        pathBox.Text = current.Path;
        title.Text = mode switch { 1 => "找到空间里的大块头", 2 => "按文件类型，理解空间", _ => "文件夹空间，一目了然" };
        subtitle.Text = $"{current.Name}  /  " + (typeFilter != null ? $"仅显示 {typeFilter} 文件 · " : "") + (current.Incomplete ? "部分内容未读取，统计不完整。" : "浏览文件分布，双击文件夹继续深入。");
        clearType.Visible = typeFilter != null;
        if (filterColumn != null) filterColumn.Width = typeFilter != null ? 142 * DeviceDpi / 96f : 0;
        totalCard.Value = SizeFormat.Bytes(current.Bytes(metric)) + (metric == SizeMetric.Allocated && current.UnknownAllocationFiles > 0 ? " + 未知" : "");
        totalCard.Title = metric == SizeMetric.Allocated ? "实际占用空间" : "当前目录大小";
        totalCard.Caption = metric == SizeMetric.Allocated ? "文件数据分配 · 硬链接只计一次" : "文件逻辑大小 · 1024 进制";
        filesCard.Value = SizeFormat.Number(current.FileCount);
        foldersCard.Value = SizeFormat.Number(current.FolderCount);
        timeCard.Value = scan.Elapsed.TotalSeconds < .01 ? "< 0.01 s" : scan.Elapsed.TotalSeconds < 1 ? $"{scan.Elapsed.TotalSeconds:0.00} s" : $"{scan.Elapsed.TotalSeconds:0.0} s";
        timeCard.Caption = scan.Skipped > 0 ? $"{scan.Skipped:N0} 项已跳过 · 见底部详情" : "扫描完成 · 可随时重新扫描";
        foreach (var card in new[] { totalCard, filesCard, foldersCard, timeCard }) card.Invalidate();
        up.Enabled = current.Parent != null; home.Enabled = current != scan.Root;
        updating = true;
        try
        {
            grid.RowCount = 0; sortedRows = []; grid.Enabled = false;
            previousPage.Enabled = nextPage.Enabled = false;
            if (pagingRow != null) pagingRow.Height = 0;
            grid.Columns["count"]!.Visible = requestedMode == 2;
            grid.Columns["location"]!.Visible = requestedMode == 1;
        }
        finally { updating = false; }
        SelectItem(null, false);
        map.Enabled = false; map.LoadingView = true; map.SetItems([]);
        mapCaption.Text = "正在整理视图…";
        try
        {
            var result = await Task.Run(() => ViewQuery.Build(request, token), token);
            if (IsDisposed || Disposing || !ReferenceEquals(viewCancellation, cancellation) || token.IsCancellationRequested) return;
            if (requestedMode == 2)
            {
                mapTitle.Text = "类型分布"; listTitle.Text = "文件类型";
                mapCaption.Text = $"包含子目录 · {result.Rows.Count:N0} 种类型 · 双击查看该类型文件";
            }
            else
            {
                mapTitle.Text = requestedMode == 1 ? "大文件分布" : "空间分布";
                listTitle.Text = requestedMode == 1 ? request.ExtensionFilter != null ? $"{request.ExtensionFilter} 文件" : "最大的 500 个文件" : "文件与文件夹";
                mapCaption.Text = requestedMode == 1 ? $"包含所有子目录 · {result.MatchedCount:N0} 个匹配文件，显示前 {result.Rows.Count:N0} 个" : "当前层级 · 双击文件夹进入 · 小矩形合并展示";
            }
            listCaption.Text = $"{result.Rows.Count:N0} 项 · {(request.Metric == SizeMetric.Allocated ? "实际占用" : "文件大小")} · 占当前目录 {SizeFormat.Percent(result.VisibleBytes, directory.Bytes(request.Metric))}" + (request.Metric == SizeMetric.Allocated && directory.UnknownAllocationFiles > 0 ? " · 含未知" : "");
            map.TotalSize = directory.Bytes(request.Metric); map.Metric = request.Metric; map.ShowNested = requestedMode == 0;
            bool searched = request.Search.Trim().Length > 0;
            map.EmptyTitle = result.Rows.Count == 0 ? (searched ? "没有找到匹配项" : directory.Incomplete ? "当前目录的内容未能读取" : "这里还是空的") : "这些文件的大小为 0 B";
            if (request.Metric == SizeMetric.Allocated && result.Rows.Count > 0) map.EmptyTitle = "这些项目没有单独计入的数据占用";
            if (request.Metric == SizeMetric.Allocated && result.Rows.Any(i => i.UnknownFiles > 0)) map.EmptyTitle = "部分项目的占用空间未知";
            map.EmptyCaption = searched ? "试试其他名称或扩展名，按 Esc 清空搜索" : directory.Incomplete ? "查看底部的跳过项，确认访问权限后重新扫描" : "零字节文件和空文件夹仍会出现在右侧列表中";
            if (request.Metric == SizeMetric.Allocated && directory.UnknownAllocationFiles > 0) map.EmptyCaption = "右侧保留文件名和文件大小；底部可查看占用未知的原因";
            map.SetItems(result.MapItems);
            PopulateGrid(result.Rows);
            if (previous != null && result.Rows.Contains(previous)) SelectItem(previous, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing && ReferenceEquals(viewCancellation, cancellation))
            {
                status.Text = "整理视图失败：" + ex.Message;
                map.EmptyTitle = "无法整理当前视图"; map.EmptyCaption = "请重新扫描后再试";
            }
        }
        finally
        {
            if (ReferenceEquals(viewCancellation, cancellation))
            {
                viewCancellation = null;
                if (!IsDisposed && !Disposing) { map.LoadingView = false; map.Enabled = true; grid.Enabled = true; map.Invalidate(); }
            }
        }
    }

    private void PopulateGrid(List<MapItem> rows)
    {
        updating = true;
        grid.SuspendLayout();
        try
        {
            // Virtual mode avoids creating one WinForms row for every entry in huge folders.
            sortedRows = rows;
            grid.VirtualMode = true;
            PopulatePage(0);
            foreach (DataGridViewColumn col in grid.Columns)
                col.HeaderText = (col.Name switch { "name" => mode == 2 ? "类型" : "名称", "size" => metric == SizeMetric.Allocated ? "占用" : "大小", "count" => "文件数", "location" => "所在位置", _ => "占比" }) + (col.Name == sortColumn ? sortAscending ? " ↑" : " ↓" : "");
        }
        finally { grid.ResumeLayout(); updating = false; }
        SelectItem(null, false);
    }

    private void PopulatePage(int requestedPage)
    {
        var page = PageSlice.Create(sortedRows.Count, requestedPage);
        pageIndex = page.Index;
        bool wasUpdating = updating;
        updating = true;
        grid.SuspendLayout();
        try
        {
            grid.RowCount = 0;
            grid.RowCount = page.Length;
            grid.ClearSelection();
            previousPage.Enabled = page.Index > 0;
            nextPage.Enabled = page.Index + 1 < page.Pages;
            pageInfo.Text = $"第 {page.Index + 1:N0} / {page.Pages:N0} 页";
            if (pagingRow != null) pagingRow.Height = page.Pages > 1 ? 42 * DeviceDpi / 96f : 0;
        }
        finally { grid.ResumeLayout(); updating = wasUpdating; }
    }

    private List<MapItem> sortedRows = [];
    private MapItem? RowItem(int index)
    {
        int globalIndex = pageIndex * PageSlice.Size + index;
        return index >= 0 && globalIndex < sortedRows.Count ? sortedRows[globalIndex] : null;
    }

    protected override void OnLoad(EventArgs e)
    {
        PrepareInitialLayout();
        base.OnLoad(e);
        grid.CellValueNeeded += (_, args) =>
        {
            if (RowItem(args.RowIndex) is not { } item) return;
            args.Value = grid.Columns[args.ColumnIndex].Name switch
            {
                "name" => item.Name, "size" => item.UnknownFiles > 0 ? item.Size == 0 ? "未知" : SizeFormat.Bytes(item.Size) + " + 未知" : metric == SizeMetric.Allocated && item.Entry?.SharedAllocation == true ? "共享" : SizeFormat.Bytes(item.Size),
                "count" => SizeFormat.Number(item.Count),
                "location" => item.Entry?.Parent is { } parent && current is { } scope ? parent == scope ? "当前目录" : Path.GetRelativePath(scope.Path, parent.Path) : "",
                _ => SizeFormat.Percent(item.Size, current?.Bytes(metric) ?? 0)
            };
        };
        grid.CellToolTipTextNeeded += (_, args) =>
        {
            if (RowItem(args.RowIndex) is not { } item) return;
            args.ToolTipText = item.Entry?.Path ?? $"{item.Name} · {item.Count:N0} 个文件 · {SizeFormat.Bytes(item.Size)}";
        };
    }

    private void SelectItem(MapItem? item, bool syncGrid)
    {
        selection = item; map.SelectItem(item); reveal.Enabled = item?.Entry != null;
        if (syncGrid)
        {
            updating = true; grid.ClearSelection();
            int index = item == null ? -1 : sortedRows.IndexOf(item);
            if (index >= 0)
            {
                if (index / PageSlice.Size != pageIndex) PopulatePage(index / PageSlice.Size);
                int row = index % PageSlice.Size;
                grid.Rows[row].Selected = true; grid.FirstDisplayedScrollingRowIndex = row;
            }
            updating = false;
        }
        if (item == null)
        {
            detailTitle.Text = "选择矩形或列表项目，查看详情";
            detailPath.Text = "双击文件夹进入 · 双击文件在资源管理器中定位";
            return;
        }
        detailTitle.Text = $"{item.Name} · {(item.UnknownFiles > 0 ? item.Size == 0 ? "占用未知" : SizeFormat.Bytes(item.Size) + " + 未知" : SizeFormat.Bytes(item.Size))} · {SizeFormat.Percent(item.Size, current?.Bytes(metric) ?? 0)}";
        detailPath.Text = item.Entry is { } entry
            ? $"{entry.Path}  ·  修改于 {entry.Modified:yyyy-MM-dd HH:mm}" + (entry.Incomplete ? " · 部分内容未读取" : "")
            : !item.IsAggregate && mode == 2 ? $"{item.Category}  ·  {item.Count:N0} 个文件（含所有子目录）" : $"合并展示的 {item.Count:N0} 项，可在右侧列表查看单个项目";
        if (item.Entry is { } metadata)
        {
            string allocation = metadata.UnknownAllocationFiles > 0 ? "占用未知" : SizeFormat.Bytes(metadata.IsDirectory ? metadata.AllocatedSize : metadata.Storage?.AllocatedBytes ?? 0);
            detailTitle.Text = $"{item.Name} · 文件大小 {SizeFormat.Bytes(metadata.Size)} · 实际占用 {allocation}" +
                (metadata.Storage is { LinkCount: > 1 } storage ? $" · {storage.LinkCount} 个硬链接{(metadata.SharedAllocation ? "（本路径共享，不重复计入）" : "")}" : "");
        }
    }

    private void ActivateItem(MapItem item)
    {
        if (mode == 2 && item.Entry == null && !item.IsAggregate)
        {
            typeFilter = item.Name; mode = 1; navigation.SelectedIndex = 1;
            ResetSearch(); sortColumn = "size"; sortAscending = false; UpdateView();
        }
        else if (item.Entry is { IsDirectory: true } directory) OpenDirectory(directory);
        else if (item.Entry != null) { selection = item; RevealSelection(); }
    }

    private void RevealSelection()
    {
        if (selection?.Entry is not { } entry) return;
        TryAction(() =>
        {
            if (!File.Exists(entry.Path) && !Directory.Exists(entry.Path)) throw new IOException("该项目已移动或删除，请重新扫描。" );
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.Path}\"") { UseShellExecute = true });
        });
    }

    private void TryAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        { MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }

    private void ShowRefreshDetails()
    {
        if (scan?.Statistics is not { } stats) return;
        using var dialog = new Form { Text = "最近完成的扫描 · 刷新详情", Size = new Size(780, 520), StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.Surface, ForeColor = Theme.Text, MinimizeBox = false, Font = Theme.Font(9), Padding = new Padding(18) };
        var text = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.Font(10) };
        text.Text = (RefreshPresentation.Details(stats) + "\n\n" + ScanPolicy.Explain(stats.Mode)).Replace("\n", "\r\n");
        dialog.Controls.Add(text); dialog.ShowDialog(this);
    }

    private void ShowIssues()
    {
        if (scan == null) return;
        using var dialog = new Form { Text = $"未读取项 · 跳过 {scan.Skipped:N0}，占用未知 {scan.Root.UnknownAllocationFiles:N0}", Size = new Size(850, 520), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Surface, ForeColor = Theme.Text, MinimizeBox = false, Font = Theme.Font(9) };
        var text = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.Font(9) };
        text.Text = "跳过项未纳入文件大小统计；占用未知的文件仍计入文件大小，但不假定其占用为 0。\r\n两类各最多保留 2,000 条详情。\r\n\r\n" + string.Join("\r\n\r\n", scan.Issues.Concat(scan.AllocationIssues).Select(i => $"{i.Path}\r\n{i.Reason}"));
        dialog.Padding = new Padding(18); dialog.Controls.Add(text); dialog.ShowDialog(this);
    }

    private async Task RestoreSavedBackground()
    {
        try
        {
            var saved = await Task.Run(() => BackgroundSettings.Load(backgroundSettingsPath));
            if (saved == null) return;
            if (IsDisposed || Disposing) saved.Dispose();
            else ApplyBackground(saved.Image, saved.View, saved.AutomaticAccent);
        }
        catch (Exception error) when (BackgroundSettings.IsFileError(error))
        {
            if (!IsDisposed && !Disposing && scanCancellation == null)
                status.Text = "自定义背景无法读取，已使用默认背景。";
        }
        finally { if (!IsDisposed && !Disposing) backgroundSettings.Enabled = true; }
    }

    private void EditBackground()
    {
        if (BackgroundImage is not { } current) return;
        using var dialog = new BackgroundDialog(current, BackgroundCrop, automaticBackgroundAccent, ClientSize,
            backgroundSettingsPath, isDefault: !HasCustomBackground);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (dialog.RestoredDefault) ApplyBackground(Wallpaper.Load(), BackgroundView.Default, Theme.DefaultAccent, useDefault: true);
        else if (dialog.Result is { } result) ApplyBackground(result.Image, result.View, result.AutomaticAccent);
    }

    private void ApplyBackground(Bitmap image, BackgroundView view, Color automatic, bool useDefault = false)
    {
        var previous = BackgroundImage;
        BackgroundCrop = view;
        HasCustomBackground = !useDefault;
        automaticBackgroundAccent = automatic;
        Theme.SetAccent(useDefault ? null : view.AccentArgb is int argb ? Color.FromArgb(argb) : automatic);
        WallpaperRaster.Dispose();
        BackgroundImage = image;
        grid.DefaultCellStyle.SelectionBackColor = Theme.Selection;
        grid.GridColor = useDefault ? Color.FromArgb(239, 224, 217) : Theme.Mix(Theme.Border, Theme.Surface, .55f);
        previous?.Dispose();
        UpdateTextSurfaces(this);
        Invalidate(true);

        void UpdateTextSurfaces(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is Label or CheckBox)
                    child.BackColor = HasCustomBackground ? Theme.Surface : Color.Transparent;
                UpdateTextSurfaces(child);
            }
        }
    }

    private async void HandleKeys(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.O) { e.Handled = e.SuppressKeyPress = true; await ChooseFolder(); }
        else if (e.KeyCode == Keys.F5) { e.Handled = true; if (e.Control) await ForceRefresh(); else if (scan != null) await ScanFolder(scan.Root.Path); }
        else if (e.Alt && e.KeyCode == Keys.Up) { e.Handled = true; if (current?.Parent != null) OpenDirectory(current.Parent); }
        else if (e.KeyCode == Keys.Escape) { search.Text = ""; }
        else if (e.Control && e.KeyCode == Keys.F) { e.Handled = e.SuppressKeyPress = true; search.Focus(); search.SelectAll(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            searchTimer.Dispose(); previewTimer.Dispose(); scanTip.Dispose(); scanCancellation?.Cancel(); viewCancellation?.Cancel();
            WallpaperRaster.Dispose();
            var image = BackgroundImage; BackgroundImage = null; image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
