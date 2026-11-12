namespace DiskAtlas.NtfsPrototype;

// Pure bounded streams. The collected references and names remain in memory;
// these tests do not create files, open handles or establish source authority.
internal static class NtfsMftIndexCollectorTests
{
    private static NtfsMftMap Map(int slots)
    {
        int clusters = (slots + 3) / 4; long valid = slots * 1024L;
        byte[] runs = [0x12, (byte)clusters, (byte)(clusters >> 8), 10, 0];
        var geometry = new NtfsMftGeometry(512,4096,1024,256L*1024*1024,65536,10,valid);
        return NtfsMftMap.Parse(NtfsMftMapTests.Record0(runs,(ulong)(clusters-1),clusters*4096L,valid,valid),geometry).Map!;
    }
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition,string name)
        {
            if (!condition) throw new InvalidOperationException("MFT collector check failed: " + name);
            passed++; Console.WriteLine("PASS MFT collector " + name);
        }
        Exception? Catch(Action action) { try { action(); return null; } catch(Exception error) { return error; } }
        byte[] ordinary = NtfsRecordFixtures.File(NtfsRecordFixtures.Name(),NtfsRecordFixtures.Nonresident());
        byte[] free = NtfsRecordFixtures.Record([],sequence:4,inUse:false);
        byte[] invalid = new byte[1024]; byte[] unsupported = ordinary.ToArray(); NtfsRecordFixtures.U16(unsupported,22,5);
        int reads = 0; byte[]? page = null; bool reused = true;
        int Mixed(long offset,Memory<byte> destination,CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); reads++;
            System.Runtime.InteropServices.MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination,out var segment);
            if(page == null) page = segment.Array; else reused &= ReferenceEquals(page,segment.Array);
            for(int local=0;local<destination.Length;local+=1024)
            {
                ulong slot=(ulong)((offset+local)/1024);
                (slot switch { 5=>free,6=>invalid,7=>unsupported,_=>ordinary }).CopyTo(destination.Span[local..]);
            }
            return destination.Length;
        }
        var collected=NtfsMftIndexCollector.Collect(Map(70),[NtfsRecordFixtures.Reference(0,3),NtfsRecordFixtures.Reference(69,3)],Mixed);
        Check(collected.Range is { ExpectedSlots:70,VisitedSlots:70,Decoded:67,Free:1,Invalid:1,Unsupported:1,Pages:2,AllZeroInvalid:1 } &&
            collected.Records.Count==67 && collected.Slots.Count==70 && reads==2 && reused,
            "collector keeps bounded decoded records and statuses while reusing one page and preserving all classifications");
        Check(collected.Slots[5] is { Status:NtfsDecodeStatus.Free } && collected.Slots[5].HeaderReference==NtfsRecordFixtures.Reference(5,4) &&
            collected.Slots[6].HeaderReference==null && collected.Slots[7].HeaderReference==null,
            "only strictly validated decoded or free headers provide a full reference for corroboration");
        Check(collected.Range.TargetRecords.Count==2 && collected.Records.ContainsKey(NtfsRecordFixtures.Reference(69,3)) &&
            collected.Range.CodeCounts.Sum(row=>row.Count)==70,
            "retention of index records does not change the separate fixed target and reason coverage contracts");
        bool immutable=false;try{((IDictionary<ulong,NtfsRecord>)collected.Records).Clear();}catch(NotSupportedException){immutable=true;}
        Check(immutable,"collected records cannot be replaced by an outside consumer");
        int Repeat(byte[] record,long _,Memory<byte> destination,CancellationToken token)
        { token.ThrowIfCancellationRequested();for(int offset=0;offset<destination.Length;offset+=1024)record.CopyTo(destination.Span[offset..]);return destination.Length; }
        var manyNames=NtfsRecordFixtures.Record(Enumerable.Range(0,8).Select(index=>NtfsRecordFixtures.Name(((char)('a'+index)).ToString()))
            .Append(NtfsRecordFixtures.Nonresident()),links:8);
        Check(Catch(()=>NtfsMftIndexCollector.Collect(Map(4096),[],(offset,destination,token)=>Repeat(manyNames,offset,destination,token))) is
            NtfsMftEnumerationException { Code:"MftCollectionNameBudget" },"name entry ceiling rejects collection before an unbounded index can be retained");
        var manyCharacters=NtfsRecordFixtures.Record([NtfsRecordFixtures.Name(new string('a',255)),NtfsRecordFixtures.Name(new string('b',45)),
            NtfsRecordFixtures.Nonresident()],links:2);
        Check(Catch(()=>NtfsMftIndexCollector.Collect(Map(4096),[],(offset,destination,token)=>Repeat(manyCharacters,offset,destination,token))) is
            NtfsMftEnumerationException { Code:"MftCollectionNameBudget" },"name character ceiling is independently bounded below the record limit");
        int oversizedReads=0;
        Check(Catch(()=>NtfsMftIndexCollector.Collect(Map(4097),[],(_,destination,_)=>{oversizedReads++;return destination.Length;})) is
            NtfsMftEnumerationException { Code:"MftCollectionGeometry" } && oversizedReads==0,
            "an oversized range fails before the first callback rather than returning a prefix as complete");
        Check(Catch(()=>NtfsMftIndexCollector.Collect(Map(70),[NtfsRecordFixtures.Reference(5,4)],Mixed)) is
            NtfsMftEnumerationException { Code:"MftCollectionCoverage" },"a free target cannot become a decoded object because its header identity is known");
        int partialReads=0;NtfsMftIndexCollection? partial=null;
        Check(Catch(()=>partial=NtfsMftIndexCollector.Collect(Map(70),[],(offset,destination,token)=>
        {partialReads++;return partialReads==2?destination.Length-1:Repeat(ordinary,offset,destination,token);})) is
            NtfsMftEnumerationException { Code:"MftCollectionShortRead" } && partial==null && partialReads==2,
            "short final page discards all previously collected names and records");
        using var cancellation=new CancellationTokenSource();partial=null;
        Check(Catch(()=>partial=NtfsMftIndexCollector.Collect(Map(70),[],(offset,destination,token)=>
        {int bytes=Repeat(ordinary,offset,destination,token);cancellation.Cancel();return bytes;},cancellation.Token)) is OperationCanceledException && partial==null,
            "cancelled collection exposes no partial records or allocation claims");
        foreach (Exception forged in new Exception[] { new OperationCanceledException("SyntheticPrivateReader"),new NtfsMftEnumerationException("SyntheticPrivateReader") })
        {
            var error=Catch(()=>NtfsMftIndexCollector.Collect(Map(70),[],(_,_,_)=>throw forged));
            Check(error is NtfsMftEnumerationException {Code:"MftCollectionReadFailed"} && error.Message=="MftCollectionReadFailed" && error.InnerException==null,
                "reader-controlled cancellation and exception codes cannot leak through the fixed collector boundary");
        }
        return passed;
    }
}
