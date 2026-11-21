using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

// Pure owned bitmaps. This compares two GDI+ calls, not UI frames, physical
// display latency or full application performance. No Form or file is created.
internal static class WallpaperDrawingProbe
{
    private static readonly Color Background = Color.FromArgb(249, 241, 236);
    private static readonly Color Surface = Color.FromArgb(255, 252, 249);
    private static readonly Color Sentinel = Color.FromArgb(47, 79, 103);
    private const int SamplingHalo = 2;

    internal static int Run()
    {
        var budget = Stopwatch.StartNew();
        int cases = 0;
        try
        {
            using var raster = Pattern(2140, 1354);
            foreach (Size canvasSize in new[] { new Size(320, 240), new Size(960, 640) })
            foreach (Point offset in new[] { Point.Empty, new Point(235, 157), new Point(-80, -40), new Point(2067, 1303) })
            foreach (int clipKind in Enumerable.Range(0, 3))
            foreach (int wash in new[] { 44, 122 })
            foreach (bool halfPixel in new[] { false, true })
            {
                if (budget.Elapsed > TimeSpan.FromSeconds(6)) return Fail("CorrectnessBudgetExceeded", cases);
                cases++;
                using var reference = Canvas(canvasSize);
                using var candidate = Canvas(canvasSize);
                Rectangle clip = clipKind switch
                {
                    0 => new Rectangle(Point.Empty, canvasSize),
                    1 => new Rectangle(17, 13, 120, 55),
                    _ => new Rectangle(-9, -7, 65, 43)
                };
                bool complex = cases % 3 == 0;
                PaintCase(reference, raster, offset, clip, wash, halfPixel, complex, false);
                PaintCase(candidate, raster, offset, clip, wash, halfPixel, complex, true);
                int differing = DifferentPixels(reference, candidate);
                if (differing != 0)
                {
                    Console.Error.WriteLine($"FAIL wallpaper drawing code=PixelMismatch; case={cases}; differing_pixels={differing}");
                    return 1;
                }
            }

            using var timingCanvas = Canvas(new Size(960, 640));
            Point timingOffset = new(850, 380);
            // Warm both paths before alternating three paired samples.
            Batch(timingCanvas, raster, timingOffset, false);
            Batch(timingCanvas, raster, timingOffset, true);
            var referenceMs = new List<double>(3);
            var candidateMs = new List<double>(3);
            for (int round = 0; round < 3; round++)
            {
                if (budget.Elapsed > TimeSpan.FromSeconds(8)) return Fail("TimingBudgetExceeded", cases);
                if (round % 2 == 0) { Measure(false, referenceMs); Measure(true, candidateMs); }
                else { Measure(true, candidateMs); Measure(false, referenceMs); }
            }
            referenceMs.Sort(); candidateMs.Sort();
            Console.WriteLine(FormattableString.Invariant(
                $"PASS wallpaper drawing exact_cases={cases}; source_pixels={raster.Width * raster.Height}; sampling_halo={SamplingHalo}; calls_per_sample=32; warm_pairs=3; full_median_ms={referenceMs[1]:F3}; cropped_median_ms={candidateMs[1]:F3}; elapsed_ms={budget.Elapsed.TotalMilliseconds:F0}; bitmap_calls_only=True; ui_performance_claim=False"));
            return 0;

            void Measure(bool cropped, List<double> samples)
            {
                long started = Stopwatch.GetTimestamp();
                Batch(timingCanvas, raster, timingOffset, cropped);
                samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL wallpaper drawing code=OwnedBitmapFailure; hresult=" +
                error.HResult.ToString("X8", CultureInfo.InvariantCulture));
            return 1;
        }
    }

    private static int Fail(string code, int cases)
    {
        Console.Error.WriteLine($"FAIL wallpaper drawing code={code}; exact_cases_completed={cases}");
        return 1;
    }

    private static Bitmap Pattern(int width, int height)
    {
        var image = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        try
        {
            image.SetResolution(96, 96);
            var data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int index = x * 4;
                        row[index] = (byte)((x * 17 + y * 3) % 251);
                        row[index + 1] = (byte)((x / 5 + y * 11) % 253);
                        row[index + 2] = (byte)((x * 7 + y / 3) % 255);
                        row[index + 3] = 255;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                }
            }
            finally { image.UnlockBits(data); }
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    private static Bitmap Canvas(Size size)
    {
        var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        try { using var graphics = Graphics.FromImage(image); graphics.Clear(Sentinel); return image; }
        catch { image.Dispose(); throw; }
    }

    private static void PaintCase(Bitmap canvas, Bitmap raster, Point offset, Rectangle clip, int wash,
        bool halfPixel, bool complex, bool cropped)
    {
        using var graphics = Graphics.FromImage(canvas);
        graphics.SetClip(clip);
        if (complex)
        {
            // An ellipse crossing the clip's four edges creates a nonrectangular
            // region. Retain its shape; only its bounding box may be copied.
            using var shape = new GraphicsPath();
            shape.AddEllipse(clip.X - 3, clip.Y - 5, Math.Max(1, clip.Width + 5), Math.Max(1, clip.Height + 9));
            graphics.SetClip(shape, CombineMode.Intersect);
        }
        if (halfPixel)
        {
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        }
        Paint(graphics, new Rectangle(Point.Empty, canvas.Size), raster, offset, wash, cropped);
    }

    private static void Paint(Graphics graphics, Rectangle client, Bitmap raster, Point offset, int wash, bool cropped)
    {
        using var background = new SolidBrush(Background);
        graphics.FillRectangle(background, client);
        if (cropped)
        {
            Rectangle visible = Rectangle.Intersect(client, Rectangle.Ceiling(graphics.VisibleClipBounds));
            // Keep sampling neighbours outside the dirty rectangle. A tight
            // source rectangle changes GDI+'s edge sampling with Half pixel
            // offset, even at 1:1 scale. The original graphics clip remains
            // unchanged, so the halo cannot paint outside the invalid area.
            if (visible.Width > 0 && visible.Height > 0) visible.Inflate(SamplingHalo, SamplingHalo);
            Rectangle destination = Rectangle.Intersect(visible, new Rectangle(-offset.X, -offset.Y, raster.Width, raster.Height));
            if (destination.Width > 0 && destination.Height > 0)
                graphics.DrawImage(raster, destination, destination.X + offset.X, destination.Y + offset.Y,
                    destination.Width, destination.Height, GraphicsUnit.Pixel);
        }
        else
        {
            // Independent reference: the exact original Wallpaper.Paint call.
            graphics.DrawImageUnscaled(raster, -offset.X, -offset.Y);
        }
        using var veil = new SolidBrush(Color.FromArgb(wash, Surface));
        graphics.FillRectangle(veil, client);
    }

    private static void Batch(Bitmap canvas, Bitmap raster, Point offset, bool cropped)
    {
        using var graphics = Graphics.FromImage(canvas);
        Rectangle client = new(Point.Empty, canvas.Size);
        graphics.Clear(Sentinel);
        for (int row = 0; row < 8; row++)
        for (int column = 0; column < 4; column++)
        {
            graphics.SetClip(new Rectangle(8 + column * 234, 8 + row * 72, 228, 66));
            Paint(graphics, client, raster, offset, 122, cropped);
        }
        graphics.Flush(FlushIntention.Sync);
    }

    private static int DifferentPixels(Bitmap reference, Bitmap candidate)
    {
        var rectangle = new Rectangle(Point.Empty, reference.Size);
        var left = reference.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var right = candidate.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int different = 0;
                var leftRow = new byte[reference.Width * 4];
                var rightRow = new byte[leftRow.Length];
                for (int row = 0; row < reference.Height; row++)
                {
                    Marshal.Copy(IntPtr.Add(left.Scan0, row * left.Stride), leftRow, 0, leftRow.Length);
                    Marshal.Copy(IntPtr.Add(right.Scan0, row * right.Stride), rightRow, 0, rightRow.Length);
                    for (int column = 0; column < reference.Width; column++)
                        if (!leftRow.AsSpan(column * 4, 4).SequenceEqual(rightRow.AsSpan(column * 4, 4))) different++;
                }
                return different;
            }
            finally { candidate.UnlockBits(right); }
        }
        finally { reference.UnlockBits(left); }
    }
}
