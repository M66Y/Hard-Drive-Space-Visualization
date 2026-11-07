using DiskAtlas;

internal static class ErrorReportRegression
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "AsukaLens-error-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Deliberately sensitive-looking synthetic payloads; none may reach the report.
            const string privateText = "private-account/private-document secret-marker@example.invalid";
            Exception error;
            try { throw new IOException(privateText, new Exception(privateText)); }
            catch (Exception caught) { error = caught; }
            error.Data[privateText] = privateText;
            var first = ErrorReport.Write(error, root);
            Check(first.Saved && first.Path != null && File.Exists(first.Path), "Successful save was not reported");
            var text = File.ReadAllText(first.Path!);
            Check(!text.Contains("private-") && !text.Contains("secret-marker") && !text.Contains("Stack") &&
                !text.Contains(root) && !first.UserMessage.Contains("private-"), "Private exception material escaped");
            Check(text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>line.Split('=')[0]).SequenceEqual(
                new[] {"App","Schema","TimeUtc","Category","Code"}),"Report field whitelist changed");
            Check(first.Category=="IO" && first.Code.StartsWith("0x"),"Diagnostic category/code missing");
            Console.WriteLine("PASS error report whitelists fields and omits message, stack, data, inner exceptions and private paths");

            var unexpected = ErrorReport.Write(new SecretException(), root);
            Check(unexpected.Saved && unexpected.Category=="Unexpected", "Unknown exception not safely classified");
            Check(!File.ReadAllText(unexpected.Path!).Contains(nameof(SecretException)), "Custom exception type leaked");
            var entries = Directory.GetFiles(root);
            Check(entries.Length==1 && new FileInfo(entries[0]).Length<512,"Repeated reports grow unbounded or leave temporary files");
            Console.WriteLine("PASS error report avoids custom exception getters and retains one bounded report");

            var before = File.ReadAllBytes(first.Path!);
            using (var locked = new FileStream(first.Path!, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = ErrorReport.Write(error,root);
                Check(!failed.Saved && failed.Path==null && failed.UserMessage.Contains("未能写入") && !failed.UserMessage.Contains("已保存"),
                    "Failed write reported success");
            }
            Check(File.ReadAllBytes(first.Path!).SequenceEqual(before) && Directory.GetFiles(root).Length==1,"Failed replace lost existing report");
            string blocked=Path.Combine(root,"blocked"); File.WriteAllText(blocked,"fixture");
            Check(!ErrorReport.Write(error,blocked).Saved,"File-as-directory failure was not handled");
            Console.WriteLine("PASS error report save failure stays nonfatal, preserves prior file and reports the true outcome");
            Console.WriteLine("All 3 error report regression groups passed."); return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string prefix=Path.Combine(Path.GetFullPath(Path.GetTempPath()),"AsukaLens-error-tests-");
            if (!Path.GetFullPath(root).StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup");
            Directory.Delete(root,recursive:true);
        }
    }

    private sealed class SecretException : Exception
    {
        public override string Message => throw new Exception("Message must not be evaluated");
        public override string? StackTrace => throw new Exception("Stack must not be evaluated");
        public override string ToString() => throw new Exception("ToString must not be evaluated");
    }
}
