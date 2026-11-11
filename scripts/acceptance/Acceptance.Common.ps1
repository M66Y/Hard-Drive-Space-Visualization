#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

$script:AcceptanceChecks = @('portable_start','scan_and_navigation','views_and_metrics','search_sort_file_helpers',
    'refresh_and_cancel','background_and_restart','diagnostics_and_notes','normal_exit','installer_workflow')

function Get-AcceptanceHash([IO.Stream]$Stream, [long]$ExpectedLength = -1) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        if ($ExpectedLength -lt 0) { $hash=$sha.ComputeHash($Stream) }
        else {
            $buffer=[byte[]]::new(65536); $total=0L
            while (($read=$Stream.Read($buffer,0,[int][Math]::Min($buffer.Length,$ExpectedLength-$total+1))) -gt 0) {
                $total+=$read
                if ($total -gt $ExpectedLength) { throw 'ACCEPT_STREAM_SIZE' }
                [void]$sha.TransformBlock($buffer,0,$read,$null,0)
            }
            if ($total -ne $ExpectedLength) { throw 'ACCEPT_STREAM_SIZE' }
            [void]$sha.TransformFinalBlock([byte[]]@(),0,0); $hash=$sha.Hash
        }
        return ([BitConverter]::ToString($hash)).Replace('-','').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Assert-AcceptancePath([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.IndexOf(':', 2) -ge 0) { throw 'ACCEPT_ABSOLUTE_LOCAL_PATH_REQUIRED' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    if ($full.Length -le 3) { throw 'ACCEPT_VOLUME_ROOT_REJECTED' }
    $cursor = $full
    while ($cursor) {
        $item=$null
        try { $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop }
        catch [System.Management.Automation.ItemNotFoundException] { }
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'ACCEPT_REPARSE_POINT' }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}

function Assert-AcceptanceTree([string]$Path) {
    [void](Assert-AcceptancePath $Path)
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'ACCEPT_REPARSE_POINT' }
        if ($item.PSIsContainer) { Assert-AcceptanceTree $item.FullName }
    }
}

function Assert-AcceptanceRelative([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.Length -gt 240 -or $Name.StartsWith('/') -or $Name.Contains('\')) { throw 'ACCEPT_ARCHIVE_PATH' }
    foreach ($part in $Name.Split('/')) {
        if ($part -in @('','.','..') -or $part -match '[<>:"|?*\x00-\x1f]' -or $part -match '[. ]$' -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'ACCEPT_ARCHIVE_PATH' }
    }
}

function Write-AcceptanceJson($Value, [string]$Path, [switch]$New) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 16) + "`n")
    if ($New) {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        return
    }
    $temp = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllBytes($temp,$bytes)
        [IO.File]::Replace($temp,$Path,[NullString]::Value)
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
}

function Read-AcceptanceJson([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or $item.Length -gt 8MB) { throw 'ACCEPT_JSON_SIZE' }
    return ([IO.File]::ReadAllText($Path) | ConvertFrom-Json)
}

function Resolve-AcceptanceRuntimeEvidence([string[]]$Evidence, [bool]$Incomplete) {
    $known = @('path_host','standard_location','registry_location','environment_location')
    if (@($Evidence | Where-Object { $_ -notin $known }).Count) { throw 'ACCEPT_ENVIRONMENT_EVIDENCE' }
    $found = @($Evidence | Sort-Object -Unique)
    $status = if ($found.Count) { 'detected' } elseif ($Incomplete) { 'unknown' } else { 'not-detected' }
    return [ordered]@{ status=$status; checks_complete=(-not $Incomplete); evidence=$found }
}

function Get-AcceptanceEnvironment {
    $evidence = [Collections.Generic.List[string]]::new()
    $incomplete = $false
    $locations = [Collections.Generic.List[object]]::new()
    try {
        if (Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue) { $evidence.Add('path_host') }
    } catch { $incomplete = $true }
    foreach ($variable in @('ProgramFiles','ProgramFiles(x86)')) {
        $base = [Environment]::GetEnvironmentVariable($variable)
        if ($base) { $locations.Add(@{ path=(Join-Path $base 'dotnet'); kind='standard_location' }) }
    }
    foreach ($scope in @('Process','User','Machine')) {
        foreach ($variable in @('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_ROOT_X86','DOTNET_ROOT_ARM64','DOTNET_ROOT(x86)')) {
            try {
                $value = [Environment]::GetEnvironmentVariable($variable,$scope)
                if ($value) { $locations.Add(@{path=$value;kind='environment_location'}) }
            } catch { $incomplete = $true }
        }
    }
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)) {
        $baseKey = $null
        try {
            $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$view)
            foreach ($arch in @('x64','x86','arm64')) {
                $key = $baseKey.OpenSubKey("SOFTWARE\dotnet\Setup\InstalledVersions\$arch")
                if ($key) {
                    try {
                        $location = $key.GetValue('InstallLocation')
                        if ($location) { $locations.Add(@{path=[string]$location;kind='registry_location'}) }
                    } finally { $key.Dispose() }
                }
            }
        } catch { $incomplete = $true } finally { if ($baseKey) { $baseKey.Dispose() } }
    }
    foreach ($location in $locations) {
        try {
            if ($location.path -notmatch '^[A-Za-z]:[\\/]') { $incomplete=$true; continue }
            foreach ($relative in @('dotnet.exe','sdk','shared')) {
                try {
                    $item = Get-Item -LiteralPath (Join-Path $location.path $relative) -Force -ErrorAction Stop
                    if ($item) { $evidence.Add($location.kind); break }
                } catch [System.Management.Automation.ItemNotFoundException] { }
            }
        } catch { $incomplete=$true }
    }
    $os = 'unknown'; $architecture = 'unknown'
    try {
        $build = [int](Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name CurrentBuildNumber)
        $installation = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name InstallationType
        $os = Get-AcceptanceOs $build $installation
        $arch = Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' -Name PROCESSOR_ARCHITECTURE
        $architecture = switch ($arch) { 'AMD64' {'x64'} 'ARM64' {'arm64'} 'x86' {'x86'} default {'unknown'} }
    } catch { }
    return [pscustomobject][ordered]@{ os=$os; architecture=$architecture; host_x64=[Environment]::Is64BitProcess;
        powershell_major=$PSVersionTable.PSVersion.Major; modern_dotnet=(Resolve-AcceptanceRuntimeEvidence $evidence.ToArray() $incomplete) }
}

function Get-AcceptanceOs([int]$Build,[string]$Installation) {
    if ($Installation -ne 'Client') { return 'other-windows' }
    if ($Build -ge 22000) { return 'windows-11' }
    if ($Build -ge 19045 -and $Build -lt 22000) { return 'windows-10-22h2' }
    return 'other-windows'
}

function Get-AcceptanceAssetStem($Manifest) {
    $legacy='AsukaLens '+$Manifest.version
    if (@($Manifest.assets | Where-Object { $_.path -ceq ($legacy+'.zip') }).Count -eq 1) { return $legacy }
    return 'AsukaLens.'+$Manifest.version
}

function Assert-AcceptanceManifest($Manifest) {
    if ($Manifest.schema -ne 1 -or $Manifest.product -cne 'AsukaLens' -or $Manifest.version -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
        $Manifest.commit -cnotmatch '^[0-9a-f]{40}$' -or $Manifest.rid -cne 'win-x64' -or $Manifest.self_contained -isnot [bool] -or
        -not $Manifest.self_contained -or $Manifest.local_test -isnot [bool] -or $Manifest.runtime -cnotmatch '^10\.0\.[0-9]+$' -or
        $Manifest.source_kind -cne $(if ($Manifest.local_test) {'working-tree'} else {'git-commit'})) { throw 'ACCEPT_MANIFEST_IDENTITY' }
    foreach ($part in $Manifest.version.Substring(1).Split('.')) { if ([long]$part -gt 65535) { throw 'ACCEPT_MANIFEST_VERSION' } }
    $stem=Get-AcceptanceAssetStem $Manifest
    $expected=@("$stem.zip","$stem.exe","$stem-source.zip","$stem-source.tar.gz")
    $actual=@($Manifest.assets | ForEach-Object { $_.path })
    if ($actual.Count -ne 4 -or (Compare-Object ($expected | Sort-Object) ($actual | Sort-Object) -CaseSensitive)) { throw 'ACCEPT_ASSET_NAMES' }
    foreach ($list in @(@{items=$Manifest.assets;limit=2GB},@{items=$Manifest.payload_files;limit=1GB})) {
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $total=0L
        if (@($list.items).Count -eq 0 -or @($list.items).Count -gt 10000) { throw 'ACCEPT_INVENTORY_LIMIT' }
        foreach ($entry in $list.items) {
            Assert-AcceptanceRelative $entry.path
            if (-not $seen.Add($entry.path)) { throw 'ACCEPT_DUPLICATE_PATH' }
            if ($entry.bytes -isnot [long] -and $entry.bytes -isnot [int]) { throw 'ACCEPT_FILE_SIZE' }
            if ($entry.bytes -lt 0 -or $entry.bytes -gt $list.limit -or $entry.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'ACCEPT_INVENTORY_VALUE' }
            $total += $entry.bytes
        }
        if ($total -gt $list.limit) { throw 'ACCEPT_INVENTORY_LIMIT' }
        foreach ($name in $seen) {
            $parent=$name
            while ($parent.Contains('/')) {
                $parent=$parent.Substring(0,$parent.LastIndexOf('/'))
                if ($seen.Contains($parent)) { throw 'ACCEPT_PATH_COLLISION' }
            }
        }
    }
    $names=@($Manifest.payload_files | ForEach-Object { $_.path })
    foreach ($required in @('AsukaLens.exe','DiskAtlas.dll','DiskAtlas.runtimeconfig.json','DiskAtlas.deps.json','coreclr.dll','hostfxr.dll','hostpolicy.dll',
        'System.Private.CoreLib.dll','System.Windows.Forms.dll','LICENSE','NOTICE.md','licenses/CC-BY-NC-4.0.txt','licenses/Inno-Setup.txt','build-info.json','INSTALL-NOTICE.txt','使用说明.txt','docs/diagnostics.md')) {
        if ($required -cnotin $names) { throw 'ACCEPT_PAYLOAD_REQUIRED_FILE' }
    }
    foreach ($pack in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
        $minimum=if ($pack -like 'microsoft.netcore.*') {2} else {1}
        if (@($names | Where-Object { $_ -clike "licenses/runtime/$pack/*" }).Count -lt $minimum) { throw 'ACCEPT_RUNTIME_NOTICES' }
    }
}

function Assert-AcceptanceZip($Zip, $Manifest) {
    $expected=@{}
    foreach ($file in $Manifest.payload_files) { $expected.Add($file.path,$file) }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $files=0
    if ($Zip.Entries.Count -gt 20000) { throw 'ACCEPT_ARCHIVE_LIMIT' }
    foreach ($entry in $Zip.Entries) {
        $name=$entry.FullName.TrimEnd('/')
        Assert-AcceptanceRelative $name
        if (-not $seen.Add($name)) { throw 'ACCEPT_DUPLICATE_PATH' }
        $mode=($entry.ExternalAttributes -shr 16) -band 0xF000
        if ($mode -notin @(0,0x8000,0x4000) -or ($entry.ExternalAttributes -band 0x400)) { throw 'ACCEPT_ARCHIVE_LINK' }
        if ($entry.FullName.EndsWith('/')) {
            if ($expected.ContainsKey($name) -or $entry.Length -ne 0) { throw 'ACCEPT_PATH_COLLISION' }
            continue
        }
        if (-not $expected.ContainsKey($name) -or $expected[$name].path -cne $name -or $expected[$name].bytes -ne $entry.Length) { throw 'ACCEPT_PAYLOAD_INVENTORY' }
        $stream=$entry.Open()
        try {
            if ((Get-AcceptanceHash $stream $entry.Length) -ne $expected[$name].sha256) { throw 'ACCEPT_PAYLOAD_HASH' }
        } finally { $stream.Dispose() }
        $files++
    }
    if ($files -ne $expected.Count) { throw 'ACCEPT_PAYLOAD_INVENTORY' }
    foreach ($name in $seen) {
        $parent=$name
        while ($parent.Contains('/')) {
            $parent=$parent.Substring(0,$parent.LastIndexOf('/'))
            if ($expected.ContainsKey($parent)) { throw 'ACCEPT_PATH_COLLISION' }
        }
    }
    function Read-PayloadJson([string]$Name) {
        $entry=$Zip.GetEntry($Name)
        if ($entry.Length -gt 1MB) { throw 'ACCEPT_JSON_SIZE' }
        $reader=[IO.StreamReader]::new($entry.Open())
        try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
    }
    $identity=Read-PayloadJson 'build-info.json'
    if ($identity.version -cne $Manifest.version -or $identity.commit -cne $Manifest.commit -or $identity.local_test -ne $Manifest.local_test) { throw 'ACCEPT_PAYLOAD_IDENTITY' }
    $runtime=(Read-PayloadJson 'DiskAtlas.runtimeconfig.json').runtimeOptions
    if ($runtime.PSObject.Properties.Name -contains 'framework' -or $runtime.PSObject.Properties.Name -contains 'frameworks') { throw 'ACCEPT_SHARED_RUNTIME_REQUIRED' }
    foreach ($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
        if (@($runtime.includedFrameworks | Where-Object { $_.name -ceq $framework -and $_.version -ceq $Manifest.runtime }).Count -ne 1) { throw 'ACCEPT_RUNTIME_VERSION' }
    }
}

function New-AcceptanceWorkspace([string]$Assets,[string]$ManifestSha256,[string]$Workspace) {
    $Assets=Assert-AcceptancePath $Assets
    $Workspace=Assert-AcceptancePath $Workspace
    if ($ManifestSha256 -cnotmatch '^[0-9a-fA-F]{64}$') { throw 'ACCEPT_MANIFEST_HASH_REQUIRED' }
    if (Test-Path -LiteralPath $Workspace) { throw 'ACCEPT_WORKSPACE_EXISTS' }
    if (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($Workspace)) -PathType Container)) { throw 'ACCEPT_PARENT_MISSING' }
    if ($Workspace.StartsWith($Assets+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'ACCEPT_WORKSPACE_INSIDE_ASSETS' }
    Assert-AcceptanceTree $Assets
    $environment=Get-AcceptanceEnvironment
    if ($environment.os -notin @('windows-10-22h2','windows-11') -or $environment.architecture -ne 'x64' -or -not $environment.host_x64) { throw 'ACCEPT_UNSUPPORTED_ENVIRONMENT' }
    $handles=[Collections.Generic.List[IO.Stream]]::new(); $zip=$null; $created=$false
    try {
        $manifestStream=[IO.File]::Open((Join-Path $Assets 'release-manifest.json'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $handles.Add($manifestStream)
        if ($manifestStream.Length -gt 8MB -or (Get-AcceptanceHash $manifestStream) -ne $ManifestSha256.ToLowerInvariant()) { throw 'ACCEPT_MANIFEST_HASH' }
        $manifestStream.Position=0
        $reader=[IO.StreamReader]::new($manifestStream,[Text.Encoding]::UTF8,$true,1024,$true)
        try { $manifestText=$reader.ReadToEnd(); $manifest=$manifestText | ConvertFrom-Json } finally { $reader.Dispose() }
        Assert-AcceptanceManifest $manifest
        $expected=@('release-manifest.json','SHA256SUMS.txt') + @($manifest.assets | ForEach-Object { $_.path })
        $actual=@(Get-ChildItem -LiteralPath $Assets -Force)
        if ($actual.Count -ne 6 -or @($actual | Where-Object { $_.PSIsContainer }).Count -or (Compare-Object ($expected | Sort-Object) ($actual.Name | Sort-Object) -CaseSensitive)) { throw 'ACCEPT_ASSET_DIRECTORY' }
        $portable=$null
        foreach ($asset in $manifest.assets) {
            $stream=[IO.File]::Open((Join-Path $Assets $asset.path),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            $handles.Add($stream)
            if ($stream.Length -ne $asset.bytes -or (Get-AcceptanceHash $stream) -ne $asset.sha256) { throw 'ACCEPT_ASSET_HASH' }
            if ($asset.path -ceq ((Get-AcceptanceAssetStem $manifest)+'.zip')) { $portable=$stream; $stream.Position=0 }
        }
        $sums=@([IO.File]::ReadAllLines((Join-Path $Assets 'SHA256SUMS.txt')))
        $expectedSums=@($manifest.assets | ForEach-Object { $_.sha256+'  '+$_.path })
        if ($sums.Count -ne 4 -or (Compare-Object $expectedSums $sums -CaseSensitive)) { throw 'ACCEPT_CHECKSUM_LIST' }
        $zip=[IO.Compression.ZipArchive]::new($portable,[IO.Compression.ZipArchiveMode]::Read,$true)
        Assert-AcceptanceZip $zip $manifest
        # All external content is verified before creating the owned workspace.
        New-Item -ItemType Directory -Path $Workspace -ErrorAction Stop | Out-Null
        $created=$true
        $state=[ordered]@{ schema=1; tool='AsukaLens offline acceptance'; id=[guid]::NewGuid().ToString('N'); root=$Workspace;
            phase='preparing'; manifest_sha256=$ManifestSha256.ToLowerInvariant(); created_utc=[DateTime]::UtcNow.ToString('o');
            processes=@(); launches=0; environment=$environment; provenance='unconfirmed'; checks=[ordered]@{} }
        foreach ($name in $script:AcceptanceChecks) { $state.checks[$name]='pending' }
        Write-AcceptanceJson $state (Join-Path $Workspace '.acceptance-session.json') -New
        # Preserve original bytes, including any BOM, for the independent hash binding.
        $manifestStream.Position=0
        $copy=[IO.File]::Open((Join-Path $Workspace '.acceptance-manifest.json'),[IO.FileMode]::Create)
        try { $manifestStream.CopyTo($copy) } finally { $copy.Dispose() }
        foreach ($dir in @('portable','data','sample','sample/图片','sample/文档','sample/文档/嵌套')) { [IO.Directory]::CreateDirectory((Join-Path $Workspace $dir)) | Out-Null }
        foreach ($entry in $zip.Entries) {
            $target=Join-Path (Join-Path $Workspace 'portable') $entry.FullName.Replace('/','\')
            if ($entry.FullName.EndsWith('/')) { [IO.Directory]::CreateDirectory($target) | Out-Null; continue }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$false)
        }
        [IO.File]::WriteAllBytes((Join-Path $Workspace 'sample/零字节.txt'),[byte[]]@())
        [IO.File]::WriteAllBytes((Join-Path $Workspace 'sample/文档/说明.txt'),[Text.Encoding]::UTF8.GetBytes('AsukaLens synthetic acceptance sample.'))
        [IO.File]::WriteAllBytes((Join-Path $Workspace 'sample/文档/嵌套/示例.bin'),[byte[]]::new(65536))
        Add-Type -AssemblyName System.Drawing
        $bitmap=[Drawing.Bitmap]::new(640,360); $graphics=[Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.Clear([Drawing.Color]::MistyRose); $graphics.FillEllipse([Drawing.Brushes]::Coral,350,60,200,200); $bitmap.Save((Join-Path $Workspace 'sample/图片/合成背景.png'),[Drawing.Imaging.ImageFormat]::Png) }
        finally { $graphics.Dispose(); $bitmap.Dispose() }
        $state.phase='ready'
        Write-AcceptanceJson $state (Join-Path $Workspace '.acceptance-session.json')
    } catch {
        # An interrupted/failed preparation stays marked preparing for explicit cleanup.
        # If the marker itself could not be written, remove only this invocation's new tree.
        if ($created -and -not (Test-Path -LiteralPath (Join-Path $Workspace '.acceptance-session.json'))) {
            Assert-AcceptanceTree $Workspace
            Remove-Item -LiteralPath $Workspace -Recurse -Force
        }
        throw
    } finally { if ($zip) { $zip.Dispose() }; foreach ($handle in $handles) { $handle.Dispose() } }
}

function Open-AcceptanceSession([string]$Workspace, [switch]$Cleanup) {
    $Workspace=Assert-AcceptancePath $Workspace
    if (-not (Test-Path -LiteralPath (Join-Path $Workspace '.acceptance-session.json') -PathType Leaf)) { throw 'ACCEPT_OWNER_MISSING' }
    Assert-AcceptanceTree $Workspace
    $lock=[IO.File]::Open((Join-Path $Workspace '.acceptance.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $state=Read-AcceptanceJson (Join-Path $Workspace '.acceptance-session.json')
        if ($state.schema -ne 1 -or $state.tool -cne 'AsukaLens offline acceptance' -or $state.id -cnotmatch '^[0-9a-f]{32}$' -or
            $state.root -ine $Workspace -or $state.manifest_sha256 -cnotmatch '^[0-9a-f]{64}$' -or
            $state.phase -notin @('preparing','ready','cleaning')) { throw 'ACCEPT_OWNER_MISMATCH' }
        if (-not $Cleanup -and $state.phase -ne 'ready') { throw 'ACCEPT_PREPARATION_INCOMPLETE' }
        $manifest=$null
        if (-not $Cleanup) {
            $file=Join-Path $Workspace '.acceptance-manifest.json'
            if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $state.manifest_sha256) { throw 'ACCEPT_SESSION_BINDING' }
            $manifest=Read-AcceptanceJson $file
            Assert-AcceptanceManifest $manifest
            if (@($state.checks.PSObject.Properties.Name).Count -ne $script:AcceptanceChecks.Count) { throw 'ACCEPT_CHECK_SCHEMA' }
            foreach ($name in $script:AcceptanceChecks) {
                if ($state.checks.$name -cnotin @('pending','passed','failed','not-tested')) { throw 'ACCEPT_CHECK_RESULT' }
            }
            if ($state.provenance -cnotin @('unconfirmed','fresh-install','known-clean-snapshot')) { throw 'ACCEPT_PROVENANCE' }
            if ($state.launches -isnot [int] -or $state.launches -lt 0) { throw 'ACCEPT_LAUNCH_COUNT' }
        }
        return [pscustomobject]@{ root=$Workspace; state=$state; manifest=$manifest; lock=$lock }
    } catch { $lock.Dispose(); throw }
}

function Assert-AcceptanceIdle($Session) {
    foreach ($record in $Session.state.processes) {
        $process=Get-Process -Id $record.pid -ErrorAction SilentlyContinue
        if ($process) {
            try { if ($process.StartTime.ToUniversalTime().ToString('o') -eq $record.started_utc) { throw 'ACCEPT_PROCESS_RUNNING' } }
            finally { $process.Dispose() }
        }
    }
    foreach ($process in @(Get-Process -Name AsukaLens -ErrorAction SilentlyContinue)) {
        try {
            $path=$process.Path
            if (-not $path) { throw 'ACCEPT_PROCESS_UNKNOWN' }
            if ($path.StartsWith($Session.root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'ACCEPT_PROCESS_RUNNING' }
        } finally { $process.Dispose() }
    }
}

function Start-AcceptanceApp($Session) {
    Assert-AcceptanceIdle $Session
    $payload=Join-Path $Session.root 'portable'
    $files=@(Get-ChildItem -LiteralPath $payload -File -Recurse -Force)
    if ($files.Count -ne @($Session.manifest.payload_files).Count) { throw 'ACCEPT_PAYLOAD_CHANGED' }
    foreach ($entry in $Session.manifest.payload_files) {
        $file=Get-Item -LiteralPath (Join-Path $payload $entry.path)
        if ($file.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { throw 'ACCEPT_PAYLOAD_CHANGED' }
    }
    $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $payload 'AsukaLens.exe'))
    $info.UseShellExecute=$false; $info.WorkingDirectory=$payload
    $info.EnvironmentVariables['ASUKALENS_DATA_DIR']=Join-Path $Session.root 'data'
    $app=[Diagnostics.Process]::Start($info)
    try {
        $record=[ordered]@{ pid=$app.Id; started_utc=$app.StartTime.ToUniversalTime().ToString('o') }
        # Only one launch may be active; retain its identity without growing a history forever.
        $Session.state.processes=@($record)
        $Session.state.launches++
        Write-AcceptanceJson $Session.state (Join-Path $Session.root '.acceptance-session.json')
    } finally { $app.Dispose() }
}

function Set-AcceptanceResult($Session,[string]$Check,[string]$Result,[string]$Provenance) {
    if ($Provenance) {
        if ($Check -or $Result -or $Provenance -cnotin @('unconfirmed','fresh-install','known-clean-snapshot')) { throw 'ACCEPT_RECORD_ARGUMENTS' }
        if ($Provenance -ne 'unconfirmed') {
            $current=Get-AcceptanceEnvironment
            if ($Session.state.environment.modern_dotnet.status -ne 'not-detected' -or -not $Session.state.environment.modern_dotnet.checks_complete -or
                $current.modern_dotnet.status -ne 'not-detected' -or -not $current.modern_dotnet.checks_complete -or
                $current.os -notin @('windows-10-22h2','windows-11') -or $current.os -ne $Session.state.environment.os -or $current.architecture -ne 'x64') { throw 'ACCEPT_CLEAN_ENVIRONMENT_NOT_ESTABLISHED' }
        }
        $Session.state.provenance=$Provenance
    } else {
        if ($Check -cnotin $script:AcceptanceChecks -or $Result -cnotin @('pending','passed','failed','not-tested')) { throw 'ACCEPT_RECORD_ARGUMENTS' }
        if ($Result -eq 'passed' -and $Session.state.launches -eq 0 -and $Check -ne 'installer_workflow') { throw 'ACCEPT_LAUNCH_REQUIRED' }
        if ($Check -eq 'normal_exit' -and $Result -eq 'passed') { Assert-AcceptanceIdle $Session }
        $Session.state.checks.$Check=$Result
    }
    Write-AcceptanceJson $Session.state (Join-Path $Session.root '.acceptance-session.json')
}

function Get-AcceptanceReport($Session) {
    $envState=$Session.state.environment
    # Whitelist reconstruction: local owner paths, process IDs and free text never leave the workspace.
    if ($envState.os -cnotin @('windows-10-22h2','windows-11','other-windows','unknown') -or $envState.architecture -cnotin @('x64','x86','arm64','unknown') -or
        $envState.host_x64 -isnot [bool] -or $envState.powershell_major -notin @(5,7) -or $envState.modern_dotnet.checks_complete -isnot [bool]) { throw 'ACCEPT_ENVIRONMENT_SCHEMA' }
    $runtime=Resolve-AcceptanceRuntimeEvidence @($envState.modern_dotnet.evidence) (-not $envState.modern_dotnet.checks_complete)
    if ($runtime.status -cne $envState.modern_dotnet.status) { throw 'ACCEPT_ENVIRONMENT_SCHEMA' }
    $checks=[ordered]@{}
    foreach ($name in $script:AcceptanceChecks) { $checks[$name]=$Session.state.checks.$name }
    return [ordered]@{ schema=1; report_kind='offline-acceptance-observations';
        binding=[ordered]@{version=$Session.manifest.version;commit=$Session.manifest.commit;manifest_sha256=$Session.state.manifest_sha256;
            local_test=$Session.manifest.local_test;source_kind=$Session.manifest.source_kind};
        automated=[ordered]@{assets_verified=4;payload_files=@($Session.manifest.payload_files).Count;launches=$Session.state.launches;
            environment=[ordered]@{os=$envState.os;architecture=$envState.architecture;host_x64=$envState.host_x64;
                powershell_major=[int]$envState.powershell_major;modern_dotnet=$runtime}};
        operator=[ordered]@{clean_environment_provenance=$Session.state.provenance;checks=$checks};
        release_gate='not-evaluated'; source_archive_contents='requires-development-validation'; installer_execution='operator-record-only' }
}

function Remove-AcceptanceWorkspace($Session) {
    Assert-AcceptanceIdle $Session
    Assert-AcceptanceTree $Session.root
    $Session.state.phase='cleaning'
    Write-AcceptanceJson $Session.state (Join-Path $Session.root '.acceptance-session.json')
    # Keep ownership and lock until all removable content is gone, so an I/O failure is retryable.
    foreach ($item in Get-ChildItem -LiteralPath $Session.root -Force) {
        if ($item.Name -in @('.acceptance-session.json','.acceptance.lock')) { continue }
        if (-not $item.FullName.StartsWith($Session.root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'ACCEPT_CLEANUP_BOUNDARY' }
        if ($item.PSIsContainer) { Remove-Item -LiteralPath $item.FullName -Recurse -Force }
        else { Remove-Item -LiteralPath $item.FullName -Force }
    }
    # Release the Windows file lock only after marking the session unavailable for launch.
    $Session.lock.Dispose()
    [void](Assert-AcceptancePath $Session.root)
    Remove-Item -LiteralPath (Join-Path $Session.root '.acceptance.lock') -Force
    Remove-Item -LiteralPath (Join-Path $Session.root '.acceptance-session.json') -Force
    [IO.Directory]::Delete($Session.root)
}
