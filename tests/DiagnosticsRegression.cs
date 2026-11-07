using DiskAtlas;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal static class DiagnosticsRegression
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static DiagnosticRecord Entry(DiagnosticKind kind = DiagnosticKind.ScanStarted) => new()
    { Kind = kind, Session = Guid.NewGuid(), TimeUtc = DateTimeOffset.UtcNow, Files = 42, Mode = ScanMode.LowImpact };
    private static void Throws<T>(Action work) where T : Exception
    { try { work(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void StorageFails(Action work)
    { try { work(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } throw new Exception("Expected storage failure"); }

    public static int Child(string root)
    {
        try { new DiagnosticStore(root).Policy(); return 1; }
        catch (IOException) { Console.WriteLine("PASS cross-process mutex bounded contention"); return 0; }
    }

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "AsukaLens-diagnostics-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); int passed = 0;
        void Test(string name, Action<string> work)
        { string part = Path.Combine(root, (++passed).ToString()); Directory.CreateDirectory(part); work(part); Console.WriteLine("PASS " + name); }
        try
        {
            Test("strict record schema rejects unknown fields, enum values, duplicate keys, nulls and unbounded input", path =>
            {
                string valid = DiagnosticCodec.Encode(Entry());
                Check(DiagnosticCodec.Decode(valid) != null, "Valid record rejected");
                foreach (string invalid in new[] { valid.Replace("\"schema\":1", "\"schema\":2"), valid.Replace("\"kind\":\"ScanStarted\"", "\"kind\":999"),
                    valid.Replace("\"schema\":1", "\"schema\":1,\"schema\":1"), valid.Replace("\"schema\":1,", ""),
                    valid[..^1] + ",\"path\":\"private-marker@example.invalid\"}", "{\"build\":null}", new string('x', 1100), "[]" })
                    Check(DiagnosticCodec.Decode(invalid) == null, "Untrusted record accepted");
                Throws<InvalidDataException>(() => DiagnosticCodec.Encode(Entry() with { Milliseconds = double.NaN }));
                var error = new SecretException();
                Check(DiagnosticCodec.Classify(error) == ErrorCategory.Unexpected, "Exception getter evaluated");
                string legacy = "App=AsukaLens\nSchema=1\nTimeUtc=" + DateTime.UtcNow.ToString("O") + "\nCategory=IO\nCode=0x80070005\n";
                Check(DiagnosticCodec.ReadLegacy(legacy) is { Build: null } && DiagnosticCodec.ReadLegacy(legacy + "Path=private\n") == null &&
                    DiagnosticCodec.ReadLegacy(legacy.Replace("Category=IO", "Category=private@example.invalid")) == null, "Legacy whitelist failed");
            });
            Test("persistent switch, corrupt preferences fail closed, explicit recovery and exception opt-out", path =>
            {
                var store = new DiagnosticStore(path); Check(store.Initialize().Enabled, "First-run default not enabled");
                store.SetEnabled(false); Check(!new DiagnosticStore(path).Initialize().Enabled, "Off did not persist");
                var report = store.ReportError(new SecretException());
                Check(!report.Enabled && !report.Saved && report.UserMessage.Contains("未启用") && !File.Exists(store.LegacyPath), "Off wrote an exception");
                File.WriteAllText(store.SettingsPath, "{}"); Check(!store.Initialize().Healthy && !store.Policy().Enabled, "Corrupt settings enabled");
                var policy = store.SetEnabled(true);
                string json = File.ReadAllText(store.SettingsPath).Replace("\"enabled\":true", "\"enabled\":false,\"enabled\":true");
                File.WriteAllText(store.SettingsPath, json); Check(!store.Policy().Healthy, "Duplicate setting enabled capture");
                store.SetEnabled(false);
                byte[] before = File.ReadAllBytes(store.SettingsPath);
                using (var locked = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    StorageFails(() => store.SetEnabled(true));
                Check(File.ReadAllBytes(store.SettingsPath).SequenceEqual(before) && !store.Policy().Enabled, "Failed switch altered state");
                Check(!Directory.GetFiles(store.DirectoryPath, "*.tmp").Any(), "Atomic settings temp leaked");
            });
            Test("rotation and mixed-age retention remain bounded across wall-clock changes", path =>
            {
                var now = DateTimeOffset.UtcNow; var store = new DiagnosticStore(path, () => now, 2048); var policy = store.Initialize();
                for (int i = 0; i < 220; i++) store.Append([(policy.Generation, Entry() with { Scan = i, TimeUtc = now })]);
                var files = Directory.GetFiles(store.DirectoryPath, "runtime-*.jsonl");
                Check(files.Length <= 8 && files.All(f => new FileInfo(f).Length <= 2048) && files.Sum(f => new FileInfo(f).Length) <= 8 * 2048, "Quota exceeded");
                Check(store.Read().Records.All(r => r.Scan > 0) && store.Read().Records.Any(r => r.Scan == 219), "Rotation retained oldest batches or lost latest");
                now = now.AddDays(13); store.Append([(policy.Generation, Entry() with { Scan = 900, TimeUtc = now })]);
                now = now.AddDays(2); var retained = store.Read();
                Check(retained.Records.Count == 1 && retained.Records[0].Scan == 900, "Mixed segment did not expire individual records");
                now = now.AddDays(-20); Check(store.Read().Records.Count == 1, "Clock rollback destroyed bounded future record");
                now = now.AddDays(40); Check(store.Read().Records.Count == 0, "Retention failed after clock change");
                File.WriteAllBytes(store.Segment(0), new byte[3000]);
                Check(store.Read().Rejected > 0 && !File.Exists(store.Segment(0)), "Oversize input not safely rejected");
            });
            Test("clear uses a generation barrier, preserves unrelated files and reports partial failure", path =>
            {
                var store = new DiagnosticStore(path); var before = store.Initialize();
                store.Append([(before.Generation, Entry())]);
                var note = store.SaveNote(null, "Synthetic note", "Not exported");
                string background = Path.Combine(path, "background.dat"); File.WriteAllText(background, "fixture");
                string other = Path.Combine(store.DirectoryPath, "keep.txt"); File.WriteAllText(other, "fixture");
                string orphan = store.LegacyPath + "." + Guid.NewGuid().ToString("N") + ".tmp"; File.WriteAllText(orphan, "fixture");
                using (var locked = new FileStream(store.Segment(0), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var partial = store.Clear(); Check(partial.Failed == 1 && partial.Policy.Generation != before.Generation, "Partial clear reported success");
                }
                var cleared = store.Clear(); Check(cleared.Failed == 0, "Clear retry failed");
                store.Append([(before.Generation, Entry())]);
                Check(store.Read().Records.Count == 0, "Old queued records resurrected");
                Check(store.Notes().Single().Id == note.Id && File.Exists(background) && File.Exists(other) && !File.Exists(orphan), "Clear scope or orphan cleanup failed");
                store.Append([(cleared.Policy.Generation, Entry())]); Check(store.Read().Records.Count == 1, "New events cannot resume after clear");
            });
            Test("corrupt records and legacy report cannot contaminate the selected export", path =>
            {
                var store = new DiagnosticStore(path); var policy = store.Initialize();
                store.Append([(policy.Generation, Entry())]);
                File.AppendAllText(store.Segment(0), "{\"private\":\"secret-marker@example.invalid\"}\n{broken}\n");
                File.WriteAllText(store.LegacyPath, "App=AsukaLens\nMessage=private-marker\n");
                store.SaveNote(null, "private-marker", "secret-marker@example.invalid");
                var snapshot = store.Read(); Check(snapshot.Rejected == 3 && snapshot.Records.Count == 1, "Rejection accounting incorrect");
                string preview = DiagnosticCodec.Preview(snapshot.Records, 7, snapshot.Rejected, snapshot.Limited);
                Check(!preview.Contains("private") && !preview.Contains("secret-marker") && preview.Contains("QueueDropped=7") && preview.Contains("UnreadableOrRejected=3"), "Preview crossed whitelist");
                using var locked = new FileStream(store.LegacyPath, FileMode.Open, FileAccess.Read, FileShare.None);
                var partial = store.Read();
                Check(partial.MaintenanceFailed && partial.Records.Count == 1, "One locked file hid readable diagnostics");
            });
            Test("atomic export matches immutable preview, cancellation/failure preserve the existing target", path =>
            {
                string file = Path.Combine(path, "export.txt"); string preview = DiagnosticCodec.Preview([Entry()], 0, 0, false);
                DiagnosticService.ExportAsync(file, preview, CancellationToken.None).GetAwaiter().GetResult();
                Check(File.ReadAllBytes(file).SequenceEqual(Encoding.UTF8.GetBytes(preview)), "Export differs from preview");
                using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    StorageFails(() => DiagnosticService.ExportAsync(file, "changed", CancellationToken.None).GetAwaiter().GetResult());
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                Throws<OperationCanceledException>(() => DiagnosticService.ExportAsync(file, "changed", cancelled.Token).GetAwaiter().GetResult());
                Check(File.ReadAllText(file) == preview && Directory.GetFiles(path).Length == 1, "Failed export replaced target or leaked temp");
            });
            Test("notes restore, enforce revisions/limits and preserve unsaved or corrupt data", path =>
            {
                var store = new DiagnosticStore(path); store.Initialize();
                var first = store.SaveNote(null, "问题 Example", "Synthetic reproduction");
                var other = new DiagnosticStore(path); var prior = other.Notes().Single();
                var updated = store.SaveNote(first, "Updated", "Local only");
                Throws<NoteConflictException>(() => other.SaveNote(prior, "Stale", "Stale"));
                Throws<NoteConflictException>(() => other.DeleteNote(prior));
                Check(other.Notes().Single().Body == "Local only", "Stale writer overwrote note");
                Throws<NoteLimitException>(() => store.SaveNote(null, new string('x', 101), ""));
                Throws<NoteLimitException>(() => store.SaveNote(null, "x", new string('x', 2001)));
                store.DeleteNote(updated); Check(store.Notes().Count == 0, "Note deletion failed");
                for (int i = 0; i < 100; i++) store.SaveNote(null, "Note " + i, "text");
                Throws<NoteLimitException>(() => store.SaveNote(null, "Overflow", "text"));
                string before = File.ReadAllText(store.NotesPath);
                using (var locked = new FileStream(store.NotesPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    StorageFails(() => store.DeleteNote(store.Notes()[0]));
                Check(File.ReadAllText(store.NotesPath) == before, "Failed note write lost prior data");
                File.WriteAllText(store.NotesPath, "{broken}");
                Throws<JsonException>(() => store.SaveNote(null, "new", ""));
                Check(File.ReadAllText(store.NotesPath) == "{broken}", "Corrupt notes silently overwritten");
            });
            Test("note file byte cap refuses growth before overwriting", path =>
            {
                var store = new DiagnosticStore(path); store.Initialize(); bool limited = false; string before = "";
                for (int i = 0; i < 100; i++)
                {
                    before = File.Exists(store.NotesPath) ? File.ReadAllText(store.NotesPath) : "";
                    try { store.SaveNote(null, "Unicode " + i, new string('界', 2000)); }
                    catch (NoteLimitException) { limited = true; break; }
                }
                Check(limited && new FileInfo(store.NotesPath).Length <= DiagnosticStore.NoteLimit && File.ReadAllText(store.NotesPath) == before, "Byte limit not atomic");
            });
            Test("queue overload is bounded, producer never waits on storage, off/clear isolate pending work", path =>
            {
                using var service = new DiagnosticService(path); service.Ready.GetAwaiter().GetResult(); service.FlushAsync().GetAwaiter().GetResult();
                var store = new DiagnosticStore(path);
                using (var mutex = new Mutex(false, store.MutexName))
                {
                    mutex.WaitOne();
                    try
                    {
                        var watch = Stopwatch.StartNew();
                        for (int i = 0; i < 10000; i++) service.Record(Entry());
                        Check(service.Pending <= 128 && service.Dropped > 0 && watch.Elapsed < TimeSpan.FromSeconds(3), "Queue growth or blocking producer");
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                service.FlushAsync().GetAwaiter().GetResult();
                Check(service.SnapshotAsync().GetAwaiter().GetResult().Records.Any(r => r.Kind == DiagnosticKind.QueueOverflow), "Dropped events not reported");
                service.SetEnabledAsync(false).GetAwaiter().GetResult();
                int count = service.SnapshotAsync().GetAwaiter().GetResult().Records.Count;
                service.Record(Entry()); var error = service.ReportErrorAsync(new SecretException()).GetAwaiter().GetResult();
                Check(!error.Enabled && service.Pending == 0 && service.SnapshotAsync().GetAwaiter().GetResult().Records.Count == count, "Disabled service wrote data");
                service.SetEnabledAsync(true).GetAwaiter().GetResult();
                for (int i = 0; i < 128; i++) service.Record(Entry());
                service.ClearAsync().GetAwaiter().GetResult(); service.FlushAsync().GetAwaiter().GetResult();
                Check(service.SnapshotAsync().GetAwaiter().GetResult().Records.Count == 0, "Queue survived clear");
                service.Failure(DiagnosticKind.ScanFailed, new SecretException(), 5); service.FlushAsync().GetAwaiter().GetResult();
                Check(service.SnapshotAsync().GetAwaiter().GetResult().Records.Single().Category == ErrorCategory.Unexpected, "Error integration failed");
            });
            Test("multi-instance switch and clear invalidate old generations across services", path =>
            {
                using var first = new DiagnosticService(path); using var second = new DiagnosticService(path);
                Task.WaitAll(first.Ready, second.Ready); Task.WaitAll(first.FlushAsync(), second.FlushAsync());
                var store = new DiagnosticStore(path); var stale = store.Policy();
                second.SetEnabledAsync(false).GetAwaiter().GetResult();
                first.Record(Entry()); first.FlushAsync().GetAwaiter().GetResult();
                Check(!first.Enabled, "Other instance did not observe off");
                second.ClearAsync().GetAwaiter().GetResult();
                store.Append([(stale.Generation, Entry())]); Check(store.Read().Records.Count == 0, "Stale batch crossed generation");
            });
            Test("cross-process mutex contention has a finite wait", path =>
            {
                var store = new DiagnosticStore(path); store.Initialize();
                using var mutex = new Mutex(false, store.MutexName); mutex.WaitOne();
                try
                {
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(DiagnosticsRegression).Assembly.Location);
                    start.ArgumentList.Add("--diagnostics-child"); start.ArgumentList.Add(path);
                    using var child = Process.Start(start)!;
                    if (!child.WaitForExit(10000)) { child.Kill(); throw new Exception("Child lock wait unbounded"); }
                    Check(child.ExitCode == 0 && child.StandardOutput.ReadToEnd().Contains("PASS cross-process"), "Cross-process mutex missing");
                }
                finally { mutex.ReleaseMutex(); }
            });
            Test("storage errors remain nonfatal and bounded view reports truncation", path =>
            {
                string blocked = Path.Combine(path, "blocked"); File.WriteAllText(blocked, "fixture");
                using var service = new DiagnosticService(blocked); service.Ready.GetAwaiter().GetResult();
                Check(service.StorageFailed && !service.Enabled, "Failed store enabled collection");
                service.Record(Entry()); Check(service.Pending == 0, "Unavailable store accepted events");
                var store = new DiagnosticStore(path); var policy = store.Initialize();
                store.Append(Enumerable.Range(0, 2100).Select(i => (policy.Generation, Entry() with { Scan = i })).ToArray());
                var snapshot = store.Read(); Check(snapshot.Limited && snapshot.Records.Count == 2000, "View unbounded");
            });
            Console.WriteLine($"All {passed} diagnostics regression groups passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "AsukaLens-diagnostics-tests-"), StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe cleanup");
            if (Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) throw new Exception("Unexpected redirected fixture");
            Directory.Delete(root, true);
        }
    }
    private sealed class SecretException : Exception
    {
        public override string Message => throw new Exception("Forbidden getter");
        public override string? StackTrace => throw new Exception("Forbidden getter");
        public override string ToString() => throw new Exception("Forbidden getter");
    }
}
