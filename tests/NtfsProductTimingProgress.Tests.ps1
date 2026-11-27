#requires -Version 7.4
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../scripts/ntfs-prototype/ProductTimingProgress.ps1')

$checks = 0
function Check([bool]$Passed, [string]$Name) {
    if (-not $Passed) { throw "FAIL product timing progress: $Name" }
    $script:checks++
}

function Read-SyntheticReport([string]$InputText, [string]$Order) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($InputText)
    $stream = [IO.MemoryStream]::new($bytes)
    $reader = [IO.StreamReader]::new($stream)
    $report = [Text.StringBuilder]::new()
    try {
        $messages = @(Read-ProductTimingReport -Reader $reader -Order $Order -Report $report -Cancellation ([Threading.CancellationToken]::None))
        return @{ Messages=$messages; Text=$report.ToString() }
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

$fastStage = '{"revision":"personal-ntfs-product-timing-v1","kind":"stage","mode":"F","stage":"Bridge_LaunchStarting"}'
$ordinaryStage = '{"revision":"personal-ntfs-product-timing-v1","kind":"stage","mode":"O","stage":"Ordinary_Start"}'
$fastSample = '{"revision":"personal-ntfs-product-timing-v1","kind":"sample","mode":"F","total_ms":1}'
$ordinarySample = '{"revision":"personal-ntfs-product-timing-v1","kind":"sample","mode":"O","total_ms":1}'
$launchReturned = $fastStage.Replace('Bridge_LaunchStarting','Bridge_LaunchReturned')
$oneOfTwo = 'PRODUCT_TIMING: Fast scan 1/2 requesting administrator permission'
$oneOfFour = 'PRODUCT_TIMING: Fast scan 1/4 requesting administrator permission'
$fourOfFour = 'PRODUCT_TIMING: Fast scan 4/4 requesting administrator permission'

# Use both LF and CRLF, including a final record without a newline. The
# evidence parser receives the original text, independent of its progress view.
$group = (@($fastStage,$launchReturned,$fastSample,$ordinaryStage,$ordinarySample) -join "`r`n") + "`n" +
    (@($ordinaryStage,$ordinarySample,$fastStage) -join "`n") + "`n" + $launchReturned
$result = Read-SyntheticReport $group FOOF
Check ($result.Messages.Count -eq 2) 'one notice per fast scan'
Check ($result.Messages[0] -ceq $oneOfFour -and $result.Messages[1] -ceq $fourOfFour) 'FOOF round numbers'
Check ($result.Text -ceq $group) 'complete report including line endings'

$pair = "$fastStage`r`n$fastSample`r`n$ordinaryStage`r`n$ordinarySample`r`n"
$result = Read-SyntheticReport $pair FO
Check ($result.Messages.Count -eq 1 -and $result.Messages[0] -ceq $oneOfTwo) 'FO round number'
Check ($result.Text -ceq $pair) 'final CRLF retained'

$repeated = "$fastStage`n$fastStage`n$launchReturned`n$fastStage`n"
$result = Read-SyntheticReport $repeated FOOF
Check ($result.Messages.Count -eq 1 -and $result.Messages[0] -ceq $oneOfFour) 'repeated fast event never starts a fourth round'
Check ($result.Text -ceq $repeated) 'duplicate events retained for evidence validation'

$ignored = @(
    $fastStage.Replace('LaunchStarting','UnknownPhase'),
    $fastStage.Replace('v1','v2'),
    $fastStage.Replace('"}','","extra":"synthetic private text"}'),
    $fastStage.Replace('"mode":"F"','"mode":"O"'),
    '{"revision":"personal-ntfs-timing-v2","kind":"failed","code":"PermissionDenied"}'
) -join "`n"
$result = Read-SyntheticReport $ignored FOOF
Check ($result.Messages.Count -eq 0) 'unknown, malformed or failure records never become a launch notice'
Check ($result.Text -ceq $ignored) 'unprojected records preserved'

$split = ('x' * 4080) + "`n" + $fastStage + "`n"
$result = Read-SyntheticReport $split FO
Check ($result.Messages.Count -eq 1 -and $result.Messages[0] -ceq $oneOfTwo -and $result.Text -ceq $split) 'launch record across 4096-character buffer boundary'

foreach ($payload in @(('a' * 262145), ('测' * 87382))) {
    $failed = $false
    try { Read-SyntheticReport $payload FO | Out-Null } catch { $failed = $true }
    Check $failed 'reject an oversized UTF-8 report without a newline'
}

# A child writes the first stage, then cannot reach the second line or exit
# until the parent has seen the notice and sends its acknowledgement over stdin.
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
foreach ($argument in @('-NoProfile','-NonInteractive','-Command', @'
[Console]::WriteLine('{"revision":"personal-ntfs-product-timing-v1","kind":"stage","mode":"F","stage":"Bridge_LaunchStarting"}')
[Console]::Out.Flush()
$inputReader = [IO.StreamReader]::new([Console]::OpenStandardInput())
$ack = $inputReader.ReadLineAsync()
if (-not $ack.Wait(5000) -or $ack.Result -ne 'ack') { exit 1 }
[Console]::WriteLine('finished-after-notice')
'@)) { $start.ArgumentList.Add($argument) }
$child = [Diagnostics.Process]::Start($start)
$timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(12))
try {
    $report = [Text.StringBuilder]::new()
    $received = [Collections.Generic.List[string]]::new()
    Read-ProductTimingReport -Reader $child.StandardOutput -Order FO -Report $report -Cancellation $timeout.Token | ForEach-Object {
        Check (-not $child.HasExited -and $_ -ceq $oneOfTwo) 'notice delivered while child still awaiting acknowledgement'
        $received.Add($_)
        $child.StandardInput.WriteLine('ack')
        $child.StandardInput.Close()
    }
    Check ($received.Count -eq 1 -and $child.WaitForExit(3000) -and $child.ExitCode -eq 0) 'child completed acknowledged exchange'
    Check ($report.ToString() -ceq ($fastStage + [Environment]::NewLine + 'finished-after-notice' + [Environment]::NewLine)) 'stream continues after notice'
} finally {
    $child.StandardInput.Close()
    if (-not $child.WaitForExit(3000)) { $child.Kill(); $child.WaitForExit() }
    $child.Dispose()
    $timeout.Dispose()
}

# Cancellation must interrupt a blocked read even when no child emits a line.
$name = 'AsukaLens.Progress.Test.' + [guid]::NewGuid().ToString('N')
$server = [IO.Pipes.NamedPipeServerStream]::new($name, [IO.Pipes.PipeDirection]::In, 1,
    [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
$client = [IO.Pipes.NamedPipeClientStream]::new('.', $name, [IO.Pipes.PipeDirection]::Out)
$timeout = [Threading.CancellationTokenSource]::new()
$watchdog = [Threading.CancellationTokenSource]::new()
$writerClose = $watchdog.Token.Register([Action]$client.Dispose)
$reader = $null
try {
    $client.Connect(3000)
    $server.WaitForConnection()
    $reader = [IO.StreamReader]::new($server)
    $watchdog.CancelAfter(5000)
    $timeout.CancelAfter(200)
    $failed = $false
    try { Read-ProductTimingReport -Reader $reader -Order FO -Report ([Text.StringBuilder]::new()) -Cancellation $timeout.Token | Out-Null }
    catch { $failed = $true }
    Check ($failed -and $timeout.IsCancellationRequested -and -not $watchdog.IsCancellationRequested) 'silent stream honours caller timeout'
} finally {
    $writerClose.Dispose()
    $watchdog.Dispose()
    $timeout.Dispose()
    if ($null -ne $reader) { $reader.Dispose() }
    $client.Dispose()
    $server.Dispose()
}

# Redirected process stdout on Windows may use a synchronous anonymous pipe.
# The writer stays alive and silent after its ready marker, so EOF cannot be
# mistaken for an interrupted read. It also exits on its own within 4 seconds.
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
foreach ($argument in @('-NoProfile','-NonInteractive','-Command',
    "[Console]::WriteLine('ready'); [Console]::Out.Flush(); [Threading.Thread]::Sleep(4000)")) {
    $start.ArgumentList.Add($argument)
}
$child = [Diagnostics.Process]::Start($start)
$timeout = [Threading.CancellationTokenSource]::new()
try {
    if ($child.StandardOutput.ReadLine() -cne 'ready') { throw 'Missing test ready signal' }
    $timeout.CancelAfter(200)
    $failed = $false
    try { Read-ProductTimingReport -Reader $child.StandardOutput -Order FO -Report ([Text.StringBuilder]::new()) -Cancellation $timeout.Token | Out-Null }
    catch { $failed = $true }
    Check ($failed -and $timeout.IsCancellationRequested -and -not $child.HasExited) 'deadline returns while redirected child pipe still silent and open'
} finally {
    if (-not $child.HasExited) { $child.Kill() }
    $child.WaitForExit()
    $child.Dispose()
    $timeout.Dispose()
}

Write-Output "PASS product timing progress checks: $checks"
