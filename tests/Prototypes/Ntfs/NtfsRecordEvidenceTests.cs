using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DiskAtlas.NtfsPrototype;

// Uses deterministic synthetic bytes and this invocation's temporary artifacts only.
// No volume handles, personal paths, or original kernel records are read here.
internal static class NtfsRecordEvidenceTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("NTFS record evidence check failed: " + name);
            passed++;
            Console.WriteLine("PASS NTFS record evidence " + name);
        }
        void Rejected(Action action, string name)
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidDataException) { rejected = true; }
            catch (IOException) { rejected = true; }
            Check(rejected, name);
        }

        string temp = Path.GetFullPath(Path.GetTempPath());
        string owner = Path.Combine(temp, "AsukaLens-record-evidence-" + Guid.NewGuid().ToString("N"));
        string artifacts = Path.Combine(owner, "artifacts");
        Directory.CreateDirectory(artifacts);
        const ulong volume = 41;
        ulong reference = NtfsRecordFixtures.Reference(52, 3);
        ulong slot = reference & NtfsRecordDecoder.MaximumRecordNumber;
        byte[] protectedRecord = NtfsRecordFixtures.File();
        byte[] restoredRecord = (byte[])protectedRecord.Clone();
        Array.Copy(restoredRecord, 50, restoredRecord, 510, 2);
        Array.Copy(restoredRecord, 52, restoredRecord, 1022, 2);
        byte[] mismatchedRecord = (byte[])protectedRecord.Clone();
        NtfsRecordFixtures.U16(mismatchedRecord, 510, 0x1234);
        string Code(byte[] bytes) => NtfsRecordDecoder.DecodeKernelReturned(bytes, 512, slot, 4096, 3, 1).Code;
        string Capture(NtfsRecordEvidence evidence, string caseId, byte[] bytes, string? code = null) =>
            evidence.Capture(caseId, bytes, volume, reference, 512, 4096, 3, 1, code ?? Code(bytes));
        void ChildReplay(string directory, int expectedExit, string expectedSummary)
        {
            string host = Environment.ProcessPath ?? throw new InvalidOperationException("Evidence child host unavailable.");
            var start = new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(NtfsRecordEvidenceTests).Assembly.Location);
            start.ArgumentList.Add("--ntfs-record-replay");
            start.ArgumentList.Add(directory);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Evidence child did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit(10000);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); }
                finally { process.WaitForExit(2000); }
            }
            Check(exited, "independent replay process finishes within its time budget");
            Check(Task.WaitAll([output, errors], 2000), "independent replay redirected streams complete");
            // Never echo captured child output: only compare it with the fixed public contract.
            Check(process.ExitCode == expectedExit && errors.Result.Length == 0 && output.Result.Length < 256 &&
                output.Result.TrimEnd('\r', '\n') == expectedSummary,
                "independent replay returns the expected fixed summary and exit status");
        }

        try
        {
            Check(Code(protectedRecord) == "Decoded", "protected control has a strict expected result");
            Check(Code(restoredRecord) == "Decoded", "restored control has a strict expected result");
            Check(Code(mismatchedRecord) == "FixupMismatch", "mismatched tails stay a strict failure");
            var evidence = NtfsRecordEvidence.Create(artifacts);
            string root = evidence.Root;
            Check(Path.GetDirectoryName(root) == artifacts && Directory.Exists(root), "session is an owned direct child");
            Check(Path.GetFileName(root).StartsWith("ntfs-record-evidence-", StringComparison.Ordinal), "session has a recognizable bounded name");
            Check(Capture(evidence, "Protected", protectedRecord) == "Saved", "protected bytes are captured");
            Check(Capture(evidence, "Restored", restoredRecord) == "Saved", "restored bytes are captured");
            Check(Capture(evidence, "Mismatch", mismatchedRecord) == "Saved", "strict failure bytes can be captured without becoming success");
            var report = NtfsRecordEvidence.Replay(root);
            Check(report.Records == 3 && report.Matching == 3 && report.Mismatching == 0, "same-process disk reload reproduces all expected outcomes");
            ChildReplay(root, 0, "NTFS record replay: records=3; matching=3; mismatching=0");
            Check(protectedRecord.SequenceEqual(NtfsRecordFixtures.File()), "capture and replay leave caller bytes unchanged");

            string manifest = Path.Combine(root, "manifest.json");
            JsonObject document = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
            var records = document["Records"]!.AsArray();
            Check(records.Count == 3, "manifest contains only saved records");
            byte[][] originals = [protectedRecord, restoredRecord, mismatchedRecord];
            for (int i = 0; i < originals.Length; i++)
            {
                var entry = records[i]!.AsObject();
                string payload = entry["Payload"]!.GetValue<string>();
                byte[] saved = File.ReadAllBytes(Path.Combine(root, payload));
                Check(saved.SequenceEqual(originals[i]), "stored payload is byte exact");
                Check(entry["Length"]!.GetValue<int>() == saved.Length, "stored payload length matches the original");
                Check(string.Equals(entry["Sha256"]!.GetValue<string>(), Convert.ToHexString(SHA256.HashData(saved)), StringComparison.OrdinalIgnoreCase),
                    "manifest hash binds the exact original payload");
            }
            byte[] manifestBytes = File.ReadAllBytes(manifest);
            void AlterManifest(Action<JsonObject> change, Action assertion)
            {
                try
                {
                    var changed = JsonNode.Parse(manifestBytes)!.AsObject();
                    change(changed);
                    File.WriteAllText(manifest, changed.ToJsonString());
                    assertion();
                }
                finally { File.WriteAllBytes(manifest, manifestBytes); }
            }
            void RejectManifest(Action<JsonObject> change, string name) =>
                AlterManifest(change, () => Rejected(() => NtfsRecordEvidence.Replay(root), name));
            void RejectRawManifest(Func<string, string> change, string name)
            {
                try
                {
                    string compact = JsonNode.Parse(manifestBytes)!.ToJsonString();
                    File.WriteAllText(manifest, change(compact));
                    Rejected(() => NtfsRecordEvidence.Replay(root), name);
                }
                finally { File.WriteAllBytes(manifest, manifestBytes); }
            }
            RejectRawManifest(x => "{\"Schema\":1," + x[1..], "duplicate JSON properties are rejected");
            RejectRawManifest(x => "{\"schema\":1," + x[1..], "case-variant JSON properties are rejected");
            RejectRawManifest(x => x.Insert(x.IndexOf("\"CaseId\":", StringComparison.Ordinal), "\"CaseId\":\"Injected\","),
                "duplicate per-record JSON properties are rejected");
            RejectManifest(x => x["Injected"] = "not permitted", "unknown manifest fields are rejected");
            RejectManifest(x => x["Records"]![0]!["Injected"] = "not permitted", "unknown per-record fields are rejected");
            RejectManifest(x => x["Schema"] = 999, "unknown schema cannot be replayed");
            RejectManifest(x => x["Nonce"] = "not-the-owned-session", "manifest must match the owned session identity");
            RejectManifest(x => x["Records"]![0]!["Payload"] = "../outside.bin", "payload path traversal is rejected");
            RejectManifest(x => x["Records"]![0]!["Payload"] = Path.Combine(owner, "outside.bin"), "absolute payload paths are rejected");
            RejectManifest(x => x["Records"]![0]!["CaseId"] = "../Injected", "injected case paths are rejected at replay");
            RejectManifest(x => x["Records"]![1]!["CaseId"] = x["Records"]![0]!["CaseId"]!.GetValue<string>(), "duplicate case identities are rejected");
            RejectManifest(x => x["Records"]![1]!["Payload"] = x["Records"]![0]!["Payload"]!.GetValue<string>(), "duplicate payload references are rejected");
            RejectManifest(x => x["Records"]![0]!["Length"] = 65537, "declared oversized payload is rejected");
            RejectManifest(x => x["Records"]![0]!["Sha256"] = new string('0', 64), "hash tampering is rejected");
            RejectManifest(x => x["Records"]![0]!["ExpectedCode"] = "Injected\nMessage", "non-whitelisted expected result text is rejected");
            AlterManifest(x => x["Records"]![0]!["ExpectedCode"] = "FixupMismatch", () =>
            {
                var different = NtfsRecordEvidence.Replay(root);
                Check(different.Records == 3 && different.Matching == 2 && different.Mismatching == 1,
                    "a legal expected-code disagreement remains a visible mismatch");
                ChildReplay(root, 1, "NTFS record replay: records=3; matching=2; mismatching=1");
            });
            AlterManifest(x => x["Records"]![0]!["Reference"] = NtfsRecordFixtures.Reference(52, 4), () =>
            {
                var different = NtfsRecordEvidence.Replay(root);
                Check(different.Records == 3 && different.Matching == 2 && different.Mismatching == 1,
                    "decoded record sequence disagreement is a visible identity mismatch");
            });
            try
            {
                File.WriteAllBytes(manifest, new byte[512 * 1024 + 1]);
                Rejected(() => NtfsRecordEvidence.Replay(root), "oversized manifest is rejected before parsing");
            }
            finally { File.WriteAllBytes(manifest, manifestBytes); }

            string firstPayload = Path.Combine(root, records[0]!["Payload"]!.GetValue<string>());
            byte[] firstBytes = File.ReadAllBytes(firstPayload);
            try
            {
                byte[] altered = (byte[])firstBytes.Clone(); altered[100] ^= 1;
                File.WriteAllBytes(firstPayload, altered);
                Rejected(() => NtfsRecordEvidence.Replay(root), "payload modification is rejected by its hash");
                File.WriteAllBytes(firstPayload, new byte[65537]);
                Rejected(() => NtfsRecordEvidence.Replay(root), "actual oversized payload is rejected");
            }
            finally { File.WriteAllBytes(firstPayload, firstBytes); }
            foreach (string caseId in new[] { "", "../escape", "bad/name", "bad\\name", "bad:name", "bad\0name", new string('x', 41) })
                Rejected(() => Capture(evidence, caseId, protectedRecord), "invalid capture case identity is rejected");
            Rejected(() => Capture(evidence, "Protected", protectedRecord), "capture refuses an existing case identity");
            foreach (int length in new[] { 0, 512, 1025, 65537 })
                Rejected(() => Capture(evidence, "InvalidSize", new byte[length], "RecordLength"), "capture rejects out-of-contract payload size");
            Rejected(() => Capture(evidence, "BadCode", protectedRecord, "Injected\nCode"), "capture expected result is bounded controlled text");

            string unknown = Path.Combine(root, "unknown.tmp");
            File.WriteAllText(unknown, "synthetic interference");
            Rejected(() => NtfsRecordEvidence.Replay(root), "unexpected session files block replay");
            Rejected(evidence.DeleteOwned, "unknown files block owned deletion");
            Check(File.Exists(unknown) && File.Exists(firstPayload), "refused deletion preserves all session files");
            File.Delete(unknown);

            string targetSentinel = Path.Combine(owner, "symlink-target.bin");
            File.WriteAllBytes(targetSentinel, firstBytes);
            bool linkCreated = false;
            try
            {
                File.Delete(firstPayload);
                try { File.CreateSymbolicLink(firstPayload, targetSentinel); linkCreated = true; }
                catch (UnauthorizedAccessException) { Console.WriteLine("SKIP NTFS record evidence reparse checks: link capability unavailable"); }
                catch (IOException) { Console.WriteLine("SKIP NTFS record evidence reparse checks: link capability unavailable"); }
                if (linkCreated)
                {
                    Rejected(() => NtfsRecordEvidence.Replay(root), "a known payload name cannot redirect replay through a reparse entry");
                    Rejected(evidence.DeleteOwned, "a known payload reparse entry blocks owned deletion");
                    Check(File.ReadAllBytes(targetSentinel).SequenceEqual(firstBytes), "refused reparse deletion leaves its external target untouched");
                }
            }
            finally
            {
                if (linkCreated) File.Delete(firstPayload);
                File.WriteAllBytes(firstPayload, firstBytes);
            }

            for (int i = 4; i <= 5; i++)
                Check(Capture(evidence, "Limit" + i, protectedRecord) == "Saved", "capture accepts records within the six-record budget");
            Check(Capture(evidence, "MaximumSize", new byte[65536]) == "Saved", "exact 64 KiB payload endpoint is retained without truncation");
            Check(Capture(evidence, "Seventh", protectedRecord) == "LimitReached", "seventh capture is explicitly limited");
            var limited = NtfsRecordEvidence.Replay(root);
            Check(limited.Records == 6 && limited.Matching == 6 && limited.Mismatching == 0, "limit refusal does not alter the six retained records");
            Check(Directory.EnumerateFiles(root, "*.bin").Count() == 6, "limit refusal creates no extra binary sample");
            evidence.DeleteOwned();
            Check(!Directory.Exists(root) && Directory.Exists(artifacts), "owned cleanup removes its session and preserves the artifacts parent");

            // Legacy schema remains strict: only its original fields are accepted.
            var legacy = NtfsRecordEvidence.Create(artifacts);
            Check(Capture(legacy, "Legacy", protectedRecord) == "Saved", "legacy compatibility fixture is captured");
            string legacyPath = Path.Combine(legacy.Root, "manifest.json");
            var legacyJson = JsonNode.Parse(File.ReadAllText(legacyPath))!.AsObject();
            legacyJson["Schema"] = 1;
            foreach (var item in legacyJson["Records"]!.AsArray())
            {
                item!.AsObject().Remove("Source"); item.AsObject().Remove("Kernel");
            }
            File.WriteAllText(legacyPath, legacyJson.ToJsonString());
            Check(NtfsRecordEvidence.Replay(legacy.Root) is { Records: 1, Matching: 1, Mismatching: 0 }, "strict schema one remains replayable");
            ChildReplay(legacy.Root, 0, "NTFS record replay: records=1; matching=1; mismatching=0");
            legacyJson["Records"]![0]!["Kernel"] = null;
            File.WriteAllText(legacyPath, legacyJson.ToJsonString());
            Rejected(() => NtfsRecordEvidence.Replay(legacy.Root), "schema one does not accept schema two fields");
            legacyJson["Records"]![0]!.AsObject().Remove("Kernel");
            File.WriteAllText(legacyPath, legacyJson.ToJsonString());
            legacy.DeleteOwned();

            var kernelEvidence = NtfsRecordEvidence.Create(artifacts);
            var stable = new FileStorageInfo(new(volume, reference, 0), 4096, 1, false, 1024,
                DateTime.FromFileTimeUtc(10_000_000));
            byte[] Output(byte[] body, ulong? returnedReference = null)
            {
                var output = new byte[12 + body.Length];
                BinaryPrimitives.WriteUInt64LittleEndian(output, returnedReference ?? slot);
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), (uint)body.Length);
                body.CopyTo(output, 12);
                return output;
            }
            string Kernel(string id, byte[] output, int? returned = null, FileStorageInfo? before = null, FileStorageInfo? after = null,
                string? code = null) => kernelEvidence.CaptureKernel(id, output, returned ?? output.Length,
                    before ?? stable, after ?? stable, 512, 4096, 3, 1, code ?? "Decoded");
            byte[] kernelOutput = Output(protectedRecord), kernelOriginal = (byte[])kernelOutput.Clone();
            Check(Kernel("KernelProtected", kernelOutput) == "Saved", "kernel capture binds a stable external identity and header");
            Check(kernelOutput.SequenceEqual(kernelOriginal), "kernel capture does not mutate header or body");
            Check(File.ReadAllBytes(Path.Combine(kernelEvidence.Root, "record-001.bin")).SequenceEqual(protectedRecord),
                "kernel payload contains exactly the original body without the outer header");
            Check(Kernel("KernelStrictFailure", Output(mismatchedRecord), code: "FixupMismatch") == "Saved",
                "kernel strict failure is retained without relaxing decoding");
            Check(NtfsRecordEvidence.Replay(kernelEvidence.Root) is { Records: 2, Matching: 2, Mismatching: 0 },
                "kernel-bound protected and failure bodies reproduce their exact expected results");
            ChildReplay(kernelEvidence.Root, 0, "NTFS record replay: records=2; matching=2; mismatching=0");
            Rejected(() => Kernel("WrongSlot", Output(protectedRecord, slot + 1)), "kernel capture rejects another returned slot");
            Rejected(() => Kernel("NegativeCount", kernelOutput, -1), "kernel capture rejects a negative returned count");
            Rejected(() => Kernel("ShortHeader", kernelOutput, 11), "kernel capture rejects a truncated outer header");
            Rejected(() => Kernel("ShortBody", kernelOutput, kernelOutput.Length - 1), "kernel capture rejects a truncated declared body");
            Rejected(() => Kernel("LargeCount", kernelOutput, kernelOutput.Length + 1), "kernel capture rejects a returned count beyond its buffer");
            Rejected(() => Kernel("ChangedIdentity", kernelOutput, after: stable with { Identity = new(volume, reference + 1, 0) }), "kernel capture rejects identity changes");
            Rejected(() => Kernel("WideIdentity", kernelOutput, before: stable with { Identity = new(volume, reference, 1) },
                after: stable with { Identity = new(volume, reference, 1) }), "kernel capture rejects an unsupported high identity component");
            Rejected(() => Kernel("ChangedLogical", kernelOutput, after: stable with { LogicalBytes = 1025 }), "kernel capture rejects changing logical size");
            Rejected(() => Kernel("ChangedAllocation", kernelOutput, after: stable with { AllocatedBytes = 8192 }), "kernel capture rejects changing allocation");
            Rejected(() => Kernel("ChangedLinks", kernelOutput, after: stable with { LinkCount = 2 }), "kernel capture rejects changing link counts");
            Rejected(() => Kernel("NoLinks", kernelOutput, before: stable with { LinkCount = 0 }, after: stable with { LinkCount = 0 }), "kernel capture rejects zero link counts");
            Rejected(() => Kernel("ChangedTime", kernelOutput, after: stable with { Modified = DateTime.FromFileTimeUtc(20_000_000) }), "kernel capture rejects changing native timestamps");
            Rejected(() => Kernel("MissingTime", kernelOutput, before: stable with { Modified = null }), "kernel capture requires a native timestamp");
            Rejected(() => Kernel("Writer", kernelOutput, after: stable with { WritableHandleOpen = true }), "kernel capture rejects a writable observation");
            foreach (uint length in new uint[] { 0, 1023, 1025, 65537 })
            {
                byte[] invalid = (byte[])kernelOutput.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(8), length);
                Rejected(() => Kernel("InvalidHeaderLength", invalid), "kernel capture rejects invalid declared record lengths");
            }
            string kernelManifest = Path.Combine(kernelEvidence.Root, "manifest.json");
            byte[] kernelManifestBytes = File.ReadAllBytes(kernelManifest);
            var kernelDocument = JsonNode.Parse(kernelManifestBytes)!.AsObject();
            Check(kernelDocument["Schema"]!.GetValue<int>() == 2 &&
                kernelDocument["Records"]![0]!["Source"]!.GetValue<string>() == "KernelIoctl", "kernel metadata uses explicit schema two provenance");
            void RejectKernelManifest(Action<JsonObject> change, string name)
            {
                try
                {
                    var changed = JsonNode.Parse(kernelManifestBytes)!.AsObject();
                    change(changed);
                    File.WriteAllText(kernelManifest, changed.ToJsonString());
                    Rejected(() => NtfsRecordEvidence.Replay(kernelEvidence.Root), name);
                }
                finally { File.WriteAllBytes(kernelManifest, kernelManifestBytes); }
            }
            RejectKernelManifest(x => x["Records"]![0]!["Source"] = "Other", "replay rejects unknown capture provenance");
            RejectKernelManifest(x => x["Records"]![0]!["Kernel"] = null, "kernel provenance requires its bound metadata");
            RejectKernelManifest(x => x["Records"]![0]!["Kernel"]!["Injected"] = 1, "kernel binding is a strict field whitelist");
            RejectKernelManifest(x => x["Records"]![0]!["Kernel"]!["ReturnedReference"] = slot + 1, "replay rejects a header slot unrelated to the external identity");
            RejectKernelManifest(x => x["Records"]![0]!["Kernel"]!["ReturnedLength"] = 2048, "replay rejects a header length unrelated to saved bytes");
            RejectKernelManifest(x => x["Records"]![0]!["Kernel"]!["ReturnedBytes"] = 11, "replay rejects truncated returned boundaries");
            RejectKernelManifest(x => x["Records"]![0]!["Kernel"]!["LinkCount"] = 0, "replay rejects an invalid stable native link count");
            RejectKernelManifest(x => x["Records"]![0]!["Kernel"]!["WritableHandleOpen"] = true, "replay rejects a writable saved context");
            Check(Directory.EnumerateFiles(kernelEvidence.Root, "*.bin").Count() == 2, "rejected kernel captures create no additional samples");
            kernelEvidence.DeleteOwned();
            Check(!Directory.Exists(kernelEvidence.Root), "kernel-bound sessions retain the same owned cleanup boundary");
            return passed;
        }
        finally
        {
            // Every path in this flat synthetic test owner was created above. Never follow links.
            string full = Path.GetFullPath(owner);
            if (Path.GetDirectoryName(full) != temp.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ||
                !Path.GetFileName(full).StartsWith("AsukaLens-record-evidence-", StringComparison.Ordinal))
                throw new InvalidOperationException("NTFS record evidence cleanup boundary failed.");
            if (Directory.Exists(full))
            {
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) Directory.Delete(full);
                else DeleteTree(full);
            }
        }
    }

    private static void DeleteTree(string directory)
    {
        // Check the directory itself before enumeration; a changed root must never redirect cleanup.
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directory);
            return;
        }
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (isDirectory) Directory.Delete(entry); else File.Delete(entry);
            }
            else if (isDirectory) DeleteTree(entry);
            else File.Delete(entry);
        }
        Directory.Delete(directory);
    }
}
