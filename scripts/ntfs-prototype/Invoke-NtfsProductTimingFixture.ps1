#requires -Version 7.4
[CmdletBinding()]
param()

# The test window stays at ordinary integrity. Only the fixture owner and the
# product's own NTFS helper use the Windows administrator prompt.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$timer = Join-Path $repository 'tests/bin/Release/net10.0-windows/DiskAtlas.Tests.exe'
$artifacts = Join-Path $repository 'artifacts'
$ownerScript = Join-Path $PSScriptRoot 'Invoke-NtfsScale.ps1'
$tokenName = [guid]::NewGuid().ToString('N')
$result = Join-Path $artifacts ('ntfs-live-result-' + $tokenName + '.json')
$name = 'AsukaLens.Timing.' + $tokenName
$owner = $null; $window = $null; $pipe = $null
$stage = 'Validate'
. (Join-Path $PSScriptRoot 'ProductTimingFixturePipe.ps1')
. (Join-Path $PSScriptRoot 'KernelMatrixEvidence.ps1')
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'ORDINARY_PROCESS_REQUIRED' }
        $sid = $identity.User
    } finally { $identity.Dispose() }
    if (-not (Test-Path -LiteralPath $timer -PathType Leaf)) { throw 'TIMER_NOT_BUILT' }
    $space = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($artifacts))
    if ($space.AvailableFreeSpace -lt 2GB) { throw 'FIXTURE_SPACE' }
    Initialize-ProductTimingFixturePipe
    $acl = [IO.Pipes.PipeSecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($sid)
    $acl.AddAccessRule([IO.Pipes.PipeAccessRule]::new($sid, [IO.Pipes.PipeAccessRights]::FullControl,
        [Security.AccessControl.AccessControlType]::Allow))
    $pipe = [IO.Pipes.NamedPipeServerStreamAcl]::Create($name, [IO.Pipes.PipeDirection]::InOut, 1,
        [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous, 65536, 65536,
        $acl, [IO.HandleInheritability]::None, [IO.Pipes.PipeAccessRights]0)

    $stage = 'Provisioning'
    Write-Output 'PRODUCT_TIMING: Provisioning (administrator prompt)'
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.UseShellExecute = $true
    $start.Verb = 'runas'
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $ownerScript,
        '-ResultPath', $result, '-ProductTimingPipe', $name, '-ProductTimingParentId', [string]$PID)) {
        $start.ArgumentList.Add($argument)
    }
    $owner = [Diagnostics.Process]::Start($start)
    if ($null -eq $owner) { throw 'FIXTURE_OWNER_NOT_STARTED' }
    $connect = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(90))
    try { $pipe.WaitForConnectionAsync($connect.Token).GetAwaiter().GetResult() }
    finally { $connect.Dispose() }
    [uint32]$clientPid = 0
    if (-not [AsukaLensProductTimingPipe]::GetNamedPipeClientProcessId($pipe.SafePipeHandle, [ref]$clientPid) -or
        $clientPid -ne $owner.Id) { throw 'FIXTURE_OWNER_BINDING' }
    $ready = [AsukaLensProductTimingPipe]::Read($pipe, 300)
    Assert-UniqueJsonKeys $ready
    $fixture = $ready | ConvertFrom-Json
    if ($fixture.revision -cne 'product-timing-fixture-v1' -or $fixture.scope -isnot [string] -or
        $fixture.volume -isnot [string]) { throw 'FIXTURE_READY_INVALID' }
    Assert-ProductTimingScope $fixture.scope $fixture.volume

    $stage = 'Run'
    Write-Output 'PRODUCT_TIMING: Fixture verified; starting ordinary-privilege window and NTFS prompt'
    $startTimer = [Diagnostics.ProcessStartInfo]::new($timer)
    $startTimer.WorkingDirectory = $repository
    $startTimer.UseShellExecute = $false
    $startTimer.CreateNoWindow = $true
    $startTimer.RedirectStandardInput = $true
    $startTimer.RedirectStandardOutput = $true
    $startTimer.RedirectStandardError = $true
    $startTimer.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $startTimer.ArgumentList.Add('--ntfs-personal-timing-ui')
    $window = [Diagnostics.Process]::Start($startTimer)
    if ($null -eq $window) { throw 'TIMING_WINDOW_NOT_STARTED' }
    $output = $window.StandardOutput.ReadToEndAsync()
    $errors = $window.StandardError.ReadToEndAsync()
    $window.StandardInput.WriteLine('FO')
    $window.StandardInput.WriteLine($fixture.scope)
    $window.StandardInput.Close()
    if (-not $window.WaitForExit(1320000)) { throw 'TIMING_WINDOW_TIMEOUT' }
    $report = $output.GetAwaiter().GetResult()
    # Error text can contain local paths. Drain the stream without exporting it.
    [void]$errors.GetAwaiter().GetResult()
    Assert-ProductTimingScope $fixture.scope $fixture.volume

    $stage = 'Cleanup'
    Write-Output 'PRODUCT_TIMING: Measuring window exited; checking snapshot and detaching fixture'
    $reply = [ordered]@{ revision='product-timing-fixture-v1'; exit_code=$window.ExitCode; report=$report }
    [AsukaLensProductTimingPipe]::Write($pipe, ($reply | ConvertTo-Json -Compress), 10)
    if (-not $owner.WaitForExit(120000)) { throw 'FIXTURE_CLEANUP_TIMEOUT' }
    if (-not (Test-Path -LiteralPath $result -PathType Leaf) -or (Get-Item -LiteralPath $result).Length -gt 65536) {
        throw 'FIXTURE_RESULT_MISSING'
    }
    $resultText = Get-Content -LiteralPath $result -Raw -Encoding utf8
    Assert-UniqueJsonKeys $resultText
    $summary = $resultText | ConvertFrom-Json
    if ($summary.mode -cne 'product-timing' -or $summary.schema -ne 1) { throw 'FIXTURE_RESULT_INVALID' }
    Write-Output $resultText
    if ($owner.ExitCode -eq 0 -and (!$summary.detached -or !$summary.cleanup_completed -or
        !$summary.snapshot_cleanup_confirmed -or $summary.status -cne 'product_timing_compared')) { throw 'FIXTURE_RESULT_CONTRADICTION' }
    exit $owner.ExitCode
} catch {
    # Dispose connections, never force-stop a process which may own a snapshot.
    [ordered]@{mode='product-timing';status='driver_failed';stage=$stage;owner_started=($null -ne $owner);
        fixture_result_available=(Test-Path -LiteralPath $result -PathType Leaf)} | ConvertTo-Json -Compress | Write-Output
    exit 1
} finally {
    if ($null -ne $pipe) { $pipe.Dispose() }
    if ($null -ne $window) { $window.Dispose() }
    if ($null -ne $owner) { $owner.Dispose() }
}
