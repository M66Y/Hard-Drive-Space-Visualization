using System.Buffers.Binary;
using System.Security;

namespace DiskAtlas;

internal static class DiskActivityResolverTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Disk activity resolver check failed: " + name);
            passed++; Console.WriteLine("PASS disk activity resolver " + name);
        }
        byte[] Extent(uint count = 1, uint disk = 2, long start = 1048576, long length = 268435456)
        {
            byte[] bytes = new byte[32];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, count);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), disk);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), start);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), length);
            return bytes;
        }
        bool ExtentAccepted(byte[] bytes, int? returned = null) =>
            DiskActivityResolver.TryReadSingleExtent(bytes, returned ?? bytes.Length, out _);
        Check(DiskActivityResolver.TryReadSingleExtent(Extent(), 32, out int disk) && disk == 2,
            "aligned single extent supplies the physical disk number");
        Check(DiskActivityResolver.TryReadSingleExtent(Extent(disk: 0, start: 0, length: 1), 32, out disk) && disk == 0,
            "zero disk and zero starting offset are valid");
        foreach (uint count in new uint[] { 0, 2, uint.MaxValue })
            Check(!ExtentAccepted(Extent(count: count)), "ambiguous or missing extents are rejected");
        foreach (int returned in new[] { -1, 0, 8, 31, 33, int.MaxValue })
            Check(!ExtentAccepted(Extent(), returned), "invalid native extent length is rejected");
        Check(!ExtentAccepted(new byte[31], 32), "short extent buffer cannot be read");
        Check(!ExtentAccepted(Extent(disk: uint.MaxValue)), "unrepresentable disk number is rejected");
        Check(!ExtentAccepted(Extent(start: -1)), "negative starting offset is rejected");
        Check(!ExtentAccepted(Extent(length: 0)) && !ExtentAccepted(Extent(length: -1)), "nonpositive extent length is rejected");
        Check(!ExtentAccepted(Extent(start: long.MaxValue, length: 1)), "extent end overflow is rejected");
        Check(ExtentAccepted(Extent(start: long.MaxValue - 1, length: 1)), "largest valid extent end stays representable");
        Check(!DiskActivityResolver.TryReadSingleExtent(Extent(count: 2), 32, out disk) && disk == -1,
            "failed extent parsing leaves no guessed disk number");

        byte[] Descriptor(uint bus = 17, int size = 40)
        {
            var bytes = new byte[size];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 40);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), bus);
            return bytes;
        }
        bool DescriptorAccepted(byte[] bytes, int? returned = null) =>
            DiskActivityResolver.IsSupportedDeviceDescriptor(bytes, returned ?? bytes.Length);
        Check(DescriptorAccepted(Descriptor()), "fixed NVMe direct-access descriptor is supported");
        foreach (uint bus in new uint[] { 0, 9, 14, 15, 16, 20, 127, uint.MaxValue })
            Check(!DescriptorAccepted(Descriptor(bus)), "unknown remote or virtual bus is rejected");
        foreach (uint bus in new uint[] { 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 17, 18, 19 })
            Check(DescriptorAccepted(Descriptor(bus)), "known fixed local bus is accepted");
        var removable = Descriptor(); removable[10] = 1;
        Check(!DescriptorAccepted(removable), "removable media are rejected");
        var optical = Descriptor(); optical[8] = 5;
        Check(!DescriptorAccepted(optical), "non-disk device types are rejected");
        foreach (int returned in new[] { -1, 0, 7, 35, 39, 41, int.MaxValue })
            Check(!DescriptorAccepted(Descriptor(), returned), "invalid or truncated descriptor result is rejected");
        var wrongVersion = Descriptor(); BinaryPrimitives.WriteUInt32LittleEndian(wrongVersion, 0);
        Check(!DescriptorAccepted(wrongVersion), "missing descriptor version is rejected");
        var futureVersion = Descriptor(); BinaryPrimitives.WriteUInt32LittleEndian(futureVersion, 41);
        Check(!DescriptorAccepted(futureVersion), "descriptor version cannot exceed its declared size");
        var oversized = Descriptor(); BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(4), 65537);
        Check(!DiskActivityResolver.TryReadDescriptorSize(oversized, 8, out _), "descriptor allocation is bounded");
        var shortHeader = new byte[7];
        Check(!DiskActivityResolver.TryReadDescriptorSize(shortHeader, 7, out _), "truncated descriptor header is rejected");
        var rawOverflow = Descriptor(); BinaryPrimitives.WriteUInt32LittleEndian(rawOverflow.AsSpan(32), 5);
        Check(!DescriptorAccepted(rawOverflow), "bus-specific raw data cannot extend beyond the descriptor");
        var rawFits = Descriptor(); BinaryPrimitives.WriteUInt32LittleEndian(rawFits.AsSpan(32), 4);
        Check(DescriptorAccepted(rawFits), "bounded bus-specific raw bytes need not be interpreted");

        foreach (string? path in new[] { null, "", " ", "relative", "C:relative", @"\rooted", @"\\server\fixture",
            @"\\?\UNC\server\fixture", @"\\.\PhysicalDrive0", @"\\?\GLOBALROOT\Device\fixture", "C:\\fixture\0" })
            Check(!DiskActivityResolver.IsLocalAbsolutePath(path) && DiskActivityResolver.Resolve(path!) == null,
                "unsupported input returns unavailable before any native lookup");
        foreach (string path in new[] { @"C:\", @"D:\fixture\folder", "D:/fixture/folder", @"\\?\D:\fixture" })
            Check(DiskActivityResolver.IsLocalAbsolutePath(path), "absolute local syntax is recognized without accessing it");
        const string guidPath = @"\\?\Volume{4f4ce85b-a857-4892-9c4f-c326c0aa60c6}\";
        Check(DiskActivityResolver.IsVolumeGuidPath(guidPath), "canonical volume GUID path is accepted");
        foreach (string value in new[] { @"C:\", guidPath[..^1], guidPath + "extra", guidPath.Replace("4f4ce85b", "notaguid") })
            Check(!DiskActivityResolver.IsVolumeGuidPath(value), "noncanonical volume identity is rejected");
        foreach (Exception error in new Exception[] { new IOException(), new UnauthorizedAccessException(), new ArgumentException(),
            new NotSupportedException(), new SecurityException(), new DllNotFoundException(), new EntryPointNotFoundException() })
            Check(DiskActivityResolver.IsExpectedReadFailure(error), "expected metadata failure can return unavailable");
        Check(!DiskActivityResolver.IsExpectedReadFailure(new OperationCanceledException()), "cancellation is not swallowed as unavailable");
        Check(!DiskActivityResolver.IsExpectedReadFailure(new InvalidOperationException()), "unexpected programming failure is not swallowed");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool resolveStopped = false, discoverStopped = false;
            try { DiskActivityResolver.Resolve("synthetic", cancelled.Token); }
            catch (OperationCanceledException) { resolveStopped = true; }
            try { DiskActivityResolver.Discover(cancelled.Token); }
            catch (OperationCanceledException) { discoverStopped = true; }
            Check(resolveStopped && discoverStopped, "pre-cancelled resolution and discovery perform no native work");
        }
        Check(new DiskActivityTarget(2, "磁盘 2").Label == "磁盘 2", "public target label contains only the disk number");
        Console.WriteLine("PASS disk activity resolver checks: " + passed + "; native drive mapping not exercised");
        return passed;
    }
}
