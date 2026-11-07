namespace DiskAtlas;

internal sealed class BackgroundDialog : Form
{
    private readonly Panel preview = new PreviewSurface { Dock = DockStyle.Fill, BackColor = Theme.Surface, Cursor = Cursors.Hand };
    private readonly TrackBar zoom = new() { Minimum = 100, Maximum = 300, Value = 100, TickFrequency = 50, Width = 160, Height = 38, AccessibleName = "背景缩放" };
    private readonly NumericUpDown focusX = new() { Maximum = 100, Value = 50, Width = 58, AccessibleName = "横向焦点" };
    private readonly NumericUpDown focusY = new() { Maximum = 100, Value = 50, Width = 58, AccessibleName = "纵向焦点" };
    private readonly CheckBox automatic = new() { Text = "自动取色", Checked = true, AutoSize = true };
    private readonly Button colorButton = new() { Text = "选强调色", AutoSize = true };
    private readonly Button apply = new() { Text = "应用", AutoSize = true };
    private readonly Button cancel = new() { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly FlowLayoutPanel adjustments = new() { AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(8) };
    private readonly FlowLayoutPanel commands = new() { AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(8) };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 44, Text = "选择 PNG、JPEG 或 BMP。拖动画面或输入焦点百分比。", AutoEllipsis = false };
    private readonly Size referenceSize;
    private readonly string settingsPath;
    private BackgroundChoice? choice;
    private Color manual = Theme.DefaultAccent;
    private bool busy;
    private bool committing, resetPending;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<string, CancellationToken, BackgroundChoice> importImage;
    private Point dragStart;
    private decimal dragX, dragY;
    public bool RestoredDefault { get; private set; }
    public BackgroundChoice? Result { get; private set; }
    internal bool IsBusy => busy;

    public BackgroundDialog(Image current, BackgroundView view, Color automaticAccent, Size referenceSize, string settingsPath,
        bool isDefault = false, Func<string, CancellationToken, BackgroundChoice>? importImage = null)
    {
        this.importImage = importImage ?? BackgroundSettings.Import;
        this.referenceSize = referenceSize;
        this.settingsPath = settingsPath;
        choice = new BackgroundChoice(new Bitmap(current), view, automaticAccent);
        Text = "背景设置";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(800, 560);
        MinimumSize = new Size(570, 410);
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        var pick = new Button { Text = "选择图片…", AutoSize = true };
        var restore = new Button { Text = "恢复默认", AutoSize = true };
        static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(4, 7, 4, 0) };
        adjustments.Controls.AddRange([Caption("缩放"), zoom, Caption("横向焦点 %"), focusX,
            Caption("纵向焦点 %"), focusY, automatic, colorButton]);
        commands.Controls.AddRange([pick, restore, apply, cancel]);
        Controls.Add(preview);
        Controls.Add(adjustments);
        Controls.Add(status);
        Controls.Add(commands);
        CancelButton = cancel;
        AcceptButton = apply;
        preview.Paint += PaintPreview;
        preview.Resize += (_, _) => preview.Invalidate();
        preview.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            dragStart = e.Location;
            dragX = focusX.Value;
            dragY = focusY.Value;
            preview.Capture = true;
        };
        preview.MouseMove += (_, e) =>
        {
            if (!preview.Capture || e.Button != MouseButtons.Left || choice == null) return;
            var box = PreviewBounds();
            var bounds = Wallpaper.CoverBounds(choice.Image, box.Size, Point.Empty, ReadView());
            focusX.Value = Math.Clamp(dragX - (decimal)((e.X - dragStart.X) * 100f / bounds.Width), 0, 100);
            focusY.Value = Math.Clamp(dragY - (decimal)((e.Y - dragStart.Y) * 100f / bounds.Height), 0, 100);
        };
        preview.MouseUp += (_, _) => preview.Capture = false;
        zoom.ValueChanged += (_, _) => Changed();
        focusX.ValueChanged += (_, _) => Changed();
        focusY.ValueChanged += (_, _) => Changed();
        automatic.CheckedChanged += (_, _) => { Changed(); UpdateColor(); };
        colorButton.Click += (_, _) =>
        {
            using var picker = new ColorDialog { Color = manual, FullOpen = true };
            if (picker.ShowDialog(this) == DialogResult.OK) { manual = picker.Color; Changed(); UpdateColor(); }
        };
        pick.Click += async (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            await ImportImageAsync(picker.FileName);
        };
        apply.Click += async (_, _) => await ApplyAsync();
        restore.Click += async (_, _) => await RestoreDefaultAsync();
        FormClosing += (_, e) =>
        {
            if (committing && DialogResult != DialogResult.OK && e.CloseReason == CloseReason.UserClosing)
            { e.Cancel = true; status.Text = "正在保存，请稍候。完成后窗口会自动关闭。"; }
            else lifetime.Cancel();
        };
        SetView(view);
        resetPending = isDefault;
    }

    internal Task ImportImageAsync(string path) => RunFileWork("读取图片", false, async () =>
    {
        var token = lifetime.Token;
        var loaded = await Task.Run(() => importImage(path, token), token);
        if (token.IsCancellationRequested || IsDisposed || Disposing) { loaded.Dispose(); return; }
        choice?.Dispose(); choice = loaded;
        SetView(loaded.View); resetPending = false;
        status.Text = "图片已载入。调整后点击应用；取消会保留原设置。";
    });

    internal Task RestoreDefaultAsync() => RunFileWork("读取默认背景", false, async () =>
    {
        var token = lifetime.Token;
        var image = await Task.Run(Wallpaper.Load, token);
        if (token.IsCancellationRequested || IsDisposed || Disposing) { image.Dispose(); return; }
        choice?.Dispose(); choice = new(image, BackgroundView.Default, Theme.DefaultAccent);
        SetView(BackgroundView.Default); resetPending = true;
        status.Text = "已预览默认背景。点击应用后生效，取消会保留原设置。";
        preview.Invalidate();
    });

    internal Task ApplyAsync() => RunFileWork(resetPending ? "恢复默认设置" : "保存背景", true, async () =>
    {
        if (choice == null) return;
        if (resetPending)
        {
            await Task.Run(() => BackgroundSettings.Reset(settingsPath));
            if (IsDisposed || Disposing) return;
            RestoredDefault = true;
        }
        else
        {
            var selected = choice with { View = ReadView() };
            // GDI+ images cannot be painted and encoded concurrently.
            using var snapshot = selected with { Image = new Bitmap(selected.Image) };
            await Task.Run(() => BackgroundSettings.Save(settingsPath, snapshot));
            if (IsDisposed || Disposing) return;
            Result = selected; choice = null;
        }
        DialogResult = DialogResult.OK;
    });

    private void Changed() { resetPending = false; preview.Invalidate(); }

    private async Task RunFileWork(string operation, bool commit, Func<Task> work)
    {
        if (busy || IsDisposed || Disposing || lifetime.IsCancellationRequested) return;
        busy = true;
        committing = commit;
        foreach (Control control in commands.Controls) control.Enabled = !commit && control == cancel;
        adjustments.Enabled = preview.Enabled = false;
        status.Text = $"正在{operation}…" + (commit ? "请稍候。" : "可以取消。若读取较慢，可关闭窗口。");
        try { await work(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (BackgroundSettings.IsFileError(error))
        {
            if (!IsDisposed && !Disposing)
                status.Text = commit ? $"{operation}失败，原设置保留。请检查本地存储是否可写，或稍后重试。"
                    : "读取失败。请选择可读取且不超过 24 MiB、4000 万像素的 PNG、JPEG 或 BMP。";
        }
        finally
        {
            busy = committing = false;
            if (!IsDisposed && !Disposing)
            {
                foreach (Control control in commands.Controls) control.Enabled = true;
                adjustments.Enabled = preview.Enabled = true;
            }
        }
    }

    private BackgroundView ReadView() => new((float)focusX.Value / 100, (float)focusY.Value / 100,
        zoom.Value / 100f, automatic.Checked ? null : manual.ToArgb());

    private void SetView(BackgroundView view)
    {
        zoom.Value = (int)Math.Round(view.Zoom * 100);
        focusX.Value = (decimal)view.X * 100;
        focusY.Value = (decimal)view.Y * 100;
        manual = view.AccentArgb is int argb ? Color.FromArgb(argb) : choice!.AutomaticAccent;
        automatic.Checked = !view.AccentArgb.HasValue;
        UpdateColor();
    }

    private void UpdateColor()
    {
        colorButton.Enabled = !automatic.Checked;
        colorButton.BackColor = Theme.ReadableAccent(automatic.Checked ? choice?.AutomaticAccent ?? manual : manual);
        colorButton.ForeColor = Color.White;
        preview.Invalidate();
    }

    private Rectangle PreviewBounds()
    {
        float factor = Math.Min((float)preview.Width / Math.Max(1, referenceSize.Width),
            (float)preview.Height / Math.Max(1, referenceSize.Height));
        var size = new Size(Math.Max(1, (int)(referenceSize.Width * factor)), Math.Max(1, (int)(referenceSize.Height * factor)));
        return new Rectangle((preview.Width - size.Width) / 2, (preview.Height - size.Height) / 2, size.Width, size.Height);
    }

    private void PaintPreview(object? sender, PaintEventArgs e)
    {
        if (choice == null) return;
        var box = PreviewBounds();
        var state = e.Graphics.Save();
        e.Graphics.SetClip(box);
        e.Graphics.DrawImage(choice.Image, Wallpaper.CoverBounds(choice.Image, box.Size, new Point(-box.X, -box.Y), ReadView()));
        using var wash = new SolidBrush(Color.FromArgb(42, Theme.Surface));
        e.Graphics.FillRectangle(wash, box);
        var accent = Theme.ReadableAccent(automatic.Checked ? choice.AutomaticAccent : manual);
        float scale = DeviceDpi / 96f;
        var panel = new Rectangle(box.Left + (int)(14 * scale), box.Top + (int)(14 * scale),
            Math.Min(box.Width - (int)(28 * scale), (int)(280 * scale)), (int)(142 * scale));
        if (panel.Width > 100 * scale && panel.Bottom < box.Bottom)
        {
            using var surface = new SolidBrush(Theme.Surface);
            e.Graphics.FillRectangle(surface, panel);
            using var font = Theme.Font(9);
            Theme.TextAt(e.Graphics, "界面效果预览", font, Theme.Text, new Rectangle(panel.X + 12, panel.Y + 6, panel.Width - 24, (int)(28 * scale)));
            Theme.TextAt(e.Graphics, "文件大小  ·  12.5 GB", font, Theme.Text, new Rectangle(panel.X + 12, panel.Y + (int)(36 * scale), panel.Width - 24, (int)(26 * scale)));
            var button = new Rectangle(panel.X + 12, panel.Y + (int)(70 * scale), panel.Width - 24, (int)(30 * scale));
            using var swatch = new SolidBrush(accent);
            e.Graphics.FillRectangle(swatch, button);
            Theme.TextAt(e.Graphics, "选择文件夹", font, Color.White, button, TextFormatFlags.HorizontalCenter);
            var colors = Theme.ProgressColors(resetPending ? null : accent);
            var track = new Rectangle(panel.X + 12, panel.Y + (int)(116 * scale), panel.Width - 24, (int)(6 * scale));
            using var line = new Pen(Theme.Mix(accent, Theme.Surface, .68f));
            e.Graphics.DrawRectangle(line, track);
            using var gradient = new System.Drawing.Drawing2D.LinearGradientBrush(track, colors.Start, colors.End, 0f);
            e.Graphics.FillRectangle(gradient, track.X, track.Y, track.Width * .65f, track.Height);
        }
        e.Graphics.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed) { lifetime.Cancel(); lifetime.Dispose(); choice?.Dispose(); choice = null; }
        base.Dispose(disposing);
    }

    private sealed class PreviewSurface : Panel
    {
        public PreviewSurface() => DoubleBuffered = true;
    }
}
