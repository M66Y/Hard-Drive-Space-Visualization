namespace DiskAtlas;

// Keep one existing label surface. A long name may elide, but cannot consume
// the space reserved for both size metrics. No native ellipsis tooltip.
internal sealed class SelectionSummaryLabel : Label
{
    private string displayName = "未选择项目", metadata = "";
    private Font? metadataFont;
    private int desiredMetadataWidth;
    internal Rectangle NameBounds { get; private set; }
    internal Rectangle MetadataBounds { get; private set; }

    internal SelectionSummaryLabel()
    {
        AutoSize = AutoEllipsis = false; Dock = DockStyle.Fill;
        BackColor = Color.Transparent; ForeColor = Theme.Text;
        TextAlign = ContentAlignment.MiddleLeft; Text = displayName;
    }

    internal void SetDetails(SelectionDetails? details, Font captionFont)
    {
        metadataFont = captionFont;
        displayName = details?.Name ?? "未选择项目";
        metadata = details == null ? "" : details.Kind is "文件" or "文件夹"
            ? $"文件大小 {details.LogicalSize} · 实际占用 {details.Allocation}"
            : details.Kind;
        Text = details?.Summary ?? displayName;
        MeasureMetadata(); Invalidate();
    }
    private void MeasureMetadata()
    {
        desiredMetadataWidth = metadata.Length == 0 ? 0 : TextRenderer.MeasureText(metadata, metadataFont ?? Font,
            Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        UpdateBands();
    }
    private void UpdateBands()
    {
        int gap = metadata.Length > 0 ? (int)Math.Round(10 * DeviceDpi / 96f) : 0;
        int width = Math.Min(desiredMetadataWidth, Math.Max(0, ClientSize.Width * 3 / 4 - gap));
        MetadataBounds = new(Math.Max(0, ClientSize.Width - width), 0, width, ClientSize.Height);
        NameBounds = new(0, 0, Math.Max(0, MetadataBounds.Left - gap), ClientSize.Height);
    }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateBands(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); MeasureMetadata(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); MeasureMetadata(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping;
        var state = e.Graphics.Save();
        try
        {
            e.Graphics.SetClip(ClientRectangle, System.Drawing.Drawing2D.CombineMode.Intersect);
            Color text = SystemInformation.HighContrast ? SystemColors.ControlText : Theme.Text;
            if (NameBounds.Width > 0) TextRenderer.DrawText(e.Graphics, displayName, Font, NameBounds, text, flags);
            if (MetadataBounds.Width > 0) TextRenderer.DrawText(e.Graphics, metadata, metadataFont ?? Font, MetadataBounds,
                SystemInformation.HighContrast ? text : Theme.Muted, flags | TextFormatFlags.Right);
        }
        finally { e.Graphics.Restore(state); }
    }
}
