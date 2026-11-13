namespace DiskAtlas.NtfsPrototype;

// Fixed presets bound work; choosing a preset never proves volume provenance.
// Legacy fixtures retain their old entry/page budgets. Only the personal source
// explicitly opts into Large, with a matching cumulative native reader budget.
internal sealed class NtfsScanLimits
{
    internal static NtfsScanLimits Standard { get; } = new(262144, 32*1024*1024, 4096, 512, 32768, false);
    internal static NtfsScanLimits Large { get; } = new(1048576, 64*1024*1024, 16384, 4096, 131072, true);
    private NtfsScanLimits(int entries, int characters, int mftPages, int bitmapPages, int nativeObjects, bool compact)
    { MaximumEntries=entries; MaximumCharacters=characters; MaximumMftPages=mftPages; MaximumBitmapPages=bitmapPages; MaximumNativeObjects=nativeObjects; CompactPaths=compact; }
    internal int MaximumEntries { get; }
    internal int MaximumCharacters { get; }
    internal int MaximumMftPages { get; }
    internal int MaximumBitmapPages { get; }
    internal int MaximumNativeObjects { get; }
    internal bool CompactPaths { get; }
}
