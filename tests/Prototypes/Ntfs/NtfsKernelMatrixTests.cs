using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiskAtlas;

namespace DiskAtlas.NtfsPrototype;

// Pure table/decoder observations only: no filesystem writes, handles or VHD access.
internal static class NtfsKernelMatrixTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS kernel matrix check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS kernel matrix " + name);
        }
        void Rejected(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, name);
        }
        var cases = NtfsKernelMatrix.Cases;
        Check(NtfsKernelMatrix.Revision == "kernel-matrix-v1", "fixture revision is explicit");
        Check(cases.Count == 21 && cases.Select(x => x.CaseId).Distinct(StringComparer.Ordinal).Count() == 21, "twenty-one unique cases are fixed");
        Check(cases.Count <= NtfsKernelMatrix.MaximumObjects && cases.Count * NtfsKernelMatrix.ReadStages.Count <= NtfsKernelMatrix.MaximumRequests,
            "fixed cases stay below object and request budgets");
        Check(NtfsKernelMatrix.ReadStages.SequenceEqual(new[] { "Initial", "Reopened", "Flushed" }), "three observation histories have fixed names and order");
        Check(cases.Count(x => x.Stage == "A") == 9 && cases.Count(x => x.Stage == "B") == 6 && cases.Count(x => x.Stage == "C") == 6,
            "name, hard-link and named-stream factors have separate case groups");
        foreach (string kind in new[] { "Ordinary", "Compressed", "Sparse" })
        {
            var typed = cases.Where(x => x.Kind == kind).ToArray();
            Check(typed.Length == 7 && typed.Select(x => x.LogicalBytes).Distinct().Count() == 1, "same-kind cases keep default EOF fixed");
            Check(typed.Where(x => x.Stage == "A").Select(x => x.NameLength).SequenceEqual(new[] { 8, 24, 64 }), "name experiment includes all three controlled lengths");
            foreach (string phase in new[] { "B", "C" })
                Check(typed.Where(x => x.Stage == phase).Select(x => x.NameLength).SequenceEqual(new[] { 8, 64 }), "additional-factor experiments use matching short and long controls");
        }
        foreach (var item in cases)
        {
            Check(Regex.IsMatch(item.CaseId, @"\A[ABC]-(Ordinary|Compressed|Sparse)-(8|24|64)\z"), "case identity is public controlled text");
            Check(item.HardLink == (item.Stage == "B") && item.NamedStream == (item.Stage == "C") && !(item.HardLink && item.NamedStream),
                "each case adds at most one independent factor");
            string name = NtfsKernelMatrix.FileName(item.NameLength);
            Check(name.Length == item.NameLength && name.All(char.IsAscii) && name.IndexOfAny(['/', '\\', ':']) < 0,
                "generated basename has exact ASCII length without path syntax");
        }
        Rejected(() => NtfsKernelMatrix.FileName(255), "unplanned lengths are rejected");
        bool cleaned = false;
        Check(NtfsKernelMatrix.CleanupCapture(0, () => cleaned = true) == "EmptyRemoved" && cleaned,
            "empty owned capture is removed with an explicit successful status");
        foreach (int count in new[] { 1, 6 })
        {
            cleaned = false;
            Check(NtfsKernelMatrix.CleanupCapture(count, () => cleaned = true) == "Retained" && !cleaned,
                "retained samples are never passed to cleanup");
        }
        Check(NtfsKernelMatrix.CleanupCapture(0, () => throw new IOException("SyntheticIO")) == "CleanupFailed",
            "cleanup IO failure remains a status without replacing collection evidence");
        Check(NtfsKernelMatrix.CleanupCapture(0, () => throw new UnauthorizedAccessException("SyntheticAccess")) == "CleanupFailed",
            "cleanup access failure remains a status without replacing collection evidence");
        foreach (int count in new[] { -1, 7 })
        {
            cleaned = false;
            Rejected(() => NtfsKernelMatrix.CleanupCapture(count, () => cleaned = true), "invalid capture count is rejected");
            Check(!cleaned, "invalid capture count performs no cleanup");
        }
        var item0 = cases[0];
        ulong reference = NtfsRecordFixtures.Reference(52, 3);
        var storage = new FileStorageInfo(new(41, reference, 0), 4096, 1, false, 17, new DateTime(2020, 1, 1));
        NtfsKernelRead Wrap(byte[] payload)
        {
            var output = new byte[payload.Length + 12];
            BinaryPrimitives.WriteUInt64LittleEndian(output, reference);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), (uint)payload.Length);
            payload.CopyTo(output, 12);
            return new(output, output.Length, storage, storage, 512, 4096, 3, 1);
        }
        byte[] raw = NtfsRecordFixtures.File();
        var observed = Wrap(raw);
        var row = NtfsKernelMatrix.Observe(item0, "Initial", observed);
        Check(row.Status == "Decoded" && row.Code == "Decoded" && row.VisibleNames == 1, "valid record has controlled successful name count");
        Check(row.ProtectedTails == 2 && row.RestoredTails == 0 && row.Sectors == 2 && row.UsedBytes is > 0 and <= 1024,
            "protected-tail diagnostics are bounded direct observations");
        Check(row.CaseId == "A-Ordinary-8-Initial" && row.Stage == "Initial", "row identity binds phase, kind, length and read history");
        Check(raw.SequenceEqual(NtfsRecordFixtures.File()), "observation does not mutate source bytes");
        var restored = (byte[])raw.Clone(); Array.Copy(restored, 50, restored, 510, 2); Array.Copy(restored, 52, restored, 1022, 2);
        var restoredRow = NtfsKernelMatrix.Observe(item0, "Reopened", Wrap(restored));
        Check(restoredRow.Status == "Decoded" && restoredRow.ProtectedTails == 0 && restoredRow.RestoredTails == 2, "fully restored control keeps strict successful semantics");
        var mismatch = (byte[])raw.Clone(); NtfsRecordFixtures.U16(mismatch, 510, 0x1234);
        var failed = NtfsKernelMatrix.Observe(item0, "Flushed", Wrap(mismatch));
        Check(failed.Status == "Invalid" && failed.Code == "FixupMismatch" && failed.VisibleNames == -1,
            "strict failure does not publish unvalidated names");
        Check(failed.ProtectedTails == 1 && failed.RestoredTails == 0, "partial protection is observed without accepting mixed tails");
        var usedOverflow = (byte[])raw.Clone(); NtfsRecordFixtures.U32(usedOverflow, 24, uint.MaxValue);
        Check(NtfsKernelMatrix.Observe(item0, "Initial", Wrap(usedOverflow)).UsedBytes == -1, "untrusted oversized used length maps to an explicit unknown sentinel");
        var badUsa = (byte[])raw.Clone(); NtfsRecordFixtures.U16(badUsa, 4, ushort.MaxValue);
        var usaRow = NtfsKernelMatrix.Observe(item0, "Initial", Wrap(badUsa));
        Check(usaRow.Status == "Invalid" && usaRow.ProtectedTails == 0 && usaRow.RestoredTails == 0, "bad USA bounds are never traversed for diagnostics");
        Rejected(() => NtfsKernelMatrix.Observe(item0, "Other", observed), "unplanned observation stages are rejected");
        Rejected(() => NtfsKernelMatrix.Observe(item0 with { CaseId = "Injected" }, "Initial", observed), "unplanned cases are rejected");
        Rejected(() => NtfsKernelMatrix.Observe(item0, "Initial", observed with { ReturnedBytes = 11 }), "short IOCTL header is infrastructure failure");
        Rejected(() => NtfsKernelMatrix.Observe(item0, "Initial", observed with { ReturnedBytes = observed.Output.Length + 1 }), "reported bytes cannot exceed the returned buffer");
        Rejected(() => NtfsKernelMatrix.Observe(item0, "Initial", observed with { Sector = 4096 }), "unverified geometry is infrastructure failure");
        Rejected(() => NtfsKernelMatrix.Observe(item0, "Initial", observed with { After = storage with { LogicalBytes = 18 } }), "changing native EOF is infrastructure failure");
        Rejected(() => NtfsKernelMatrix.Observe(item0, "Initial", observed with { After = storage with { Identity = new(41, reference + 1, 0) } }), "changing native identity is infrastructure failure");
        Rejected(() => NtfsKernelMatrix.Observe(item0, "Initial", observed with { After = storage with { WritableHandleOpen = true } }), "writer evidence cannot become a decoder observation");
        foreach (var invalid in new[] { storage with { LogicalBytes = -1 }, storage with { AllocatedBytes = -1 },
            storage with { LinkCount = 0 }, storage with { Modified = null } })
            Rejected(() => NtfsKernelMatrix.Observe(item0, "Initial", observed with { Before = invalid, After = invalid }),
                "stable but incomplete native context is infrastructure failure");
        var wrongSequence = (byte[])raw.Clone(); NtfsRecordFixtures.U16(wrongSequence, 16, 4);
        Rejected(() => NtfsKernelMatrix.Observe(item0, "Initial", Wrap(wrongSequence)), "decoded reused sequence is infrastructure failure");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new NtfsKernelMatrixReport(NtfsKernelMatrix.Revision, 1, 1, 0, 1, 1, 0, 0, [failed])));
        Check(json.RootElement.EnumerateObject().Select(x => x.Name).SequenceEqual(new[] { "Revision", "Objects", "Requests", "Decoded", "DecodeFailures", "Captured", "CaptureFailures", "CaptureLimited", "Rows" }),
            "report exposes exactly the controlled aggregate fields");
        Check(json.RootElement.GetProperty("Rows")[0].EnumerateObject().Select(x => x.Name).SequenceEqual(new[] { "CaseId", "Stage", "Kind", "NameLength", "Status", "Code", "ProtectedTails", "RestoredTails", "Sectors", "UsedBytes", "VisibleNames" }),
            "row schema contains no paths, raw names or identities");
        return passed;
    }
}
