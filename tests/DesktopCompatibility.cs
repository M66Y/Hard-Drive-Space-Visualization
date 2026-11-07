using DiskAtlas;
using System.Collections.Specialized;
using System.Reflection;
using System.Windows.Forms;

// Exercise the production drag/drop and copy handlers using synthetic files.
internal static class DesktopCompatibility
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "AsukaLens-desktop-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "Unicode 示例");
        string file = Path.Combine(directory, "sample.txt");
        Directory.CreateDirectory(directory);
        File.WriteAllText(file, "sample");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            using var form = new MainForm(null, backgroundSettingsPath: Path.Combine(root, "background.dat"));
            bool wroteClipboard = false;
            T Field<T>(string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            void Raise(string method, DragEventArgs args) => typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [args]);
            void Check(bool value, string message) { if (!value) throw new Exception(message); }
            DragEventArgs DropData(params string[] paths)
            {
                var data = new DataObject();
                var collection = new StringCollection(); collection.AddRange(paths);
                data.SetFileDropList(collection);
                return new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
            }
            form.Shown += async (_, _) =>
            {
                try
                {
                    var nonDirectory = DropData(file);
                    Raise("OnDragEnter", nonDirectory);
                    Check(nonDirectory.Effect == DragDropEffects.None, "File was accepted as a directory");
                    var multiple = DropData(directory, root);
                    Raise("OnDragEnter", multiple);
                    Check(multiple.Effect == DragDropEffects.None, "Multiple directories were accepted");
                    var single = DropData(directory);
                    Raise("OnDragEnter", single);
                    Check(single.Effect == DragDropEffects.Copy, "Unicode directory was rejected");
                    Raise("OnDragDrop", single);
                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    while (Field<ScanResult?>("scan") == null || !Field<DataGridView>("grid").Enabled || Field<List<MapItem>>("sortedRows").Count != 1)
                    {
                        if (DateTime.UtcNow >= deadline) throw new TimeoutException("Dropped directory scan did not complete");
                        await Task.Delay(25);
                    }
                    Check(Field<ScanResult>("scan").Root.FileCount == 1 && Field<ScanResult>("scan").Root.Size == 6, "Dropped directory statistics changed");
                    Console.WriteLine("PASS desktop FileDrop directory dispatch, Unicode paths and invalid selection rejection");

                    var grid = Field<DataGridView>("grid");
                    var item = Field<List<MapItem>>("sortedRows").Single();
                    typeof(MainForm).GetMethod("SelectItem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [item, true]);
                    grid.ContextMenuStrip!.Items.Cast<ToolStripItem>().Single(i => i.Text == "复制完整路径").PerformClick();
                    wroteClipboard = true;
                    Check(Clipboard.GetText() == file, "Copy path did not round-trip through the Windows clipboard");
                    Console.WriteLine("PASS desktop production copy-path action and Windows clipboard text round-trip");
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    // Never read the clipboard before our own copy operation.
                    try { if (wroteClipboard && Clipboard.GetText() == file) Clipboard.Clear(); }
                    catch (System.Runtime.InteropServices.ExternalException error) { failure ??= error; }
                    form.Close();
                }
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        try
        {
            thread.Start(); thread.Join();
            if (failure != null) throw failure;
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL desktop compatibility: " + error.GetType().Name); return 1; }
        finally { Directory.Delete(root, recursive: true); }
    }
}
