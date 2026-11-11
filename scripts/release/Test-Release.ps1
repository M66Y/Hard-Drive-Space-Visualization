#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$Directory = (Resolve-Path -LiteralPath $Directory).Path
Assert-SafeTree $Directory
$manifest = Get-Content -LiteralPath (Join-Path $Directory 'release-manifest.json') -Raw | ConvertFrom-Json
$numeric = Assert-ReleaseVersion $manifest.version
if ($manifest.schema -ne 1 -or $manifest.product -ne 'AsukaLens' -or $manifest.commit -notmatch '^[0-9a-f]{40}$' -or
    $manifest.rid -ne 'win-x64' -or $manifest.self_contained -ne $true -or $manifest.local_test -isnot [bool] -or
    $manifest.source_kind -ne $(if ($manifest.local_test) { 'working-tree' } else { 'git-commit' })) { throw 'Invalid release manifest.' }
$version = $manifest.version
$stem = Get-ReleaseAssetStem $manifest
$expectedNames = @("$stem.zip", "$stem.exe", "$stem-source.zip", "$stem-source.tar.gz")
$assetNames = @($manifest.assets | ForEach-Object { $_.path })
if ($assetNames.Count -ne 4 -or (Compare-Object ($expectedNames | Sort-Object) ($assetNames | Sort-Object) -CaseSensitive)) { throw 'Exactly four correctly named assets are required.' }
$expectedFiles = $expectedNames + @('release-manifest.json','SHA256SUMS.txt')
if ((Get-ChildItem -LiteralPath $Directory -Force).Count -ne 6 -or
    (Compare-Object ($expectedFiles | Sort-Object) @((Get-ChildItem -LiteralPath $Directory -Force).Name | Sort-Object) -CaseSensitive)) { throw 'Unexpected files in the release directory.' }
foreach ($asset in $manifest.assets) {
    Assert-SafeRelativePath $asset.path
    $file = Get-Item -LiteralPath (Join-Path $Directory $asset.path)
    if ($file.Length -ne $asset.bytes -or (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant() -ne $asset.sha256) { throw 'Asset checksum mismatch.' }
}
$checksumLines = @([IO.File]::ReadAllLines((Join-Path $Directory 'SHA256SUMS.txt')))
$expectedChecksums = @($manifest.assets | ForEach-Object { $_.sha256 + '  ' + $_.path })
if ($checksumLines.Count -ne 4 -or (Compare-Object $expectedChecksums $checksumLines)) { throw 'SHA256SUMS does not match the manifest.' }
$sourceZip = @(Get-ArchiveInventory (Join-Path $Directory "$stem-source.zip"))
$sourceTar = @(Get-ArchiveInventory (Join-Path $Directory "$stem-source.tar.gz") -TarGz)
foreach ($file in $sourceZip) { Assert-SourcePath $file.path }
Assert-SameInventory $manifest.source_files $sourceZip
Assert-SameInventory $sourceZip $sourceTar
$payload = @(Get-ArchiveInventory (Join-Path $Directory "$stem.zip"))
Assert-SameInventory $manifest.payload_files $payload
$payloadNames = @($payload | ForEach-Object { $_.path })
foreach ($required in @('AsukaLens.exe','DiskAtlas.dll','DiskAtlas.runtimeconfig.json','DiskAtlas.deps.json','coreclr.dll','hostfxr.dll','hostpolicy.dll',
    'System.Private.CoreLib.dll','System.Windows.Forms.dll','LICENSE','NOTICE.md','licenses/CC-BY-NC-4.0.txt','licenses/Inno-Setup.txt','build-info.json','INSTALL-NOTICE.txt','使用说明.txt','docs/diagnostics.md')) {
    if ($required -cnotin $payloadNames) { throw ('Payload is missing ' + $required) }
}
foreach ($pack in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
    $minimum = if ($pack -like 'microsoft.netcore.*') { 2 } else { 1 }
    if (@($payloadNames | Where-Object { $_ -like "licenses/runtime/$pack/*" }).Count -lt $minimum) { throw 'Missing third-party runtime notices.' }
}
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $Directory "$stem.zip"))
try {
    function Read-ZipJson([string]$Name) {
        $reader = [IO.StreamReader]::new($zip.GetEntry($Name).Open())
        try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
    }
    $config = Read-ZipJson 'DiskAtlas.runtimeconfig.json'
    if ($config.runtimeOptions.PSObject.Properties.Name -contains 'framework' -or $config.runtimeOptions.PSObject.Properties.Name -contains 'frameworks') { throw 'Payload still requires a shared runtime.' }
    $frameworks = @($config.runtimeOptions.includedFrameworks)
    if (@($frameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' -and $_.version -eq $manifest.runtime }).Count -ne 1 -or
        @($frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' -and $_.version -eq $manifest.runtime }).Count -ne 1) { throw 'Bundled runtime versions differ.' }
    $identity = Read-ZipJson 'build-info.json'
    if ($identity.version -ne $version -or $identity.commit -ne $manifest.commit -or $identity.local_test -ne $manifest.local_test) { throw 'Payload build identity mismatch.' }
} finally { $zip.Dispose() }
$installer = Join-Path $Directory "$stem.exe"
$exe = [IO.File]::OpenRead($installer)
try { if ($exe.ReadByte() -ne 77 -or $exe.ReadByte() -ne 90) { throw 'Installer is not a Windows executable.' } }
finally { $exe.Dispose() }
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($installer)
$fileVersion = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart,$info.FileMinorPart,$info.FileBuildPart,$info.FilePrivatePart
if ($info.ProductName.Trim() -ne $(if ($manifest.local_test) {'AsukaLens (LOCAL TEST)'} else {'AsukaLens'}) -or $fileVersion -ne ($numeric+'.0')) {
    throw ("Installer metadata mismatch: product=$($info.ProductName), version=$fileVersion, expected=$numeric.0, local-test=$($manifest.local_test)")
}
[PSCustomObject]@{ Passed=$true; Version=$version; LocalTest=$manifest.local_test; Assets=4; SourceFiles=$sourceZip.Count; PayloadFiles=$payload.Count; PackageExecutionTested=$false }
