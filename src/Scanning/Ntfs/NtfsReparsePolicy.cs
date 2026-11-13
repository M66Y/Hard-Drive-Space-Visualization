using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace DiskAtlas.NtfsPrototype;

internal sealed record NtfsReparseBoundary(FileIdentity ParentIdentity,string Name,bool Directory);
internal sealed record NtfsReparseScope(NtfsSnapshotNamespaceResult Included,long ExcludedCount,
    IReadOnlyList<NtfsReparseBoundary> Details,IReadOnlySet<FileIdentity> IncompleteDirectories);
internal sealed class NtfsReparsePolicyException(string code):IOException(code)
{ internal string Code{get;}=code; }

// Namespace policy only. A listed reparse entry is a boundary, never a target
// to open. Filtering cannot prove a snapshot or manufacture complete coverage.
internal static class NtfsReparsePolicy
{
    internal const int MaximumDetails=2000;
    internal const string Reason="跳过符号链接、联接点或其他重解析点；链接及目标未纳入统计";
    private static readonly FrozenSet<FileIdentity> EmptyIds=Array.Empty<FileIdentity>().ToFrozenSet();
    private static void Require([DoesNotReturnIf(false)]bool condition,string code)
    { if(!condition)throw new NtfsReparsePolicyException(code); }
    private static T External<T>(Func<T> access,CancellationToken token)
    {
        T value;
        try{value=access();}catch(Exception){token.ThrowIfCancellationRequested();throw new NtfsReparsePolicyException("ReparsePolicyInput");}
        token.ThrowIfCancellationRequested();return value;
    }
    private static bool ValidName(string name)=>name.Length is >=1 and <=255&&name is not ("." or "..")&&
        !name.Any(c=>char.IsControl(c)||c is '/' or '\\' or ':');

    internal static NtfsReparseScope Filter(NtfsSnapshotNamespaceResult scope,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();Require(scope!=null,"ReparsePolicyInput");
        var limits=scope.Limits;var report=scope.Report;
        int count=External(()=>scope.Entries.Count,token);
        Require(count>=1&&count<=limits.MaximumEntries,"ReparsePolicyBudget");
        Require(report is{Revision:"snapshot-namespace-v1",CompleteWithinBoundary:true}&&report.Entries==count&&report.ReparseEntries>=0,
            "ReparsePolicyNamespace");
        // Ordinary scans preserve their original immutable inventory. Only a
        // reported boundary needs a private copy for index remapping.
        NtfsSnapshotNamespaceEntry[]? owned=report.ReparseEntries>0?new NtfsSnapshotNamespaceEntry[count]:null;
        if(owned!=null)for(int i=0;i<count;i++){token.ThrowIfCancellationRequested();owned[i]=External(()=>scope.Entries[i],token);}
        IReadOnlyList<NtfsSnapshotNamespaceEntry> entries=owned is null?scope.Entries:owned;
        var root=scope.RootIdentity;
        Require(root.High==0&&root.Low>>48!=0,"ReparsePolicyIdentity");
        var kinds=new Dictionary<FileIdentity,(bool Directory,bool Reparse)>();
        var slots=new Dictionary<ulong,FileIdentity>();
        var lengths=new int[count];var depths=limits.CompactPaths?new int[count]:null;
        long characters=0,pathCharacters=0;int files=0,directories=0,reparse=0,reparseDirectories=0,uniqueFiles=0;
        for(int i=0;i<count;i++)
        {
            token.ThrowIfCancellationRequested();var entry=External(()=>entries[i],token);
            Require(entry!=null&&entry.Index==i&&entry.Name!=null,"ReparsePolicyNamespace");
            Require(entry.Identity.Volume==root.Volume&&entry.Identity.High==0&&entry.Identity.Low>>48!=0,"ReparsePolicyIdentity");
            Require(entry.Reparse==((entry.Attributes&FileAttributes.ReparsePoint)!=0),"ReparsePolicyNamespace");
            if(i==0)Require(entry.ParentIndex==-1&&entry.Identity==root&&entry.Directory&&!entry.Reparse&&entry.Name.Length==0,"ReparsePolicyNamespace");
            else
            {
                Require(entry.ParentIndex>=0&&entry.ParentIndex<i&&ValidName(entry.Name),"ReparsePolicyNamespace");
                var parent=External(()=>entries[entry.ParentIndex],token);
                Require(parent!=null&&parent.Directory&&!parent.Reparse,"ReparsePolicyNamespace");
                long length=(long)lengths[entry.ParentIndex]+entry.Name.Length+(lengths[entry.ParentIndex]==0?0:1);
                Require(length<=int.MaxValue,"ReparsePolicyBudget");lengths[i]=(int)length;
                if(depths!=null){depths[i]=depths[entry.ParentIndex]+1;Require(depths[i]<=512&&length<=32767,"ReparsePolicyBudget");}
                characters+=entry.Name.Length;Require(characters<=limits.MaximumCharacters,"ReparsePolicyBudget");
                if(!limits.CompactPaths||entry.Directory){pathCharacters+=length;Require(pathCharacters<=limits.MaximumCharacters,"ReparsePolicyBudget");}
                if(entry.Directory)directories++;else files++;
                if(entry.Reparse){reparse++;if(entry.Directory)reparseDirectories++;}
            }
            if(kinds.TryGetValue(entry.Identity,out var previous))Require(!entry.Directory&&previous==(entry.Directory,entry.Reparse),"ReparsePolicyIdentity");
            else{kinds.Add(entry.Identity,(entry.Directory,entry.Reparse));if(!entry.Directory)uniqueFiles++;}
            ulong slot=entry.Identity.Low&NtfsRecordDecoder.MaximumRecordNumber;
            if(slots.TryGetValue(slot,out var prior))Require(prior==entry.Identity,"ReparsePolicyIdentity");else slots.Add(slot,entry.Identity);
        }
        int opened=directories-reparseDirectories+1;
        Require(report.FileEntries==files&&report.Directories==directories&&report.UniqueFileObjects==uniqueFiles&&
            report.ReparseEntries==reparse&&report.NameCharacters==characters&&report.OpenedDirectories==opened&&report.CompletedDirectories==opened&&
            report.Pages>=0&&report.Queries>=0&&report.Queries==report.Pages+(long)opened&&report.Queries<=limits.MaximumEntries*4L+1&&
            (count==1||report.Pages>0),"ReparsePolicyNamespace");
        token.ThrowIfCancellationRequested();
        if(reparse==0)return new(scope,0,Array.Empty<NtfsReparseBoundary>(),EmptyIds);

        var remap=new int[count];Array.Fill(remap,-1);
        var included=new List<NtfsSnapshotNamespaceEntry>(count-reparse);var paths=new List<string?>(count-reparse);
        var details=new List<NtfsReparseBoundary>(Math.Min(reparse,MaximumDetails));var affected=new HashSet<FileIdentity>();
        var retainedFiles=new HashSet<FileIdentity>();int retainedDirectories=0,retainedFileEntries=0;long retainedCharacters=0;
        for(int i=0;i<count;i++)
        {
            token.ThrowIfCancellationRequested();var entry=entries[i];
            if(entry.Reparse)
            {
                if(details.Count<MaximumDetails)details.Add(new(entries[entry.ParentIndex].Identity,entry.Name,entry.Directory));
                if(reparse<=MaximumDetails)
                    for(int parent=entry.ParentIndex;parent>=0;parent=entries[parent].ParentIndex)
                    {token.ThrowIfCancellationRequested();if(!affected.Add(entries[parent].Identity))break;}
                continue;
            }
            int index=included.Count,parentIndex=i==0?-1:remap[entry.ParentIndex];
            Require(i==0||parentIndex>=0,"ReparsePolicyNamespace");remap[i]=index;
            included.Add(entry with{Index=index,ParentIndex=parentIndex});
            string prefix=i==0?"":paths[parentIndex]??throw new NtfsReparsePolicyException("ReparsePolicyNamespace");
            paths.Add(!limits.CompactPaths||entry.Directory?(prefix.Length==0?entry.Name:prefix+"/"+entry.Name):null);
            retainedCharacters+=entry.Name.Length;
            if(i>0){if(entry.Directory)retainedDirectories++;else{retainedFileEntries++;retainedFiles.Add(entry.Identity);}}
            if(reparse>MaximumDetails&&entry.Directory)affected.Add(entry.Identity);
        }
        Require(opened==retainedDirectories+1,"ReparsePolicyNamespace");
        var includedReport=new NtfsSnapshotNamespaceReport("snapshot-namespace-v1",included.Count,retainedFileEntries,retainedDirectories,
            retainedFiles.Count,0,report.OpenedDirectories,report.Pages,report.Queries,checked((int)retainedCharacters),report.CompletedDirectories,true);
        var frozenAffected=affected.ToFrozenSet();token.ThrowIfCancellationRequested();
        return new(new(includedReport,included.AsReadOnly(),paths.AsReadOnly(),root,limits),reparse,details.AsReadOnly(),frozenAffected);
    }

    // Validate all requested directory identities before setting any flags.
    // The caller still owns cancellation/publication of the private result tree.
    internal static void ApplyIncomplete(DiskEntry root,IReadOnlySet<FileIdentity> incompleteDirectories,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();Require(root!=null&&root.IsDirectory&&incompleteDirectories!=null,"ReparsePolicyProjection");
        int requestedCount=External(()=>incompleteDirectories.Count,token);
        Require(requestedCount>=0&&requestedCount<=NtfsScanLimits.Large.MaximumEntries,"ReparsePolicyBudget");
        var requested=new HashSet<FileIdentity>();int observed=0;
        var enumerator=External(()=>incompleteDirectories.GetEnumerator(),token);
        Require(enumerator!=null,"ReparsePolicyInput");
        try
        {
            while(External(()=>enumerator.MoveNext(),token))
            {
                Require(++observed<=requestedCount,"ReparsePolicyProjection");
                Require(requested.Add(External(()=>enumerator.Current,token)),"ReparsePolicyProjection");
            }
        }
        finally{External(()=>{enumerator.Dispose();return true;},token);}
        Require(observed==requestedCount,"ReparsePolicyProjection");
        if(requestedCount==0)return;
        var matched=new HashSet<FileIdentity>();var directories=new HashSet<FileIdentity>();var targets=new List<DiskEntry>();
        var pending=new Stack<DiskEntry>();pending.Push(root);int visited=0;
        while(pending.TryPop(out var entry))
        {
            token.ThrowIfCancellationRequested();Require(++visited<=NtfsScanLimits.Large.MaximumEntries,"ReparsePolicyBudget");
            if(entry.IsDirectory)
            {
                var storage=entry.Storage;
                Require(storage!=null&&directories.Add(storage.Identity),"ReparsePolicyProjection");
                if(requested.Contains(storage.Identity)){matched.Add(storage.Identity);targets.Add(entry);}
                foreach(var child in entry.Children){token.ThrowIfCancellationRequested();Require(child!=null&&ReferenceEquals(child.Parent,entry),"ReparsePolicyProjection");pending.Push(child);}
            }
        }
        Require(matched.Count==requested.Count,"ReparsePolicyProjection");token.ThrowIfCancellationRequested();
        foreach(var target in targets){token.ThrowIfCancellationRequested();target.Incomplete=true;}
    }
}
