namespace DiskAtlas;

internal sealed partial class BackgroundDialog
{
    private DisplayPreferences? initialDisplay;
    private string? displayPath;
    private Action<DisplayPreferences>? previewDisplay;
    private bool imageChanged;
    private Panel? displayPreview;
    private TabControl? appearanceTabs;
    private readonly TrackBar strength = new() { Minimum = 40, Maximum = 100, Value = 100, TickFrequency = 10, Dock = DockStyle.Fill, AccessibleName = "背景显示强度" };
    private readonly ComboBox palette = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "图块配色" };
    private readonly ComboBox density = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "阅读密度" };
    private readonly Label strengthValue = new() { AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
    private readonly System.Windows.Forms.Timer displayTimer = new() { Interval = 120 };
    internal DisplayPreferences? DisplayResult { get; private set; }
    internal DisplayPreferences DisplayDraft => new(strength.Value, (CategoryPalette)Math.Max(0, palette.SelectedIndex), (ReadingDensity)Math.Max(0, density.SelectedIndex));

    private void ConfigureDisplay(DisplayPreferences value, string path, Action<DisplayPreferences>? previewCallback, Button pick, Button restore)
    {
        if (!value.IsValid || string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Invalid display settings.");
        initialDisplay = value; displayPath = path; previewDisplay = previewCallback;
        Font = Theme.Font(Theme.BodyFontSize);
        var tabs = new TabControl { Dock = DockStyle.Fill, AccessibleName = "背景设置页面" };
        appearanceTabs = tabs;
        var imagePage = new TabPage("图片") { BackColor = Theme.Surface };
        var displayPage = new TabPage("显示") { BackColor = Theme.Surface, Padding = new(14) };
        Controls.Remove(preview); Controls.Remove(adjustments);
        imagePage.Controls.Add(preview); imagePage.Controls.Add(adjustments);
        tabs.TabPages.AddRange([imagePage, displayPage]); Controls.Add(tabs); tabs.BringToFront();
        var settings = new TableLayoutPanel { Dock = DockStyle.Top, Height = 195, ColumnCount = 2, RowCount = 4 };
        settings.ColumnStyles.Add(new(SizeType.Percent, 35)); settings.ColumnStyles.Add(new(SizeType.Percent, 65));
        foreach (int h in new[] { 44, 48, 46, 46 }) settings.RowStyles.Add(new(SizeType.Absolute, h));
        settings.Controls.Add(new Label { Text = "背景显示强度", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        settings.Controls.Add(strengthValue, 1, 0); settings.Controls.Add(strength, 0, 1); settings.SetColumnSpan(strength, 2);
        settings.Controls.Add(new Label { Text = "图块配色", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2); settings.Controls.Add(palette, 1, 2);
        settings.Controls.Add(new Label { Text = "阅读密度", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3); settings.Controls.Add(density, 1, 3);
        palette.Items.AddRange(["原有类别色", "类别分明"]); density.Items.AddRange(["舒适（当前）", "紧凑"]);
        displayPreview = new PreviewSurface { Dock = DockStyle.Fill, BackColor = Theme.Background };
        displayPreview.Paint += PaintPreview;
        displayPage.Controls.Add(displayPreview); displayPage.Controls.Add(settings);
        var resetDisplay = new Button { Text = "恢复显示默认", AutoSize = true, Visible = false };
        commands.Controls.Add(resetDisplay); commands.Controls.SetChildIndex(resetDisplay, 0);
        resetDisplay.Click += (_, _) => SetDisplayDraft(DisplayPreferences.Default);
        tabs.SelectedIndexChanged += (_, _) =>
        {
            bool image = tabs.SelectedTab == imagePage;
            pick.Visible = restore.Visible = image; resetDisplay.Visible = !image;
        };
        displayTimer.Tick += (_, _) => { displayTimer.Stop(); if (!busy && !IsDisposed && !Disposing) previewDisplay?.Invoke(DisplayDraft); };
        void ChangedDisplay()
        {
            strengthValue.Text = strength.Value == 100 ? "当前 100%" : strength.Value + "%";
            displayPreview.Invalidate(); displayTimer.Stop(); displayTimer.Start();
        }
        strength.ValueChanged += (_, _) => ChangedDisplay(); palette.SelectedIndexChanged += (_, _) => ChangedDisplay(); density.SelectedIndexChanged += (_, _) => ChangedDisplay();
        SetDisplayDraft(value); displayTimer.Stop(); imageChanged = false;
        status.Text = "";
    }

    internal void SetDisplayDraft(DisplayPreferences value)
    {
        if (!value.IsValid) throw new InvalidDataException();
        strength.Value = value.BackgroundStrength; palette.SelectedIndex = (int)value.Palette; density.SelectedIndex = (int)value.Density;
        strengthValue.Text = value.BackgroundStrength == 100 ? "当前 100%" : value.BackgroundStrength + "%";
    }

    private async Task CommitAppearanceAsync()
    {
        if (choice == null || initialDisplay == null) return;
        displayTimer.Stop();
        var preferences = DisplayDraft;
        var selected = choice with { View = ReadView() };
        using var snapshot = imageChanged && !resetPending ? selected with { Image = new Bitmap(selected.Image) } : null;
        bool reset = imageChanged && resetPending;
        await Task.Run(() => AppearanceSettingsCommit.Save(settingsPath, displayPath!, snapshot, reset,
            preferences == initialDisplay ? null : preferences));
        if (IsDisposed || Disposing) return;
        DisplayResult = preferences;
        if (imageChanged)
        {
            if (reset) RestoredDefault = true;
            else { Result = selected; choice = null; }
        }
        DialogResult = DialogResult.OK;
    }
}
