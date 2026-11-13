namespace DiskAtlas.NtfsPrototype;

// Pure path/binding validation; no volume handle, COM, snapshot or user data.
internal static class NtfsPersonalSnapshotSourceTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("Personal source " + name); passed++; Console.WriteLine("PASS personal source " + name); }
        void Rejected(Action action, string name)
        {
            bool rejected = false;
            try { action(); }
            catch (NtfsVssProbeException error)
            { rejected = error.Code is "VssPersonalPath" or "VssSnapshotNative" && error.Message == error.Code && error.InnerException == null; }
            Check(rejected, name);
        }
        Check(NtfsPersonalSnapshotSource.NormalizeSelectedPath(@"d:\") == @"D:\", "drive root normalized without changing selected scope");
        Check(NtfsPersonalSnapshotSource.NormalizeSelectedPath(@"D:\Synthetic\目录\") == @"D:\Synthetic\目录", "local directory and Unicode spelling retained");
        foreach (string path in new[] { "", "D:", "D:/Synthetic", "Synthetic", @"\Synthetic", @"\\server\share", @"\\?\D:\Synthetic", @"\\.\D:",
            @"D:\..\outside", @"D:\.\Synthetic", @"D:\a\\b", @"D:\a\b:stream", @"D:\a\b.", @"D:\a\b ", @"D:\a\*.tmp", @"D:\a\?", @"D:\a|b", "D:\\a\u0085b", "D:\\a\nb" })
            Rejected(() => NtfsPersonalSnapshotSource.NormalizeSelectedPath(path), "invalid ambiguous device network stream or traversal path rejected");
        Rejected(() => NtfsPersonalSnapshotSource.NormalizeSelectedPath("D:\\" + new string('a', 256)), "overlong component rejected");
        Rejected(() => NtfsPersonalSnapshotSource.NormalizeSelectedPath("D:\\" + new string('a', 32768)), "overlong selected path rejected");
        Check(NtfsPersonalSnapshotSource.RelativeSegments("Synthetic/f.bin", false).SequenceEqual(new[] { "Synthetic", "f.bin" }), "private namespace paths have one spelling");
        Check(NtfsPersonalSnapshotSource.RelativeSegments("", true).Length == 0, "selected root represented only as directory");
        foreach (string path in new[] { "", "../f", "d/../f", "d//f", "d/", @"d\f", @"C:\Synthetic", "/Synthetic", "d/x:stream", "d/\u0085" })
            Rejected(() => NtfsPersonalSnapshotSource.RelativeSegments(path, false), "invalid relative namespace request rejected");
        const string volume = @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\";
        Check(NtfsVssNativeSession.IsVolumeGuid(volume) && NtfsVssNativeSession.IsVolumeGuid(volume.ToUpperInvariant()), "source volume GUID namespace validated exactly");
        foreach (string value in new[] { @"D:\", volume + "extra", volume.TrimEnd('\\'), @"\\?\Volume{00000000-0000-0000-0000-000000000000}\", @"\\.\PhysicalDrive0" })
            Check(!NtfsVssNativeSession.IsVolumeGuid(value), "caller path cannot masquerade as canonical volume GUID");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool cleanCancel = false;
        try { _ = NtfsPersonalSnapshotSource.Create(@"D:\Synthetic", cancelled.Token); }
        catch (NtfsPersonalSnapshotOpenException error)
        { cleanCancel = error.Code == "VssCancelled" && error.CleanupConfirmed && error.NativeError == 0 && error.Message == error.Code && error.InnerException == null; }
        Check(cleanCancel, "cancel before preparation opens no source and preserves cleanup fact");
        var closeFailed = NtfsPersonalSnapshotSource.WrapOpenFailure(new NtfsVssProbeException("VssCreateSnapshot", -7), () => throw new IOException("private-marker"));
        Check(!closeFailed.CleanupConfirmed && closeFailed.Code == "VssCreateSnapshot" && closeFailed.NativeError == -7 && closeFailed.Message == closeFailed.Code && closeFailed.InnerException == null,
            "cleanup callback exception cannot erase snapshot ownership uncertainty or leak exception text");
        var stillPresent = NtfsPersonalSnapshotSource.WrapOpenFailure(new IOException("private-marker"), () => false);
        Check(!stillPresent.CleanupConfirmed && stillPresent.Code == "VssPersonalNative" && stillPresent.NativeError == 0, "unconfirmed cleanup remains failure with a sanitized category");
        var closed = NtfsPersonalSnapshotSource.WrapOpenFailure(new OperationCanceledException("private-marker"), () => true);
        Check(closed.CleanupConfirmed && closed.Code == "VssCancelled" && closed.Message == closed.Code && closed.InnerException == null, "cancelled preparation preserves confirmed source teardown");
        return passed;
    }
}
