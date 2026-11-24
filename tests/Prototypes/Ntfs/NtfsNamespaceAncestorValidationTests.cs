using System.Buffers.Binary;
using System.Text;

namespace DiskAtlas.NtfsPrototype;

// Entirely in memory: synthetic attribute callbacks, directory pages and
// handles. No native source, filesystem, volume, VSS or raw reader is opened.
internal static class NtfsNamespaceAncestorValidationTests
{
    private static readonly FileIdentity RootId = Id(1);
    private static FileIdentity Id(int slot) => new(7, (1ul << 48) | (uint)slot, 0);
    private static NtfsSnapshotNamespaceEntry Dir(int index, int parent, string name) =>
        new(index, parent, name, Id(index + 1), FileAttributes.Directory, false);
    private static NtfsSnapshotNamespaceEntry Root => Dir(0, -1, "");
    private static Exception? Catch(Action action) { try { action(); return null; } catch (Exception e) { return e; } }
    private static bool Fixed(Exception? e) => e is NtfsVssProbeException value && value.Code == "VssSnapshotNative" &&
        value.Message == value.Code && value.InnerException == null;

    private sealed class Model : NtfsSnapshotNamespace.IBackend, IDisposable
    {
        internal readonly object Generation = new();
        internal readonly Dictionary<string, List<byte[]>> Pages = new(StringComparer.Ordinal);
        internal readonly List<(string Path, bool Reused)> Calls = [];
        internal readonly NtfsNamespaceAncestorValidation? Validation;
        internal int AttributesRead, Opened, IdentityChecks, TagChecks, Reads, Closed;
        internal bool Active = true, Deny, FailRead, FailClose;
        internal FileIdentity? WrongIdentity;
        internal FileAttributes HandleAttributes = FileAttributes.Directory;
        internal string? ReparsePath, FilePath, FailAttributesAt;
        internal Action? DuringOpen, DuringRead;
        internal Model(bool reuse = true, int capacity = NtfsNamespaceAncestorValidation.MaximumStates,
            CancellationToken lifetime = default)
        {
            if (reuse) Validation = new(Generation, RootId, EnsureActive, OpenCore, lifetime, capacity);
        }
        private void EnsureActive() { if (!Active) throw new IOException("SyntheticPrivateSource"); }
        public FileIdentity RootIdentity => RootId;
        public NtfsSnapshotNamespace.ICursor OpenDirectory(NtfsSnapshotNamespaceEntry entry, string path, CancellationToken token)
            => Validation == null ? OpenCore(entry, path, false, token) : Validation.OpenDirectory(Generation, entry, path, token);
        internal void OpenOnce(NtfsSnapshotNamespaceEntry entry, string path)
        { using var cursor = OpenDirectory(entry, path, default); }
        private NtfsSnapshotNamespace.ICursor OpenCore(NtfsSnapshotNamespaceEntry entry, string path, bool reuse, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); EnsureActive(); Calls.Add((path, reuse));
            var parts = NtfsPersonalSnapshotSource.RelativeSegments(path, true);
            void Attributes(string prefix)
            {
                AttributesRead++;
                if (prefix == FailAttributesAt) throw new IOException("SyntheticPrivateAttributes");
                if (prefix == ReparsePath || prefix == FilePath) throw new IOException("SyntheticPrivateType");
            }
            if (reuse) NtfsNamespaceAncestorValidation.ResolveLeaf(@"X:\SyntheticSnapshot\", path,
                absolute => { Attributes(absolute[@"X:\SyntheticSnapshot\".Length..].Replace('\\', '/')); return FileAttributes.Directory; }, token);
            else
            {
                Attributes(""); string prefix = "";
                foreach (var part in parts) { prefix = prefix.Length == 0 ? part : prefix + "/" + part; Attributes(prefix); }
            }
            if (Deny) throw new UnauthorizedAccessException("SyntheticPrivateAcl");
            Opened++; IdentityChecks++; TagChecks++;
            var cursor = new Cursor(this, entry, path);
            // Models NativeCursor's still-mandatory native handle checks.
            if (cursor.Identity != entry.Identity || (cursor.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            { cursor.Dispose(); throw new IOException("SyntheticPrivateHandle"); }
            try { DuringOpen?.Invoke(); return cursor; } catch { cursor.Dispose(); throw; }
        }
        private sealed class Cursor(Model model, NtfsSnapshotNamespaceEntry entry, string path) : NtfsSnapshotNamespace.ICursor
        {
            private int page, disposed;
            public FileIdentity Identity => model.WrongIdentity ?? entry.Identity;
            public FileAttributes Attributes => model.HandleAttributes;
            public NtfsSnapshotNamespaceIo Read(bool restart, Memory<byte> destination, CancellationToken token)
            {
                model.Reads++; model.DuringRead?.Invoke();
                if (model.FailRead) return new(false, 5);
                if (!model.Pages.TryGetValue(path, out var pages) || page == pages.Count) return new(false, 18);
                pages[page++].CopyTo(destination); return new(true, 0);
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                model.Closed++; if (model.FailClose) throw new IOException("SyntheticPrivateClose");
            }
        }
        internal void CloseSource() { Active = false; Validation?.Invalidate(); }
        public void Dispose() => Validation?.Dispose();
    }
    private static byte[] Page(params NtfsSnapshotNamespaceEntry[] entries)
    {
        byte[] page = new byte[NtfsSnapshotNamespace.PageBytes]; int offset = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i]; byte[] name = Encoding.Unicode.GetBytes(entry.Name);
            int length = (88 + name.Length + 7) & ~7;
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(offset), i + 1 < entries.Length ? (uint)length : 0);
            BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(offset + 40), ulong.MaxValue);
            BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(offset + 48), ulong.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(offset + 56), (uint)entry.Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(offset + 60), (uint)name.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(offset + 72), entry.Identity.Low);
            name.CopyTo(page, offset + 88); offset += length;
        }
        return page;
    }
    internal static int Run()
    {
        int passed = 0;
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException(name); passed++; Console.WriteLine("PASS ancestor reuse " + name); }
        void Failed(Model model, Action action, string name)
        { Check(Fixed(Catch(action)) && model.Validation!.Retired && model.Validation.Count == 0, name); }

        {
            int calls = 0; string? observed = null;
            string path = NtfsNamespaceAncestorValidation.ResolveLeaf(@"X:\SyntheticSnapshot\", "a/child",
                p => { calls++; observed = p; return FileAttributes.Directory; }, default);
            Check(calls == 1 && path == @"X:\SyntheticSnapshot\a\child" && observed == path, "production leaf helper queries exactly the target path");
            foreach (var attributes in new[] { FileAttributes.Archive, FileAttributes.Directory | FileAttributes.ReparsePoint })
                Check(Fixed(Catch(() => NtfsNamespaceAncestorValidation.ResolveLeaf(@"X:\SyntheticSnapshot\", "a/child", _ => attributes, default))),
                    "production leaf helper refuses non-directory or reparse attributes");
            Check(Fixed(Catch(() => NtfsNamespaceAncestorValidation.ResolveLeaf(@"X:\SyntheticSnapshot\", "", _ => FileAttributes.Directory, default))),
                "root never takes the reduced leaf path");
        }

        // Actual traversal equivalence, including empty EOF, multi-page root,
        // hardlink aliases, excluded links and poisoned cached size fields.
        using (var old = new Model(false))
        using (var candidate = new Model())
        {
            var a = Dir(1, 0, "a"); var b = Dir(2, 0, "b"); var child = Dir(3, 1, "child");
            var file = new NtfsSnapshotNamespaceEntry(4, 0, "one.bin", Id(100), FileAttributes.Archive, false);
            var alias = file with { Index = 5, Name = "two.bin" };
            var link = Dir(6, 0, "link") with { Reparse = true, Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint };
            foreach (var m in new[] { old, candidate })
            {
                m.Pages[""] = [Page(a, b, file), Page(alias, link)];
                m.Pages["a"] = [Page(child)];
            }
            var before = NtfsSnapshotNamespace.Read(old); var after = NtfsSnapshotNamespace.Read(candidate);
            Check(before.Report == after.Report && before.Entries.SequenceEqual(after.Entries), "complete inventory equals reference");
            Check(Enumerable.Range(0, before.Entries.Count).All(i => before.RelativePath(i) == after.RelativePath(i)), "all relative paths equal reference");
            Check(old.AttributesRead == 8 && candidate.AttributesRead == 4, "shared ancestors reduce only synthetic attribute calls 8 to 4");
            Check(old.Opened == 4 && candidate.Opened == 4 && old.IdentityChecks == candidate.IdentityChecks && old.TagChecks == candidate.TagChecks,
                "every directory retains actual open identity and tag checks");
            Check(old.Reads == candidate.Reads && candidate.Closed == 4 && after.Report.CompleteWithinBoundary && after.Report.CompletedDirectories == 4,
                "pages empty EOF and handle release stay complete");
            Check(after.Report.UniqueFileObjects == 1 && after.Report.ReparseEntries == 1 && !candidate.Calls.Any(c => c.Path == "link"),
                "hardlink aliases and reparse exclusion unchanged");
            candidate.Dispose(); Check(candidate.Validation!.Count == 0 && candidate.Validation.Retired, "namespace end clears all proofs");
        }
        // Measured callback counts for the three previously analytic examples.
        foreach (string shape in new[] { "flat", "shared", "chain" })
        using (var old = new Model(false))
        using (var candidate = new Model())
        {
            old.OpenOnce(Root, ""); candidate.OpenOnce(Root, "");
            if (shape == "shared")
                for (int i = 0; i < 100; i++)
                {
                    int index = i * 2 + 1; string name = "g" + i;
                    foreach (var m in new[] { old, candidate })
                    { m.OpenOnce(Dir(index, 0, name), name); m.OpenOnce(Dir(index + 1, index, "child"), name + "/child"); }
                }
            if (shape == "chain")
            {
                string path = "";
                for (int i = 1; i <= 64; i++)
                { path = path.Length == 0 ? "d" : path + "/d"; foreach (var m in new[] { old, candidate }) m.OpenOnce(Dir(i, i - 1, "d"), path); }
            }
            int dirs = shape == "flat" ? 1 : shape == "shared" ? 201 : 65;
            int attributes = shape == "flat" ? 1 : shape == "shared" ? 501 : 2145;
            Check(old.AttributesRead == attributes && candidate.AttributesRead == dirs, shape + " exact callback counts");
            Check(old.Opened == dirs && candidate.Opened == dirs && candidate.IdentityChecks == dirs && candidate.TagChecks == dirs && candidate.Closed == dirs,
                shape + " opens handle checks and close unchanged");
        }
        using (var m = new Model())
        {
            m.OpenOnce(Dir(2, 1, "child"), "missing/child");
            Check(m.AttributesRead == 3 && m.Validation!.Count == 0 && !m.Calls[0].Reused, "missing parent uses full path and cannot seed an unbound proof");
            m.OpenOnce(Root, ""); m.OpenOnce(Dir(1, 0, "missing"), "missing"); m.OpenOnce(Dir(2, 1, "child"), "missing/child");
            Check(m.Calls[^1].Reused && m.Validation!.Count == 3, "later real parent opens can establish the chain");
        }
        foreach (int capacity in new[] { 0, 1, 2, 8192 })
        using (var m = new Model(capacity: capacity))
        {
            m.OpenOnce(Root, "");
            int count = capacity == 8192 ? 8200 : 4;
            for (int i = 1; i <= count; i++) { string name = "d" + i; m.OpenOnce(Dir(i, 0, name), name); }
            Check(m.Validation!.Count == capacity && !m.Calls[^1].Reused && !m.Validation.Retired, "capacity " + capacity + " remains bounded and full fallback stays usable");
            Check(m.Opened == count + 1 && m.Closed == count + 1, "capacity " + capacity + " never skips opens");
        }
        foreach (int capacity in new[] { -1, 8193 })
            Check(Fixed(Catch(() => new Model(capacity: capacity))), "invalid capacity rejected");
        foreach (string invalid in new[] { "../x", "a//x", "a/", "a\\x", "a:x", "/x", "a/./x", "a\nx", new string('x', 256) })
        using (var m = new Model())
            Failed(m, () => m.OpenOnce(Dir(1, 0, "x"), invalid), "lexical path rejected before reuse");
        foreach (string fault in new[] { "parent", "name", "index", "root", "volume", "sequence", "high", "entry-reparse", "entry-file", "contradiction" })
        using (var m = new Model())
        {
            m.OpenOnce(Root, ""); m.OpenOnce(Dir(1, 0, "a"), "a");
            var entry = Dir(2, 1, "child"); string path = "a/child";
            switch (fault)
            {
                case "parent": entry = entry with { ParentIndex = 0 }; break;
                case "name": entry = entry with { Name = "other" }; break;
                case "index": entry = entry with { ParentIndex = 2 }; break;
                case "root": entry = Root with { Identity = Id(99) }; path = ""; break;
                case "volume": entry = entry with { Identity = entry.Identity with { Volume = 8 } }; break;
                case "sequence": entry = entry with { Identity = new(7, 3, 0) }; break;
                case "high": entry = entry with { Identity = new(7, (1ul << 48) | 3, 1) }; break;
                case "entry-reparse": entry = entry with { Reparse = true }; break;
                case "entry-file": entry = entry with { Attributes = FileAttributes.Archive }; break;
                case "contradiction": entry = Dir(1, 0, "a") with { Identity = Id(99) }; path = "a"; break;
            }
            int calls = m.Calls.Count;
            Failed(m, () => m.OpenOnce(entry, path), fault + " cannot reuse or publish a proof");
            Check(m.Calls.Count == calls, fault + " rejected before native open");
        }
        foreach (string fault in new[] { "acl", "attributes", "reparse", "file", "identity", "tag", "closed-during-open", "open-exception" })
        using (var m = new Model())
        {
            m.OpenOnce(Root, "");
            switch (fault)
            {
                case "acl": m.Deny = true; break;
                case "attributes": m.FailAttributesAt = "a"; break;
                case "reparse": m.ReparsePath = "a"; break;
                case "file": m.FilePath = "a"; break;
                case "identity": m.WrongIdentity = Id(99); break;
                case "tag": m.HandleAttributes |= FileAttributes.ReparsePoint; break;
                case "closed-during-open": m.DuringOpen = () => m.CloseSource(); break;
                case "open-exception": m.DuringOpen = () => throw new IOException("SyntheticPrivateOpen"); break;
            }
            Failed(m, () => m.OpenOnce(Dir(1, 0, "a"), "a"), fault + " is fatal and retires prior proofs");
            Check(m.Opened == m.Closed, fault + " releases any opened handle");
        }
        using (var m = new Model())
        {
            m.OpenOnce(Root, "");
            Failed(m, () => m.Validation!.OpenDirectory(new object(), Dir(1, 0, "a"), "a", default).Dispose(), "cross-source generation cannot borrow proofs");
            Check(m.Opened == 1, "cross-source rejected before opening");
        }
        using (var first = new Model())
        using (var second = new Model())
        {
            first.OpenOnce(Root, ""); second.OpenOnce(Dir(1, 0, "a"), "a");
            Check(second.AttributesRead == 2 && !second.Calls[0].Reused && second.Validation!.Count == 0, "fresh namespace shares no proof even with same root identity");
        }
        foreach (string stage in new[] { "before", "open", "read", "after" })
        {
            using var stop = new CancellationTokenSource(); using var m = new Model(lifetime: stop.Token);
            if (stage == "before") stop.Cancel(); else m.OpenOnce(Root, "");
            Exception? error;
            if (stage == "open") m.DuringOpen = stop.Cancel;
            if (stage == "read")
            {
                using var cursor = m.OpenDirectory(Dir(1, 0, "a"), "a", stop.Token);
                m.DuringRead = stop.Cancel; error = Catch(() => cursor.Read(true, new byte[65536], stop.Token));
            }
            else
            {
                if (stage == "after") stop.Cancel();
                error = Catch(() => { using var cursor = m.OpenDirectory(Dir(1, 0, "a"), "a", stop.Token); });
            }
            Check(error is OperationCanceledException canceled && canceled.CancellationToken == stop.Token && m.Validation!.Retired && m.Validation.Count == 0,
                stage + " cancellation retires proofs and preserves caller token");
        }
        using (var m = new Model())
        {
            m.OpenOnce(Root, ""); m.DuringOpen = () => m.OpenOnce(Dir(2, 0, "b"), "b");
            Failed(m, () => m.OpenOnce(Dir(1, 0, "a"), "a"), "reentrant open retires in-flight publication");
            Check(m.Opened == m.Closed, "reentry leaks no handle");
        }
        foreach (bool disposeFailure in new[] { false, true })
        using (var m = new Model())
        {
            m.OpenOnce(Root, ""); var cursor = m.OpenDirectory(Dir(1, 0, "a"), "a", default);
            if (disposeFailure)
            { m.FailClose = true; Check(Fixed(Catch(cursor.Dispose)), "close errors are sanitized"); cursor.Dispose(); }
            else
            { m.FailRead = true; Check(cursor.Read(true, new byte[65536], default).Error == 5, "read errors stay errors"); cursor.Dispose(); }
            Check(m.Validation!.Retired && m.Validation.Count == 0 && m.Opened == m.Closed, "cursor failure retires state and closes once");
        }
        using (var m = new Model())
        {
            m.OpenOnce(Root, ""); m.CloseSource();
            Failed(m, () => m.OpenOnce(Dir(1, 0, "a"), "a"), "source close rejects retained namespace reference");
        }
        using (var m = new Model(capacity: 1))
        {
            m.OpenOnce(Root, ""); m.ReparsePath = "unproven";
            Failed(m, () => m.OpenOnce(Dir(2, 1, "child"), "unproven/child"), "full fallback still rejects an unproven reparse ancestor");
            Check(m.Calls[^1].Reused == false && m.Opened == 1, "fallback fails before opening a target through a link");
        }
        using (var m = new Model())
        {
            m.OpenOnce(Root, ""); m.Active = false;
            Failed(m, () => m.OpenOnce(Dir(1, 0, "a"), "a"), "active source check prevents reuse even without an explicit invalidation callback");
        }
        using (var m = new Model())
        {
            m.OpenOnce(Root, ""); m.OpenOnce(Dir(1, 0, "a"), "a"); m.OpenOnce(Dir(2, 1, "child"), "a/child");
            for (int i = 0; i < 100; i++) { m.OpenOnce(Root, ""); m.OpenOnce(Dir(1, 0, "a"), "a"); }
            Check(m.Validation!.Count == 3 && m.Opened == 203 && m.Closed == 203, "repeated opens retain three proofs while repeating all real handle checks");
        }
        using (var m = new Model())
        {
            var cursor = m.OpenDirectory(Root, "", default); m.Dispose();
            Check(Fixed(Catch(() => cursor.Read(true, new byte[65536], default))) && m.Reads == 0, "retained cursor cannot read after namespace disposal");
            cursor.Dispose(); Check(m.Opened == m.Closed, "retained cursor still releases after namespace disposal");
        }
        return passed;
    }
}
