namespace DiskAtlas;

internal readonly record struct FileListRowBands(Rectangle Name, Rectangle Path, Rectangle Dot);

// Presentation only: path text does not change MapItem names, sorting or identity.
internal static class FileListPresentation
{
    internal static int RowHeight(int dpi, bool detailed, bool compact = false) =>
        Math.Max(1, (int)Math.Round((detailed ? compact ? 50 : 58 : compact ? 36 : 44) * dpi / 96d));

    internal static FileListRowBands Bands(Rectangle cell, int dpi, bool detailed)
    {
        if (cell.Width <= 0 || cell.Height <= 0) return default;
        double scale = dpi / 96d;
        int Dip(int value) => Math.Max(1, (int)Math.Round(value * scale));
        int left = Math.Min(cell.Right, cell.Left + Dip(28));
        int width = Math.Max(0, cell.Right - left - Dip(10));
        Rectangle name, path = Rectangle.Empty;
        if (detailed)
        {
            int block = Math.Min(cell.Height, Dip(43));
            int top = cell.Top + (cell.Height - block) / 2;
            int nameHeight = Math.Min(block, Dip(22));
            int gap = Math.Min(block - nameHeight, Dip(3));
            int pathHeight = block - nameHeight - gap;
            name = new(left, top, width, nameHeight);
            path = new(left, top + nameHeight + gap, width, pathHeight);
        }
        else name = new(left, cell.Top, width, cell.Height);
        var dot = Rectangle.Intersect(cell, new Rectangle(cell.Left + Dip(12),
            name.Top + name.Height / 2 - Dip(4), Dip(8), Dip(8)));
        return new(name, path, dot);
    }

    internal static string RelativeLocation(MapItem item, DiskEntry? scope)
    {
        if (item.Entry?.Parent is not { } parent || scope == null) return "";
        return StringComparer.OrdinalIgnoreCase.Equals(parent.Path, scope.Path)
            ? "当前目录" : System.IO.Path.GetRelativePath(scope.Path, parent.Path);
    }
}
