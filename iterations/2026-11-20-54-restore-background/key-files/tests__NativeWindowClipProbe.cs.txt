using DiskAtlas;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class NativeWindowClipProbe
{
    internal static int Run(bool dpi96, bool verify=false)
    {
        string? failed=null;object? result=null;
        var thread=new Thread(()=>
        {
            try
            {
                Application.SetHighDpiMode(dpi96?HighDpiMode.DpiUnaware:HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
                using var form=new Form{ClientSize=new Size(600,400),ShowInTaskbar=false};
                using var panel=new Panel{Bounds=new Rectangle(123,95,193,127)};form.Controls.Add(panel);form.Show();Application.DoEvents();
                using var source=new NativeResizeFrame(panel.ClientSize,panel.DeviceDpi);source.Graphics.Clear(Color.SteelBlue);
                var rows=new List<object>();
                foreach(bool hole in new[]{false,true})
                {
                    IntPtr dc=GetDCEx(panel.Handle,IntPtr.Zero,0x2|0x8|0x10);if(dc==IntPtr.Zero)throw new InvalidOperationException();
                    try
                    {
                        using var graphics=Graphics.FromHdc(dc);
                        graphics.Clear(Color.Coral);graphics.Flush(FlushIntention.Sync);
                        using var clip=new Region(new Rectangle(20,10,41,27));if(hole)clip.Exclude(new Rectangle(31,14,9,9));
                        graphics.SetClip(clip,CombineMode.Replace);
                        IntPtr region=clip.GetHrgn(graphics);Rect native;
                        try{GetRgnBox(region,out native);}finally{DeleteObject(region);}
                        bool copied=source.TryCopyTo(graphics,panel.ClientRectangle);graphics.Flush(FlushIntention.Sync);
                        if(verify && !copied)throw new InvalidOperationException();
                        var pixels=new List<object>();
                        foreach(var p in new[]{new Point(5,5),new Point(25,15),new Point(35,18),new Point(50,25),new Point(100,50)})
                        {
                            bool inside=new Rectangle(20,10,41,27).Contains(p)&&(!hole||!new Rectangle(31,14,9,9).Contains(p));
                            var expected=copied&&inside?Color.SteelBlue:Color.Coral;uint rgb=GetPixel(dc,p.X,p.Y);
                            int actual=unchecked((int)((rgb&255)<<16|(rgb&0xff00)|(rgb>>16&255)));
                            pixels.Add(new{x=p.X,y=p.Y,equal=actual==(expected.ToArgb()&0xffffff),actual_rgb=actual,expected_rgb=expected.ToArgb()&0xffffff});
                            if(verify && actual!=(expected.ToArgb()&0xffffff))throw new InvalidOperationException();
                        }
                        rows.Add(new{hole,copied,failure=source.LastCopyFailure.ToString(),clip=new{native.Left,native.Top,native.Right,native.Bottom},pixels});
                    }
                    finally{ReleaseDC(panel.Handle,dc);}
                }
                result=new{dpi=panel.DeviceDpi,rows,scope="owned plain native HWND; no screenshot"};form.Close();
            }
            catch{failed="NativeClipObservationFailed";}
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(20)))failed="NativeClipDeadline";
        if(failed!=null){Console.WriteLine("FAIL "+failed);return 1;}
        string path=Path.Combine("artifacts","restore-background-C54",$"native-clip-{(verify?"verified-":"")}{(dpi96?96:144)}.json");
        if(File.Exists(path))return 2;Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(verify?"PASS native HWND clip checks=12; exact_rgb=true":"PASS native HWND clip observation; semantic_result_in_report=true");return 0;
    }
    [StructLayout(LayoutKind.Sequential)]private struct Rect{internal int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern IntPtr GetDCEx(IntPtr window,IntPtr region,uint flags);
    [DllImport("user32.dll")]private static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")]private static extern uint GetPixel(IntPtr dc,int x,int y);
    [DllImport("gdi32.dll")]private static extern int GetRgnBox(IntPtr region,out Rect rect);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(IntPtr value);
}
