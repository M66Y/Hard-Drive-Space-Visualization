namespace DiskAtlas.NtfsPrototype;

// Offsets belong to the validated MFT data stream, never to a raw volume.
// The private source owner retains provenance and enforces cumulative I/O limits.
internal delegate int NtfsMftReadInto(long mftOffset, Memory<byte> destination, CancellationToken token);
