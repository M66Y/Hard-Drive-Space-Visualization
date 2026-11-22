using System.Text.Json;

namespace DiskAtlas;

internal enum CategoryPalette { Original, Clearer }
internal enum ReadingDensity { Comfortable, Compact }
internal sealed record DisplayPreferences(int BackgroundStrength = 100,
    CategoryPalette Palette = CategoryPalette.Original, ReadingDensity Density = ReadingDensity.Comfortable)
{
    private static readonly Color[] ClearerColors = [Color.FromArgb(231,173,138), Color.FromArgb(223,139,136), Color.FromArgb(229,193,110),
        Color.FromArgb(196,151,182), Color.FromArgb(173,153,198), Color.FromArgb(129,179,165),
        Color.FromArgb(141,174,208), Color.FromArgb(183,176,166)];
    internal static DisplayPreferences Default { get; } = new();
    internal bool IsValid => BackgroundStrength is >= 40 and <= 100 && Enum.IsDefined(Palette) && Enum.IsDefined(Density);
    internal static DisplayPreferences For(Control control) => (control.FindForm() as MainForm)?.DisplayOptions ?? Default;
    internal Color CategoryColor(string category)
    {
        int index = Array.FindIndex(Theme.Categories, c => c.Name == category);
        if (index < 0) index = Theme.Categories.Length - 1;
        if (Palette == CategoryPalette.Original) return Theme.Categories[index].Color;
        return ClearerColors[index];
    }
}

internal static class DisplayPreferencesStore
{
    internal const int MaxBytes = 4096;
    internal static string PathForUser => Path.Combine(Path.GetDirectoryName(BackgroundSettings.PathForUser)!, "display.json");
    internal static DisplayPreferences Load(string path)
    {
        FileStream stream;
        try { stream = File.OpenRead(path); }
        catch (FileNotFoundException) { return DisplayPreferences.Default; }
        catch (DirectoryNotFoundException) { return DisplayPreferences.Default; }
        using (stream)
        {
            if (stream.Length is <= 0 or > MaxBytes) throw new InvalidDataException();
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || property.Name is not ("version" or "backgroundStrength" or "palette" or "density")) throw new InvalidDataException();
            if (names.Count != 4 || !root.GetProperty("version").TryGetInt32(out int version) || version != 1 ||
                !root.GetProperty("backgroundStrength").TryGetInt32(out int strength)) throw new InvalidDataException();
            var palette = root.GetProperty("palette").GetString() switch
            { "original" => CategoryPalette.Original, "clearer" => CategoryPalette.Clearer, _ => throw new InvalidDataException() };
            var density = root.GetProperty("density").GetString() switch
            { "comfortable" => ReadingDensity.Comfortable, "compact" => ReadingDensity.Compact, _ => throw new InvalidDataException() };
            var result = new DisplayPreferences(strength, palette, density);
            return result.IsValid ? result : throw new InvalidDataException();
        }
    }
    internal static void Save(string path, DisplayPreferences preferences)
    {
        if (!preferences.IsValid) throw new InvalidDataException();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, backgroundStrength = preferences.BackgroundStrength,
            palette = preferences.Palette == CategoryPalette.Original ? "original" : "clearer",
            density = preferences.Density == ReadingDensity.Comfortable ? "comfortable" : "compact" });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(pending, path, true);
        }
        finally { try { File.Delete(pending); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
    internal static bool IsReadError(Exception error) => BackgroundSettings.IsFileError(error) ||
        error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException;
}

// Stage both settings before touching their destinations. Each replacement is
// atomic; an ordinary multi-file commit failure restores the previous bytes.
// Display-only changes never encode or replace the image file.
internal sealed class AppearanceRestoreException() : IOException("Appearance rollback incomplete; local recovery copies retained.");

internal static class AppearanceSettingsCommit
{
    private static readonly object gate = new();
    internal static void Save(string backgroundPath, string displayPath, BackgroundChoice? image, bool resetImage,
        DisplayPreferences? display, Action<int>? afterCommit = null)
    {
        lock (gate)
        {
            var entries = new List<(string Target, string Stage, string Backup, bool Existed, bool Remove)>();
            var committed = new List<int>();
            bool rollbackFailed = false;
            string id = Guid.NewGuid().ToString("N");
            void Stage(string target, bool remove, Action<string> prepare, long limit)
            {
                target = Path.GetFullPath(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                bool existed = File.Exists(target);
                if (existed && new FileInfo(target).Length > limit) throw new InvalidDataException();
                string stage = target + "." + id + ".stage", backup = target + "." + id + ".backup";
                entries.Add((target, stage, backup, existed, remove));
                if (!remove) prepare(stage);
                if (existed) File.Copy(target, backup, false);
            }
            try
            {
                if (resetImage || image != null)
                    Stage(backgroundPath, resetImage, p => BackgroundSettings.Save(p, image!), 24 * 1024 * 1024);
                if (display != null) Stage(displayPath, false, p => DisplayPreferencesStore.Save(p, display), DisplayPreferencesStore.MaxBytes);
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry.Remove) File.Delete(entry.Target); else File.Move(entry.Stage, entry.Target, true);
                    committed.Add(i); afterCommit?.Invoke(i);
                }
            }
            catch
            {
                foreach (int i in committed.AsEnumerable().Reverse())
                {
                    var entry = entries[i];
                    try { if (entry.Existed) File.Move(entry.Backup, entry.Target, true); else File.Delete(entry.Target); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { rollbackFailed = true; }
                }
                if (rollbackFailed) throw new AppearanceRestoreException();
                throw;
            }
            finally
            {
                foreach (var entry in entries)
                foreach (string path in new[] { entry.Stage, entry.Backup })
                    if (!rollbackFailed || path != entry.Backup)
                        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
