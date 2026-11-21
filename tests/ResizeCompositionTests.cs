using DiskAtlas;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

// Natural owned-window size transitions. Inspect the final exposed parent
// surface before any explicit invalidation, Refresh or DrawToBitmap.
internal static class ResizeCompositionTests
{
    internal static int Run(bool dpi96)
    {
        int checks=0; string? failure=null; Action? close=null; bool finished=false;
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
                        await Task.Delay(100);
                        var split=Descendants(form).OfType<WallpaperSplitContainer>().Single();
                        var common=form.Size;var session=form.Rendering!;
                        var probe=new ResizeWorkProbe();ResizeWorkProbe.Current=probe;
                        for(int i=0;i<8;i++)
                        {
                            probe.BeginStep(i+1);
                            form.Size=i%2==0?form.MinimumSize:common;
                            // Return of Size is the contract boundary, not a later
                            // forced refresh. Empty split-parent gutters expose
                            // the panel behind the unchanged child layout.
                            Check(!form.IsGeometryPresentationPaused,"GeometryBoundaryReleased");
                            Check(GetProp(form.Handle,"SysSetRedraw")==IntPtr.Zero,"RootRedrawOwnershipUnchanged");
                            await Task.Delay(30);
                            foreach(var panel in new[]{split.Panel1,split.Panel2})
                            {
                                IntPtr dc=GetDCEx(panel.Handle,IntPtr.Zero,0x2|0x8|0x10);
                                Check(dc!=IntPtr.Zero,"PanelDcAvailable");
                                try
                                {
                                    using var expected=new Bitmap(panel.Width,panel.Height);
                                    using(var g=Graphics.FromImage(expected))Wallpaper.Paint(panel,g,nativeCopy:false);
                                    int sampled=0;
                                    foreach(var p in new[]{new Point(0,0),new Point(panel.Width-1,0),new Point(0,panel.Height-1),new Point(panel.Width-1,panel.Height-1)})
                                    {
                                        if(!PtVisible(dc,p.X,p.Y))continue;
                                        uint actual=GetPixel(dc,p.X,p.Y);var color=expected.GetPixel(p.X,p.Y);
                                        uint wanted=(uint)(color.R|color.G<<8|color.B<<16);
                                        Check(actual!=0xffffffff && (actual&0xffffff)==wanted,"NaturalPanelBackgroundMatches");sampled++;
                                    }
                                    Check(sampled>0,"VisiblePanelWitnessExists");
                                }
                                finally{ReleaseDC(panel.Handle,dc);}
                            }
                            Check(session.Active&&session.Bytes<=RenderSession.MaxBytes,"DibSessionRemainsBounded");
                        }
                        ResizeWorkProbe.Current=null;
                        var summary=JsonSerializer.SerializeToElement(probe.Summary());
                        Check(summary.GetProperty("deferred_panel_erases").GetInt64()>0,"IntermediatePanelWorkWasDeferred");
                        Check(split.GetPanelBackgroundCounts(1).DirectPaints>0&&split.GetPanelBackgroundCounts(2).DirectPaints>0,"FinalPanelsStillActuallyPaint");
                        form.Dispose();Check(session.Bytes==0,"AllPixelsReleased");
                    }
                    catch(Exception e){failure=e is Failure f?f.Code:"UnexpectedResizeComposition";}
                    finally{ResizeWorkProbe.Current=null;if(!form.IsDisposed)form.Close();}
                };
                Application.Run(form);finished=true;
            }
            catch{failure="ResizeCompositionWorker";}
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(40))){close?.Invoke();thread.Join(3000);failure="ResizeCompositionDeadline";}
        Console.WriteLine($"{(failure==null?"PASS":"FAIL")} resize composition checks={checks}; code={failure??"None"}; worker_finished={finished}; natural_pixels=true; screen_acceptance=separate");
        return failure==null?0:1;
        void Check(bool value,string code){if(!value)throw new Failure(code);checks++;}
    }
    private static IEnumerable<Control> Descendants(Control control)
    {foreach(Control child in control.Controls){yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private sealed class Failure(string code):Exception{internal string Code{get;}=code;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetProp(IntPtr hwnd,string name);
    [DllImport("user32.dll")]private static extern IntPtr GetDCEx(IntPtr hwnd,IntPtr region,uint flags);
    [DllImport("user32.dll")]private static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
    [DllImport("gdi32.dll")]private static extern bool PtVisible(IntPtr dc,int x,int y);
    [DllImport("gdi32.dll")]private static extern uint GetPixel(IntPtr dc,int x,int y);
}
