using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

// Private fixture context. Paths and full identities never enter the report.
internal sealed record NtfsRawTarget(string CaseId, string Path, string ParentPath,
    FileStorageInfo Storage, FileStorageInfo ParentStorage, FileAttributes Attributes);
internal sealed record NtfsRawVolumeFacts(NtfsMftGeometry Geometry, ulong Serial, int PhysicalSectorBytes);
internal sealed record NtfsRawComparisonCase(string CaseId, string Kind, int NameLength, string KernelCode,
    string RawCode, bool IdentityMatched, bool LogicalMatched, bool LinksMatched, bool NamesMatched, string AllocationCheck);
internal sealed record NtfsRawComparisonReport(string Revision, int Objects, int KernelRequests, int KernelDecoded,
    int KernelRejected, int RawRecords, int RawDecoded, int MatchedObjects, int UnknownAllocationObjects,
    int MftRuns, int ReadCalls, long RequestedBytes, int PhysicalSectorBytes, bool LockAcquired,
    bool UnlockSucceeded, bool PostNativeStable, IReadOnlyList<NtfsRawComparisonCase> Cases);

// Test-only orchestration. The caller has proved image/volume ownership and has
// closed every fixture file before entering. No production or arbitrary-volume API.
internal static class NtfsRawComparison
{
    internal static NtfsRawComparisonReport Run(SafeFileHandle volume, IReadOnlyList<NtfsRawTarget> targets,
        NtfsKernelMatrixReport kernel, NtfsRawVolumeFacts facts, Func<NtfsRawVolumeFacts> refreshFacts,
        Action<bool, string> check, CancellationToken token = default)
    {
        void Require(bool condition, string code)
        { check(condition, code); if (!condition) throw new InvalidDataException(code); }
        token.ThrowIfCancellationRequested();
        Require(targets.Count == 21 && targets.Count < 32 && kernel.Objects == targets.Count && kernel.Requests == 63,
            "RawFixedTargetBudget");
        Require(facts.Geometry is { SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024 }, "RawFixtureGeometry");
        Require(targets.Select(item => item.CaseId).Distinct(StringComparer.Ordinal).Count() == targets.Count &&
            targets.Select(item => item.Storage.Identity).Distinct().Count() == targets.Count, "RawUniqueTargets");
        var descriptors = NtfsKernelMatrix.Cases.ToDictionary(item => item.CaseId, StringComparer.Ordinal);
        var kernelFinal = kernel.Rows.Where(item => item.Stage == "Flushed")
            .ToDictionary(item => item.CaseId[..^"-Flushed".Length], StringComparer.Ordinal);
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            Require(descriptors.ContainsKey(target.CaseId) && kernelFinal.ContainsKey(target.CaseId), "RawKnownTarget");
            Require(target.Storage.Identity.Volume == facts.Serial && target.Storage.Identity.High == 0 &&
                target.Storage.Identity.Low >> 48 != 0 && target.ParentStorage.Identity.Volume == facts.Serial &&
                target.ParentStorage.Identity.High == 0 && target.ParentStorage.Identity.Low >> 48 != 0 &&
                !target.Storage.WritableHandleOpen && target.Storage.Modified.HasValue, "RawTargetNativeIdentity");
        }

        NtfsRawVolumeLease? lease = null;
        NtfsMftMap? map = null;
        int rawDecoded = 0, unknown = 0;
        var rows = new List<NtfsRawComparisonCase>(21);
        try
        {
            lease = NtfsRawVolumeLease.Acquire(volume, facts.Geometry.VolumeBytes,
                facts.Geometry.SectorBytes, facts.PhysicalSectorBytes, token);
            Require(true, "RawLockAcquired");
            long bootstrap = checked(facts.Geometry.MftStartLcn * facts.Geometry.ClusterBytes);
            byte[] raw0 = lease.Read(bootstrap, facts.Geometry.RecordBytes, token);
            var parsed = NtfsMftMap.Parse(raw0, facts.Geometry, token);
            Require(parsed.Status == "Mapped" && parsed.Map != null, "RawMftMap-" + parsed.Code);
            map = parsed.Map!;
            var bootstrapPlan = map.PlanRecord(0, token);
            Require(bootstrapPlan.Count == 1 && bootstrapPlan[0].VolumeOffset == bootstrap &&
                bootstrapPlan[0].Length == facts.Geometry.RecordBytes && bootstrapPlan[0].RecordOffset == 0,
                "RawBootstrapReverseBinding");
            rawDecoded++;
            foreach (var target in targets)
            {
                token.ThrowIfCancellationRequested();
                var descriptor = descriptors[target.CaseId];
                ulong slot = target.Storage.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber;
                byte[] raw = new byte[facts.Geometry.RecordBytes];
                int copied = 0;
                foreach (var slice in map.PlanRecord(slot, token))
                {
                    token.ThrowIfCancellationRequested();
                    Require(slice.RecordOffset == copied && slice.Length > 0 && slice.Length <= raw.Length - copied,
                        "RawSliceCoverage");
                    byte[] bytes = lease.Read(slice.VolumeOffset, slice.Length, token);
                    bytes.CopyTo(raw, slice.RecordOffset); copied += bytes.Length;
                }
                Require(copied == raw.Length, "RawCompleteRecordRead");
                var result = NtfsRecordDecoder.Decode(raw, facts.Geometry.SectorBytes, token: token,
                    recordNumber: slot, clusterSize: facts.Geometry.ClusterBytes);
                Require(result.Status == NtfsDecodeStatus.Decoded && result.Record != null, "RawStrict-" + result.Code);
                var record = result.Record!;
                bool identity = record.Reference == target.Storage.Identity.Low && record.BaseReference == 0 && !record.IsDirectory;
                bool logical = record.LogicalBytes == target.Storage.LogicalBytes;
                bool links = MatchesLinks(record, target.Storage.LinkCount);
                bool names = MatchesNames(record, target.ParentStorage.Identity.Low, descriptor);
                Require(identity && logical && links && names, "RawRecordMatchesNativeContext");
                Require(MatchesLookupReasons(record, descriptor), "RawLookupReasonsMatchFixture");
                string allocation;
                if (descriptor.Kind == "Ordinary")
                {
                    Require(record.AllocatedBytes.HasValue && record.AllocatedBytes == target.Storage.AllocatedBytes,
                        "RawDirectAllocationMatches");
                    allocation = "Matched";
                }
                else
                {
                    NtfsLookupReason expected = descriptor.Kind == "Compressed" ? NtfsLookupReason.Compressed : NtfsLookupReason.Sparse;
                    Require(record.LookupReasons.HasFlag(expected) && record.AllocatedBytes == null,
                        "RawSpecialAllocationRemainsUnknown");
                    unknown++; allocation = "Unknown";
                }
                rawDecoded++;
                rows.Add(new(target.CaseId, descriptor.Kind, descriptor.NameLength, kernelFinal[target.CaseId].Code,
                    result.Code, identity, logical, links, names, allocation));
            }
        }
        catch (NtfsRawVolumeException error)
        {
            Require(false, "RawNative-" + error.Code + "-" + error.NativeErrorCode);
            throw;
        }
        finally
        {
            lease?.Dispose();
        }
        Require(lease != null && lease.UnlockSucceeded, "RawUnlockSucceeded");
        token.ThrowIfCancellationRequested();
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            Require(File.GetAttributes(target.Path) == target.Attributes &&
                NativeStorage.TryRead(target.Path, false, target.Attributes, out var after, out _) && after == target.Storage,
                "RawPostNativeObjectStable");
            Require(NativeStorage.TryRead(target.ParentPath, true, File.GetAttributes(target.ParentPath), out var parent, out _) &&
                parent != null && parent.Identity == target.ParentStorage.Identity, "RawPostNativeParentStable");
            var descriptor = descriptors[target.CaseId];
            FileStorageInfo? ReadAlias(string path)
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return null;
                return NativeStorage.TryRead(path, false, attributes, out var info, out _) ? info : null;
            }
            Require(MatchesPostAlias(target, descriptor, ReadAlias), "RawPostNativeAliasStable");
            if (descriptor.NamedStream)
            {
                using var stream = new FileStream(target.Path + ":meta", FileMode.Open, FileAccess.Read, FileShare.Read);
                ReadOnlySpan<byte> expected = "synthetic named stream"u8;
                Require(stream.Length == expected.Length, "RawPostNamedStreamLength");
                var bytes = new byte[expected.Length]; stream.ReadExactly(bytes);
                Require(bytes.AsSpan().SequenceEqual(expected), "RawPostNamedStreamContent");
            }
            token.ThrowIfCancellationRequested();
        }
        var afterFacts = ReadFinalFacts(refreshFacts, token);
        Require(afterFacts == facts, "RawVolumeFactsStableAfterUnlock");
        Require(rawDecoded == 22 && rows.Count == 21 && unknown == 14, "RawResultCoverage");
        token.ThrowIfCancellationRequested();
        return new("raw-records-v1", targets.Count, kernel.Requests, kernel.Decoded, kernel.DecodeFailures,
            22, rawDecoded, rows.Count, unknown, map!.Runs.Count, lease!.ReadCalls, lease.RequestedBytes,
            facts.PhysicalSectorBytes, true, true, true, rows.AsReadOnly());
    }

    internal static NtfsRawVolumeFacts ReadFinalFacts(Func<NtfsRawVolumeFacts> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = read();
        token.ThrowIfCancellationRequested();
        return result;
    }

    internal static bool MatchesLinks(NtfsRecord record, uint nativeLinks)
    {
        var visible = record.Names.Where(item => item.Namespace != 2).ToArray();
        return visible.Length == nativeLinks &&
            visible.Select(item => (item.ParentReference, item.Name)).Distinct().Count() == visible.Length &&
            record.Names.Select(item => (item.ParentReference, item.Name, item.Namespace)).Distinct().Count() == record.Names.Count &&
            record.LinkCount == record.Names.Count;
    }

    internal static bool MatchesNames(NtfsRecord record, ulong expectedParent, NtfsKernelMatrixCase descriptor)
    {
        var visible = record.Names.Where(item => item.Namespace != 2).ToArray();
        var expected = new HashSet<string>(StringComparer.Ordinal) { NtfsKernelMatrix.FileName(descriptor.NameLength) };
        if (descriptor.HardLink) expected.Add("link.bin");
        return record.Names.All(item => item.ParentReference == expectedParent) &&
            visible.Length == expected.Count && expected.SetEquals(visible.Select(item => item.Name));
    }

    internal static bool MatchesLookupReasons(NtfsRecord record, NtfsKernelMatrixCase descriptor)
    {
        NtfsLookupReason? expected = descriptor.Kind switch
        {
            "Ordinary" => NtfsLookupReason.None,
            "Compressed" => NtfsLookupReason.Compressed,
            "Sparse" => NtfsLookupReason.Sparse,
            _ => null
        };
        if (!expected.HasValue) return false;
        if (descriptor.NamedStream) expected |= NtfsLookupReason.NamedData;
        return record.LookupReasons == expected.Value;
    }

    internal static bool MatchesPostAlias(NtfsRawTarget target, NtfsKernelMatrixCase descriptor,
        Func<string, FileStorageInfo?> reader) => !descriptor.HardLink ||
        reader(Path.Combine(target.ParentPath, "link.bin")) == target.Storage;
}
