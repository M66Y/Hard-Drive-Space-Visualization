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
        float scale = Math.Max((float)form.ClientSize.Width / image.Width, (float)form.ClientSize.Height / image.Height);
        float width = image.Width * scale, height = image.Height * scale;
        var target = new RectangleF((form.ClientSize.Width - width) / 2 - offset.X,
            (form.ClientSize.Height - height) / 2 - offset.Y, width, height);
        graphics.DrawImage(image, target);
        using var veil = new SolidBrush(Color.FromArgb(wash, Theme.Surface));
        graphics.FillRectangle(veil, control.ClientRectangle);
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
