using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsScaleWorkload(int Files,int Directories,int HardlinkAliases,int ResidentFiles,int NonresidentFiles,int EmptyFiles);
internal sealed record NtfsScaleSample(int Round,string Algorithm,double Milliseconds,double CpuMilliseconds,long ManagedAllocatedBytes,
    int RawReadCalls,long RawRequestedBytes,int NativeFallbackObjects,int NativeBeforeReads,int NativeAfterReads,bool Matched,
    long SampledPeakWorkingSetBytes,int WorkingSetSamples);
internal sealed record NtfsScaleResultCounts(int FileObjects,long FileEntries,long Directories,long LogicalBytes,long AllocatedBytes,long UnknownAllocationFiles);
internal sealed record NtfsScaleBenchmarkReport(string Revision,NtfsScaleWorkload Workload,double PreparationMilliseconds,
    IReadOnlyList<NtfsScaleSample> Samples,bool Matched,int WorkingSetSampleIntervalMilliseconds,NtfsScaleResultCounts ResultCounts);
internal sealed class NtfsScaleException(string code):IOException(code) { internal string Code{get;}=code; }

// Dedicated controlled-volume experiment. Its only real entry accepts a
// guarded manifest and one of two fixed corpus sizes, never a personal path.
internal static class NtfsScaleExperiment
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool CreateHardLinkW(string link,string existing,IntPtr security);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool CreateDirectoryW(string path,IntPtr security);
    internal sealed record ReadResult(ScanResult Scan,int ReadCalls,long RequestedBytes,int FallbackObjects,int BeforeReads,int AfterReads);
    internal interface IBackend:IDisposable
    {
        SafeFileHandle Volume{get;}
        string DataRoot{get;}
        FileStorageInfo Root(CancellationToken token);
        NtfsRawVolumeFacts Facts(bool reopen,CancellationToken token);
        IReadOnlyDictionary<ulong,FileStorageInfo> Before(CancellationToken token);
        void After(IReadOnlyDictionary<ulong,FileStorageInfo> before,CancellationToken token);
    }
    private sealed class Backend(NtfsVerifiedFixtureContext context,int files):IBackend
    {
        public SafeFileHandle Volume=>context.Volume;
        public string DataRoot=>context.DataRoot;
        public FileStorageInfo Root(CancellationToken token)=>context.CaptureScaleRoot(token);
        public NtfsRawVolumeFacts Facts(bool reopen,CancellationToken token)=>context.QueryFacts(reopen,token);
        private readonly Dictionary<ulong,string> paths=[];
        public IReadOnlyDictionary<ulong,FileStorageInfo> Before(CancellationToken token)
        {
            var values=new Dictionary<ulong,FileStorageInfo>(files/10);
            for(int i=0;i<files;i+=10)
            {
                token.ThrowIfCancellationRequested();string path=FilePath(DataRoot,files,i);context.ValidateScalePath(path,token);
                Require(NativeStorage.TryRead(path,false,FileAttributes.Archive,out var value,out _)&&value!=null&&
                    value.Identity.High==0&&value.Identity.Low>>48!=0&&value.LogicalBytes==32&&value.AllocatedBytes>=0&&
                    value.LinkCount==1&&!value.WritableHandleOpen&&value.Modified.HasValue,"ScaleNativeBefore");
                Require(values.TryAdd(value.Identity.Low,value)&&paths.TryAdd(value.Identity.Low,path),"ScaleNativeBefore");
            }
            return values;
        }
        public void After(IReadOnlyDictionary<ulong,FileStorageInfo> before,CancellationToken token)
        {
            foreach(var item in before)
            {
                token.ThrowIfCancellationRequested();string path=paths[item.Key];context.ValidateScalePath(path,token);
                Require(NativeStorage.TryRead(path,false,FileAttributes.Archive,out var value,out _)&&value==item.Value,"ScaleNativeAfter");
            }
        }
        public void Dispose()=>context.Dispose();
    }
    private static void Require([DoesNotReturnIf(false)] bool condition,string code) { if(!condition)throw new NtfsScaleException(code); }
    internal static NtfsScaleWorkload Workload(int files)
    {
        Require(files is 10000 or 100000,"ScaleCount");return new(files,100,10,files/10,files*8/10,files/10);
    }
    internal static string FilePath(string root,int files,int index)=>Path.Combine(root,"d"+(index/(files/100)).ToString("D4",System.Globalization.CultureInfo.InvariantCulture),
        "f"+index.ToString("D7",System.Globalization.CultureInfo.InvariantCulture)+".dat");
    internal static int ExpectedLength(int index)=>index%10==0?32:index%10==9?0:1024;

    private static void Prepare(string manifest,int files,CancellationToken token)
    {
        using var context=NtfsVerifiedFixtureContext.CreateForScale(manifest,preparation:true,token:token);
        Require(!Directory.Exists(context.DataRoot)&&!File.Exists(context.DataRoot),"ScaleFreshData");
        Require(CreateDirectoryW(context.DataRoot,IntPtr.Zero),"ScaleCreateRoot");context.ValidateScalePath(context.DataRoot,token);
        byte[] body=Enumerable.Repeat((byte)0x61,1024).ToArray();int perDirectory=files/100;
        for(int directory=0;directory<100;directory++)
        {
            token.ThrowIfCancellationRequested();string parent=Path.GetDirectoryName(FilePath(context.DataRoot,files,directory*perDirectory))!;
            Require(CreateDirectoryW(parent,IntPtr.Zero),"ScaleCreateDirectory");context.ValidateScalePath(parent,token);
            for(int local=0;local<perDirectory;local++)
            {
                token.ThrowIfCancellationRequested();int index=directory*perDirectory+local;
                using var stream=new FileStream(FilePath(context.DataRoot,files,index),FileMode.CreateNew,FileAccess.Write,FileShare.None);
                int length=ExpectedLength(index);if(length!=0)stream.Write(body,0,length);
            }
            if(directory<10)
            {
                string alias=Path.Combine(parent,"h"+directory.ToString("D7",System.Globalization.CultureInfo.InvariantCulture)+".dat");
                Require(CreateHardLinkW(alias,FilePath(context.DataRoot,files,directory*perDirectory+1),IntPtr.Zero),"ScaleCreateHardLink");
            }
        }
        token.ThrowIfCancellationRequested();
    }

    internal static ReadResult ReadForTests(IBackend backend,NtfsRawVolumeLease.INative native,CancellationToken token=default)=>ReadRaw(backend,native,token);
    private static ReadResult ReadRaw(IBackend backend,NtfsRawVolumeLease.INative? native,CancellationToken token,Action<string>? progress=null)
    {
        NtfsRawVolumeLease? lease=null;bool disposed=false;
        try
        {
            token.ThrowIfCancellationRequested();progress?.Invoke("SourceGuard");var root=backend.Root(token);var facts=backend.Facts(false,token);
            Require(facts.Geometry is {SectorBytes:512,ClusterBytes:4096,RecordBytes:1024}&&facts.Geometry.MftValidDataLength is >=1024&&
                facts.Geometry.MftValidDataLength%1024==0&&facts.Geometry.MftValidDataLength/1024<=262144,"ScaleGeometry");
            progress?.Invoke("SourceNativeBefore");var before=backend.Before(token);Require(before.Count<=32768,"ScaleFallbackBudget");
            foreach(var item in before)Require(item.Value.Identity.Volume==root.Identity.Volume&&item.Value.Identity.High==0&&item.Value.Identity.Low==item.Key&&
                item.Value.LogicalBytes>=0&&item.Value.AllocatedBytes>=0&&!item.Value.WritableHandleOpen&&item.Value.Modified.HasValue,"ScaleNativeBefore");
            progress?.Invoke("SourceLock");lease=native==null?NtfsRawVolumeLease.AcquireScale(backend.Volume,facts.Geometry.VolumeBytes,512,facts.PhysicalSectorBytes,token):
                NtfsRawVolumeLease.AcquireScaleForTests(backend.Volume,facts.Geometry.VolumeBytes,512,facts.PhysicalSectorBytes,native,token);
            progress?.Invoke("SourceMap");byte[] bootstrap=new byte[1024];long start=checked(facts.Geometry.MftStartLcn*4096);
            Require(lease.ReadInto(start,bootstrap,token)==1024,"ScaleBootstrap");var mapped=NtfsMftMap.Parse(bootstrap,facts.Geometry,token);
            Require(mapped.Status=="Mapped"&&mapped.Map!=null,"ScaleMap");var map=mapped.Map!;var reverse=map.PlanRecord(0,token);
            Require(reverse.Count==1&&reverse[0].VolumeOffset==start&&reverse[0].Length==1024&&reverse[0].RecordOffset==0,"ScaleMapBinding");
            progress?.Invoke("SourceBitmap");int slots=checked((int)(facts.Geometry.MftValidDataLength/1024));var prefixPlan=NtfsMftBitmapPrefix.ParseScale(bootstrap,facts.Geometry,slots,token);
            byte[] prefix;
            if(prefixPlan.Status=="Resident")prefix=prefixPlan.CopyPrefix();
            else
            {
                Require(prefixPlan.Status=="Planned"&&prefixPlan.ReadBytes>0&&prefixPlan.ReadBytes<=32768,"ScaleBitmapPlan");
                byte[] aligned=new byte[prefixPlan.ReadBytes];int copied=0;
                foreach(var slice in prefixPlan.ReadPlan)
                {
                    token.ThrowIfCancellationRequested();Require(slice.RecordOffset==copied&&slice.Length>0&&slice.Length<=aligned.Length-copied,"ScaleBitmapCoverage");
                    copied+=lease.ReadInto(slice.VolumeOffset,aligned.AsMemory(copied,slice.Length),token);
                }
                Require(copied==aligned.Length,"ScaleBitmapCoverage");prefix=aligned.AsSpan(0,prefixPlan.LogicalPrefixBytes).ToArray();
            }
            Require(prefix.Length==(slots+7)/8,"ScaleBitmapCoverage");
            int Read(long offset,Memory<byte> destination,CancellationToken cancellation)
            {
                int copied=0;foreach(var slice in map.PlanRange(offset,destination.Length,cancellation))
                {
                    cancellation.ThrowIfCancellationRequested();Require(slice.RecordOffset==copied&&slice.Length>0&&slice.Length<=destination.Length-copied,"ScaleReadCoverage");
                    copied+=lease.ReadInto(slice.VolumeOffset,destination.Slice(copied,slice.Length),cancellation);
                }
                Require(copied==destination.Length,"ScaleReadCoverage");return copied;
            }
            progress?.Invoke("SourceRead");var collected=NtfsMftIndexCollector.CollectScale(map,[],Read,token);
            bool Allocated(ulong slot){Require(slot<(ulong)slots,"ScaleBitmapSlot");return(prefix[(int)(slot/8)]&(1<<(int)(slot%8)))!=0;}
            progress?.Invoke("SourceIndex");var index=NtfsScaleDirectoryIndex.Build(collected.Records,Allocated,root,before,token);
            Require(index.Report.CompleteWithinObservedRecords&&index.Report.UnknownLogicalObjects==0&&index.Report.UnknownAllocatedObjects==0,"ScaleIndexIncomplete");
            foreach(var item in before)Require(collected.Records.TryGetValue(item.Key,out var record)&&record.LogicalBytes==item.Value.LogicalBytes&&
                record.LinkCount==item.Value.LinkCount,"ScaleNativeBinding");
            var tree=NtfsScaleDirectoryIndex.Materialize(index,backend.DataRoot,token);
            int calls=lease.ReadCalls;long bytes=lease.RequestedBytes;lease.Dispose();token.ThrowIfCancellationRequested();
            Require(lease.UnlockSucceeded&&lease.BufferReleaseSucceeded,"ScaleUnlock");
            progress?.Invoke("SourceNativeAfter");backend.After(before,token);Require(backend.Root(token)==root&&backend.Facts(true,token)==facts,"ScalePostFacts");
            progress?.Invoke("SourceClose");
            Exception? cleanup=null;disposed=true;try{backend.Dispose();}catch(Exception error){cleanup=error;}
            token.ThrowIfCancellationRequested();Require(cleanup==null,"ScaleCleanup");Array.Clear(prefix);
            return new(new(tree,TimeSpan.Zero,0,[]),calls,bytes,before.Count,before.Count,before.Count);
        }
        finally
        {
            lease?.Dispose();if(!disposed){try{backend.Dispose();}catch{}}
        }
    }
    private static ReadResult ReadRaw(string manifest,int files,CancellationToken token,Action<string>? progress=null)
    {
        progress?.Invoke("SourceGuard");return ReadRaw(new Backend(NtfsVerifiedFixtureContext.CreateForScale(manifest,token:token),files),null,token,progress);
    }
    private static ReadResult ReadDirectory(string manifest,CancellationToken token)
    {
        using var context=NtfsVerifiedFixtureContext.CreateForScale(manifest,token:token);var before=context.CaptureScaleRoot(token);var facts=context.QueryFacts(false,token);
        var scan=new DiskScanner(8).Scan(context.DataRoot,null,token);
        Require(context.CaptureScaleRoot(token)==before&&context.QueryFacts(true,token)==facts,"ScaleDirectoryPostFacts");
        token.ThrowIfCancellationRequested();return new(scan,0,0,0,0,0);
    }
    internal static byte[] Fingerprint(ScanResult scan,int files,CancellationToken token=default)
    {
        Require(scan.Skipped==0&&scan.Issues.Count==0&&scan.AllocationIssues.Count==0&&!scan.Root.Incomplete&&scan.Root.UnknownAllocationFiles==0&&
            scan.Root.FileCount==files+10&&scan.Root.FolderCount==100,"ScaleResultIncomplete");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var queue=new Queue<(DiskEntry Node,string Prefix)>();queue.Enqueue((scan.Root,""));var seen=new HashSet<DiskEntry>(ReferenceEqualityComparer.Instance);
        int visited=0;long characters=0;var objects=new HashSet<FileIdentity>();byte[] numeric=new byte[49];
        while(queue.Count>0)
        {
            token.ThrowIfCancellationRequested();var(node,prefix)=queue.Dequeue();Require(seen.Add(node),"ScaleTreeCycle");
            foreach(var child in node.Children.OrderBy(item=>item.Name,StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();Require(++visited<=files+110&&child.Storage!=null&&!child.Incomplete&&child.UnknownAllocationFiles==0,"ScaleTreeBudget");
                string path=prefix+child.Name;characters+=path.Length;Require(characters<=32L*1024*1024,"ScaleTreeBudget");
                hash.AppendData(Encoding.Unicode.GetBytes(path));var row=numeric.AsSpan();
                row[0]=child.IsDirectory?(byte)1:(byte)0;BinaryPrimitives.WriteUInt64LittleEndian(row[1..],child.Storage!.Identity.Volume);
                BinaryPrimitives.WriteUInt64LittleEndian(row[9..],child.Storage.Identity.Low);BinaryPrimitives.WriteUInt64LittleEndian(row[17..],child.Storage.Identity.High);
                BinaryPrimitives.WriteInt64LittleEndian(row[25..],child.IsDirectory?0:child.Size);
                BinaryPrimitives.WriteInt64LittleEndian(row[33..],child.IsDirectory?0:child.Storage.AllocatedBytes);
                BinaryPrimitives.WriteInt64LittleEndian(row[41..],child.Name.Length);hash.AppendData(numeric);
                if(!child.IsDirectory)objects.Add(child.Storage.Identity);
                if(child.IsDirectory)queue.Enqueue((child,path+"/"));
            }
        }
        Require(visited==files+110&&objects.Count==files&&scan.Root.Storage!=null,"ScaleTreeBudget");
        byte[] totals=new byte[64];BinaryPrimitives.WriteInt64LittleEndian(totals.AsSpan(0),scan.Root.Size);
        BinaryPrimitives.WriteInt64LittleEndian(totals.AsSpan(8),scan.Root.AllocatedSize);BinaryPrimitives.WriteInt64LittleEndian(totals.AsSpan(16),scan.Root.FileCount);
        BinaryPrimitives.WriteInt64LittleEndian(totals.AsSpan(24),scan.Root.FolderCount);BinaryPrimitives.WriteUInt64LittleEndian(totals.AsSpan(32),scan.Root.Storage!.Identity.Volume);
        BinaryPrimitives.WriteUInt64LittleEndian(totals.AsSpan(40),scan.Root.Storage.Identity.Low);BinaryPrimitives.WriteUInt64LittleEndian(totals.AsSpan(48),scan.Root.Storage.Identity.High);
        BinaryPrimitives.WriteInt64LittleEndian(totals.AsSpan(56),objects.Count);hash.AppendData(totals);return hash.GetHashAndReset();
    }
    private sealed class Sampler:IDisposable
    {
        private readonly Process process=Process.GetCurrentProcess();private readonly CancellationTokenSource cancellation=new();private readonly Task sampling;
        internal long Peak;internal int Samples;internal bool Failed;private int disposed;
        internal Sampler()
        {
            Sample();
            // A blocking sampler must not consume one of the worker-pool
            // threads used by the directory-scanner comparison.
            sampling=Task.Factory.StartNew(()=>{while(!cancellation.Token.WaitHandle.WaitOne(20))Sample();},
                CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        }
        private void Sample(){try{process.Refresh();long value=process.WorkingSet64;Interlocked.Increment(ref Samples);long current=Interlocked.Read(ref Peak);
            while(value>current){long actual=Interlocked.CompareExchange(ref Peak,value,current);if(actual==current)break;current=actual;}}catch{Failed=true;}}
        public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;cancellation.Cancel();try{sampling.GetAwaiter().GetResult();Sample();}catch{Failed=true;}
            finally{process.Dispose();cancellation.Dispose();}}
    }
    internal static int Run(string manifestPath,int files)
    {
        string? summary=null;string stage="Preparation";
        try
        {
            var workload=Workload(files);manifestPath=Path.GetFullPath(manifestPath);
            using(var preparation=NtfsVerifiedFixtureContext.CreateForScale(manifestPath,preparation:true))
                summary=Path.Combine(Path.GetDirectoryName(manifestPath)!,"live-summary.json");
            Require(!File.Exists(summary),"ScaleFreshSummary");var prepare=Stopwatch.StartNew();Prepare(manifestPath,files,default);
            // Explicit warm-up is not a cold-cache claim and is excluded from
            // scan timings. Each measured path still opens/closes its own owner.
            stage="Source";_=Fingerprint(ReadRaw(manifestPath,files,default,value=>stage=value).Scan,files);
            stage="Directory";var reference=ReadDirectory(manifestPath,default).Scan;
            byte[] expected=Fingerprint(reference,files);var counts=new NtfsScaleResultCounts(files,reference.Root.FileCount,reference.Root.FolderCount,
                reference.Root.Size,reference.Root.AllocatedSize,reference.Root.UnknownAllocationFiles);reference=null!;prepare.Stop();
            var samples=new List<NtfsScaleSample>(6);
            for(int round=1;round<=3;round++)foreach(string algorithm in round==2?new[]{"Directory","RawMft"}:new[]{"RawMft","Directory"})
            {
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
                using var process=Process.GetCurrentProcess();process.Refresh();TimeSpan cpu=process.TotalProcessorTime;long allocated=GC.GetTotalAllocatedBytes(false);
                stage=algorithm=="RawMft"?"Source":"Directory";
                var sampler=new Sampler();var watch=Stopwatch.StartNew();ReadResult value;
                try{value=algorithm=="RawMft"?ReadRaw(manifestPath,files,default,current=>stage=current):ReadDirectory(manifestPath,default);}
                finally{watch.Stop();sampler.Dispose();}
                Require(!sampler.Failed,"ScaleSampler");process.Refresh();double cpuMs=(process.TotalProcessorTime-cpu).TotalMilliseconds;
                long allocatedBytes=Math.Max(0,GC.GetTotalAllocatedBytes(false)-allocated);
                stage="Comparison";bool matched=Fingerprint(value.Scan,files).SequenceEqual(expected);Require(matched,"ScaleComparison");
                samples.Add(new(round,algorithm,watch.Elapsed.TotalMilliseconds,cpuMs,allocatedBytes,value.ReadCalls,value.RequestedBytes,value.FallbackObjects,
                    value.BeforeReads,value.AfterReads,true,sampler.Peak,sampler.Samples));
            }
            var report=new NtfsScaleBenchmarkReport("ntfs-scale-benchmark-v1",workload,prepare.Elapsed.TotalMilliseconds,samples.AsReadOnly(),true,20,counts);
            WriteSummary(summary,new{schema=1,status="scale_benchmarked",checks=1,failures=0,code="None",stage="Complete",native_error=0,native_operation="None",scale_benchmark=report});
            Console.WriteLine("PASS NTFS controlled scale benchmark");return 0;
        }
        catch(Exception error)
        {
            string code=error is OperationCanceledException?"ScaleCancelled":error is NtfsScaleException {Code:"ScaleCleanup"}?"ScaleCleanupFailed":
                stage=="Preparation"?"ScalePreparationFailed":stage.StartsWith("Source",StringComparison.Ordinal)?"ScaleSourceFailed":stage=="Directory"?"ScaleDirectoryFailed":stage=="Comparison"?"ScaleComparisonFailed":"ScaleFailed";
            int nativeError=error is NtfsRawVolumeException raw?raw.NativeErrorCode:error is NtfsFixtureGuardException guard?guard.NativeErrorCode:0;
            string operation=error is NtfsRawVolumeException known&&known.Code is "LockFailed" or "SeekFailed" or "ReadFailed" or "ShortRead" or "AllocationFailed" or "BufferReleaseFailed"?known.Code:"None";
            if(summary!=null&&!File.Exists(summary)){try{WriteSummary(summary,new{schema=1,status="failed",checks=1,failures=1,code,stage,native_error=Math.Max(0,nativeError),native_operation=operation});}catch{}}
            Console.Error.WriteLine("FAIL NTFS controlled scale benchmark "+code);return 1;
        }
    }
    private static void WriteSummary(string path,object value)
    {
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(value);Require(bytes.Length<=65536,"ScaleSummaryBudget");
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);stream.Write(bytes);stream.Flush(true);
    }
}
