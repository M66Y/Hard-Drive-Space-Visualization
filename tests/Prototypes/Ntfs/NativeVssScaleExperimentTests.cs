using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// Only private byte arrays, synthetic cursors and product trees. No snapshot,
// corpus creation, VHD, volume handle or filesystem access occurs here.
internal static class NativeVssScaleExperimentTests
{
    private static readonly FileIdentity RootId=new(41,NtfsRecordFixtures.Reference(1,3),0);
    private static readonly FileIdentity FileId=new(41,NtfsRecordFixtures.Reference(2,3),0);
    private sealed class Fake:NativeVssScaleExperiment.IBackend,NtfsSnapshotNamespace.IBackend
    {
        private readonly byte[] image=new byte[1024*1024];
        internal int RawCalls,DirectoryCalls,LookupCalls,CleanupCalls,DisposeCalls,ClosedCursors;
        internal bool CleanupResult=true,DisposeFails,CleanupThrows,Short,ReadFails,DirectoryFails,BadRoot;
        internal Action? OnRead,OnDispose;
        internal Fake(bool resident=false,bool allocated=true,bool unsupported=false)
        {
            NtfsMftMapTests.Record0(extra:NtfsRecordFixtures.Resident(0xb0,new byte[]{allocated?(byte)7:(byte)3})).CopyTo(image,40960);
            NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("scope"),NtfsRecordFixtures.Data(0)],directory:true).CopyTo(image,40960+1024);
            var record=NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("a.dat",parent:RootId.Low),NtfsRecordFixtures.Name("b.dat",parent:RootId.Low),
                resident?NtfsRecordFixtures.Data(32):NtfsRecordFixtures.Nonresident()],links:2);
            if(unsupported)NtfsRecordFixtures.U16(record,22,5);record.CopyTo(image,40960+2048);
        }
        public string RootPath=>@"S:\SyntheticSnapshot";
        public FileStorageInfo RootStorage=>new(RootId,0,1,false,-1,new DateTime(2020,1,1));
        public NtfsSnapshotVolumeFacts Facts(CancellationToken token){token.ThrowIfCancellationRequested();return new(new(512,4096,1024,image.Length,256,10,4096),41);}
        public int ReadPhysical(long offset,Memory<byte> destination,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();RawCalls++;RequestedBytes+=destination.Length;
            if(ReadFails)throw new IOException("SyntheticPrivateRead");image.AsMemory((int)offset,destination.Length).CopyTo(destination);OnRead?.Invoke();return Short?destination.Length-512:destination.Length;
        }
        public NtfsSnapshotNamespace.IBackend Namespace(CancellationToken token){token.ThrowIfCancellationRequested();return this;}
        public NtfsSnapshotNativeSize Lookup(NtfsSnapshotNamespaceResult inventory,NtfsSnapshotSizeRequest request,CancellationToken token)
        {token.ThrowIfCancellationRequested();Interlocked.Increment(ref LookupCalls);return new(new(request.Identity,32,2,false,32,new DateTime(2020,1,1)),false);}
        public ScanResult Directory(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();DirectoryCalls++;if(DirectoryFails)throw new IOException("SyntheticPrivateDirectory");
            var root=new DiskEntry(RootPath,true){Storage=BadRoot?RootStorage with{Identity=new(41,99,0)}:RootStorage};
            foreach(string name in new[]{"a.dat","b.dat"})
            {
                var file=DiskEntry.FromName(name,false,root,".dat");file.Size=6000;file.FileCount=1;file.Storage=new(FileId,8192,2,false,6000);root.MutableChildren.Add(file);
            }
            root.Size=12000;root.FileCount=2;StorageMetrics.Calculate(root,token);return new(root,TimeSpan.Zero,0,[]);
        }
        public int ReadCalls=>RawCalls;
        public long RequestedBytes{get;private set;}
        public bool Cleanup(){CleanupCalls++;if(CleanupThrows)throw new IOException("SyntheticPrivateCleanup");return CleanupResult;}
        public void Dispose(){DisposeCalls++;OnDispose?.Invoke();if(DisposeFails)throw new IOException("SyntheticPrivateDispose");}
        public FileIdentity RootIdentity=>RootId;
        public NtfsSnapshotNamespace.ICursor OpenDirectory(NtfsSnapshotNamespaceEntry entry,string path,CancellationToken token)=>new Cursor(this);
        private sealed class Cursor(Fake owner):NtfsSnapshotNamespace.ICursor
        {
            private bool emitted;
            public FileIdentity Identity=>RootId;
            public FileAttributes Attributes=>FileAttributes.Directory;
            public NtfsSnapshotNamespaceIo Read(bool restart,Memory<byte> destination,CancellationToken token)
            {
                token.ThrowIfCancellationRequested();if(emitted)return new(false,18);emitted=true;var bytes=destination.Span;bytes.Clear();int offset=0;
                foreach(string name in new[]{"a.dat","b.dat"})
                {
                    byte[] text=Encoding.Unicode.GetBytes(name);int length=(88+text.Length+7)&~7;
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..],name=="b.dat"?0u:(uint)length);
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes[(offset+56)..],(uint)FileAttributes.Archive);
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes[(offset+60)..],(uint)text.Length);
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes[(offset+72)..],FileId.Low);text.CopyTo(bytes[(offset+88)..]);offset+=length;
                }
                return new(true,0);
            }
            public void Dispose(){owner.ClosedCursors++;}
        }
    }
    internal static int Run()
    {
        int passed=0;
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("VSS scale check failed: "+name);passed++;Console.WriteLine("PASS VSS scale "+name);}
        Exception? Catch(Action action){try{action();return null;}catch(Exception error){return error;}}
        Check(Enumerable.Range(1,3).SelectMany(NativeVssScaleExperiment.Order).SequenceEqual(new[]{"RawMft","Directory","Directory","RawMft","RawMft","Directory"}),
            "three measured pairs use the declared alternating order without an implicit warm-up session");
        Check(Catch(()=>NativeVssScaleExperiment.Order(0)) is ArgumentOutOfRangeException,"an invalid round cannot become an unbounded source-session count");
        Check(NativeVssScaleExperiment.OpenDiagnostic(new NtfsVssFixtureOpenException("VssComSecurity",-1,false))=="VssComSecurity"&&
            NativeVssScaleExperiment.OpenDiagnostic(new NtfsFixtureGuardException("FixtureSnapshotScope"))=="FixtureSnapshotScope"&&
            NativeVssScaleExperiment.OpenDiagnostic(new NtfsVssFixtureOpenException("SyntheticPrivateCode",5,false))=="VssUnexpected"&&
            NativeVssScaleExperiment.OpenDiagnostic(new IOException("SyntheticPrivateMessage"))=="VssUnexpected",
            "snapshot-open diagnostics preserve only fixed literal operation categories without private messages or arbitrary typed codes");
        Check(NativeVssScaleExperiment.PinBindingValid(RootId,RootId,FileAttributes.Directory)&&
            !NativeVssScaleExperiment.PinBindingValid(RootId,FileId,FileAttributes.Directory)&&
            !NativeVssScaleExperiment.PinBindingValid(RootId,RootId,FileAttributes.Directory|FileAttributes.ReparsePoint)&&
            !NativeVssScaleExperiment.PinBindingValid(RootId,RootId,FileAttributes.Archive),
            "corpus directory pins require the same full identity an ordinary directory and no reparse traversal");
        {
            var fake=new Fake();var result=NativeVssScaleExperiment.MeasureForTests(_=>fake,"RawMft");
            Check(result.Scan.Root is{Size:12000,AllocatedSize:8192,FileCount:2,FolderCount:0,UnknownAllocationFiles:0}&&
                result is{RawReadCalls:2,RawRequestedBytes:5120,NativeFallbackObjects:0,NativeSupplementCalls:0,ScopeDecodedRecords:2,NamespaceEntries:3,NamespaceComplete:true,Closed:true},
                "bootstrap bitmap complete namespace selected sizes and product projection form one bounded source attempt");
            Check(fake.RawCalls==2&&fake.LookupCalls==0&&fake.DirectoryCalls==0&&fake.CleanupCalls==1&&fake.DisposeCalls==1&&fake.ClosedCursors==1,
                "one fast attempt closes its cursor source and owner without a hidden directory traversal or warm-up read");
            var phase=result.Phases;
            Check(phase.OwnerMilliseconds>=0&&phase.CleanupMilliseconds>=0&&phase.NamespaceMilliseconds>=0&&phase.RawReadMilliseconds>=0&&
                phase.ParseMilliseconds>=0&&phase.IndexMilliseconds>=0&&phase.NativeMilliseconds>=0&&phase.ProjectionMilliseconds>=0&&
                phase.DirectoryMilliseconds==0&&phase.OwnerMilliseconds+phase.BackendMilliseconds+phase.CleanupMilliseconds<=result.Milliseconds+1,
                "backend contains its measured subphases while creation and cleanup belong to the actual end-to-end wall");
            Check(result.Scan.Root.Storage?.Identity==RootId&&result.Scan.Root.Children.All(child=>child.Storage?.Identity==FileId)&&
                result.Scan.Root.Children.Count(child=>child.SharedAllocation)==1,"projection keeps source identities and deduplicates hard links across entries");
        }
        {
            var fake=new Fake(resident:true);var result=NativeVssScaleExperiment.MeasureForTests(_=>fake,"RawMft");
            Check(result is{NativeFallbackObjects:1,NativeSupplementCalls:1,Closed:true}&&result.Scan.Root is{Size:64,AllocatedSize:32}&&fake.LookupCalls==1,
                "two resident aliases trigger one same-source Native supplement with measured nonzero allocation");
        }
        {
            var fake=new Fake();var result=NativeVssScaleExperiment.MeasureForTests(_=>fake,"Directory");
            Check(fake.RawCalls==0&&fake.LookupCalls==0&&fake.DirectoryCalls==1&&result is{RawReadCalls:0,RawRequestedBytes:0,NamespaceEntries:0,NamespaceComplete:false,Closed:true},
                "directory baseline owns a separate source session and never consumes a raw or fast Native request");
            Check(result.Phases.DirectoryMilliseconds>0&&result.Phases.BackendMilliseconds>=result.Phases.DirectoryMilliseconds&&
                result.Phases.NamespaceMilliseconds==0&&result.Phases.RawReadMilliseconds==0&&result.Phases.ParseMilliseconds==0&&
                result.Phases.IndexMilliseconds==0&&result.Phases.NativeMilliseconds==0&&result.Phases.ProjectionMilliseconds==0,
                "directory scan time is explicit instead of being labelled as raw or Native supplement work");
        }
        foreach(string fault in new[]{"Short","Read","Directory","Root","Bitmap"})
        {
            var fake=new Fake(allocated:fault!="Bitmap"){Short=fault=="Short",ReadFails=fault=="Read",DirectoryFails=fault=="Directory",BadRoot=fault=="Root"};
            NativeVssScaleExperiment.Attempt? partial=null;string algorithm=fault is"Directory" or"Root"?"Directory":"RawMft";
            var error=Catch(()=>partial=NativeVssScaleExperiment.MeasureForTests(_=>fake,algorithm));
            Check(error is NtfsVssScaleAttemptException{Code:"VssScaleSourceFailed",CleanupConfirmed:true}&&partial==null&&fake.CleanupCalls==1&&fake.DisposeCalls==1&&
                error.InnerException==null&&!error.Message.Contains("SyntheticPrivate",StringComparison.Ordinal),
                "source contradictions short reads and private exceptions discard the attempt and preserve only fixed failure fields");
        }
        foreach(string fault in new[]{"False","Throw","Dispose"})
        {
            var fake=new Fake{CleanupResult=fault!="False",CleanupThrows=fault=="Throw",DisposeFails=fault=="Dispose"};
            Check(Catch(()=>NativeVssScaleExperiment.MeasureForTests(_=>fake,"Directory")) is
                NtfsVssScaleAttemptException{Code:"VssScaleCleanupFailed",CleanupConfirmed:false}&&fake.DisposeCalls==1,
                "successful algorithms cannot publish a success when source cleanup or final disposal is unconfirmed");
        }
        foreach(bool cleanup in new[]{false,true})
        {
            var error=Catch(()=>NativeVssScaleExperiment.MeasureForTests(_=>throw new NtfsVssFixtureOpenException("VssSnapshotBinding",5,cleanup),"RawMft"));
            Check(error is NtfsVssScaleAttemptException{Code:"VssScaleSourceFailed",Stage:"SnapshotCreate",NativeError:5} typed&&typed.CleanupConfirmed==cleanup,
                "factory failure carries its actual cleanup observation even though no backend object was returned");
        }
        Check(Catch(()=>NativeVssScaleExperiment.MeasureForTests(_=>throw new IOException("SyntheticPrivateUnknownFactory"),"RawMft")) is
            NtfsVssScaleAttemptException{CleanupConfirmed:false},"an unknown factory exception does not imply that no snapshot was created");
        foreach(bool cleanup in new[]{false,true})
        {
            using var cancelled=new CancellationTokenSource();var fake=new Fake{CleanupResult=cleanup,OnDispose=cancelled.Cancel};
            var error=Catch(()=>NativeVssScaleExperiment.MeasureForTests(_=>fake,"Directory",cancelled.Token));
            Check(error is NtfsVssScaleAttemptException{Code:"VssScaleCancelled",Stage:"Cleanup"} typed&&typed.CleanupConfirmed==cleanup&&fake.DisposeCalls==1,
                "final disposal cancellation prevents success and takes precedence over a simultaneous cleanup failure");
        }
        {
            using var cancelled=new CancellationTokenSource();var fake=new Fake{ReadFails=true,CleanupResult=false,OnDispose=cancelled.Cancel};
            Check(Catch(()=>NativeVssScaleExperiment.MeasureForTests(_=>fake,"RawMft",cancelled.Token)) is
                NtfsVssScaleAttemptException{Code:"VssScaleSourceFailed",CleanupConfirmed:false},
                "an established source failure remains primary if later cleanup both fails and triggers cancellation");
        }
        {
            using var cancelled=new CancellationTokenSource();var fake=new Fake{OnRead=cancelled.Cancel};
            Check(Catch(()=>NativeVssScaleExperiment.MeasureForTests(_=>fake,"RawMft",cancelled.Token)) is
                NtfsVssScaleAttemptException{Code:"VssScaleCancelled",CleanupConfirmed:true}&&fake.CleanupCalls==1&&fake.DisposeCalls==1,
                "raw read cancellation settles cleanup without returning a half-built namespace or tree");
        }
        {
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();int created=0;
            Check(Catch(()=>NativeVssScaleExperiment.MeasureForTests(_=>{created++;return new Fake();},"RawMft",cancelled.Token)) is OperationCanceledException&&created==0,
                "a pre-cancelled attempt creates no source");
        }
        var sample=new NtfsVssScaleSample(1,"RawMft",10,2,100,2,5120,0,2,3,true,true,true,1024,2,new(1,1,1,1,1,0,1,1,7,0),0);
        string json=JsonSerializer.Serialize(sample);
        Check(!json.Contains("Synthetic",StringComparison.Ordinal)&&!json.Contains("Identity",StringComparison.Ordinal)&&!json.Contains("Path",StringComparison.Ordinal)&&
            !json.Contains("Fingerprint",StringComparison.Ordinal),"safe benchmark samples contain costs and counts rather than source identifiers names or hashes");
        return passed;
    }
}
