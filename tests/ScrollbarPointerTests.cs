using DiskAtlas;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;

internal static class ScrollbarPointerTests
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    internal static int Run(bool dpi96)
    {
        int checks = 0; string? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(dpi96 ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles(); Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Check(ScrollbarPointerTracking.Maximum(0, 100, 10) == 91 && ScrollbarPointerTracking.Maximum(5, 10, 100) == 5, "EffectiveRangeUsesPageAndMinimum");
                Check(ScrollbarPointerTracking.Maximum(0,int.MaxValue,0)==int.MaxValue, "ZeroPageKeepsMaximumWithoutOverflow");
                Check(ScrollbarPointerTracking.Position(10,0,10,100,0,1000,false)==0 && ScrollbarPointerTracking.Position(110,0,10,100,0,1000,false)==1000, "PointerRangeEndpoints");
                Check(ScrollbarPointerTracking.Position(10,0,10,100,0,1000,true)==1000 && ScrollbarPointerTracking.Position(110,0,10,100,0,1000,true)==0, "RtlEndpointsReverse");
                Check(ScrollbarPointerTracking.Position(-100,0,10,100,0,1000,false)==0 && ScrollbarPointerTracking.Position(10000,0,10,100,0,1000,false)==1000, "OutsidePointerClamps");
                Check(ScrollbarPointerTracking.Position(60,0,10,100,0,int.MaxValue,false)>65535, "PositionRetainsFullNativeRange");
                Check(ScrollbarPointerTracking.ThumbStart(500,10,100,0,1000,false)==60 && ScrollbarPointerTracking.ThumbStart(0,10,100,0,1000,true)==110, "PreviewThumbMatchesRangeAndRtl");
                Check(ScrollbarPointerTracking.Position(100,0,10,0,5,5,false)==5, "ZeroTravelHasFinitePosition");
                using var image = new Bitmap(80,64);
                using(var g=Graphics.FromImage(image))g.Clear(Color.FromArgb(247,199,169));
                using var form = new Form {ClientSize=new(700,460),BackgroundImage=image,ShowInTaskbar=false};
                using var grid = new WallpaperGrid { Dock=DockStyle.Fill,AllowUserToAddRows=false,RowHeadersVisible=false,VirtualMode=true,ReadOnly=true,RowTemplate={Height=32} };
                grid.Columns.Add("name","Name");grid.Columns.Add("size","Size");
                foreach(DataGridViewColumn c in grid.Columns)c.Width=560;
                grid.CellValueNeeded+=(_,e)=>e.Value="Synthetic "+e.RowIndex;grid.RowCount=100;form.Controls.Add(grid);form.Show();Pump(40);
                var verticalNative = grid.Controls.OfType<VScrollBar>().Single(b=>b.Visible);
                Check(GridScrollbarSkin.TryGetGeometry(verticalNative,out var originalRange),"FilterRangeGeometryAvailable");
                grid.RowCount=50;Pump(20);
                Check(GridScrollbarSkin.TryGetGeometry(verticalNative,out var filteredRange)&&filteredRange.Thumb.Height>originalRange.Thumb.Height,
                    "FilteredRowCountRefreshesNativeThumbLength");
                grid.RowCount=100;Pump(20);
                foreach(var bar in grid.Controls.OfType<ScrollBar>().Where(b=>b.Visible).ToArray())
                {
                    using var releaseProbe = new ReleaseProbe(bar);
                    bool vertical=bar is VScrollBar;
                    bar.Value=bar.Minimum;bar.Invalidate();bar.Update();
                    Check(GridScrollbarSkin.TryGetGeometry(bar,out var geometry),"PointerGeometryAvailable");
                    var events=new List<ScrollEventType>();ScrollEventHandler record=(_,e)=>events.Add(e.Type);bar.Scroll+=record;
                    var center=Center(geometry.Thumb);int oldRow=grid.FirstDisplayedScrollingRowIndex;
                    uint[] before=Rail(bar,geometry);
                    Message(bar,0x201,center);
                    Check(bar.Capture,"PointerCapturesOriginalNativeBar");
                    Point end=vertical?new(center.X,geometry.EndArrow.Top-3):new(geometry.EndArrow.Left-3,center.Y);
                    Message(bar,0x200,end);
                    Check(before.SequenceEqual(Rail(bar,geometry)),"TrackingRailNeverPublishesSystemGrey_"+(vertical?"Vertical":"Horizontal"));
                    Check(SystemInformation.DragFullWindows || bar.Value==bar.Minimum,"DeferredDragKeepsNativeSystemPreference");
                    Message(bar,0x202,end);
                    Check(!bar.Capture&&bar.Value>bar.Minimum,"ReleaseCommitsPositionAndDropsCapture");
                    Check(releaseProbe.ValueOnRelease==bar.Value,"CaptureReleaseCannotExposeOldThumbValue");
                    Check(events.Contains(ScrollEventType.ThumbPosition)&&events.Count(t=>t==ScrollEventType.EndScroll)==1,"NativeScrollContractAndSingleEnd");
                    Check(vertical?grid.FirstDisplayedScrollingRowIndex>oldRow:grid.HorizontalScrollingOffset>0,"GridMovesThroughExistingScrollHandler");
                    bar.Value=bar.Minimum;events.Clear();GridScrollbarSkin.TryGetGeometry(bar,out geometry);
                    Message(bar,0x201,Center(geometry.Thumb));Message(bar,0x200,end);
                    int committed=bar.Value;bar.Capture=false;
                    Check(!bar.Capture&&bar.Value==committed&&events.Count(t=>t==ScrollEventType.EndScroll)==1,"CaptureLossCancelsDeferredCommit");
                    GridScrollbarSkin.TryGetGeometry(bar,out geometry);events.Clear();
                    Message(bar,0x201,Center(geometry.EndArrow));Pump(510);Message(bar,0x202,Center(geometry.EndArrow));
                    Check(events.Count(t=>t==ScrollEventType.SmallIncrement)>=2,"HeldArrowRepeatsAndReleases");
                    int count=events.Count;Pump(100);Check(count==events.Count,"RepeatStopsAfterRelease");
                    Message(bar,0x201,Center(geometry.EndArrow));grid.Hide();
                    Check(!bar.Capture&&grid.ScrollbarBufferedBytes==0,"HidingCancelsPointerAndReleasesFrames");grid.Show();Pump(20);
                    bar.Scroll-=record;
                }
                using(var large=new VScrollBar{Minimum=0,Maximum=int.MaxValue,LargeChange=10,Width=24,Height=220,Left=20,Top=40})
                {
                    grid.Controls.Add(large);large.BringToFront();Pump(20);
                    large.Scroll+=(_,e)=>e.NewValue=Math.Min(e.NewValue,123456);
                    Check(GridScrollbarSkin.TryGetGeometry(large,out var geometry),"LargeRangeGeometryAvailable");
                    var down=Center(geometry.Thumb);var end=new Point(down.X,geometry.EndArrow.Top-2);
                    Message(large,0x201,down);Message(large,0x200,end);Message(large,0x202,end);
                    Check(large.Value==123456,"HandlerAdjustedNewValueHonoredBeyond16Bits");
                    large.Value=0;GridScrollbarSkin.TryGetGeometry(large,out geometry);
                    Message(large,0x201,Center(geometry.Thumb));Message(large,0x100,Point.Empty,27);
                    Check(!large.Capture&&large.Value==0,"EscapeCancelsAndReleasesCapture");
                    large.Enabled=false;Message(large,0x201,Center(geometry.Thumb));Check(!large.Capture,"DisabledBarDoesNotStartTracking");
                    large.Enabled=true;GridScrollbarSkin.TryGetGeometry(large,out geometry);
                    large.Value=0;
                    using(var deferred=new ScrollbarPointerTracking(grid,large,()=>{},()=>false))
                    using(var release=new ReleaseProbe(large))
                    {
                        GridScrollbarSkin.TryGetGeometry(large,out geometry);var start=Center(geometry.Thumb);
                        var target=new Point(start.X,geometry.EndArrow.Top-2);
                        var downMessage=System.Windows.Forms.Message.Create(large.Handle,0x201,new IntPtr(1),Pack(start));deferred.Handle(ref downMessage);
                        var moveMessage=System.Windows.Forms.Message.Create(large.Handle,0x200,new IntPtr(1),Pack(target));deferred.Handle(ref moveMessage);
                        Check(large.Value==0,"ForcedDeferredDragDoesNotChangeCommittedPosition");
                        var upMessage=System.Windows.Forms.Message.Create(large.Handle,0x202,IntPtr.Zero,Pack(target));deferred.Handle(ref upMessage);
                        Check(large.Value>0&&release.ValueOnRelease==large.Value,"DeferredReleaseCannotPublishOldThumbValue");
                    }
                    GridScrollbarSkin.TryGetGeometry(large,out geometry);
                    Message(large,0x201,Center(geometry.EndArrow));large.Dispose();
                    Check(grid.SkinnedScrollbarCount==2,"DisposingTrackedChildRemovesHooksAndTimer");
                }
                grid.Dispose();Check(grid.ScrollbarBufferedBytes==0&&grid.SkinnedScrollbarCount==0,"GridDisposalReleasesAllPointerAndFrameOwners");
                form.BackgroundImage=null;form.Close();
            }
            catch(Failure e){failure=e.Code;}
            catch(Exception e){failure="UnexpectedPointer_"+e.GetType().Name;}
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(30)))failure="PointerDeadline";
        if(failure!=null){Console.Error.WriteLine($"FAIL scrollbar pointer code={failure}; completed_checks={checks}");return 1;}
        Console.WriteLine($"PASS scrollbar pointer checks={checks}; synthetic_only=True; dpi96={dpi96}");return 0;
        void Check(bool value,string code){checks++;if(!value)throw new Failure(code);}
    }
    private static Point Center(Rectangle r)=>new(r.Left+r.Width/2,r.Top+r.Height/2);
    private static IntPtr Pack(Point point)=>new((point.X&0xffff)|((point.Y&0xffff)<<16));
    private static void Message(ScrollBar bar,uint msg,Point point,int w=1)=>SendMessage(bar.Handle,msg,new IntPtr(w),new IntPtr((point.X&0xffff)|((point.Y&0xffff)<<16)));
    private static void Pump(int ms){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(5);}}
    private static uint[] Rail(ScrollBar bar,GridScrollbarSkin.Geometry shape)
    {
        bool vertical=bar is VScrollBar;int start=vertical?shape.StartArrow.Bottom:shape.StartArrow.Right,end=vertical?shape.EndArrow.Top:shape.EndArrow.Left;
        IntPtr dc=GetDC(bar.Handle);
        try{return Enumerable.Range(start+3,Math.Max(1,end-start-6)).Where(i=>i%5==0).Select(i=>GetPixel(dc,vertical?0:i,vertical?i:0)).ToArray();}
        finally{if(dc!=IntPtr.Zero)ReleaseDC(bar.Handle,dc);}
    }
    private sealed class Failure(string code):Exception{internal string Code=>code;}
    private sealed class ReleaseProbe : NativeWindow, IDisposable
    {
        private readonly ScrollBar bar;
        internal int? ValueOnRelease { get; private set; }
        internal ReleaseProbe(ScrollBar bar) { this.bar=bar;AssignHandle(bar.Handle); }
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if(message.Msg==0x0215)ValueOnRelease=bar.Value;
        }
        public void Dispose(){if(Handle!=IntPtr.Zero)ReleaseHandle();}
    }
}
