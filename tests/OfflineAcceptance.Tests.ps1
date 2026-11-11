#requires -Version 5.1
[CmdletBinding()]
param([string]$Summary)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../scripts/acceptance/Acceptance.Common.ps1')
$parent=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
[IO.Directory]::CreateDirectory($parent) | Out-Null
$root=Join-Path $parent ('acceptance-check-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$script:checks=[Collections.Generic.List[string]]::new()
$session=$null; $child=$null; $workspace=$null
function Check([bool]$Condition,[string]$Name) {
    if (-not $Condition) { throw ('FAIL '+$Name) }
    $script:checks.Add($Name)
    Write-Host ('PASS '+$Name)
}
function Reject([scriptblock]$Action,[string]$Name,[string]$Code) {
    $message=$null
    try { & $Action | Out-Null } catch { $message=$_.Exception.Message }
    if ($Code -and $message -ne $Code) { Write-Host ('Expected '+$Code+'; actual '+$message) }
    Check ($null -ne $message -and (-not $Code -or $message -eq $Code)) $Name
}
function Remove-TestTree([string]$Path) {
    $full=Assert-AcceptancePath $Path
    if (-not $full.StartsWith($parent+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Test cleanup boundary' }
    if (Test-Path -LiteralPath $full) { Assert-AcceptanceTree $full; Remove-Item -LiteralPath $full -Recurse -Force }
}
function File-Record([string]$Path,[string]$Name) {
    return [ordered]@{path=$Name;bytes=(Get-Item -LiteralPath $Path).Length;sha256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
}
function Save-Fixture($Fixture,[switch]$RebuildZip) {
    if ($RebuildZip) {
        $zip=Join-Path $Fixture.assets 'AsukaLens v1.4.2.zip'
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
        $Fixture.manifest.payload_files=@(Get-ChildItem -LiteralPath $Fixture.payload -Recurse -File | Sort-Object FullName | ForEach-Object {
            File-Record $_.FullName $_.FullName.Substring($Fixture.payload.Length+1).Replace('\','/')
        })
        # Match production ZIP names; the old Framework CreateFromDirectory uses backslashes.
        $archive=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $Fixture.manifest.payload_files) {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $Fixture.payload $file.path),$file.path)
            }
        } finally { $archive.Dispose() }
    }
    $Fixture.manifest.assets=@(Get-ChildItem -LiteralPath $Fixture.assets -File | Where-Object { $_.Name -like 'AsukaLens *' -or $_.Name -like 'AsukaLens.*' } | Sort-Object Name | ForEach-Object { File-Record $_.FullName $_.Name })
    [IO.File]::WriteAllText((Join-Path $Fixture.assets 'release-manifest.json'),($Fixture.manifest | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllLines((Join-Path $Fixture.assets 'SHA256SUMS.txt'),[string[]]@($Fixture.manifest.assets | ForEach-Object {$_.sha256+'  '+$_.path}),[Text.UTF8Encoding]::new($false))
    $Fixture.hash=(Get-FileHash -LiteralPath (Join-Path $Fixture.assets 'release-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
}
function New-Fixture {
    $base=Join-Path $root ('fixture-'+[guid]::NewGuid().ToString('N'))
    $assets=Join-Path $base 'assets'; $payload=Join-Path $base 'payload'
    [IO.Directory]::CreateDirectory($assets) | Out-Null
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    foreach ($name in @('DiskAtlas.dll','DiskAtlas.deps.json','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','System.Windows.Forms.dll',
        'LICENSE','NOTICE.md','licenses/CC-BY-NC-4.0.txt','licenses/Inno-Setup.txt','INSTALL-NOTICE.txt','使用说明.txt','docs/diagnostics.md',
        'licenses/runtime/microsoft.netcore.app.runtime.win-x64/LICENSE.txt','licenses/runtime/microsoft.netcore.app.runtime.win-x64/THIRD-PARTY-NOTICES.txt',
        'licenses/runtime/microsoft.windowsdesktop.app.runtime.win-x64/LICENSE.txt')) {
        $file=Join-Path $payload $name
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file)) | Out-Null
        [IO.File]::WriteAllText($file,'synthetic fixture, not a release')
    }
    [IO.File]::Copy((Join-Path $root 'helper.exe'),(Join-Path $payload 'AsukaLens.exe'))
    [IO.File]::WriteAllText((Join-Path $payload 'build-info.json'),' {"version":"v1.4.2","commit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","local_test":true}')
    [IO.File]::WriteAllText((Join-Path $payload 'DiskAtlas.runtimeconfig.json'),'{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.14"},{"name":"Microsoft.WindowsDesktop.App","version":"10.0.14"}]}}')
    foreach ($suffix in @('.exe','-source.zip','-source.tar.gz')) {
        [IO.File]::WriteAllText((Join-Path $assets ('AsukaLens v1.4.2'+$suffix)),'synthetic non-executable asset; contents validated on developer machine separately')
    }
    $fixture=[pscustomobject]@{base=$base;assets=$assets;payload=$payload;hash='';manifest=[ordered]@{schema=1;product='AsukaLens';version='v1.4.2';
        commit=('a'*40);rid='win-x64';self_contained=$true;local_test=$true;source_kind='working-tree';runtime='10.0.14';assets=@();payload_files=@()}}
    Save-Fixture $fixture -RebuildZip
    return $fixture
}
function Reject-Fixture([scriptblock]$Mutation,[string]$Name,[string]$Code) {
    $fixture=New-Fixture
    try {
        & $Mutation $fixture
        $target=Join-Path $fixture.base 'work'
        Reject { New-AcceptanceWorkspace $fixture.assets $fixture.hash $target } $Name $Code
        Check (-not (Test-Path -LiteralPath $target)) ($Name+' before writes')
    } finally { Remove-TestTree $fixture.base }
}
try {
    foreach ($script in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../scripts/acceptance') -Filter '*.ps1') {
        $bytes=[IO.File]::ReadAllBytes($script.FullName)
        Check ($bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) ('UTF-8 BOM '+$script.Name)
        $tokens=$null; $errors=$null
        [Management.Automation.Language.Parser]::ParseFile($script.FullName,[ref]$tokens,[ref]$errors) | Out-Null
        Check ($errors.Count -eq 0) ('syntax '+$script.Name)
    }
    Check ($PSVersionTable.PSVersion.Major -eq 5 -and [Environment]::Is64BitProcess) 'actual Windows PowerShell 5.1 x64 host'
    Check ((Get-AcceptanceOs 19044 'Client') -eq 'other-windows') 'older Windows 10 rejected'
    Check ((Get-AcceptanceOs 19045 'Client') -eq 'windows-10-22h2') 'Windows 10 22H2 accepted'
    Check ((Get-AcceptanceOs 22000 'Client') -eq 'windows-11') 'Windows 11 accepted'
    Check ((Get-AcceptanceOs 19045 'Server') -eq 'other-windows') 'server excluded'
    Check ((Resolve-AcceptanceRuntimeEvidence @() $false).status -eq 'not-detected') 'no known runtime evidence remains qualified'
    Check ((Resolve-AcceptanceRuntimeEvidence @() $true).status -eq 'unknown') 'incomplete probe is unknown'
    Check ((Resolve-AcceptanceRuntimeEvidence @('path_host') $true).status -eq 'detected') 'positive evidence wins with incomplete flag'
    Reject { Resolve-AcceptanceRuntimeEvidence @('private path') $false } 'runtime evidence whitelist' 'ACCEPT_ENVIRONMENT_EVIDENCE'
    $realEnvironment=Get-AcceptanceEnvironment
    Check ($realEnvironment.os -eq 'windows-11' -and $realEnvironment.architecture -eq 'x64') 'real supported host detection'
    Check ($realEnvironment.modern_dotnet.status -eq 'detected') 'real developer runtime detected'
    foreach ($bad in @('relative','C:\','C:\sample:stream','\\server\share\workspace')) { Reject { Assert-AcceptancePath $bad } 'unsafe workspace path rejected' }
    foreach ($bad in @('../escape','a/../escape','/root','C:/root','a\b','a//b','a./b','NUL.txt','src/file:stream')) { Reject { Assert-AcceptanceRelative $bad } 'unsafe entry rejected' }
    $stream=[IO.MemoryStream]::new([byte[]]@(1,2))
    try { Reject { Get-AcceptanceHash $stream 1 } 'stream length bounded' 'ACCEPT_STREAM_SIZE' } finally { $stream.Dispose() }
    # Built-in .NET Framework compiler only in tests. The target helper never compiles or calls dotnet.
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
public static class AcceptanceFixture {
    [STAThread] public static void Main() {
        string directory = Environment.GetEnvironmentVariable("ASUKALENS_DATA_DIR");
        if (String.IsNullOrEmpty(directory)) return;
        File.WriteAllText(Path.Combine(directory, "launched.txt"), "isolated");
        for (int i=0; i<200 && !File.Exists(Path.Combine(directory,"stop.txt")); i++) Thread.Sleep(100);
    }
}
'@ -OutputAssembly (Join-Path $root 'helper.exe') -OutputType WindowsApplication
    Reject-Fixture { param($f) $f.hash='0'*64 } 'manifest checksum mismatch' 'ACCEPT_MANIFEST_HASH'
    Reject-Fixture { param($f) $f.manifest.version='v01.4.2'; Save-Fixture $f } 'noncanonical version' 'ACCEPT_MANIFEST_IDENTITY'
    Reject-Fixture { param($f) Remove-Item -LiteralPath (Join-Path $f.assets 'AsukaLens v1.4.2.exe') } 'missing asset' 'ACCEPT_ASSET_DIRECTORY'
    Reject-Fixture { param($f) [IO.File]::AppendAllText((Join-Path $f.assets 'AsukaLens v1.4.2.exe'),'changed') } 'asset bytes changed' 'ACCEPT_ASSET_HASH'
    Reject-Fixture { param($f) [IO.File]::WriteAllText((Join-Path $f.assets 'extra.txt'),'unexpected') } 'extra asset' 'ACCEPT_ASSET_DIRECTORY'
    Reject-Fixture { param($f) [IO.File]::WriteAllText((Join-Path $f.assets 'SHA256SUMS.txt'),'wrong') } 'checksum list mismatch' 'ACCEPT_CHECKSUM_LIST'
    Reject-Fixture {
        param($f) $zip=[IO.Compression.ZipFile]::Open((Join-Path $f.assets 'AsukaLens v1.4.2.zip'),[IO.Compression.ZipArchiveMode]::Update)
        try { [void]$zip.CreateEntry('../outside.txt') } finally { $zip.Dispose() }; Save-Fixture $f
    } 'ZIP traversal' 'ACCEPT_ARCHIVE_PATH'
    Reject-Fixture {
        param($f) $zip=[IO.Compression.ZipFile]::Open((Join-Path $f.assets 'AsukaLens v1.4.2.zip'),[IO.Compression.ZipArchiveMode]::Update)
        try { [void]$zip.CreateEntry('diskatlas.dll') } finally { $zip.Dispose() }; Save-Fixture $f
    } 'ZIP duplicate case' 'ACCEPT_DUPLICATE_PATH'
    Reject-Fixture {
        param($f) $zip=[IO.Compression.ZipFile]::Open((Join-Path $f.assets 'AsukaLens v1.4.2.zip'),[IO.Compression.ZipArchiveMode]::Update)
        try { $entry=$zip.CreateEntry('link'); $entry.ExternalAttributes=-1610612736 } finally { $zip.Dispose() }; Save-Fixture $f
    } 'ZIP symlink rejected' 'ACCEPT_ARCHIVE_LINK'
    Reject-Fixture {
        param($f) $zip=[IO.Compression.ZipFile]::Open((Join-Path $f.assets 'AsukaLens v1.4.2.zip'),[IO.Compression.ZipArchiveMode]::Update)
        try { [void]$zip.CreateEntry('DiskAtlas.dll/child/') } finally { $zip.Dispose() }; Save-Fixture $f
    } 'ZIP file directory collision' 'ACCEPT_PATH_COLLISION'
    Reject-Fixture { param($f) [IO.File]::WriteAllText((Join-Path $f.payload 'build-info.json'),'{"version":"v9.9.9","commit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","local_test":true}'); Save-Fixture $f -RebuildZip } 'payload version mismatch' 'ACCEPT_PAYLOAD_IDENTITY'
    Reject-Fixture { param($f) [IO.File]::WriteAllText((Join-Path $f.payload 'DiskAtlas.runtimeconfig.json'),'{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.14"}}}'); Save-Fixture $f -RebuildZip } 'shared runtime requirement' 'ACCEPT_SHARED_RUNTIME_REQUIRED'
    Reject-Fixture { param($f) Remove-Item -LiteralPath (Join-Path $f.payload 'coreclr.dll'); Save-Fixture $f -RebuildZip } 'required runtime missing' 'ACCEPT_PAYLOAD_REQUIRED_FILE'
    Reject-Fixture { param($f) Remove-Item -LiteralPath (Join-Path $f.payload 'licenses/runtime/microsoft.windowsdesktop.app.runtime.win-x64/LICENSE.txt'); Save-Fixture $f -RebuildZip } 'runtime notices missing' 'ACCEPT_RUNTIME_NOTICES'
    Reject-Fixture {
        param($f) $zip=[IO.Compression.ZipFile]::Open((Join-Path $f.assets 'AsukaLens v1.4.2.zip'),[IO.Compression.ZipArchiveMode]::Update)
        try {
            $entry=$zip.GetEntry('DiskAtlas.dll'); $size=$entry.Length; $entry.Delete()
            $stream=$zip.CreateEntry('DiskAtlas.dll').Open()
            try { $stream.Write([byte[]]::new($size),0,$size) } finally { $stream.Dispose() }
        } finally { $zip.Dispose() }; Save-Fixture $f
    } 'payload content hash mismatch' 'ACCEPT_PAYLOAD_HASH'
    Reject-Fixture {
        param($f) $zip=[IO.Compression.ZipFile]::Open((Join-Path $f.assets 'AsukaLens v1.4.2.zip'),[IO.Compression.ZipArchiveMode]::Update)
        try { $zip.GetEntry('DiskAtlas.dll').Delete() } finally { $zip.Dispose() }; Save-Fixture $f
    } 'payload entry missing' 'ACCEPT_PAYLOAD_INVENTORY'
    Reject-Fixture { param($f) $f.manifest.payload_files+=@($f.manifest.payload_files[0]); Save-Fixture $f } 'duplicate inventory path' 'ACCEPT_DUPLICATE_PATH'
    Reject-Fixture { param($f) $f.manifest.self_contained=$false; Save-Fixture $f } 'framework dependent manifest rejected' 'ACCEPT_MANIFEST_IDENTITY'
    Reject-Fixture {
        param($f)
        Move-Item -LiteralPath (Join-Path $f.assets 'AsukaLens v1.4.2.exe') -Destination (Join-Path $f.assets 'AsukaLens.v1.4.2.exe')
        Save-Fixture $f
    } 'mixed legacy and GitHub-safe asset names rejected' 'ACCEPT_ASSET_NAMES'
    $safe=New-Fixture
    try {
        foreach($asset in $safe.manifest.assets) {
            Move-Item -LiteralPath (Join-Path $safe.assets $asset.path) -Destination (Join-Path $safe.assets $asset.path.Replace('AsukaLens v','AsukaLens.v'))
        }
        Save-Fixture $safe
        $safeWork=Join-Path $safe.base 'safe-work'
        New-AcceptanceWorkspace $safe.assets $safe.hash $safeWork
        Check (Test-Path -LiteralPath (Join-Path $safeWork 'portable/AsukaLens.exe')) 'GitHub-safe manifest verifies and extracts portable payload'
    } finally { Remove-TestTree $safe.base }
    $good=New-Fixture
    $workspace=Join-Path $good.base '工作目录'
    New-AcceptanceWorkspace $good.assets $good.hash $workspace
    Check (Test-Path -LiteralPath (Join-Path $workspace 'sample/图片/合成背景.png')) 'prepared synthetic image'
    Check ((Get-Item -LiteralPath (Join-Path $workspace 'sample/零字节.txt')).Length -eq 0) 'zero byte and Chinese sample'
    Reject { New-AcceptanceWorkspace $good.assets $good.hash $workspace } 'existing workspace preserved' 'ACCEPT_WORKSPACE_EXISTS'
    $session=Open-AcceptanceSession $workspace
    $report=Get-AcceptanceReport $session
    Check (@($report.operator.checks.Values | Where-Object { $_ -ne 'pending' }).Count -eq 0) 'all manual checks initially pending'
    Check ($report.binding.manifest_sha256 -eq $good.hash -and $report.release_gate -eq 'not-evaluated') 'report bound without release approval'
    Reject { Open-AcceptanceSession $workspace } 'concurrent action locked'
    Reject { Set-AcceptanceResult $session 'portable_start' 'passed' '' } 'cannot pass before launch' 'ACCEPT_LAUNCH_REQUIRED'
    Reject { Set-AcceptanceResult $session '' '' 'fresh-install' } 'developer environment cannot claim clean' 'ACCEPT_CLEAN_ENVIRONMENT_NOT_ESTABLISHED'
    Reject { Set-AcceptanceResult $session 'unknown' 'passed' '' } 'unknown checklist field rejected' 'ACCEPT_RECORD_ARGUMENTS'
    $marker=Join-Path $workspace '.acceptance-session.json'
    $before=(Get-FileHash -LiteralPath $marker).Hash
    $hold=[IO.File]::Open($marker,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try { Reject { Set-AcceptanceResult $session 'portable_start' 'failed' '' } 'write failure propagated' }
    finally { $hold.Dispose() }
    Check ((Get-FileHash -LiteralPath $marker).Hash -eq $before -and @(Get-ChildItem -LiteralPath $workspace -Filter '*.tmp' -Force).Count -eq 0) 'failed write preserves state and clears temporary file'
    $session.lock.Dispose(); $session=Open-AcceptanceSession $workspace
    $binary=Join-Path $workspace 'portable/DiskAtlas.dll'
    $original=[IO.File]::ReadAllBytes($binary)
    [IO.File]::AppendAllText($binary,'changed')
    Reject { Start-AcceptanceApp $session } 'changed extracted payload cannot launch' 'ACCEPT_PAYLOAD_CHANGED'
    [IO.File]::WriteAllBytes($binary,$original)
    $parentOverride=[Environment]::GetEnvironmentVariable('ASUKALENS_DATA_DIR')
    Start-AcceptanceApp $session
    $child=Get-Process -Id $session.state.processes[-1].pid
    for ($i=0;$i -lt 50 -and -not (Test-Path -LiteralPath (Join-Path $workspace 'data/launched.txt'));$i++) { Start-Sleep -Milliseconds 100 }
    Check (([IO.File]::ReadAllText((Join-Path $workspace 'data/launched.txt'))) -eq 'isolated') 'actual synthetic process receives isolated data'
    Check ([Environment]::GetEnvironmentVariable('ASUKALENS_DATA_DIR') -eq $parentOverride) 'parent environment unchanged'
    Reject { Start-AcceptanceApp $session } 'duplicate start blocked' 'ACCEPT_PROCESS_RUNNING'
    Reject { Remove-AcceptanceWorkspace $session } 'cleanup while process active blocked' 'ACCEPT_PROCESS_RUNNING'
    Reject { Set-AcceptanceResult $session 'normal_exit' 'passed' '' } 'normal exit cannot pass while active' 'ACCEPT_PROCESS_RUNNING'
    Set-AcceptanceResult $session 'portable_start' 'passed' ''
    Set-AcceptanceResult $session 'scan_and_navigation' 'failed' ''
    $session.state | Add-Member -NotePropertyName ignored_private_field -NotePropertyValue 'C:\Users\private-fixture\secret.txt'
    Write-AcceptanceJson $session.state $marker
    $session.lock.Dispose(); $session=Open-AcceptanceSession $workspace
    $json=Get-AcceptanceReport $session | ConvertTo-Json -Depth 16
    Check ($json -notmatch 'private-fixture|[A-Z]:\\|pid|started_utc|root') 'export excludes private fields and local paths'
    $parsed=$json | ConvertFrom-Json
    Check ($parsed.operator.checks.portable_start -eq 'passed' -and $parsed.operator.checks.scan_and_navigation -eq 'failed' -and
        $parsed.operator.checks.views_and_metrics -eq 'pending' -and $parsed.release_gate -eq 'not-evaluated') 'partial results never become full pass'
    $export=Join-Path $root 'observations.json'
    Write-AcceptanceJson (Get-AcceptanceReport $session) $export -New
    Reject { Write-AcceptanceJson (Get-AcceptanceReport $session) $export -New } 'report export cannot overwrite'
    [IO.File]::WriteAllText((Join-Path $workspace 'data/stop.txt'),'stop')
    Check ($child.WaitForExit(5000)) 'synthetic child exits normally'
    $child.Dispose(); $child=$null
    Set-AcceptanceResult $session 'normal_exit' 'passed' ''
    $outside=Join-Path $root 'unrelated'; [IO.Directory]::CreateDirectory($outside) | Out-Null
    [IO.File]::WriteAllText((Join-Path $outside 'keep.txt'),'keep')
    $link=Join-Path $workspace 'linked'
    New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
    try { Reject { Remove-AcceptanceWorkspace $session } 'cleanup rejects junction before recursion' 'ACCEPT_REPARSE_POINT' }
    finally {
        # Framework/Windows PowerShell may prompt on a junction. Remove only the link itself.
        if ($link.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -and
            ((Get-Item -LiteralPath $link -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { [IO.Directory]::Delete($link) }
    }
    $session.lock.Dispose(); $session=$null
    $state=Read-AcceptanceJson $marker; $state.root=$outside; Write-AcceptanceJson $state $marker
    Reject { Open-AcceptanceSession $workspace -Cleanup } 'copied or mismatched owner refused' 'ACCEPT_OWNER_MISMATCH'
    $state.root=$workspace; $state.phase='preparing'; Write-AcceptanceJson $state $marker
    Remove-Item -LiteralPath (Join-Path $workspace '.acceptance-manifest.json')
    Reject { Open-AcceptanceSession $workspace } 'interrupted prepare cannot launch' 'ACCEPT_PREPARATION_INCOMPLETE'
    $session=Open-AcceptanceSession $workspace -Cleanup
    Remove-AcceptanceWorkspace $session
    Check (-not (Test-Path -LiteralPath $workspace) -and (Test-Path -LiteralPath (Join-Path $outside 'keep.txt')) -and
        (Test-Path -LiteralPath (Join-Path $good.assets 'release-manifest.json'))) 'interrupted cleanup preserves sources and unrelated data'
    $session=$null
    Reject { Open-AcceptanceSession $outside -Cleanup } 'unowned directory cannot clean' 'ACCEPT_OWNER_MISSING'
    $invalidExe=New-Fixture
    [IO.File]::WriteAllText((Join-Path $invalidExe.payload 'AsukaLens.exe'),'invalid executable fixture')
    Save-Fixture $invalidExe -RebuildZip
    $invalidWork=Join-Path $invalidExe.base 'work'
    New-AcceptanceWorkspace $invalidExe.assets $invalidExe.hash $invalidWork
    $invalidSession=Open-AcceptanceSession $invalidWork
    try {
        Reject { Start-AcceptanceApp $invalidSession } 'invalid executable launch fails'
        Check ((Get-AcceptanceReport $invalidSession).automated.launches -eq 0) 'failed launch does not become successful fact'
        Remove-AcceptanceWorkspace $invalidSession
    } finally { $invalidSession.lock.Dispose() }
    $retryFixture=New-Fixture; $retryWork=Join-Path $retryFixture.base 'work'
    New-AcceptanceWorkspace $retryFixture.assets $retryFixture.hash $retryWork
    $retrySession=Open-AcceptanceSession $retryWork
    $heldPath=Join-Path $retryWork 'data/held.txt'; [IO.File]::WriteAllText($heldPath,'held')
    $hold=[IO.File]::Open($heldPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        Reject { Remove-AcceptanceWorkspace $retrySession } 'cleanup I/O failure is reported'
        Check ((Read-AcceptanceJson (Join-Path $retryWork '.acceptance-session.json')).phase -eq 'cleaning') 'failed cleanup preserves ownership marker'
    } finally { $hold.Dispose(); $retrySession.lock.Dispose() }
    Reject { Open-AcceptanceSession $retryWork } 'partial cleanup cannot relaunch' 'ACCEPT_PREPARATION_INCOMPLETE'
    $retrySession=Open-AcceptanceSession $retryWork -Cleanup
    try { Remove-AcceptanceWorkspace $retrySession } finally { $retrySession.lock.Dispose() }
    Check (-not (Test-Path -LiteralPath $retryWork)) 'cleanup retries after file released'
    # Exercise the user-facing CLI as a separate legacy PowerShell process.
    $cli=Join-Path $PSScriptRoot '../scripts/acceptance/Invoke-Acceptance.ps1'
    & (Join-Path $PSHOME 'powershell.exe') -NoLogo -NoProfile -NonInteractive -File $cli -Action Prepare -Assets $good.assets -ManifestSha256 $good.hash -Workspace $workspace | Out-Null
    Check ($LASTEXITCODE -eq 0) 'CLI prepare with Unicode paths'
    $cliJson=& (Join-Path $PSHOME 'powershell.exe') -NoLogo -NoProfile -NonInteractive -File $cli -Action Inspect -Workspace $workspace
    Check ($LASTEXITCODE -eq 0 -and ($cliJson -join "`n" | ConvertFrom-Json).release_gate -eq 'not-evaluated') 'CLI inspect JSON'
    $cliExport=Join-Path $root 'cli-export.json'
    & (Join-Path $PSHOME 'powershell.exe') -NoLogo -NoProfile -NonInteractive -File $cli -Action Export -Workspace $workspace -Output $cliExport | Out-Null
    Check ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $cliExport)) 'CLI whitelist export'
    $copiedManifest=Join-Path $workspace '.acceptance-manifest.json'
    $originalManifest=[IO.File]::ReadAllBytes($copiedManifest)
    [IO.File]::AppendAllText($copiedManifest,' ')
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'powershell.exe'))
    $start.Arguments='-NoLogo -NoProfile -NonInteractive -File "'+$cli+'" -Action Inspect -Workspace "'+$workspace+'"'
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $probe=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$probe.StandardOutput.ReadToEnd(); $stderr=$probe.StandardError.ReadToEnd(); $probe.WaitForExit()
        Check ($probe.ExitCode -eq 1 -and $stdout.Trim() -eq '' -and $stderr.Trim() -eq 'Acceptance failed: ACCEPT_SESSION_BINDING') 'CLI rejects changed binding with path-free error'
    } finally { $probe.Dispose(); [IO.File]::WriteAllBytes($copiedManifest,$originalManifest) }
    & (Join-Path $PSHOME 'powershell.exe') -NoLogo -NoProfile -NonInteractive -File $cli -Action Cleanup -Workspace $workspace | Out-Null
    Check ($LASTEXITCODE -eq 0 -and -not (Test-Path -LiteralPath $workspace)) 'CLI cleanup'
    $result=[ordered]@{schema=1;host='Windows PowerShell 5.1 x64';passed=$checks.Count;checks=$checks.ToArray();
        environment=$realEnvironment;assets='synthetic; source and installer contents not executable packages';
        child_process='synthetic .NET Framework helper, not AsukaLens';real_release_payload_tested=$false;clean_environment_tested=$false}
    if ($Summary) { Write-AcceptanceJson $result ([IO.Path]::GetFullPath($Summary)) -New }
    Write-Host ('PASS offline acceptance checks: '+$checks.Count)
} finally {
    if ($session) { $session.lock.Dispose() }
    if ($child) {
        [IO.File]::WriteAllText((Join-Path $workspace 'data/stop.txt'),'stop')
        if (-not $child.WaitForExit(5000)) { throw 'Synthetic child is still active; fixture retained.' }
        $child.Dispose()
    }
    Remove-TestTree $root
}
