namespace DiskAtlas.NtfsPrototype;

internal sealed class NtfsVssProbeException(string code, int error = 0) : IOException(code)
{ internal string Code { get; } = code; internal int Error { get; } = error; }
