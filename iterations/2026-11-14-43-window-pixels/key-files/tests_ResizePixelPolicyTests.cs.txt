using System.Runtime.InteropServices;
using DiskAtlas;

// Synthetic WINDOWPOS buffers and owned, unshown controls only. No desktop
// input, screenshots, raw storage or claims about visual resize acceptance.
internal static class ResizePixelPolicyTests
{
    internal static int Run()
    {
        int passed = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new Panel { Bounds = new Rectangle(10, 20, 80, 50) };
                Check(control, 0x0046, Position(11, 20, 80, 50), true);
                Check(control, 0x0046, Position(10, 21, 80, 50), true);
                Check(control, 0x0046, Position(10, 20, 81, 50), true);
                Check(control, 0x0046, Position(10, 20, 80, 51), true);
                Check(control, 0x0046, Position(10, 20, 80, 50), false);
                Check(control, 0x0046, Position(999, 999, 80, 50, 0x0002), false);
                Check(control, 0x0046, Position(10, 20, 999, 999, 0x0001), false);
                Check(control, 0x0046, Position(999, 999, 999, 999, 0x0003), false);
                Check(control, 0x0046, Position(999, 999, 81, 50, 0x0002), true);
                Check(control, 0x0046, Position(11, 20, 999, 999, 0x0001), true);
                Check(control, 0x0046, Position(11, 20, 80, 50, 0x0010 | 0x0004 | 0x0200), true);
                Check(control, 0x0046, Position(10, 20, 80, 50, 0x0010 | 0x0004), false);
                Check(control, 0x0046, Position(11, 20, 80, 50, 0x0100), true);
                Check(control, 0x0047, Position(11, 20, 80, 50), false);
                Check(control, 0x0005, Position(11, 20, 80, 50), false);
                var empty = Message.Create(IntPtr.Zero, 0x0046, IntPtr.Zero, IntPtr.Zero);
                ResizePixelPolicy.Prepare(control, ref empty);
                Assert(empty.Msg == 0x0046 && empty.LParam == IntPtr.Zero, "NullBufferUnchanged");
                using var form = new Form();
                form.Bounds = new Rectangle(10, 20, 280, 180);
                Check(form, 0x0046, Position(11, 20, 280, 180), false);
                Check(form, 0x0046, Position(10, 20, 281, 180), false);
                Check(form, 0x0046, Position(10, 20, 281, 180, 0x0100), false);
                form.Controls.Add(control);
                form.WindowState = FormWindowState.Minimized;
                Check(control, 0x0046, Position(11, 20, 80, 50), false);
                Assert(!form.IsHandleCreated && !control.IsHandleCreated, "NoNativeWindowsCreated");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure != null) throw new InvalidOperationException("ResizePixelPolicyCheckFailed", failure);
        return passed;

        void Check(Control control, int messageId, WindowPosition before, bool expected)
        {
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<WindowPosition>());
            try
            {
                Marshal.StructureToPtr(before, buffer, false);
                var message = Message.Create(new IntPtr(17), messageId, new IntPtr(19), buffer);
                ResizePixelPolicy.Prepare(control, ref message);
                var after = Marshal.PtrToStructure<WindowPosition>(buffer);
                uint expectedFlags = expected ? before.Flags | 0x0100u : before.Flags;
                Assert(after.Flags == expectedFlags, "OnlyRequiredCopyPolicyChanged");
                Assert(after.Window == before.Window && after.InsertAfter == before.InsertAfter &&
                    after.X == before.X && after.Y == before.Y && after.Width == before.Width && after.Height == before.Height &&
                    message.HWnd == new IntPtr(17) && message.Msg == messageId && message.WParam == new IntPtr(19) &&
                    message.LParam == buffer, "OtherFieldsPreserved");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        void Assert(bool condition, string code)
        {
            if (!condition) throw new InvalidOperationException(code);
            passed++;
        }
    }

    private static WindowPosition Position(int x, int y, int width, int height, uint flags = 0) => new()
    {
        Window = new IntPtr(23), InsertAfter = new IntPtr(29), X = x, Y = y,
        Width = width, Height = height, Flags = flags
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        internal IntPtr Window, InsertAfter;
        internal int X, Y, Width, Height;
        internal uint Flags;
    }
}
