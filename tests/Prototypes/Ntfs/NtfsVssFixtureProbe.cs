using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.NtfsPrototype;

internal sealed class NtfsVssProbeException(string code, int error = 0) : IOException(code)
{ internal string Code { get; } = code; internal int Error { get; } = error; }
internal sealed record NtfsVssFixtureEvidence(string Revision, string Stage, int CreatedSnapshots, int DeletedSnapshots,
    int FilesCompared, int RawRecordsCompared, int RawReadCalls, long RawRequestedBytes, bool PropertiesBound,
    bool SnapshotFilesStable, bool LiveChangesObserved, bool GeometryVerified, bool RawRecordsMatched, bool CleanupConfirmed);

// Dedicated requester for one new, guarded fixture. It never accepts an arbitrary
// volume, snapshot ID/device, Native verifier, or source-proof boolean from CLI.
internal static class NtfsVssFixtureProbe
{
    internal static void Require(bool value, string code, int error = 0) { if (!value) throw new NtfsVssProbeException(code, error); }
    internal static int Run(string manifest, CancellationToken token = default)
    {
        int checks = 0, nativeError = 0; string code = "None"; bool cleanup = false;
        string? summary = null; NtfsVssFixtureEvidence? evidence = null; NtfsVssFixtureOwner? session = null; bool ownerAttempted = false;
        NtfsVerifiedFixtureContext? context = null;
        void Check(bool condition, string failure) { Require(condition, failure); checks++; }
        try
        {
            context = NtfsVerifiedFixtureContext.CreateForVss(manifest, token);
            summary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifest))!, "live-summary.json");
            Check(!File.Exists(summary) && !Directory.Exists(context.DataRoot), "VssFixtureCreate");
            var origin = context.QueryFacts(token: token);
            Directory.CreateDirectory(context.DataRoot);
            Check((File.GetAttributes(context.DataRoot) & FileAttributes.ReparsePoint) == 0, "VssFixtureCreate");
            // Pin this exact new directory without delete sharing throughout all
            // file creation/mutation. Later writers open the file itself without
            // following reparse points and recheck its bound identity first.
            using var directoryPin = NativeStorage.CreateFileW(context.DataRoot, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            Check(!directoryPin.IsInvalid, "VssFixtureCreate");
            var directoryIdentity = BoundIdentity(directoryPin, directory: true);
            Check(directoryIdentity.Volume == origin.Serial, "VssFixtureCreate");
            string[] names = ["s0000001.bin", "s0000002.bin", "s0000003.bin"];
            for (int i = 0; i < 3; i++)
            {
                token.ThrowIfCancellationRequested();
                using var file = new FileStream(Path.Combine(context.DataRoot, names[i]), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(new byte[(i + 1) * 8192]); file.Flush(true);
            }
            ownerAttempted = true; session = NtfsVssFixtureOwner.CreateProbe(context, token);
            string snapshotRoot = session.SnapshotRoot;
            string rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(context.DataRoot));
            string snapshotData = snapshotRoot + "\\" + rootName;
            FileStorageInfo SnapshotInfo(string name, bool directory = false)
            {
                token.ThrowIfCancellationRequested(); string path = directory ? snapshotData : snapshotData + "\\" + name;
                var attributes = File.GetAttributes(path);
                FileStorageInfo? storage = null;
                Check((attributes & FileAttributes.ReparsePoint) == 0 && NativeStorage.TryRead(path, directory, attributes, out storage, out _) && storage != null,
                    "VssSnapshotNative");
                return storage!;
            }
            var parent = SnapshotInfo("", true);
            Check(parent.Identity == directoryIdentity, "VssSnapshotNative");
            var before = names.Select(name => SnapshotInfo(name)).ToArray();
            void CheckSnapshotContent(string name)
            {
                token.ThrowIfCancellationRequested();
                using var file = new FileStream(snapshotData + "\\" + name, FileMode.Open, FileAccess.Read, FileShare.Read);
                var prefix = new byte[32]; file.ReadExactly(prefix); Check(prefix.All(value => value == 0), "VssSnapshotNative");
            }
            foreach (string name in names) CheckSnapshotContent(name);
            Check(before.All(item => item.Identity.Volume == origin.Serial && item.Identity.High == 0 && item.Identity.Low >> 48 != 0 &&
                item.LinkCount == 1 && !item.WritableHandleOpen), "VssSnapshotNative");
            for (int i = 0; i < 3; i++)
            {
                token.ThrowIfCancellationRequested(); string path = Path.Combine(context.DataRoot, names[i]);
                using (var writer = NativeStorage.CreateFileW(path, 0x40000000 | 0x80, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                {
                    Check(!writer.IsInvalid && BoundIdentity(writer, directory: false) == before[i].Identity &&
                        BoundIdentity(directoryPin, directory: true) == directoryIdentity, "VssLiveMutation");
                    using var file = new FileStream(writer, FileAccess.Write);
                    file.WriteByte((byte)(0x40 + i)); file.SetLength((i + 1) * 8192 + 4096); file.Flush(true);
                }
                Check(NativeStorage.TryRead(path, false, File.GetAttributes(path), out var changed, out _) && changed != null &&
                    changed.Identity == before[i].Identity && changed.LogicalBytes == before[i].LogicalBytes + 4096, "VssLiveMutation");
            }
            for (int i = 0; i < 3; i++) { Check(SnapshotInfo(names[i]) == before[i], "VssSnapshotNative"); CheckSnapshotContent(names[i]); }
            int rawCalls, rawRecords = 1; long rawBytes;
            using (var reader = session.OpenReader(token))
            {
                var facts = reader.Facts();
                Check(facts.Serial == origin.Serial && facts.Geometry is { SectorBytes: 512, ClusterBytes: 4096, RecordBytes: 1024 } &&
                    facts.Geometry.VolumeBytes == origin.Geometry.VolumeBytes, "VssRawGeometry");
                long start = checked(facts.Geometry.MftStartLcn * 4096);
                var mapped = NtfsMftMap.Parse(reader.Read(start, 1024, token), facts.Geometry, token);
                Check(mapped.Status == "Mapped" && mapped.Map != null, "VssRawMap");
                var bootstrap = mapped.Map!.PlanRecord(0, token);
                Check(bootstrap.Count == 1 && bootstrap[0].VolumeOffset == start && bootstrap[0].Length == 1024 && bootstrap[0].RecordOffset == 0,
                    "VssRawMap");
                for (int i = 0; i < 3; i++)
                {
                    var native = before[i]; ulong slot = native.Identity.Low & NtfsRecordDecoder.MaximumRecordNumber;
                    var raw = new byte[1024]; int copied = 0;
                    foreach (var slice in mapped.Map!.PlanRecord(slot, token))
                    {
                        Check(slice.RecordOffset == copied, "VssRawCompare"); var bytes = reader.Read(slice.VolumeOffset, slice.Length, token);
                        bytes.CopyTo(raw, copied); copied += bytes.Length;
                    }
                    Check(copied == 1024, "VssRawCompare");
                    var decoded = NtfsRecordDecoder.Decode(raw, 512, token: token, recordNumber: slot, clusterSize: 4096);
                    Check(decoded.Status == NtfsDecodeStatus.Decoded && decoded.Record != null, "VssRawCompare");
                    var record = decoded.Record!; var visible = record.Names.Where(name => name.Namespace != 2).ToArray();
                    Check(record.Reference == native.Identity.Low && record.BaseReference == 0 && !record.IsDirectory &&
                        record.LogicalBytes == native.LogicalBytes && record.AllocatedBytes == native.AllocatedBytes && record.LookupReasons == NtfsLookupReason.None &&
                        NtfsRawComparison.MatchesLinks(record, native.LinkCount) && visible.Length == 1 && visible[0].Name == names[i] &&
                        record.Names.All(name => name.ParentReference == parent.Identity.Low), "VssRawCompare");
                    rawRecords++;
                }
                for (int i = 0; i < 3; i++) Check(SnapshotInfo(names[i]) == before[i], "VssSnapshotNative");
                rawCalls = reader.ReadCalls; rawBytes = reader.RequestedBytes;
            }
            token.ThrowIfCancellationRequested();
            cleanup = session.Cleanup();
            Check(cleanup, "VssCleanup"); session.Dispose(); session = null;
            token.ThrowIfCancellationRequested();
            evidence = new("fixture-vss-v1", "SnapshotReadVerified", 1, 1, 3, rawRecords, rawCalls, rawBytes,
                true, true, true, true, true, true);
        }
        catch (OperationCanceledException) { code = "VssCancelled"; }
        catch (NtfsVssFixtureOpenException error) { code = error.Code; nativeError = error.NativeError; cleanup = error.CleanupConfirmed; }
        catch (NtfsVssProbeException error) { code = error.Code; nativeError = error.Error; }
        catch (NtfsFixtureGuardException error) { code = "VssFixtureGuard"; nativeError = error.NativeErrorCode; }
        catch { code = "VssUnexpected"; }
        finally
        {
            if (session != null) { cleanup = session.Cleanup(); session.Dispose(); }
            else if (context != null && !ownerAttempted) cleanup = true; // No requester was created, or it was already explicitly cleaned.
            try { context?.Dispose(); } catch { cleanup = false; }
            if (!cleanup) { if (code == "None") code = "VssCleanup"; evidence = null; }
        }
        if (summary != null)
        {
            var value = new { schema = 1, status = evidence != null && code == "None" ? "vss_verified" : "failed",
                stage = evidence != null && code == "None" ? "SnapshotReadVerified" : code, checks, failures = evidence != null && code == "None" ? 0 : 1,
                code, native_error = nativeError, cleanup_confirmed = cleanup, vss_evidence = evidence };
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
                using var file = new FileStream(summary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(bytes); file.Flush(true);
            }
            catch { Console.WriteLine("RESULT VSS failed"); return 1; }
        }
        Console.WriteLine("RESULT VSS " + (evidence != null && code == "None" ? "verified" : "failed"));
        return evidence != null && code == "None" ? 0 : 1;
    }

    [StructLayout(LayoutKind.Sequential)] private struct IdInfo { internal ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeInfo { internal uint Attributes, Tag; }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadIdentity(SafeFileHandle handle, int kind, out IdInfo value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadAttributes(SafeFileHandle handle, int kind, out AttributeInfo value, uint size);
    private static FileIdentity BoundIdentity(SafeFileHandle handle, bool directory)
    {
        Require(ReadAttributes(handle, 9, out var attributes, 8) && (attributes.Attributes & (uint)FileAttributes.ReparsePoint) == 0 &&
            ((attributes.Attributes & (uint)FileAttributes.Directory) != 0) == directory, "VssFixtureCreate");
        Require(ReadIdentity(handle, 18, out var identity, 24) && identity.High == 0 && identity.Low != 0 && identity.Low >> 48 != 0, "VssFixtureCreate");
        return new(identity.Volume, identity.Low, identity.High);
    }


}
