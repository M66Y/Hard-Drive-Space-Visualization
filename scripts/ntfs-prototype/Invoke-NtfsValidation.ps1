#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$StatePath,
    [Parameter(Mandatory)][string]$SmallResult,
    [Parameter(Mandatory)][string]$LargeResult,
    [Parameter(Mandatory)][string]$SnapshotResult)

# One administrator launch, three separate child processes. The children retain
# all ownership, format, budget, timeout and cleanup gates of the isolated runner.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifacts=[IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
. (Join-Path $PSScriptRoot 'KernelMatrixEvidence.ps1')
function SafePath([string]$Path,[string]$Pattern) {
    $full=[IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($full) -cne $artifacts -or [IO.Path]::GetFileName($full) -cnotmatch $Pattern) { throw 'VALIDATION_PATH' }
    $current=$full
    while($current) {
        if(Test-Path -LiteralPath $current) {
            if((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'VALIDATION_REPARSE' }
        }
        $next=[IO.Path]::GetDirectoryName($current)
        if($next -ceq $current){break}; $current=$next
    }
    return $full
}
$StatePath=SafePath $StatePath '^ntfs-validation-result-[0-9a-f]{32}\.json$'
$SmallResult=SafePath $SmallResult '^ntfs-live-result-[0-9a-f]{32}\.json$'
$LargeResult=SafePath $LargeResult '^ntfs-live-result-[0-9a-f]{32}\.json$'
$SnapshotResult=SafePath $SnapshotResult '^ntfs-live-result-[0-9a-f]{32}\.json$'
$all=@($StatePath,$SmallResult,$LargeResult,$SnapshotResult)
if(@($all|Sort-Object -Unique).Count -ne 4){throw 'VALIDATION_DUPLICATE_PATH'}
foreach($path in $all){if(Test-Path -LiteralPath $path){throw 'VALIDATION_RESULT_EXISTS'}}
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'ADMINISTRATOR_REQUIRED'}
$pwsh=(Get-Command pwsh.exe -CommandType Application -ErrorAction Stop).Source
$runner=Join-Path $PSScriptRoot 'Invoke-NtfsScale.ps1'
$phase='Scale10000';$status='running';$code='None'
$reservation=[IO.File]::Open($StatePath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
$reservation.Dispose()
function State {
    [void](SafePath $StatePath '^ntfs-validation-result-[0-9a-f]{32}\.json$')
    $temp=$StatePath+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [IO.File]::WriteAllText($temp,([ordered]@{schema=1;status=$status;phase=$phase;code=$code}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temp,$StatePath,$true)
    } finally {if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Force}}
}
function Completed([string]$Path,[string]$Expected) {
    $full=SafePath $Path '^ntfs-live-result-[0-9a-f]{32}\.json$'
    $file=Get-Item -LiteralPath $full -Force
    if($file.PSIsContainer -or $file.Length -le 0 -or $file.Length -gt 65536){throw 'CONTROLLED_VALIDATION_FAILED'}
    $text=Get-Content -LiteralPath $full -Raw -Encoding utf8
    Assert-UniqueJsonKeys $text
    $value=$text|ConvertFrom-Json
    $expectedMode=if($Expected -ceq 'vss_verified'){'fixture-vss'}else{'fixture-scale'}
    if($value.schema -ne 1 -or $value.code -cne 'None' -or $value.exit_code -ne 0 -or $value.mode -cne $expectedMode -or
        $value.status -cne $Expected -or $value.detached -isnot [bool] -or -not $value.detached -or
        $value.cleanup_completed -isnot [bool] -or -not $value.cleanup_completed){throw 'CONTROLLED_VALIDATION_FAILED'}
    if($Expected -ceq 'vss_verified' -and
        ($value.snapshot_cleanup_confirmed -isnot [bool] -or -not $value.snapshot_cleanup_confirmed)) {throw 'CONTROLLED_VALIDATION_FAILED'}
}
try {
    State
    & $pwsh -NoProfile -File $runner -ResultPath $SmallResult -FileCount 10000
    if($LASTEXITCODE -ne 0){throw 'CONTROLLED_VALIDATION_FAILED'}
    Completed $SmallResult 'scale_benchmarked'
    $phase='Scale100000';State
    & $pwsh -NoProfile -File $runner -ResultPath $LargeResult -FileCount 100000
    if($LASTEXITCODE -ne 0){throw 'CONTROLLED_VALIDATION_FAILED'}
    Completed $LargeResult 'scale_benchmarked'
    $phase='Snapshot';State
    & $pwsh -NoProfile -File $runner -ResultPath $SnapshotResult -Snapshot
    if($LASTEXITCODE -ne 0){throw 'CONTROLLED_VALIDATION_FAILED'}
    Completed $SnapshotResult 'vss_verified'
    $phase='Complete';$status='passed'
} catch {$status='failed';$code='ControlledValidationFailed'}
finally {State}
Write-Output ('RESULT: '+$status)
if($status -cne 'passed'){exit 1}
