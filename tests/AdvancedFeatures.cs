using DiskAtlas;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

internal static class AdvancedFeatures
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr reserved);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static int Run()
    {
        string workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string root = Path.Combine(workspace, "artifacts", "feature-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); int passed = 0;
        void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
        DiskEntry FileEntry(DiskEntry tree, string suffix) => tree.Descendants().Single(e => !e.IsDirectory && e.Path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        void Equal(DiskEntry a, DiskEntry b)
        {
            var x = a.Descendants().Prepend(a).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            var y = b.Descendants().Prepend(b).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            Check(x.Length == y.Length, "Entry count differs from full scan");
            for (int i = 0; i < x.Length; i++)
                Check(x[i].Path == y[i].Path && x[i].Size == y[i].Size && x[i].FileCount == y[i].FileCount && x[i].FolderCount == y[i].FolderCount &&
                    x[i].AllocatedSize == y[i].AllocatedSize && x[i].UnknownAllocationFiles == y[i].UnknownAllocationFiles, "Entry differs from full scan: " + x[i].Path);
        }
        try
        {
            Test("scan policies have explicit worker limits and low-impact yielding", () =>
            {
                Check(ScanPolicy.Resolve(ScanMode.Automatic, DiskKind.SolidState).Workers == 8, "SSD automatic policy");
                Check(ScanPolicy.Resolve(ScanMode.Automatic, DiskKind.Mechanical).Workers == 1, "HDD automatic policy");
                Check(ScanPolicy.Resolve(ScanMode.Automatic, DiskKind.Network).Workers == 2, "Network automatic policy");
                Check(ScanPolicy.Resolve(ScanMode.LowImpact, DiskKind.Unknown) is { Workers: 1, BatchDelayMilliseconds: > 0 }, "Low-impact policy");
                Check(ScanPolicy.Resolve(ScanMode.HighSpeed, DiskKind.Mechanical).Workers == 8, "High-speed policy");
            });
            string physical = Path.Combine(root, "physical"); Directory.CreateDirectory(physical);
            File.WriteAllBytes(Path.Combine(physical, "a.bin"), new byte[12000]);
            Test("actual allocation and file identities recognize hard links without double counting", () =>
            {
                Check(CreateHardLinkW(Path.Combine(physical, "z.bin"), Path.Combine(physical, "a.bin"), IntPtr.Zero), "Hard-link creation failed: " + Marshal.GetLastWin32Error());
                var scan = new DiskScanner().Scan(physical, null, CancellationToken.None);
                Check(scan.Root.UnknownAllocationFiles == 0, "Native allocation probe failed: " + string.Join(";", scan.AllocationIssues.Select(i => i.Reason)));
                var a = FileEntry(scan.Root, "a.bin"); var z = FileEntry(scan.Root, "z.bin");
                Check(a.Storage!.Identity == z.Storage!.Identity && a.Storage.LinkCount == 2, "Hard-link identity mismatch");
                Check(scan.Root.Size == 24000 && scan.Root.FileCount == 2, "Logical sizes must remain per path");
                Check(scan.Root.AllocatedSize == a.Storage.AllocatedBytes && a.AllocatedSize > 0 && z.SharedAllocation, "Hard link counted twice or wrong deterministic owner");
            });
            Test("sparse files use allocated data rather than logical length", () =>
            {
                string path = Path.Combine(physical, "sparse.bin");
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    Check(NativeStorage.DeviceIoControl(stream.SafeFileHandle, 0x000900c4, null, 0, [], 0, out _, IntPtr.Zero), "Cannot mark sparse test file");
                    stream.SetLength(16 * 1024 * 1024);
                }
                var scan = new DiskScanner().Scan(physical, null, CancellationToken.None);
                var sparse = FileEntry(scan.Root, "sparse.bin");
                Check(sparse.Size == 16 * 1024 * 1024 && sparse.Storage != null && sparse.Storage.AllocatedBytes < sparse.Size, "Sparse allocation not respected");
            });
            Test("compressed files report compressed allocation", () =>
            {
                string path = Path.Combine(physical, "compressed.bin");
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    byte[] format = [2, 0];
                    Check(NativeStorage.DeviceIoControl(stream.SafeFileHandle, 0x0009c040, format, format.Length, [], 0, out _, IntPtr.Zero), "Cannot enable compression on test file");
                    stream.Write(new byte[2 * 1024 * 1024]);
                    stream.Flush(flushToDisk: true);
                }
                var file = FileEntry(new DiskScanner().Scan(physical, null, CancellationToken.None).Root, "compressed.bin");
                Check(file.Storage != null && file.Storage.AllocatedBytes < file.Size, $"Compression allocation not respected: logical {file.Size}, allocation {file.Storage?.AllocatedBytes}, attributes {File.GetAttributes(path)}");
            });
            Test("all scan modes keep identical logical and physical results", () =>
            {
                var reference = new DiskScanner().Scan(physical, null, CancellationToken.None);
                foreach (var mode in Enum.GetValues<ScanMode>())
                {
                    var scan = new DiskScanner().Scan(physical, null, CancellationToken.None, options: new(mode)); Equal(reference.Root, scan.Root);
                    Check(scan.Statistics!.Mode == mode, "Mode not recorded");
                }
            });
            Test("allocated queries preserve deduplicated totals and expose unknown values", () =>
            {
                var scan = new DiskScanner().Scan(physical, null, CancellationToken.None);
                foreach (var mode in Enum.GetValues<ViewMode>())
                {
                    var view = ViewQuery.Build(new(scan.Root, mode, Metric: SizeMetric.Allocated), CancellationToken.None);
                    Check(view.VisibleBytes == scan.Root.AllocatedSize, "Allocated query total mismatch");
                }
                var unknown = new DiskEntry(Path.Combine(root, "unknown"), true);
                unknown.MutableChildren.Add(new DiskEntry(Path.Combine(unknown.Path, "one.bin"), false, unknown) { Size = 17, FileCount = 1 });
                StorageMetrics.Calculate(unknown, CancellationToken.None);
                var types = ViewQuery.Build(new(unknown, ViewMode.FileTypes, Metric: SizeMetric.Allocated), CancellationToken.None);
                Check(unknown.UnknownAllocationFiles == 1 && types.Rows[0].UnknownFiles == 1, "Unknown allocation presented as a known zero");
            });

            string scope = Path.Combine(root, "incremental");
            Directory.CreateDirectory(Path.Combine(scope, "A", "deep")); Directory.CreateDirectory(Path.Combine(scope, "B"));
            File.WriteAllBytes(Path.Combine(scope, "root.txt"), new byte[29]);
            File.WriteAllBytes(Path.Combine(scope, "A", "deep", "changed.bin"), new byte[47]);
            File.WriteAllBytes(Path.Combine(scope, "B", "stable.bin"), new byte[97]);
            var fake = new FakeJournal(); var coordinator = new ScanCoordinator(_ => fake);
            var options = new ScanOptions(ScanMode.HighSpeed);
            ScanResult baseline = coordinator.Scan(scope, null, options, null, CancellationToken.None);
            Check(baseline.Root.UnknownAllocationFiles == 0, "Incremental fixture storage unknown");
            Test("unchanged journal reuses the tree without enumerating directories", () =>
            {
                var next = coordinator.Scan(scope, baseline, options, null, CancellationToken.None);
                Equal(next.Root, baseline.Root);
                Check(next.Statistics is { Incremental: true, ReadDirectories: 0, ReusedFiles: 3 }, "No-change scan did filesystem work");
                Check(!ReferenceEquals(next.Root, baseline.Root) && !ReferenceEquals(FileEntry(next.Root, "stable.bin"), FileEntry(baseline.Root, "stable.bin")), "Incremental refresh mutates the completed snapshot");
            });
            Test("deep edits with preserved timestamps rescan only the changed directory", () =>
            {
                var original = FileEntry(baseline.Root, "changed.bin");
                var stamp = File.GetLastWriteTimeUtc(original.Path); File.WriteAllBytes(original.Path, new byte[8000]); File.SetLastWriteTimeUtc(original.Path, stamp);
                fake.Advance(Change(original));
                var next = coordinator.Scan(scope, baseline, options, null, CancellationToken.None);
                Equal(next.Root, new DiskScanner().Scan(scope, null, CancellationToken.None).Root);
                Check(next.Statistics is { Incremental: true, ReadDirectories: 1, ReusedFiles: 2 }, "Deep edit scanned unrelated directories");
                Check(original.Size == 47, "Previous snapshot was changed"); baseline = next;
            });
            Test("directory moves, deletion and newly created subtrees match a full scan", () =>
            {
                var moved = baseline.Root.FindDirectory(Path.Combine(scope, "A", "deep"))!;
                var oldParent = moved.Parent!; var destination = baseline.Root.FindDirectory(Path.Combine(scope, "B"))!;
                var deleted = FileEntry(baseline.Root, "stable.bin"); File.Delete(deleted.Path);
                Directory.Move(moved.Path, Path.Combine(destination.Path, "moved"));
                Directory.CreateDirectory(Path.Combine(oldParent.Path, "new", "nested")); File.WriteAllBytes(Path.Combine(oldParent.Path, "new", "nested", "new.txt"), new byte[13]);
                fake.Advance(Change(moved), new(moved.Storage!.Identity.Low, moved.Storage.Identity.High, destination.Storage!.Identity.Low, destination.Storage.Identity.High, 0x2000, true), Change(deleted),
                    new(999, 0, oldParent.Storage!.Identity.Low, oldParent.Storage.Identity.High, 0x100, true));
                var next = coordinator.Scan(scope, baseline, options, null, CancellationToken.None);
                Equal(next.Root, new DiskScanner().Scan(scope, null, CancellationToken.None).Root); baseline = next;
            });
            Test("writes through a hard link outside the root invalidate all known aliases", () =>
            {
                string source = Path.Combine(scope, "A", "shared.bin"), alias = Path.Combine(scope, "B", "shared.bin"), outside = Path.Combine(root, "outside.bin");
                File.WriteAllBytes(source, new byte[17000]); Check(CreateHardLinkW(alias, source, IntPtr.Zero) && CreateHardLinkW(outside, source, IntPtr.Zero), "Cannot create cross-directory hard links");
                baseline = coordinator.Scan(scope, null, options, null, CancellationToken.None);
                var entry = FileEntry(baseline.Root, Path.Combine("A", "shared.bin")); File.WriteAllBytes(outside, new byte[33000]);
                fake.Advance(new JournalChange(entry.Storage!.Identity.Low, entry.Storage.Identity.High, 0xdead, 0, 2, false));
                var next = coordinator.Scan(scope, baseline, options, null, CancellationToken.None);
                Equal(next.Root, new DiskScanner().Scan(scope, null, CancellationToken.None).Root);
                Check(next.Root.Descendants().Where(e => e.Name == "shared.bin").All(e => e.Size == 33000), "An alias kept stale logical length"); baseline = next;
            });
            Test("already-open writers remain dirty even without another journal record", () =>
            {
                string path = Path.Combine(scope, "writer.bin");
                using var writer = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                writer.Write(new byte[5000]); writer.Flush();
                var first = coordinator.Scan(scope, null, options, null, CancellationToken.None);
                Check(FileEntry(first.Root, "writer.bin").Storage!.WritableHandleOpen, "Open writer not detected");
                writer.SetLength(9000); writer.Flush();
                var next = coordinator.Scan(scope, first, options, null, CancellationToken.None);
                Check(FileEntry(next.Root, "writer.bin").Size == 9000 && next.Statistics!.ReadDirectories > 0, "Coalesced open-writer change was missed");
            });
            Test("changes during a refresh are replayed from its start checkpoint", () =>
            {
                baseline = coordinator.Scan(scope, null, options, null, CancellationToken.None);
                var file = FileEntry(baseline.Root, "root.txt"); bool changed = false;
                long expected = fake.Next;
                var next = coordinator.Scan(scope, baseline, options, new Inline(p =>
                {
                    if (!changed) { changed = true; File.WriteAllBytes(file.Path, new byte[24000]); fake.Advance(Change(file)); }
                }), CancellationToken.None);
                Check(next.Checkpoint!.NextUsn == expected, "Saved the end cursor and lost concurrent changes");
                var replay = coordinator.Scan(scope, next, options, null, CancellationToken.None);
                Equal(replay.Root, new DiskScanner().Scan(scope, null, CancellationToken.None).Root); baseline = replay;
            });
            Test("journal gaps and unavailable journals safely fall back to a full scan", () =>
            {
                fake.Id++;
                var next = coordinator.Scan(scope, baseline, options, null, CancellationToken.None);
                Check(!next.Statistics!.Incremental && next.Statistics.Reason.Contains("回退"), "Journal reset reused stale data");
                Equal(next.Root, new DiskScanner().Scan(scope, null, CancellationToken.None).Root);
                var fallback = new ScanCoordinator(_ => null).Scan(scope, next, options, null, CancellationToken.None);
                Check(fallback.Statistics is { Incremental: true, VerifiedDirectories: > 0, VerifiedFiles: > 0 }, "Unavailable journal reused data without metadata verification");
                var forced = coordinator.Scan(scope, next, options, null, CancellationToken.None, forceFull: true);
                Check(!forced.Statistics!.Incremental && forced.Statistics.Reason.Contains("强制"), "Force-full request ignored"); baseline = forced;
            });
            Test("cancelled incremental refresh leaves old logical and physical totals untouched", () =>
            {
                var expected = baseline.Root.Size; var allocated = baseline.Root.AllocatedSize;
                using var stop = new CancellationTokenSource();
                try { coordinator.Scan(scope, baseline, options, new Inline(_ => stop.Cancel()), stop.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
                Check(baseline.Root.Size == expected && baseline.Root.AllocatedSize == allocated, "Cancellation mutated the previous result");
            });
            Test("journal record parser accepts IDs and rejects unsupported or malformed records", () =>
            {
                byte[] record = new byte[64]; BinaryPrimitives.WriteInt32LittleEndian(record, 64); BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
                BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), 123); BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), 456);
                BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), 1); BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(40), 2);
                var entries = new List<JournalChange>(); NtfsChangeJournal.Parse(record, 2, entries);
                Check(entries.Count == 1 && entries[0].IdLow == 123 && entries[0].ParentLow == 456, "Journal ID parser mismatch");
                record[4] = 4;
                try { NtfsChangeJournal.Parse(record, 2, []); throw new Exception("Unsupported journal version accepted"); } catch (IOException) { }
                record[4] = 2; record[0] = 0;
                try { NtfsChangeJournal.Parse(record, 2, []); throw new Exception("Malformed journal length accepted"); } catch (IOException) { }
            });
            Test("real NTFS refresh agrees with a full scan and reports its actual path", () =>
            {
                var actual = new ScanCoordinator(); var first = actual.Scan(scope, null, options, null, CancellationToken.None);
                File.WriteAllBytes(Path.Combine(scope, "A", "real.txt"), new byte[35000]);
                var next = actual.Scan(scope, first, options, null, CancellationToken.None);
                Equal(next.Root, new DiskScanner().Scan(scope, null, CancellationToken.None).Root);
                Console.WriteLine($"REAL journal: {next.Statistics!.Incremental}; directories {next.Statistics.ReadDirectories}; reused {next.Statistics.ReusedFiles}; reason {next.Statistics.Reason}; automatic disk {NativeStorage.DetectDisk(scope)}");
                if (!next.Statistics.Incremental)
                {
                    string volume = @"\\.\" + Path.GetPathRoot(scope)![..2];
                    foreach (uint access in new uint[] { 0, 0x80, 1, 0x80000000 })
                    {
                        using var handle = NativeStorage.CreateFileW(volume, access, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                        int openError = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                        byte[] data = new byte[128]; bool queried = !handle.IsInvalid && NativeStorage.DeviceIoControl(handle, 0x900f4, null, 0, data, data.Length, out _, IntPtr.Zero);
                        Console.WriteLine($"JOURNAL DIAG access {access:X}: open error {openError}; query {queried}; query error {(queried ? 0 : Marshal.GetLastWin32Error())}");
                    }
                }
            });
            Test("metadata verification catches deep edits with restored timestamps and hard-link alias writes", () =>
            {
                var verified = new ScanCoordinator(_ => null);
                var before = verified.Scan(scope, null, options, null, CancellationToken.None);
                var target = FileEntry(before.Root, Path.Combine("A", "shared.bin"));
                DateTime originalTime = File.GetLastWriteTimeUtc(target.Path);
                File.WriteAllBytes(Path.Combine(root, "outside.bin"), new byte[49000]); File.SetLastWriteTimeUtc(target.Path, originalTime);
                var after = verified.Scan(scope, before, options, null, CancellationToken.None);
                Equal(after.Root, new DiskScanner().Scan(scope, null, CancellationToken.None).Root);
                Check(after.Statistics is { Incremental: true, VerifiedDirectories: > 0, VerifiedFiles: > 0, ReusedFiles: > 0 }, "Metadata reuse is not validated");
            });
            Test("metadata verification notices new directories and incomplete baselines force a full scan", () =>
            {
                var verified = new ScanCoordinator(_ => null); var before = verified.Scan(scope, null, options, null, CancellationToken.None);
                Directory.CreateDirectory(Path.Combine(scope, "new", "deep")); File.WriteAllBytes(Path.Combine(scope, "new", "deep", "one.bin"), new byte[1700]);
                var after = verified.Scan(scope, before, options, null, CancellationToken.None);
                Equal(after.Root, new DiskScanner().Scan(scope, null, CancellationToken.None).Root);
                before.Root.UnknownAllocationFiles = 1;
                var full = verified.Scan(scope, before, options, null, CancellationToken.None);
                Check(!full.Statistics!.Incremental && full.Statistics.Reason.Contains("不完整"), "Incomplete baseline was reused");
            });
            Test("replaced directories with identical names and timestamps do not inherit cached file identities", () =>
            {
                string swap = Path.Combine(root, "swap"), child = Path.Combine(swap, "child"); Directory.CreateDirectory(child);
                string file = Path.Combine(child, "same.bin"); File.WriteAllBytes(file, new byte[12000]);
                var timestamp = File.GetLastWriteTimeUtc(file);
                var verified = new ScanCoordinator(_ => null); var before = verified.Scan(swap, null, options, null, CancellationToken.None);
                var identity = FileEntry(before.Root, "same.bin").Storage!.Identity;
                Directory.Move(child, Path.Combine(root, "saved-child")); Directory.CreateDirectory(child);
                File.WriteAllBytes(file, new byte[12000]); File.SetLastWriteTimeUtc(file, timestamp);
                var after = verified.Scan(swap, before, options, null, CancellationToken.None);
                Check(FileEntry(after.Root, "same.bin").Storage!.Identity != identity, "Replacement kept an unverified cached identity");
                Equal(after.Root, new DiskScanner().Scan(swap, null, CancellationToken.None).Root);
            });
            Console.WriteLine($"All {passed} advanced checks passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            string prefix = Path.GetFullPath(Path.Combine(workspace, "artifacts")) + Path.DirectorySeparatorChar + "feature-tests-";
            if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe advanced-test cleanup path");
            Directory.Delete(root, true);
        }
    }
    private static JournalChange Change(DiskEntry entry) => new(entry.Storage!.Identity.Low, entry.Storage.Identity.High,
        entry.Parent!.Storage!.Identity.Low, entry.Parent.Storage.Identity.High, 2, entry.IsDirectory);
    private sealed class Inline(Action<ScanProgress> report) : IProgress<ScanProgress> { public void Report(ScanProgress value) => report(value); }
    private sealed class FakeJournal : IChangeJournal
    {
        public ulong Id = 123; public long Next = 1;
        private readonly List<(long Usn, JournalChange Change)> changes = [];
        public void Advance(params JournalChange[] records) { foreach (var record in records) changes.Add((Next, record)); Next++; }
        public JournalCheckpoint Capture() => new(Id, Next, 0);
        public IReadOnlyList<JournalChange> Read(JournalCheckpoint from, JournalCheckpoint to, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (from.JournalId != to.JournalId) throw new IOException("fake journal reset");
            return changes.Where(c => c.Usn >= from.NextUsn && c.Usn < to.NextUsn).Select(c => c.Change).ToArray();
        }
        public void Dispose() { }
    }
}
