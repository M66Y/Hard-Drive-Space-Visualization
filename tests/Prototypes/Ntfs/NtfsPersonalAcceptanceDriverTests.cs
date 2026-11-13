namespace DiskAtlas.NtfsPrototype;

internal static class NtfsPersonalAcceptanceDriverTests
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("Acceptance input contract"); count++; }
        foreach (string input in new[] { "B:\\Synthetic\n", "B:\\Synthetic\r\n", "B:\\Synthetic", "B:\\Synthetic\nnot-another-request" })
            Check(NtfsPersonalAcceptanceDriver.ReadScope(new StringReader(input)) == @"B:\Synthetic");
        foreach (string input in new[] { "", "\n", @"\\remote\share", @"B:\..\x", @"B:\x:stream", "\"B:\\Synthetic\"", new string('x',32768) })
        {
            bool rejected = false;
            try { _ = NtfsPersonalAcceptanceDriver.ReadScope(new StringReader(input)); }
            catch (FastScanWireException error) { rejected = error.InnerException == null && !error.Message.Contains("PRIVATE"); }
            Check(rejected);
        }
        {
            bool sanitized = false;
            try { _ = NtfsPersonalAcceptanceDriver.ReadScope(new FailingInput()); }
            catch (FastScanWireException error) { sanitized = error.InnerException == null && !error.Message.Contains("PRIVATE"); }
            Check(sanitized);
        }
        foreach (var pair in new[] { ("NamespaceEntryBudget", FastScanFailureDetail.NamespaceEntryLimit),
            ("NamespaceNameBudget", FastScanFailureDetail.NamespaceNameLimit), ("NamespacePathBudget", FastScanFailureDetail.NamespacePathLimit),
            ("NamespaceQueryBudget", FastScanFailureDetail.NamespaceQueryLimit) })
            Check(NtfsPersonalScan.ClassifyDetail(new NtfsSnapshotNamespaceException(pair.Item1)) == pair.Item2);
        Check(NtfsPersonalScan.ClassifyDetail(new NtfsSnapshotNamespaceException("PRIVATE_UNKNOWN")) == FastScanFailureDetail.NamespaceValidation);
        Check(NtfsPersonalScan.ClassifyDetail(new IOException("PRIVATE_UNKNOWN")) == FastScanFailureDetail.UnexpectedPipelineFailure);
        return count;
    }
    private sealed class FailingInput : TextReader
    { public override int Read() => throw new IOException("PRIVATE_INPUT_MUST_NOT_ESCAPE"); }
}
