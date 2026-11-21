namespace DiskAtlas;

// A modeless view of the canonical panel. It owns no provider, worker or clock.
internal sealed class DiskActivityDetailsDialog : Form
{
    private readonly DiskActivityPanel source;
    private readonly DiskActivityPanel view;
    private bool applying;
    private bool initialBoundsPrepared;
    internal ComboBox TargetSelector => view.TargetSelector;
    internal DiskActivityPanel ViewPanel => view;

    internal DiskActivityDetailsDialog(DiskActivityPanel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsDisposed) throw new ObjectDisposedException(nameof(source));
        this.source = source;
        view = new DiskActivityPanel(viewOnly: true) { Dock = DockStyle.Fill };
        Text = "AsukaLens · 磁盘活动详情";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(860, 250);
        MinimumSize = new Size(640, 220);
        if (source.FindForm() is { IsDisposed: false } owner)
        {
            Owner = owner;
            CenterOnOwner();
        }
        BackColor = Theme.Surface;
        Controls.Add(view);
        view.TargetChanged += ViewTargetChanged;
        source.StateChanged += SourceStateChanged;
        source.Disposed += SourceDisposed;
        RefreshView();
        ApplyTheme();
    }

    private int S(int logical) => (int)Math.Round(logical * DeviceDpi / 96f);
    protected override void OnLoad(EventArgs e)
    {
        if (!initialBoundsPrepared)
        {
            initialBoundsPrepared = true;
            var area = Screen.FromControl(Owner ?? this).WorkingArea;
            int frameWidth = Width - ClientSize.Width, frameHeight = Height - ClientSize.Height;
            MinimumSize = new Size(Math.Min(S(640), area.Width), Math.Min(S(220), area.Height));
            ClientSize = new Size(Math.Max(1, Math.Min(S(860), area.Width - frameWidth)),
                Math.Max(1, Math.Min(S(250), area.Height - frameHeight)));
            CenterOnOwner();
        }
        base.OnLoad(e);
    }
    protected override void OnShown(EventArgs e)
    {
        CenterOnOwner();
        base.OnShown(e);
    }
    private void CenterOnOwner()
    {
        var owner = Owner ?? source.FindForm();
        var area = Screen.FromControl(owner ?? this).WorkingArea;
        var anchor = owner == null ? area : owner.WindowState == FormWindowState.Minimized ? owner.RestoreBounds : owner.Bounds;
        int left = anchor.Left + (anchor.Width - Width) / 2, top = anchor.Top + (anchor.Height - Height) / 2;
        Location = new Point(Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    internal void ApplyTheme()
    {
        if (IsDisposed || Disposing) return;
        bool contrast = SystemInformation.HighContrast;
        BackColor = contrast ? SystemColors.Window : Theme.Surface;
        ForeColor = contrast ? SystemColors.WindowText : Theme.Text;
        view.BackColor = BackColor;
        view.TargetSelector.BackColor = BackColor;
        view.TargetSelector.ForeColor = ForeColor;
        Invalidate(true);
    }

    private void SourceStateChanged(object? sender, EventArgs e) => RefreshView();
    private void SourceDisposed(object? sender, EventArgs e) { if (!IsDisposed && !Disposing) Close(); }
    private void ViewTargetChanged(object? sender, EventArgs e)
    {
        if (!applying && !source.IsDisposed) source.SelectDisk(view.SelectedDiskNumber);
    }
    private void RefreshView()
    {
        if (IsDisposed || Disposing || source.IsDisposed) return;
        applying = true;
        try { view.ApplyViewSnapshot(source.GetViewSnapshot()); }
        finally { applying = false; }
    }
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessDialogKey(keyData);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            source.StateChanged -= SourceStateChanged;
            source.Disposed -= SourceDisposed;
            view.TargetChanged -= ViewTargetChanged;
        }
        base.Dispose(disposing);
    }
}
