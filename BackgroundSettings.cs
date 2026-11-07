using System.Drawing.Imaging;
using System.Security.Cryptography;

namespace DiskAtlas;

internal readonly record struct BackgroundView(float X, float Y, float Zoom, int? AccentArgb)
{
    public static BackgroundView Default => new(.5f, .5f, 1, null);

    public bool IsValid => X is >= 0 and <= 1 && Y is >= 0 and <= 1 && Zoom is >= 1 and <= 3;
}

internal sealed record BackgroundChoice(Bitmap Image, BackgroundView View, Color AutomaticAccent) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

internal static class BackgroundSettings
{
    private const int FormatVersion = 2;
    private const int MaxFileBytes = 24 * 1024 * 1024;
    internal const int MaxSourcePixels = 40_000_000;
    internal const int MaxSavedSide = 2048;
    public static string PathForUser => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AsukaLens", "background.dat");

    public static BackgroundChoice Import(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var file = File.OpenRead(path);
        CheckLength(file);
        using var image = Image.FromStream(file);
        if (image.RawFormat.Guid != ImageFormat.Png.Guid && image.RawFormat.Guid != ImageFormat.Jpeg.Guid &&
            image.RawFormat.Guid != ImageFormat.Bmp.Guid)
            throw new InvalidDataException();
        CheckDimensions(image, MaxSourcePixels);
        // Normalize camera orientation before sizing and discard source metadata in the copy.
        if (image.PropertyIdList.Contains(0x0112))
        {
            var orientation = image.GetPropertyItem(0x0112);
            if (orientation?.Value is { Length: >= 2 } value)
                image.RotateFlip(BitConverter.ToUInt16(value, 0) switch
                {
                    2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone,
                    4 => RotateFlipType.Rotate180FlipX, 5 => RotateFlipType.Rotate90FlipX,
                    6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX,
                    8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone
                });
        }
        token.ThrowIfCancellationRequested();
        var result = Prepare(image, BackgroundView.Default, MaxSourcePixels);
        if (!token.IsCancellationRequested) return result;
        result.Dispose();
        token.ThrowIfCancellationRequested();
        throw new OperationCanceledException(token);
    }

    public static BackgroundChoice? Load(string path)
    {
        FileStream file;
        try { file = File.OpenRead(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using var ownedFile = file;
        CheckLength(file);
        using var header = new BinaryReader(file);
        int version = header.ReadInt32();
        using var payload = new MemoryStream();
        if (version == FormatVersion)
        {
            int length = header.ReadInt32();
            var hash = header.ReadBytes(32);
            if (length < 18 || hash.Length != 32 || length != file.Length - file.Position)
                throw new InvalidDataException();
            file.CopyTo(payload);
            if (!CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(payload.GetBuffer().AsSpan(0, length))))
                throw new InvalidDataException();
        }
        else if (version == 1) file.CopyTo(payload); // Read the earlier development preview format.
        else throw new InvalidDataException();
        payload.Position = 0;
        using var reader = new BinaryReader(payload);
        float x = reader.ReadSingle(), y = reader.ReadSingle(), zoom = reader.ReadSingle();
        byte manual = reader.ReadByte();
        int argb = reader.ReadInt32();
        var view = new BackgroundView(x, y, zoom, manual == 1 ? argb : null);
        if (manual > 1 || !view.IsValid) throw new InvalidDataException();
        // Image decoders seek relative to their stream start.
        using var png = new MemoryStream();
        payload.CopyTo(png);
        png.Position = 0;
        using var image = Image.FromStream(png);
        if (image.RawFormat.Guid != ImageFormat.Png.Guid || image.Width > MaxSavedSide || image.Height > MaxSavedSide)
            throw new InvalidDataException();
        return Prepare(image, view, MaxSavedSide * MaxSavedSide);
    }

    public static void Save(string path, BackgroundChoice choice)
    {
        if (!choice.View.IsValid) throw new InvalidDataException();
        if (choice.Image.Width > MaxSavedSide || choice.Image.Height > MaxSavedSide) throw new InvalidDataException();
        // Encode before opening the destination; a failed save must leave the old file intact.
        using var payload = new MemoryStream();
        using (var fields = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            fields.Write(choice.View.X); fields.Write(choice.View.Y); fields.Write(choice.View.Zoom);
            fields.Write(choice.View.AccentArgb.HasValue); fields.Write(choice.View.AccentArgb.GetValueOrDefault());
            using var png = new MemoryStream();
            choice.Image.Save(png, ImageFormat.Png);
            png.WriteTo(payload);
        }
        if (payload.Length + 40 > MaxFileBytes) throw new InvalidDataException();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write(FormatVersion);
                writer.Write((int)payload.Length);
                writer.Write(SHA256.HashData(payload.GetBuffer().AsSpan(0, (int)payload.Length)));
                payload.WriteTo(file);
                file.Flush(flushToDisk: true);
            }
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(pending); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void Reset(string path)
    {
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { }
    }

    private static void CheckLength(FileStream file)
    {
        if (file.Length <= 0 || file.Length > MaxFileBytes) throw new InvalidDataException();
    }

    private static BackgroundChoice Prepare(Image image, BackgroundView view, int maxPixels)
    {
        CheckDimensions(image, maxPixels);
        float factor = Math.Min(1f, (float)MaxSavedSide / Math.Max(image.Width, image.Height));
        var bitmap = new Bitmap(Math.Max(1, (int)(image.Width * factor)), Math.Max(1, (int)(image.Height * factor)));
        try
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, new Rectangle(Point.Empty, bitmap.Size));
            }
            return new BackgroundChoice(bitmap, view, FindAccent(bitmap));
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static void CheckDimensions(Image image, int maxPixels)
    {
        if (image.Width < 1 || image.Height < 1 || (long)image.Width * image.Height > maxPixels)
            throw new InvalidDataException();
    }

    private static Color FindAccent(Bitmap bitmap)
    {
        using var sample = new Bitmap(bitmap, 32, 32);
        var colors = new List<Color>();
        for (int y = 0; y < sample.Height; y++)
        for (int x = 0; x < sample.Width; x++)
        {
            var color = sample.GetPixel(x, y);
            if (color.GetSaturation() > .25f && color.GetBrightness() is > .18f and < .82f)
                colors.Add(color);
        }
        if (colors.Count == 0) return Theme.DefaultAccent;
        // Pick a common hue rather than averaging unrelated hues into gray.
        var group = colors.GroupBy(c => (int)(c.GetHue() / 30)).MaxBy(g => g.Count())!;
        return Color.FromArgb((int)group.Average(c => c.R), (int)group.Average(c => c.G), (int)group.Average(c => c.B));
    }

    public static bool IsFileError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException;
}
