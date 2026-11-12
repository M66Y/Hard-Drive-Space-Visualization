namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsSpecialCaseResult(string Kind, long LogicalBytes, long AllocatedBytes, string Source);
internal sealed record NtfsSpecialLookupReport(int Objects, int CompressedObjects, int SparseObjects, int UnsupportedObjects,
    int ResolvedObjects, int FailedObjects, int LeaseCalls, int HandlesOpened, int SnapshotReads,
    IReadOnlyList<NtfsSpecialCaseResult> Cases);

// Correctness evidence only: no paths, object IDs, native messages or timing.
internal static class NtfsSpecialEvidence
{
    internal static NtfsSpecialLookupReport Run(IReadOnlyList<(string Kind, NtfsLookupItem Item)> supported,
        IReadOnlyList<NtfsLookupItem> unsupported, Action<bool, string> check)
    {
        void Require(bool condition, string code)
        { check(condition, code); if (!condition) throw new InvalidOperationException(code); }
        string[] kinds = ["CompressedZero", "CompressedRandom", "SparseHole", "SparseWritten"];
        Require(supported.Count is > 0 and <= 4 && supported.Count + unsupported.Count <= 16, "SpecialEvidenceBound");
        Require(supported.Select(item => item.Kind).Distinct().Count() == supported.Count &&
            supported.All(item => kinds.Contains(item.Kind)), "SpecialEvidenceKinds");
        var items = supported.Select(item => item.Item).Concat(unsupported).ToArray();
        int calls = 0, handles = 0, snapshots = 0;
        bool allSuccessful = true;
        var batch = NtfsSizeLookup.ResolveSpecialWithLease(items, (item, token) =>
        {
            calls++;
            var result = NtfsSpecialLease.Read(item.Volume, item.Record, item.Path, token);
            handles += result.HandlesOpened; snapshots += result.SnapshotReads;
            allSuccessful &= result.Success;
            return result;
        });
        Require(batch.Items.Count == items.Length && batch.Metrics.EligibleObjects == supported.Count &&
            batch.Metrics.ResolvedObjects == supported.Count && batch.Metrics.FailedObjects == 0 &&
            batch.Metrics.UnsupportedObjects == unsupported.Count && batch.Complete == (unsupported.Count == 0), "SpecialEvidenceBatch");
        Require(allSuccessful && calls == supported.Count && handles == calls && snapshots == 2 * calls &&
            batch.Metrics.NativeCalls == calls, "SpecialEvidenceWorkBudget");
        var output = batch.Items.ToDictionary(item => (item.Volume, item.Reference));
        var cases = new List<NtfsSpecialCaseResult>();
        foreach (var (kind, input) in supported)
        {
            Require(input.Record.LookupReasons == (kind.StartsWith("Compressed", StringComparison.Ordinal)
                ? NtfsLookupReason.Compressed : NtfsLookupReason.Sparse), "SpecialEvidenceReasonMatchesKind");
            var result = output[(input.Volume, input.Record.Reference)];
            Require(result.AllocatedSource == NtfsSizeSource.HandleLookup && result.LogicalSource == NtfsSizeSource.Record &&
                result.Code == "SpecialLookup" && result.LogicalBytes.HasValue && result.AllocatedBytes.HasValue, "SpecialEvidenceSource");
            Require(NativeStorage.TryRead(input.Path, false, File.GetAttributes(input.Path), out var reference, out _) &&
                reference != null && !reference.WritableHandleOpen && reference.Identity == new FileIdentity(input.Volume, input.Record.Reference, 0) &&
                result.LogicalBytes == reference.LogicalBytes && result.AllocatedBytes == reference.AllocatedBytes, "SpecialEvidenceMatchesReference");
            cases.Add(new(kind, result.LogicalBytes!.Value, result.AllocatedBytes!.Value, result.AllocatedSource.ToString()));
        }
        foreach (var input in unsupported)
        {
            var result = output[(input.Volume, input.Record.Reference)];
            Require(result.AllocatedBytes == null && result.AllocatedSource == NtfsSizeSource.Unknown, "SpecialEvidenceUnsupportedRemainsUnknown");
        }
        int compressed = supported.Count(item => item.Item.Record.LookupReasons == NtfsLookupReason.Compressed);
        return new(items.Length, compressed, supported.Count - compressed, unsupported.Count, supported.Count, 0,
            calls, handles, snapshots, cases.AsReadOnly());
    }
}
