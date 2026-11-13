using System.Collections;
using System.Collections.Frozen;
using System.Text.Json;

namespace DiskAtlas.NtfsPrototype;

// Pure inventories and private product trees only; no target is opened.
internal static class NtfsReparsePolicyTests
{
    private static FileIdentity Id(ulong slot)=>new(61,NtfsRecordFixtures.Reference(slot,3),0);
    private static NtfsSnapshotNamespaceEntry Entry(int index,int parent,string name,ulong slot,bool directory=false,bool reparse=false)=>
        new(index,parent,name,Id(slot),(directory?FileAttributes.Directory:FileAttributes.Archive)|(reparse?FileAttributes.ReparsePoint:0),reparse);
    private static NtfsSnapshotNamespaceResult Scope(NtfsScanLimits limits,params NtfsSnapshotNamespaceEntry[] descendants)
    {
        var entries=new[]{Entry(0,-1,"",1,true)}.Concat(descendants).ToArray();
        int dirs=entries.Skip(1).Count(e=>e.Directory),files=entries.Count(e=>!e.Directory),opened=entries.Count(e=>e.Directory&&!e.Reparse);
        int pages=descendants.Length; // One row per synthetic successful page.
        var report=new NtfsSnapshotNamespaceReport("snapshot-namespace-v1",entries.Length,files,dirs,
            entries.Where(e=>!e.Directory).Select(e=>e.Identity).Distinct().Count(),entries.Count(e=>e.Reparse),opened,pages,pages+opened,
            entries.Sum(e=>e.Name.Length),opened,true);
        var paths=new string?[entries.Length];paths[0]="";
        for(int i=1;i<entries.Length;i++)
        {
            var e=entries[i];string prefix=e.ParentIndex>=0&&e.ParentIndex<i?paths[e.ParentIndex]??"":"";
            paths[i]=!limits.CompactPaths||e.Directory?(prefix.Length==0?e.Name:prefix+"/"+e.Name):null;
        }
        return new(report,entries,paths,Id(1),limits);
    }
    private static NtfsSnapshotNamespaceResult Mixed(NtfsScanLimits limits)=>Scope(limits,
        Entry(1,0,"a",2,true),Entry(2,0,"junction",3,true,true),Entry(3,0,"ordinary.bin",4),
        Entry(4,1,"nested",5,true),Entry(5,4,"file-link",6,false,true),Entry(6,1,"alias.bin",4),
        Entry(7,4,"child.bin",7),Entry(8,0,"unaffected",8,true),Entry(9,8,"other.bin",9));
    private static NtfsSnapshotNamespaceResult WithReport(NtfsSnapshotNamespaceResult input,NtfsSnapshotNamespaceReport report)=>
        new(report,input.Entries,Enumerable.Range(0,input.Entries.Count).Select(input.RelativePath).ToArray(),input.RootIdentity,input.Limits);
    private static DiskEntry Project(NtfsSnapshotNamespaceResult input)
    {
        var nodes=new DiskEntry[input.Entries.Count];
        for(int i=0;i<nodes.Length;i++)
        {
            var e=input.Entries[i];
            if(i==0)nodes[i]=new("S:\\Synthetic",true);
            else
            {
                var parent=nodes[e.ParentIndex];nodes[i]=DiskEntry.FromName(e.Name,e.Directory,parent,e.Directory?"文件夹":DiskEntry.ExtensionOf(e.Name));
                parent.MutableChildren.Add(nodes[i]);
            }
            nodes[i].Storage=new(e.Identity,0,1,false,e.Directory?-1:0,new DateTime(2020,1,1));
        }
        return nodes[0];
    }
    private sealed class BadEntries(int count,bool badCount=false):IReadOnlyList<NtfsSnapshotNamespaceEntry>
    {
        public int Count=>badCount?throw new NtfsReparsePolicyException("SyntheticPrivateCount"):count;
        public NtfsSnapshotNamespaceEntry this[int index]=>throw new NtfsReparsePolicyException("SyntheticPrivateEntry");
        public IEnumerator<NtfsSnapshotNamespaceEntry> GetEnumerator()=>throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private sealed class ForeignSet(bool repeats):IReadOnlySet<FileIdentity>
    {
        public int Count=>1;
        private static IEnumerable<FileIdentity> Repeat(){while(true)yield return Id(1);}
        public IEnumerator<FileIdentity> GetEnumerator()=>repeats?Repeat().GetEnumerator():throw new NtfsReparsePolicyException("SyntheticPrivateSet");
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
        public bool Contains(FileIdentity item)=>throw new NotSupportedException();
        public bool IsProperSubsetOf(IEnumerable<FileIdentity> other)=>throw new NotSupportedException();
        public bool IsProperSupersetOf(IEnumerable<FileIdentity> other)=>throw new NotSupportedException();
        public bool IsSubsetOf(IEnumerable<FileIdentity> other)=>throw new NotSupportedException();
        public bool IsSupersetOf(IEnumerable<FileIdentity> other)=>throw new NotSupportedException();
        public bool Overlaps(IEnumerable<FileIdentity> other)=>throw new NotSupportedException();
        public bool SetEquals(IEnumerable<FileIdentity> other)=>throw new NotSupportedException();
    }
    internal static int Run()
    {
        int passed=0;
        void Check(bool value,string name){if(!value)throw new InvalidOperationException("Reparse policy check failed: "+name);passed++;Console.WriteLine("PASS NTFS reparse policy "+name);}
        Exception? Catch(Action action){try{action();return null;}catch(Exception error){return error;}}
        void Reject(NtfsSnapshotNamespaceResult input,string name)
        {
            NtfsReparseScope? partial=null;var error=Catch(()=>partial=NtfsReparsePolicy.Filter(input));
            Check(error is NtfsReparsePolicyException failure&&failure.Message==failure.Code&&failure.InnerException==null&&partial==null,name);
        }
        Check(NtfsReparsePolicy.Reason=="跳过符号链接、联接点或其他重解析点；链接及目标未纳入统计"&&NtfsReparsePolicy.MaximumDetails==2000,
            "boundary reason and bounded detail contract are fixed");
        foreach(var limits in new[]{NtfsScanLimits.Standard,NtfsScanLimits.Large})
        {
            var original=Mixed(limits);var result=NtfsReparsePolicy.Filter(original);var included=result.Included;
            Check(result.ExcludedCount==2&&included.Report is{Entries:8,FileEntries:4,Directories:3,UniqueFileObjects:3,ReparseEntries:0,OpenedDirectories:4,CompletedDirectories:4}&&
                included.Report.Pages==original.Report.Pages&&included.Report.Queries==original.Report.Queries&&ReferenceEquals(included.Limits,limits),
                "file and directory boundaries are omitted while real cursor counts and selected profile remain intact");
            Check(included.Entries.All(e=>!e.Reparse&&(e.Attributes&FileAttributes.ReparsePoint)==0)&&
                included.Entries.Select(e=>e.Index).SequenceEqual(Enumerable.Range(0,8))&&included.Entries[3].ParentIndex==1&&
                included.Entries[5].ParentIndex==3&&included.RelativePath(5)=="a/nested/child.bin"&&included.RelativePath(7)=="unaffected/other.bin",
                "indices parents and paths are remapped without importing any target inventory");
            Check(included.Report.NameCharacters==included.Entries.Sum(e=>e.Name.Length)&&included.Entries[2].Identity==included.Entries[4].Identity&&
                result.Details.SequenceEqual(new[]{new NtfsReparseBoundary(Id(1),"junction",true),new NtfsReparseBoundary(Id(5),"file-link",false)}),
                "ordinary hard link aliases survive and details contain only parent identity leaf name and kind");
            Check(result.IncompleteDirectories.SetEquals(new[]{Id(1),Id(2),Id(5)})&&!result.IncompleteDirectories.Contains(Id(8)),
                "only parents and their actual ancestors are incomplete below the detail cap");
            Check(!limits.CompactPaths||Enumerable.Range(0,included.Entries.Count).All(i=>included.HasCachedPath(i)==included.Entries[i].Directory),
                "large filtered inventories cache directory prefixes without file path copies");
            var tree=Project(included);NtfsReparsePolicy.ApplyIncomplete(tree,result.IncompleteDirectories);
            Check(tree.Incomplete&&tree.Descendants().Where(e=>e.IsDirectory).All(e=>e.Incomplete==result.IncompleteDirectories.Contains(e.Storage!.Identity))&&
                tree.Descendants().Where(e=>!e.IsDirectory).All(e=>!e.Incomplete),"incompleteness applies only to included directory identities");
            var invalidTree=Project(included);var missing=result.IncompleteDirectories.Append(Id(999)).ToFrozenSet();
            Check(Catch(()=>NtfsReparsePolicy.ApplyIncomplete(invalidTree,missing)) is NtfsReparsePolicyException&&
                !invalidTree.Incomplete&&invalidTree.Descendants().All(e=>!e.Incomplete),"unknown affected identity is rejected before any directory flag changes");
            var ordinary=Scope(limits,Entry(1,0,"folder",2,true),Entry(2,1,"f.bin",3));
            var unchanged=NtfsReparsePolicy.Filter(ordinary);
            Check(ReferenceEquals(unchanged.Included,ordinary)&&unchanged.ExcludedCount==0&&unchanged.Details.Count==0&&unchanged.IncompleteDirectories.Count==0,
                "ordinary inventories retain the original instance without copying their entry graph");
        }
        var allLinks=Scope(NtfsScanLimits.Large,Entry(1,0,"f-link",2,false,true),Entry(2,0,"d-link",3,true,true));
        var all=NtfsReparsePolicy.Filter(allLinks);
        Check(all.ExcludedCount==2&&all.Included.Report is{Entries:1,FileEntries:0,Directories:0,UniqueFileObjects:0,OpenedDirectories:1,CompletedDirectories:1}&&
            all.IncompleteDirectories.SetEquals(new[]{Id(1)}),"all-link directory yields an explicitly incomplete empty included root");

        var many=new List<NtfsSnapshotNamespaceEntry>{Entry(1,0,"a",2,true),Entry(2,0,"b",3,true)};
        for(int i=0;i<2001;i++)many.Add(Entry(i+3,1,"link"+i,(ulong)i+4,false,true));
        var truncated=NtfsReparsePolicy.Filter(Scope(NtfsScanLimits.Large,many.ToArray()));
        Check(truncated.ExcludedCount==2001&&truncated.Details.Count==2000&&truncated.IncompleteDirectories.SetEquals(new[]{Id(1),Id(2),Id(3)})&&
            truncated.Included.Report.Entries==3,"detail truncation preserves total exclusions and conservatively marks every included directory");
        many.RemoveAt(many.Count-1);var exact=NtfsReparsePolicy.Filter(Scope(NtfsScanLimits.Large,many.ToArray()));
        Check(exact.ExcludedCount==2000&&exact.Details.Count==2000&&exact.IncompleteDirectories.SetEquals(new[]{Id(1),Id(2)}),
            "exact detail boundary retains precise ancestors rather than truncation policy");

        var mixed=Mixed(NtfsScanLimits.Standard);
        foreach(var report in new[]{mixed.Report with{ReparseEntries=0},mixed.Report with{ReparseEntries=3},mixed.Report with{NameCharacters=mixed.Report.NameCharacters+1},
            mixed.Report with{OpenedDirectories=5,CompletedDirectories=5,Queries=mixed.Report.Queries+1},mixed.Report with{Queries=mixed.Report.Queries-1},mixed.Report with{CompleteWithinBoundary=false}})
            Reject(WithReport(mixed,report),"contradictory counts or incomplete cursor coverage cannot be turned into partial success");
        var descendants=mixed.Entries.Skip(1).ToArray();descendants[2]=descendants[2] with{ParentIndex=2};
        Reject(Scope(NtfsScanLimits.Standard,descendants),"a listed child below a reparse directory is rejected rather than silently removed");
        descendants=mixed.Entries.Skip(1).ToArray();descendants[1]=descendants[1] with{Reparse=false};
        Reject(Scope(NtfsScanLimits.Standard,descendants),"boolean and attribute reparse declarations must agree");
        descendants=mixed.Entries.Skip(1).ToArray();descendants[3]=descendants[3] with{Identity=Id(2)};
        Reject(Scope(NtfsScanLimits.Standard,descendants),"ordinary directory aliases cannot manufacture an ancestor graph");
        foreach(bool badCount in new[]{false,true})
        {
            var hostile=new NtfsSnapshotNamespaceResult(mixed.Report,new BadEntries(mixed.Entries.Count,badCount),Array.Empty<string?>(),Id(1));
            NtfsReparseScope? partial=null;var error=Catch(()=>partial=NtfsReparsePolicy.Filter(hostile));
            Check(error is NtfsReparsePolicyException{Code:"ReparsePolicyInput",Message:"ReparsePolicyInput",InnerException:null}&&partial==null,
                "foreign accessor exceptions cannot expose private messages or publish an inventory prefix");
        }
        var oversized=new NtfsSnapshotNamespaceResult(mixed.Report,new BadEntries(NtfsScanLimits.Standard.MaximumEntries+1),Array.Empty<string?>(),Id(1));
        Check(Catch(()=>NtfsReparsePolicy.Filter(oversized)) is NtfsReparsePolicyException{Code:"ReparsePolicyBudget"},
            "selected profile entry cap is checked before fetching untrusted entries");
        foreach(bool repeats in new[]{false,true})
        {
            var tree=Project(NtfsReparsePolicy.Filter(mixed).Included);
            var error=Catch(()=>NtfsReparsePolicy.ApplyIncomplete(tree,new ForeignSet(repeats)));
            Check(error is NtfsReparsePolicyException failure&&failure.Code==(repeats?"ReparsePolicyProjection":"ReparsePolicyInput")&&
                failure.Message==failure.Code&&failure.InnerException==null&&!tree.Incomplete&&tree.Descendants().All(e=>!e.Incomplete),
                "foreign identity sets are sanitized and iteration is bounded by their declared count before mutation");
        }
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();NtfsReparseScope? partial=null;var tree=Project(NtfsReparsePolicy.Filter(mixed).Included);
            Check(Catch(()=>partial=NtfsReparsePolicy.Filter(mixed,canceled.Token)) is OperationCanceledException&&partial==null&&
                Catch(()=>NtfsReparsePolicy.ApplyIncomplete(tree,new[]{Id(1)}.ToFrozenSet(),canceled.Token)) is OperationCanceledException&&!tree.Incomplete,
                "pre-cancelled filtering and flag application produce no partial published result");
        }
        Check(JsonSerializer.Serialize(truncated.Included)=="{}"&&!JsonSerializer.Serialize(truncated.Included.Report).Contains("link",StringComparison.OrdinalIgnoreCase),
            "safe inventory reports do not serialize local names paths or boundary details");
        return passed;
    }
}
