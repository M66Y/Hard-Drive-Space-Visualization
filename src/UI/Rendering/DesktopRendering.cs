namespace DiskAtlas;

// Ordinary startup uses the accepted composition path. Explicit developer
// entries remain stable so archived candidates and pixel references keep their
// original meaning; MainForm's test constructor is not silently changed.
internal readonly record struct DesktopRendering(bool Dib, bool Opaque, bool Composition, bool CandidateLabel)
{
    internal static DesktopRendering Resolve(string[] args, string? executable)
    {
        string? selected = null;
        foreach (string flag in new[] { "--legacy-rendering", "--dib-candidate", "--opaque-redirect-candidate", "--composition-dib-candidate" })
        {
            if (!args.Contains(flag, StringComparer.Ordinal)) continue;
            if (selected != null) throw new ArgumentException("Choose one rendering option.");
            selected = flag;
        }
        if (selected == null)
            selected = Path.GetFileNameWithoutExtension(executable)?.ToUpperInvariant() switch
            {
                "ASUKALENS.DIBCANDIDATE" => "--dib-candidate",
                "ASUKALENS.OPAQUEDIBCANDIDATE" => "--opaque-redirect-candidate",
                "ASUKALENS.COMPOSITIONDIBCANDIDATE" => "--composition-dib-candidate",
                _ => null
            };
        return selected switch
        {
            "--legacy-rendering" => new(false, false, false, false),
            "--dib-candidate" => new(true, false, false, true),
            "--opaque-redirect-candidate" => new(true, true, false, true),
            "--composition-dib-candidate" => new(true, false, true, true),
            _ => new(true, false, true, false)
        };
    }
}
