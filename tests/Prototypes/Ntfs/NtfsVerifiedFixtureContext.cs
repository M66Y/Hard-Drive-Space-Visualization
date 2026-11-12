using System.Buffers.Binary;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed class NtfsFixtureGuardException : IOException
{
    internal string Code { get; }
    internal int NativeErrorCode { get; }
    internal NtfsFixtureGuardException(string code, int nativeErrorCode = 0) : base(code)
    { Code = code; NativeErrorCode = nativeErrorCode; }
}
internal sealed record NtfsFixtureReleasedWitness(string CaseId, FileStorageInfo Storage);
internal sealed record NtfsFixtureFastContext(FileStorageInfo DataRootStorage, IReadOnlyList<NtfsFixtureReleasedWitness> Released);

// Test-only owner for exactly one newly created fixture. It never accepts a
// caller-supplied volume, target path, disk number, or a validated boolean.
internal sealed class NtfsVerifiedFixtureContext : IDisposable
{
    internal readonly record struct PathInfo(FileAttributes Attributes, long Length);
    internal interface INative
    {
        PathInfo Stat(string path);
        byte[] ReadFile(string path, int maximum);
        string ResolveVolume(string driveRoot);
        string DriveFormat(string driveRoot);
        SafeFileHandle OpenImage(string imagePath);
        (string Device, long VirtualBytes, uint ProviderSubtype) ImageInfo(SafeFileHandle image);
        SafeFileHandle OpenVolume(string volumeGuidWithoutSlash);
        byte[] Control(SafeFileHandle volume, uint code, byte[]? input, int maximum);
        FileStorageInfo ReadStorage(string path, bool directory, FileAttributes attributes);
    }
    internal interface IWitnessNative
    {
        SafeFileHandle HoldDirectory(string path);
        SafeFileHandle CreateEmpty(string path);
        FileStorageInfo ReadHandle(SafeFileHandle handle, bool directory);
        void DeleteOnClose(SafeFileHandle handle);
    }
    private sealed record Manifest(string Nonce, string Workspace, string Image, string DriveRoot, string Volume, string Mode);
    private const long ImageBytes = 256L * 1024 * 1024;
    private readonly object gate = new();
    private readonly INative native;
    private readonly Manifest fixture;
    private readonly string manifestPath;
    private readonly SafeFileHandle volume, image;
    private readonly uint diskNumber;
    private readonly long partitionStart, partitionBytes;
    private readonly ulong serial;
    private bool disposed, witnessesPrepared;
    internal bool IsSynthetic { get; }
    internal string DataRoot { get; }
    // Borrowed only; the context owns and closes this handle after the lease.
    internal SafeFileHandle Volume { get { lock (gate) { Active(); return volume; } } }

    private NtfsVerifiedFixtureContext(INative adapter, Manifest binding, string manifest,
        SafeFileHandle ownedVolume, SafeFileHandle ownedImage, uint number, long start, long length,
        ulong volumeSerial, bool synthetic)
    {
        native = adapter; fixture = binding; manifestPath = manifest; volume = ownedVolume; image = ownedImage;
        diskNumber = number; partitionStart = start; partitionBytes = length; serial = volumeSerial; IsSynthetic = synthetic;
        DataRoot = Path.Combine(binding.Volume, "AsukaLensFixture-" + binding.Nonce);
    }

    internal static NtfsVerifiedFixtureContext Create(string manifestPath, CancellationToken token = default) =>
        CreateCore(manifestPath, RepositoryRoot(), Native.Instance, false, requireDataRoot: true, expectedMode: "fixture-source", token);

    // Preparation proves ownership before the legacy matrix can create data or
    // open any old control path. It never returns a source/context authority.
    internal static void VerifyPreparation(string manifestPath, CancellationToken token = default)
    {
        using var context = CreateCore(manifestPath, RepositoryRoot(), Native.Instance, false, requireDataRoot: false, expectedMode: "fixture-source", token);
    }

    // Explicit synthetic seam: it does not invoke the real native adapter, and
    // its result cannot be passed into the real source factory.
    internal static NtfsVerifiedFixtureContext CreateForTests(string manifestPath, string repositoryRoot,
        INative adapter, CancellationToken token = default) => CreateCore(manifestPath, repositoryRoot, adapter, true, requireDataRoot: true, expectedMode: "fixture-source", token);

    internal static void VerifyPreparationForTests(string manifestPath, string repositoryRoot,
        INative adapter, CancellationToken token = default)
    {
        using var context = CreateCore(manifestPath, repositoryRoot, adapter, true, requireDataRoot: false, expectedMode: "fixture-source", token);
    }

    // A distinct factory pins the bounded-range permission to its own mode.
    // Existing fixture-source callers cannot silently select the larger scope.
    internal static NtfsVerifiedFixtureContext CreateForBoundedMft(string manifestPath, CancellationToken token = default) =>
        CreateCore(manifestPath, RepositoryRoot(), Native.Instance, false, requireDataRoot: true, expectedMode: "mft-range", token);
    internal static void VerifyPreparationForBoundedMft(string manifestPath, CancellationToken token = default)
    {
        using var context = CreateCore(manifestPath, RepositoryRoot(), Native.Instance, false, requireDataRoot: false, expectedMode: "mft-range", token);
    }
    internal static NtfsVerifiedFixtureContext CreateBoundedMftForTests(string manifestPath, string repositoryRoot,
        INative adapter, CancellationToken token = default) =>
        CreateCore(manifestPath, repositoryRoot, adapter, true, requireDataRoot: true, expectedMode: "mft-range", token);
    internal static void VerifyPreparationBoundedMftForTests(string manifestPath, string repositoryRoot,
        INative adapter, CancellationToken token = default)
    {
        using var context = CreateCore(manifestPath, repositoryRoot, adapter, true, requireDataRoot: false, expectedMode: "mft-range", token);
    }

    // A separate fixed mode observes only bootstrap shape under the existing
    // bounded-source lifecycle. It cannot change either older factory's mode.
    internal static NtfsVerifiedFixtureContext CreateForBitmapObservation(string manifestPath, CancellationToken token = default) =>
        CreateCore(manifestPath, RepositoryRoot(), Native.Instance, false, requireDataRoot: true, expectedMode: "bitmap-bootstrap", token);
    internal static void VerifyPreparationForBitmapObservation(string manifestPath, CancellationToken token = default)
    {
        using var context = CreateCore(manifestPath, RepositoryRoot(), Native.Instance, false, requireDataRoot: false, expectedMode: "bitmap-bootstrap", token);
    }
    internal static NtfsVerifiedFixtureContext CreateBitmapObservationForTests(string manifestPath, string repositoryRoot,
        INative adapter, CancellationToken token = default) =>
        CreateCore(manifestPath, repositoryRoot, adapter, true, requireDataRoot: true, expectedMode: "bitmap-bootstrap", token);
    internal static void VerifyPreparationBitmapObservationForTests(string manifestPath, string repositoryRoot,
        INative adapter, CancellationToken token = default)
    {
        using var context = CreateCore(manifestPath, repositoryRoot, adapter, true, requireDataRoot: false, expectedMode: "bitmap-bootstrap", token);
    }

    internal static NtfsVerifiedFixtureContext CreateForFastScan(string manifestPath, CancellationToken token = default) =>
        CreateCore(manifestPath, RepositoryRoot(), Native.Instance, false, requireDataRoot: true, expectedMode: "fixture-fast-scan", token);
    internal static void VerifyPreparationForFastScan(string manifestPath, CancellationToken token = default)
    {
        using var context = CreateCore(manifestPath, RepositoryRoot(), Native.Instance, false, requireDataRoot: false, expectedMode: "fixture-fast-scan", token);
    }
    internal static NtfsVerifiedFixtureContext CreateFastScanForTests(string manifestPath, string repositoryRoot,
        INative adapter, CancellationToken token = default) =>
        CreateCore(manifestPath, repositoryRoot, adapter, true, requireDataRoot: true, expectedMode: "fixture-fast-scan", token);
    internal static void VerifyPreparationFastScanForTests(string manifestPath, string repositoryRoot,
        INative adapter, CancellationToken token = default)
    {
        using var context = CreateCore(manifestPath, repositoryRoot, adapter, true, requireDataRoot: false, expectedMode: "fixture-fast-scan", token);
    }

    internal NtfsFixtureFastContext PrepareFastContext(CancellationToken token = default) => Guard(() =>
    {
        lock (gate)
        {
            Active(); token.ThrowIfCancellationRequested(); Require(fixture.Mode == "fixture-fast-scan" && !witnessesPrepared, "FixtureWitnessScope");
            Require(native is IWitnessNative, "FixtureWitnessAdapter"); witnessesPrepared = true;
            var writer = (IWitnessNative)native;
            VerifyOrigin(); NoReparse(native, DataRoot);
            using var directory = writer.HoldDirectory(DataRoot);
            Require(!directory.IsInvalid && !directory.IsClosed, "FixtureWitnessDirectory");
            var rootBefore = writer.ReadHandle(directory, true);
            Require(Storage(rootBefore, serial, directory: true), "FixtureWitnessDirectory");
            var handles = new SafeFileHandle?[2]; var marked = new bool[2]; var witnesses = new List<NtfsFixtureReleasedWitness>(2);
            bool cleanupSucceeded = true, primary = false;
            try
            {
                // Both files exist at once. A create-delete-create sequence can
                // reuse one MFT slot and would not provide two independent cases.
                for (int i = 0; i < 2; i++)
                {
                    token.ThrowIfCancellationRequested(); NoReparse(native, DataRoot);
                    string path = Path.Combine(DataRoot, "released-witness-" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".tmp");
                    handles[i] = writer.CreateEmpty(path);
                    Require(!handles[i]!.IsInvalid && !handles[i]!.IsClosed, "FixtureWitnessCreate");
                    token.ThrowIfCancellationRequested(); var storage = writer.ReadHandle(handles[i]!, false);
                    Require(Storage(storage, serial) && storage.LogicalBytes == 0 && storage.AllocatedBytes == 0 && storage.LinkCount == 1,
                        "FixtureWitnessIdentity");
                    Require((native.Stat(path).Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Compressed |
                        FileAttributes.SparseFile | FileAttributes.Encrypted)) == 0 && writer.ReadHandle(directory, true).Identity == rootBefore.Identity,
                        "FixtureWitnessDirectory");
                    witnesses.Add(new("Released-" + (i + 1).ToString(CultureInfo.InvariantCulture), storage));
                }
                Require(witnesses.Select(item => item.Storage.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber).Distinct().Count() == 2,
                    "FixtureWitnessIdentity");
                token.ThrowIfCancellationRequested();
            }
            catch { primary = true; throw; }
            finally
            {
                for (int i = 0; i < 2; i++)
                {
                    if (handles[i] == null) continue;
                    try { writer.DeleteOnClose(handles[i]!); marked[i] = true; } catch { cleanupSucceeded = false; }
                    try { handles[i]!.Dispose(); } catch { cleanupSucceeded = false; }
                }
                // A cleanup fault cannot replace a pre-existing cancellation or
                // creation error; it also cannot produce a successful context.
                if (!primary) { token.ThrowIfCancellationRequested(); Require(cleanupSucceeded && marked.All(value => value), "FixtureWitnessCleanup"); }
            }
            for (int i = 0; i < 2; i++)
            {
                string path = Path.Combine(DataRoot, "released-witness-" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".tmp");
                RequireAbsent(path);
            }
            NoReparse(native, DataRoot); var rootAfter = native.ReadStorage(DataRoot, true, native.Stat(DataRoot).Attributes);
            Require(Storage(rootAfter, serial, directory: true) && rootAfter.Identity == rootBefore.Identity &&
                writer.ReadHandle(directory, true).Identity == rootBefore.Identity, "FixtureWitnessDirectory");
            token.ThrowIfCancellationRequested(); return new NtfsFixtureFastContext(rootAfter, witnesses.AsReadOnly());
        }
    });

    internal void VerifyFastContext(NtfsFixtureFastContext baseline, CancellationToken token = default) => Guard(() =>
    {
        lock (gate)
        {
            Active(); token.ThrowIfCancellationRequested(); Require(fixture.Mode == "fixture-fast-scan" && witnessesPrepared && baseline != null &&
                Storage(baseline.DataRootStorage, serial, directory: true) && baseline.Released.Count == 2 &&
                baseline.Released.Select(item => item.CaseId).SequenceEqual(new[] { "Released-1", "Released-2" }), "FixtureWitnessScope");
            VerifyOrigin(); NoReparse(native, DataRoot);
            Require(native.ReadStorage(DataRoot, true, native.Stat(DataRoot).Attributes) == baseline.DataRootStorage, "FixtureWitnessDirectory");
            for (int i = 1; i <= 2; i++) RequireAbsent(Path.Combine(DataRoot, "released-witness-" + i.ToString(CultureInfo.InvariantCulture) + ".tmp"));
            token.ThrowIfCancellationRequested(); return true;
        }
    });

    internal ScanResult ScanFastFixture(CancellationToken token = default) => Guard(() =>
    {
        lock (gate)
        {
            Active(); token.ThrowIfCancellationRequested(); Require(fixture.Mode == "fixture-fast-scan" && witnessesPrepared, "FixtureWitnessScope");
            VerifyOrigin(); NoReparse(native, DataRoot);
            var result = new DiskScanner(1).Scan(DataRoot, null, token);
            token.ThrowIfCancellationRequested(); return result;
        }
    });

    private void RequireAbsent(string path)
    {
        try { _ = native.Stat(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return; }
        throw Failure("FixtureWitnessNotReleased");
    }

    private static NtfsVerifiedFixtureContext CreateCore(string manifestPath, string repositoryRoot,
        INative adapter, bool synthetic, bool requireDataRoot, string expectedMode, CancellationToken token) => Guard(() =>
    {
        token.ThrowIfCancellationRequested();
        Require(adapter != null, "FixtureAdapter");
        string repository = Full(repositoryRoot), manifest = Full(manifestPath);
        string artifacts = Path.Combine(repository, "artifacts");
        string workspace = Path.GetDirectoryName(manifest) ?? throw Failure("FixtureWorkspace");
        string leaf = Path.GetFileName(workspace);
        Require(Path.GetDirectoryName(workspace)?.Equals(artifacts, StringComparison.OrdinalIgnoreCase) == true &&
            Path.GetFileName(manifest) == "fixture.json" && leaf.StartsWith("ntfs-live-", StringComparison.Ordinal) &&
            Nonce(leaf[10..]), "FixtureWorkspace");
        NoReparse(adapter, manifest); RequireFile(adapter.Stat(manifest), 1, 16383, "FixtureManifestBounds");
        var binding = Parse(Read(adapter, manifest, 16383));
        Require(binding.Mode == expectedMode && binding.Nonce == leaf[10..] &&
            SamePath(Full(binding.Workspace), workspace) && SamePath(Full(binding.Image), Path.Combine(workspace, "fixture.vhd")),
            "FixtureManifestIdentity");
        Require(binding.DriveRoot.Length == 3 && binding.DriveRoot[0] is >= 'D' and <= 'Z' && binding.DriveRoot[1..] == ":\\" &&
            VolumeSyntax(binding.Volume), "FixtureVolumeSyntax");
        NoReparse(adapter, binding.Image); RequireFile(adapter.Stat(binding.Image), ImageBytes, ImageBytes + 1024 * 1024, "FixtureImageBounds");
        SafeFileHandle? openedImage = null, openedVolume = null;
        try
        {
            token.ThrowIfCancellationRequested();
            openedImage = adapter.OpenImage(binding.Image);
            Require(!openedImage.IsInvalid && !openedImage.IsClosed, "FixtureImageOpen");
            token.ThrowIfCancellationRequested();
            var info = adapter.ImageInfo(openedImage);
            uint number = PhysicalNumber(info.Device);
            Require(info.VirtualBytes == ImageBytes, "FixtureImageSize");
            Require(info.ProviderSubtype == 2, "FixtureImageFixedType");
            Require(adapter.ResolveVolume(binding.DriveRoot).Equals(binding.Volume, StringComparison.OrdinalIgnoreCase) &&
                adapter.DriveFormat(binding.DriveRoot).Equals("NTFS", StringComparison.OrdinalIgnoreCase), "FixtureMountedVolume");
            token.ThrowIfCancellationRequested();
            openedVolume = adapter.OpenVolume(binding.Volume.TrimEnd('\\'));
            Require(!openedVolume.IsInvalid && !openedVolume.IsClosed, "FixtureVolumeOpen");
            token.ThrowIfCancellationRequested();
            var extent = Extent(adapter, openedVolume, number);
            Partition(adapter, openedVolume, extent.Start, extent.Length);
            // Marker bytes are not touched until the actual opened volume has
            // been proved to be backed by this owned virtual image.
            string marker = Path.Combine(binding.Volume, ".asukalens-ntfs-fixture.json");
            NoReparse(adapter, marker); RequireFile(adapter.Stat(marker), 1, 16383, "FixtureMarkerBounds");
            Require(Parse(Read(adapter, marker, 16383)) == binding, "FixtureMarkerBinding");
            var facts = Facts(adapter, openedVolume, extent.Length, null);
            token.ThrowIfCancellationRequested();
            var result = new NtfsVerifiedFixtureContext(adapter, binding, manifest, openedVolume, openedImage,
                number, extent.Start, extent.Length, facts.Serial, synthetic);
            CheckDataRoot(adapter, result.DataRoot, requireDataRoot);
            token.ThrowIfCancellationRequested();
            openedImage = null; openedVolume = null;
            return result;
        }
        finally { openedVolume?.Dispose(); openedImage?.Dispose(); }
    });

    internal NtfsRawVolumeFacts QueryFacts(bool reopen = false, CancellationToken token = default) => Guard(() =>
    {
        lock (gate)
        {
            Active(); token.ThrowIfCancellationRequested(); VerifyOrigin();
            using var fresh = reopen ? native.OpenVolume(fixture.Volume.TrimEnd('\\')) : null;
            var target = fresh ?? volume;
            Require(!target.IsInvalid && !target.IsClosed, "FixtureVolumeOpen");
            var extent = Extent(native, target, diskNumber);
            Require(extent.Start == partitionStart && extent.Length == partitionBytes, "FixturePartitionChanged");
            Partition(native, target, extent.Start, extent.Length);
            var result = Facts(native, target, partitionBytes, serial);
            token.ThrowIfCancellationRequested(); Active(); return result;
        }
    });

    internal IReadOnlyList<NtfsRawTarget> CaptureFixedTargets(CancellationToken token = default) => Guard(() =>
    {
        lock (gate)
        {
            Active(); token.ThrowIfCancellationRequested();
            var targets = new List<NtfsRawTarget>(21);
            var identities = new HashSet<FileIdentity>();
            var slots = new HashSet<ulong>();
            Require(NtfsKernelMatrix.Cases.Count == 21, "FixtureCaseBudget");
            foreach (var descriptor in NtfsKernelMatrix.Cases)
            {
                token.ThrowIfCancellationRequested();
                string parent = Path.Combine(DataRoot, descriptor.CaseId), path = Path.Combine(parent, NtfsKernelMatrix.FileName(descriptor.NameLength));
                NoReparse(native, parent); NoReparse(native, path);
                var attributes = native.Stat(path).Attributes;
                Require((native.Stat(parent).Attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.Directory) == 0,
                    "FixtureTargetType");
                FileAttributes expected = descriptor.Kind switch { "Compressed" => FileAttributes.Compressed, "Sparse" => FileAttributes.SparseFile, _ => 0 };
                Require((attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) == expected &&
                    (attributes & (FileAttributes.Encrypted | FileAttributes.Offline | FileAttributes.ReparsePoint)) == 0,
                    "FixtureTargetAttributes");
                var storage = native.ReadStorage(path, false, attributes);
                var parentStorage = native.ReadStorage(parent, true, native.Stat(parent).Attributes);
                Require(Storage(storage, serial) && Storage(parentStorage, serial, directory: true) && storage.LogicalBytes == descriptor.LogicalBytes &&
                    storage.LinkCount == (descriptor.HardLink ? 2u : 1u) && identities.Add(storage.Identity) &&
                    slots.Add(storage.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber), "FixtureTargetIdentity");
                if (descriptor.HardLink)
                {
                    string alias = Path.Combine(parent, "link.bin"); NoReparse(native, alias);
                    var aliasAttributes = native.Stat(alias).Attributes;
                    Require((aliasAttributes & FileAttributes.Directory) == 0 && native.ReadStorage(alias, false, aliasAttributes) == storage,
                        "FixtureAliasBinding");
                }
                if (descriptor.NamedStream)
                    Require(Read(native, path + ":meta", 64).AsSpan().SequenceEqual("synthetic named stream"u8), "FixtureNamedStream");
                targets.Add(new(descriptor.CaseId, path, parent, storage, parentStorage, attributes));
            }
            token.ThrowIfCancellationRequested(); Active(); return Array.AsReadOnly(targets.ToArray());
        }
    });

    internal void VerifyFixedTargets(IReadOnlyList<NtfsRawTarget> baseline, CancellationToken token = default) => Guard(() =>
    {
        lock (gate)
        {
            Active(); token.ThrowIfCancellationRequested();
            Require(baseline != null && baseline.Count == 21, "FixtureBaselineBudget");
            var expected = new Dictionary<string, NtfsRawTarget>(StringComparer.Ordinal);
            foreach (var item in baseline)
            {
                token.ThrowIfCancellationRequested();
                var descriptor = NtfsKernelMatrix.Cases.SingleOrDefault(value => value.CaseId == item?.CaseId);
                Require(item != null && descriptor != null && !expected.ContainsKey(item.CaseId), "FixtureBaselineCase");
                string parent = Path.Combine(DataRoot, descriptor!.CaseId), path = Path.Combine(parent, NtfsKernelMatrix.FileName(descriptor.NameLength));
                Require(SamePath(item!.Path, path) && SamePath(item.ParentPath, parent), "FixtureBaselinePath");
                expected.Add(item.CaseId, item);
            }
            Active();
            foreach (var current in CaptureFixedTargets(token))
            {
                var old = expected[current.CaseId];
                Require(current.Storage == old.Storage && current.ParentStorage == old.ParentStorage && current.Attributes == old.Attributes,
                    "FixturePostTargetChanged");
            }
            _ = QueryFacts(reopen: true, token);
            token.ThrowIfCancellationRequested(); Active(); return true;
        }
    });

    private void VerifyOrigin()
    {
        NoReparse(native, manifestPath); NoReparse(native, fixture.Image);
        Require(Parse(Read(native, manifestPath, 16383)) == fixture, "FixtureManifestChanged");
        RequireFile(native.Stat(fixture.Image), ImageBytes, ImageBytes + 1024 * 1024, "FixtureImageBounds");
        var current = native.ImageInfo(image);
        Require(current.VirtualBytes == ImageBytes && current.ProviderSubtype == 2 && PhysicalNumber(current.Device) == diskNumber,
            "FixtureImageChanged");
        Require(native.ResolveVolume(fixture.DriveRoot).Equals(fixture.Volume, StringComparison.OrdinalIgnoreCase) &&
            native.DriveFormat(fixture.DriveRoot).Equals("NTFS", StringComparison.OrdinalIgnoreCase), "FixtureMountedVolume");
        string marker = Path.Combine(fixture.Volume, ".asukalens-ntfs-fixture.json");
        NoReparse(native, marker);
        Require(Parse(Read(native, marker, 16383)) == fixture, "FixtureMarkerBinding");
    }

    private static bool Storage(FileStorageInfo? value, ulong volumeSerial, bool directory = false) => value != null && value.Identity.Volume == volumeSerial &&
        value.Identity.High == 0 && value.Identity.Low >> 48 != 0 && (directory ? value.LogicalBytes == -1 : value.LogicalBytes >= 0) && value.AllocatedBytes >= 0 &&
        value.LinkCount > 0 && !value.WritableHandleOpen && value.Modified.HasValue;
    private static (long Start, long Length) Extent(INative adapter, SafeFileHandle target, uint number)
    {
        byte[] bytes = adapter.Control(target, 0x560000, null, 128);
        Require(bytes.Length is >= 32 and <= 128 && U32(bytes, 0) == 1 && U32(bytes, 8) == number, "FixtureVolumeExtents");
        long start = I64(bytes, 16), length = I64(bytes, 24);
        Require(start >= 0 && start % 512 == 0 && length > 0 && length % 512 == 0 && start <= ImageBytes - length,
            "FixturePartitionBounds");
        return (start, length);
    }
    private static void Partition(INative adapter, SafeFileHandle target, long start, long length)
    {
        byte[] bytes = adapter.Control(target, 0x70048, null, 144);
        Require(bytes.Length is >= 40 and <= 144 && U32(bytes, 0) == 0 && I64(bytes, 8) == start && I64(bytes, 16) == length &&
            U32(bytes, 24) > 0 && bytes[29] == 0 && bytes[32] == 7 && bytes[33] == 0 && bytes[34] != 0,
            "FixturePartitionIdentity");
    }
    private static NtfsRawVolumeFacts Facts(INative adapter, SafeFileHandle target, long partitionLength, ulong? expectedSerial)
    {
        byte[] bytes = adapter.Control(target, 0x90064, null, 128);
        Require(bytes.Length is >= 104 and <= 128, "FixtureVolumeDataBounds");
        ulong volumeSerial = U64(bytes, 0);
        long sectors = I64(bytes, 8), clusters = I64(bytes, 16);
        Require((!expectedSerial.HasValue || volumeSerial == expectedSerial.Value) && U32(bytes, 40) == 512 &&
            U32(bytes, 44) == 4096 && U32(bytes, 48) == 1024 && sectors > 0 && sectors <= partitionLength / 512 &&
            clusters > 0 && clusters <= sectors / 8 && U16(bytes, 100) == 3 && U16(bytes, 102) == 1,
            "FixtureNtfsGeometry");
        long mftStart = I64(bytes, 64), valid = I64(bytes, 56);
        Require(mftStart >= 0 && mftStart < clusters && valid >= 1024 && valid % 1024 == 0 && valid <= sectors * 512,
            "FixtureMftGeometry");
        var query = new byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(query, 6);
        byte[] alignment = adapter.Control(target, 0x2d1400, query, 64);
        Require(alignment.Length is >= 28 and <= 64 && U32(alignment, 0) >= 28 && U32(alignment, 4) >= 28 &&
            U32(alignment, 4) <= alignment.Length && U32(alignment, 16) == 512, "FixtureAlignmentBounds");
        uint physical = U32(alignment, 20);
        Require(physical is >= 512 and <= 65536 && (physical & (physical - 1)) == 0, "FixturePhysicalSector");
        return new(new(512, 4096, 1024, sectors * 512, clusters, mftStart, valid), volumeSerial, (int)physical);
    }

    private static Manifest Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        string[] allowed = ["schema", "image_path", "volume_guid", "drive_root", "workspace", "nonce", "mode"];
        Require(root.ValueKind == JsonValueKind.Object, "FixtureJsonShape");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject()) Require(allowed.Contains(field.Name, StringComparer.Ordinal) && seen.Add(field.Name), "FixtureJsonFields");
        Require(seen.Count == allowed.Length && root.GetProperty("schema").TryGetInt32(out int schema) && schema == 1, "FixtureJsonSchema");
        string Text(string name)
        {
            var value = root.GetProperty(name);
            Require(value.ValueKind == JsonValueKind.String, "FixtureJsonValue");
            return value.GetString() ?? throw Failure("FixtureJsonValue");
        }
        string nonce = Text("nonce"); Require(Nonce(nonce), "FixtureNonce");
        return new(nonce, Text("workspace"), Text("image_path"), Text("drive_root"), Text("volume_guid"), Text("mode"));
    }
    private static byte[] Read(INative adapter, string path, int limit)
    {
        byte[] bytes = adapter.ReadFile(path, limit);
        Require(bytes != null && bytes.Length >= 1 && bytes.Length <= limit, "FixtureReadBounds");
        return (byte[])bytes.Clone();
    }
    private static void NoReparse(INative adapter, string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
            Require((adapter.Stat(current).Attributes & FileAttributes.ReparsePoint) == 0, "FixtureReparse");
    }
    private static void CheckDataRoot(INative adapter, string path, bool required)
    {
        try
        {
            NoReparse(adapter, path);
            Require((adapter.Stat(path).Attributes & FileAttributes.Directory) != 0, "FixtureDataRoot");
        }
        catch (Exception error) when (!required && (error is FileNotFoundException or DirectoryNotFoundException))
        {
            // Absence is allowed only for this exact derived data root, never
            // for its existing volume/ancestors. No directories are created.
            NoReparse(adapter, Path.GetDirectoryName(path) ?? throw Failure("FixtureDataRoot"));
        }
    }
    private static void RequireFile(PathInfo info, long minimum, long maximum, string code) =>
        Require((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 && info.Length >= minimum && info.Length <= maximum, code);
    private static uint PhysicalNumber(string value)
    {
        const string prefix = @"\\.\PhysicalDrive";
        Require(value != null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _), "FixturePhysicalDevice");
        return uint.Parse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture);
    }
    private static bool Nonce(string value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool VolumeSyntax(string value) => value != null && value.Length == 49 && value.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) &&
        value.EndsWith(@"}\", StringComparison.Ordinal) && Guid.TryParseExact(value.AsSpan(11, 36), "D", out _);
    private static string Full(string value)
    {
        Require(!string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value), "FixtureAbsolutePath");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static NtfsFixtureGuardException Failure(string code) => new(code);
    private static void Require([DoesNotReturnIf(false)] bool condition, string code) { if (!condition) throw Failure(code); }
    private void Active() { Require(!disposed && !volume.IsClosed && !image.IsClosed, "FixtureContextClosed"); }
    private static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (OperationCanceledException) { throw; }
        catch (NtfsFixtureGuardException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or OverflowException)
        { throw Failure("FixtureGuardOperation"); }
    }
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && directory != null; i++, directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DiskAtlas.csproj")) && File.Exists(Path.Combine(directory.FullName, "global.json")))
                return directory.FullName;
        throw Failure("FixtureRepositoryRoot");
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            try { volume.Dispose(); } finally { image.Dispose(); }
        }
    }

    private sealed class Native : INative, IWitnessNative
    {
        internal static readonly Native Instance = new();
        [StructLayout(LayoutKind.Sequential)] private struct WitnessBasic { public long Creation, Access, Write, Change; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)] private struct WitnessId { public ulong Volume, Low, High; }
        [StructLayout(LayoutKind.Sequential)] private struct WitnessStandard { public long Allocated, Logical; public uint Links; public byte DeletePending, Directory; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct WitnessDisposition { public byte Delete; }
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool WitnessInfo(SafeFileHandle file, int kind, out WitnessBasic data, uint bytes);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool WitnessInfo(SafeFileHandle file, int kind, out WitnessId data, uint bytes);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool WitnessInfo(SafeFileHandle file, int kind, out WitnessStandard data, uint bytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref WitnessDisposition data, uint bytes);
        public SafeFileHandle HoldDirectory(string path) => OpenWitness(path, 0x80, 3, 0x02200000);
        public SafeFileHandle CreateEmpty(string path) => OpenWitness(path, 0x10080, 1, 0x00200080);
        private static SafeFileHandle OpenWitness(string path, uint access, uint creation, uint flags)
        {
            var handle = NativeStorage.CreateFileW(path, access, 1, IntPtr.Zero, creation, flags, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new NtfsFixtureGuardException("FixtureWitnessOpen", error); }
            return handle;
        }
        public FileStorageInfo ReadHandle(SafeFileHandle handle, bool directory)
        {
            if (!WitnessInfo(handle, 0, out WitnessBasic basic, (uint)Marshal.SizeOf<WitnessBasic>()) ||
                !WitnessInfo(handle, 18, out WitnessId id, (uint)Marshal.SizeOf<WitnessId>()) ||
                !WitnessInfo(handle, 1, out WitnessStandard standard, (uint)Marshal.SizeOf<WitnessStandard>()))
                throw new NtfsFixtureGuardException("FixtureWitnessQuery", Marshal.GetLastWin32Error());
            Require((basic.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Encrypted | FileAttributes.Compressed | FileAttributes.SparseFile)) == 0 &&
                ((basic.Attributes & (uint)FileAttributes.Directory) != 0) == directory && standard.Directory == (directory ? 1 : 0) && standard.DeletePending == 0,
                "FixtureWitnessIdentity");
            return new(new(id.Volume, id.Low, id.High), directory ? 0 : standard.Allocated, standard.Links, false,
                directory ? -1 : standard.Logical, DateTime.FromFileTimeUtc(basic.Write).ToLocalTime());
        }
        public void DeleteOnClose(SafeFileHandle handle)
        {
            var disposition = new WitnessDisposition { Delete = 1 };
            if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
                throw new NtfsFixtureGuardException("FixtureWitnessDelete", Marshal.GetLastWin32Error());
        }
        [StructLayout(LayoutKind.Sequential)] private struct StorageType { public uint DeviceId; public Guid Vendor; }
        [StructLayout(LayoutKind.Sequential)] private struct OpenParameters { public uint Version, Depth; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder volume, int length);
        [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
        private static extern uint OpenVirtualDisk(ref StorageType type, string path, uint access, uint flags, ref OpenParameters parameters, out SafeFileHandle handle);
        [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetVirtualDiskPhysicalPath(SafeFileHandle handle, ref uint bytes, StringBuilder path);
        [DllImport("virtdisk.dll")]
        private static extern uint GetVirtualDiskInformation(SafeFileHandle handle, ref uint bytes, IntPtr info, out uint used);
        public PathInfo Stat(string path)
        {
            var attributes = File.GetAttributes(path);
            return new(attributes, (attributes & FileAttributes.Directory) != 0 ? 0 : new FileInfo(path).Length);
        }
        public byte[] ReadFile(string path, int maximum)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Require(stream.Length is > 0 && stream.Length <= maximum, "FixtureReadBounds");
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            Require(stream.Position == stream.Length, "FixtureReadBounds"); return bytes;
        }
        public string ResolveVolume(string driveRoot)
        {
            var name = new StringBuilder(128);
            if (!GetVolumeNameForVolumeMountPointW(driveRoot, name, name.Capacity)) throw new NtfsFixtureGuardException("FixtureResolveVolume", Marshal.GetLastWin32Error());
            return name.ToString();
        }
        public string DriveFormat(string driveRoot) => new DriveInfo(driveRoot).DriveFormat;
        public SafeFileHandle OpenImage(string path)
        {
            var type = new StorageType { DeviceId = 2, Vendor = new("ec984aec-a0f9-47e9-901f-71415a66345b") };
            var parameters = new OpenParameters { Version = 1, Depth = 1 };
            // NO_PARENTS prevents even opening a differencing-chain parent;
            // ProviderSubtype is separately required to be Fixed before use.
            uint error = OpenVirtualDisk(ref type, path, 0xc0000, 1, ref parameters, out var result);
            if (error != 0) { result?.Dispose(); throw new NtfsFixtureGuardException("FixtureImageOpen", unchecked((int)error)); }
            return result;
        }
        public (string Device, long VirtualBytes, uint ProviderSubtype) ImageInfo(SafeFileHandle handle)
        {
            var name = new StringBuilder(128); uint bytes = 256;
            uint error = GetVirtualDiskPhysicalPath(handle, ref bytes, name);
            if (error != 0) throw new NtfsFixtureGuardException("FixtureImageDevice", unchecked((int)error));
            IntPtr info = Marshal.AllocHGlobal(64);
            try
            {
                Marshal.Copy(new byte[64], 0, info, 64); Marshal.WriteInt32(info, 1); bytes = 64;
                error = GetVirtualDiskInformation(handle, ref bytes, info, out uint used);
                if (error != 0) throw new NtfsFixtureGuardException("FixtureImageInformation", unchecked((int)error));
                Require(used >= 32 && used <= 64 && Marshal.ReadInt32(info) == 1, "FixtureImageInformation");
                long virtualBytes = Marshal.ReadInt64(info, 8);
                // GET_VIRTUAL_DISK_INFO_PROVIDER_SUBTYPE = 7. Its DWORD union
                // member uses the same eight-byte aligned union offset.
                Marshal.Copy(new byte[64], 0, info, 64); Marshal.WriteInt32(info, 7); bytes = 64;
                error = GetVirtualDiskInformation(handle, ref bytes, info, out used);
                if (error != 0) throw new NtfsFixtureGuardException("FixtureImageSubtype", unchecked((int)error));
                Require(used >= 12 && used <= 64 && Marshal.ReadInt32(info) == 7, "FixtureImageSubtype");
                return (name.ToString(), virtualBytes, unchecked((uint)Marshal.ReadInt32(info, 8)));
            }
            finally { Marshal.FreeHGlobal(info); }
        }
        public SafeFileHandle OpenVolume(string name)
        {
            var result = NativeStorage.CreateFileW(name, 0x80000000, NativeStorage.ShareAll, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (result.IsInvalid) { int error = Marshal.GetLastWin32Error(); result.Dispose(); throw new NtfsFixtureGuardException("FixtureVolumeOpen", error); }
            return result;
        }
        public byte[] Control(SafeFileHandle volume, uint code, byte[]? input, int maximum)
        {
            var bytes = new byte[maximum];
            if (!NativeStorage.DeviceIoControl(volume, code, input, input?.Length ?? 0, bytes, bytes.Length, out int used, IntPtr.Zero))
                throw new NtfsFixtureGuardException("FixtureControlFailed", Marshal.GetLastWin32Error());
            Require(used >= 0 && used <= maximum, "FixtureControlBounds"); return bytes.AsSpan(0, used).ToArray();
        }
        public FileStorageInfo ReadStorage(string path, bool directory, FileAttributes attributes)
        {
            if (!NativeStorage.TryRead(path, directory, attributes, out var storage, out _) || storage == null)
                throw new NtfsFixtureGuardException("FixtureStorageFailed");
            return storage;
        }
    }
}
