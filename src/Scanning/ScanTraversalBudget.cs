namespace DiskAtlas;

internal enum ScanTraversalLimitKind { None, Entries, Names, Paths, Queue, InvalidUse }

// Deliberately not an IOException: a test boundary must abort the scan, never
// be converted into an ordinary skipped entry or a partially successful tree.
internal sealed class ScanTraversalLimitException(ScanTraversalLimitKind kind) : Exception("Traversal" + kind)
{
    internal ScanTraversalLimitKind Kind { get; } = kind;
}

// Opt-in, single-use guard for a full independent snapshot comparison. It
// stores counters only; normal product scans do not allocate this object.
internal sealed class ScanTraversalBudget
{
    private readonly object gate = new();
    private readonly int maxEntries, maxQueuedDirectories;
    private readonly long maxNameCharacters, maxPathCharacters;
    private long names, paths;
    private int entries, queued, rootLength;
    private bool started;
    private ScanTraversalLimitKind failure;

    internal ScanTraversalBudget(int maxEntries = 1_048_576, long maxNameCharacters = 64L * 1024 * 1024,
        long maxPathCharacters = 64L * 1024 * 1024, int? maxQueuedDirectories = null)
    {
        int queueLimit = maxQueuedDirectories ?? maxEntries;
        if (maxEntries is < 1 or > 1_048_576 || maxNameCharacters is < 0 or > 64L * 1024 * 1024 ||
            maxPathCharacters is < 0 or > 64L * 1024 * 1024 || queueLimit < 1 || queueLimit > maxEntries)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        this.maxEntries = maxEntries; this.maxNameCharacters = maxNameCharacters;
        this.maxPathCharacters = maxPathCharacters; this.maxQueuedDirectories = queueLimit;
    }

    internal ScanTraversalLimitKind Failure { get { lock (gate) return failure; } }
    internal void Begin(int rootPathLength)
    {
        lock (gate)
        {
            Require(!started && rootPathLength is >= 1 and <= 32767, ScanTraversalLimitKind.InvalidUse);
            started = true; rootLength = rootPathLength; entries = 1;
        }
    }

    internal void ObserveEntry(int nameLength, int parentRelativeLength, bool directory)
    {
        lock (gate)
        {
            CheckActive();
            Require(nameLength is >= 1 and <= 255 && parentRelativeLength is >= 0 and <= 32767, ScanTraversalLimitKind.InvalidUse);
            long relative = parentRelativeLength == 0 ? nameLength : (long)parentRelativeLength + 1 + nameLength;
            Require(entries < maxEntries, ScanTraversalLimitKind.Entries);
            Require(names + nameLength <= maxNameCharacters, ScanTraversalLimitKind.Names);
            Require(relative + rootLength + 1 <= 32767 && (!directory || paths + relative <= maxPathCharacters), ScanTraversalLimitKind.Paths);
            entries++; names += nameLength; if (directory) paths += relative;
        }
    }

    internal void EnqueueDirectory()
    {
        lock (gate) { CheckActive(); Require(queued < maxQueuedDirectories, ScanTraversalLimitKind.Queue); queued++; }
    }
    internal void DequeueDirectory()
    {
        lock (gate) { CheckActive(); Require(queued > 0, ScanTraversalLimitKind.InvalidUse); queued--; }
    }
    private void CheckActive()
    {
        if (failure != ScanTraversalLimitKind.None) throw new ScanTraversalLimitException(failure);
        Require(started, ScanTraversalLimitKind.InvalidUse);
    }
    private void Require(bool condition, ScanTraversalLimitKind kind)
    {
        if (condition) return;
        if (failure == ScanTraversalLimitKind.None) failure = kind;
        throw new ScanTraversalLimitException(failure);
    }
}
