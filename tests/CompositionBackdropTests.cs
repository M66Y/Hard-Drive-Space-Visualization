using DiskAtlas;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class CompositionBackdropTests
{
    internal static int Run(bool dpi96, bool preview = false)
    {
        int checks = 0; string? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9));
                using var form = new Harness();
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        await Task.Delay(120);
                        form.UpdateFrame();
                        Verify(form);
                        var common = form.Size;
                        for (int i = 0; i < 10; i++)
                        {
                            form.Size = i % 2 == 0 ? new Size(850, 620) : common;
                            await Task.Delay(20); Verify(form);
                        }
                        var source = form.Backdrop.SourceHandle;
                        int pubs = form.Backdrop.Publications;
                        form.UpdateFrame(); Verify(form);
                        Check(form.Backdrop.Publications == pubs + 1, "NewImagePublishedAtSameSize");
                        form.Combo.DroppedDown = true; await Task.Delay(30);
                        Check(form.Combo.DroppedDown, "NativeDropDownOpens"); form.Combo.DroppedDown = false;
                        form.Input.Focus(); form.Input.Text = "synthetic input";
                        Check(form.Input.Focused && form.Input.Text == "synthetic input", "NativeFocusInputPreserved");
                        form.Recreate(); await Task.Delay(60); form.UpdateFrame(); Verify(form);
                        Check(!IsWindow(source) && form.Backdrop.SourceHandle != IntPtr.Zero, "OldSourceDestroyedOnHandleRecreate");
                        if (preview) { Console.WriteLine("COMPOSITION_PREVIEW ready"); return; }
                        form.Backdrop.TestFault = stage => { if (stage == "commit") throw new InvalidOperationException("SyntheticFault"); };
                        source = form.Backdrop.SourceHandle; form.UpdateFrame();
                        Check(!form.Backdrop.Active && form.Backdrop.Status == CompositionBackdrop.State.Failed && !IsWindow(source), "CommitFailureDetachesStaleTreeAndSource");
                        Check(form.Visible && form.Input.Visible, "FailureRetainsNativeForm");
                        form.Close();
                    }
                    catch (Exception e) { failure = e is TestFailure f ? f.Code : $"Unexpected_{e.GetType().Name}_{e.HResult:X8}"; form.Close(); }
                };
                Application.Run(form);
                if (failure == null && !preview)
                {
                    foreach (string stage in new[] { "device", "target", "publish", "commit" })
                    {
                        using var f = new Harness();
                        f.Backdrop.TestFault = at => { if (at == stage) throw new InvalidOperationException("SyntheticFault"); };
                        _ = f.Handle; f.UpdateFrame();
                        Check(f.Backdrop.Status == CompositionBackdrop.State.Failed && f.Backdrop.SourceHandle == IntPtr.Zero, "PartialSetupFailureClean_" + stage);
                    }
                    foreach (string stage in new[] { "device", "target", "publish", "commit" })
                    {
                        using var f = new Harness(); _ = f.Handle;
                        f.Backdrop.TestFault = at => { if (at == stage) { f.Backdrop.TestFault=null; f.Recreate(); } };
                        f.UpdateFrame();
                        Check(!f.Backdrop.Active && f.Backdrop.SourceHandle == IntPtr.Zero && f.Backdrop.Status==CompositionBackdrop.State.Waiting,"RetiredSetupCannotPublish_"+stage);
                        f.UpdateFrame(); Verify(f);
                    }
                    var gdi = new List<int>(); var user = new List<int>();
                    for (int i = 0; i < 6; i++)
                    {
                        var f = new Harness(); _ = f.Handle; f.UpdateFrame(); Verify(f);
                        IntPtr source = f.Backdrop.SourceHandle; var session = f.Session;
                        f.Dispose();
                        Check(!IsWindow(source) && session.Bytes == 0 && f.Backdrop.Status == CompositionBackdrop.State.Disposed, "OwnedResourcesReleased");
                        using var p = Process.GetCurrentProcess();
                        gdi.Add(GetGuiResources(p.Handle, 0)); user.Add(GetGuiResources(p.Handle, 1));
                    }
                    Check(gdi[^1] <= gdi[0] && user[^1] <= user[0], "ClosedCyclesNoHandleGrowth");
                    Console.WriteLine($"COMPOSITION_RESOURCES gdi={string.Join(',',gdi)} user={string.Join(',',user)} system_bytes=not_measured");
                }
            }
            catch (Exception e) { failure = e is TestFailure f ? f.Code : $"Unexpected_{e.GetType().Name}_{e.HResult:X8}"; }
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(preview ? TimeSpan.FromHours(2) : TimeSpan.FromSeconds(90))) { Console.Error.WriteLine("FAIL CompositionTimeout"); return 1; }
        if (failure != null) { Console.Error.WriteLine("FAIL " + failure); return 1; }
        Console.WriteLine($"PASS CompositionBackdrop checks={checks} dpi={(dpi96 ? 96 : 144)}"); return 0;

        void Check(bool value, string code) { checks++; if (!value) throw new TestFailure(code); }
        void Verify(Harness f)
        {
            var b = f.Backdrop;
            Check(b.Active, "BackdropActive_" + b.FailureCode.ToString("X8"));
            Check(b.PublishedSize == f.ClientSize, "ExactClientSizePublished");
            Check((GetWindowLongPtr(f.Handle, -20).ToInt64() & 0x80000) == 0, "RootLayeredStyleUnchanged");
            Check(b.SourceHandle != IntPtr.Zero && IsWindow(b.SourceHandle), "OwnedSourceAlive");
            Check(DwmGetWindowAttribute(b.SourceHandle, 14, out int cloaked, 4) >= 0 && cloaked != 0, "SourceActuallyCloaked");
            long ex = GetWindowLongPtr(b.SourceHandle, -20).ToInt64();
            Check((ex & (0x80000 | 0x80 | 0x8000000)) == (0x80000 | 0x80 | 0x8000000), "SourceLayeredToolNoActivate");
            Check(f.Session.Bytes <= RenderSession.MaxBytes, "ApplicationBudgetHeld");
        }
    }
    private sealed class TestFailure(string code) : Exception { internal string Code => code; }
    private sealed class Harness : Form
    {
        internal RenderSession Session { get; } = new(true);
        internal CompositionBackdrop Backdrop { get; }
        internal ComboBox Combo = new() { Location = new(50, 70), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        internal TextBox Input = new() { Location = new(50, 125), Width = 270 };
        private PixelSurface? frame;
        internal Harness()
        {
            Text = "AsukaLens C57 · 自有背景合成验证"; Size = new(1100, 760);
            Backdrop = new CompositionBackdrop(this);
            Combo.Items.AddRange(["原生下拉框", "behind children"]); Combo.SelectedIndex = 0;
            Controls.Add(Combo); Controls.Add(Input);
            Controls.Add(new Button { Text = "原生按钮", Location = new(350, 70), Size = new(120, 36) });
            SetStyle(ControlStyles.ResizeRedraw, true);
        }
        internal void Recreate() => RecreateHandle();
        internal void UpdateFrame()
        {
            frame?.Dispose(); frame = PixelSurface.Create(ClientSize, DeviceDpi, Session);
            frame!.RenderOpaque(g =>
            {
                g.Clear(Color.FromArgb(249, 232, 224));
                using var brush = new SolidBrush(Color.FromArgb(175, 72, 65));
                g.FillEllipse(brush, ClientSize.Width - 260, 30, 200, 200);
                g.DrawString("C57 · 独立背景位于原生控件之后", Font, Brushes.Maroon, 50, 20);
                g.DrawRectangle(Pens.Brown, 1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
            });
            bool result = Backdrop.Publish(frame);
            if (!result) Console.WriteLine($"BACKDROP_DIAGNOSTIC native={frame.Native} handle={IsHandleCreated} disposing={Disposing} state={Backdrop.Status} code={Backdrop.FailureCode:X8}");
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated && Backdrop != null && ClientSize.Width > 0 && ClientSize.Height > 0) UpdateFrame(); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Intentional contradictory GDI colour: the actual screen must show
            // warm wallpaper, proving the compositor rather than a GDI probe.
            e.Graphics.Clear(Color.Magenta);
            if (!Backdrop.Active) e.Graphics.DrawString("C55 fallback (prototype)", Font, Brushes.Black, 30, 200);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Backdrop?.Dispose(); frame?.Dispose(); frame = null; }
            base.Dispose(disposing);
        }
    }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int bytes);
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int kind);
}
