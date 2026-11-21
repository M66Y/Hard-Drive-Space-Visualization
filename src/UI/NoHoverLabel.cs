namespace DiskAtlas;

// Label.AutoEllipsis creates its own native tooltip. Draw visual truncation
// without that provider; Text stays complete for accessible readers.
internal sealed class NoHoverLabel : Label
{
    [System.ComponentModel.DefaultValue(false)]
    internal bool WrapText { get; set; }
    public NoHoverLabel() { AutoEllipsis = false; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var bounds = new Rectangle(Padding.Left, Padding.Top,
            Math.Max(0, ClientSize.Width - Padding.Horizontal), Math.Max(0, ClientSize.Height - Padding.Vertical));
        if (bounds.Width == 0 || bounds.Height == 0) return;
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping;
        flags |= WrapText ? TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl
            : TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        flags |= TextAlign switch
        {
            ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter,
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.Left
        };
        flags |= TextAlign switch
        {
            ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight => TextFormatFlags.VerticalCenter,
            ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight => TextFormatFlags.Bottom,
            _ => TextFormatFlags.Top
        };
        if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds,
            SystemInformation.HighContrast ? Enabled ? SystemColors.ControlText : SystemColors.GrayText
                : Enabled ? ForeColor : SystemColors.GrayText, flags);
    }
}
