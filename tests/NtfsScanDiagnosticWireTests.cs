using System.Buffers.Binary;
using DiskAtlas.NtfsPrototype;

namespace DiskAtlas;

// Exercise the protocol with only an in-memory tree and byte streams.
internal static class NtfsScanDiagnosticWireTests
{
    private const string RootPath = @"B:\AsukaLens-Synthetic";

    internal static int Run()
    {
        int checks = 0;
        void Check(bool test, string name)
        {
            checks++;
            if (!test) throw new InvalidOperationException(name);
        }
        void RejectRead(FastScanWireReader reader, string name)
        {
            try { reader.Read(); }
            catch (FastScanWireException) { Check(true, name); return; }
            Check(false, name);
        }
        void Reject(byte[] bytes, bool detailed, bool afterReport, string name)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var reader = detailed ? new FastScanWireReader(stream, RootPath, acceptDiagnostics: true) : new FastScanWireReader(stream, RootPath);
            if (afterReport) Check(reader.Read().Kind == FastScanWireEventKind.Diagnostics, "valid report before " + name);
            RejectRead(reader, name);
            RejectRead(reader, "invalid transport stays terminal");
        }
        try
        {
            int[] reasons = new int[128]; reasons[3] = 1;
            var report = new NtfsScanDiagnosticReport([2.5, 1, 7, 0, 3], 1, reasons);
            byte[] diagnostics = Frames(s => FastScanWire.WriteDiagnostics(s, report, default));
            byte[] status = Frames(s => FastScanWire.WriteStatus(s, FastScanWireStage.Cleanup));
            byte[] tree = Frames(WriteTree);

            using (var stream = new MemoryStream([.. status, .. diagnostics, .. tree], writable: false))
            {
                var reader = new FastScanWireReader(stream, RootPath, acceptDiagnostics: true);
                Check(reader.Read() is { Kind: FastScanWireEventKind.Status, Stage: FastScanWireStage.Cleanup, Diagnostics: null },
                    "existing status may precede diagnostic");
                var observed = reader.Read();
                Check(observed is { Kind: FastScanWireEventKind.Diagnostics, CleanupConfirmed: false, Result: null } &&
                    observed.Diagnostics is { } detail && detail.PhaseMilliseconds.SequenceEqual(report.PhaseMilliseconds)
                    && detail.SupplementObjects == 1 && detail.ReasonCombinations.SequenceEqual(reasons), "diagnostic event round trip");
                var final = reader.Read();
                Check(final is { Kind: FastScanWireEventKind.Completed, CleanupConfirmed: true, Diagnostics: null } &&
                    final.Result?.Root is { FileCount: 1, Size: 17, AllocatedSize: 32 } && stream.Position == stream.Length,
                    "valid tree required for terminal success");
                RejectRead(reader, "success stays terminal");
            }
            using (var stream = new MemoryStream(tree, writable: false))
            {
                var normal = new FastScanWireReader(stream, RootPath);
                Check(normal.Read() is { Kind: FastScanWireEventKind.Completed, CleanupConfirmed: true, Diagnostics: null }, "original wire success");
            }
            Reject([.. diagnostics, .. tree], detailed: false, afterReport: false, "normal reader rejects report");
            Reject(tree, detailed: true, afterReport: false, "detailed reader rejects missing report");
            Reject([.. diagnostics, .. diagnostics, .. tree], true, true, "duplicate report");
            Reject([.. diagnostics, .. status, .. tree], true, true, "status between report and tree");
            Reject(diagnostics, true, true, "report alone cannot complete scan");
            int endOfTreeHeader = 12 + BinaryPrimitives.ReadInt32LittleEndian(tree.AsSpan(8));
            Reject([.. diagnostics, .. tree[..endOfTreeHeader], .. diagnostics, .. tree[endOfTreeHeader..]], true, true, "report inside tree");
            Reject([.. diagnostics, .. tree[..^1]], true, true, "partial tree cannot complete scan");

            foreach (int length in new[] { 1, 11, 12, diagnostics.Length - 1 })
                Reject(diagnostics[..length], true, false, "truncated diagnostic frame");
            byte[] corrupted = (byte[])diagnostics.Clone();
            BinaryPrimitives.WriteDoubleLittleEndian(corrupted.AsSpan(12), double.NaN);
            Reject([.. corrupted, .. tree], true, false, "malformed diagnostic duration");
            byte[] trailing = [.. diagnostics, 255];
            BinaryPrimitives.WriteInt32LittleEndian(trailing.AsSpan(8), trailing.Length - 12);
            Reject([.. trailing, .. tree], true, false, "unexpected diagnostic payload byte");

            foreach (var code in new[] { FastScanWireError.PermissionDenied, FastScanWireError.Cancelled, FastScanWireError.CleanupUnconfirmed })
            {
                bool cleaned = code != FastScanWireError.CleanupUnconfirmed;
                byte[] failure = Frames(s => FastScanWire.WriteFailure(s, code, cleaned));
                using var stream = new MemoryStream([.. status, .. failure, .. diagnostics, .. tree], writable: false);
                var reader = new FastScanWireReader(stream, RootPath, acceptDiagnostics: true);
                Check(reader.Read().Kind == FastScanWireEventKind.Status, "status before failure");
                var message = reader.Read();
                Check(message is { Kind: FastScanWireEventKind.Failure, Diagnostics: null, Result: null }
                    && message.Error == code && message.CleanupConfirmed == cleaned, "failure preserves cleanup without report");
                RejectRead(reader, "failure cannot become success");
            }
            byte[] transferFailure = Frames(s => FastScanWire.WriteFailure(s, FastScanWireError.ProtocolError, true));
            using (var stream = new MemoryStream([.. diagnostics, .. transferFailure], writable: false))
            {
                var reader = new FastScanWireReader(stream, RootPath, acceptDiagnostics: true);
                Check(reader.Read() is { Kind: FastScanWireEventKind.Diagnostics, CleanupConfirmed: false, Result: null },
                    "diagnostics are provisional");
                Check(reader.Read() is { Kind: FastScanWireEventKind.Failure, Error: FastScanWireError.ProtocolError,
                    CleanupConfirmed: true, Diagnostics: null, Result: null }, "tree validation failure after report");
                RejectRead(reader, "failed publication stays terminal");
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL NTFS scan diagnostic wire checks={checks}: {error.Message}");
            return 1;
        }
        Console.WriteLine($"PASS NTFS scan diagnostic wire checks={checks}; memory_streams_only=True");
        return 0;
    }

    private static byte[] Frames(Action<Stream> write)
    {
        using var stream = new MemoryStream();
        write(stream);
        return stream.ToArray();
    }

    private static void WriteTree(Stream stream)
    {
        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var root = new DiskEntry(RootPath, true) { Storage = new(new(1, (1UL << 48) | 5, 0), 0, 1, false, -1, modified), Modified = modified };
        var file = DiskEntry.FromName("demo.bin", false, root, ".bin");
        file.Storage = new(new(1, (1UL << 48) | 6, 0), 32, 1, false, 17, modified);
        file.Modified = modified;
        file.FileCount = root.FileCount = 1;
        file.Size = root.Size = 17;
        root.MutableChildren.Add(file);
        StorageMetrics.Calculate(root, default);
        FastScanWire.WriteResult(stream, root, TimeSpan.FromMilliseconds(20), new DateTimeOffset(modified));
    }
}
