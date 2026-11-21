using DiskAtlas;
using System.Drawing;
using System.Drawing.Imaging;

internal static class BackgroundPixelMappingTests
{
    internal static int Run()
    {
        int cases=0,failedCases=0;long pixels=0;string? failure=null;
        var thread=new Thread(()=>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
                Application.EnableVisualStyles();
                // First demonstrate the independent API contract: physical size
                // is different from our already-sized cache's pixel contract.
                using(var marker=new Bitmap(64,64,PixelFormat.Format32bppPArgb))
                using(var physical=new Bitmap(128,128,PixelFormat.Format32bppPArgb))
                {
                    marker.SetResolution(96,96);physical.SetResolution(144,144);
                    using(var g=Graphics.FromImage(marker))g.Clear(Color.Red);
                    using(var g=Graphics.FromImage(physical)){g.Clear(Color.White);g.DrawImageUnscaled(marker,0,0);}
                    int extent=0;for(int x=0;x<128;x++)if(physical.GetPixel(x,32).R>200&&physical.GetPixel(x,32).G<80)extent=x+1;
                    Console.WriteLine($"OBSERVE physical_size_copy source_px=64 source_dpi=96 target_dpi=144 visible_width={extent}");
                    // The filtered outer edge need not be fully red. Probe well
                    // inside/outside the 96px footprint, beyond the 64px source.
                    if(physical.GetPixel(80,32).G>=80 || physical.GetPixel(110,32).G!=255)
                        throw new Failure("IndependentPhysicalSizeContract");
                }
                using var form=new MainForm(null,dibCandidate:true);
                form.BackgroundImage?.Dispose();
                var image=new Bitmap(256,128,PixelFormat.Format32bppPArgb);image.SetResolution(96,96);
                for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++)image.SetPixel(x,y,Color.FromArgb((x*7+y*3)%256,(x+y*11)%256,(x*13+y*5)%256));
                form.BackgroundImage=image;
                foreach(int dpi in new[]{96,120,144,192})
                foreach(var size in new[]{new Size(180,120),new Size(260,160)})
                {
                    using var actual=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);
                    using var expected=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);
                    actual.SetResolution(dpi,dpi);expected.SetResolution(dpi,dpi);
                    var raster=form.WallpaperRaster.Get(image,size,form.BackgroundCrop)!;
                    using(var g=Graphics.FromImage(expected))
                    {
                        g.Clear(Theme.Background);
                        var area=new Rectangle(Point.Empty,size);
                        g.DrawImage(raster,area,area,GraphicsUnit.Pixel);
                        using var wash=new SolidBrush(Color.FromArgb(Wallpaper.SurfaceAlpha(form,WallpaperSurface.Ambient,42),Theme.Surface));
                        g.FillRectangle(wash,area);
                    }
                    using(var g=Graphics.FromImage(actual))Wallpaper.Paint(form,g,nativeSize:size,nativeCopy:false);
                    int different=0;
                    for(int y=0;y<size.Height;y++)for(int x=0;x<size.Width;x++)
                    {pixels++;if(actual.GetPixel(x,y).ToArgb()!=expected.GetPixel(x,y).ToArgb())different++;}
                    cases++;
                    Console.WriteLine($"BACKGROUND_PIXEL_MAPPING dpi={dpi} width={size.Width} height={size.Height} different={different}");
                    if(different!=0)failedCases++;
                }
                if(failedCases!=0)throw new Failure("BackgroundCacheDpiRescaled");
            }
            catch(Exception e){failure=e is Failure f?f.Code:"UnexpectedBackgroundPixelMapping";}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(30))){Console.Error.WriteLine("FAIL BackgroundPixelMappingDeadline");return 1;}
        if(failure!=null){Console.Error.WriteLine("FAIL "+failure);return 1;}
        Console.WriteLine($"PASS background pixel mapping cases={cases} pixels={pixels} zero_tolerance=True");return 0;
    }
    private sealed class Failure(string code):Exception { internal string Code=>code; }
}
