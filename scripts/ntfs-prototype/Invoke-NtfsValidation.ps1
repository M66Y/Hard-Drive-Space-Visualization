#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$StatePath,
    [Parameter(Mandatory)][string]$SmallResult,
    [Parameter(Mandatory)][string]$LargeResult,
    [Parameter(Mandatory)][string]$SnapshotResult,
    [switch]$UseSnapshotScale,
    [string]$MetadataRoot,
    [string]$MetadataResult)

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
if ([bool]$MetadataRoot -ne [bool]$MetadataResult) { throw 'VALIDATION_METADATA_ARGUMENTS' }
if ($MetadataRoot) {
    if ($MetadataRoot -cnotmatch '^[A-Za-z]:\\$') { throw 'VALIDATION_METADATA_ROOT' }
    $MetadataResult=SafePath $MetadataResult '^ntfs-personal-metadata-[0-9a-f]{32}\.json$'
    $all+=$MetadataResult
}
if(@($all|Sort-Object -Unique).Count -ne $all.Count){throw 'VALIDATION_DUPLICATE_PATH'}
foreach($path in $all){if(Test-Path -LiteralPath $path){throw 'VALIDATION_RESULT_EXISTS'}}
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'ADMINISTRATOR_REQUIRED'}
$pwsh=(Get-Command pwsh.exe -CommandType Application -ErrorAction Stop).Source
$runner=Join-Path $PSScriptRoot 'Invoke-NtfsScale.ps1'
$testsDll=Join-Path $root 'tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll'
$dotnet=$null
if ($MetadataRoot) { $dotnet=(Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source }
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
    $expectedMode=switch -CaseSensitive ($Expected) { 'vss_verified' {'fixture-vss'}; 'scale_benchmarked' {'fixture-scale'};
        'vss_scale_benchmarked' {'fixture-vss-scale'}; default {throw 'CONTROLLED_VALIDATION_FAILED'} }
    if($value.schema -ne 1 -or $value.code -cne 'None' -or $value.exit_code -ne 0 -or $value.mode -cne $expectedMode -or
        $value.status -cne $Expected -or $value.detached -isnot [bool] -or -not $value.detached -or
        $value.cleanup_completed -isnot [bool] -or -not $value.cleanup_completed){throw 'CONTROLLED_VALIDATION_FAILED'}
    if($Expected -cin @('vss_verified','vss_scale_benchmarked') -and
        ($value.snapshot_cleanup_confirmed -isnot [bool] -or -not $value.snapshot_cleanup_confirmed)) {throw 'CONTROLLED_VALIDATION_FAILED'}
}
function MetadataEvidence($Value) {
    try {
        $fields=@('Revision','Status','Code','NativeError','FileSystem','OpenedWithZeroAccess','SectorBytes','ClusterBytes','RecordBytes','VolumeBytes','MftValidDataBytes','MftSlots')
        if ($Value -isnot [pscustomobject]) { throw 'VALIDATION_METADATA_EVIDENCE' }
        $actual=@($Value.PSObject.Properties)
        if ($actual.Count -ne 12 -or (Compare-Object $fields @($actual.Name) -CaseSensitive) -or
            @($actual|Where-Object MemberType -ne NoteProperty).Count) { throw 'VALIDATION_METADATA_EVIDENCE' }
        if ($Value.Revision -isnot [string] -or $Value.Revision -cne 'volume-preflight-v1' -or
            $Value.Status -isnot [string] -or $Value.Code -isnot [string] -or $Value.FileSystem -isnot [string] -or
            $Value.FileSystem -cnotin @('NTFS','ReFS','FAT','FAT32','exFAT','UDF','CDFS','Unknown') -or
            ($Value.NativeError -isnot [int] -and $Value.NativeError -isnot [long]) -or
            $Value.NativeError -lt [int]::MinValue -or $Value.NativeError -gt [int]::MaxValue -or
            $Value.OpenedWithZeroAccess -isnot [bool] -or $Value.OpenedWithZeroAccess) { throw 'VALIDATION_METADATA_EVIDENCE' }
        $geometry=@('SectorBytes','ClusterBytes','RecordBytes','VolumeBytes','MftValidDataBytes','MftSlots')
        if ($Value.Status -ceq 'MetadataAvailable') {
            if ($Value.Code -cne 'None' -or $Value.NativeError -ne 0 -or $Value.FileSystem -cne 'NTFS') { throw 'VALIDATION_METADATA_EVIDENCE' }
            foreach ($field in $geometry) {
                if (($Value.$field -isnot [int] -and $Value.$field -isnot [long]) -or $Value.$field -le 0) { throw 'VALIDATION_METADATA_EVIDENCE' }
            }
            foreach ($field in @('SectorBytes','ClusterBytes','RecordBytes')) {
                if (($Value.$field -band ($Value.$field-1)) -ne 0) { throw 'VALIDATION_METADATA_EVIDENCE' }
            }
            [long]$remainder=0
            [long]$slots=[Math]::DivRem([long]$Value.MftValidDataBytes,[long]$Value.RecordBytes,[ref]$remainder)
            if ($Value.SectorBytes -lt 512 -or $Value.SectorBytes -gt 65536 -or $Value.ClusterBytes -lt $Value.SectorBytes -or
                $Value.ClusterBytes -gt 2097152 -or $Value.RecordBytes -lt $Value.SectorBytes -or $Value.RecordBytes -gt 65536 -or
                $Value.VolumeBytes -lt $Value.ClusterBytes -or $Value.VolumeBytes%$Value.SectorBytes -ne 0 -or
                $Value.MftValidDataBytes -gt $Value.VolumeBytes -or $Value.MftValidDataBytes%$Value.RecordBytes -ne 0 -or
                $Value.MftSlots -ne $slots) { throw 'VALIDATION_METADATA_EVIDENCE' }
        } else {
            foreach ($field in $geometry) { if ($null -ne $Value.$field) { throw 'VALIDATION_METADATA_EVIDENCE' } }
            if ($Value.Status -ceq 'Unavailable') {
                if ($Value.Code -cnotin @('PreflightCancelled','PreflightNative','PreflightFormat','PreflightOpen','PreflightQuery')) { throw 'VALIDATION_METADATA_EVIDENCE' }
                if ($Value.Code -cin @('PreflightFormat','PreflightCancelled') -and $Value.FileSystem -cne 'Unknown') { throw 'VALIDATION_METADATA_EVIDENCE' }
                if ($Value.Code -cin @('PreflightOpen','PreflightQuery') -and $Value.FileSystem -cne 'NTFS') { throw 'VALIDATION_METADATA_EVIDENCE' }
                if ($Value.Code -ceq 'PreflightNative' -and $Value.FileSystem -cnotin @('Unknown','NTFS')) { throw 'VALIDATION_METADATA_EVIDENCE' }
                if ($Value.Code -cin @('PreflightNative','PreflightCancelled') -and $Value.NativeError -ne 0) { throw 'VALIDATION_METADATA_EVIDENCE' }
            } elseif ($Value.Status -ceq 'Unsupported') {
                if ($Value.Code -cne 'PreflightNotNtfs' -or $Value.FileSystem -ceq 'NTFS' -or $Value.NativeError -ne 0) { throw 'VALIDATION_METADATA_EVIDENCE' }
            } elseif ($Value.Status -ceq 'Invalid') {
                if ($Value.Code -cnotin @('PreflightRoot','PreflightPayload','PreflightGeometry','PreflightMftLength') -or $Value.NativeError -ne 0 -or
                    ($Value.Code -ceq 'PreflightRoot' -and $Value.FileSystem -cne 'Unknown') -or
                    ($Value.Code -cne 'PreflightRoot' -and $Value.FileSystem -cne 'NTFS')) { throw 'VALIDATION_METADATA_EVIDENCE' }
            } else { throw 'VALIDATION_METADATA_EVIDENCE' }
        }
        $safe=[ordered]@{}
        foreach ($field in $fields) { $safe[$field]=$Value.$field }
        return $safe
    } catch { throw 'VALIDATION_METADATA_EVIDENCE' }
}
function ReadMetadata {
    $process=[Diagnostics.Process]::new();$started=$false;$discard=$null
    try {
        $process.StartInfo=[Diagnostics.ProcessStartInfo]::new($dotnet)
        $process.StartInfo.UseShellExecute=$false;$process.StartInfo.CreateNoWindow=$true
        $process.StartInfo.RedirectStandardOutput=$true;$process.StartInfo.RedirectStandardError=$true
        foreach ($argument in @($testsDll,'--ntfs-volume-preflight',$MetadataRoot,'--read-metadata-handle')) { $process.StartInfo.ArgumentList.Add($argument) }
        $started=$process.Start()
        if (-not $started) { throw 'VALIDATION_METADATA_PROCESS' }
        # Drain stderr straight to the null sink; never retain it or copy it to a report.
        $discard=$process.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
        $watch=[Diagnostics.Stopwatch]::StartNew();$bytes=[byte[]]::new(65537);$used=0
        while ($used -lt $bytes.Length) {
            $read=$process.StandardOutput.BaseStream.ReadAsync($bytes,$used,$bytes.Length-$used)
            $remaining=[int][Math]::Max(0,20000-$watch.ElapsedMilliseconds)
            if (-not $read.Wait($remaining)) { throw 'VALIDATION_METADATA_TIMEOUT' }
            $count=$read.GetAwaiter().GetResult()
            if ($count -eq 0) { break };$used+=$count
        }
        if ($used -eq 0 -or $used -gt 65536 -or -not $process.WaitForExit([int][Math]::Max(0,20000-$watch.ElapsedMilliseconds))) { throw 'VALIDATION_METADATA_PROCESS' }
        $null=$discard.GetAwaiter().GetResult()
        $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes,0,$used)
        Assert-UniqueJsonKeys $text
        $safe=MetadataEvidence ($text|ConvertFrom-Json)
        $expectedExit=if($safe.Status -ceq 'MetadataAvailable'){0}elseif($safe.Status -cin @('Unavailable','Unsupported')){2}else{1}
        if ($process.ExitCode -ne $expectedExit) { throw 'VALIDATION_METADATA_PROCESS' }
        return $safe
    } catch { throw 'VALIDATION_METADATA_PROCESS' }
    finally {
        if ($started -and -not $process.HasExited) {
            try {
                $process.Kill($true)
                if (-not $process.WaitForExit(5000)) { throw 'VALIDATION_METADATA_PROCESS' }
            } catch { throw 'VALIDATION_METADATA_PROCESS' }
        }
        $process.Dispose()
    }
}
try {
    if ($MetadataRoot) {
        $phase='Metadata';State
        $privateReport=ReadMetadata
        [void](SafePath $MetadataResult '^ntfs-personal-metadata-[0-9a-f]{32}\.json$')
        $privateBytes=[Text.UTF8Encoding]::new($false).GetBytes(($privateReport|ConvertTo-Json -Depth 4))
        $privateStream=[IO.File]::Open($MetadataResult,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $privateStream.Write($privateBytes);$privateStream.Flush($true) } finally { $privateStream.Dispose() }
    }
    $phase=if($UseSnapshotScale){'SnapshotScale10000'}else{'Scale10000'}
    State
    if($UseSnapshotScale){& $pwsh -NoProfile -File $runner -ResultPath $SmallResult -SnapshotFileCount 10000}
    else{& $pwsh -NoProfile -File $runner -ResultPath $SmallResult -FileCount 10000}
    if($LASTEXITCODE -ne 0){throw 'CONTROLLED_VALIDATION_FAILED'}
    Completed $SmallResult $(if($UseSnapshotScale){'vss_scale_benchmarked'}else{'scale_benchmarked'})
    $phase=if($UseSnapshotScale){'SnapshotScale100000'}else{'Scale100000'};State
    if($UseSnapshotScale){& $pwsh -NoProfile -File $runner -ResultPath $LargeResult -SnapshotFileCount 100000}
    else{& $pwsh -NoProfile -File $runner -ResultPath $LargeResult -FileCount 100000}
    if($LASTEXITCODE -ne 0){throw 'CONTROLLED_VALIDATION_FAILED'}
    Completed $LargeResult $(if($UseSnapshotScale){'vss_scale_benchmarked'}else{'scale_benchmarked'})
    $phase='Snapshot';State
    & $pwsh -NoProfile -File $runner -ResultPath $SnapshotResult -Snapshot
    if($LASTEXITCODE -ne 0){throw 'CONTROLLED_VALIDATION_FAILED'}
    Completed $SnapshotResult 'vss_verified'
    $phase='Complete';$status='passed'
} catch {$status='failed';$code='ControlledValidationFailed'}
finally {State}
Write-Output ('RESULT: '+$status)
if($status -cne 'passed'){exit 1}
