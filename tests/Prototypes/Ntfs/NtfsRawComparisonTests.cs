using DiskAtlas;

namespace DiskAtlas.NtfsPrototype;

// Pure metadata and reader-callback checks; never touches a filesystem or volume.
internal static class NtfsRawComparisonTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS raw comparison check failed: " + name);
            passed++; Console.WriteLine("PASS NTFS raw comparison " + name);
        }
        const ulong volume = 41;
        ulong parent = NtfsRecordFixtures.Reference(7, 2), reference = NtfsRecordFixtures.Reference(52, 3);
        var ordinary = NtfsKernelMatrix.Cases.Single(x => x.CaseId == "A-Ordinary-64");
        var linked = NtfsKernelMatrix.Cases.Single(x => x.CaseId == "B-Ordinary-64");
        var named = NtfsKernelMatrix.Cases.Single(x => x.CaseId == "C-Ordinary-64");
        string name = NtfsKernelMatrix.FileName(64);
        NtfsName primary = new(parent, name, 1), alias = new(parent, "link.bin", 3);
        NtfsName dos = new(parent, "FXXXXX~1.BIN", 2), aliasDos = new(parent, "LINK~1.BIN", 2);
        NtfsRecord Record(params NtfsName[] names) => new(reference, 0, false, (ushort)names.Length,
            names, 131072, 131072, NtfsLookupReason.None);
        bool Matches(NtfsRecord record, NtfsKernelMatrixCase descriptor, uint links) =>
            NtfsRawComparison.MatchesLinks(record, links) && NtfsRawComparison.MatchesNames(record, parent, descriptor);

        Check(Matches(Record(primary), ordinary, 1), "ordinary single visible name matches");
        Check(Matches(Record(primary, dos), ordinary, 1), "DOS alias does not count as another native link");
        Check(Matches(Record(primary with { Namespace = 0 }), ordinary, 1), "valid POSIX name namespace remains visible");
        Check(Matches(Record(primary with { Namespace = 3 }), ordinary, 1), "combined namespace name remains one visible name");
        Check(Matches(Record(primary, alias), linked, 2), "two distinct visible hard links match");
        Check(Matches(Record(primary, dos, alias, aliasDos), linked, 2), "hard links with distinct DOS aliases preserve native link count");
        Check(Matches(Record(aliasDos, alias, dos, primary), linked, 2), "name order has no semantic effect");
        Check(NtfsRawComparison.MatchesLinks(Record(primary, primary), 2) == false,
            "duplicate visible raw entries are rejected even when raw and native counts agree");
        Check(NtfsRawComparison.MatchesLinks(Record(primary, primary), 1) == false,
            "duplicate visible entries cannot be hidden by distinct-count folding");
        Check(!Matches(Record(primary, primary), ordinary, 1), "duplicate visible names cannot claim a complete ordinary match");
        Check(!Matches(Record(primary, primary), linked, 2), "two copies of the primary cannot replace the required alias");
        Check(!NtfsRawComparison.MatchesLinks(Record(primary, primary with { Namespace = 3 }), 2),
            "same parent and name in different visible namespaces is still duplicate identity");
        Check(!NtfsRawComparison.MatchesLinks(Record(primary, dos, dos), 1), "duplicate DOS records are rejected independently of visible count");
        Check(!Matches(Record(primary, dos, dos), ordinary, 1), "duplicate DOS metadata cannot pass the combined comparison");
        Check(!NtfsRawComparison.MatchesLinks(Record(primary) with { LinkCount = 2 }, 1), "raw header link count must match raw name count");
        Check(!NtfsRawComparison.MatchesLinks(Record(primary, alias), 1), "native visible link count cannot lose an alias");
        Check(!NtfsRawComparison.MatchesLinks(Record(primary), 2), "native visible link count cannot invent an alias");
        Check(!NtfsRawComparison.MatchesNames(Record(primary with { ParentReference = parent + 1 }), parent, ordinary),
            "wrong visible parent reference is rejected");
        Check(!NtfsRawComparison.MatchesNames(Record(primary, dos with { ParentReference = parent + 1 }), parent, ordinary),
            "wrong DOS parent reference is rejected");
        Check(!Matches(Record(primary, alias with { ParentReference = parent + 1 }), linked, 2),
            "hard-link alias in another parent cannot match the generated case");
        Check(!NtfsRawComparison.MatchesNames(Record(), parent, ordinary), "missing primary name is rejected");
        Check(!NtfsRawComparison.MatchesNames(Record(primary), parent, linked), "missing hard-link alias is rejected");
        Check(!NtfsRawComparison.MatchesNames(Record(alias), parent, linked), "alias cannot replace the primary name");
        Check(!NtfsRawComparison.MatchesNames(Record(primary, alias), parent, ordinary), "extra visible name is rejected for ordinary case");
        Check(!NtfsRawComparison.MatchesNames(Record(primary, alias, new(parent, "other.bin", 1)), parent, linked),
            "extra third visible name is rejected for hard-link case");
        Check(!NtfsRawComparison.MatchesNames(Record(primary, alias with { Name = "other.bin" }), parent, linked),
            "different alias spelling cannot be accepted by matching counts");
        Check(!NtfsRawComparison.MatchesNames(Record(primary with { Name = "other.bin" }, alias), parent, linked),
            "swapped-away primary spelling cannot be replaced by the alias");
        Check(!NtfsRawComparison.MatchesNames(Record(primary with { Name = name.ToUpperInvariant() }), parent, ordinary),
            "generated spelling comparison remains ordinal");
        Check(Matches(Record(primary, dos), named, 1), "named-stream descriptor keeps the same default-file name contract");

        var compressed = NtfsKernelMatrix.Cases.Single(x => x.CaseId == "A-Compressed-64");
        var compressedNamed = NtfsKernelMatrix.Cases.Single(x => x.CaseId == "C-Compressed-64");
        var sparse = NtfsKernelMatrix.Cases.Single(x => x.CaseId == "A-Sparse-64");
        var sparseNamed = NtfsKernelMatrix.Cases.Single(x => x.CaseId == "C-Sparse-64");
        var reasonCases = new (NtfsKernelMatrixCase Descriptor, NtfsLookupReason Expected)[]
        {
            (ordinary, NtfsLookupReason.None), (named, NtfsLookupReason.NamedData),
            (compressed, NtfsLookupReason.Compressed), (compressedNamed, NtfsLookupReason.Compressed | NtfsLookupReason.NamedData),
            (sparse, NtfsLookupReason.Sparse), (sparseNamed, NtfsLookupReason.Sparse | NtfsLookupReason.NamedData)
        };
        foreach (var item in reasonCases)
        {
            Check(NtfsRawComparison.MatchesLookupReasons(Record(primary) with { LookupReasons = item.Expected }, item.Descriptor),
                "exact ordinary or single-special fixture reasons match with the declared stream scope");
            foreach (var reason in Enum.GetValues<NtfsLookupReason>().Where(value => value != NtfsLookupReason.None))
            {
                NtfsLookupReason changed = item.Expected ^ reason;
                Check(!NtfsRawComparison.MatchesLookupReasons(Record(primary) with { LookupReasons = changed }, item.Descriptor),
                    "missing or unexpected lookup reason cannot become a complete fixture match");
            }
            Check(!NtfsRawComparison.MatchesLookupReasons(Record(primary) with { LookupReasons = item.Expected | (NtfsLookupReason)(1 << 20) }, item.Descriptor),
                "unknown lookup reason bit is rejected");
        }
        Check(!NtfsRawComparison.MatchesLookupReasons(Record(primary) with
            { LookupReasons = NtfsLookupReason.Compressed | NtfsLookupReason.Sparse }, compressed),
            "compressed fixture cannot silently accept sparse metadata too");
        Check(!NtfsRawComparison.MatchesLookupReasons(Record(primary) with
            { LookupReasons = NtfsLookupReason.Compressed | NtfsLookupReason.Sparse }, sparse),
            "sparse fixture cannot silently accept compressed metadata too");
        Check(!NtfsRawComparison.MatchesLookupReasons(Record(primary), ordinary with { Kind = "Unknown" }),
            "unknown fixture kind never defaults to ordinary reasons");

        var storage = new FileStorageInfo(new(volume, reference, 0), 131072, 2, false, 131072, new DateTime(2020, 1, 1));
        const string syntheticParent = @"C:\Synthetic\B-Ordinary-64";
        var target = new NtfsRawTarget(linked.CaseId, Path.Combine(syntheticParent, name), syntheticParent,
            storage, new(new(volume, parent, 0), 0, 1, false), FileAttributes.Archive);
        int calls = 0;
        string? requested = null;
        FileStorageInfo? ReadGood(string path) { calls++; requested = path; return storage; }
        Check(NtfsRawComparison.MatchesPostAlias(target, linked, ReadGood), "post-unlock alias matches the complete stored native snapshot");
        Check(calls == 1 && requested == Path.Combine(syntheticParent, "link.bin"),
            "alias callback is limited to the expected basename in its generated parent");
        Check(!NtfsRawComparison.MatchesPostAlias(target, linked, _ => null), "missing post-unlock alias fails");
        foreach (var changed in new[]
        {
            storage with { Identity = new(volume + 1, reference, 0) },
            storage with { Identity = new(volume, reference + 1, 0) },
            storage with { Identity = new(volume, NtfsRecordFixtures.Reference(52, 4), 0) },
            storage with { Identity = new(volume, reference, 1) },
            storage with { LogicalBytes = storage.LogicalBytes + 1 },
            storage with { AllocatedBytes = storage.AllocatedBytes + 4096 },
            storage with { LinkCount = 1 },
            storage with { WritableHandleOpen = true },
            storage with { Modified = storage.Modified!.Value.AddTicks(1) },
            storage with { Modified = null }
        })
            Check(!NtfsRawComparison.MatchesPostAlias(target, linked, _ => changed),
                "changed alias identity or native metadata cannot pass by identity alone");
        foreach (var descriptor in new[] { ordinary, named })
        {
            calls = 0;
            Check(NtfsRawComparison.MatchesPostAlias(target, descriptor, _ => { calls++; return null; }) && calls == 0,
                "non-hard-link case performs no alias lookup");
        }
        bool propagated = false;
        try { NtfsRawComparison.MatchesPostAlias(target, linked, _ => throw new IOException("SyntheticAliasRead")); }
        catch (IOException) { propagated = true; }
        Check(propagated, "alias infrastructure failure propagates rather than producing successful verification");

        var facts = new NtfsRawVolumeFacts(new(512, 4096, 1024, 1048576, 256, 10, 4096), 41, 512);
        using var cancel = new CancellationTokenSource();
        calls = 0;
        cancel.Cancel();
        bool canceled = false;
        try { NtfsRawComparison.ReadFinalFacts(() => { calls++; return facts; }, cancel.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && calls == 0, "pre-canceled final query performs no native callback");
        using var duringRead = new CancellationTokenSource();
        canceled = false;
        try { NtfsRawComparison.ReadFinalFacts(() => { duringRead.Cancel(); return facts; }, duringRead.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "cancellation during final metadata callback cannot publish verification");
        Check(NtfsRawComparison.ReadFinalFacts(() => facts, default) == facts,
            "stable final query preserves the full geometry and volume facts");
        propagated = false;
        try { NtfsRawComparison.ReadFinalFacts(() => throw new IOException("SyntheticFactsRead"), default); }
        catch (IOException) { propagated = true; }
        Check(propagated, "final query failure propagates instead of publishing old facts");
        return passed;
    }
}
