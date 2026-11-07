using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace DiskAtlas;

// Each surface samples the same window-sized image, so panel boundaries do not
// restart the wallpaper. Text and data can use a stronger white wash locally.
internal static class Wallpaper
{
    public static Bitmap Load()
    {
        using var stream = typeof(Wallpaper).Assembly.GetManifestResourceStream("DiskAtlas.Background")
            ?? throw new InvalidOperationException("Missing embedded background image.");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    public static void Paint(Control control, Graphics graphics, int wash = 42)
    {
        var form = control.FindForm();
        using var background = new SolidBrush(Theme.Background);
        graphics.FillRectangle(background, control.ClientRectangle);
        if (form?.BackgroundImage is not { } image || form.ClientSize.Width <= 0 || form.ClientSize.Height <= 0) return;
        var offset = Point.Empty;
        for (Control? node = control; node != null && node != form; node = node.Parent)
            offset.Offset(node.Left, node.Top);
        if (form is MainForm main && main.WallpaperRaster.Get(image, form.ClientSize, main.BackgroundCrop) is { } raster)
            graphics.DrawImageUnscaled(raster, -offset.X, -offset.Y);
        else
            graphics.DrawImage(image, CoverBounds(image, form.ClientSize, offset, (form as MainForm)?.BackgroundCrop));
        if (form is MainForm { HasCustomBackground: true } &&
            (control is NavigationPanel or ScanProgressControl || control is TreemapControl && wash == 130))
            wash = Math.Max(wash, 235);
        using var veil = new SolidBrush(Color.FromArgb(wash, Theme.Surface));
        graphics.FillRectangle(veil, control.ClientRectangle);
    }

    internal static RectangleF CoverBounds(Image image, Size size, Point offset, BackgroundView? view = null)
    {
        var crop = view ?? BackgroundView.Default;
        float scale = crop.Zoom * Math.Max((float)size.Width / image.Width, (float)size.Height / image.Height);
        float width = image.Width * scale, height = image.Height * scale;
        float left = Math.Clamp(size.Width / 2f - crop.X * width, Math.Min(0, size.Width - width), 0);
        float top = Math.Clamp(size.Height / 2f - crop.Y * height, Math.Min(0, size.Height - height), 0);
        return new RectangleF(left - offset.X, top - offset.Y, width, height);
    }
}

internal sealed class WallpaperRaster : IDisposable
{
    internal const long MaxPixels = 8_000_000;
    private Image? source;
    private Size size;
    private Bitmap? raster;
    private BackgroundView? view;

    public Bitmap? Get(Image? image, Size clientSize, BackgroundView? crop = null)
    {
        if (ReferenceEquals(image, source) && clientSize == size && view == crop) return raster;
        Dispose();
        source = image;
        size = clientSize;
        view = crop;
        if (image == null || size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > MaxPixels)
            return null;

        Bitmap? candidate = null;
        try
        {
            candidate = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(candidate);
            graphics.Clear(Theme.Background);
            graphics.DrawImage(image, Wallpaper.CoverBounds(image, size, Point.Empty, crop));
            raster = candidate;
            candidate = null;
            return raster;
        }
        catch (Exception ex) when (ex is OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
        finally { candidate?.Dispose(); }
    }

    public void Dispose()
    {
        raster?.Dispose();
        raster = null;
        source = null;
        size = Size.Empty;
    }

}

internal sealed class WallpaperTable : TableLayoutPanel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Wash { get; set; } = 42;
    public WallpaperTable()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e) => Wallpaper.Paint(this, e.Graphics, Wash);
}

internal sealed class WallpaperGrid : DataGridView
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Keep real cells opaque. Only the unused area reveals the background.
        int top = ColumnHeadersVisible ? ColumnHeadersHeight : 0;
        if (RowCount > 0)
        {
            int last = Rows.GetLastRow(DataGridViewElementStates.Displayed);
            if (last >= 0) top = Math.Max(top, GetRowDisplayRectangle(last, false).Bottom);
        }
        int right = ClientSize.Width - (Controls.OfType<VScrollBar>().Any(s => s.Visible) ? SystemInformation.VerticalScrollBarWidth : 0);
        int bottom = ClientSize.Height - (Controls.OfType<HScrollBar>().Any(s => s.Visible) ? SystemInformation.HorizontalScrollBarHeight : 0);
        if (top >= bottom || right <= 0) return;
        var state = e.Graphics.Save();
        e.Graphics.SetClip(new Rectangle(0, top, right, bottom - top), CombineMode.Intersect);
        Wallpaper.Paint(this, e.Graphics, 125);
        e.Graphics.Restore(state);
    }
}
