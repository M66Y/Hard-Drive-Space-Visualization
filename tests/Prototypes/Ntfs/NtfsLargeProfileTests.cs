using System.Buffers.Binary;
using System.Collections;
using System.Text;

namespace DiskAtlas.NtfsPrototype;

// All inventories, cursor pages, MFT records and callbacks are synthetic.
// These checks neither open paths nor create a Native source or snapshot.
internal static class NtfsLargeProfileTests
{
    private static FileIdentity Id(ulong slot)=>new(41,NtfsRecordFixtures.Reference(slot,3),0);
    private readonly record struct Row(string Name,ulong Slot,bool Directory=false);
    private sealed class Backend(Func<int,(int Count,Func<int,Row> Row)> tree):NtfsSnapshotNamespace.IBackend
    {
        internal int Opened,Closed;
        internal readonly List<string> DirectoryPaths=[];
        public FileIdentity RootIdentity=>Id(1);
        public NtfsSnapshotNamespace.ICursor OpenDirectory(NtfsSnapshotNamespaceEntry entry,string path,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Opened++;DirectoryPaths.Add(path);
            var source=tree(entry.Index);return new Cursor(this,entry.Identity,source.Count,source.Row);
        }
        private sealed class Cursor(Backend owner,FileIdentity identity,int count,Func<int,Row> row):NtfsSnapshotNamespace.ICursor
        {
            private int emitted;
            public FileIdentity Identity=>identity;
            public FileAttributes Attributes=>FileAttributes.Directory;
            public NtfsSnapshotNamespaceIo Read(bool restart,Memory<byte> destination,CancellationToken token)
            {
                token.ThrowIfCancellationRequested();if(emitted==count)return new(false,18);
                var span=destination.Span;span.Clear();int offset=0,last=0;
                while(emitted<count)
                {
                    token.ThrowIfCancellationRequested();var value=row(emitted);
                    int length=(88+value.Name.Length*2+7)&~7;
                    if(offset+length>span.Length)break;
                    BinaryPrimitives.WriteUInt32LittleEndian(span[offset..],(uint)length);
                    BinaryPrimitives.WriteUInt32LittleEndian(span[(offset+56)..],(uint)(value.Directory?FileAttributes.Directory:FileAttributes.Archive));
                    BinaryPrimitives.WriteUInt32LittleEndian(span[(offset+60)..],(uint)(value.Name.Length*2));
                    BinaryPrimitives.WriteUInt64LittleEndian(span[(offset+72)..],Id(value.Slot).Low);
                    Encoding.Unicode.GetBytes(value.Name.AsSpan(),span[(offset+88)..]);
                    last=offset;offset+=length;emitted++;
                }
                BinaryPrimitives.WriteUInt32LittleEndian(span[last..],0);return new(true,0);
            }
            public void Dispose()=>owner.Closed++;
        }
    }
    private static Backend Small()=>new(index=>index switch
    {
        0=>(2,i=>i==0?new("folder",2,true):new("root.bin",3)),
        1=>(1,_=>new("child.bin",4)),
        _=>(0,_=>default)
    });
    private static Backend Chain(int depth,int nameLength=1)=>new(index=>index<depth?(1,_=>new(new string('d',nameLength),(ulong)index+2,true)):(0,_=>default));
    private static NtfsMftMap Map(int slots)
    {
        long bytes=(long)slots*1024,clusters=(slots+3L)/4;
        byte[] runs=new byte[18];runs[0]=0x88;
        BinaryPrimitives.WriteInt64LittleEndian(runs.AsSpan(1),clusters);
        BinaryPrimitives.WriteInt64LittleEndian(runs.AsSpan(9),10);
        var record=NtfsMftMapTests.Record0(runs,(ulong)clusters-1,clusters*4096,bytes,bytes);
        return NtfsMftMap.Parse(record,new(512,4096,1024,1L<<31,(1L<<31)/4096,10,bytes)).Map!;
    }
    private sealed class VirtualEntries(int count):IReadOnlyList<NtfsSnapshotNamespaceEntry>
    {
        internal int Accesses;
        public int Count=>count;
        public NtfsSnapshotNamespaceEntry this[int index]{get{Accesses++;throw new NtfsSnapshotSizeException("SyntheticPrivateInput");}}
        public IEnumerator<NtfsSnapshotNamespaceEntry> GetEnumerator()=>throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private sealed class BadPaths:IReadOnlyList<string?>
    {
        public int Count=>2;
        public string? this[int index]=>throw new NtfsSnapshotNamespaceException("SyntheticPrivatePath");
        public IEnumerator<string?> GetEnumerator()=>throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private static NtfsSnapshotNamespaceReport Report(int count,int directories,int? nameCharacters=null)=>new("snapshot-namespace-v1",count,
        count-directories-1,directories,count-directories-1,0,directories+1,directories+1,2*(directories+1),nameCharacters??count-1,directories+1,true);
    private static NtfsSnapshotSizeIndexResult ZeroDirectories(int directories,NtfsScanLimits limits)
    {
        var entries=new NtfsSnapshotNamespaceEntry[directories+1];var paths=new string?[entries.Length];
        var objects=new Dictionary<FileIdentity,NtfsSnapshotObjectSize>();
        for(int i=0;i<entries.Length;i++)
        {
            entries[i]=new(i,i==0?-1:0,i==0?"":"d"+i,Id((ulong)i+1),FileAttributes.Directory,false);
            paths[i]=entries[i].Name;objects.Add(entries[i].Identity,new(entries[i].Identity,true,0,0,1,false));
        }
        var scope=new NtfsSnapshotNamespaceResult(Report(entries.Length,directories,entries.Sum(entry=>entry.Name.Length)),entries,paths,Id(1),limits);
        return new(scope,objects,new("snapshot-size-index-v1",objects.Count,0,directories,0,objects.Count,0,0,0,0,objects.Count,0,0,0,0,0,0,0,0,true));
    }

    internal static int Run()
    {
        int passed=0;
        void Check(bool value,string name){if(!value)throw new InvalidOperationException("Large profile check failed: "+name);passed++;Console.WriteLine("PASS NTFS large profile "+name);}
        Exception? Catch(Action action){try{action();return null;}catch(Exception error){return error;}}
        var standard=NtfsScanLimits.Standard;var large=NtfsScanLimits.Large;
        Check(standard.MaximumEntries==262144&&standard.MaximumCharacters==32*1024*1024&&standard.MaximumMftPages==4096&&
            standard.MaximumBitmapPages==512&&standard.MaximumNativeObjects==32768&&!standard.CompactPaths,"legacy fixed budgets remain unchanged");
        Check(large.MaximumEntries==1048576&&large.MaximumCharacters==64*1024*1024&&large.MaximumMftPages==16384&&
            large.MaximumBitmapPages==4096&&large.MaximumNativeObjects==131072&&large.CompactPaths,"large fixed budgets are explicit rather than unbounded caller values");
        var legacyBackend=Small();var legacy=NtfsSnapshotNamespace.Read(legacyBackend);
        var compactBackend=Small();var compact=NtfsSnapshotNamespace.Read(compactBackend,limits:large);
        Check(ReferenceEquals(legacy.Limits,standard)&&ReferenceEquals(compact.Limits,large)&&legacy.Report==compact.Report&&
            Enumerable.Range(0,4).All(i=>legacy.RelativePath(i)==compact.RelativePath(i)),"optional profile preserves namespace identities counts and paths");
        Check(Enumerable.Range(0,4).All(legacy.HasCachedPath)&&compact.HasCachedPath(0)&&compact.HasCachedPath(1)&&
            !compact.HasCachedPath(2)&&!compact.HasCachedPath(3)&&compact.RelativePath(3)=="folder/child.bin",
            "compact inventory retains only directory prefixes and composes file paths on demand");
        Check(compactBackend.DirectoryPaths.SequenceEqual(new[]{"","folder"})&&compactBackend.Opened==compactBackend.Closed,
            "directory cursors receive complete prefixes and all are released");
        var bad=new NtfsSnapshotNamespaceResult(compact.Report,compact.Entries,new BadPaths(),compact.RootIdentity,large);
        Check(Catch(()=>bad.RelativePath(1)) is NtfsSnapshotNamespaceException{Code:"NamespaceEntryIndex",InnerException:null} e&&
            e.Message=="NamespaceEntryIndex","foreign path accessor errors never expose caller text");

        // Just over the old boundary: no million-object fixture or real files.
        NtfsSnapshotNamespaceResult? partial=null;
        var rejectedBackend=new Backend(_=>(standard.MaximumEntries,i=>new("f"+i.ToString("D8"),2)));
        Check(Catch(()=>partial=NtfsSnapshotNamespace.Read(rejectedBackend)) is NtfsSnapshotNamespaceException{Code:"NamespaceEntryBudget"}&&
            partial==null&&rejectedBackend.Opened==rejectedBackend.Closed,"standard actual cursor still stops at its original entry boundary");
        var acceptedBackend=new Backend(_=>(standard.MaximumEntries,i=>new("f"+i.ToString("D8"),2)));
        var accepted=NtfsSnapshotNamespace.Read(acceptedBackend,limits:large);
        Check(accepted.Entries.Count==standard.MaximumEntries+1&&accepted.Report.UniqueFileObjects==1&&
            !accepted.HasCachedPath(standard.MaximumEntries)&&accepted.RelativePath(standard.MaximumEntries)=="f00262143"&&
            acceptedBackend.Opened==acceptedBackend.Closed,"large actual cursor accepts a scope above 262144 without retaining file path copies");

        var depth512=NtfsSnapshotNamespace.Read(Chain(512),limits:large);
        Check(depth512.Entries.Count==513&&depth512.RelativePath(512).Length==1023,"compact depth 512 is valid");
        partial=null;
        Check(Catch(()=>partial=NtfsSnapshotNamespace.Read(Chain(513),limits:large)) is NtfsSnapshotNamespaceException{Code:"NamespacePathBudget"}&&partial==null,
            "compact depth overflow publishes no partial graph");
        Check(NtfsSnapshotNamespace.Read(Chain(513)).Entries.Count==514,"legacy traversal is not given an unrelated new depth restriction");
        Check(NtfsSnapshotNamespace.Read(Chain(128,255),limits:large).RelativePath(128).Length==32767,
            "compact relative path accepts its exact character boundary");
        Check(Catch(()=>NtfsSnapshotNamespace.Read(Chain(129,255),limits:large)) is NtfsSnapshotNamespaceException{Code:"NamespacePathBudget"},
            "compact path length is bounded independently of depth and total directory characters");

        var map=Map(16);
        foreach(var limits in new[]{standard,large})
        {
            var virtualEntries=new VirtualEntries(standard.MaximumEntries+1);
            var scope=new NtfsSnapshotNamespaceResult(Report(virtualEntries.Count,0),virtualEntries,Array.Empty<string?>(),Id(1),limits);
            NtfsSnapshotSizeIndexResult? indexed=null;
            var error=Catch(()=>indexed=NtfsSnapshotSizeIndex.BuildAsync(scope,map,(_,_,_)=>throw new InvalidOperationException(),_=>true,
                (_,_)=>throw new InvalidOperationException()).GetAwaiter().GetResult());
            string expected=limits==standard?"SnapshotSizeNamespaceIncomplete":"SnapshotSizeInput";
            Check(error is NtfsSnapshotSizeException failure&&failure.Code==expected&&failure.Message==expected&&failure.InnerException==null&&
                indexed==null&&virtualEntries.Accesses==(limits==standard?0:1),"index applies selected entry cap before source access and sanitizes foreign failures");
        }

        // Sparse selected identities exceed the old page cap using only 4097
        // small declarations. No raw page is allocated by the test callback.
        var sparseEntries=new NtfsSnapshotNamespaceEntry[4097];
        sparseEntries[0]=new(0,-1,"",Id(1),FileAttributes.Directory,false);
        for(int i=1;i<sparseEntries.Length;i++)sparseEntries[i]=new(i,0,"f"+i,Id((ulong)i*64),FileAttributes.Archive,false);
        var sparseMap=Map(262145);
        foreach(var limits in new[]{standard,large})
        {
            var scope=new NtfsSnapshotNamespaceResult(Report(sparseEntries.Length,0,sparseEntries.Sum(entry=>entry.Name.Length)),sparseEntries,new string?[sparseEntries.Length],Id(1),limits);
            int reads=0;NtfsSnapshotSizeIndexResult? indexed=null;
            var error=Catch(()=>indexed=NtfsSnapshotSizeIndex.BuildAsync(scope,sparseMap,(_,_,_)=>
                {reads++;throw new NtfsSnapshotSizeException("SyntheticPrivateRaw");},_=>true,(_,_)=>throw new InvalidOperationException()).GetAwaiter().GetResult());
            string expected=limits==standard?"SnapshotSizeRawBudget":"SnapshotSizeRead";
            Check(error is NtfsSnapshotSizeException failure&&failure.Code==expected&&failure.Message==expected&&failure.InnerException==null&&
                reads==(limits==standard?0:1)&&indexed==null,"selected MFT pages obey the profile cap and failed reads publish no partial index");
        }

        // One object beyond the old Native supplement cap; generated records
        // are copied into a single reused page rather than kept as a corpus.
        var residentEntries=new NtfsSnapshotNamespaceEntry[standard.MaximumNativeObjects+2];
        residentEntries[0]=new(0,-1,"",Id(1),FileAttributes.Directory,false);
        for(int i=1;i<residentEntries.Length;i++)residentEntries[i]=new(i,0,"r"+i,Id((ulong)i+1),FileAttributes.Archive,false);
        var residentMap=Map(residentEntries.Length+1);
        int ResidentRead(long offset,Memory<byte> buffer,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();buffer.Span.Clear();
            for(int at=0;at+1024<=buffer.Length;at+=1024)
            {
                ulong slot=(ulong)((offset+at)/1024);if(slot==0||slot>(ulong)residentEntries.Length)continue;
                byte[] record=slot==1?NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("scope"),NtfsRecordFixtures.Data(0)],directory:true):
                    NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("r"+(slot-1),parent:Id(1).Low),NtfsRecordFixtures.Data(1)]);
                record.CopyTo(buffer.Span[at..]);
            }
            return buffer.Length;
        }
        foreach(var limits in new[]{standard,large})
        {
            var scope=new NtfsSnapshotNamespaceResult(Report(residentEntries.Length,0,residentEntries.Sum(entry=>entry.Name.Length)),residentEntries,new string?[residentEntries.Length],Id(1),limits);
            int calls=0;NtfsSnapshotSizeIndexResult? indexed=null;
            var error=Catch(()=>indexed=NtfsSnapshotSizeIndex.BuildAsync(scope,residentMap,ResidentRead,_=>true,(_,_)=>
                {Interlocked.Increment(ref calls);throw new NtfsSnapshotSizeException("SyntheticPrivateNative");}).GetAwaiter().GetResult());
            string expected=limits==standard?"SnapshotSizeNativeBudget":"SnapshotSizeNativeRead";
            Check(error is NtfsSnapshotSizeException failure&&failure.Code==expected&&failure.Message==expected&&failure.InnerException==null&&
                (limits==standard?calls==0:calls is >=1 and <=NtfsSnapshotSizeIndex.NativeWorkers)&&indexed==null,
                "native supplement cap follows the profile while callback failures cancel and join the bounded workers");
        }

        // Build real compact owned tables from tiny in-memory raw pages.
        var raw=new Dictionary<ulong,byte[]>
        {
            [1]=NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("scope"),NtfsRecordFixtures.Data(0)],directory:true),
            [2]=NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("folder",parent:Id(1).Low),NtfsRecordFixtures.Data(0)],directory:true),
            [3]=NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("root.bin",parent:Id(1).Low),NtfsRecordFixtures.Nonresident()]),
            [4]=NtfsRecordFixtures.Record([NtfsRecordFixtures.Name("child.bin",parent:Id(2).Low),NtfsRecordFixtures.Nonresident()])
        };
        int Read(long offset,Memory<byte> buffer,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();buffer.Span.Clear();
            foreach(var pair in raw){long at=(long)pair.Key*1024-offset;if(at>=0&&at<=buffer.Length-1024)pair.Value.CopyTo(buffer.Span[(int)at..]);}
            return buffer.Length;
        }
        var index=NtfsSnapshotSizeIndex.BuildAsync(compact,map,Read,_=>true,(_,_)=>throw new InvalidOperationException("NoNativeExpected")).GetAwaiter().GetResult();
        Check(ReferenceEquals(index.Namespace.Limits,large)&&!index.Namespace.HasCachedPath(2)&&!index.Namespace.HasCachedPath(3)&&
            index.Namespace.RelativePath(3)=="folder/child.bin","owned size index preserves compact file paths after defensive namespace copying");
        var projected=NtfsSnapshotSizeIndex.Materialize(index,"S:\\Synthetic");
        Check(projected.Size==12000&&projected.AllocatedSize==16384&&projected.FileCount==2&&projected.FolderCount==1&&
            projected.Children.Single(n=>n.IsDirectory).Children.Single().Path==Path.Join("S:\\Synthetic","folder","child.bin"),
            "tree projection preserves totals and the existing lazy file path behavior");

        // Root prefix cost is counted once per retained absolute directory.
        foreach(var limits in new[]{standard,large})
        {
            int dirs=limits.MaximumCharacters/16384+1;var zero=ZeroDirectories(dirs,limits);DiskEntry? tree=null;
            Check(Catch(()=>tree=NtfsSnapshotSizeIndex.Materialize(zero,"S:\\"+new string('x',16381))) is
                NtfsSnapshotSizeException{Code:"SnapshotSizeProjectionBudget"}&&tree==null,
                "absolute root prefix multiplied by directory count is rejected before tree publication");
        }
        var smallZero=ZeroDirectories(1,large);
        Check(NtfsSnapshotSizeIndex.Materialize(smallZero,"S:\\").Children.Single().Path=="S:\\d1",
            "absolute directory accounting handles a root that already ends in a separator");
        using(var cancelled=new CancellationTokenSource())
        {
            cancelled.Cancel();
            Check(Catch(()=>NtfsSnapshotNamespace.Read(Small(),cancelled.Token,large)) is OperationCanceledException&&
                Catch(()=>NtfsSnapshotSizeIndex.BuildAsync(compact,map,Read,_=>true,(_,_)=>throw new InvalidOperationException(),cancelled.Token).GetAwaiter().GetResult()) is OperationCanceledException&&
                Catch(()=>NtfsSnapshotSizeIndex.Materialize(index,"S:\\Synthetic",cancelled.Token)) is OperationCanceledException,
                "cancelled traversal indexing and projection cannot publish partial results");
        }
        return passed;
    }
}
