using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DiskAtlas.ResizePrototype;

// A UI-thread-owned, single-current-geometry cache. Returned bitmaps are
// borrowed until the next miss, Invalidate, or Dispose; the host must not own
// or mutate them. No path, HWND capture, persistent data, or background worker.
internal sealed class ResizeSceneBaseCache : IDisposable
{
    internal const long MaxPixels = 8_000_000;
    private Bitmap? bitmap;
    private CacheKey? key;
    private bool disposed, building;
    private long version;

    internal long Hits { get; private set; }
    internal long Misses { get; private set; }
    internal long Builds { get; private set; }
    internal long Rejections { get; private set; }
    internal long RetainedBytes => bitmap == null ? 0 : (long)bitmap.Width * bitmap.Height * 4;
    internal Size CachedSize => bitmap?.Size ?? Size.Empty;

    internal bool TryGet(ResizeSceneRenderer renderer, ResizeSceneLayout layout, ResizeSceneState state,
        Image background, Image? icon, out Bitmap? image, Action<string>? diagnosticStage = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(background);
        image = null;
        if (building) { Rejections++; return false; }

        var requested = new CacheKey(renderer, layout, background, icon, ThemeKey.Capture());
        if (bitmap != null && key != null && key.Matches(requested))
        {
            Hits++;
            image = bitmap;
            return true;
        }
        Misses++;
        // Release before allocating the replacement: never retain two cache
        // bitmaps or stretch a previous geometry while rebuilding this one.
        Release();
        Size size = layout.PixelSize;
        if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > MaxPixels)
        {
            Rejections++;
            return false;
        }

        Bitmap? candidate = null;
        long startedVersion = version;
        building = true;
        try
        {
            candidate = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            candidate.SetResolution(layout.Dpi, layout.Dpi);
            using (var graphics = Graphics.FromImage(candidate))
                renderer.Render(graphics, layout, state, background, icon, 0, diagnosticStage, includeEdgeAnimation: false);
            // A diagnostic callback can re-enter the host or change its theme.
            // Such an interrupted scene must not become a reusable base layer.
            if (disposed || startedVersion != version || requested.Theme != ThemeKey.Capture())
            {
                Rejections++;
                return false;
            }
            bitmap = candidate;
            candidate = null;
            key = requested;
            Builds++;
            image = bitmap;
            return true;
        }
        catch (Exception failure) when (failure is OutOfMemoryException or ArgumentException or ExternalException or InvalidOperationException)
        {
            Rejections++;
            return false;
        }
        finally
        {
            candidate?.Dispose();
            building = false;
        }
    }

    // Required if an owner edits the pixels of an existing Image in place;
    // changing the background/icon reference already invalidates the key.
    internal void Invalidate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        version++;
        Release();
    }
    private void Release()
    {
        bitmap?.Dispose();
        bitmap = null;
        key = null;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        version++;
        Release();
    }

    private sealed class CacheKey(ResizeSceneRenderer renderer, ResizeSceneLayout layout,
        Image background, Image? icon, ThemeKey theme)
    {
        private readonly ResizeSceneRenderer renderer = renderer;
        private readonly Size size = layout.PixelSize;
        private readonly int dpi = layout.Dpi;
        private readonly ResizeSceneSnapshot state = layout.State;
        private readonly Image background = background;
        private readonly Image? icon = icon;
        internal ThemeKey Theme { get; } = theme;
        internal bool Matches(CacheKey other) => ReferenceEquals(renderer, other.renderer) &&
            size == other.size && dpi == other.dpi && state == other.state &&
            ReferenceEquals(background, other.background) && ReferenceEquals(icon, other.icon) && Theme == other.Theme;
    }

    private readonly record struct ThemeKey(int Background, int Sidebar, int Surface, int Border,
        int Text, int Muted, int Accent, int Selection, int ControlBorder, int ProgressStart, int ProgressEnd,
        string Categories)
    {
        internal static ThemeKey Capture()
        {
            var palette = DiskAtlas.Theme.ProgressPalette;
            // Keep complete values rather than a hash-only palette key. The
            // category array is readonly as a reference but its entries can
            // still be changed by a synthetic test or future theme provider.
            string categories = string.Join("\u001f", DiskAtlas.Theme.Categories.Select(category =>
                category.Name.Length.ToString(CultureInfo.InvariantCulture) + ":" + category.Name + ":" +
                category.Color.ToArgb().ToString(CultureInfo.InvariantCulture)));
            return new(DiskAtlas.Theme.Background.ToArgb(), DiskAtlas.Theme.Sidebar.ToArgb(),
                DiskAtlas.Theme.Surface.ToArgb(), DiskAtlas.Theme.Border.ToArgb(), DiskAtlas.Theme.Text.ToArgb(),
                DiskAtlas.Theme.Muted.ToArgb(), DiskAtlas.Theme.Accent.ToArgb(), DiskAtlas.Theme.Selection.ToArgb(),
                DiskAtlas.Theme.ControlBorder.ToArgb(), palette.Start.ToArgb(), palette.End.ToArgb(), categories);
        }
    }
}
