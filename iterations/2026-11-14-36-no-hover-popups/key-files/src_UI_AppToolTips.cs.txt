namespace DiskAtlas;

// Keep local explanation metadata without turning pointer movement into text.
internal static class AppToolTips
{
    internal static ToolTip Create()
    {
        var tip = new ToolTip { Active = false, ShowAlways = false };
        tip.Popup += (_, args) => args.Cancel = true;
        return tip;
    }
}
