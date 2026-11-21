using DiskAtlas;
using System.Drawing.Drawing2D;

internal static class CacheTextProbe
{
    internal static void Run(Form owner)
    {
        using var form = new Form { ClientSize = new Size(700, 210), ShowInTaskbar = false };
        using var surface = new Surface { Dock = DockStyle.Fill };
        form.Controls.Add(surface); form.Show(owner);
        long compared=0;
        for(int background=0;background<3;background++)
        foreach(bool clipped in new[]{false,true})
        {
            surface.BackgroundCase=background;surface.Clipped=clipped;surface.Cached=false;
            using var direct=Capture();
            surface.Cached=true;
            using var cached=Capture();
            long diff=0;
            for(int y=0;y<surface.Height;y++)for(int x=0;x<surface.Width;x++)
            {
                if(direct.GetPixel(x,y).ToArgb()!=cached.GetPixel(x,y).ToArgb())diff++;
                compared++;
            }
            if(diff!=0)throw new InvalidOperationException($"OpaqueTextCachePixelMismatch:{background}:{clipped}:{diff}");
        }
        CheckLifetime(surface.DeviceDpi);
        Console.WriteLine($"PASS opaque text cache: cases=6; differing=0; pixels={compared}; dpi={surface.DeviceDpi}; lifetime=true");
        form.Close();

        Bitmap Capture()
        {
            var bitmap=new Bitmap(surface.Width,surface.Height);
            bitmap.SetResolution(surface.DeviceDpi,surface.DeviceDpi);
            surface.DrawToBitmap(bitmap,surface.ClientRectangle);
            return bitmap;
        }
    }
    private static void CheckLifetime(int dpi)
    {
        using var frame=new NativeResizeFrame(new Size(80,48),dpi);
        bool threw=false;
        try
        {
            frame.RenderOpaque(g=>
            {
                g.SetClip(new Rectangle(4,4,9,9));g.TranslateTransform(11,13);
                throw new InvalidOperationException("SyntheticPaintFailure");
            });
        }
        catch(InvalidOperationException e)when(e.Message=="SyntheticPaintFailure"){threw=true;}
        if(!threw)throw new InvalidOperationException("OpaquePaintFailureLost");
        frame.RenderOpaque(g=>g.Clear(Color.Coral));
        for(int y=0;y<48;y++)for(int x=0;x<80;x++)
            if(frame.Bitmap.GetPixel(x,y).ToArgb()!=Color.Coral.ToArgb())
                throw new InvalidOperationException("OpaqueStateOrAlphaNotRestored");
        bool wrongThread=Task.Run(()=>
        {
            try{frame.RenderOpaque(_=>{});return false;}
            catch(InvalidOperationException e)when(e.Message=="NativeFrameThread"){return true;}
        }).GetAwaiter().GetResult();
        if(!wrongThread)throw new InvalidOperationException("OpaqueThreadOwnershipLost");
        frame.Dispose();
        try{frame.RenderOpaque(_=>{});}
        catch(ObjectDisposedException){return;}
        throw new InvalidOperationException("OpaqueDisposedFrameAccepted");
    }
    private sealed class Surface : Control
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool Cached {get;set;}
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal int BackgroundCase {get;set;}
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool Clipped {get;set;}
        protected override void OnPaint(PaintEventArgs e)
        {
            if(!Cached) Draw(e.Graphics);
            else
            {
                using var surface=PixelSurface.Create(ClientSize,DeviceDpi,null,gdiText:true)
                    ?? throw new InvalidOperationException("OpaqueTextCacheUnavailable");
                surface.RenderOpaque(Draw);
                e.Graphics.DrawImageUnscaled(surface.Bitmap,0,0);
            }
        }
        private void Draw(Graphics graphics)
        {
            graphics.Clear(BackgroundCase==1?Color.FromArgb(73,54,51):Theme.Background);
            if(BackgroundCase!=1)
            {
                using var brush=new LinearGradientBrush(ClientRectangle,
                    BackgroundCase==0?Color.PeachPuff:Color.LightSteelBlue,Color.White,30f);
                graphics.FillRectangle(brush,ClientRectangle);
                if(BackgroundCase==2)
                {
                    using var tint=new SolidBrush(Color.FromArgb(70,Color.Salmon));
                    graphics.FillRectangle(tint,new Rectangle(33,0,103,Height));
                }
            }
            var state=graphics.Save();
            try
            {
                if(Clipped)
                {
                    using var clip=new Region(new Rectangle(28,16,241,84));
                    clip.Exclude(new Rectangle(71,23,12,16));
                    graphics.SetClip(clip,CombineMode.Intersect);
                }
                using var font=Theme.Font(11,FontStyle.Bold);
                using var size=Theme.Font(9.5f);
                var text=BackgroundCase==1?Color.WhiteSmoke:Theme.Text;
                Theme.TextAt(graphics,"合成 sample-000",font,text,new Rectangle(20,20,500,40),TextFormatFlags.SingleLine|TextFormatFlags.PreserveGraphicsClipping);
                Theme.TextAt(graphics,"3.52 KB",size,text,new Rectangle(20,70,500,40),TextFormatFlags.SingleLine|TextFormatFlags.PreserveGraphicsClipping);
            }
            finally{graphics.Restore(state);}
        }
    }
}
