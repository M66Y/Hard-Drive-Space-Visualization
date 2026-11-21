namespace DiskAtlas.ResizePrototype;

internal static class LiveResizePreview
{
    internal static int Run(string[] args)
    {
        if (args.Length > 4 || args.Length == 0 || args[0] != "--live-resize-preview" ||
            args.Skip(1).Any(value => value is not ("canvas" or "text" or "combo" or "grid" or "all" or "--minimum" or "--dpi-96")) ||
            args.Skip(1).Distinct(StringComparer.Ordinal).Count() != args.Length - 1 ||
            args.Skip(1).Count(value => !value.StartsWith("--", StringComparison.Ordinal)) > 1)
        { Console.Error.WriteLine("FAIL resize-prototype invocation"); return 2; }
        string modeName = args.Skip(1).FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal)) ?? "canvas";
        if (!Enum.TryParse<ResizeNativeMode>(modeName, true, out var mode)) return 2;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.SetHighDpiMode(args.Contains("--dpi-96") ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using var form = new LiveResizePrototypeForm(mode);
                int closeRequested = 0;
                form.Shown += (_, _) =>
                {
                    if (args.Contains("--minimum")) form.SetWindowSize(true);
                    Console.WriteLine("READY resize-prototype synthetic=True; F2=mode; F6=30s-program-driven-resize; F7=simulated-progress; no-personal-scan; settings-unchanged");
                    form.PrintInfo();
                    var input = new Thread(() =>
                    {
                        while (Volatile.Read(ref closeRequested) == 0)
                        {
                            string? command;
                            try { command = Console.ReadLine(); }
                            catch (IOException) { break; }
                            if (command == null) break; // EOF never closes a user-visible window.
                            string value = command.Trim().ToLowerInvariant();
                            if (value.Length > 32) continue;
                            try
                            {
                                form.BeginInvoke((Action)(() =>
                                {
                                    if (form.IsDisposed || form.Disposing) return;
                                    if (value == "close") form.Close();
                                    else if (value == "minimum") form.SetWindowSize(true);
                                    else if (value == "common") form.SetWindowSize(false);
                                    else if (value == "demo") form.ToggleDemo();
                                    else if (value == "info") form.PrintInfo();
                                    else if (value.StartsWith("mode ", StringComparison.Ordinal) &&
                                        Enum.TryParse<ResizeNativeMode>(value[5..], true, out var selected) && Enum.IsDefined(selected))
                                        form.SetMode(selected);
                                }));
                            }
                            catch (InvalidOperationException) { break; }
                        }
                    }) { IsBackground = true, Name = "ResizePrototypeInput" };
                    input.Start();
                };
                form.FormClosed += (_, _) => { Interlocked.Exchange(ref closeRequested, 1); form.PrintInfo(); };
                Application.Run(form);
                Console.WriteLine("CLOSED resize-prototype owned-resources-released-on-dispose; no-fixture-files");
            }
            catch (Exception failure) { error = failure; }
        }) { IsBackground = false, Name = "ResizePrototypeUI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error == null) return 0;
        Console.Error.WriteLine($"FAIL resize-prototype startup; type={error.GetType().Name}; code=0x{error.HResult:X8}");
        return 1;
    }
}
