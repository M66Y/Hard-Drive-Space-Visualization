using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsVssScalePhases(double OwnerMilliseconds,double NamespaceMilliseconds,double RawReadMilliseconds,
    double ParseMilliseconds,double IndexMilliseconds,double NativeMilliseconds,double ProjectionMilliseconds,double CleanupMilliseconds,
    double BackendMilliseconds,double DirectoryMilliseconds);
internal sealed record NtfsVssScaleSample(int Round,string Algorithm,double Milliseconds,double CpuMilliseconds,long ManagedAllocatedBytes,
    int RawReadCalls,long RawRequestedBytes,int NativeFallbackObjects,int ScopeDecodedRecords,int NamespaceEntries,bool NamespaceComplete,
    bool Matched,bool Closed,long SampledPeakWorkingSetBytes,int WorkingSetSamples,NtfsVssScalePhases Phases,int NativeSupplementCalls);
internal sealed record NtfsVssScaleReport(string Revision,NtfsScaleWorkload Workload,double PreparationMilliseconds,
    IReadOnlyList<NtfsVssScaleSample> Samples,bool Matched,int WorkingSetSampleIntervalMilliseconds,NtfsScaleResultCounts ResultCounts,
    int SnapshotSessions,int RawSessions,int TotalRawReadCalls,long TotalRawRequestedBytes);
internal sealed class NtfsVssScaleAttemptException(string code,string stage,int nativeError,bool cleanupConfirmed):IOException(code)
{
    internal string Code{get;}=code;
    internal string Stage{get;}=stage;
    internal int NativeError{get;}=nativeError;
    internal bool CleanupConfirmed{get;}=cleanupConfirmed;
}

// Only Run may create real sources, using the fixed owned-fixture factory.
// The in-memory test seam below cannot mint a snapshot or product source proof.
internal static class NativeVssScaleExperiment
{
    internal interface IBackend:IDisposable
    {
        string RootPath{get;}
        FileStorageInfo RootStorage{get;}
        NtfsSnapshotVolumeFacts Facts(CancellationToken token);
        int ReadPhysical(long offset,Memory<byte> destination,CancellationToken token);
        NtfsSnapshotNamespace.IBackend Namespace(CancellationToken token);
        NtfsSnapshotNativeSize Lookup(NtfsSnapshotNamespaceResult inventory,NtfsSnapshotSizeRequest request,CancellationToken token);
        ScanResult Directory(CancellationToken token);
        int ReadCalls{get;}
        long RequestedBytes{get;}
        bool Cleanup();
    }
    internal sealed record Attempt(ScanResult Scan,double Milliseconds,NtfsVssScalePhases Phases,int RawReadCalls,long RawRequestedBytes,
        int NativeFallbackObjects,int NativeSupplementCalls,int ScopeDecodedRecords,int NamespaceEntries,bool NamespaceComplete,bool Closed);
    private sealed record Value(ScanResult Scan,double Namespace,double Read,double Parse,double Index,double Native,double Projection,double Directory,
        int NativeObjects,int NativeCalls,int Decoded,int Entries,bool NamespaceComplete);
    private sealed class Backend(NtfsVerifiedFixtureContext context,NtfsVssFixtureOwner owner,FileStorageInfo originRoot):IBackend
    {
        private NtfsSnapshotVolumeReader? reader;
        private bool disposed;
        public string RootPath=>owner.SnapshotDataRoot;
        public FileStorageInfo RootStorage=>owner.RootStorage;
        public NtfsSnapshotVolumeFacts Facts(CancellationToken token){reader??=owner.OpenReader(token);return reader.Facts(token);}
        public int ReadPhysical(long offset,Memory<byte> destination,CancellationToken token)
        {Require(reader!=null,"VssScaleSourceFailed");return reader.ReadInto(offset,destination,token);}
        public NtfsSnapshotNamespace.IBackend Namespace(CancellationToken token){token.ThrowIfCancellationRequested();return owner.CreateNamespaceBackend();}
        public NtfsSnapshotNativeSize Lookup(NtfsSnapshotNamespaceResult inventory,NtfsSnapshotSizeRequest request,CancellationToken token)=>owner.LookupSize(inventory,request,token);
        public ScanResult Directory(CancellationToken token)=>new DiskScanner(8).Scan(RootPath,null,token);
        public int ReadCalls=>reader?.ReadCalls??0;
        public long RequestedBytes=>reader?.RequestedBytes??0;
        public bool Cleanup()
        {
            bool unchanged=false;try{unchanged=context.CaptureVssScaleRoot()==originRoot;}catch{}
            return owner.Cleanup()&&unchanged;
        }
        public void Dispose(){if(disposed)return;disposed=true;owner.Dispose();context.Dispose();}
    }
    private static IBackend Open(string manifest,CancellationToken token)
    {
        NtfsVerifiedFixtureContext? context=null;NtfsVssFixtureOwner? owner=null;
        try
        {
            context=NtfsVerifiedFixtureContext.CreateForVssScale(manifest,token:token);
            var origin=context.CaptureVssScaleRoot(token);owner=NtfsVssFixtureOwner.CreateScale(context,token);
            return new Backend(context,owner,origin);
        }
        catch(Exception error)
        {
            bool closed=error is NtfsVssFixtureOpenException known?known.CleanupConfirmed:owner==null;
            if(owner!=null){try{closed=owner.Cleanup();}catch{closed=false;}}
            try{context?.Dispose();}catch{closed=false;}
            int number=error is NtfsVssFixtureOpenException opened?opened.NativeError:error is NtfsVssProbeException native?native.Error:
                error is NtfsFixtureGuardException guard?guard.NativeErrorCode:0;
            string code=error is OperationCanceledException?"VssCancelled":OpenDiagnostic(error);
            Console.Error.WriteLine("NTFS_VSS_OPEN_CODE:"+code);
            throw new NtfsVssFixtureOpenException(code,number,closed);
        }
    }
    // Fixed literal categories only. Exception types and an ASCII-looking
    // string are not themselves authority to put arbitrary text in stderr.
    internal static string OpenDiagnostic(Exception error)
    {
        string? code=error is NtfsVssFixtureOpenException opened?opened.Code:error is NtfsFixtureGuardException guard?guard.Code:
            error is NtfsVssProbeException native?native.Code:null;
        return code switch
        {
            "VssAddVolume" or "VssAsyncStatus" or "VssAsyncTimeout" or "VssAsyncWait" or "VssCancelled" or "VssCleanup" or
            "VssComInitialize" or "VssComSecurity" or "VssContext" or "VssCreateBackup" or "VssCreateSnapshot" or "VssFixtureCreate" or
            "VssFixtureGuard" or "VssInitializeBackup" or "VssProperties" or "VssRawOpen" or "VssSnapshotBinding" or "VssSnapshotNative" or
            "VssStartSet" or "VssUnexpected" or "VssVolumeUnsupported" or
            "FixtureAbsolutePath" or "FixtureAdapter" or "FixtureAliasBinding" or "FixtureAlignmentBounds" or "FixtureBaselineBudget" or
            "FixtureBaselineCase" or "FixtureBaselinePath" or "FixtureCaseBudget" or "FixtureContextClosed" or "FixtureControlBounds" or
            "FixtureControlFailed" or "FixtureDataRoot" or "FixtureGuardOperation" or "FixtureImageBounds" or "FixtureImageChanged" or
            "FixtureImageDevice" or "FixtureImageFixedType" or "FixtureImageInformation" or "FixtureImageOpen" or "FixtureImageSize" or
            "FixtureImageSubtype" or "FixtureJsonFields" or "FixtureJsonSchema" or "FixtureJsonShape" or "FixtureJsonValue" or
            "FixtureManifestBounds" or "FixtureManifestChanged" or "FixtureManifestIdentity" or "FixtureMarkerBinding" or "FixtureMarkerBounds" or
            "FixtureMftGeometry" or "FixtureMountedVolume" or "FixtureNamedStream" or "FixtureNonce" or "FixtureNtfsGeometry" or
            "FixturePartitionBounds" or "FixturePartitionChanged" or "FixturePartitionIdentity" or "FixturePhysicalDevice" or "FixturePhysicalSector" or
            "FixturePostTargetChanged" or "FixtureReadBounds" or "FixtureReparse" or "FixtureRepositoryRoot" or "FixtureResolveVolume" or
            "FixtureScalePath" or "FixtureScaleRoot" or "FixtureScaleScope" or "FixtureSnapshotRoot" or "FixtureSnapshotScope" or
            "FixtureStorageFailed" or "FixtureTargetAttributes" or "FixtureTargetIdentity" or "FixtureTargetType" or "FixtureVolumeDataBounds" or
            "FixtureVolumeExtents" or "FixtureVolumeOpen" or "FixtureVolumeSyntax" or "FixtureWorkspace"=>code,
            _=>"VssUnexpected"
        };
    }
    private static void Require([DoesNotReturnIf(false)]bool value,string code){if(!value)throw new IOException(code);}
    private static double Milliseconds(long ticks)=>ticks*1000.0/Stopwatch.Frequency;
    internal static string[] Order(int round)=>round switch{1 or 3=>["RawMft","Directory"],2=>["Directory","RawMft"],_=>throw new ArgumentOutOfRangeException(nameof(round))};

    // Called synchronously on the COM owner thread. BuildAsync performs raw I/O
    // before its first await; only its bounded file-metadata callbacks use tasks.
    private static Value ReadFast(IBackend backend,CancellationToken token,Action<string> stage)
    {
        stage("RawRead");var facts=backend.Facts(token);var geometry=facts.Geometry;
        Require(geometry is{SectorBytes:512,ClusterBytes:4096,RecordBytes:1024}&&geometry.MftValidDataLength>=1024&&
            geometry.MftValidDataLength%1024==0&&geometry.MftValidDataLength/1024<=262144,"VssScaleSourceFailed");
        long initRead=0,initParse=0;
        int Physical(long offset,Memory<byte> destination)
        {long tick=Stopwatch.GetTimestamp();int received=backend.ReadPhysical(offset,destination,token);initRead+=Stopwatch.GetTimestamp()-tick;return received;}
        long bootstrap=checked(geometry.MftStartLcn*4096);byte[] raw0=new byte[1024];
        Require(Physical(bootstrap,raw0)==raw0.Length,"VssScaleSourceFailed");stage("Parse");long parseStart=Stopwatch.GetTimestamp();
        var mapped=NtfsMftMap.Parse(raw0,geometry,token);Require(mapped.Status=="Mapped"&&mapped.Map!=null,"VssScaleSourceFailed");
        var map=mapped.Map!;var reverse=map.PlanRecord(0,token);
        Require(reverse.Count==1&&reverse[0].VolumeOffset==bootstrap&&reverse[0].Length==1024&&reverse[0].RecordOffset==0,"VssScaleSourceFailed");
        int slots=checked((int)(geometry.MftValidDataLength/1024));var planned=NtfsMftBitmapPrefix.ParseScale(raw0,geometry,slots,token);
        initParse+=Stopwatch.GetTimestamp()-parseStart;byte[] prefix;
        if(planned.Status=="Resident")prefix=planned.CopyPrefix();
        else
        {
            Require(planned.Status=="Planned"&&planned.ReadBytes is >0 and <=32768,"VssScaleSourceFailed");
            stage("RawRead");var bytes=new byte[planned.ReadBytes];int copied=0;
            foreach(var slice in planned.ReadPlan)
            {
                token.ThrowIfCancellationRequested();Require(slice.RecordOffset==copied&&slice.Length>0&&slice.Length<=bytes.Length-copied,"VssScaleSourceFailed");
                int received=Physical(slice.VolumeOffset,bytes.AsMemory(copied,slice.Length));Require(received==slice.Length,"VssScaleSourceFailed");copied+=received;
            }
            Require(copied==bytes.Length&&planned.LogicalPrefixBytes<=bytes.Length,"VssScaleSourceFailed");prefix=bytes.AsSpan(0,planned.LogicalPrefixBytes).ToArray();Array.Clear(bytes);
        }
        Require(prefix.Length==(slots+7)/8,"VssScaleSourceFailed");
        try
        {
            stage("Namespace");long tick=Stopwatch.GetTimestamp();var inventory=NtfsSnapshotNamespace.Read(backend.Namespace(token),token);
            double namespaceMs=Milliseconds(Stopwatch.GetTimestamp()-tick);
            Require(inventory.RootIdentity==backend.RootStorage.Identity&&inventory.Report.CompleteWithinBoundary,"VssScaleSourceFailed");
            int Read(long offset,Memory<byte> destination,CancellationToken cancellation)
            {
                stage("RawRead");int copied=0;
                foreach(var slice in map.PlanRange(offset,destination.Length,cancellation))
                {
                    cancellation.ThrowIfCancellationRequested();Require(slice.RecordOffset==copied&&slice.Length>0&&slice.Length<=destination.Length-copied,"VssScaleSourceFailed");
                    int received=backend.ReadPhysical(slice.VolumeOffset,destination.Slice(copied,slice.Length),cancellation);
                    Require(received==slice.Length,"VssScaleSourceFailed");copied+=received;
                }
                Require(copied==destination.Length,"VssScaleSourceFailed");return copied;
            }
            bool Allocated(ulong slot){Require(slot<(ulong)slots,"VssScaleSourceFailed");return(prefix[(int)(slot/8)]&(1<<(int)(slot%8)))!=0;}
            NtfsSnapshotNativeSize Lookup(NtfsSnapshotSizeRequest request,CancellationToken cancellation)
            {stage("Native");return backend.Lookup(inventory,request,cancellation);}
            stage("Index");var sized=NtfsSnapshotSizeIndex.BuildAsync(inventory,map,Read,Allocated,Lookup,token).GetAwaiter().GetResult();
            stage("Projection");tick=Stopwatch.GetTimestamp();var tree=NtfsSnapshotSizeIndex.Materialize(sized,backend.RootPath,token);
            tree.Storage=backend.RootStorage;double projectionMs=Milliseconds(Stopwatch.GetTimestamp()-tick);
            Require(tree.Storage.Identity==inventory.RootIdentity&&sized.Report.Complete&&sized.Report.UnknownObjects==0,"VssScaleSourceFailed");
            return new(new(tree,TimeSpan.Zero,0,[]),namespaceMs,Milliseconds(initRead)+sized.Report.RawReadMilliseconds,
                Milliseconds(initParse)+sized.Report.ParseMilliseconds,sized.Report.IndexMilliseconds,sized.Report.NativeMilliseconds,projectionMs,0,
                sized.Report.NativeResolvedObjects,sized.Report.NativeCalls,sized.Report.DecodedScopeRecords,inventory.Report.Entries,true);
        }
        finally{Array.Clear(prefix);Array.Clear(raw0);}
    }

    internal static Attempt MeasureForTests(Func<CancellationToken,IBackend> factory,string algorithm,CancellationToken token=default)
        =>Measure(factory,algorithm,token,_=>{});
    private static Attempt Measure(Func<CancellationToken,IBackend> factory,string algorithm,CancellationToken token,Action<string> progress)
    {
        token.ThrowIfCancellationRequested();Require(algorithm is "RawMft" or "Directory","VssScaleSourceFailed");
        long started=Stopwatch.GetTimestamp(),created=started,backendEnd=started,cleanupStart=started,finished=started;
        IBackend? backend=null;Value? value=null;Exception? primary=null;bool cleanup=false;string current="SnapshotCreate",failedStage=current;
        int calls=0;long bytes=0;
        void Stage(string next){current=next;progress(next);}
        try
        {
            Stage("SnapshotCreate");backend=factory(token);Require(backend!=null,"VssScaleSourceFailed");token.ThrowIfCancellationRequested();created=Stopwatch.GetTimestamp();
            if(algorithm=="RawMft")value=ReadFast(backend,token,Stage);
            else
            {
                Stage("Directory");long tick=Stopwatch.GetTimestamp();var scan=backend.Directory(token);
                Require(scan.Root.Storage?.Identity==backend.RootStorage.Identity,"VssScaleSourceFailed");
                value=new(scan,0,0,0,0,0,0,Milliseconds(Stopwatch.GetTimestamp()-tick),0,0,0,0,false);
            }
            token.ThrowIfCancellationRequested();backendEnd=Stopwatch.GetTimestamp();calls=backend.ReadCalls;bytes=backend.RequestedBytes;
            Require(calls>=0&&calls<=8192&&bytes>=0&&bytes<=268500992&&(algorithm!="Directory"||calls==0&&bytes==0),"VssScaleSourceFailed");
        }
        catch(Exception error){primary=error;failedStage=current;}
        finally
        {
            cleanupStart=Stopwatch.GetTimestamp();Stage("Cleanup");
            if(backend!=null)
            {
                try{cleanup=backend.Cleanup();}catch{cleanup=false;}
                try{backend.Dispose();}catch{cleanup=false;}
            }
            else cleanup=primary is NtfsVssFixtureOpenException opened&&opened.CleanupConfirmed;
            finished=Stopwatch.GetTimestamp();
        }
        if(primary!=null)
        {
            int error=primary is NtfsVssFixtureOpenException open?open.NativeError:primary is NtfsVssProbeException native?native.Error:
                primary is NtfsFixtureGuardException guard?guard.NativeErrorCode:0;
            bool cancelled=primary is OperationCanceledException||primary is NtfsVssFixtureOpenException{Code:"VssCancelled"};
            throw new NtfsVssScaleAttemptException(cancelled?"VssScaleCancelled":"VssScaleSourceFailed",failedStage,error,cleanup);
        }
        if(token.IsCancellationRequested)throw new NtfsVssScaleAttemptException("VssScaleCancelled","Cleanup",0,cleanup);
        if(!cleanup)throw new NtfsVssScaleAttemptException("VssScaleCleanupFailed","Cleanup",0,false);
        Require(value!=null,"VssScaleSourceFailed");
        var phases=new NtfsVssScalePhases(Milliseconds(created-started),value.Namespace,value.Read,value.Parse,value.Index,value.Native,value.Projection,
            Milliseconds(finished-cleanupStart),Milliseconds(backendEnd-created),value.Directory);
        return new(value.Scan,Milliseconds(finished-started),phases,calls,bytes,value.NativeObjects,value.NativeCalls,value.Decoded,value.Entries,value.NamespaceComplete,true);
    }

    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateDirectoryW(string path,IntPtr security);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateHardLinkW(string link,string existing,IntPtr security);
    [StructLayout(LayoutKind.Sequential)]private struct PinId{internal ulong Volume,Low,High;}
    [StructLayout(LayoutKind.Sequential)]private struct PinAttributes{internal uint Attributes,ReparseTag;}
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)]
    private static extern bool PinIdentity(SafeFileHandle handle,int info,out PinId value,uint length);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)]
    private static extern bool PinAttribute(SafeFileHandle handle,int info,out PinAttributes value,uint length);
    internal static bool PinBindingValid(FileIdentity expected,FileIdentity observed,FileAttributes attributes)=>expected==observed&&observed.High==0&&
        observed.Low>>48!=0&&(attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))==FileAttributes.Directory;
    private static SafeFileHandle PinDirectory(string path,FileIdentity expected,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var handle=NativeStorage.CreateFileW(path,0x80,1,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
        try
        {
            PinId identity=default;PinAttributes attributes=default;
            Require(!handle.IsInvalid&&PinIdentity(handle,18,out identity,24)&&PinAttribute(handle,9,out attributes,8),"VssScalePreparationFailed");
            Require(PinBindingValid(expected,new(identity.Volume,identity.Low,identity.High),(FileAttributes)attributes.Attributes),"VssScalePreparationFailed");
            token.ThrowIfCancellationRequested();return handle;
        }
        catch{handle.Dispose();throw;}
    }
    private static void Prepare(string manifest,int files,CancellationToken token)
    {
        using var context=NtfsVerifiedFixtureContext.CreateForVssScale(manifest,preparation:true,token:token);
        Require(!Directory.Exists(context.DataRoot)&&!File.Exists(context.DataRoot),"VssScalePreparationFailed");
        Require(CreateDirectoryW(context.DataRoot,IntPtr.Zero),"VssScalePreparationFailed");context.ValidateVssScalePath(context.DataRoot,token);
        var root=context.CaptureVssScaleRoot(token);using var rootPin=PinDirectory(context.DataRoot,root.Identity,token);
        byte[] body=Enumerable.Repeat((byte)0x61,1024).ToArray();int perDirectory=files/100;
        for(int directory=0;directory<100;directory++)
        {
            token.ThrowIfCancellationRequested();string parent=Path.GetDirectoryName(NtfsScaleExperiment.FilePath(context.DataRoot,files,directory*perDirectory))!;
            Require(CreateDirectoryW(parent,IntPtr.Zero),"VssScalePreparationFailed");context.ValidateVssScalePath(parent,token);
            Require(NativeStorage.TryRead(parent,true,FileAttributes.Directory,out var directoryInfo,out _)&&directoryInfo!=null&&directoryInfo.Identity.Volume==root.Identity.Volume,"VssScalePreparationFailed");
            using var pin=PinDirectory(parent,directoryInfo.Identity,token);
            for(int local=0;local<perDirectory;local++)
            {
                token.ThrowIfCancellationRequested();int index=directory*perDirectory+local;
                using var stream=new FileStream(NtfsScaleExperiment.FilePath(context.DataRoot,files,index),FileMode.CreateNew,FileAccess.Write,FileShare.None);
                int length=NtfsScaleExperiment.ExpectedLength(index);if(length!=0)stream.Write(body,0,length);
            }
            if(directory<10)
            {
                string alias=Path.Combine(parent,"h"+directory.ToString("D7",System.Globalization.CultureInfo.InvariantCulture)+".dat");
                Require(CreateHardLinkW(alias,NtfsScaleExperiment.FilePath(context.DataRoot,files,directory*perDirectory+1),IntPtr.Zero),"VssScalePreparationFailed");
            }
        }
        token.ThrowIfCancellationRequested();
    }
    private sealed class Sampler:IDisposable
    {
        private readonly Process process=Process.GetCurrentProcess();private readonly CancellationTokenSource stop=new();private readonly Task worker;
        private int disposed;internal long Peak;internal int Samples;internal bool Failed;
        internal Sampler(){Sample();worker=Task.Factory.StartNew(()=>{while(!stop.Token.WaitHandle.WaitOne(20))Sample();},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);}
        private void Sample(){try{process.Refresh();long value=process.WorkingSet64;Interlocked.Increment(ref Samples);long previous=Interlocked.Read(ref Peak);
            while(value>previous){long actual=Interlocked.CompareExchange(ref Peak,value,previous);if(actual==previous)break;previous=actual;}}catch{Failed=true;}}
        public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;stop.Cancel();try{worker.GetAwaiter().GetResult();Sample();}catch{Failed=true;}finally{process.Dispose();stop.Dispose();}}
    }
    internal static int Run(string manifestPath,int files)
    {
        string? summary=null;string stage="Preparation";int checks=0;bool clean=true;
        try
        {
            var workload=NtfsScaleExperiment.Workload(files);manifestPath=Path.GetFullPath(manifestPath);
            using(var preparation=NtfsVerifiedFixtureContext.CreateForVssScale(manifestPath,preparation:true))summary=Path.Combine(Path.GetDirectoryName(manifestPath)!,"live-summary.json");
            Require(!File.Exists(summary),"VssScalePreparationFailed");var preparationWatch=Stopwatch.StartNew();Prepare(manifestPath,files,default);preparationWatch.Stop();
            // No raw or scanner warm-up. Preserve first VSS service/provider startup.
            var samples=new List<NtfsVssScaleSample>(6);byte[]? expected=null;NtfsScaleResultCounts? counts=null;int totalCalls=0;long totalBytes=0;
            for(int round=1;round<=3;round++)foreach(string algorithm in Order(round))
            {
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
                using var process=Process.GetCurrentProcess();process.Refresh();TimeSpan cpu=process.TotalProcessorTime;long allocated=GC.GetTotalAllocatedBytes(false);
                var sampler=new Sampler();Attempt attempt;
                try{attempt=Measure(token=>Open(manifestPath,token),algorithm,default,current=>stage=current);}
                finally{sampler.Dispose();}
                clean=attempt.Closed;Require(!sampler.Failed,"VssScaleSourceFailed");process.Refresh();
                double cpuMs=Math.Max(0,(process.TotalProcessorTime-cpu).TotalMilliseconds);long managed=Math.Max(0,GC.GetTotalAllocatedBytes(false)-allocated);
                stage="Comparison";byte[] fingerprint=NtfsScaleExperiment.Fingerprint(attempt.Scan,files);
                if(expected==null)
                {
                    expected=fingerprint;counts=new(files,attempt.Scan.Root.FileCount,attempt.Scan.Root.FolderCount,attempt.Scan.Root.Size,attempt.Scan.Root.AllocatedSize,attempt.Scan.Root.UnknownAllocationFiles);
                }
                else Require(fingerprint.SequenceEqual(expected),"VssScaleComparisonFailed");
                Require(attempt.Closed&&attempt.Scan.Root.UnknownAllocationFiles==0,"VssScaleComparisonFailed");checks++;
                totalCalls=checked(totalCalls+attempt.RawReadCalls);totalBytes=checked(totalBytes+attempt.RawRequestedBytes);
                samples.Add(new(round,algorithm,attempt.Milliseconds,cpuMs,managed,attempt.RawReadCalls,attempt.RawRequestedBytes,attempt.NativeFallbackObjects,
                    attempt.ScopeDecodedRecords,attempt.NamespaceEntries,attempt.NamespaceComplete,true,true,sampler.Peak,sampler.Samples,attempt.Phases,attempt.NativeSupplementCalls));
                attempt=null!; // Do not deliberately retain the preceding tree during the next sampled session.
            }
            Require(checks==6&&counts!=null,"VssScaleComparisonFailed");
            var report=new NtfsVssScaleReport("vss-scale-benchmark-v1",workload,preparationWatch.Elapsed.TotalMilliseconds,samples.AsReadOnly(),true,20,counts,6,3,totalCalls,totalBytes);
            stage="Complete";WriteSummary(summary,new{schema=1,status="vss_scale_benchmarked",code="None",stage,checks,failures=0,native_error=0,cleanup_confirmed=true,vss_scale=report});
            Console.WriteLine("PASS NTFS owned snapshot scale benchmark");return 0;
        }
        catch(Exception error)
        {
            string code=error is NtfsVssScaleAttemptException attempt?attempt.Code:error is OperationCanceledException?"VssScaleCancelled":
                stage=="Preparation"?"VssScalePreparationFailed":stage=="Comparison"?"VssScaleComparisonFailed":"VssScaleFailed";
            int nativeError=error is NtfsVssScaleAttemptException typed?typed.NativeError:error is NtfsFixtureGuardException guard?guard.NativeErrorCode:0;
            if(error is NtfsVssScaleAttemptException failed){stage=failed.Stage;clean=failed.CleanupConfirmed;}
            if(summary!=null&&!File.Exists(summary))try{WriteSummary(summary,new{schema=1,status="failed",code,stage,checks,failures=1,native_error=nativeError,cleanup_confirmed=clean,vss_scale=(NtfsVssScaleReport?)null});}catch{}
            Console.Error.WriteLine("FAIL NTFS owned snapshot scale benchmark "+code);return 1;
        }
    }
    private static void WriteSummary(string path,object value)
    {
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(value);Require(bytes.Length<=65536,"VssScaleFailed");
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);stream.Write(bytes);stream.Flush(true);
    }
}
