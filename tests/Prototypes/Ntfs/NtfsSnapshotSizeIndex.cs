using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsSnapshotSizeRequest(int EntryIndex,FileIdentity Identity,bool Directory,long? ExpectedLogicalBytes);
internal sealed record NtfsSnapshotNativeSize(FileStorageInfo Storage,bool Directory,bool DeletePending=false);
internal readonly record struct NtfsSnapshotObjectSize(FileIdentity Identity,bool Directory,long LogicalBytes,long AllocatedBytes,uint Links,bool Native);
internal sealed record NtfsSnapshotSizeIndexReport(string Revision,int ScopeObjects,int FileObjects,int DirectoryObjects,int FileEntries,
    int RawResolvedObjects,int NativeResolvedObjects,int NativeCalls,int PeakNativeWorkers,int PeakQueuedNative,int DecodedScopeRecords,
    int RawPagesRead,int UnknownObjects,long LogicalBytes,long AllocatedBytes,double RawReadMilliseconds,double ParseMilliseconds,
    double IndexMilliseconds,double NativeMilliseconds,bool Complete);
internal sealed class NtfsSnapshotSizeIndexResult(NtfsSnapshotNamespaceResult scope,
    IReadOnlyDictionary<FileIdentity,NtfsSnapshotObjectSize> objects,NtfsSnapshotSizeIndexReport report)
{
    internal NtfsSnapshotNamespaceResult Namespace{get;}=scope;
    internal IReadOnlyDictionary<FileIdentity,NtfsSnapshotObjectSize> Objects{get;}=objects;
    internal NtfsSnapshotSizeIndexReport Report{get;}=report;
}
internal sealed class NtfsSnapshotSizeException(string code):IOException(code){internal string Code{get;}=code;}

// Pure selected-scope reconciliation. Real snapshot provenance belongs to the
// private owner supplying these callbacks, not to this model or its report.
// No full-volume names or parsed NtfsRecord instances survive decoding.
internal static class NtfsSnapshotSizeIndex
{
    internal const int MaximumEntries=262144,MaximumNativeObjects=32768,NativeWorkers=8,NativeQueueCapacity=128;
    private sealed class Item(FileIdentity identity,bool directory,int entry)
    {
        internal readonly FileIdentity Identity=identity;
        internal readonly bool Directory=directory;
        internal readonly int EntryIndex=entry;
        internal int Aliases=1;
        internal bool RawSeen,Decoded,NeedsNative;
        internal long? Logical,Allocated;
        internal uint? Links;
        internal NtfsSnapshotObjectSize Final;
    }
    private static void Require([DoesNotReturnIf(false)]bool condition,string code){if(!condition)throw new NtfsSnapshotSizeException(code);}
    private static T External<T>(Func<T> action,CancellationToken token,string code)
    {
        T value;try{value=action();}catch(Exception){token.ThrowIfCancellationRequested();throw new NtfsSnapshotSizeException(code);}
        token.ThrowIfCancellationRequested();return value;
    }
    private static long Add(long total,long value){Require(value>=0&&value<=long.MaxValue-total,"SnapshotSizeOverflow");return total+value;}
    private static double Milliseconds(long ticks)=>ticks*1000.0/Stopwatch.Frequency;
    private static void Peak(ref int peak,int value){int current=Volatile.Read(ref peak);while(value>current){int actual=Interlocked.CompareExchange(ref peak,value,current);if(actual==current)break;current=actual;}}

    internal static async Task<NtfsSnapshotSizeIndexResult> BuildAsync(NtfsSnapshotNamespaceResult scope,NtfsMftMap map,NtfsMftReadInto read,
        Func<ulong,bool> allocated,Func<NtfsSnapshotSizeRequest,CancellationToken,NtfsSnapshotNativeSize> lookup,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();long totalStart=Stopwatch.GetTimestamp();
        Require(scope!=null&&map!=null&&read!=null&&allocated!=null&&lookup!=null,"SnapshotSizeInput");
        int entryCount=External(()=>scope.Entries.Count,token,"SnapshotSizeInput");
        Require(entryCount is >=1 and <=MaximumEntries&&scope.Report.Revision=="snapshot-namespace-v1"&&scope.Report.CompleteWithinBoundary&&scope.Report.Entries==entryCount,"SnapshotSizeNamespaceIncomplete");
        var geometry=map.Geometry;long valid=geometry.MftValidDataLength;
        Require(geometry is {SectorBytes:512,ClusterBytes:4096,RecordBytes:1024}&&valid>=1024&&valid%1024==0&&valid/1024<=262144&&
            valid<=map.DataBytes&&valid<=map.InitializedBytes&&valid<=map.AllocatedBytes,"SnapshotSizeMap");
        var root=scope.RootIdentity;
        Require(root.High==0&&root.Low>>48!=0,"SnapshotSizeIdentity");
        var items=new Dictionary<FileIdentity,Item>();var bySlot=new Dictionary<ulong,Item>();
        int files=0,directories=0;long characters=0,pathCharacters=0;var directoryFlags=new bool[entryCount];
        var ownedEntries=new NtfsSnapshotNamespaceEntry[entryCount];var ownedPaths=new string[entryCount];
        for(int i=0;i<entryCount;i++)
        {
            token.ThrowIfCancellationRequested();var entry=External(()=>scope.Entries[i],token,"SnapshotSizeInput");
            Require(entry!=null&&entry.Name!=null&&entry.Index==i&&entry.Identity.Volume==root.Volume&&entry.Identity.High==0&&entry.Identity.Low>>48!=0,"SnapshotSizeIdentity");
            Require(i==0?entry.ParentIndex==-1&&entry.Identity==root&&entry.Directory&&entry.Name.Length==0:
                entry.ParentIndex>=0&&entry.ParentIndex<i&&directoryFlags[entry.ParentIndex],"SnapshotSizeNamespaceIncomplete");
            Require(i==0||entry.Name.Length is >=1 and <=255&&entry.Name is not ("." or "..")&&
                !entry.Name.Any(character=>char.IsControl(character)||character is '/' or '\\' or ':'),"SnapshotSizeNamespaceIncomplete");
            directoryFlags[i]=entry.Directory;
            ownedEntries[i]=entry;string prefix=i==0||entry.ParentIndex==0?"":ownedPaths[entry.ParentIndex]+"/";
            Require(pathCharacters<=32L*1024*1024-prefix.Length-entry.Name.Length,"SnapshotSizeNamespaceBudget");
            ownedPaths[i]=prefix+entry.Name;pathCharacters+=ownedPaths[i].Length;
            Require(!entry.Reparse&&(entry.Attributes&FileAttributes.ReparsePoint)==0,"SnapshotSizeBoundary");
            characters=Add(characters,entry.Name.Length);Require(characters<=32L*1024*1024,"SnapshotSizeNamespaceBudget");
            ulong slot=entry.Identity.Low&NtfsRecordDecoder.MaximumRecordNumber;
            Require(slot<(ulong)(valid/1024),"SnapshotSizeSlotOutsideMap");
            if(items.TryGetValue(entry.Identity,out var existing))
            {
                Require(!entry.Directory&&!existing.Directory,"SnapshotSizeDirectoryAlias");existing.Aliases++;
            }
            else
            {
                var item=new Item(entry.Identity,entry.Directory,i);Require(bySlot.TryAdd(slot,item),"SnapshotSizeSlotReused");
                Require(External(()=>allocated(slot),token,"SnapshotSizeAllocationRead"),"SourceConsistencyFailed");items.Add(entry.Identity,item);
            }
            if(i!=0){if(entry.Directory)directories++;else files++;}
        }
        Require(scope.Report.FileEntries==files&&scope.Report.Directories==directories&&scope.Report.ReparseEntries==0&&
            scope.Report.UniqueFileObjects==items.Values.Count(item=>!item.Directory)&&scope.Report.NameCharacters==characters&&
            scope.Report.OpenedDirectories==directories+1&&scope.Report.CompletedDirectories==directories+1,"SnapshotSizeNamespaceIncomplete");
        long readTicks=0,parseTicks=0;int pages=0,decoded=0;
        byte[] page=new byte[65536];
        // Membership comes from complete same-snapshot namespace enumeration.
        // Unselected slots are neither decoded nor reported as free/valid.
        foreach(var batch in bySlot.OrderBy(pair=>pair.Key).GroupBy(pair=>pair.Key/64))
        {
            token.ThrowIfCancellationRequested();long offset=checked((long)batch.Key*65536);int length=checked((int)Math.Min(65536,valid-offset));
            long before=Stopwatch.GetTimestamp();int received=External(()=>read(offset,page.AsMemory(0,length),token),token,"SnapshotSizeRead");
            readTicks+=Stopwatch.GetTimestamp()-before;Require(received==length,"SnapshotSizeShortRead");pages++;before=Stopwatch.GetTimestamp();
            foreach(var pair in batch)
            {
                token.ThrowIfCancellationRequested();var item=pair.Value;int local=checked((int)(pair.Key%64)*1024);
                var result=NtfsRecordDecoder.Decode(page.AsSpan(local,1024),512,token:token,recordNumber:pair.Key,clusterSize:4096);
                item.RawSeen=true;
                Require(result.Status is not (NtfsDecodeStatus.Free or NtfsDecodeStatus.Invalid),"SourceConsistencyFailed");
                // With this fixed raw geometry, every Unsupported result has
                // already passed the strict header and USA checks. Header
                // identity and object type contradictions must not be hidden
                // by its earlier format-unsupported return.
                var header=page.AsSpan(local,1024);ushort flags=BinaryPrimitives.ReadUInt16LittleEndian(header[22..]);
                Require(BinaryPrimitives.ReadUInt16LittleEndian(header[16..])==item.Identity.Low>>48&&(flags&1)!=0&&
                    ((flags&2)!=0)==item.Directory&&BinaryPrimitives.ReadUInt64LittleEndian(header[32..])==0&&
                    BinaryPrimitives.ReadUInt16LittleEndian(header[18..])>=item.Aliases,"SourceConsistencyFailed");
                if(result.Status==NtfsDecodeStatus.Decoded)
                {
                    var record=result.Record;Require(record!=null&&record.Reference==item.Identity.Low&&record.IsDirectory==item.Directory,"SourceConsistencyFailed");
                    Require(record.LinkCount>0&&record.LinkCount>=item.Aliases,"SourceConsistencyFailed");
                    item.Decoded=true;decoded++;item.Logical=record.LogicalBytes;item.Allocated=record.AllocatedBytes;
                    // The record header counts separate DOS FILE_NAME records;
                    // Native links count visible hard-link names. Compute this
                    // bounded scalar while the parsed names are still local.
                    int visible=0;bool unique=true;(ulong Parent,string Name) first=default;HashSet<(ulong,string)>? visibleNames=null;
                    foreach(var name in record.Names)
                    {
                        if(name.Namespace==2)continue;var pairName=(name.ParentReference,name.Name);
                        if(visible==0)first=pairName;
                        else{visibleNames??=new(){first};unique&=visibleNames.Add(pairName);}visible++;
                    }
                    bool completeNames=record.LinkCount==record.Names.Count&&visible>0&&unique&&
                        !record.LookupReasons.HasFlag(NtfsLookupReason.LinkNamesIncomplete);
                    item.Links=item.Directory?1u:completeNames?(uint)visible:null;
                    if(item.Links.HasValue)Require(item.Links.Value>=item.Aliases,"SourceConsistencyFailed");
                    item.NeedsNative=record.BaseReference!=0||!item.Logical.HasValue||!item.Allocated.HasValue||!item.Links.HasValue;
                    if(!item.NeedsNative)
                    {
                        Require(item.Logical>=0&&item.Allocated>=0,"SnapshotSizeNegative");
                        item.Final=new(item.Identity,item.Directory,item.Directory?0:item.Logical.GetValueOrDefault(),item.Directory?0:item.Allocated.GetValueOrDefault(),item.Links.GetValueOrDefault(),false);
                    }
                }
                else if(result.Status==NtfsDecodeStatus.Unsupported)item.NeedsNative=true;
                else throw new NtfsSnapshotSizeException("SnapshotSizeDecode");
            }
            parseTicks+=Stopwatch.GetTimestamp()-before;
        }
        Require(items.Values.All(item=>item.RawSeen),"SnapshotSizeCoverage");
        var pending=items.Values.Where(item=>item.NeedsNative).ToArray();Require(pending.Length<=MaximumNativeObjects,"SnapshotSizeNativeBudget");
        int calls=0,active=0,peakWorkers=0,peakQueue=0;long nativeStart=Stopwatch.GetTimestamp();
        if(pending.Length>0)
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token);
            var channel=Channel.CreateBounded<Item>(new BoundedChannelOptions(NativeQueueCapacity){SingleWriter=true,SingleReader=false,FullMode=BoundedChannelFullMode.Wait,AllowSynchronousContinuations=false});
            Exception? failure=null;
            void Fail(Exception error)
            {
                Interlocked.CompareExchange(ref failure,error,null);
                // A callback can register a throwing cancellation handler.
                // Preserve the fixed primary error and still join all workers.
                try{linked.Cancel();}catch(Exception){}
            }
            var workers=Enumerable.Range(0,Math.Min(NativeWorkers,pending.Length)).Select(_=>Task.Run(async()=>
            {
                try
                {
                    await foreach(var item in channel.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
                    {
                        linked.Token.ThrowIfCancellationRequested();int count=Interlocked.Increment(ref active);Peak(ref peakWorkers,count);
                        try
                        {
                            Interlocked.Increment(ref calls);
                            var request=new NtfsSnapshotSizeRequest(item.EntryIndex,item.Identity,item.Directory,item.Directory?null:item.Logical);
                            var observed=External(()=>lookup(request,linked.Token),linked.Token,"SnapshotSizeNativeRead");
                            Require(observed!=null&&observed.Storage!=null&&!observed.DeletePending&&observed.Directory==item.Directory,"SnapshotSizeNativeBinding");
                            var storage=observed.Storage;
                            Require(storage.Identity==item.Identity&&!storage.WritableHandleOpen&&storage.Modified.HasValue&&storage.AllocatedBytes>=0&&
                                storage.LinkCount>0&&storage.LinkCount>=item.Aliases&&(!item.Links.HasValue||storage.LinkCount==item.Links.Value),"SnapshotSizeNativeBinding");
                            long logical=item.Directory?0:storage.LogicalBytes;
                            Require(item.Directory?storage.LogicalBytes==-1&&storage.AllocatedBytes==0:storage.LogicalBytes>=0,"SnapshotSizeNativeSize");
                            Require(!item.Logical.HasValue||logical==item.Logical.Value,"SnapshotSizeNativeLogical");
                            Require(!item.Allocated.HasValue||storage.AllocatedBytes==item.Allocated.Value,"SnapshotSizeNativeAllocation");
                            linked.Token.ThrowIfCancellationRequested();item.Final=new(item.Identity,item.Directory,logical,storage.AllocatedBytes,storage.LinkCount,true);
                        }
                        finally{Interlocked.Decrement(ref active);}
                    }
                }
                catch(OperationCanceledException){if(token.IsCancellationRequested)Fail(new OperationCanceledException(token));}
                catch(Exception error){Fail(error is NtfsSnapshotSizeException?error:new NtfsSnapshotSizeException("SnapshotSizeNativeRead"));}
            })).ToArray();
            try
            {
                foreach(var item in pending){await channel.Writer.WriteAsync(item,linked.Token).ConfigureAwait(false);Peak(ref peakQueue,channel.Reader.Count);}
            }
            catch(OperationCanceledException){if(token.IsCancellationRequested)Fail(new OperationCanceledException(token));}
            catch(Exception){Fail(new NtfsSnapshotSizeException("SnapshotSizeNativeQueue"));}
            finally{channel.Writer.TryComplete();}
            await Task.WhenAll(workers).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if(failure!=null)throw failure;
            Require(calls==pending.Length,"SnapshotSizeNativeCoverage");
        }
        long nativeTicks=Stopwatch.GetTimestamp()-nativeStart;
        long logicalBytes=0,allocatedBytes=0;int fileObjects=0,dirObjects=0;
        var values=new Dictionary<FileIdentity,NtfsSnapshotObjectSize>(items.Count);
        foreach(var item in items.Values)
        {
            token.ThrowIfCancellationRequested();Require(item.Final.Identity==item.Identity&&item.Final.Links>0,"SnapshotSizeUnknown");
            values.Add(item.Identity,item.Final);
            if(item.Directory)dirObjects++;
            else
            {
                fileObjects++;allocatedBytes=Add(allocatedBytes,item.Final.AllocatedBytes);
                Require(item.Final.LogicalBytes==0||item.Aliases<=long.MaxValue/item.Final.LogicalBytes,"SnapshotSizeOverflow");
                logicalBytes=Add(logicalBytes,item.Final.LogicalBytes*item.Aliases);
            }
        }
        token.ThrowIfCancellationRequested();long indexTicks=Math.Max(0,Stopwatch.GetTimestamp()-totalStart-readTicks-parseTicks-nativeTicks);
        var report=new NtfsSnapshotSizeIndexReport("snapshot-size-index-v1",items.Count,fileObjects,dirObjects-1,files,items.Count-pending.Length,pending.Length,
            calls,peakWorkers,peakQueue,decoded,pages,0,logicalBytes,allocatedBytes,Milliseconds(readTicks),Milliseconds(parseTicks),Milliseconds(indexTicks),Milliseconds(nativeTicks),true);
        var ownedScope=new NtfsSnapshotNamespaceResult(scope.Report,Array.AsReadOnly(ownedEntries),Array.AsReadOnly(ownedPaths),root);
        return new(ownedScope,new ReadOnlyDictionary<FileIdentity,NtfsSnapshotObjectSize>(values),report);
    }

    // Pure UI-tree projection. The display root is never opened, and a scalar
    // size table never creates a native snapshot or product source proof.
    internal static DiskEntry Materialize(NtfsSnapshotSizeIndexResult index,string rootPath,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();Require(index!=null&&rootPath!=null&&index.Report.Complete&&index.Report.UnknownObjects==0,"SnapshotSizeProjectionInput");
        var entries=index.Namespace.Entries;int count=entries.Count;
        Require(count is >=1 and <=MaximumEntries&&index.Report.ScopeObjects==index.Objects.Count,"SnapshotSizeProjectionInput");
        var nodes=new DiskEntry[count];
        for(int i=0;i<count;i++)
        {
            token.ThrowIfCancellationRequested();var item=entries[i];NtfsSnapshotObjectSize size=default;
            Require(item!=null&&item.Index==i&&index.Objects.TryGetValue(item.Identity,out size)&&size.Identity==item.Identity&&
                size.Directory==item.Directory&&size.LogicalBytes>=0&&size.AllocatedBytes>=0&&size.Links>0,"SnapshotSizeProjectionBinding");
            DiskEntry node;
            if(i==0){Require(item.ParentIndex==-1&&item.Identity==index.Namespace.RootIdentity&&item.Directory,"SnapshotSizeProjectionBinding");node=new(rootPath,true);}
            else
            {
                Require(item.ParentIndex>=0&&item.ParentIndex<i&&nodes[item.ParentIndex].IsDirectory,"SnapshotSizeProjectionBinding");
                var parent=nodes[item.ParentIndex];node=DiskEntry.FromName(item.Name,item.Directory,parent,item.Directory?"文件夹":DiskEntry.ExtensionOf(item.Name));parent.MutableChildren.Add(node);
            }
            node.Storage=new(size.Identity,size.AllocatedBytes,size.Links,false,item.Directory?-1:size.LogicalBytes);
            if(!item.Directory){node.Size=size.LogicalBytes;node.FileCount=1;}nodes[i]=node;
        }
        for(int i=nodes.Length-1;i>0;i--)
        {
            token.ThrowIfCancellationRequested();var node=nodes[i];var parent=node.Parent!;
            parent.Size=Add(parent.Size,node.Size);parent.FileCount=Add(parent.FileCount,node.FileCount);
            parent.FolderCount=Add(parent.FolderCount,Add(node.FolderCount,node.IsDirectory?1:0));
        }
        StorageMetrics.Calculate(nodes[0],token);token.ThrowIfCancellationRequested();
        Require(nodes[0].Size==index.Report.LogicalBytes&&nodes[0].AllocatedSize==index.Report.AllocatedBytes&&nodes[0].UnknownAllocationFiles==0&&
            nodes[0].FileCount==index.Report.FileEntries&&nodes[0].FolderCount==index.Report.DirectoryObjects,"SnapshotSizeProjectionTotals");
        return nodes[0];
    }
}
