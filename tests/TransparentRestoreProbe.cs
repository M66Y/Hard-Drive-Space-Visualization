using DiskAtlas;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

// Synthetic owned windows only. Observe natural paint first; the optional
// local invalidation is a labelled causal comparison, never a pre-sample fix.
internal static class TransparentRestoreProbe
{
    internal static int Run(bool dpi96, bool cache, bool verify=false, bool custom=false)
    {
        string label=verify?(custom?"verified-custom":"verified-default"):(cache?"on-managed":"off-managed");
        string report=Path.Combine("artifacts","restore-background-C54",$"{label}-{(dpi96?96:144)}.json");
        if(File.Exists(report)){Console.Error.WriteLine("FAIL restore probe report exists");return 2;}
        string? failure=null;int samples=0,checks=0;
        var thread=new Thread(()=>
        {
            try
            {
                Application.SetHighDpiMode(dpi96?HighDpiMode.DpiUnaware:HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();Application.SetDefaultFont(new Font("Microsoft YaHei UI",9));
                using var form=new MainForm(null,dibCandidate:true);
                form.ShowInTaskbar=false;
                form.Shown+=async(_,_)=>
                {
                    try
                    {
                        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                        var box=(CheckBox)typeof(MainForm).GetField("reduceMotion",flags)!.GetValue(form)!;
                        var coverage=(DibWindowCoverage)typeof(MainForm).GetField("dibCoverage",flags)!.GetValue(form)!;
                        coverage.UseRootCache=cache;
                        if(custom)
                        {
                            var image=new Bitmap(64,48);
                            using(var g=Graphics.FromImage(image))
                            {
                                g.Clear(Color.MidnightBlue);
                                g.FillRectangle(Brushes.Salmon,0,0,32,24);g.FillRectangle(Brushes.PeachPuff,32,24,32,24);
                            }
                            typeof(MainForm).GetMethod("ApplyBackground",flags)!.Invoke(form,[image,new BackgroundView(.8f,.15f,1.3f,null),Color.Firebrick,false]);
                        }
                        using var observer=new MoveObserver(box);
                        var rows=new List<object>();
                        await Task.Delay(500);Sample("initial");
                        form.WindowState=FormWindowState.Maximized;await Task.Delay(300);Sample("maximized");
                        form.WindowState=FormWindowState.Normal;await Task.Delay(300);Sample("restored");
                        await Task.Delay(400);Sample("settled");
                        box.Invalidate();await Task.Delay(200);Sample("local-invalidation");
                        box.Parent!.Invalidate();await Task.Delay(200);Sample("parent-invalidation");
                        if(verify)
                        {
                            var map=(TreemapControl)typeof(MainForm).GetField("map",flags)!.GetValue(form)!;
                            box.Checked=true;await Task.Delay(100);Sample("checked");Check(!map.AnimationsEnabled,"ReducedMotionStillApplied");
                            box.Checked=false;await Task.Delay(100);Sample("unchecked");Check(map.AnimationsEnabled,"MotionStillRestored");
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                        File.WriteAllText(report,JsonSerializer.Serialize(new {dpi=form.DeviceDpi,root_cache=cache,custom,verify,checks,rows,moves=observer.Changes,scope="owned synthetic window; no screenshot"},new JsonSerializerOptions{WriteIndented=true}));
                        Console.WriteLine(verify?$"PASS transparent restore checks={checks}; dpi={form.DeviceDpi}; custom={custom}; exact_rgb=true":$"PASS restore observation dpi={form.DeviceDpi}; cache={cache}; samples={samples}; pixels_are_observations=true");

                        void Sample(string stage)
                        {
                            var parent=(WallpaperTable)box.Parent!;
                            using var expected=new Bitmap(box.Width,box.Height,PixelFormat.Format32bppPArgb);
                            using(var graphics=Graphics.FromImage(expected))Wallpaper.Paint(box,graphics,parent.Wash,parent.Surface,nativeCopy:false);
                            IntPtr dc=GetDC(box.Handle);if(dc==IntPtr.Zero)throw new InvalidOperationException("OwnedControlDcUnavailable");
                            try
                            {
                                var observed=new List<object>();
                                foreach(var point in new[]{new Point(box.Width-3,2),new Point(box.Width-3,box.Height-3),new Point(box.Width/2,box.Height-2)})
                                {
                                    uint rgb=GetPixel(dc,point.X,point.Y);var reference=expected.GetPixel(point.X,point.Y);
                                    int actual=unchecked((int)((rgb&255)<<16|(rgb&0xff00)|(rgb>>16&255)));
                                    int wanted=reference.ToArgb()&0xffffff;
                                    observed.Add(new {x=point.X,y=point.Y,valid=rgb!=0xffffffff,actual_rgb=actual,expected_rgb=wanted,equal=actual==wanted});samples++;
                                    if(verify && stage is "restored" or "settled" or "checked" or "unchecked")Check(rgb!=0xffffffff && actual==wanted,"CheckboxBackgroundMismatch");
                                }
                                using var expectedParent=new Bitmap(parent.Width,parent.Height,PixelFormat.Format32bppPArgb);
                                using(var graphics=Graphics.FromImage(expectedParent))Wallpaper.Paint(parent,graphics,parent.Wash,parent.Surface,nativeCopy:false);
                                var gaps=new List<object>();
                                IntPtr parentDc=GetDCEx(parent.Handle,IntPtr.Zero,0x2|0x8|0x10);
                                if(parentDc==IntPtr.Zero)throw new InvalidOperationException("OwnedParentDcUnavailable");
                                try
                                {
                                    foreach(var point in new[]{new Point(box.Left-20,parent.Height/2),new Point(box.Left-7,parent.Height/2),new Point(box.Left-20,3)})
                                    {
                                        uint rgb=GetPixel(parentDc,point.X,point.Y);var c=expectedParent.GetPixel(point.X,point.Y);
                                        int actual=unchecked((int)((rgb&255)<<16|(rgb&0xff00)|(rgb>>16&255)));
                                        int wanted=c.ToArgb()&0xffffff;
                                        gaps.Add(new{x=point.X,y=point.Y,visible=PtVisible(parentDc,point.X,point.Y),actual_rgb=actual,expected_rgb=wanted,equal=actual==wanted});
                                        if(verify)Check(rgb!=0xffffffff && PtVisible(parentDc,point.X,point.Y) && actual==wanted,"ExposedParentBackgroundMismatch");
                                    }
                                }
                                finally{ReleaseDC(parent.Handle,parentDc);}
                                rows.Add(new {stage,width=box.Width,height=box.Height,left=box.Left,top=box.Top,parent_width=parent.Width,parent_height=parent.Height,observed,gaps});
                            }
                            finally{ReleaseDC(box.Handle,dc);}
                        }
                        void Check(bool value,string code){if(!value)throw new ProbeFailure(code);checks++;}
                    }
                    catch(Exception error){failure=error is ProbeFailure known?known.Code:"RestoreObservationFailed";}
                    finally{form.Close();}
                };
                Application.Run(form);
            }
            catch{failure="RestoreWorkerFailed";}
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(35)))failure="RestoreDeadline";
        if(failure!=null){Console.Error.WriteLine("FAIL restore probe code="+failure);return 1;}
        return 0;
    }
    private sealed class ProbeFailure(string code):Exception{internal string Code{get;}=code;}
    private sealed class MoveObserver:NativeWindow,IDisposable
    {
        private readonly CheckBox box;
        internal List<object> Changes {get;}=[];
        internal MoveObserver(CheckBox box){this.box=box;AssignHandle(box.Handle);}
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==0x46 && m.LParam!=IntPtr.Zero && Changes.Count<32)
            {
                var p=Marshal.PtrToStructure<Position>(m.LParam);
                Changes.Add(new {x=p.X,y=p.Y,width=p.Width,height=p.Height,old_x=box.Left,old_y=box.Top,nocopy=(p.Flags&0x100)!=0,nosize=(p.Flags&1)!=0,nomove=(p.Flags&2)!=0});
            }
            base.WndProc(ref m);
        }
        public void Dispose()=>ReleaseHandle();
    }
    [StructLayout(LayoutKind.Sequential)]private struct Position{internal IntPtr Hwnd,After;internal int X,Y,Width,Height;internal uint Flags;}
    [DllImport("user32.dll")]private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]private static extern IntPtr GetDCEx(IntPtr window,IntPtr region,uint flags);
    [DllImport("user32.dll")]private static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")]private static extern uint GetPixel(IntPtr dc,int x,int y);
    [DllImport("gdi32.dll")]private static extern bool PtVisible(IntPtr dc,int x,int y);
}
