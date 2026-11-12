using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

// Inert handles and memory-only native/filesystem operations. Never opens VHDs,
// devices, files or personal paths, and never invokes the real context factory.
internal static class NtfsVerifiedFixtureContextTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Fixture context check failed: " + name);
            passed++; Console.WriteLine("PASS fixture context " + name);
        }
        void Reject(Action<Fake> alter, string name, bool noOpen = false)
        {
            var fake = new Fake(); alter(fake);
            string? code = null;
            try { using var ignored = fake.Open(); }
            catch (NtfsFixtureGuardException error) { code = error.Code; Check(error.Message == error.Code && error.InnerException == null,
                "guard error contains only its fixed code"); }
            Check(code != null && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), name);
            Check(fake.Handles.All(value => value.IsClosed), "rejected creation closes every opened synthetic handle");
            if (noOpen) Check(fake.Handles.Count == 0, "invalid ownership is rejected before a volume or image opens");
            if (code is "FixtureImageFixedType" or "FakeImageInfoFailure")
                Check(fake.Handles.Count == 1 && fake.StoragePaths.Count == 0 && !fake.FileReads.Contains(fake.MarkerPath),
                    "unproven image type is rejected before opening the volume or reading its marker and targets");
        }
        var valid = new Fake();
        var preparation = new Fake(); preparation.MissingPaths.Add(preparation.Data);
        preparation.Prepare();
        Check(preparation.Handles.Count == 2 && preparation.Handles.All(x => x.IsClosed) && preparation.StoragePaths.Count == 0,
            "preparation permits the missing derived data root and releases metadata handles without capturing targets");
        bool requiresData = false;
        try { using var ignored = preparation.Open(); } catch (NtfsFixtureGuardException) { requiresData = true; }
        Check(requiresData && preparation.Handles.All(x => x.IsClosed), "real source construction still requires the complete data directory");
        foreach (uint subtype in new uint[] { 3, 4, 0 })
        {
            var badPreparation = new Fake { ProviderSubtype = subtype }; badPreparation.MissingPaths.Add(badPreparation.Data);
            bool rejected = false;
            try { badPreparation.Prepare(); } catch (NtfsFixtureGuardException error) { rejected = error.Code == "FixtureImageFixedType"; }
            Check(rejected && badPreparation.Handles.Count == 1 && badPreparation.Handles.All(x => x.IsClosed) &&
                badPreparation.StoragePaths.Count == 0, "preparation never weakens fixed-image checks just because data is not created yet");
        }
        var redirectedPreparation = new Fake(); redirectedPreparation.ManifestPath = Path.Combine(redirectedPreparation.Repository, "fixture.json");
        bool preparationRejected = false;
        try { redirectedPreparation.Prepare(); } catch (NtfsFixtureGuardException) { preparationRejected = true; }
        Check(preparationRejected && redirectedPreparation.Handles.Count == 0, "preparation rejects foreign workspace before opening native objects");
        var linkedPreparation = new Fake(); linkedPreparation.StatOverrides[linkedPreparation.Data] = new(FileAttributes.Directory | FileAttributes.ReparsePoint, 0);
        preparationRejected = false;
        try { linkedPreparation.Prepare(); } catch (NtfsFixtureGuardException) { preparationRejected = true; }
        Check(preparationRejected && linkedPreparation.Handles.All(x => x.IsClosed), "existing preparation data root cannot be a reparse entry");
        using (var context = valid.Open())
        {
            Check(context.IsSynthetic && !context.Volume.IsClosed && valid.Handles.Count == 2, "synthetic guard result owns exactly its two inert handles");
            Check(context.DataRoot == valid.Data, "data root is derived from the verified volume and nonce");
            var facts = context.QueryFacts();
            Check(facts.Serial == 41 && facts.Geometry is { SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024 } &&
                facts.Geometry.MftStartLcn == 10 && facts.PhysicalSectorBytes == 4096, "facts preserve verified volume geometry");
            var targets = context.CaptureFixedTargets();
            Check(targets.Count == 21 && targets.Select(x => x.CaseId).Distinct().Count() == 21 &&
                targets.All(x => x.Path.StartsWith(valid.Data + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
                "only all twenty-one fixed paths are captured under this nonce root");
            Check(valid.StoragePaths.All(path => valid.Storages.ContainsKey(path)), "metadata operations never use a caller-provided path");
            context.VerifyFixedTargets(targets);
            Check(valid.Handles.Count == 3 && valid.Handles[2].IsClosed && !context.Volume.IsClosed,
                "post-unlock fact requery closes its fresh handle and retains the owned handle");
            var redirected = targets.ToArray(); redirected[0] = redirected[0] with { Path = @"C:\SyntheticOther\not-owned.bin" };
            valid.StoragePaths.Clear(); valid.FileReads.Clear();
            bool rejected = false;
            try { context.VerifyFixedTargets(redirected); } catch (NtfsFixtureGuardException) { rejected = true; }
            Check(rejected && valid.StoragePaths.Count == 0 && valid.FileReads.Count == 0,
                "redirected baseline is rejected before any file or Native metadata operation");
            var changed = targets.ToArray(); changed[0] = changed[0] with { Storage = changed[0].Storage with { LogicalBytes = 1 } };
            rejected = false;
            try { context.VerifyFixedTargets(changed); } catch (NtfsFixtureGuardException) { rejected = true; }
            Check(rejected, "post-verifier cannot accept a caller assertion of different object data");
            context.Dispose(); context.Dispose();
            rejected = false;
            try { context.QueryFacts(); } catch (NtfsFixtureGuardException error) { rejected = error.Code == "FixtureContextClosed"; }
            Check(rejected && valid.Handles.All(x => x.IsClosed), "closed or twice-disposed context cannot query again");
        }
        Reject(fake => fake.ManifestPath = Path.Combine(fake.Repository, "fixture.json"), "workspace must be a direct owned artifacts child", true);
        Reject(fake => fake.ManifestPath = Path.Combine(fake.Workspace, "nested", "fixture.json"), "nested workspaces cannot pass prefix checks", true);
        Reject(fake => fake.ManifestPath = "fixture.json", "relative manifest is rejected", true);
        Reject(fake => fake.ChangeManifest(value => value["nonce"] = new string('b', 32)), "manifest nonce must match directory identity", true);
        Reject(fake => fake.ChangeManifest(value => value["mode"] = "raw-records"), "new factory cannot silently accept another fixture mode", true);
        Reject(fake => fake.ChangeManifest(value => value["image_path"] = @"C:\SyntheticOther\fixture.vhd"), "image cannot leave the owned workspace", true);
        Reject(fake => fake.ChangeManifest(value => value["workspace"] = @"C:\SyntheticOther"), "workspace field cannot redirect ownership", true);
        Reject(fake => fake.ChangeManifest(value => value["drive_root"] = "C:\\"), "system drive spelling is outside the fixture letter range", true);
        Reject(fake => fake.ChangeManifest(value => value["volume_guid"] = "\\\\?\\Volume{bad}\\"), "volume name must contain one complete GUID", true);
        Reject(fake => fake.ChangeManifest(value => value["Path"] = "private-marker"), "extra manifest fields are rejected", true);
        Reject(fake => fake.ChangeManifest(value => value.Remove("schema")), "missing manifest field is rejected", true);
        Reject(fake => fake.ChangeManifest(value => value["schema"] = 1.5), "noninteger schema is rejected", true);
        Reject(fake => fake.Manifest = Encoding.UTF8.GetBytes("{\"schema\":1," + Encoding.UTF8.GetString(fake.Manifest)[1..]),
            "duplicate JSON fields are rejected before their values can be lost", true);
        Reject(fake => fake.Manifest = Encoding.UTF8.GetBytes("{\"Schema\":1," + Encoding.UTF8.GetString(fake.Manifest)[1..]),
            "case variant manifest fields are rejected", true);
        Reject(fake => fake.Manifest = new byte[16384], "manifest byte bound is enforced", true);
        Reject(fake => fake.StatOverrides[fake.ManifestPath] = new(FileAttributes.ReparsePoint, fake.Manifest.Length), "manifest reparse entry is rejected", true);
        Reject(fake => fake.StatOverrides[fake.Workspace] = new(FileAttributes.Directory | FileAttributes.ReparsePoint, 0), "workspace reparse ancestor is rejected", true);
        Reject(fake => fake.StatOverrides[fake.Image] = new(FileAttributes.ReparsePoint, 256L * 1024 * 1024), "image reparse entry is rejected", true);
        Reject(fake => fake.StatOverrides[fake.Image] = new(FileAttributes.Archive, 255L * 1024 * 1024), "undersized image cannot be treated as the owned VHD", true);
        Reject(fake => fake.VirtualBytes++, "native VHD virtual size must be exactly the fixture budget");
        foreach (uint subtype in new uint[] { 0, 3, 4, uint.MaxValue })
            Reject(fake => fake.ProviderSubtype = subtype, "dynamic differencing or unknown VHD subtype is rejected before volume access");
        Reject(fake => fake.FailImageInfo = true, "image subtype or size query failure cannot be replaced by caller assertions");
        Reject(fake => fake.Device = @"\\.\PhysicalDrive-1", "invalid physical device identifier is rejected");
        Reject(fake => fake.ResolvedVolume = @"\\?\Volume{00000000-0000-0000-0000-000000000002}\", "drive root must resolve to the bound volume GUID");
        Reject(fake => fake.Format = "ReFS", "volume must be NTFS");
        Reject(fake => fake.MutateControl(0x560000, bytes => W32(bytes, 8, 8)), "volume extents must refer to the opened image device");
        Reject(fake => fake.MutateControl(0x560000, bytes => W32(bytes, 0, 2)), "multiple physical extents are unsupported");
        Reject(fake => fake.MutateControl(0x560000, bytes => W64(bytes, 16, -1)), "negative partition start is rejected");
        Reject(fake => fake.MutateControl(0x560000, bytes => W64(bytes, 24, 256L * 1024 * 1024)), "partition cannot extend beyond virtual disk bounds");
        Reject(fake => fake.MutateControl(0x70048, bytes => W32(bytes, 0, 1)), "unexpected partition style is rejected");
        Reject(fake => fake.MutateControl(0x70048, bytes => bytes[33] = 1), "boot partition flag is rejected");
        Reject(fake => fake.MutateControl(0x70048, bytes => W64(bytes, 8, 2 * 1024 * 1024)), "partition info must match volume extents");
        Reject(fake => fake.Marker = Encoding.UTF8.GetBytes("{}"), "volume marker must match every manifest field");
        Reject(fake => fake.StatOverrides[fake.MarkerPath] = new(FileAttributes.ReparsePoint, fake.Marker.Length), "marker reparse entry is rejected");
        Reject(fake => fake.MutateControl(0x90064, bytes => W32(bytes, 44, 8192)), "unexpected cluster size is rejected");
        Reject(fake => fake.MutateControl(0x90064, bytes => W32(bytes, 48, 4096)), "unexpected real record size is rejected");
        Reject(fake => fake.MutateControl(0x90064, bytes => W64(bytes, 8, long.MaxValue)), "sector count overflow is rejected before multiplication");
        Reject(fake => fake.MutateControl(0x90064, bytes => W64(bytes, 16, long.MaxValue)), "cluster count cannot exceed partition geometry");
        Reject(fake => fake.MutateControl(0x90064, bytes => W64(bytes, 64, long.MaxValue)), "MFT start must be a valid volume LCN");
        Reject(fake => fake.MutateControl(0x90064, bytes => W64(bytes, 56, 1025)), "MFT valid length must contain complete records");
        Reject(fake => fake.MutateControl(0x2d1400, bytes => W32(bytes, 20, 3072)), "invalid physical buffer alignment is rejected");
        Reject(fake => fake.ControlData[0x90064] = new byte[103], "short native geometry is rejected");
        Reject(fake => fake.StatOverrides[fake.Data] = new(FileAttributes.Directory | FileAttributes.ReparsePoint, 0), "nonce data directory cannot redirect to another tree");
        foreach (string change in new[] { "identity", "writer", "parent", "alias", "ads", "attribute", "reparse" })
        {
            var fake = new Fake(); using var context = fake.Open(); var baseline = context.CaptureFixedTargets();
            var primary = baseline.First(x => change == "alias" ? x.CaseId.StartsWith("B-", StringComparison.Ordinal) :
                change == "ads" ? x.CaseId.StartsWith("C-", StringComparison.Ordinal) : true);
            switch (change)
            {
                case "identity": fake.Storages[primary.Path] = primary.Storage with { Identity = new(42, primary.Storage.Identity.Low, 0) }; break;
                case "writer": fake.Storages[primary.Path] = primary.Storage with { WritableHandleOpen = true }; break;
                case "parent": fake.Storages[primary.ParentPath] = primary.ParentStorage with { Identity = new(41, primary.ParentStorage.Identity.Low + 1, 0) }; break;
                case "alias": fake.Storages[Path.Combine(primary.ParentPath, "link.bin")] = primary.Storage with { AllocatedBytes = 1 }; break;
                case "ads": fake.Stream = "changed"u8.ToArray(); break;
                case "attribute": fake.StatOverrides[primary.Path] = new(primary.Attributes | FileAttributes.Compressed, 0); break;
                default: fake.StatOverrides[primary.ParentPath] = new(FileAttributes.Directory | FileAttributes.ReparsePoint, 0); break;
            }
            bool rejected = false;
            try { context.VerifyFixedTargets(baseline); } catch (NtfsFixtureGuardException) { rejected = true; }
            Check(rejected, "post-verification rejects changed identities types aliases streams and reparse paths");
        }
        var canceled = new Fake(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false;
        try { using var ignored = canceled.Open(cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && canceled.Handles.Count == 0, "pre-cancellation performs no native open");
        var cancelDuring = new Fake(); using var during = new CancellationTokenSource();
        cancelDuring.OnVolumeOpen = during.Cancel; cancelled = false;
        try { using var ignored = cancelDuring.Open(during.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && cancelDuring.Handles.All(x => x.IsClosed), "canceled construction releases every handle and publishes no context");
        foreach (bool preparationPhase in new[] { false, true })
        {
            var cancelFinal = new Fake(); using var final = new CancellationTokenSource();
            cancelFinal.OnStat = path => { if (path == cancelFinal.Data) final.Cancel(); };
            cancelled = false;
            try
            {
                if (preparationPhase) cancelFinal.Prepare(final.Token);
                else { using var ignored = cancelFinal.Open(final.Token); }
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && cancelFinal.Handles.Count == 2 && cancelFinal.Handles.All(x => x.IsClosed),
                "cancellation at the final data-root check cannot publish context or successful preparation");
        }
        foreach (string mode in new[] { "fixture-source", "mft-range", "comparison", "raw-records" })
        foreach (bool bounded in new[] { false, true })
        {
            var scope = new Fake(); scope.ChangeManifest(value => value["mode"] = mode);
            scope.Marker = (byte[])scope.Manifest.Clone();
            bool accepted = false;
            try
            {
                using var context = bounded
                    ? NtfsVerifiedFixtureContext.CreateBoundedMftForTests(scope.ManifestPath, scope.Repository, scope)
                    : scope.Open();
                accepted = context.IsSynthetic;
            }
            catch (NtfsFixtureGuardException) { }
            Check(accepted == (mode == (bounded ? "mft-range" : "fixture-source")),
                "fixed and bounded factories cannot silently exchange their manifest permissions");
            Check(scope.Handles.All(handle => handle.IsClosed), "mode-isolated factory releases every opened synthetic handle");
        }
        var rangePreparation = new Fake(); rangePreparation.ChangeManifest(value => value["mode"] = "mft-range");
        rangePreparation.Marker = (byte[])rangePreparation.Manifest.Clone(); rangePreparation.MissingPaths.Add(rangePreparation.Data);
        NtfsVerifiedFixtureContext.VerifyPreparationBoundedMftForTests(rangePreparation.ManifestPath, rangePreparation.Repository, rangePreparation);
        Check(rangePreparation.Handles.All(handle => handle.IsClosed) && rangePreparation.StoragePaths.Count == 0,
            "bounded preparation validates ownership without publishing authority or reading absent targets");
        return passed;
    }

    private sealed class Fake : NtfsVerifiedFixtureContext.INative
    {
        internal string Repository = @"C:\Synthetic\FixtureProject";
        internal string Workspace => Path.Combine(Repository, "artifacts", "ntfs-live-" + new string('a', 32));
        internal string ManifestPath;
        internal string Image => Path.Combine(Workspace, "fixture.vhd");
        internal string Volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\";
        internal string Data => Path.Combine(Volume, "AsukaLensFixture-" + new string('a', 32));
        internal string MarkerPath => Path.Combine(Volume, ".asukalens-ntfs-fixture.json");
        internal byte[] Manifest, Marker, Stream = "synthetic named stream"u8.ToArray();
        internal string Device = @"\\.\PhysicalDrive7", Format = "NTFS", ResolvedVolume;
        internal long VirtualBytes = 256L * 1024 * 1024;
        internal uint ProviderSubtype = 2;
        internal bool FailImageInfo;
        internal Action? OnVolumeOpen;
        internal Action<string>? OnStat;
        internal readonly List<SafeFileHandle> Handles = new();
        internal readonly Dictionary<string, NtfsVerifiedFixtureContext.PathInfo> StatOverrides = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> MissingPaths = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<uint, byte[]> ControlData = new();
        internal readonly Dictionary<string, FileStorageInfo> Storages = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> StoragePaths = new(), FileReads = new();
        private readonly Dictionary<string, FileAttributes> attributes = new(StringComparer.OrdinalIgnoreCase);
        internal Fake()
        {
            ManifestPath = Path.Combine(Workspace, "fixture.json"); ResolvedVolume = Volume;
            Manifest = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, image_path = Image, volume_guid = Volume,
                drive_root = "E:\\", workspace = Workspace, nonce = new string('a', 32), mode = "fixture-source" });
            Marker = (byte[])Manifest.Clone();
            byte[] extents = new byte[32]; W32(extents, 0, 1); W32(extents, 8, 7); W64(extents, 16, 1024 * 1024); W64(extents, 24, 255L * 1024 * 1024);
            ControlData[0x560000] = extents;
            byte[] partition = new byte[144]; W64(partition, 8, 1024 * 1024); W64(partition, 16, 255L * 1024 * 1024);
            W32(partition, 24, 1); partition[32] = 7; partition[34] = 1; ControlData[0x70048] = partition;
            byte[] geometry = new byte[104]; W64(geometry, 0, 41); W64(geometry, 8, 255L * 1024 * 1024 / 512);
            W64(geometry, 16, 255L * 1024 * 1024 / 4096); W32(geometry, 40, 512); W32(geometry, 44, 4096); W32(geometry, 48, 1024);
            W64(geometry, 56, 65536); W64(geometry, 64, 10); BinaryPrimitives.WriteUInt16LittleEndian(geometry.AsSpan(100), 3);
            BinaryPrimitives.WriteUInt16LittleEndian(geometry.AsSpan(102), 1); ControlData[0x90064] = geometry;
            byte[] alignment = new byte[28]; W32(alignment, 0, 28); W32(alignment, 4, 28); W32(alignment, 16, 512); W32(alignment, 20, 4096);
            ControlData[0x2d1400] = alignment;
            int index = 0;
            foreach (var descriptor in NtfsKernelMatrix.Cases)
            {
                string parent = Path.Combine(Data, descriptor.CaseId), path = Path.Combine(parent, NtfsKernelMatrix.FileName(descriptor.NameLength));
                var time = new DateTime(2020, 1, 1);
                var storage = new FileStorageInfo(new(41, NtfsRecordFixtures.Reference((ulong)(100 + index), 1), 0),
                    descriptor.Kind == "Ordinary" ? descriptor.LogicalBytes : 0, descriptor.HardLink ? 2u : 1u, false, descriptor.LogicalBytes, time);
                Storages[path] = storage; Storages[parent] = new(new(41, NtfsRecordFixtures.Reference((ulong)(50 + index), 1), 0), 0, 1, false, -1, time);
                var kind = descriptor.Kind == "Compressed" ? FileAttributes.Compressed : descriptor.Kind == "Sparse" ? FileAttributes.SparseFile : 0;
                attributes[path] = FileAttributes.Archive | kind;
                if (descriptor.HardLink) { string alias = Path.Combine(parent, "link.bin"); Storages[alias] = storage; attributes[alias] = attributes[path]; }
                index++;
            }
        }
        internal NtfsVerifiedFixtureContext Open(CancellationToken token = default) => NtfsVerifiedFixtureContext.CreateForTests(ManifestPath, Repository, this, token);
        internal void Prepare(CancellationToken token = default) => NtfsVerifiedFixtureContext.VerifyPreparationForTests(ManifestPath, Repository, this, token);
        internal void ChangeManifest(Action<JsonObject> edit) { var value = JsonNode.Parse(Manifest)!.AsObject(); edit(value); Manifest = Encoding.UTF8.GetBytes(value.ToJsonString()); }
        internal void MutateControl(uint code, Action<byte[]> mutate) => mutate(ControlData[code]);
        public NtfsVerifiedFixtureContext.PathInfo Stat(string path)
        {
            OnStat?.Invoke(path);
            if (MissingPaths.Contains(path)) throw new FileNotFoundException("SyntheticMissing");
            if (StatOverrides.TryGetValue(path, out var changed)) return changed;
            if (path == ManifestPath) return new(FileAttributes.Archive, Manifest.Length);
            if (path == MarkerPath) return new(FileAttributes.Archive, Marker.Length);
            if (path == Image) return new(FileAttributes.Archive, 256L * 1024 * 1024 + 512);
            if (attributes.TryGetValue(path, out var flags)) return new(flags, 0);
            return new(FileAttributes.Directory, 0);
        }
        public byte[] ReadFile(string path, int maximum)
        {
            FileReads.Add(path);
            if (path == ManifestPath) return (byte[])Manifest.Clone();
            if (path == MarkerPath) return (byte[])Marker.Clone();
            if (path.EndsWith(":meta", StringComparison.Ordinal) && Storages.ContainsKey(path[..^5])) return (byte[])Stream.Clone();
            throw new NtfsFixtureGuardException("FakeUnexpectedRead");
        }
        public string ResolveVolume(string driveRoot) => ResolvedVolume;
        public string DriveFormat(string driveRoot) => Format;
        private SafeFileHandle Handle() { var handle = new SafeFileHandle((IntPtr)(100 + Handles.Count), false); Handles.Add(handle); return handle; }
        public SafeFileHandle OpenImage(string imagePath) => Handle();
        public (string Device, long VirtualBytes, uint ProviderSubtype) ImageInfo(SafeFileHandle image) =>
            FailImageInfo ? throw new NtfsFixtureGuardException("FakeImageInfoFailure") : (Device, VirtualBytes, ProviderSubtype);
        public SafeFileHandle OpenVolume(string volumeGuidWithoutSlash) { var result = Handle(); OnVolumeOpen?.Invoke(); return result; }
        public byte[] Control(SafeFileHandle volume, uint code, byte[]? input, int maximum) => (byte[])ControlData[code].Clone();
        public FileStorageInfo ReadStorage(string path, bool directory, FileAttributes ignored)
        { StoragePaths.Add(path); return Storages.TryGetValue(path, out var value) ? value : throw new NtfsFixtureGuardException("FakeUnexpectedStorage"); }
    }
    private static void W32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void W64(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
}
