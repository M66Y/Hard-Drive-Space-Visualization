using DiskAtlas;
using System.Reflection;
using System.Drawing.Drawing2D;

internal static class RootCompositeTests
{
    internal static int Run(bool dpi96)
    {
        int checks=0;long pixels=0;string? failure=null;
        var thread=new Thread(()=>
        {
            try
            {
                Application.SetHighDpiMode(dpi96?HighDpiMode.DpiUnaware:HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                using var form=new MainForm(null,dibCandidate:true);
                form.Shown+=(_,_)=>
                {
                    try
                    {
                        var coverage=(DibWindowCoverage)typeof(MainForm).GetField("dibCoverage",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
                        var session=form.Rendering!;
                        var size=new Size(193,127);
                        for(int sample=0;sample<3;sample++)
                        {
                            using var image=new Bitmap(67,43);
                            using(var g=Graphics.FromImage(image))
                            {
                                using var brush=new LinearGradientBrush(new Rectangle(0,0,67,43),sample==0?Color.Black:Color.Salmon,sample==2?Color.Navy:Color.White,37f);
                                g.FillRectangle(brush,0,0,67,43);
                            }
                            var previous=form.BackgroundImage;form.BackgroundImage=new Bitmap(image);previous?.Dispose();
                            using var child=new WallpaperTable{Size=new Size(79,61)};form.Controls.Add(child);
                            var frame=coverage.RootFrame(size);
                            Check(frame!=null && frame.Native,"RootFrameAvailable");
                            using var direct=new NativeResizeFrame(size,form.DeviceDpi);
                            direct.RenderOpaque(g=>Wallpaper.Paint(form,g,nativeSize:size,nativeCopy:false));
                            for(int y=0;y<size.Height;y++)for(int x=0;x<size.Width;x++)
                            {
                                if(frame!.Bitmap.GetPixel(x,y).ToArgb()!=direct.Bitmap.GetPixel(x,y).ToArgb())throw new Failure("RootCompositePixelDifference");
                                pixels++;
                            }
                            Check(ReferenceEquals(frame,coverage.RootFrame(size)),"MatchingKeyReusesRoot");
                            using var target=new NativeResizeFrame(child.Size,form.DeviceDpi);
                            bool copied=false;
                            target.RenderOpaque(g=>{g.Clear(Color.Coral);copied=coverage.TryPaintCachedRoot(child,g,size,new Point(18,12),42);});
                            Check(copied,"MappedRootSubsetCopied");
                            for(int y=0;y<child.Height;y++)for(int x=0;x<child.Width;x++)
                            {
                                if(target.Bitmap.GetPixel(x,y).ToArgb()!=frame!.Bitmap.GetPixel(x+18,y+12).ToArgb())throw new Failure("RootSubsetPixelDifference");
                                pixels++;
                            }
                            target.RenderOpaque(g=>
                            {
                                g.Clear(Color.Coral);
                                using var clip=new Region(new Rectangle(3,4,41,32));clip.Exclude(new Rectangle(10,11,7,9));g.SetClip(clip,CombineMode.Replace);
                                Check(coverage.TryPaintCachedRoot(child,g,size,new Point(18,12),42),"NativeClipAccepted");
                            });
                            for(int y=0;y<child.Height;y++)for(int x=0;x<child.Width;x++)
                            {
                                bool copiedPixel=new Rectangle(3,4,41,32).Contains(x,y)&&!new Rectangle(10,11,7,9).Contains(x,y);
                                int expected=copiedPixel?frame!.Bitmap.GetPixel(x+18,y+12).ToArgb():Color.Coral.ToArgb();
                                if(target.Bitmap.GetPixel(x,y).ToArgb()!=expected)throw new Failure("RootClipPixelDifference");pixels++;
                            }
                            target.RenderOpaque(g=>Check(!coverage.TryPaintCachedRoot(child,g,size,Point.Empty,122),"OtherWashRejected"));
                            target.RenderOpaque(g=>Check(!coverage.TryPaintCachedRoot(child,g,new Size(194,127),Point.Empty,42),"WrongSizeRejected"));
                            target.RenderOpaque(g=>
                            {
                                g.Clear(Color.Coral);g.TranslateTransform(1,0);
                                Check(coverage.TryPaintCachedRoot(child,g,size,Point.Empty,42),"TranslatedManagedCacheAccepted");
                            });
                            for(int y=0;y<child.Height;y++)for(int x=0;x<child.Width;x++)
                            {
                                int expected=x==0?Color.Coral.ToArgb():frame!.Bitmap.GetPixel(x-1,y).ToArgb();
                                if(target.Bitmap.GetPixel(x,y).ToArgb()!=expected)throw new Failure("TranslatedRootCachePixels");pixels++;
                            }
                            target.RenderOpaque(g=>
                            {
                                g.Clear(Color.Coral);g.TranslateTransform(-3,2);
                                using var clip=new Region(new Rectangle(4,5,40,31));clip.Exclude(new Rectangle(11,13,7,8));g.SetClip(clip,CombineMode.Replace);
                                Check(coverage.TryPaintCachedRoot(child,g,size,new Point(18,12),42),"TranslatedHoledManagedCacheAccepted");
                            });
                            for(int y=0;y<child.Height;y++)for(int x=0;x<child.Width;x++)
                            {
                                var p=new Point(x+3,y-2);
                                bool visible=new Rectangle(4,5,40,31).Contains(p)&&!new Rectangle(11,13,7,8).Contains(p);
                                int expected=visible?frame!.Bitmap.GetPixel(p.X+18,p.Y+12).ToArgb():Color.Coral.ToArgb();
                                if(target.Bitmap.GetPixel(x,y).ToArgb()!=expected)throw new Failure("TranslatedHoledCachePixels");pixels++;
                            }
                            target.RenderOpaque(g=>{g.TranslateTransform(.5f,0);Check(!coverage.TryPaintCachedRoot(child,g,size,Point.Empty,42),"FractionalMappingFallsBack");});
                            target.RenderOpaque(g=>{g.ScaleTransform(1.2f,1);Check(!coverage.TryPaintCachedRoot(child,g,size,Point.Empty,42),"ScaledMappingFallsBack");});
                            target.RenderOpaque(g=>{g.RotateTransform(2);Check(!coverage.TryPaintCachedRoot(child,g,size,Point.Empty,42),"RotatedMappingFallsBack");});
                        }
                        var oldCropFrame=coverage.RootFrame(size);
                        typeof(MainForm).GetProperty("BackgroundCrop",BindingFlags.Instance|BindingFlags.NonPublic)!
                            .SetValue(form,new BackgroundView(.1f,.85f,1.4f,null));
                        var cropped=coverage.RootFrame(size);
                        Check(cropped!=null && !ReferenceEquals(cropped,oldCropFrame),"CropOnlyChangeInvalidatesRoot");
                        using(var direct=new NativeResizeFrame(size,form.DeviceDpi))
                        {
                            direct.RenderOpaque(g=>Wallpaper.Paint(form,g,nativeSize:size,nativeCopy:false));
                            for(int y=0;y<size.Height;y++)for(int x=0;x<size.Width;x++)
                            {if(cropped!.Bitmap.GetPixel(x,y).ToArgb()!=direct.Bitmap.GetPixel(x,y).ToArgb())throw new Failure("RootCropPixels");pixels++;}
                        }
                        typeof(MainForm).GetProperty("HasCustomBackground",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(form,true);
                        using(var navigation=new NavigationPanel{Size=new Size(79,61)})
                        using(var target=new NativeResizeFrame(navigation.Size,form.DeviceDpi))
                            target.RenderOpaque(g=>Check(!coverage.TryPaintCachedRoot(navigation,g,size,Point.Empty,42),"CustomNavigationWashNotReplaced"));
                        long reserved=RenderSession.MaxBytes-session.Bytes;
                        Check(session.Reserve(reserved),"FillTestBudget");
                        Check(coverage.RootFrame(new Size(1000,800))==null,"NoOverspendOrStaleFrame");
                        session.Release(reserved);
                        Check(coverage.RootFrame(size)!=null && session.Bytes<=RenderSession.MaxBytes,"RecoverAfterBudgetFreed");
                        coverage.UseRootCache=false;Check(coverage.RootFrame(size)==null,"ComparisonSwitchUsesOriginalPath");
                        coverage.UseRootCache=true;Check(coverage.RootFrame(size)!=null,"ComparisonSwitchRestoresCache");
                        session.Failed();session.Failed();session.Failed();Check(coverage.RootFrame(size)==null,"DisabledSessionReleasesRoot");
                        form.Dispose();Check(session.Bytes==0,"AllSessionPixelsReleased");
                        Check(coverage.RootFrame(size)==null && session.Bytes==0,"DisposedCoverageCannotAllocate");
                    }
                    catch(Exception e){failure=e is Failure f?f.Code:"UnexpectedRootComposite";}
                    finally{if(!form.IsDisposed)form.Close();}
                };
                Application.Run(form);
            }
            catch{failure="RootWorkerFailed";}
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(40)))failure="RootCompositeDeadline";
        Console.WriteLine($"{(failure==null?"PASS":"FAIL")} root composite checks={checks}; rgba_pixels={pixels}; code={failure??"None"}; zero_tolerance=true");
        return failure==null?0:1;
        void Check(bool condition,string code){if(!condition)throw new Failure(code);checks++;}
    }
    private sealed class Failure(string code):Exception{internal string Code {get;}=code;}
}
