using System.Runtime.InteropServices;

namespace DiskAtlas;

// Retains wallpaper in the compositor, behind every
// child HWND. It never captures UI, changes root styles, or owns input.
internal sealed class CompositionBackdrop : IDisposable
{
    internal enum State { Waiting, Active, Failed, Disposed }
    private readonly Form owner;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private IntPtr root, device, target, visual, content;
    private Source? source;
    private PixelSurface? published;
    private bool disposed, publishing;
    internal State Status { get; private set; }
    internal bool Active => Status == State.Active;
    internal int Publications { get; private set; }
    internal int Generation { get; private set; }
    internal Size PublishedSize { get; private set; }
    internal IntPtr SourceHandle => source?.Handle ?? IntPtr.Zero;
    internal int FailureCode { get; private set; }
    internal Action<string>? TestFault { get; set; }

    internal CompositionBackdrop(Form owner)
    {
        this.owner = owner;
        owner.HandleDestroyed += Destroyed;
    }

    internal bool Publish(PixelSurface? frame)
    {
        if (disposed || publishing || Environment.CurrentManagedThreadId != thread || owner.IsDisposed || owner.Disposing) return false;
        if (frame == null || !frame.Native || !owner.IsHandleCreated)
        {
            // An invalid frame cannot leave an old image attached to the root.
            Detach(); return false;
        }
        if (Status == State.Failed) return false;
        publishing = true;
        IntPtr expectedRoot = owner.Handle;
        int expectedGeneration = 0;
        try
        {
            Trace("begin");
            if (root != expectedRoot) { Detach(); root = expectedRoot; Generation++; }
            expectedGeneration = Generation;
            if (device == IntPtr.Zero) Initialize(expectedRoot, expectedGeneration);
            Trace("initialized");
            if (ReferenceEquals(frame, published)) return Active;
            var size = frame.Bitmap.Size;
            TestFault?.Invoke("publish");
            Guard(expectedRoot, expectedGeneration);
            if (!frame.TryPublishLayered(source!.Handle)) throw new COMException("BackdropUpload", Marshal.GetHRForLastWin32Error());
            Trace("uploaded");
            if (!IsWindowVisible(source.Handle)) ShowWindow(source.Handle, 4); // stays cloaked
            Check(DwmGetWindowAttribute(source.Handle, 14, out int cloaked, sizeof(int)));
            if (cloaked == 0) throw new COMException("BackdropCloakLost");
            TestFault?.Invoke("commit");
            Guard(expectedRoot, expectedGeneration);
            Check(Call<NoArgs>(device, 3)(device));
            Trace("committed");
            Check(Call<NoArgs>(device, 4)(device));
            Guard(expectedRoot, expectedGeneration);
            Trace("ready");
            published = frame;
            PublishedSize = size;
            Publications++;
            Status = State.Active;
            return true;
        }
        catch (RetiredPublication)
        {
            Detach();
            Status = disposed ? State.Disposed : State.Waiting;
            return false;
        }
        catch (Exception e) when (e is ExternalException or InvalidOperationException or ArgumentException or OutOfMemoryException or DllNotFoundException or EntryPointNotFoundException)
        {
            FailureCode = e.HResult;
            Detach(); Status = State.Failed;
            return false;
        }
        finally { publishing = false; }
    }
    private static void Trace(string stage)
    {
        if (AppContext.TryGetSwitch("AsukaLens.TraceComposition", out bool trace) && trace)
            Console.WriteLine("COMPOSITION_STAGE " + stage);
    }

    private sealed class RetiredPublication : Exception { }
    private void Guard(IntPtr expectedRoot, int expectedGeneration)
    {
        if (disposed || root != expectedRoot || Generation != expectedGeneration ||
            !owner.IsHandleCreated || owner.Handle != expectedRoot)
            throw new RetiredPublication();
    }
    private void Initialize(IntPtr expectedRoot, int expectedGeneration)
    {
        TestFault?.Invoke("device");
        Guard(expectedRoot, expectedGeneration);
        Guid iid = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
        Check(DCompositionCreateDevice(IntPtr.Zero, ref iid, out device));
        source = new Source(root);
        Guard(expectedRoot, expectedGeneration);
        int cloak = 1;
        Check(DwmSetWindowAttribute(source.Handle, 13, ref cloak, sizeof(int)));
        TestFault?.Invoke("target");
        Guard(expectedRoot, expectedGeneration);
        Check(Call<CreateTarget>(device, 6)(device, root, 0, out target)); // below children
        Check(Call<CreateObject>(device, 7)(device, out visual));
        Check(Call<CreateWindowSurface>(device, 11)(device, source.Handle, out content));
        Check(Call<SetObject>(visual, 15)(visual, content));
        Check(Call<SetObject>(target, 3)(target, visual));
    }

    private void Destroyed(object? sender, EventArgs e)
    {
        Detach();
        if (!disposed) Status = State.Waiting;
    }
    private void Detach()
    {
        Generation++;
        if (target != IntPtr.Zero)
        {
            // Best effort on device loss; release our exact tree either way.
            _ = Call<SetObject>(target, 3)(target, IntPtr.Zero);
            if (device != IntPtr.Zero) _ = Call<NoArgs>(device, 3)(device);
        }
        Release(ref content); Release(ref visual); Release(ref target); Release(ref device);
        source?.DestroyHandle(); source = null;
        root = IntPtr.Zero; published = null; PublishedSize = Size.Empty;
        if (!disposed && Status != State.Failed) Status = State.Waiting;
    }
    public void Dispose()
    {
        if (disposed) return;
        if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("BackdropThread");
        disposed = true;
        owner.HandleDestroyed -= Destroyed;
        Detach(); Status = State.Disposed;
    }
    private static void Release(ref IntPtr value)
    {
        var owned = value; value = IntPtr.Zero;
        if (owned != IntPtr.Zero) Marshal.Release(owned);
    }
    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    // Slots follow the Windows SDK IDCompositionDevice/Target/Visual vtables.
    private static T Call<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArgs(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateObject(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTarget(IntPtr self, IntPtr hwnd, int topmost, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateWindowSurface(IntPtr self, IntPtr hwnd, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetObject(IntPtr self, IntPtr value);

    private sealed class Source : NativeWindow
    {
        internal Source(IntPtr root)
        {
            CreateHandle(new CreateParams { Caption = "AsukaLens owned backdrop", Parent = root,
                Style = unchecked((int)0x80000000), ExStyle = 0x80000 | 0x80 | 0x8000000,
                X = -32000, Y = -32000, Width = 1, Height = 1 });
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; }
            if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }
            base.WndProc(ref m);
        }
    }
    [DllImport("dcomp.dll")] private static extern int DCompositionCreateDevice(IntPtr dxgi, ref Guid iid, out IntPtr device);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
}
