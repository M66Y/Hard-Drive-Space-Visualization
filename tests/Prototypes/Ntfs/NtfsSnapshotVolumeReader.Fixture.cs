namespace DiskAtlas.NtfsPrototype;

internal sealed partial class NtfsSnapshotVolumeReader
{
    internal static NtfsSnapshotVolumeReader OpenBound(NtfsVssFixtureOwner owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var binding = owner.TakeReadBinding();
        var native = new Native(binding.Device);
        var reader = new NtfsSnapshotVolumeReader(native, binding.Serial, binding.VolumeBytes, binding.Scale, false);
        try { owner.TrackReader(reader); token.ThrowIfCancellationRequested(); return reader; }
        catch { reader.Dispose(); throw; }
    }
}
