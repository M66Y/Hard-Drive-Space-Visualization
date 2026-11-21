using DiskAtlas;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class CompositionMainFormTests
{
    internal static int Run(bool dpi96)
    {
        int checks=0; string? failure=null;
        string data=Path.Combine(Path.GetTempPath(),"AsukaLens-C57-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var thread=new Thread(()=>
        {
            try
            {
                Application.SetHighDpiMode(dpi96?HighDpiMode.DpiUnaware:HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles(); Application.SetDefaultFont(new Font("Microsoft YaHei UI",9));
                AppContext.SetSwitch("AsukaLens.TraceComposition",false);
                using var form=new MainForm(null,backgroundSettingsPath:Path.Combine(data,"background.dat"),compositionCandidate:true);
                form.Shown+=async(_,_)=>
                {
                    try
                    {
                        await Task.Delay(80); Verify();
                        string scope=Path.Combine(data,"scope");Directory.CreateDirectory(scope);
                        for(int i=0;i<4;i++)
                        {
                            string child=Path.Combine(scope,"group-"+i);Directory.CreateDirectory(child);
                            for(int j=0;j<15;j++)File.WriteAllBytes(Path.Combine(child,$"sample-{j}.bin"),new byte[4096*(i+1)]);
                        }
                        await (Task)typeof(MainForm).GetMethod("ScanFolder",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(form,[scope])!;
                        Verify();
                        using var probe=new ReadyProbe(form);
                        Size common=form.Size;
                        for(int i=0;i<12;i++)
                        {
                            form.Size=i%2==0?form.MinimumSize:common;
                            await Task.Delay(30); Verify();
                        }
                        Check(probe.Observed>0 && probe.Mismatched==0,"NextBackgroundCommittedBeforeExposedClientPaint");
                        Console.WriteLine($"COMPOSITION_READINESS observed={probe.Observed} mismatched={probe.Mismatched} screen_acceptance=False");
                        var combo=Field<ComboBox>("metricCombo");combo.DroppedDown=true;await Task.Delay(30);
                        Check(combo.DroppedDown,"ProductionComboOpens");combo.DroppedDown=false;
                        var search=Field<TextBox>("search");search.Focus();search.Text="sample";
                        Check(search.Focused&&search.Text=="sample","ProductionInputFocus");search.Clear();
                        Field<CheckBox>("reduceMotion").Checked=true;Check(!Field<TreemapControl>("map").AnimationsEnabled,"ReduceMotionPreserved");
                        Field<CheckBox>("reduceMotion").Checked=false;Check(Field<TreemapControl>("map").AnimationsEnabled,"GlowPreserved");
                        foreach(var state in new[]{FormWindowState.Maximized,FormWindowState.Normal,FormWindowState.Minimized,FormWindowState.Normal})
                        {form.WindowState=state;await Task.Delay(80);if(state!=FormWindowState.Minimized)Verify();}
                        int publications=form.CompositionBackdrop!.Publications;
                        var image=new Bitmap(160,100);using(var g=Graphics.FromImage(image))g.Clear(Color.PeachPuff);
                        typeof(MainForm).GetMethod("ApplyBackground",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(form,
                            [image,new BackgroundView(.8f,.15f,1.3f,null),Color.Firebrick,false]);
                        Check(form.CompositionBackdrop.Publications>publications,"BackgroundCommitPublishesEvenWithoutRootPaint");
                        Verify();
                        var backing=form.CompositionBackdrop!;var session=form.Rendering!;IntPtr source=backing.SourceHandle;
                        form.Dispose();Check(!IsWindow(source)&&backing.Status==CompositionBackdrop.State.Disposed&&session.Bytes==0,"ProductionOwnerReleasesAllOwnedResources");
                    }
                    catch(Exception e){failure=e is Failure f?f.Code:$"Unexpected_{e.GetType().Name}_{e.HResult:X8}";}
                    finally{form.Close();}
                };
                Application.Run(form);
                void Verify()
                {
                    var b=form.CompositionBackdrop;
                    Check(b?.Active==true,"ProductionBackdropActive");
                    Check(b!.PublishedSize==form.ClientSize,"ProductionExactSize");
                    Check(form.OpaqueRedirect==null && form.Opacity==1 && form.TransparencyKey==Color.Empty,"C56NotRequiredAndNoTransparency");
                    Check(form.Rendering!.Bytes<=RenderSession.MaxBytes,"ProductionApplicationBudget");
                }
                T Field<T>(string name)=>(T)typeof(MainForm).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
            }
            catch(Exception e){failure=e is Failure f?f.Code:$"Unexpected_{e.GetType().Name}_{e.HResult:X8}";}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(90))){Console.Error.WriteLine("FAIL CompositionMainFormTimeout");return 1;}
        // Only the just-created random fixture is removed after the UI exited.
        if(!Path.GetFullPath(data).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetTempPath())+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(data)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("FixtureOwnershipLost");
        Directory.Delete(data,true);
        if(failure!=null){Console.Error.WriteLine("FAIL "+failure);return 1;}
        Console.WriteLine($"PASS CompositionMainForm checks={checks} dpi={(dpi96?96:144)}");return 0;
        void Check(bool value,string code){checks++;if(!value)throw new Failure(code);}
    }
    private sealed class Failure(string code):Exception{internal string Code=>code;}
    private sealed class ReadyProbe:NativeWindow,IDisposable
    {
        private readonly MainForm form;
        internal int Observed,Mismatched;
        internal ReadyProbe(MainForm form){this.form=form;AssignHandle(form.Handle);}
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==0x85 && !IsIconic(Handle) && GetClientRect(Handle,out var client))
            {
                Observed++;
                if(form.CompositionBackdrop is not {Active:true} b || b.PublishedSize!=new Size(client.Right,client.Bottom))Mismatched++;
            }
            base.WndProc(ref m);
        }
        public void Dispose()=>ReleaseHandle();
    }
    [StructLayout(LayoutKind.Sequential)]private struct Rect{internal int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")]private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr hwnd);
}
