using System.Runtime.InteropServices;

namespace DiskAtlas;

// These surfaces sample window coordinates. Preserved pixels are invalid when
// their geometry changes, even when Windows considers the client content reusable.
internal static class ResizePixelPolicy
{
    internal static void Prepare(Control control, ref Message message)
    {
        // The top-level window must retain its last complete client pixels
        // until the later geometry transaction presents the new surfaces.
        // Never add or remove the caller's copy policy for that window.
        if (control is Form || message.Msg != 0x0046 || message.LParam == IntPtr.Zero ||
            control.FindForm()?.WindowState == FormWindowState.Minimized) return;
        var position = Marshal.PtrToStructure<WindowPosition>(message.LParam);
        bool moved = (position.Flags & 0x0002) == 0 &&
            (position.X != control.Left || position.Y != control.Top);
        bool resized = (position.Flags & 0x0001) == 0 &&
            (position.Width != control.Width || position.Height != control.Height);
        if (!resized && !moved) return;
        position.Flags |= 0x0100; // SWP_NOCOPYBITS: invalidate instead of blitting old pixels.
        Marshal.StructureToPtr(position, message.LParam, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Handle, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
}
