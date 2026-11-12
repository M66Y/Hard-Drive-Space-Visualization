using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

// In-memory source operations and synthetic space trees only; never calls the
// real corpus creator, guard factory, scanner, volume lock or benchmark runner.
internal static class NtfsScaleExperimentTests
{
    private sealed class Fake:NtfsScaleExperiment.IBackend,NtfsRawVolumeLease.INative
    {
        private readonly byte[] image=new byte[1024*1024];private long offset;private bool disposed;
        private readonly FileStorageInfo root=new(new(41,NtfsRecordFixtures.Reference(1,3),0),0,1,false,-1,new DateTime(2020,1,1));
        internal int Locks,Unlocks,Reads,Allocations,Frees,Disposes,AfterCalls;
        internal bool LockFails,ShortRead,AfterFails,FactsChange,DisposeFails;
        internal Action? OnRead,OnDispose;
        public SafeFileHandle Volume{get;}=new(new IntPtr(12345),false);
        public string DataRoot=>@"X:\SyntheticScale";
        internal Fake()
        {
            Put(0,NtfsMftMapTests.Record0(extra:NtfsRecordFixtures.Resident(0xb0,new byte[]{7})));
            Put(1,NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("SyntheticScale"),NtfsRecordFixtures.Data(0)],directory:true));
            Put(2,NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("file.bin",3,root.Identity.Low),NtfsRecordFixtures.Nonresident(logical:1024)]));
        }
        private void Put(int slot,byte[] value)=>value.CopyTo(image,10*4096+slot*1024);
        public FileStorageInfo Root(CancellationToken token){token.ThrowIfCancellationRequested();return root;}
        public NtfsRawVolumeFacts Facts(bool reopen,CancellationToken token)
        {token.ThrowIfCancellationRequested();return new(new(512,4096,1024,image.Length,image.Length/4096,10,reopen&&FactsChange?5120:4096),41,4096);}
        public IReadOnlyDictionary<ulong,FileStorageInfo> Before(CancellationToken token){token.ThrowIfCancellationRequested();return new Dictionary<ulong,FileStorageInfo>();}
        public void After(IReadOnlyDictionary<ulong,FileStorageInfo> _,CancellationToken token)
        {token.ThrowIfCancellationRequested();AfterCalls++;if(Unlocks!=1||Disposes!=0)throw new IOException("SyntheticLifecycle");if(AfterFails)throw new IOException("SyntheticPrimaryAfter");}
        public NtfsRawVolumeLease.NativeResult Lock(SafeFileHandle _){Locks++;return new(!LockFails,LockFails?5:0);}
        public NtfsRawVolumeLease.NativeResult Unlock(SafeFileHandle _){Unlocks++;return new(true,0);}
        public NtfsRawVolumeLease.NativeResult Seek(SafeFileHandle _,long value){offset=value;return new(true,0);}
        public NtfsRawVolumeLease.NativeReadResult Read(SafeFileHandle _,IntPtr buffer,int length)
        {Reads++;int count=ShortRead?length-512:length;Marshal.Copy(image,(int)offset,buffer,count);OnRead?.Invoke();return new(true,count,0);}
        public IntPtr Allocate(int bytes){Allocations++;return Marshal.AllocHGlobal(bytes);}
        public void Free(IntPtr pointer){Frees++;Marshal.FreeHGlobal(pointer);}
        public void Dispose(){if(disposed)return;disposed=true;Disposes++;Volume.Dispose();OnDispose?.Invoke();if(DisposeFails)throw new IOException("SyntheticDispose");}
    }
    internal static int Run()
    {
        int passed=0;
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("Scale experiment check failed: "+name);passed++;Console.WriteLine("PASS scale experiment "+name);}
        Exception? Catch(Action action){try{action();return null;}catch(Exception error){return error;}}
        foreach(int count in new[]{10000,100000})
        {
            var work=NtfsScaleExperiment.Workload(count);
            Check(work.Files==count&&work.Directories==100&&work.HardlinkAliases==10&&work.ResidentFiles==count/10&&work.NonresidentFiles==count*8/10&&work.EmptyFiles==count/10,
                "workload fixes a representative ten-eighty-ten mixture and ten aliases at either authorized scale");
            var sizes=Enumerable.Range(0,count).GroupBy(NtfsScaleExperiment.ExpectedLength).ToDictionary(group=>group.Key,group=>group.Count());
            Check(sizes[32]==count/10&&sizes[1024]==count*8/10&&sizes[0]==count/10,"generated lengths exactly match the declared resident nonresident and empty mixture");
            var parents=new HashSet<string>();for(int index=0;index<count;index++)
            {
                string path=NtfsScaleExperiment.FilePath(@"X:\SyntheticScale",count,index);parents.Add(Path.GetDirectoryName(path)!);
                if(index==0||index==count-1)Check(Path.GetFileNameWithoutExtension(path).Length==8&&Path.GetExtension(path)==".dat",
                    "generated names stay in the intended eight-dot-three protocol without long-name ambiguity");
            }
            Check(parents.Count==100,"both scales distribute their files among exactly one hundred fixed directories");
        }
        foreach(int count in new[]{-1,0,9999,10001,int.MaxValue})Check(Catch(()=>NtfsScaleExperiment.Workload(count)) is NtfsScaleException {Code:"ScaleCount"},
            "unsupported corpus sizes cannot become arbitrary file-creation budgets");
        using(var fake=new Fake())
        {
            var result=NtfsScaleExperiment.ReadForTests(fake,fake);
            Check(result.Scan.Root is {FileCount:1,FolderCount:0,Size:1024,AllocatedSize:8192}&&result.ReadCalls==2&&result.RequestedBytes==5120&&
                result.FallbackObjects==0,"scale source returns a materialized space tree with complete bootstrap and MFT request accounting");
            Check(fake.Unlocks==1&&fake.Frees==1&&fake.Disposes==1&&fake.AfterCalls==1,"scale source unlocks before verification and releases each owner once");
        }
        foreach(string fault in new[]{"Lock","Short","After","Facts","Dispose"})
        {
            using var fake=new Fake{LockFails=fault=="Lock",ShortRead=fault=="Short",AfterFails=fault=="After",FactsChange=fault=="Facts",DisposeFails=fault=="Dispose"};
            NtfsScaleExperiment.ReadResult? result=null;
            Check(Catch(()=>result=NtfsScaleExperiment.ReadForTests(fake,fake)) is IOException&&result==null&&fake.Disposes==1&&fake.Frees==fake.Allocations&&
                fake.Unlocks==(fault=="Lock"?0:1),"scale source failure never returns a partial tree or leaks its buffer lock and context");
        }
        foreach(bool cleanupFails in new[]{false,true})
        {
            using var fake=new Fake{DisposeFails=cleanupFails};using var cts=new CancellationTokenSource();fake.OnDispose=cts.Cancel;
            Check(Catch(()=>NtfsScaleExperiment.ReadForTests(fake,fake,cts.Token)) is OperationCanceledException&&fake.Disposes==1&&fake.Unlocks==1&&fake.Frees==1,
                "final-disposal cancellation prevents success and takes priority over a simultaneous cleanup failure");
        }
        {
            using var fake=new Fake{ShortRead=true,DisposeFails=true};using var cts=new CancellationTokenSource();fake.OnDispose=cts.Cancel;
            Check(Catch(()=>NtfsScaleExperiment.ReadForTests(fake,fake,cts.Token)) is NtfsRawVolumeException {Code:"ShortRead"}&&fake.Disposes==1,
                "an established read failure survives cancellation and failure from subsequent owner cleanup");
        }
        {
            using var fake=new Fake();using var cts=new CancellationTokenSource();fake.OnRead=cts.Cancel;
            Check(Catch(()=>NtfsScaleExperiment.ReadForTests(fake,fake,cts.Token)) is OperationCanceledException&&fake.Unlocks==1&&fake.Frees==1&&fake.Disposes==1,
                "cancellation after a synchronous native read returns no source tree");
        }
        var tree=Tree(10000);var scan=new ScanResult(tree,TimeSpan.Zero,0,[]);byte[] fingerprint=NtfsScaleExperiment.Fingerprint(scan,10000);
        var directory=tree.Children[0];var linked=directory.Children.Where(child=>child.Storage!.LinkCount==2).ToArray();
        linked[0].AllocatedSize=0;linked[1].AllocatedSize=4096;directory.AllocatedSize+=123;
        Check(NtfsScaleExperiment.Fingerprint(scan,10000).SequenceEqual(fingerprint),"fingerprint ignores alias allocation ownership and directory allocation attribution while preserving root totals");
        linked[0].Size++;
        Check(!NtfsScaleExperiment.Fingerprint(scan,10000).SequenceEqual(fingerprint),"fingerprint still detects per-alias logical-size differences");
        linked[0].Size--;tree.AllocatedSize++;
        Check(!NtfsScaleExperiment.Fingerprint(scan,10000).SequenceEqual(fingerprint),"fingerprint independently checks the deduplicated root allocation total");
        return passed;
    }
    private static DiskEntry Tree(int files)
    {
        var root=new DiskEntry(@"X:\SyntheticScale",true){Storage=new(new(41,NtfsRecordFixtures.Reference(1,3),0),0,1,false,-1)};int per=files/100;
        for(int directory=0;directory<100;directory++)
        {
            string parent=Path.GetDirectoryName(NtfsScaleExperiment.FilePath(root.Path,files,directory*per))!;
            var folder=new DiskEntry(parent,true,root){Storage=new(new(41,NtfsRecordFixtures.Reference((ulong)(2+directory),3),0),0,1,false,-1)};root.MutableChildren.Add(folder);
            for(int local=0;local<per;local++)
            {
                int index=directory*per+local;long length=NtfsScaleExperiment.ExpectedLength(index);var storage=new FileStorageInfo(new(41,NtfsRecordFixtures.Reference((ulong)(102+index),3),0),
                    length==1024?4096:0,directory<10&&local==1?2u:1u,false,length);
                var file=new DiskEntry(NtfsScaleExperiment.FilePath(root.Path,files,index),false,folder){Storage=storage,Size=length,FileCount=1};folder.MutableChildren.Add(file);
                if(directory<10&&local==1)folder.MutableChildren.Add(new DiskEntry(Path.Combine(parent,"h"+directory.ToString("D7")+".dat"),false,folder){Storage=storage,Size=length,FileCount=1});
            }
            folder.FileCount=folder.Children.Count;folder.Size=folder.Children.Sum(file=>file.Size);root.FileCount+=folder.FileCount;root.Size+=folder.Size;root.FolderCount++;
        }
        StorageMetrics.Calculate(root,default);return root;
    }
}
