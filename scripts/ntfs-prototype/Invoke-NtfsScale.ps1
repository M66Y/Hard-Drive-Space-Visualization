#requires -Version 7.4
[CmdletBinding(DefaultParameterSetName='Scale')]
param([Parameter(Mandatory)][string]$ResultPath,
    [Parameter(Mandatory,ParameterSetName='Scale')][ValidateSet(10000,100000)][int]$FileCount,
    [Parameter(Mandatory,ParameterSetName='Snapshot')][switch]$Snapshot)

# Isolated scale mode: never accepts an existing image, disk or target directory.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($PSCmdlet.ParameterSetName -ceq 'Snapshot' -and -not $Snapshot) { throw 'SNAPSHOT_SWITCH_REQUIRED' }
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifactsRoot=[IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$fixtureNonce=[guid]::NewGuid().ToString('N')
$workspace=Join-Path $artifactsRoot ('ntfs-live-'+$fixtureNonce)
$imagePath=Join-Path $workspace 'fixture.vhd'
$runnerPath=Join-Path $repositoryRoot 'tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll'
$stage='ValidateArguments'; $mounted=$false; $mountAttempted=$false; $workspaceOwned=$false
$detached=$false; $cleaned=$false; $runner=$null; $runnerExit=$null; $runnerStopped=$true
$status='failed'; $failureCode=$(if($Snapshot){'VssProbeFailed'}else{'ScaleFailed'}); $scaleEvidence=$null
$vssEvidence=$null; $snapshotCleanupConfirmed=$false; $snapshotRunnerAttempted=$false
$observedCounts=$null
$diskNumber=$null; $diskPath=$null; $imageDevicePath=$null
. (Join-Path $PSScriptRoot 'KernelMatrixEvidence.ps1')
. (Join-Path $PSScriptRoot 'ScaleEvidence.ps1')
if ($Snapshot) { . (Join-Path $PSScriptRoot 'VssFixtureEvidence.ps1') }
function Assert-NoReparsePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'REPARSE_PATH' }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $full
}

function Assert-OwnedTree {
    $full = Assert-NoReparsePath $workspace
    if (-not $workspaceOwned -or [IO.Path]::GetDirectoryName($full) -cne $artifactsRoot -or
        [IO.Path]::GetFileName($full) -cne ('ntfs-live-' + $fixtureNonce)) { throw 'CLEANUP_OWNERSHIP' }
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($full)
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'CLEANUP_REPARSE' }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
}

function Get-OwnedDisk {
    if (-not $mountAttempted -or -not $workspaceOwned) { throw 'IMAGE_NOT_OWNED' }
    [void](Assert-NoReparsePath $imagePath)
    $images = @(Get-DiskImage -ImagePath $imagePath -ErrorAction Stop)
    if ($images.Count -ne 1 -or -not $images[0].Attached -or
        [IO.Path]::GetFullPath($images[0].ImagePath) -cne $imagePath) { throw 'IMAGE_BINDING' }
    if ($imageDevicePath -and $images[0].DevicePath -cne $imageDevicePath) { throw 'IMAGE_DEVICE_CHANGED' }
    $disks = @($images[0] | Get-Disk -ErrorAction Stop)
    if ($disks.Count -ne 1) { throw 'DISK_BINDING' }
    $disk = $disks[0]
    if ($disk.IsBoot -or $disk.IsSystem -or $disk.IsReadOnly -or
        ([string]$disk.BusType).Replace(' ', '') -cne 'FileBackedVirtual' -or
        $disk.Size -lt 1024MB -or $disk.Size -gt 1025MB) { throw 'DISK_SAFETY' }
    if ($null -ne $diskNumber -and ($disk.Number -ne $diskNumber -or $disk.Path -cne $diskPath)) { throw 'DISK_IDENTITY_CHANGED' }
    return $disk
}

function Get-OwnedPartition([uint32]$Number) {
    $disk = Get-OwnedDisk
    $partitions = @($disk | Get-Partition -ErrorAction Stop | Where-Object { $_.PartitionNumber -eq $Number })
    if ($partitions.Count -ne 1 -or $partitions[0].DiskNumber -ne $disk.Number -or
        $partitions[0].Size -lt 1000MB -or $partitions[0].Size -gt 1024MB) { throw 'PARTITION_BINDING' }
    return $partitions[0]
}

function Write-SafeJson($Value, [string]$Path) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}
# The result is the only retained evidence. It never contains paths, names or raw errors.
$ResultPath = [IO.Path]::GetFullPath($ResultPath)
if ([IO.Path]::GetDirectoryName($ResultPath) -cne $artifactsRoot -or
    [IO.Path]::GetFileName($ResultPath) -cnotmatch '^ntfs-live-result-[0-9a-f]{32}\.json$') { throw 'RESULT_LOCATION' }
if ($workspace -match '[\r\n"]' -or $repositoryRoot -match '[\r\n"]') { throw 'UNSUPPORTED_TOOL_PATH' }
[void](Assert-NoReparsePath $artifactsRoot)
[IO.Directory]::CreateDirectory($artifactsRoot) | Out-Null
[void](Assert-NoReparsePath $ResultPath)
$reservation = [IO.File]::Open($ResultPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$reservation.Dispose()
Write-SafeJson ([ordered]@{schema=1;status='running';stage=$stage}) $ResultPath

try {
    $stage = 'AdministratorCheck'
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'ADMINISTRATOR_REQUIRED' }
    foreach ($command in @('Mount-DiskImage','Get-DiskImage','Get-Disk','Initialize-Disk','New-Partition','Get-Partition','Get-Volume','Format-Volume','Dismount-DiskImage')) {
        [void](Get-Command $command -ErrorAction Stop)
    }
    [void](Assert-NoReparsePath $runnerPath)
    if (-not (Test-Path -LiteralPath $runnerPath -PathType Leaf)) { throw 'RUNNER_NOT_BUILT' }
    $dotnetPath = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source
    $existingLetters = @(Get-Volume -ErrorAction Stop | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter })

    $stage = 'CreateImage'
    if (Test-Path -LiteralPath $workspace) { throw 'WORKSPACE_EXISTS' }
    [IO.Directory]::CreateDirectory($workspace) | Out-Null; $workspaceOwned = $true
    [void](Assert-NoReparsePath $workspace)
    # Unicode native API avoids shell/script encoding and can only create a new file.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class AsukaLensScaleImage {
    [StructLayout(LayoutKind.Sequential)] struct StorageType { public uint DeviceId; public Guid Vendor; }
    [StructLayout(LayoutKind.Explicit, Size=56)] struct Parameters {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(8)] public Guid UniqueId;
        [FieldOffset(24)] public ulong MaximumSize;
        [FieldOffset(32)] public uint BlockSize;
        [FieldOffset(36)] public uint SectorSize;
        [FieldOffset(40)] public IntPtr Parent;
        [FieldOffset(48)] public IntPtr Source;
    }
    [DllImport("virtdisk.dll", CharSet=CharSet.Unicode)]
    static extern uint CreateVirtualDisk(ref StorageType type, string path, uint access, IntPtr security,
        uint flags, uint providerFlags, ref Parameters parameters, IntPtr overlapped, out SafeFileHandle handle);
    public static uint Create(string path) {
        if (!Environment.Is64BitProcess || System.IO.File.Exists(path)) throw new InvalidOperationException("IMAGE_EXISTS_OR_ARCHITECTURE");
        var type=new StorageType {DeviceId=2,Vendor=new Guid("ec984aec-a0f9-47e9-901f-71415a66345b")};
        var parameters=new Parameters {Version=1,MaximumSize=1024UL*1024*1024,SectorSize=512};
        SafeFileHandle handle=null;
        try { return CreateVirtualDisk(ref type,path,0x100000,IntPtr.Zero,1,0,ref parameters,IntPtr.Zero,out handle); }
        finally { if (handle!=null) handle.Dispose(); }
    }
}
'@
    $nativeError = [int][AsukaLensScaleImage]::Create($imagePath)
    if ($nativeError -ne 0 -or -not (Test-Path -LiteralPath $imagePath -PathType Leaf) -or
        (Get-Item -LiteralPath $imagePath).Length -lt 1024MB -or (Get-Item -LiteralPath $imagePath).Length -gt 1025MB) { throw 'IMAGE_CREATION_FAILED' }

    $stage = 'MountImage'
    $mountAttempted = $true
    $mount = Mount-DiskImage -ImagePath $imagePath -StorageType VHD -Access ReadWrite -NoDriveLetter -PassThru -ErrorAction Stop
    $mounted = $true; $imageDevicePath = $mount.DevicePath
    $disk = Get-OwnedDisk
    if ([string]$disk.PartitionStyle -cne 'RAW') { throw 'NEW_DISK_NOT_RAW' }
    $diskNumber = $disk.Number; $diskPath = $disk.Path

    $stage = 'InitializeOwnedDisk'
    $disk = Get-OwnedDisk
    if ([string]$disk.PartitionStyle -cne 'RAW') { throw 'DISK_NO_LONGER_RAW' }
    Initialize-Disk -InputObject $disk -PartitionStyle MBR -Confirm:$false -ErrorAction Stop | Out-Null

    $stage = 'PartitionOwnedDisk'
    $disk = Get-OwnedDisk
    if ([string]$disk.PartitionStyle -cne 'MBR') { throw 'PARTITION_STYLE_CHANGED' }
    if (@(Get-Partition -ErrorAction Stop | Where-Object { $_.DiskNumber -eq $disk.Number }).Count -ne 0) { throw 'UNEXPECTED_PARTITIONS' }
    $partition = New-Partition -DiskNumber $disk.Number -UseMaximumSize -ErrorAction Stop
    $partitionNumber = $partition.PartitionNumber
    $partition = Get-OwnedPartition $partitionNumber

    $stage = 'FormatOwnedPartition'
    $partition = Get-OwnedPartition $partitionNumber
    $volume = Format-Volume -Partition $partition -FileSystem NTFS -AllocationUnitSize 4096 -NewFileSystemLabel 'AsukaLensFixture' -Force -Confirm:$false -ErrorAction Stop
    $stage = 'AssignFormattedVolumeLetter'
    $partition = Get-OwnedPartition $partitionNumber
    Add-PartitionAccessPath -InputObject $partition -AssignDriveLetter -ErrorAction Stop
    $partition = Get-OwnedPartition $partitionNumber
    $driveLetter = [string]$partition.DriveLetter
    if ($driveLetter -cnotmatch '^[D-Z]$' -or $driveLetter -cin $existingLetters) { throw 'DRIVE_LETTER_COLLISION' }
    $volumes = @($partition | Get-Volume -ErrorAction Stop)
    if ($volumes.Count -ne 1) { throw 'VOLUME_BINDING' }
    $volume = $volumes[0]
    $volumeId = [string]$volume.UniqueId
    $volumePrefix = '\\?\Volume{'
    $parsedVolumeGuid = [guid]::Empty
    if (-not $volumeId.StartsWith($volumePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not $volumeId.EndsWith('}\', [StringComparison]::Ordinal) -or
        $volumeId.Length -ne $volumePrefix.Length + 36 + 2) { throw 'VOLUME_ID_FORMAT' }
    if (-not [guid]::TryParse($volumeId.Substring($volumePrefix.Length, 36), [ref]$parsedVolumeGuid) -or
        $volume.FileSystem -cne 'NTFS' -or $volume.AllocationUnitSize -ne 4096 -or
        [string]$volume.DriveLetter -cne $driveLetter) { throw 'VOLUME_FORMAT_MISMATCH' }
    $driveRoot = $driveLetter + ':\'
    $confirmedPartition = Get-OwnedPartition $partitionNumber
    $confirmedVolume = @($confirmedPartition | Get-Volume -ErrorAction Stop)
    if ([string]$confirmedPartition.DriveLetter -cne $driveLetter -or $confirmedVolume.Count -ne 1 -or
        [string]$confirmedVolume[0].UniqueId -cne $volumeId) { throw 'VOLUME_CHANGED_BEFORE_MARKER' }
    $mode = if ($Snapshot) { 'fixture-vss' } else { 'fixture-scale' }
    $fixture = [ordered]@{schema=1;image_path=$imagePath;volume_guid=[string]$volume.UniqueId;drive_root=$driveRoot;workspace=$workspace;nonce=$fixtureNonce;mode=$mode}
    $manifestPath = Join-Path $workspace 'fixture.json'
    Write-SafeJson $fixture $manifestPath
    Write-SafeJson $fixture (Join-Path $driveRoot '.asukalens-ntfs-fixture.json')

    $stage = if ($Snapshot) { 'RunSnapshotProbe' } else { 'RunScaleBenchmark' }
    [void](Get-OwnedPartition $partitionNumber)
    $runnerArgs = if ($Snapshot) { @(('"' + $runnerPath + '"'),'--ntfs-vss-live',('"' + $manifestPath + '"')) } else {
        @(('"' + $runnerPath + '"'),'--ntfs-scale-live',('"' + $manifestPath + '"'),[string]$FileCount) }
    $snapshotRunnerAttempted=[bool]$Snapshot
    $runner = Start-Process -FilePath $dotnetPath -WorkingDirectory $repositoryRoot -ArgumentList $runnerArgs -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $workspace 'runner.stdout') -RedirectStandardError (Join-Path $workspace 'runner.stderr')
    $runnerStopped = $false
    $deadline = [DateTime]::UtcNow.AddMinutes($(if ($Snapshot) { 3 } else { 15 }))
    while (-not $runner.WaitForExit(1000)) {
        $logBytes = @(Get-Item -LiteralPath (Join-Path $workspace 'runner.stdout'),(Join-Path $workspace 'runner.stderr') -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        if ([DateTime]::UtcNow -ge $deadline -or $logBytes -gt 1MB) { throw 'RUNNER_LIMIT' }
    }
    $runnerExit = $runner.ExitCode
    $summaryPath=Join-Path $workspace 'live-summary.json'
    [void](Assert-NoReparsePath $summaryPath)
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf) -or
        (Get-Item -LiteralPath $summaryPath).Length -gt 65536) { throw 'SCALE_SUMMARY_MISSING' }
    $summaryText=Get-Content -LiteralPath $summaryPath -Raw -Encoding utf8
    Assert-UniqueJsonKeys $summaryText
    $summary=$summaryText | ConvertFrom-Json
    if (-not $Snapshot -and $summary.PSObject.Properties['scale_benchmark'] -and
        $null -ne $summary.scale_benchmark -and $summary.scale_benchmark.PSObject.Properties['ResultCounts']) {
        $counts=$summary.scale_benchmark.ResultCounts
        $fields=@('FileObjects','FileEntries','Directories','LogicalBytes','AllocatedBytes','UnknownAllocationFiles')
        if ($counts -is [pscustomobject] -and @($counts.PSObject.Properties).Count -eq 6 -and
            -not (Compare-Object $fields @($counts.PSObject.Properties.Name) -CaseSensitive)) {
            $safe=[ordered]@{}; $validCounts=$true
            foreach ($field in $fields) {
                $value=$counts.$field
                if (($value -isnot [int] -and $value -isnot [long]) -or $value -lt 0 -or $value -gt 1099511627776) {
                    $validCounts=$false; break
                }
                $safe[$field]=[long]$value
            }
            if ($validCounts) { $observedCounts=$safe }
        }
    }
    if ($Snapshot) {
        # Validate the complete envelope before accepting the cleanup statement.
        $validated = ConvertTo-NtfsVssFixtureEnvelope $summary
        $snapshotCleanupConfirmed = $validated.cleanup_confirmed
        $failureCode = $validated.code
        if ($runnerExit -ne 0 -or $validated.status -cne 'vss_verified') { throw 'VSS_PROBE_FAILED' }
        $vssEvidence=$validated.vss_evidence
        $status='vss_verified'; $failureCode='None'; $stage='SnapshotProbeCompared'
    } else {
        if ($summary.schema -eq 1 -and $summary.status -ceq 'failed' -and $summary.code -is [string] -and
            $summary.code -cin @('ScalePreparationFailed','ScaleSourceFailed','ScaleDirectoryFailed','ScaleComparisonFailed',
                'ScaleCancelled','ScaleCleanupFailed','ScaleFailed')) { $failureCode=$summary.code }
        if ($runnerExit -ne 0 -or $summary.schema -ne 1 -or $summary.status -cne 'scale_benchmarked' -or
            $summary.code -cne 'None') { throw 'SCALE_BENCHMARK_FAILED' }
        $scaleEvidence=ConvertTo-NtfsScaleEvidence $summary.scale_benchmark $FileCount
        $status='scale_benchmarked'; $failureCode='None'; $stage='ScaleBenchmarkCompared'
    }
} catch {
    # Never export exception text, device identifiers, raw records or file lists.
    $status='failed'
    if (-not $Snapshot -and $_.Exception.Message -cin @('SCALE_EVIDENCE_INVALID','SCALE_SUMMARY_MISSING','RUNNER_LIMIT')) {
        $failureCode=switch -CaseSensitive ($_.Exception.Message) {
            'SCALE_EVIDENCE_INVALID' { 'ScaleEvidenceRejected' }
            'SCALE_SUMMARY_MISSING' { 'ScaleSummaryMissing' }
            'RUNNER_LIMIT' { 'ScaleRunnerLimit' }
        }
    }
} finally {
    if ($null -ne $runner) {
        if (-not $runner.HasExited) {
            try { $runner.Kill($true); [void]$runner.WaitForExit(10000) } catch { $status='failed' }
        }
        $runnerStopped = $runner.HasExited
        $runner.Dispose()
    }
    try {
        if (-not $runnerStopped) { throw 'RUNNER_STILL_ACTIVE' }
        if ($Snapshot -and $snapshotRunnerAttempted -and -not $snapshotCleanupConfirmed) { throw 'SNAPSHOT_CLEANUP_NOT_CONFIRMED' }
        if ($mountAttempted) {
            $images = @(Get-DiskImage -ImagePath $imagePath -ErrorAction Stop)
            if ($images.Count -ne 1) { throw 'IMAGE_CLEANUP_BINDING' }
            if ($images[0].Attached) { Dismount-DiskImage -ImagePath $imagePath -ErrorAction Stop }
            $images = @(Get-DiskImage -ImagePath $imagePath -ErrorAction Stop)
            $detached = $images.Count -eq 1 -and -not $images[0].Attached
            if (-not $detached) { throw 'IMAGE_STILL_ATTACHED' }
        } else { $detached = $true }
        if ($workspaceOwned) { Assert-OwnedTree; Remove-Item -LiteralPath $workspace -Recurse -Force }
        $cleaned = $true
    } catch { $status = 'cleanup_failed' }
    $result=[ordered]@{schema=1;mode=$(if($Snapshot){'fixture-vss'}else{'fixture-scale'});status=$status;stage=$stage;code=$failureCode;
        exit_code=$runnerExit;detached=$detached;cleanup_completed=$cleaned;
        scale_benchmark=$(if($status -ceq 'scale_benchmarked'){$scaleEvidence}else{$null})}
    if ($Snapshot) {
        $result.snapshot_cleanup_confirmed=$snapshotCleanupConfirmed
        $result.vss_evidence=if($status -ceq 'vss_verified'){$vssEvidence}else{$null}
    } elseif ($status -ceq 'failed' -and $null -ne $observedCounts) {
        # Failure-only diagnostic counts are not benchmark success evidence.
        $result.observed_counts=$observedCounts
    }
    $resultTemp=$ResultPath+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [void](Assert-NoReparsePath $ResultPath)
        Write-SafeJson $result $resultTemp
        [IO.File]::Move($resultTemp,$ResultPath,$true)
    } finally { if(Test-Path -LiteralPath $resultTemp){Remove-Item -LiteralPath $resultTemp -Force} }
}
Write-Output ('RESULT: '+$status)
if($status -cnotin @('scale_benchmarked','vss_verified')){exit 1}
