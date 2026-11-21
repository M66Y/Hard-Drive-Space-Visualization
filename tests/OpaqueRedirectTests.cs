using DiskAtlas;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class OpaqueRedirectTests
{
    internal static int Run(bool dpi96)
    {
        int checks=0;string? failure=null;bool finished=false;Action? close=null;
        var thread=new Thread(()=>
        {
            try
            {
                Application.SetHighDpiMode(dpi96?HighDpiMode.DpiUnaware:HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();Application.SetDefaultFont(new Font("Microsoft YaHei UI",9));
                VerifyFailure(new Fake{FailAdd=true},OpaqueWindowRedirect.State.SetupFailed,false);
                VerifyFailure(new Fake{FailOpaque=true},OpaqueWindowRedirect.State.SetupFailed,false);
                VerifyFailure(new Fake{FailVerify=true},OpaqueWindowRedirect.State.VerifyFailed,false);
                VerifyFailure(new Fake{FailOpaque=true,FailRemove=true},OpaqueWindowRedirect.State.RollbackFailed,true);
                foreach(int foreign in new[]{OpaqueWindowRedirect.Layered,OpaqueWindowRedirect.Transparent})
                {
                    using var f=new Form();_ = f.Handle;
                    var native=new Fake{Bits=foreign|0x40000};using var redirect=new OpaqueWindowRedirect(f,native);
                    Check(redirect.Status==OpaqueWindowRedirect.State.ForeignStyle&&!redirect.Active&&!redirect.OwnsLayer,"ForeignPolicyNotClaimed");
                    Check(native.SetCalls==0&&native.OpaqueCalls==0,"ForeignAttributesUntouched");
                }
                using(var f=new Harness())
                {
                    var native=new Fake();using var redirect=new OpaqueWindowRedirect(f,native);
                    Check(!f.IsHandleCreated&&redirect.Status==OpaqueWindowRedirect.State.Waiting,"NoHandleForcedBySubscription");
                    _ = f.Handle;Check(redirect.Active&&redirect.Attempts==1,"NewHandleInitialized");
                    native.Bits|=0x4000;redirect.Dispose();
                    Check(!redirect.OwnsLayer&&native.Bits==0x44000,"DisposePreservesUnrelatedStyleChanges");
                    int calls=native.SetCalls;f.Recreate();Check(native.SetCalls==calls,"DisposedSubscriptionCannotReinitialize");
                    redirect.Dispose();Check(native.SetCalls==calls,"DisposeIdempotent");
                }
                using(var f=new Harness())
                {
                    _ = f.Handle;var native=new Fake();
                    native.OnOpaque=()=>{native.OnOpaque=null;native.Bits=0x40000;f.Recreate();};
                    using var redirect=new OpaqueWindowRedirect(f,native);
                    Check(redirect.Active&&redirect.Attempts>=2,"ReentrantOldSetupCannotUndoNewHandle");
                }
                using var form=new MainForm(null,dibCandidate:true,opaqueRedirectCandidate:true);
                close=()=>{if(form.IsHandleCreated&&!form.IsDisposed)form.BeginInvoke((Action)form.Close);};
                var image=form.BackgroundImage;
                form.Shown+=async(_,_)=>
                {
                    try
                    {
                        await Task.Delay(80);
                        var redirect=form.OpaqueRedirect!;var session=form.Rendering!;
                        VerifyNative(form,redirect);
                        using(var reference=new MainForm(null,dibCandidate:true))
                        {
                            reference.Show();await Task.Delay(30);
                            Check(ReferenceEquals(image,form.BackgroundImage)&&reference.Font.Equals(form.Font)&&reference.Size==form.Size&&reference.MinimumSize==form.MinimumSize,"LayoutBackgroundFontMatchesOrdinaryDibAtSameDpi");
                            reference.Close();form.Activate();
                        }
                        var common=form.Size;
                        Check(form.Opacity==1d&&form.TransparencyKey==Color.Empty&&form.Visible,"WholeWindowRemainsFullyOpaqueAndVisible");
                        var metric=Field<ComboBox>(form,"metricCombo");metric.DroppedDown=true;await Task.Delay(40);
                        Check(metric.DroppedDown,"NativeDropDownOpens");metric.DroppedDown=false;
                        var search=Field<TextBox>(form,"search");search.Focus();search.Text="synthetic";
                        Check(search.Focused&&search.Text=="synthetic","NativeTextFocusPreserved");search.Clear();
                        var map=Field<TreemapControl>(form,"map");Field<CheckBox>(form,"reduceMotion").Checked=true;Check(!map.AnimationsEnabled,"ReducedMotionPreserved");
                        Field<CheckBox>(form,"reduceMotion").Checked=false;Check(map.AnimationsEnabled,"GlowMotionRestored");
                        for(int i=0;i<8;i++)
                        {form.Size=i%2==0?form.MinimumSize:common;await Task.Delay(30);VerifyNative(form,redirect);Check(session.Active&&session.Bytes<=RenderSession.MaxBytes,"ResizeRetainsApplicationBudget");}
                        form.WindowState=FormWindowState.Minimized;await Task.Delay(60);form.WindowState=FormWindowState.Normal;await Task.Delay(100);
                        VerifyNative(form,redirect);Check(form.Visible,"RestoreStillVisible");
                        int attempts=redirect.Attempts;
                        typeof(Control).GetMethod("RecreateHandle",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(form,null);
                        await Task.Delay(80);VerifyNative(form,redirect);Check(redirect.Attempts>attempts,"RealHandleRecreatedAndReinitialized");
                        form.Dispose();Check(session.Bytes==0&&!redirect.Active&&!redirect.OwnsLayer,"ClosedFormReleasesApplicationPixelsAndOwnership");
                        var gdiCounts=new List<int>();var userCounts=new List<int>();
                        for(int i=0;i<6;i++)
                        {
                            using var owned=new Harness();_=owned.Handle;
                            using var redirectCycle=new OpaqueWindowRedirect(owned);
                            Check(redirectCycle.Active,"RepeatedNativeSessionStarts");
                            owned.Dispose();Check(!redirectCycle.Active&&!redirectCycle.OwnsLayer,"DestroyedNativeSessionRetiresOwnership");
                            using var process=Process.GetCurrentProcess();gdiCounts.Add(GetGuiResources(process.Handle,0));userCounts.Add(GetGuiResources(process.Handle,1));
                        }
                        Check(gdiCounts.All(n=>n>0)&&gdiCounts[^1]<=gdiCounts[0],"NoGdiGrowthAcrossClosedNativeCycles");
                        Check(userCounts.All(n=>n>0)&&userCounts[^1]<=userCounts[0],"NoUserHandleGrowthAcrossClosedNativeCycles");
                        Console.WriteLine($"OPAQUE_RESOURCES closed_cycle_gdi={string.Join(',',gdiCounts)}; closed_cycle_user={string.Join(',',userCounts)}; system_redirect_bitmap_bytes=not_measured");
                    }
                    catch(Exception e){failure=e is Failure f?f.Code:"UnexpectedOpaqueRedirect";}
                    finally{if(!form.IsDisposed)form.Close();}
                };
                Application.Run(form);finished=true;
            }
            catch(Exception e){failure=e is Failure f?f.Code:"OpaqueRedirectWorker";}
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(45))){close?.Invoke();thread.Join(3000);failure="OpaqueRedirectDeadline";}
        Console.WriteLine($"{(failure==null?"PASS":"FAIL")} opaque redirect checks={checks}; code={failure??"None"}; worker_finished={finished}; alpha=255; native_ui=true; system_image_memory_not_measured=true");
        return failure==null?0:1;
        void Check(bool value,string code){if(!value)throw new Failure(code);checks++;}
        void VerifyFailure(Fake native,OpaqueWindowRedirect.State expected,bool owns)
        {
            using var f=new Form();_=f.Handle;using var redirect=new OpaqueWindowRedirect(f,native);
            Check(redirect.Status==expected&&!redirect.Active&&redirect.OwnsLayer==owns,"NativeFailureReportedWithoutFalseActive");
            if(!owns)Check((native.Bits&OpaqueWindowRedirect.Layered)==0,"SetupFailureRollsBackLayerBit");
        }
        void VerifyNative(Form form,OpaqueWindowRedirect redirect)
        {
            if(!redirect.Active)Console.WriteLine($"OBSERVE opaque state={redirect.Status}; attempts={redirect.Attempts}; owns={redirect.OwnsLayer}; layered={(GetWindowLong(form.Handle,-20)&OpaqueWindowRedirect.Layered)!=0}");
            Check(redirect.Active&&redirect.Status==OpaqueWindowRedirect.State.Active,"OpaqueRedirectActuallyActive");
            bool attributes=GetLayeredWindowAttributes(form.Handle,out _,out byte alpha,out uint flags);
            if(!attributes||alpha!=255||flags!=2)Console.WriteLine($"OBSERVE opaque attributes_valid={attributes}; alpha={alpha}; flags={flags}; layered={(GetWindowLong(form.Handle,-20)&OpaqueWindowRedirect.Layered)!=0}");
            Check(attributes&&alpha==255&&flags==2,"NativeAlpha255AndNoColorKey");
            Check((GetWindowLong(form.Handle,-20)&(OpaqueWindowRedirect.Layered|OpaqueWindowRedirect.Transparent))==OpaqueWindowRedirect.Layered,"LayeredWithoutClickThrough");
        }
    }
    private static T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
    private sealed class Failure(string code):Exception{internal string Code{get;}=code;}
    private sealed class Harness:Form{internal void Recreate()=>RecreateHandle();}
    private sealed class Fake:OpaqueWindowRedirect.INative
    {
        internal int Bits=0x40000,SetCalls,OpaqueCalls;
        internal bool FailAdd,FailOpaque,FailVerify,FailRemove;
        internal Action? OnOpaque;
        public int Style(IntPtr handle)=>Bits;
        public bool SetStyle(IntPtr handle,int value){SetCalls++;if((value&OpaqueWindowRedirect.Layered)!=0?FailAdd:FailRemove)return false;Bits=value;return true;}
        public bool SetOpaque(IntPtr handle){OpaqueCalls++;OnOpaque?.Invoke();return !FailOpaque;}
        public bool IsOpaque(IntPtr handle)=>!FailVerify;
    }
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")]private static extern int GetWindowLong(IntPtr h,int index);
    [DllImport("user32.dll")]private static extern bool GetLayeredWindowAttributes(IntPtr h,out uint color,out byte alpha,out uint flags);
    [DllImport("user32.dll")]private static extern int GetGuiResources(IntPtr process,uint flag);
}
