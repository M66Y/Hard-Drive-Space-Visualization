using DiskAtlas;
using System.Collections;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

internal static class MemoryBenchmark
{
    public static int Run()
    {
        string workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string archive = Path.Combine(workspace, "空间透镜-1.2.0-Windows.zip");
        if (!File.Exists(archive))
        {
            Console.WriteLine("SKIP historical memory comparison: the optional v1.2 baseline archive is not retained.");
            return 0;
        }
        var context = new AssemblyLoadContext("DiskAtlas-memory-baseline", isCollectible: true);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            using var stream = zip.GetEntry("DiskAtlas.dll")!.Open();
            using var buffer = new MemoryStream(); stream.CopyTo(buffer); buffer.Position = 0;
            var type = context.LoadFromStream(buffer).GetType("DiskAtlas.DiskEntry")!;
            var constructor = type.GetConstructors().Single();
            var children = type.GetProperty("Children")!;
            string path = Path.Combine(workspace, "synthetic-memory-sample", new string('p', 90));
            const int folders = 125, filesPerFolder = 1600;
            string[] extensions = [".txt", ".bin", ".jpg"];
            object Legacy()
            {
                object root = constructor.Invoke([path, true, null]); var list = (IList)children.GetValue(root)!;
                for (int d = 0; d < folders; d++)
                {
                    string folderPath = Path.Combine(path, $"folder-{d:0000}");
                    object dir = constructor.Invoke([folderPath, true, root]); list.Add(dir);
                    var entries = (IList)children.GetValue(dir)!;
                    for (int f = 0; f < filesPerFolder; f++)
                        entries.Add(constructor.Invoke([Path.Combine(folderPath, $"file-{d * filesPerFolder + f:000000}" + extensions[f % 3]), false, dir]));
                }
                return root;
            }
            object Current()
            {
                var root = new DiskEntry(path, true);
                for (int d = 0; d < folders; d++)
                {
                    var dir = DiskEntry.FromName($"folder-{d:0000}", true, root, "文件夹"); root.MutableChildren.Add(dir);
                    for (int f = 0; f < filesPerFolder; f++)
                        dir.MutableChildren.Add(DiskEntry.FromName($"file-{d * filesPerFolder + f:000000}" + extensions[f % 3], false, dir, extensions[f % 3]));
                }
                return root;
            }
            // Warm reflection/property metadata before attributing retained heap to the models.
            object warm = constructor.Invoke([path, true, null]); _ = children.GetValue(warm); warm = null!;
            var old = new List<long>(); var current = new List<long>();
            for (int repeat = 0; repeat < 3; repeat++) { old.Add(Measure(Legacy)); current.Add(Measure(Current)); }
            long baseline = old.Order().ElementAt(1), optimized = current.Order().ElementAt(1);
            Console.WriteLine($"MEMORY synthetic {folders * filesPerFolder:N0} files / {folders} folders; full GC, retained managed model only, median of 3");
            Console.WriteLine($"v1.2: {baseline:N0} B; v1.4 base nodes (excludes storage records): {optimized:N0} B; reduction {(1 - (double)optimized / baseline):P1}");
            if (optimized >= baseline) throw new Exception("Retained model memory did not improve");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { context.Unload(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(Func<object> build)
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        object model = build();
        long retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        GC.KeepAlive(model);
        return retained;
    }
}
