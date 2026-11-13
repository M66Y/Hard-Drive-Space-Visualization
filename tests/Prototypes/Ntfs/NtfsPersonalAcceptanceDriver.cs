using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// Explicit apphost entry only. Scope arrives on inherited stdin, never in argv
// or a file. The real production bridge handles elevation, peer binding and cleanup.
internal static class NtfsPersonalAcceptanceDriver
{
    internal static void Validate(NtfsPersonalSnapshotSource source, DiskEntry tree, CancellationToken token)
    {
        var verified = NtfsPersonalAcceptance.Verify(tree, (relative, directory, cancellation) =>
            source.ReadNative(relative, directory, cancellation), token, source.Limits);
        if (!verified.NamespaceComplete || !verified.AllObjectsMatched)
            throw new IOException("PersonalAcceptanceIncomplete");
    }

    internal static int Run(bool cancelOnDiscovery = false)
    {
        string stage = "Preparing";
        try
        {
            if (Path.GetFileName(Environment.ProcessPath) != "DiskAtlas.Tests.exe")
                return Report("failed", stage, "AppHostRequired", true, null);
            string scope = ReadScope(Console.In);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var progress = new DirectProgress(p =>
            {
                string? observed = p.CurrentPath switch
                {
                    "等待管理员授权" => "Elevation",
                    "建立只读快照" => "Snapshot",
                    "核对目录范围" => "Namespace",
                    "读取 NTFS 元信息" => "RawRead",
                    "补查特殊文件大小" => "Native",
                    "整理空间分布" => "ProjectionAndVerification",
                    _ => null
                };
                if (observed != null && observed != stage)
                {
                    stage = observed;
                    Console.Error.WriteLine(observed);
                }
                if (cancelOnDiscovery && observed == "Namespace") cancellation.Cancel();
            });
            var result = FastScanBridge.Scan(scope, progress, cancellation.Token);
            if (cancelOnDiscovery) return Report("failed", stage, "CancellationExpected", true, null);
            if (result.Source == null || result.Checkpoint != null || result.Root.UnknownAllocationFiles != 0)
                return Report("failed", stage, "ResultContract", false, null);
            bool partial = result.Source.ExcludedReparseEntries > 0;
            if (result.Root.Incomplete != partial || result.Skipped != result.Source.ExcludedReparseEntries ||
                result.Issues.Count != Math.Min(result.Skipped, 2000) || result.AllocationIssues.Count != 0)
                return Report("failed", stage, "ResultContract", true, null);
            return Report(partial ? "passed-partial" : "passed", "Complete", "None", true, true, partial: partial);
        }
        catch (FastScanUnavailableException error)
        { return Report("failed", error.Context?.Stage.ToString() ?? stage, error.Code.ToString(), error.CleanupConfirmed, null, error.Context); }
        catch (OperationCanceledException)
        { Report("cancelled", stage, "Cancelled", true, null); return cancelOnDiscovery ? 0 : 1; }
        catch (FastScanWireException)
        { return Report("failed", stage, "InvalidScope", true, null); }
        catch
        { return Report("failed", stage, "AcceptanceFailed", false, null); }
    }

    internal static string ReadScope(TextReader input)
    {
        try
        {
            var characters = new List<char>();
            for (int i = 0; i <= 32767; i++)
            {
                int value = input.Read();
                if (value is -1 or '\n') return FastScanWire.NormalizeRoot(new string(characters.ToArray()).TrimEnd('\r'));
                if (i == 32767) break;
                characters.Add((char)value);
            }
        }
        catch (FastScanWireException) { throw; }
        catch { throw new FastScanWireException(); }
        throw new FastScanWireException();
    }
    private static int Report(string outcome, string stage, string code, bool cleanup, bool? verified, FastScanFailureContext? context = null, bool partial = false)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            revision = "personal-ntfs-acceptance-v3", outcome, stage, code, detail = context?.Detail.ToString() ?? "None", native_error = context?.NativeError,
            cleanup_confirmed = cleanup, namespace_complete = partial ? false : verified,
            included_scope_verified = verified, reparse_exclusions_declared = partial,
            all_included_objects_matched = verified, raw_budget_respected = verified
        }));
        return outcome is "passed" or "passed-partial" ? 0 : 1;
    }
    private sealed class DirectProgress(Action<ScanProgress> deliver) : IProgress<ScanProgress>
    { public void Report(ScanProgress value) => deliver(value); }
}
