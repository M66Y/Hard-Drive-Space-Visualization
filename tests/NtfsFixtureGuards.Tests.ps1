#requires -Version 7.4
# Offline guard tests. Only named function definitions are extracted; the VHD
# script's top-level commands are never invoked. All storage queries are mocks.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:checks++; Write-Output ('PASS NTFS fixture guard ' + $Name)
}
function Reject([scriptblock]$Action, [string]$Expected, [string]$Name) {
    $message = $null
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Check ($message -ceq $Expected) $Name
}

$source = Join-Path $PSScriptRoot '../scripts/ntfs-prototype/Invoke-NtfsFixture.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $source).Path, [ref]$tokens, [ref]$parseErrors)
Check ($parseErrors.Count -eq 0) 'fixture script syntax'
foreach ($name in @('Assert-NoReparsePath','Assert-OwnedTree','Get-OwnedDisk','Get-OwnedPartition')) {
    $functions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $false))
    Check ($functions.Count -eq 1) ('extract only ' + $name)
    . ([scriptblock]::Create($functions[0].Extent.Text))
}

# These functions shadow storage cmdlets for this script's entire lifetime.
function Get-DiskImage {
    [CmdletBinding()]
    param([string]$ImagePath)
    if ($ImagePath -cne $script:imagePath) { throw 'MOCK_UNEXPECTED_IMAGE' }
    return $script:fakeImages
}
function Get-Disk {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline)]$InputObject)
    process { return $script:fakeDisks }
}
function Get-Partition {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline)]$InputObject)
    process { return $script:fakePartitions }
}

$parent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
$root = Join-Path $parent ('ntfs-guard-check-' + [guid]::NewGuid().ToString('N'))
$script:artifactsRoot = Join-Path $root 'artifacts'
$script:fixtureNonce = [guid]::NewGuid().ToString('N')
$script:workspace = Join-Path $script:artifactsRoot ('ntfs-live-' + $script:fixtureNonce)
$script:imagePath = Join-Path $script:workspace 'fixture.vhd'
$link = Join-Path $script:workspace 'junction'
function Reset-Mocks {
    $script:mountAttempted = $true; $script:workspaceOwned = $true
    $script:diskNumber = 12345; $script:diskPath = 'synthetic-disk-path'
    $script:imageDevicePath = 'synthetic-image-device'
    $script:fakeImages = @([pscustomobject]@{Attached=$true; ImagePath=$script:imagePath; DevicePath=$script:imageDevicePath})
    $script:fakeDisks = @([pscustomobject]@{IsBoot=$false;IsSystem=$false;IsReadOnly=$false;BusType='FileBackedVirtual';Size=256MB;Number=12345;Path=$script:diskPath})
    $script:fakePartitions = @([pscustomobject]@{PartitionNumber=1;DiskNumber=12345;Size=255MB})
}
try {
    [IO.Directory]::CreateDirectory($script:workspace) | Out-Null
    [IO.File]::WriteAllText($script:imagePath, 'ordinary synthetic file, not a virtual disk')
    [IO.Directory]::CreateDirectory((Join-Path $script:workspace 'nested')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $script:workspace 'nested/fixture.txt'), 'synthetic')
    Reset-Mocks
    Check ((Get-OwnedDisk).Number -eq 12345) 'owned synthetic disk accepted'
    Check ((Get-OwnedPartition 1).DiskNumber -eq 12345) 'owned synthetic partition accepted'
    Assert-OwnedTree; Check $true 'ordinary owned tree accepted'
    Reset-Mocks; $script:mountAttempted=$false
    Reject { Get-OwnedDisk } 'IMAGE_NOT_OWNED' 'no mount attempt rejected'
    Reset-Mocks; $script:workspaceOwned=$false
    Reject { Get-OwnedDisk } 'IMAGE_NOT_OWNED' 'unowned image rejected'
    Reset-Mocks; $script:fakeImages=@()
    Reject { Get-OwnedDisk } 'IMAGE_BINDING' 'missing image rejected'
    Reset-Mocks; $script:fakeImages+= $script:fakeImages[0]
    Reject { Get-OwnedDisk } 'IMAGE_BINDING' 'ambiguous image rejected'
    Reset-Mocks; $script:fakeImages[0].Attached=$false
    Reject { Get-OwnedDisk } 'IMAGE_BINDING' 'detached image rejected'
    Reset-Mocks; $script:fakeImages[0].ImagePath=Join-Path $script:workspace 'other.vhd'
    Reject { Get-OwnedDisk } 'IMAGE_BINDING' 'wrong image path rejected'
    Reset-Mocks; $script:fakeImages[0].DevicePath='changed-device'
    Reject { Get-OwnedDisk } 'IMAGE_DEVICE_CHANGED' 'changed image device rejected'
    Reset-Mocks; $script:fakeDisks=@()
    Reject { Get-OwnedDisk } 'DISK_BINDING' 'missing disk rejected'
    Reset-Mocks; $script:fakeDisks+=$script:fakeDisks[0]
    Reject { Get-OwnedDisk } 'DISK_BINDING' 'ambiguous disk rejected'
    foreach ($property in @('IsBoot','IsSystem','IsReadOnly')) {
        Reset-Mocks; $script:fakeDisks[0].$property=$true
        Reject { Get-OwnedDisk } 'DISK_SAFETY' ($property + ' rejected')
    }
    Reset-Mocks; $script:fakeDisks[0].BusType='SATA'
    Reject { Get-OwnedDisk } 'DISK_SAFETY' 'physical bus rejected'
    foreach ($size in @(255MB,258MB)) {
        Reset-Mocks; $script:fakeDisks[0].Size=$size
        Reject { Get-OwnedDisk } 'DISK_SAFETY' 'unexpected image size rejected'
    }
    Reset-Mocks; $script:fakeDisks[0].Number=12346
    Reject { Get-OwnedDisk } 'DISK_IDENTITY_CHANGED' 'changed disk number rejected'
    Reset-Mocks; $script:fakeDisks[0].Path='changed-path'
    Reject { Get-OwnedDisk } 'DISK_IDENTITY_CHANGED' 'changed disk path rejected'
    Reset-Mocks; $script:fakePartitions=@()
    Reject { Get-OwnedPartition 1 } 'PARTITION_BINDING' 'missing partition rejected'
    Reset-Mocks; $script:fakePartitions+=$script:fakePartitions[0]
    Reject { Get-OwnedPartition 1 } 'PARTITION_BINDING' 'ambiguous partition rejected'
    Reset-Mocks; $script:fakePartitions[0].DiskNumber=12346
    Reject { Get-OwnedPartition 1 } 'PARTITION_BINDING' 'wrong partition disk rejected'
    foreach ($size in @(239MB,257MB)) {
        Reset-Mocks; $script:fakePartitions[0].Size=$size
        Reject { Get-OwnedPartition 1 } 'PARTITION_BINDING' 'unexpected partition size rejected'
    }
    Reset-Mocks
    Reject { Get-OwnedPartition 2 } 'PARTITION_BINDING' 'wrong partition number rejected'
    Reset-Mocks; $script:workspaceOwned=$false
    Reject { Assert-OwnedTree } 'CLEANUP_OWNERSHIP' 'unowned cleanup rejected'
    Reset-Mocks; $script:fixtureNonce=[guid]::NewGuid().ToString('N')
    Reject { Assert-OwnedTree } 'CLEANUP_OWNERSHIP' 'mismatched cleanup nonce rejected'
    $script:fixtureNonce=[IO.Path]::GetFileName($script:workspace).Substring(10)
    Reset-Mocks
    $target=Join-Path $root 'junction-target'
    [IO.Directory]::CreateDirectory($target) | Out-Null
    New-Item -ItemType Junction -Path $link -Target $target | Out-Null
    try {
        Reject { Assert-NoReparsePath (Join-Path $link 'child') } 'REPARSE_PATH' 'reparse ancestor rejected'
        Reject { Assert-OwnedTree } 'CLEANUP_REPARSE' 'reparse child blocks recursive cleanup'
    } finally { Remove-Item -LiteralPath $link -Force }
    Assert-OwnedTree; Check $true 'ordinary tree remains valid after junction removal'
    Write-Output ('PASS NTFS fixture guard checks: ' + $script:checks + '; virtual disk operations: none')
} finally {
    # Remove only this test's random ordinary directory; never follow a junction.
    if (Test-Path -LiteralPath $link) {
        $entry=Get-Item -LiteralPath $link -Force
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Remove-Item -LiteralPath $link -Force }
    }
    $full=[IO.Path]::GetFullPath($root)
    if ([IO.Path]::GetDirectoryName($full) -cne $parent -or [IO.Path]::GetFileName($full) -cnotmatch '^ntfs-guard-check-[0-9a-f]{32}$') { throw 'TEST_CLEANUP_BOUNDARY' }
    if (Test-Path -LiteralPath $full) {
        [void](Assert-NoReparsePath $full)
        $pending=[Collections.Generic.Stack[string]]::new(); $pending.Push($full)
        while ($pending.Count) {
            foreach ($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
                if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'TEST_CLEANUP_REPARSE' }
                if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
            }
        }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
