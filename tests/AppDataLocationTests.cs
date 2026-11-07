using DiskAtlas;

internal static class AppDataLocationTests
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "AsukaLens-data-location-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); passed++; Console.WriteLine("PASS " + name); }
        try
        {
            var normal = AppDataLocation.Resolve(null);
            Check(!normal.IsIsolated && normal.BackgroundPath == BackgroundSettings.PathForUser, "default background location remains compatible");
            string directory = Path.Combine(root, "中文 空格"); Directory.CreateDirectory(directory);
            var isolated = AppDataLocation.Resolve(directory + Path.DirectorySeparatorChar);
            Check(isolated.IsIsolated && isolated.Root == directory, "explicit Unicode directory normalized");
            Check(isolated.BackgroundPath == Path.Combine(directory, "background.dat"), "background shares the isolated data root");
            foreach (string bad in new[] { "", " ", "relative", "C:relative", Path.GetPathRoot(root)!, Path.Combine(root, "missing") })
            {
                bool rejected = false;
                try { AppDataLocation.Resolve(bad); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "invalid override rejected without default fallback");
            }
            string file = Path.Combine(root, "file.txt"); File.WriteAllText(file, "synthetic");
            bool fileRejected = false;
            try { AppDataLocation.Resolve(file); } catch (ArgumentException) { fileRejected = true; }
            Check(fileRejected, "file is not accepted as a data directory");
            using (var service = new DiagnosticService(isolated.Root))
            {
                service.Ready.GetAwaiter().GetResult();
                service.ReportErrorAsync(new IOException("synthetic private path")).GetAwaiter().GetResult();
            }
            Check(Directory.Exists(Path.Combine(directory, "diagnostics")) && File.Exists(Path.Combine(directory, "last-error.log")), "diagnostic and error persistence use the isolated root");
            Check(!Directory.Exists(Path.Combine(root, "missing")), "invalid directory was not created");
            Console.WriteLine($"PASS app data location checks: {passed}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
}
