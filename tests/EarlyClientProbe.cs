using DiskAtlas;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

// Owned window, fixed geometry and bounded integer message fields only.
// No recordings, screenshots, arbitrary paths, titles or user file metadata.
internal static class EarlyClientProbe
{
    internal static int Run(string mode,bool dpi96)
    {
        if(mode is not ("baseline" or "redraw" or "layered" or "children" or "geometry"))return 2;
        bool opaqueProfile=AppContext.TryGetSwitch("AsukaLens.TestOpaqueRedirect",out bool opaqueSwitch)&&opaqueSwitch;
        string label=mode=="geometry"&&opaqueProfile?"geometry-opaque":mode;
        string report=Path.Combine("artifacts",mode=="geometry"?"early-client-C57":"early-client-C56",$"{label}-{(dpi96?96:144)}.json");
        if(File.Exists(report)){Console.Error.WriteLine("FAIL early client report exists");return 2;}
        string? failure=null;bool finished=false;Action? close=null;
        var thread=new Thread(()=>
        {
            try
            {
                Application.SetHighDpiMode(dpi96?HighDpiMode.DpiUnaware:HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();Application.SetDefaultFont(new Font("Microsoft YaHei UI",9));
                using var form=new MainForm(null,dibCandidate:true);
                close=()=>{if(form.IsHandleCreated&&!form.IsDisposed)form.BeginInvoke((Action)form.Close);};
                form.Shown+=async(_,_)=>
                {
                    try
                    {
                        if(mode=="layered")
                        {
                            int style=GetWindowLong(form.Handle,-20);
                            SetWindowLong(form.Handle,-20,style|0x80000);
                            if(!SetLayeredWindowAttributes(form.Handle,0,255,2))throw new InvalidOperationException("LayeredSetup");
                        }
                        await Task.Delay(100);
                        using var watch=new Observer(form,mode);
                        var common=form.Size;
                        for(int step=1;step<=8;step++)
                        {await Task.Delay(30);watch.Step=step;form.Size=step%2==0?common:form.MinimumSize;}
                        await Task.Delay(80);
                        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                        File.WriteAllText(report,JsonSerializer.Serialize(new{mode,opaque_requested=opaqueProfile,opaque_active=form.OpaqueRedirect?.Active==true,dpi=form.DeviceDpi,rows=watch.Rows,dropped=watch.Dropped,
                            class_style=GetClassLong(form.Handle,-26),class_background_brush_is_null=GetClassLongPtr(form.Handle,-10)==IntPtr.Zero,
                            scope="owned main window; discrete GDI observations; no visual acceptance"},new JsonSerializerOptions{WriteIndented=true}));
                        Console.WriteLine($"PASS early client observation rows={watch.Rows.Count}; dropped={watch.Dropped}; screen_acceptance=separate");
                    }
                    catch{failure="EarlyClientObservation";}
                    finally{form.Close();}
                };
                Application.Run(form);finished=true;
            }
            catch{failure="EarlyClientWorker";}
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(40))){close?.Invoke();thread.Join(3000);failure="EarlyClientDeadline";}
        if(failure!=null||!finished){Console.Error.WriteLine("FAIL early client code="+failure);return 1;}return 0;
    }
    private sealed class Observer:NativeWindow,IDisposable
    {
        private readonly MainForm owner;private readonly string mode;private readonly Stopwatch clock=Stopwatch.StartNew();
        private readonly List<ChildObserver> children=[];
        private Size old;private int depth;
        internal int Step, Dropped;
        internal List<object> Rows{get;}=[];
        internal Observer(MainForm form,string mode)
        {
            owner=form;this.mode=mode;AssignHandle(form.Handle);
            if(mode=="children")Watch(form);
        }
        private void Watch(Control parent)
        {
            foreach(Control child in parent.Controls)
            {
                if(child.IsHandleCreated && child is WallpaperTable or WallpaperSplitContainer or SplitterPanel or TreemapControl or WallpaperGrid)
                    children.Add(new ChildObserver(this,child,children.Count+1));
                Watch(child);
            }
        }
        protected override void WndProc(ref Message m)
        {
            bool observed=m.Msg is 0x46 or 0x47 or 0x83 or 0x85 or 0x14 or 0x5 or 0xf or 0x88;
            if(!observed){base.WndProc(ref m);return;}
            if(m.Msg==0x46&&GetClientRect(Handle,out var previous))old=new(previous.Right,previous.Bottom);
            depth++;Record(ref m,true);
            try
            {
                base.WndProc(ref m);
                if(mode=="redraw" && m.Msg==0x83 && m.WParam!=IntPtr.Zero)
                    m.Result=new IntPtr(0x300); // Preserve base rectangles/frame; WVR_REDRAW only.
            }
            finally{Record(ref m,false);depth--;}
        }
        private void Record(ref Message m,bool before,Control? child=null,int childId=0)
        {
            if(Step is <1 or >8)return;if(Rows.Count>=4096){Dropped++;return;}
            IntPtr target=child?.Handle??Handle;
            GetClientRect(target,out var r);
            GetWindowRect(target,out var window);
            object? position=null,calc=null;
            if(m.Msg is 0x46 or 0x47 && m.LParam!=IntPtr.Zero)
            {var p=Marshal.PtrToStructure<Position>(m.LParam);position=new{p.Width,p.Height,p.Flags};}
            if(m.Msg==0x83 && m.WParam!=IntPtr.Zero && m.LParam!=IntPtr.Zero)
            {var p=Marshal.PtrToStructure<Calc>(m.LParam);calc=new{new_width=p.New.Right-p.New.Left,new_height=p.New.Bottom-p.New.Top,old_width=p.OldClient.Right-p.OldClient.Left,old_height=p.OldClient.Bottom-p.OldClient.Top};}
            var points=new List<object>();int gridValid=0,gridBlack=0;
            IntPtr dc=child==null?GetDCEx(target,IntPtr.Zero,0x2|0x8|0x10):IntPtr.Zero;
            if(dc!=IntPtr.Zero)
            {
                try
                {
                    var sample=new List<Point>();
                    if(r.Right>old.Width)sample.Add(new(r.Right-2,r.Bottom*2/3));
                    if(r.Bottom>old.Height)sample.Add(new(r.Right*2/3,r.Bottom-2));
                    foreach(var p in sample)
                    {if(!PtVisible(dc,p.X,p.Y))continue;uint color=GetPixel(dc,p.X,p.Y);points.Add(new{x=p.X,y=p.Y,valid=color!=0xffffffff,black=(color&0xffffff)==0});}
                }
                finally{ReleaseDC(target,dc);}
            }
            IntPtr all=GetDCEx(target,IntPtr.Zero,0x2|0x10);
            if(all!=IntPtr.Zero)
            {
                try
                {
                    for(int gy=1;gy<=5;gy++)for(int gx=1;gx<=7;gx++)
                    {
                        int x=r.Right*gx/8,y=r.Bottom*gy/6;
                        if(!PtVisible(all,x,y))continue;uint pixel=GetPixel(all,x,y);
                        if(pixel==0xffffffff)continue;gridValid++;if((pixel&0xffffff)==0)gridBlack++;
                    }
                }
                finally{ReleaseDC(target,all);}
            }
            Rows.Add(new{step=Step,depth,message=m.Msg,before,ms=clock.Elapsed.TotalMilliseconds,result=before?0:m.Result.ToInt64(),grid_valid=gridValid,grid_black=gridBlack,
                role=child switch{WallpaperTable=>"Table",WallpaperSplitContainer=>"Split",SplitterPanel=>"Panel",TreemapControl=>"Map",WallpaperGrid=>"Grid",_=>"Root"},child_id=childId,
                geometry_active=owner.IsGeometryPresentationPaused,native_width=r.Right,native_height=r.Bottom,window_width=window.Right-window.Left,window_height=window.Bottom-window.Top,
                managed_width=(child??owner).ClientSize.Width,managed_height=(child??owner).ClientSize.Height,position,calc,points});
        }
        private sealed class ChildObserver:NativeWindow,IDisposable
        {
            private readonly Observer parent;private readonly Control control;private readonly int id;
            internal ChildObserver(Observer parent,Control control,int id){this.parent=parent;this.control=control;this.id=id;AssignHandle(control.Handle);}
            protected override void WndProc(ref Message m)
            {
                if(m.Msg is not (0x46 or 0x47 or 0x85 or 0x14 or 0xf)){base.WndProc(ref m);return;}
                parent.Record(ref m,true,control,id);
                try{base.WndProc(ref m);}finally{parent.Record(ref m,false,control,id);}
            }
            public void Dispose(){ReleaseHandle();GC.KeepAlive(control);}
        }
        public void Dispose(){foreach(var c in children)c.Dispose();ReleaseHandle();GC.KeepAlive(owner);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct Rect{internal int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct Position{internal IntPtr Window,After;internal int X,Y,Width,Height;internal uint Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct Calc{internal Rect New,OldWindow,OldClient;internal IntPtr Position;}
    [DllImport("user32.dll")]private static extern bool GetClientRect(IntPtr h,out Rect r);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr h,out Rect r);
    [DllImport("user32.dll")]private static extern IntPtr GetDCEx(IntPtr h,IntPtr region,uint flags);
    [DllImport("user32.dll")]private static extern int ReleaseDC(IntPtr h,IntPtr dc);
    [DllImport("user32.dll",EntryPoint="GetClassLongW")]private static extern uint GetClassLong(IntPtr h,int index);
    [DllImport("user32.dll",EntryPoint="GetClassLongPtrW")]private static extern IntPtr GetClassLongPtr(IntPtr h,int index);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")]private static extern int GetWindowLong(IntPtr h,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongW")]private static extern int SetWindowLong(IntPtr h,int index,int value);
    [DllImport("user32.dll")]private static extern bool SetLayeredWindowAttributes(IntPtr h,uint color,byte alpha,uint flags);
    [DllImport("gdi32.dll")]private static extern bool PtVisible(IntPtr dc,int x,int y);
    [DllImport("gdi32.dll")]private static extern uint GetPixel(IntPtr dc,int x,int y);
}
