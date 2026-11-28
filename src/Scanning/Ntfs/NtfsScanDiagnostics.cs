using System.Diagnostics;

namespace DiskAtlas.NtfsPrototype;

internal enum NtfsDiagnosticPhase { SourceValidation, VssSetup, VssCreation, SnapshotValidation, IndexFinalization }
[Flags]
internal enum NtfsSupplementReason : byte
{
    ResidentAllocation = 1, Compressed = 2, Sparse = 4, IncompleteMetadata = 8,
    IncompleteLinks = 16, ModifiedConflict = 32, UnsupportedRecord = 64
}

// Opt-in test instrumentation. Fixed-size aggregates only, never names or identities.
internal sealed class NtfsScanDiagnostics
{
    private readonly object gate = new();
    private readonly Func<double> milliseconds;
    private readonly double?[] phases = new double?[5];
    private readonly bool[] started = new bool[5];
    private readonly int[] reasons = new int[128];
    private int objects;

    internal NtfsScanDiagnostics(Func<double>? milliseconds = null)
    {
        var clock = Stopwatch.StartNew();
        this.milliseconds = milliseconds ?? (() => clock.Elapsed.TotalMilliseconds);
    }

    internal IDisposable Measure(NtfsDiagnosticPhase phase)
    {
        lock (gate)
        {
            int index = (int)phase;
            NtfsScanDiagnosticReport.Require(index >= 0 && index < phases.Length && !started[index]);
            double start = milliseconds();
            NtfsScanDiagnosticReport.Require(double.IsFinite(start) && start >= 0);
            started[index] = true;
            return new Measurement(this, index, start);
        }
    }

    internal void RecordSupplement(NtfsSupplementReason reason)
    {
        lock (gate)
        {
            int mask = (int)reason;
            NtfsScanDiagnosticReport.Require(mask is > 0 and < 128 && objects < 1_048_576);
            reasons[mask]++; objects++;
        }
    }

    internal NtfsScanDiagnosticReport Snapshot()
    {
        lock (gate) return new((double?[])phases.Clone(), objects, (int[])reasons.Clone());
    }

    private sealed class Measurement(NtfsScanDiagnostics owner, int index, double start) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.gate)
            {
                double elapsed = owner.milliseconds() - start;
                NtfsScanDiagnosticReport.Require(double.IsFinite(elapsed) && elapsed is >= 0 and <= 780_000);
                owner.phases[index] = elapsed;
            }
        }
    }
}

internal sealed record NtfsScanDiagnosticReport(double?[] PhaseMilliseconds, int SupplementObjects, int[] ReasonCombinations)
{
    internal void Validate(bool requireComplete = false)
    {
        Require(PhaseMilliseconds is { Length: 5 } && ReasonCombinations is { Length: 128 });
        Require(SupplementObjects is >= 0 and <= 1_048_576);
        foreach (double? value in PhaseMilliseconds)
            Require(value.HasValue ? double.IsFinite(value.Value) && value.Value is >= 0 and <= 780_000 : !requireComplete);
        long sum = 0;
        for (int i = 0; i < ReasonCombinations.Length; i++)
        {
            int count = ReasonCombinations[i];
            Require(count is >= 0 and <= 1_048_576 && (i != 0 || count == 0));
            sum += count;
        }
        Require(sum == SupplementObjects);
    }

    // Success-only payload: fixed dimensions prevent hostile allocation requests.
    internal static void Write(BinaryWriter writer, NtfsScanDiagnosticReport report)
    {
        report.Validate(requireComplete: true);
        foreach (double? value in report.PhaseMilliseconds) writer.Write(value!.Value);
        writer.Write(report.SupplementObjects);
        foreach (int count in report.ReasonCombinations) writer.Write(count);
    }

    internal static NtfsScanDiagnosticReport Read(BinaryReader reader)
    {
        var phases = new double?[5];
        for (int i = 0; i < phases.Length; i++) phases[i] = reader.ReadDouble();
        int count = reader.ReadInt32();
        var reasons = new int[128];
        for (int i = 0; i < reasons.Length; i++) reasons[i] = reader.ReadInt32();
        var report = new NtfsScanDiagnosticReport(phases, count, reasons);
        report.Validate(requireComplete: true);
        return report;
    }

    internal static void Require(bool condition)
    {
        if (!condition) throw new InvalidDataException("ScanDiagnosticInvalid");
    }
}
