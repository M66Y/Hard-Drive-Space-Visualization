using System.Runtime.InteropServices;

namespace DiskAtlas;

// Explicit candidate only. Windows owns the redirected image; the application
// keeps its existing DIB/control pipeline. No color key, opacity reduction,
// click-through, screenshot layer, redraw hold or system-wide setting.
internal sealed class OpaqueWindowRedirect : IDisposable
{
    internal const int Layered = 0x80000, Transparent = 0x20;
    internal enum State { Waiting, Active, ForeignStyle, SetupFailed, VerifyFailed, RollbackFailed, Disposed }
    internal interface INative
    {
        int Style(IntPtr handle);
        bool SetStyle(IntPtr handle,int value);
        bool SetOpaque(IntPtr handle);
        bool IsOpaque(IntPtr handle);
    }
    private readonly Form owner;
    private readonly INative native;
    private readonly bool postCreateHost;
    private bool ownsTransparencySetting;
    private IntPtr ownedHandle;
    private bool addedLayer, disposed;
    private long generation;
    internal State Status { get; private set; } = State.Waiting;
    internal bool Active => Status==State.Active && Current(ownedHandle) && native.IsOpaque(ownedHandle) &&
        (native.Style(ownedHandle)&(Layered|Transparent))==Layered;
    internal int Attempts { get; private set; }
    internal bool OwnsLayer => addedLayer;
    internal OpaqueWindowRedirect(Form owner,INative? native=null,bool postCreateHost=false)
    {
        this.owner=owner;this.native=native??new Native();this.postCreateHost=postCreateHost;
        // Form.CreateHandle finally re-applies CreateParams AFTER the public
        // event and OnHandleCreated override. Register the same intent with
        // its public API, so that later UpdateStyles keeps our layered bit.
        // This does not lower Opacity or introduce a transparency color key.
        if(postCreateHost && !owner.AllowTransparency && owner.Opacity==1d && owner.TransparencyKey==Color.Empty &&
            (!owner.IsHandleCreated || (this.native.Style(owner.Handle)&(Layered|Transparent))==0))
        { ownsTransparencySetting=true;owner.AllowTransparency=true; }
        if(!postCreateHost)owner.HandleCreated+=Created;
        owner.HandleDestroyed+=Destroyed;
        if(owner.IsHandleCreated)Created(owner,EventArgs.Empty);
    }
    // MainForm calls this AFTER Form.OnHandleCreated returns. Form updates its
    // opacity-related extended styles after the public HandleCreated event.
    internal void HandleReady()=>Created(owner,EventArgs.Empty);
    private bool Current(IntPtr handle)=>!disposed && handle!=IntPtr.Zero && !owner.IsDisposed &&
        !owner.Disposing && owner.IsHandleCreated && owner.Handle==handle;
    private void Created(object? sender,EventArgs args)
    {
        if(disposed || !owner.IsHandleCreated || owner.IsDisposed || owner.Disposing)return;
        IntPtr handle=owner.Handle;
        if(ownedHandle==handle)return;
        ownedHandle=handle;addedLayer=false;Attempts++;long lease=++generation;
        bool Valid()=>lease==generation && ownedHandle==handle && Current(handle);
        int style=native.Style(handle);
        if(!Valid())return;
        if((style&Transparent)!=0 || (style&Layered)!=0&&!ownsTransparencySetting){Status=State.ForeignStyle;return;}
        // Claim only the bit we add. Rollback preserves every other flag,
        // including changes made by the framework since initialization.
        addedLayer=true;
        bool changed=native.SetStyle(handle,style|Layered);
        if(!Valid())return;
        if(!changed){Fail(State.SetupFailed);return;}
        bool opaque=native.SetOpaque(handle);
        if(!Valid())return;
        if(!opaque){Fail(State.SetupFailed);return;}
        bool verified=native.IsOpaque(handle);
        if(!Valid())return;
        int actualStyle=native.Style(handle);
        if(!Valid())return;
        if(!verified || (actualStyle&(Layered|Transparent))!=Layered)
        { Fail(State.VerifyFailed);return; }
        Status=State.Active;
    }
    private void Fail(State reason)
    { Status=reason;if(!RemoveOwnedLayer())Status=State.RollbackFailed; }
    private bool RemoveOwnedLayer()
    {
        if(ownsTransparencySetting)
        {
            ownsTransparencySetting=false;
            // A later caller choosing real translucency owns that policy now.
            if(owner.Opacity!=1d || owner.TransparencyKey!=Color.Empty){addedLayer=false;return true;}
            if(!owner.IsDisposed&&!owner.Disposing)owner.AllowTransparency=false;
        }
        if(!addedLayer)return true;
        IntPtr handle=ownedHandle;
        long lease=generation;
        if(!Current(handle)){addedLayer=false;return true;}
        int style=native.Style(handle);
        bool restored=(style&Layered)==0 || native.SetStyle(handle,style&~Layered);
        if(lease!=generation || ownedHandle!=handle)return true;
        if(!restored)return false;
        addedLayer=false;return true;
    }
    private void Destroyed(object? sender,EventArgs args)
    { generation++;ownedHandle=IntPtr.Zero;addedLayer=false;if(!disposed)Status=State.Waiting; }
    public void Dispose()
    {
        if(disposed)return;
        if(!postCreateHost)owner.HandleCreated-=Created;
        owner.HandleDestroyed-=Destroyed;
        bool restored=RemoveOwnedLayer();disposed=true;generation++;ownedHandle=IntPtr.Zero;
        Status=restored?State.Disposed:State.RollbackFailed;
    }
    private sealed class Native:INative
    {
        public int Style(IntPtr h)=>GetWindowLong(h,-20);
        public bool SetStyle(IntPtr h,int value)
        { Marshal.SetLastPInvokeError(0);return SetWindowLong(h,-20,value)!=0 || Marshal.GetLastPInvokeError()==0; }
        public bool SetOpaque(IntPtr h)=>SetLayeredWindowAttributes(h,0,255,2);
        public bool IsOpaque(IntPtr h)=>GetLayeredWindowAttributes(h,out _,out byte alpha,out uint flags) && alpha==255 && flags==2;
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")]private static extern int GetWindowLong(IntPtr h,int index);
        [DllImport("user32.dll",EntryPoint="SetWindowLongW",SetLastError=true)]private static extern int SetWindowLong(IntPtr h,int index,int value);
        [DllImport("user32.dll",SetLastError=true)]private static extern bool SetLayeredWindowAttributes(IntPtr h,uint color,byte alpha,uint flags);
        [DllImport("user32.dll")]private static extern bool GetLayeredWindowAttributes(IntPtr h,out uint color,out byte alpha,out uint flags);
    }
}
