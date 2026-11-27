# Same-user, PID-bound transport between the ordinary timer and the elevated
# fixture owner. File paths never select an existing disk in the owner.
function Initialize-ProductTimingFixturePipe {
    if ('AsukaLensProductTimingPipe' -as [type]) { return }
    Add-Type -AssemblyName System.IO.Pipes.AccessControl
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
public static class AsukaLensProductTimingPipe {
    const int Limit = 1024 * 1024;
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder volume, uint capacity);

    public static bool DriveMatches(string root, string volume) {
        var actual = new StringBuilder(100);
        return GetVolumeNameForVolumeMountPoint(root, actual, (uint)actual.Capacity) &&
            String.Equals(actual.ToString(), volume, StringComparison.OrdinalIgnoreCase);
    }
    public static string Read(Stream stream, int timeout) {
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(timeout))) {
            var header = new byte[4];
            stream.ReadExactlyAsync(header.AsMemory(), stop.Token).AsTask().GetAwaiter().GetResult();
            int length = BitConverter.ToInt32(header, 0);
            if (length < 1 || length > Limit) throw new InvalidDataException("FIXTURE_PIPE_LENGTH");
            var bytes = new byte[length];
            stream.ReadExactlyAsync(bytes.AsMemory(), stop.Token).AsTask().GetAwaiter().GetResult();
            return Utf8.GetString(bytes);
        }
    }
    public static void Write(Stream stream, string value, int timeout) {
        var bytes = Utf8.GetBytes(value);
        if (bytes.Length < 1 || bytes.Length > Limit) throw new InvalidDataException("FIXTURE_PIPE_LENGTH");
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(timeout))) {
            var header = BitConverter.GetBytes(bytes.Length);
            stream.WriteAsync(header.AsMemory(), stop.Token).AsTask().GetAwaiter().GetResult();
            stream.WriteAsync(bytes.AsMemory(), stop.Token).AsTask().GetAwaiter().GetResult();
            stream.FlushAsync(stop.Token).GetAwaiter().GetResult();
        }
    }
}
'@
}

function Assert-ProductTimingScope([string]$Scope, [string]$Volume) {
    if ($Scope -cnotmatch '^[D-Z]:\\AsukaLensFixture-[0-9a-f]{32}$' -or
        $Volume -notmatch '^\\\\\?\\Volume\{[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\}\\$' -or
        -not [AsukaLensProductTimingPipe]::DriveMatches($Scope.Substring(0, 3), $Volume)) { throw 'PRODUCT_TIMING_SCOPE_BINDING' }
    $attributes = [IO.File]::GetAttributes($Scope)
    if (($attributes -band [IO.FileAttributes]::Directory) -eq 0 -or ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'PRODUCT_TIMING_SCOPE_TYPE'
    }
}
