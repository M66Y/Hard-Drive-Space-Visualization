namespace DiskAtlas;

// The override is explicit and process-local. Invalid input must never fall back
// to the user's real settings while an acceptance session is running.
internal sealed record AppDataLocation(string Root, bool IsIsolated)
{
    internal const string EnvironmentVariable = "ASUKALENS_DATA_DIR";
    public string BackgroundPath => Path.Combine(Root, "background.dat");

    public static AppDataLocation Resolve(string? directory)
    {
        if (directory is null)
            return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AsukaLens"), false);
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("An existing absolute data directory is required.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!), StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(root))
            throw new ArgumentException("An existing data directory below a volume root is required.");
        return new(root, true);
    }
}
