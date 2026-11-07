using DiskAtlas;
using System.Reflection;

internal static class DiagnosticsUiRegression
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static T Control<T>(Control parent, string name) where T : Control => (T)parent.Controls.Find(name, true).Single();
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static Task Scan(MainForm form, string path) => (Task)typeof(MainForm).GetMethod("ScanFolder", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [path])!;
    private static async Task Wait(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!predicate()) { if (DateTime.UtcNow > end) throw new Exception("UI condition timed out"); await Task.Delay(20); }
    }

    public static int Preview(string root)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                string fixture = Path.Combine(root, "demo-files"); Directory.CreateDirectory(fixture);
                File.WriteAllBytes(Path.Combine(fixture, "demo.txt"), new byte[4096]);
                using var service = new DiagnosticService(Path.Combine(root, "app-data")); service.Ready.GetAwaiter().GetResult();
                service.Failure(DiagnosticKind.BackgroundImportFailed, new IOException("synthetic text must not be logged"));
                using var form = new MainForm(fixture, backgroundSettingsPath: Path.Combine(root, "background.dat"), diagnostics: service)
                    { Text = "AsukaLens · 诊断验收" };
                Application.Run(form);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }

    public static int Run(bool virtual96)
    {
        string root = Path.Combine(Path.GetTempPath(), "AsukaLens-diagnostics-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.SetHighDpiMode(virtual96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using var service = new DiagnosticService(Path.Combine(root, "app-data"));
            using var host = new Form { Text = "AsukaLens diagnostic test host", Size = new Size(500, 250) };
            host.Shown += async (_, _) =>
            {
                try
                {
                    await service.Ready;
                    string scanRoot = Path.Combine(root, "synthetic-private-folder"); Directory.CreateDirectory(scanRoot);
                    File.WriteAllText(Path.Combine(scanRoot, "private-document.txt"), "synthetic");
                    using (var main = new MainForm(null, diagnostics: service))
                    {
                        main.Show(host); await Scan(main, scanRoot); await Scan(main, scanRoot);
                        Field<ActionButton>(main, "diagnosticsButton").PerformClick();
                        var dialog = Field<DiagnosticsDialog>(main, "diagnosticsWindow");
                        await Wait(() => !dialog.IsBusy && dialog.Snapshot != null);
                        Check(dialog.Owner == main && main.Enabled, "Diagnostics blocked scan/cancel through a modal owner");
                        Field<ActionButton>(main, "diagnosticsButton").PerformClick();
                        Check(ReferenceEquals(dialog, Field<DiagnosticsDialog>(main, "diagnosticsWindow")), "Duplicate diagnostics windows");
                        Field<bool>(main, "forceFullNext");
                        typeof(MainForm).GetField("forceFullNext", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, true);
                        await Scan(main, scanRoot);
                        await dialog.RefreshAsync();
                        var snapshot = dialog.Snapshot!;
                        Check(snapshot.Records.Count(r => r.Kind == DiagnosticKind.ScanCompleted) == 3 && snapshot.Records.Count(r => r.Kind == DiagnosticKind.ScanStarted) == 3,
                            "Scan lifecycle events missing or duplicated");
                        Check(snapshot.Records.Any(r => r.Reason == RefreshReason.FirstScan) && snapshot.Records.Any(r => r.Reason == RefreshReason.Forced) &&
                            snapshot.Records.Any(r => r.Method is RefreshMethod.MetadataVerification or RefreshMethod.ChangeJournal), "Refresh method/reason not captured");
                        Check(!string.Join('\n', snapshot.Records.Select(DiagnosticCodec.Encode)).Contains("private"), "Scan paths leaked");
                        var grid = Control<DataGridView>(dialog, "diagnosticRecords");
                        Check(grid.Rows.Count > 0 && grid.Rows.Cast<DataGridViewRow>().All(r => r.Cells[0].Value is false) && dialog.SelectedPreview() == null, "Default export selection not empty");
                        grid.Rows[0].Cells[0].Value = true;
                        string preview = dialog.SelectedPreview()!;
                        service.Failure(DiagnosticKind.BackgroundImportFailed, new IOException("private-marker")); await service.FlushAsync();
                        using (var export = new DiagnosticExportDialog(preview))
                        {
                            export.Show(dialog); Check(export.Preview == preview, "Export snapshot changed"); export.Close();
                        }
                        Check(!Directory.GetFiles(root, "*.txt").Any(), "Preview cancellation wrote output");
                        await DiagnosticService.ExportAsync(Path.Combine(root, "approved-export.txt"), preview, CancellationToken.None);
                        Check(File.ReadAllText(Path.Combine(root, "approved-export.txt")) == preview && preview.Contains("Records=1"), "Export did not preserve selected snapshot");
                        await dialog.RefreshAsync(); Control<ComboBox>(dialog, "levelFilter").SelectedIndex = 3;
                        Check(grid.Rows.Count == 1 && ((DiagnosticRecord)grid.Rows[0].Tag!).Level == DiagnosticLevel.Error, "Severity filter failed");
                        Control<ComboBox>(dialog, "levelFilter").SelectedIndex = 0;
                        Console.WriteLine("PASS diagnostics UI entry, scan lifecycle, reasons, default selection, filters and fixed preview");

                        using (var editor = new ProblemNoteDialog(service, null))
                        {
                            editor.Shown += (_, _) =>
                            {
                                editor.TitleInput.Text = "Synthetic UI note"; editor.BodyInput.Text = "private-marker only in local note";
                                Control<Button>(editor, "saveNote").PerformClick();
                            };
                            Check(editor.ShowDialog(dialog) == DialogResult.OK, "Note UI save failed");
                        }
                        await dialog.RefreshAsync();
                        Check(Control<DataGridView>(dialog, "problemNotes").Rows.Count == 1, "Note not displayed");
                        Check(!Control<Button>(dialog, "editNote").Enabled && !Control<Button>(dialog, "deleteNote").Enabled,
                            "Note actions enabled without an explicit selection");
                        await dialog.ToggleAsync(false); Check(!service.Enabled && !Control<CheckBox>(dialog, "loggingEnabled").Checked, "UI switch inconsistent");
                        await dialog.ClearAsync(); Check(dialog.Snapshot!.Records.Count == 0 && (await service.NotesAsync()).Count == 1, "Clear removed notes or retained log");
                        await dialog.ToggleAsync(true);
                        Console.WriteLine("PASS diagnostics UI note editor, persistent toggle and runtime-only clear");

                        Theme.SetAccent(Color.SteelBlue); dialog.ApplyTheme();
                        Check(Control<Button>(dialog, "previewExport").ForeColor == Theme.Accent, "Diagnostic theme did not adapt");
                        dialog.Size = dialog.MinimumSize; dialog.PerformLayout();
                        foreach (string name in new[] { "loggingEnabled", "refreshDiagnostics", "previewExport", "clearDiagnostics", "diagnosticRecords" })
                        {
                            var control = Control<Control>(dialog, name); var rectangle = dialog.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                            Check(control.Visible && rectangle.Left >= 0 && rectangle.Right <= dialog.ClientSize.Width && rectangle.Bottom <= dialog.ClientSize.Height,
                                "Narrow DPI layout clipped " + name);
                        }
                        Check(dialog.DeviceDpi == (virtual96 ? 96 : main.DeviceDpi), "Unexpected diagnostics DPI");
                        using var bitmap = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        Console.WriteLine($"PASS diagnostics UI narrow layout, render and theme at {dialog.DeviceDpi} DPI");
                        dialog.Close();
                    }
                    // Keep the host message loop alive while testing closing an operation's owner.
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (var cancelled = new MainForm(null, diagnostics: service)
                    {
                        ScanRunner = (_, _, token, _) => { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }
                    })
                    {
                        cancelled.Show(host); var task = Scan(cancelled, scanRoot); Field<ActionButton>(cancelled, "cancel").PerformClick(); await task; cancelled.Close();
                    }
                    using (var incomplete = new MainForm(null, diagnostics: service)
                    {
                        ScanRunner = (path, _, _, _) => new DiskScanner().Scan(path, null, CancellationToken.None) with { Skipped = 1 }
                    })
                    { incomplete.Show(host); await Scan(incomplete, scanRoot); incomplete.Close(); }
                    using (var failed = new MainForm(null, diagnostics: service)
                    {
                        ScanRunner = (_, _, _, _) => { release.Task.GetAwaiter().GetResult(); throw new IOException("private-marker"); }
                    })
                    {
                        failed.Show(host); var task = Scan(failed, scanRoot); failed.Close(); release.SetResult(); await task;
                    }
                    var outcomes = await service.SnapshotAsync();
                    Check(outcomes.Records.Any(r => r.Kind == DiagnosticKind.ScanCancelled) && outcomes.Records.Any(r => r.Kind == DiagnosticKind.ScanIncomplete) &&
                        outcomes.Records.Any(r => r.Kind == DiagnosticKind.ScanFailed && r.Category == ErrorCategory.IO), "Terminal outcome logging incomplete");
                    using (var image = new Bitmap(10, 10))
                    using (var background = new BackgroundDialog(image, BackgroundView.Default, Theme.DefaultAccent, new Size(100, 100),
                        Path.Combine(root, "background.dat"), reportFailure: (kind, error) => service.Failure(kind, error)))
                    { background.Show(host); await background.ImportImageAsync(Path.Combine(root, "missing.png")); background.Close(); }
                    Check((await service.SnapshotAsync()).Records.Any(r => r.Kind == DiagnosticKind.BackgroundImportFailed), "Background failure not integrated");
                    Console.WriteLine("PASS diagnostics UI incomplete/cancel/failure and background error integration");
                    using (var closing = new DiagnosticsDialog(service))
                    { closing.Show(host); closing.Close(); await Task.Delay(350); Check(closing.IsDisposed, "Close left diagnostics alive"); }
                    Console.WriteLine("PASS diagnostics UI pending read closes without late control writes");
                }
                catch (Exception ex) { failure = ex; }
                finally { Theme.SetAccent(null); host.Close(); }
            };
            Application.Run(host);
        });
        try
        {
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(45))) throw new Exception("Diagnostics UI timed out");
            if (failure != null) throw new Exception("Diagnostics UI failed", failure);
            Console.WriteLine("All 5 diagnostics UI regression groups passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "AsukaLens-diagnostics-ui-"), StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe cleanup");
            if (Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) throw new Exception("Redirected fixture");
            Directory.Delete(root, true);
        }
    }
}
