#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ResultPath)

# Destructive commands below are bound only to a newly created disposable image.
# No disk number, drive letter, existing image, or general command can be supplied.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$fixtureNonce = [guid]::NewGuid().ToString('N')
$workspace = Join-Path $artifactsRoot ('ntfs-live-' + $fixtureNonce)
$imagePath = Join-Path $workspace 'fixture.vhd'
$runnerPath = Join-Path $repositoryRoot 'tests/bin/Release/net10.0-windows/DiskAtlas.Tests.dll'
$stage = 'ValidateArguments'
$mounted = $false; $mountAttempted = $false; $workspaceOwned = $false
$detached = $false; $cleaned = $false; $runner = $null; $runnerExit = $null
$checks = 0; $failureCount = 0; $status = 'failed'
$failureCode = 'None'; $runnerStopped = $true
$nativeError = 0
$diskNumber = $null; $diskPath = $null; $imageDevicePath = $null

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
        $disk.Size -lt 256MB -or $disk.Size -gt 257MB) { throw 'DISK_SAFETY' }
    if ($null -ne $diskNumber -and ($disk.Number -ne $diskNumber -or $disk.Path -cne $diskPath)) { throw 'DISK_IDENTITY_CHANGED' }
    return $disk
}

function Get-OwnedPartition([uint32]$Number) {
    $disk = Get-OwnedDisk
    $partitions = @($disk | Get-Partition -ErrorAction Stop | Where-Object { $_.PartitionNumber -eq $Number })
    if ($partitions.Count -ne 1 -or $partitions[0].DiskNumber -ne $disk.Number -or
        $partitions[0].Size -lt 240MB -or $partitions[0].Size -gt 256MB) { throw 'PARTITION_BINDING' }
    return $partitions[0]
}

function Write-SafeJson($Value, [string]$Path) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
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
public static class AsukaLensFixtureImage {
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
        var parameters=new Parameters {Version=1,MaximumSize=256UL*1024*1024,SectorSize=512};
        uint error=CreateVirtualDisk(ref type,path,0x100000,IntPtr.Zero,1,0,ref parameters,IntPtr.Zero,out var handle);
        if (error==0) handle.Dispose();
        return error;
    }
}
'@
    $nativeError = [int][AsukaLensFixtureImage]::Create($imagePath)
    if ($nativeError -ne 0 -or -not (Test-Path -LiteralPath $imagePath -PathType Leaf) -or
        (Get-Item -LiteralPath $imagePath).Length -lt 256MB -or (Get-Item -LiteralPath $imagePath).Length -gt 257MB) { throw 'IMAGE_CREATION_FAILED' }

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
    $fixture = [ordered]@{schema=1;image_path=$imagePath;volume_guid=[string]$volume.UniqueId;drive_root=$driveRoot;workspace=$workspace;nonce=$fixtureNonce}
    $manifestPath = Join-Path $workspace 'fixture.json'
    Write-SafeJson $fixture $manifestPath
    Write-SafeJson $fixture (Join-Path $driveRoot '.asukalens-ntfs-fixture.json')

    $stage = 'RunRecordComparison'
    [void](Get-OwnedPartition $partitionNumber)
    $runner = Start-Process -FilePath $dotnetPath -WorkingDirectory $repositoryRoot -ArgumentList @(('"' + $runnerPath + '"'), '--ntfs-live-prototype', ('"' + $manifestPath + '"')) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $workspace 'runner.stdout') -RedirectStandardError (Join-Path $workspace 'runner.stderr')
    $runnerStopped = $false
    $deadline = [DateTime]::UtcNow.AddMinutes(2)
    while (-not $runner.WaitForExit(1000)) {
        $logBytes = @(Get-Item -LiteralPath (Join-Path $workspace 'runner.stdout'),(Join-Path $workspace 'runner.stderr') -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        if ([DateTime]::UtcNow -ge $deadline -or $logBytes -gt 1MB) { throw 'RUNNER_LIMIT' }
    }
    $runnerExit = $runner.ExitCode
    $summaryPath = Join-Path $workspace 'live-summary.json'
    [void](Assert-NoReparsePath $summaryPath)
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf) -or (Get-Item -LiteralPath $summaryPath).Length -gt 16384) { throw 'SUMMARY_MISSING' }
    $summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding utf8 | ConvertFrom-Json
    if ($summary.schema -ne 1 -or $summary.status -cnotin @('passed','failed') -or
        ($summary.checks -isnot [long] -and $summary.checks -isnot [int]) -or
        ($summary.failures -isnot [long] -and $summary.failures -isnot [int]) -or
        $summary.checks -lt 1 -or $summary.checks -gt 10000 -or $summary.failures -lt 0 -or $summary.failures -gt $summary.checks) { throw 'SUMMARY_INVALID' }
    $checks = [int]$summary.checks; $failureCount = [int]$summary.failures
    if ($summary.PSObject.Properties['code'] -and [string]$summary.code -cmatch '^[A-Za-z][A-Za-z0-9-]{0,79}$') { $failureCode=[string]$summary.code }
    if ($runnerExit -ne 0 -or $summary.status -cne 'passed' -or $failureCount -ne 0) { throw 'COMPARISON_FAILED' }
    $status = 'passed'; $stage = 'ComparisonPassed'
} catch {
    # Native messages and logs can include local paths. Never copy them to the result.
    $status = 'failed'
    if ($failureCode -ceq 'None') {
        $failureCode = if ($_.Exception.Message -cmatch '^[A-Z_]{1,80}$') { $_.Exception.Message } else { 'OperationFailed' }
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
    $result = [ordered]@{schema=1;status=$status;stage=$stage;code=$failureCode;native_error=$nativeError;exit_code=$runnerExit;checks=$checks;failures=$failureCount;detached=$detached;cleanup_completed=$cleaned}
    $resultTemp = $ResultPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [void](Assert-NoReparsePath $ResultPath)
        Write-SafeJson $result $resultTemp
        [IO.File]::Move($resultTemp, $ResultPath, $true)
    } finally { if (Test-Path -LiteralPath $resultTemp) { Remove-Item -LiteralPath $resultTemp -Force } }
}
Write-Output ('RESULT: ' + $status)
if ($status -cne 'passed') { exit 1 }
