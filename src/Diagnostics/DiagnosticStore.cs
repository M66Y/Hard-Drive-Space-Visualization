using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DiskAtlas;

internal sealed record DiagnosticPolicy(int Schema, bool Enabled, Guid Generation)
{
    internal static readonly DiagnosticPolicy Unavailable = new(1, false, Guid.Empty);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Healthy => Schema == 1 && Generation != Guid.Empty;
}
internal sealed record DiagnosticSnapshot(DiagnosticPolicy Policy, IReadOnlyList<DiagnosticRecord> Records,
    long Bytes, int Rejected, bool Limited, bool MaintenanceFailed = false);
internal sealed record DiagnosticClearResult(DiagnosticPolicy Policy, int Deleted, int Failed);
internal sealed record ProblemNote(Guid Id, string Title, string Body, DateTimeOffset CreatedUtc, DateTimeOffset ModifiedUtc, Guid Revision);
internal sealed record NoteFile(int Schema, List<ProblemNote> Notes);
internal sealed class NoteConflictException : IOException;
internal sealed class NoteLimitException : IOException;

// All methods are synchronous and run on a worker. Mutex ownership must stay on one OS thread.
internal sealed class DiagnosticStore
{
    internal const int SegmentLimit = 1024 * 1024, SegmentCount = 8, NoteLimit = 1024 * 1024;
    internal const int ViewLimit = 2000;
    internal readonly string Root, DirectoryPath;
    private readonly string mutexName;
    private readonly Func<DateTimeOffset> clock;
    private readonly int segmentLimit;
    private DateTimeOffset nextPrune;
    private DateTimeOffset lastPrune;
    private int rejectedByCleanup;
    private bool maintenanceFailed;
    internal string SettingsPath => Path.Combine(DirectoryPath, "preferences.json");
    internal string NotesPath => Path.Combine(DirectoryPath, "problem-notes.json");
    internal string LegacyPath => Path.Combine(Root, "last-error.log");
    internal string Segment(int index) => Path.Combine(DirectoryPath, $"runtime-{index}.jsonl");
    internal string MutexName => mutexName;

    public DiagnosticStore(string root, Func<DateTimeOffset>? clock = null, int segmentLimit = SegmentLimit)
    {
        Root = Path.GetFullPath(root); DirectoryPath = Path.Combine(Root, "diagnostics");
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.segmentLimit = Math.Clamp(segmentLimit, 2048, SegmentLimit);
        mutexName = "Local\\AsukaLensDiagnostics-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())));
    }

    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, mutexName);
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(250); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Diagnostic lock unavailable");
            CheckSafe(Root); CheckSafe(DirectoryPath);
            Directory.CreateDirectory(DirectoryPath);
            return action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private static void CheckSafe(string path)
    {
        // Refuse redirection of owned files/directories. Never follow a link when clearing or rotating.
        for (string? item = path; item != null; item = Path.GetDirectoryName(item))
        {
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Diagnostic storage redirected");
        }
    }

    internal static byte[] ReadBounded(string path, int limit)
    {
        CheckSafe(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > limit) throw new InvalidDataException();
        byte[] bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException();
        return bytes;
    }

    internal static void AtomicWrite(string path, byte[] bytes, CancellationToken token = default, bool export = false)
    {
        CheckSafe(path);
        string pending = path + (export ? "." + Guid.NewGuid().ToString("N") : "") + ".tmp";
        CheckSafe(pending);
        try
        {
            using (var file = new FileStream(pending, export ? FileMode.CreateNew : FileMode.Create, FileAccess.Write, FileShare.None))
            {
                token.ThrowIfCancellationRequested(); file.Write(bytes); file.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(pending, path, true);
        }
        finally { try { File.Delete(pending); } catch (Exception ex) when (IsStorageError(ex)) { } }
    }

    private DiagnosticPolicy LoadPolicy(bool create)
    {
        if (!File.Exists(SettingsPath))
        {
            if (!create) return DiagnosticPolicy.Unavailable;
            var initial = new DiagnosticPolicy(1, true, Guid.NewGuid()); SavePolicy(initial); return initial;
        }
        try
        {
            var bytes = ReadBounded(SettingsPath, 4096);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.EnumerateObject()
                .Select(p => p.Name).Order().SequenceEqual(new[] { "enabled", "generation", "schema" })) return DiagnosticPolicy.Unavailable;
            var result = JsonSerializer.Deserialize<DiagnosticPolicy>(bytes, DiagnosticCodec.Json);
            return result?.Healthy == true ? result : DiagnosticPolicy.Unavailable;
        }
        catch (Exception ex) when (IsStorageError(ex)) { return DiagnosticPolicy.Unavailable; }
    }
    private void SavePolicy(DiagnosticPolicy value) => AtomicWrite(SettingsPath, JsonSerializer.SerializeToUtf8Bytes(value, DiagnosticCodec.Json));

    public DiagnosticPolicy Initialize() => Locked(() =>
    {
        var policy = LoadPolicy(true);
        // These names belong exclusively to atomic saves in this module, under the same mutex.
        foreach (string path in new[] { SettingsPath, NotesPath }.Concat(Enumerable.Range(0, SegmentCount).Select(Segment)))
        {
            CheckSafe(path + ".tmp"); File.Delete(path + ".tmp");
        }
        foreach (string pending in EmergencyTemps()) { CheckSafe(pending); File.Delete(pending); }
        Prune(true);
        return policy;
    });

    public DiagnosticPolicy Policy() => Locked(() => LoadPolicy(false));
    public DiagnosticPolicy SetEnabled(bool enabled) => Locked(() =>
    {
        var policy = new DiagnosticPolicy(1, enabled, Guid.NewGuid()); SavePolicy(policy); return policy;
    });

    public DiagnosticPolicy Append(IReadOnlyList<(Guid Generation, DiagnosticRecord Record)> batch) => Locked(() =>
    {
        var policy = LoadPolicy(false);
        Prune(false);
        if (maintenanceFailed) throw new IOException("Diagnostic maintenance incomplete");
        foreach (var item in batch)
        {
            if (!policy.Enabled || item.Generation != policy.Generation) continue;
            byte[] bytes = Encoding.UTF8.GetBytes(DiagnosticCodec.Encode(item.Record) + "\n");
            string path = Segment(0); CheckSafe(path);
            if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > segmentLimit)
            {
                CheckSafe(Segment(SegmentCount - 1)); File.Delete(Segment(SegmentCount - 1));
                for (int i = SegmentCount - 2; i >= 0; i--)
                {
                    CheckSafe(Segment(i)); CheckSafe(Segment(i + 1));
                    if (File.Exists(Segment(i))) File.Move(Segment(i), Segment(i + 1), true);
                }
            }
            using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            file.Write(bytes);
            nextPrune = Min(nextPrune, item.Record.TimeUtc.AddDays(14));
        }
        return policy;
    });

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private void Prune(bool force)
    {
        var now = clock();
        if (!force && now >= lastPrune && now < nextPrune) return;
        lastPrune = now; nextPrune = now.AddDays(14);
        maintenanceFailed = false;
        for (int i = 0; i < SegmentCount; i++)
        {
            try { PruneSegment(i, now); }
            catch (Exception ex) when (IsStorageError(ex)) { maintenanceFailed = true; }
        }
        try
        {
            CheckSafe(LegacyPath);
            if (File.Exists(LegacyPath))
            {
                bool oversized = new FileInfo(LegacyPath).Length > 512;
                bool expired = oversized;
                if (!expired)
                {
                    var record = DiagnosticCodec.ReadLegacy(Encoding.UTF8.GetString(ReadBounded(LegacyPath, 512)));
                    var time = record?.TimeUtc ?? new DateTimeOffset(File.GetLastWriteTimeUtc(LegacyPath), TimeSpan.Zero);
                    expired = time <= now.AddDays(-14); nextPrune = Min(nextPrune, time.AddDays(14));
                }
                if (expired) { File.Delete(LegacyPath); if (oversized) rejectedByCleanup++; }
            }
        }
        catch (Exception ex) when (IsStorageError(ex)) { maintenanceFailed = true; }
        if (maintenanceFailed) nextPrune = now.AddSeconds(1);
    }

    private void PruneSegment(int i, DateTimeOffset now)
    {
            string path = Segment(i); CheckSafe(path);
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > segmentLimit) { File.Delete(path); rejectedByCleanup++; return; }
            string text = Encoding.UTF8.GetString(ReadBounded(path, segmentLimit));
            using var reader = new StringReader(text);
            var kept = new StringBuilder(); bool changed = false;
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                var record = DiagnosticCodec.Decode(line);
                if (record == null)
                {
                    changed = true; rejectedByCleanup++;
                    continue;
                }
                if (record.TimeUtc <= now.AddDays(-14)) { changed = true; continue; }
                nextPrune = Min(nextPrune, record.TimeUtc.AddDays(14)); kept.Append(line).Append('\n');
            }
            if (kept.Length == 0) File.Delete(path);
            else if (changed) AtomicWrite(path, Encoding.UTF8.GetBytes(kept.ToString()));
    }

    public DiagnosticSnapshot Read() => Locked(() =>
    {
        Prune(true);
        var records = new Queue<DiagnosticRecord>(); int rejected = rejectedByCleanup; bool limited = false; long bytes = 0;
        void Add(DiagnosticRecord? record)
        {
            if (record == null) { rejected++; return; }
            if (record.TimeUtc <= clock().AddDays(-14)) return;
            if (records.Count == ViewLimit) { records.Dequeue(); limited = true; }
            records.Enqueue(record);
        }
        for (int i = SegmentCount - 1; i >= 0; i--)
        {
            string path = Segment(i);
            try
            {
                if (!File.Exists(path)) continue;
                var data = ReadBounded(path, segmentLimit); bytes += data.Length;
                using var reader = new StringReader(Encoding.UTF8.GetString(data));
                while (reader.ReadLine() is { } line) if (line.Length != 0) Add(DiagnosticCodec.Decode(line));
            }
            catch (Exception ex) when (IsStorageError(ex)) { rejected++; }
        }
        if (File.Exists(LegacyPath))
        {
            try { var data = ReadBounded(LegacyPath, 512); Add(DiagnosticCodec.ReadLegacy(Encoding.UTF8.GetString(data))); }
            catch (Exception ex) when (IsStorageError(ex)) { rejected++; }
        }
        return new DiagnosticSnapshot(LoadPolicy(false), records.OrderByDescending(r => r.TimeUtc).ToArray(), bytes, rejected, limited, maintenanceFailed);
    });

    public DiagnosticClearResult Clear() => Locked(() =>
    {
        var policy = LoadPolicy(false) with { Generation = Guid.NewGuid() };
        SavePolicy(policy); // Cross-process barrier: batches queued before this point can never reappear.
        int deleted = 0, failed = 0;
        foreach (string path in Enumerable.Range(0, SegmentCount).Select(Segment).Append(LegacyPath).Concat(EmergencyTemps()))
        {
            try { CheckSafe(path); if (File.Exists(path)) { File.Delete(path); deleted++; } }
            catch (Exception ex) when (IsStorageError(ex)) { failed++; }
        }
        nextPrune = default;
        if (failed == 0) rejectedByCleanup = 0;
        return new DiagnosticClearResult(policy, deleted, failed);
    });

    public ErrorReportResult ReportError(Exception error) => Locked(() =>
    {
        var policy = LoadPolicy(false);
        if (!policy.Enabled) return new(DiagnosticCodec.Classify(error).ToString(), $"0x{error.HResult:X8}", false, null, Enabled: false);
        CheckSafe(LegacyPath); Prune(false);
        foreach (string pending in EmergencyTemps()) { CheckSafe(pending); File.Delete(pending); }
        return ErrorReport.Write(error, Root);
    });

    private IEnumerable<string> EmergencyTemps() => Directory.EnumerateFiles(Root, "last-error.log.*.tmp", SearchOption.TopDirectoryOnly)
        .Where(path => Path.GetFileName(path) is { Length: 51 } name && Guid.TryParseExact(name.Substring(15, 32), "N", out _));

    private List<ProblemNote> LoadNotes()
    {
        if (!File.Exists(NotesPath)) return [];
        var file = JsonSerializer.Deserialize<NoteFile>(ReadBounded(NotesPath, NoteLimit), DiagnosticCodec.Json);
        if (file?.Schema != 1 || file.Notes == null || file.Notes.Count > 100 || file.Notes.Any(n => !ValidNote(n)) ||
            file.Notes.Select(n => n.Id).Distinct().Count() != file.Notes.Count) throw new InvalidDataException();
        return file.Notes;
    }
    private static bool ValidNote(ProblemNote? n) => n != null && n.Id != Guid.Empty && n.Revision != Guid.Empty &&
        !string.IsNullOrWhiteSpace(n.Title) && n.Title.Length <= 100 && n.Body != null && n.Body.Length <= 2000 &&
        n.CreatedUtc.Offset == TimeSpan.Zero && n.ModifiedUtc.Offset == TimeSpan.Zero;
    public IReadOnlyList<ProblemNote> Notes() => Locked(() => (IReadOnlyList<ProblemNote>)LoadNotes().OrderByDescending(n => n.ModifiedUtc).ToArray());
    public ProblemNote SaveNote(ProblemNote? previous, string title, string body) => Locked(() =>
    {
        var notes = LoadNotes(); var now = clock();
        var note = new ProblemNote(previous?.Id ?? Guid.NewGuid(), title.Trim(), body, previous?.CreatedUtc ?? now, now, Guid.NewGuid());
        if (!ValidNote(note)) throw new NoteLimitException();
        int index = notes.FindIndex(n => n.Id == note.Id);
        if (previous != null && (index < 0 || notes[index].Revision != previous.Revision)) throw new NoteConflictException();
        if (index < 0) { if (notes.Count >= 100) throw new NoteLimitException(); notes.Add(note); }
        else notes[index] = note;
        SaveNotes(notes); return note;
    });
    public void DeleteNote(ProblemNote previous) => Locked(() =>
    {
        var notes = LoadNotes(); int index = notes.FindIndex(n => n.Id == previous.Id);
        if (index < 0 || notes[index].Revision != previous.Revision) throw new NoteConflictException();
        notes.RemoveAt(index); SaveNotes(notes); return true;
    });
    private void SaveNotes(List<ProblemNote> notes)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new NoteFile(1, notes), DiagnosticCodec.Json);
        if (bytes.Length > NoteLimit) throw new NoteLimitException();
        AtomicWrite(NotesPath, bytes);
    }

    internal static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or System.Security.SecurityException or JsonException or OutOfMemoryException;
}
