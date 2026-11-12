using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// Synthetic namespace inventories, raw pages and metadata callbacks only. No
// snapshot owner, filesystem handle, path opening or source proof is created.
internal static class NtfsSnapshotSizeIndexTests
{
    private static readonly DateTime Time=new(2020,1,1);
    private static FileIdentity Id(ulong slot,ushort sequence=3)=>new(41,NtfsRecordFixtures.Reference(slot,sequence),0);
    private static NtfsMftMap Map(int slots)
    {
        int clusters=(slots+3)/4;long bytes=slots*1024L;
        var raw=NtfsMftMapTests.Record0([0x12,(byte)clusters,(byte)(clusters>>8),10,0],(ulong)(clusters-1),clusters*4096L,bytes,bytes);
        return NtfsMftMap.Parse(raw,new(512,4096,1024,1024L*1024*1024,262144,10,bytes)).Map!;
    }
    private static NtfsSnapshotNamespaceResult Scope(params NtfsSnapshotNamespaceEntry[] descendants)
    {
        var entries=new[]{new NtfsSnapshotNamespaceEntry(0,-1,"",Id(1),FileAttributes.Directory,false)}.Concat(descendants).ToArray();
        int dirs=entries.Skip(1).Count(entry=>entry.Directory),files=entries.Length-1-dirs;
        var report=new NtfsSnapshotNamespaceReport("snapshot-namespace-v1",entries.Length,files,dirs,entries.Where(entry=>!entry.Directory).Select(entry=>entry.Identity).Distinct().Count(),
            entries.Count(entry=>entry.Reparse),dirs+1,dirs+1,2*(dirs+1),entries.Sum(entry=>entry.Name.Length),dirs+1,true);
        var paths=new string[entries.Length];paths[0]="";for(int i=1;i<entries.Length;i++)paths[i]=entries[i].Name;
        return new(report,Array.AsReadOnly(entries),Array.AsReadOnly(paths),Id(1));
    }
    private static NtfsSnapshotNamespaceEntry File(int index,string name,ulong slot)=>new(index,0,name,Id(slot),FileAttributes.Archive,false);
    private static byte[] Root()=>NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("scope"),NtfsRecordFixtures.Data(0)],directory:true);
    private static byte[] Resident()=>NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("s.bin",parent:Id(1).Low),NtfsRecordFixtures.Data(32)]);
    private static NtfsSnapshotNativeSize Native(NtfsSnapshotSizeRequest request,long bytes=32)=>new(new(request.Identity,bytes,1,false,request.Directory?-1:32,Time),request.Directory);

    internal static int Run()
    {
        int passed=0;
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("Snapshot size check failed: "+name);passed++;Console.WriteLine("PASS snapshot size "+name);}
        Exception? Catch(Action action){try{action();return null;}catch(Exception error){return error;}}
        var scope=Scope(File(1,"r.bin",2),File(2,"h.bin",2),File(3,"s.bin",65));
        var raw=new Dictionary<ulong,byte[]>{{1,Root()},{2,NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("r.bin",parent:Id(1).Low),
            NtfsRecordFixtures.Name("h.bin",parent:Id(1).Low),NtfsRecordFixtures.Nonresident()],links:2)},{65,Resident()}};
        int reads=0,calls=0,callerThread=Environment.CurrentManagedThreadId;bool rawOnCaller=true;
        int Read(long offset,Memory<byte> destination,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();reads++;rawOnCaller&=Environment.CurrentManagedThreadId==callerThread;destination.Span.Clear();
            foreach(var pair in raw){long position=(long)pair.Key*1024-offset;if(position>=0&&position<=destination.Length-1024)pair.Value.CopyTo(destination.Span[(int)position..]);}
            return destination.Length;
        }
        NtfsSnapshotNativeSize Lookup(NtfsSnapshotSizeRequest request,CancellationToken token){token.ThrowIfCancellationRequested();Interlocked.Increment(ref calls);return Native(request);}
        var result=NtfsSnapshotSizeIndex.BuildAsync(scope,Map(128),Read,_=>true,Lookup).GetAwaiter().GetResult();
        Check(result.Report is {ScopeObjects:3,FileObjects:2,DirectoryObjects:0,FileEntries:3,RawResolvedObjects:2,NativeResolvedObjects:1,
            NativeCalls:1,DecodedScopeRecords:3,RawPagesRead:2,UnknownObjects:0,LogicalBytes:12032,AllocatedBytes:8224,Complete:true},
            "selected scope retains compact sizes and both hard-link size conventions with one resident supplement");
        Check(reads==2&&calls==1&&rawOnCaller&&result.Objects[Id(65)].Native&&result.Objects[Id(2)].Links==2,
            "raw callbacks stay on the owner thread and supplementation occurs once per unique unknown object");
        Check(result.Report.RawReadMilliseconds>=0&&result.Report.ParseMilliseconds>=0&&result.Report.IndexMilliseconds>=0&&result.Report.NativeMilliseconds>=0,
            "stage costs remain explicit without combining namespace owner creation or cleanup timings");
        bool immutable=false;try{((IDictionary<FileIdentity,NtfsSnapshotObjectSize>)result.Objects).Clear();}catch(NotSupportedException){immutable=true;}
        Check(immutable&&JsonSerializer.Serialize(result)=="{}"&&!JsonSerializer.Serialize(result.Report).Contains("Identity",StringComparison.Ordinal),
            "result object table is immutable and no namespace names raw bodies or identities enter the safe report");
        Check(raw[65].SequenceEqual(Resident()),"strict parsing never rewrites the caller raw fixture bytes");
        var projected=NtfsSnapshotSizeIndex.Materialize(result,"S:\\SyntheticScope");
        Check(projected is {Size:12032,AllocatedSize:8224,FileCount:3,FolderCount:0,UnknownAllocationFiles:0}&&
            projected.Children.Count==3&&projected.Children.Count(child=>child.SharedAllocation)==1&&
            projected.Children.Where(child=>child.Storage?.Identity==Id(2)).All(child=>child.Storage?.AllocatedBytes==8192),
            "pure product tree projection preserves entry logical bytes object allocation and hard-link sharing");
        Check(projected.Storage?.Identity==Id(1)&&projected.Children.All(child=>child.Parent==projected)&&
            projected.Children.Single(child=>child.Name=="s.bin").Storage?.AllocatedBytes==32,
            "projection retains scalar identities and parents without a live source token or path access");
        using(var cancelledProjection=new CancellationTokenSource())
        {
            cancelledProjection.Cancel();Check(Catch(()=>NtfsSnapshotSizeIndex.Materialize(result,"S:\\SyntheticScope",cancelledProjection.Token)) is OperationCanceledException,
                "cancelled projection publishes no partial product tree");
        }
        {
            var entries=scope.Entries.ToArray();var paths=new[]{"","r.bin","h.bin","s.bin"};
            var mutableInput=new NtfsSnapshotNamespaceResult(scope.Report,entries,paths,scope.RootIdentity);
            var owned=NtfsSnapshotSizeIndex.BuildAsync(mutableInput,Map(128),Read,_=>true,Lookup).GetAwaiter().GetResult();
            entries[1]=entries[1] with{Name="changed.bin",ParentIndex=42};paths[1]="changed.bin";
            var stable=NtfsSnapshotSizeIndex.Materialize(owned,"S:\\SyntheticScope");
            Check(stable.Children[0].Name=="r.bin"&&owned.Namespace.RelativePath(1)=="r.bin"&&stable.Size==12032,
                "later caller inventory changes cannot redirect or corrupt the private projected inventory");
        }
        {
            var nested=Scope(new(1,0,"sub",Id(3),FileAttributes.Directory,false),
                File(2,"r.bin",2) with{ParentIndex=1},File(3,"h.bin",2),File(4,"s.bin",65) with{ParentIndex=1});
            raw.Add(3,NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("sub",parent:Id(1).Low),NtfsRecordFixtures.Data(0)],directory:true));
            try
            {
                var nestedResult=NtfsSnapshotSizeIndex.BuildAsync(nested,Map(128),Read,_=>true,Lookup).GetAwaiter().GetResult();
                var tree=NtfsSnapshotSizeIndex.Materialize(nestedResult,"S:\\SyntheticScope");
                var sub=tree.Children.Single(child=>child.IsDirectory);
                Check(tree is{Size:12032,AllocatedSize:8224,FileCount:3,FolderCount:1}&&sub is{Size:6032,FileCount:2,FolderCount:0}&&
                    sub.Children.All(child=>child.Parent==sub)&&sub.Children.Single(child=>child.Name=="r.bin").Storage?.Identity==tree.Children.Single(child=>child.Name=="h.bin").Storage?.Identity,
                    "nested projection computes folder counts and global hard-link accounting without relying on alias owner location");
            }
            finally{raw.Remove(3);}
        }
        {
            byte[] standard=new byte[48];NtfsRecordFixtures.U32(standard,32,0x800);
            raw.Add(3,NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("sub",parent:Id(1).Low),NtfsRecordFixtures.Data(0),
                NtfsRecordFixtures.Resident(0x10,standard)],directory:true,addStandard:false));
            try
            {
                bool directoryRequest=false;var directoryScope=Scope(new NtfsSnapshotNamespaceEntry(1,0,"sub",Id(3),FileAttributes.Directory,false));
                var supplemented=NtfsSnapshotSizeIndex.BuildAsync(directoryScope,Map(128),Read,_=>true,(request,token)=>
                {directoryRequest=request.Directory&&request.ExpectedLogicalBytes==null;return Native(request,0);}).GetAwaiter().GetResult();
                Check(directoryRequest&&supplemented.Report is{DirectoryObjects:1,NativeResolvedObjects:1,LogicalBytes:0,AllocatedBytes:0}&&
                    NtfsSnapshotSizeIndex.Materialize(supplemented,"S:\\SyntheticScope").FolderCount==1,
                    "directory supplementation validates the Native minus-one EOF sentinel without treating directory logical zero as a file EOF");
            }
            finally{raw.Remove(3);}
        }
        var knownZero=NtfsSnapshotSizeIndex.BuildAsync(scope,Map(128),Read,_=>true,(request,token)=>Native(request,0)).GetAwaiter().GetResult();
        Check(knownZero.Objects[Id(65)] is {AllocatedBytes:0,Native:true}&&knownZero.Report.UnknownObjects==0&&knownZero.Report.AllocatedBytes==8192,
            "a verified Native zero remains distinct from unresolved resident allocation");
        {
            var saved=raw[2];
            raw[2]=NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("r.bin",parent:Id(1).Low),NtfsRecordFixtures.Name("h.bin",parent:Id(1).Low),
                NtfsRecordFixtures.Name("outside.bin",parent:Id(90).Low),NtfsRecordFixtures.Nonresident()],links:3);
            var outside=NtfsSnapshotSizeIndex.BuildAsync(scope,Map(128),Read,_=>true,Lookup).GetAwaiter().GetResult();
            Check(outside.Objects[Id(2)].Links==3&&outside.Report.FileEntries==3&&outside.Report.LogicalBytes==12032,
                "hard links outside selected namespace do not inflate scoped logical bytes or force equality with scoped alias count");
            raw[2]=NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("r.bin",parent:Id(1).Low),NtfsRecordFixtures.Name("h.bin",parent:Id(1).Low),
                NtfsRecordFixtures.Name("R~1.BIN",2,Id(1).Low),NtfsRecordFixtures.Nonresident()],links:3);
            var dos=NtfsSnapshotSizeIndex.BuildAsync(scope,Map(128),Read,_=>true,Lookup).GetAwaiter().GetResult();
            Check(dos.Objects[Id(2)].Links==2&&!dos.Objects[Id(2)].Native,
                "DOS-only name attributes never become Native hard links in the compact object size table");
            raw[2]=NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("r.bin",parent:Id(1).Low),NtfsRecordFixtures.Nonresident()],links:2);
            var incompleteNames=NtfsSnapshotSizeIndex.BuildAsync(scope,Map(128),Read,_=>true,(request,token)=>request.Identity==Id(2)?
                new(new(request.Identity,8192,2,false,6000,Time),false):Native(request)).GetAwaiter().GetResult();
            Check(incompleteNames.Objects[Id(2)].Native&&incompleteNames.Report.NativeResolvedObjects==2,
                "incomplete raw name-link evidence is preserved as one bounded same-snapshot Native supplement");
            raw[2]=saved;
        }

        foreach(string conflict in new[]{"Bitmap","Free","Sequence","Directory","Invalid","UnsupportedSequence","UnsupportedFree","UnsupportedDirectory","Extension"})
        {
            reads=0;calls=0;var saved=raw[65];
            if(conflict=="Free")raw[65]=NtfsRecordFixtures.Record([],inUse:false);
            if(conflict=="Sequence"){raw[65]=Resident();NtfsRecordFixtures.U16(raw[65],16,4);}
            if(conflict=="Directory")raw[65]=NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("dir")],directory:true);
            if(conflict=="Invalid")raw[65]=new byte[1024];
            if(conflict.StartsWith("Unsupported",StringComparison.Ordinal))
            {
                raw[65]=Resident();NtfsRecordFixtures.U16(raw[65],22,(ushort)(conflict=="UnsupportedFree"?4:conflict=="UnsupportedDirectory"?7:5));
                if(conflict=="UnsupportedSequence")NtfsRecordFixtures.U16(raw[65],16,4);
            }
            if(conflict=="Extension"){raw[65]=Resident();NtfsRecordFixtures.U64(raw[65],32,Id(20).Low);}
            NtfsSnapshotSizeIndexResult? partial=null;
            var error=Catch(()=>partial=NtfsSnapshotSizeIndex.BuildAsync(scope,Map(128),Read,slot=>conflict!="Bitmap"||slot!=65,Lookup).GetAwaiter().GetResult());
            Check(error is NtfsSnapshotSizeException {Code:"SourceConsistencyFailed"}&&calls==0&&partial==null,
                "explicit allocation free identity type or integrity contradiction aborts the complete attempt before native repair");
            raw[65]=saved;
        }
        {
            var saved=raw[65];raw[65]=Resident();NtfsRecordFixtures.U16(raw[65],22,5);calls=0;
            var supplemented=NtfsSnapshotSizeIndex.BuildAsync(scope,Map(128),Read,_=>true,Lookup).GetAwaiter().GetResult();
            Check(supplemented.Report.NativeResolvedObjects==1&&calls==1&&supplemented.Objects[Id(65)].LogicalBytes==32,
                "unsupported selected format can use same-source native metadata without retaining its names");raw[65]=saved;
        }
        foreach(string mismatch in new[]{"Identity","Logical","Allocation","Directory","Deleted","Writer","Links","Private"})
        {
            NtfsSnapshotSizeIndexResult? partial=null;
            var error=Catch(()=>partial=NtfsSnapshotSizeIndex.BuildAsync(scope,Map(128),Read,_=>true,(request,token)=>
            {
                if(mismatch=="Private")throw new NtfsSnapshotSizeException("SyntheticPrivateMessage");
                var observation=Native(request);var storage=observation.Storage;
                storage=mismatch switch{"Identity"=>storage with{Identity=Id(65,4)},"Logical"=>storage with{LogicalBytes=33},
                    "Allocation"=>storage with{AllocatedBytes=-1},"Writer"=>storage with{WritableHandleOpen=true},"Links"=>storage with{LinkCount=0},_=>storage};
                return observation with{Storage=storage,Directory=mismatch=="Directory"||observation.Directory,DeletePending=mismatch=="Deleted"};
            }).GetAwaiter().GetResult());
            Check(error is NtfsSnapshotSizeException&&partial==null&&!error.Message.Contains("SyntheticPrivate",StringComparison.Ordinal),
                "native binding size deletion writer and injected errors never publish unknown as a successful zero");
        }
        reads=0;calls=0;
        Check(Catch(()=>NtfsSnapshotSizeIndex.BuildAsync(scope,Map(64),Read,_=>true,Lookup).GetAwaiter().GetResult()) is
            NtfsSnapshotSizeException {Code:"SnapshotSizeSlotOutsideMap"}&&reads==0&&calls==0,"namespace slot beyond the validated source map is never guessed or truncated");
        Check(Catch(()=>NtfsSnapshotSizeIndex.BuildAsync(scope,Map(128),(_,destination,_)=>destination.Length-1,_=>true,Lookup).GetAwaiter().GetResult()) is
            NtfsSnapshotSizeException {Code:"SnapshotSizeShortRead"},"short MFT page discards the entire selected attempt");
        Check(Catch(()=>NtfsSnapshotSizeIndex.BuildAsync(scope,Map(128),Read,_=>throw new InvalidOperationException("private"),Lookup).GetAwaiter().GetResult()) is
            NtfsSnapshotSizeException {Code:"SnapshotSizeAllocationRead"},"unavailable bitmap coverage fails rather than turning unknown evidence into false");
        var incomplete=new NtfsSnapshotNamespaceResult(scope.Report with{CompleteWithinBoundary=false},scope.Entries,new[]{"","r.bin","h.bin","s.bin"},scope.RootIdentity);
        Check(Catch(()=>NtfsSnapshotSizeIndex.BuildAsync(incomplete,Map(128),Read,_=>true,Lookup).GetAwaiter().GetResult()) is
            NtfsSnapshotSizeException {Code:"SnapshotSizeNamespaceIncomplete"},"incomplete namespace never becomes the authority for ignoring outside records");
        var foreignRevision=new NtfsSnapshotNamespaceResult(scope.Report with{Revision="foreign"},scope.Entries,new[]{"","r.bin","h.bin","s.bin"},scope.RootIdentity);
        Check(Catch(()=>NtfsSnapshotSizeIndex.BuildAsync(foreignRevision,Map(128),Read,_=>true,Lookup).GetAwaiter().GetResult()) is
            NtfsSnapshotSizeException {Code:"SnapshotSizeNamespaceIncomplete"},"a foreign namespace revision is not silently treated as the bounded inventory contract");
        var boundary=Scope(File(1,"r.bin",2) with{Reparse=true,Attributes=FileAttributes.ReparsePoint});
        Check(Catch(()=>NtfsSnapshotSizeIndex.BuildAsync(boundary,Map(128),Read,_=>true,Lookup).GetAwaiter().GetResult()) is
            NtfsSnapshotSizeException {Code:"SnapshotSizeBoundary"},"unhandled reparse boundary triggers whole-attempt fallback rather than a zero-sized placeholder");
        using(var cancellation=new CancellationTokenSource())
        {
            NtfsSnapshotSizeIndexResult? partial=null;var error=Catch(()=>partial=NtfsSnapshotSizeIndex.BuildAsync(scope,Map(128),Read,_=>true,
                (request,token)=>{cancellation.Cancel();return Native(request);},cancellation.Token).GetAwaiter().GetResult());
            Check(error is OperationCanceledException&&partial==null,"cancellation after native observation publishes no partial size table");
        }
        {
            const int count=32769;int nativeCalls=0;
            var excessive=Scope(Enumerable.Range(0,count).Select(index=>File(index+1,"f"+index.ToString("D5"),checked((ulong)(index+2)))).ToArray());
            byte[] resident=Resident(),root=Root();
            int BudgetRead(long offset,Memory<byte> destination,CancellationToken token)
            {
                token.ThrowIfCancellationRequested();destination.Span.Clear();
                for(int local=0;local<destination.Length;local+=1024)
                {
                    ulong slot=(ulong)((offset+local)/1024);if(slot==1)root.CopyTo(destination.Span[local..]);
                    else if(slot>=2&&slot<count+2)resident.CopyTo(destination.Span[local..]);
                }
                return destination.Length;
            }
            Check(Catch(()=>NtfsSnapshotSizeIndex.BuildAsync(excessive,Map(65536),BudgetRead,_=>true,(request,token)=>
                {Interlocked.Increment(ref nativeCalls);return Native(request);}).GetAwaiter().GetResult()) is
                NtfsSnapshotSizeException {Code:"SnapshotSizeNativeBudget"}&&nativeCalls==0,
                "native object ceiling is checked before issuing any lookup rather than returning a successfully resolved prefix");
        }
        {
            const int count=260;var many=Scope(Enumerable.Range(0,count).Select(index=>File(index+1,"f"+index.ToString("D4"),checked((ulong)(index+2)))).ToArray());
            int manyRead(long offset,Memory<byte> destination,CancellationToken token)
            {
                token.ThrowIfCancellationRequested();destination.Span.Clear();
                for(int local=0;local<destination.Length;local+=1024)
                {
                    ulong slot=(ulong)((offset+local)/1024);if(slot==1)Root().CopyTo(destination.Span[local..]);
                    else if(slot>=2&&slot<count+2)Resident().CopyTo(destination.Span[local..]);
                }
                return destination.Length;
            }
            using var entered=new CountdownEvent(8);using var release=new ManualResetEventSlim();int active=0,peak=0,total=0;
            var task=NtfsSnapshotSizeIndex.BuildAsync(many,Map(512),manyRead,_=>true,(request,token)=>
            {
                int current=Interlocked.Increment(ref active);int seen=Volatile.Read(ref peak);while(current>seen){int old=Interlocked.CompareExchange(ref peak,current,seen);if(old==seen)break;seen=old;}
                if(Interlocked.Increment(ref total)<=8)entered.Signal();
                try{if(!release.Wait(10000))throw new IOException("SyntheticDeadline");return Native(request);}finally{Interlocked.Decrement(ref active);}
            });
            try
            {
                Check(entered.Wait(10000)&&peak==8&&!task.IsCompleted,"bounded native pipeline reaches eight workers without publishing an in-flight table");
                release.Set();var completed=task.GetAwaiter().GetResult();
                Check(total==count&&completed.Report.NativeCalls==count&&completed.Report.PeakNativeWorkers==8&&completed.Report.PeakQueuedNative is >0 and <=128,
                    "every unknown object is queried once with eight workers and a bounded 128-object queue");
            }
            finally{release.Set();_ = task.GetAwaiter().GetResult();}
        }
        {
            const int count=16;var many=Scope(Enumerable.Range(0,count).Select(index=>File(index+1,"e"+index.ToString("D2"),checked((ulong)(index+2)))).ToArray());
            int read(long offset,Memory<byte> destination,CancellationToken token)
            {
                destination.Span.Clear();
                for(int local=0;local<destination.Length;local+=1024)
                {
                    ulong slot=(ulong)((offset+local)/1024);if(slot==1)Root().CopyTo(destination.Span[local..]);
                    else if(slot>=2&&slot<count+2)Resident().CopyTo(destination.Span[local..]);
                }
                return destination.Length;
            }
            using var entered=new CountdownEvent(8);using var beginFailure=new ManualResetEventSlim();using var cancelled=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
            int active=0,ordinal=0;NtfsSnapshotSizeIndexResult? partial=null;
            var task=NtfsSnapshotSizeIndex.BuildAsync(many,Map(64),read,_=>true,(request,token)=>
            {
                int call=Interlocked.Increment(ref ordinal);Interlocked.Increment(ref active);entered.Signal();
                try
                {
                    if(call==1)
                    {
                        _=token.Register(()=>cancelled.Set());
                        _=token.Register(()=>throw new NtfsSnapshotSizeException("SyntheticPrivateCancellation"));
                        if(!beginFailure.Wait(10000))throw new IOException("SyntheticDeadline");throw new IOException("SyntheticPrivateFailure");
                    }
                    if(!release.Wait(10000))throw new IOException("SyntheticDeadline");return Native(request);
                }
                finally{Interlocked.Decrement(ref active);}
            });
            try
            {
                Check(entered.Wait(10000),"all bounded native workers can be held under a controlled failure barrier");beginFailure.Set();
                Check(cancelled.Wait(10000)&&Volatile.Read(ref active)>0&&!task.IsCompleted,
                    "peer failure and throwing cancellation handlers still wait for every in-flight native lookup before returning");
                release.Set();var error=Catch(()=>partial=task.GetAwaiter().GetResult());
                Check(error is NtfsSnapshotSizeException {Code:"SnapshotSizeNativeRead"}&&partial==null&&active==0&&
                    !error.Message.Contains("SyntheticPrivate",StringComparison.Ordinal)&&error.InnerException==null,
                    "settled failure keeps only the fixed primary code and never publishes a partially resolved object table");
            }
            finally{beginFailure.Set();release.Set();_ = Catch(()=>task.GetAwaiter().GetResult());}
        }
        return passed;
    }
}
