#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Compiler)
. (Join-Path $PSScriptRoot '../scripts/release/Release.Common.ps1')
$Directory = (Resolve-Path -LiteralPath $Directory).Path
$Compiler = (Resolve-Path -LiteralPath $Compiler).Path
& (Join-Path $PSScriptRoot '../scripts/release/Test-Release.ps1') -Directory $Directory | Out-Host
$manifest = Get-Content -LiteralPath (Join-Path $Directory 'release-manifest.json') -Raw | ConvertFrom-Json
if (-not $manifest.local_test) { throw 'This test only accepts the isolated LOCAL TEST installer identity.' }
$version = $manifest.version
$parent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
$root = Join-Path $parent ('package-acceptance-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$registry = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/AsukaLens.LocalTest_is1'
$shortcuts = @((Join-Path ([Environment]::GetFolderPath('Programs')) 'AsukaLens (LOCAL TEST).lnk'),
    (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'AsukaLens (LOCAL TEST).lnk'))
if ((Test-Path -LiteralPath $registry) -or @($shortcuts | Where-Object { Test-Path -LiteralPath $_ }).Count) { throw 'Existing LOCAL TEST installation must be reviewed; refusing to replace it.' }
$dataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AsukaLens'
$dataWasPresent = Test-Path -LiteralPath $dataRoot
Assert-NoReparse $dataRoot
[IO.Directory]::CreateDirectory($dataRoot) | Out-Null
$sentinel = Join-Path $dataRoot ('release-acceptance-' + [guid]::NewGuid().ToString('N') + '.txt')
[IO.File]::WriteAllText($sentinel, 'owned synthetic data: preserve during upgrade/uninstall')
$installation = Join-Path $root 'installed'
$script:checks = [Collections.Generic.List[string]]::new()
function Pass([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:checks.Add($Name); Write-Host ('PASS ' + $Name)
}
function Run-Installer([string]$Exe, [string]$Log, [string[]]$Extra = @()) {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LOG="' + (Join-Path $root $Log) + '"')) + $Extra
    $process = Start-Process -FilePath $Exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { throw 'Installer timed out; it was not terminated automatically.' }
    return $process.ExitCode
}
function Assert-InstalledPayload {
    foreach ($file in $manifest.payload_files) {
        if ((Get-FileHash -LiteralPath (Join-Path $installation $file.path)).Hash.ToLowerInvariant() -ne $file.sha256) { throw 'Installed payload differs from portable payload.' }
    }
}
try {
    $payload = Join-Path $root 'payload'
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $Directory "AsukaLens $version.zip"),$payload)
    # Prepare a separate host using the same payload CoreCLR and Windows Desktop runtime.
    $hostRoot = Join-Path $root 'smoke'
    [IO.Directory]::CreateDirectory($hostRoot) | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $payload -Force) { Copy-Item -LiteralPath $item.FullName -Destination $hostRoot -Recurse }
    foreach ($name in @('DiskAtlas.Tests.exe','DiskAtlas.Tests.dll')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('bin/Release/net10.0-windows/' + $name)) -Destination $hostRoot
    }
    Copy-Item -LiteralPath (Join-Path $payload 'DiskAtlas.runtimeconfig.json') -Destination (Join-Path $hostRoot 'DiskAtlas.Tests.runtimeconfig.json')
    $hostDeps = Get-Content -LiteralPath (Join-Path $payload 'DiskAtlas.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $runtimeTarget = $hostDeps.targets[$hostDeps.runtimeTarget.name]
    $applicationKey = @($runtimeTarget.Keys | Where-Object { $_ -like 'DiskAtlas/*' })
    if ($applicationKey.Count -ne 1) { throw 'Unexpected application dependency entry.' }
    $runtimeTarget[$applicationKey[0]].runtime['DiskAtlas.Tests.dll'] = @{}
    Write-ReleaseJson $hostDeps (Join-Path $hostRoot 'DiskAtlas.Tests.deps.json')
    # Run the interactive WinForms test normally: SW_HIDE breaks SplitContainer's first GDI paint.
    & (Join-Path $hostRoot 'DiskAtlas.Tests.exe') --package-smoke 1> (Join-Path $root 'smoke.out') 2> (Join-Path $root 'smoke.err')
    $smokeExit = $LASTEXITCODE
    Get-Content -LiteralPath (Join-Path $root 'smoke.out') | Write-Host
    if ($smokeExit -ne 0) { Get-Content -LiteralPath (Join-Path $root 'smoke.err') | Write-Host; throw 'Package smoke failed.' }
    Pass $true 'shipped assembly workflow with bundled runtime and isolated settings'
    # Both source archives were compared byte-for-byte; build each extraction independently.
    foreach ($format in @('zip','tar.gz')) {
        $source = Join-Path $root ('source-' + $format.Replace('.','-'))
        [IO.Directory]::CreateDirectory($source) | Out-Null
        if ($format -eq 'zip') { [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $Directory "AsukaLens $version-source.zip"),$source) }
        else {
            $file=[IO.File]::OpenRead((Join-Path $Directory "AsukaLens $version-source.tar.gz"))
            $gzip=[IO.Compression.GZipStream]::new($file,[IO.Compression.CompressionMode]::Decompress)
            try { [System.Formats.Tar.TarFile]::ExtractToDirectory($gzip,$source,$false) } finally { $gzip.Dispose(); $file.Dispose() }
        }
        Push-Location $source
        try { Invoke-ReleaseTool dotnet @('build','DiskAtlas.csproj','-c','Release','--nologo','-p:TargetPlatformDisplayName=Windows','-p:NuGetAudit=false') }
        finally { Pop-Location }
        Pass $true ('source build: ' + $format)
    }
    # A synthetic older installer tests the upgrade mechanism, not historical data migrations.
    $older = Join-Path $root 'older'
    [IO.Directory]::CreateDirectory($older) | Out-Null
    Invoke-ReleaseTool $Compiler @('--quiet','--no-ide-signtools','--define=AppVersion=1.4.1',('--define=PayloadDir='+$payload),
        ('--define=OutputDir='+$older),'--define=LocalTest=1',(Join-Path $PSScriptRoot '../scripts/release/AsukaLens.iss'))
    $oldExe = Join-Path $older 'AsukaLens v1.4.1.exe'
    $unrelated = Join-Path $root 'unrelated'
    [IO.Directory]::CreateDirectory($unrelated) | Out-Null
    [IO.File]::WriteAllText((Join-Path $unrelated 'keep.txt'),'unrelated synthetic file')
    Pass ((Run-Installer $oldExe 'unrelated.log' @(('/DIR="'+$unrelated+'"'))) -ne 0) 'nonempty unrelated directory rejected'
    Pass ([IO.File]::ReadAllText((Join-Path $unrelated 'keep.txt')) -eq 'unrelated synthetic file') 'unrelated directory remains unchanged'
    Pass ((Run-Installer $oldExe 'install.log' @(('/DIR="'+$installation+'"'),'/TASKS=desktopicon')) -eq 0) 'standard-user installation'
    Pass ((Test-Path -LiteralPath $registry) -and @($shortcuts | Where-Object { Test-Path -LiteralPath $_ }).Count -eq 2) 'current-user registration and shortcuts'
    Assert-InstalledPayload
    $setup = Join-Path $Directory "AsukaLens $version.exe"
    Pass ((Run-Installer $setup 'upgrade.log') -eq 0) 'same-identity upgrade'
    Assert-InstalledPayload
    Pass ((Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq $version.Substring(1)) 'upgraded version registration'
    Pass ((Run-Installer $oldExe 'downgrade.log') -ne 0) 'downgrade rejected'
    Assert-InstalledPayload
    $running = [Threading.Mutex]::new($false,'Local\AsukaLens.Running')
    try {
        Pass ((Run-Installer $setup 'running-install.log') -ne 0) 'running application blocks setup without termination'
        Pass ((Run-Installer (Join-Path $installation 'unins000.exe') 'running-uninstall.log') -ne 0) 'running application blocks uninstall'
    } finally { $running.Dispose() }
    # A changed file sorts before the locked EXE, proving rollback restores bytes already replaced.
    $probeName = '000-recovery-probe.txt'
    [IO.File]::WriteAllText((Join-Path $installation $probeName),'previous synthetic bytes')
    [IO.File]::WriteAllText((Join-Path $payload $probeName),'candidate synthetic bytes')
    $failureBuild=Join-Path $root 'failure-candidate'; [IO.Directory]::CreateDirectory($failureBuild) | Out-Null
    Invoke-ReleaseTool $Compiler @('--quiet','--no-ide-signtools',('--define=AppVersion='+$version.Substring(1)),('--define=PayloadDir='+$payload),
        ('--define=OutputDir='+$failureBuild),'--define=LocalTest=1',(Join-Path $PSScriptRoot '../scripts/release/AsukaLens.iss'))
    $locked = [IO.File]::Open((Join-Path $installation 'AsukaLens.exe'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
    try { Pass ((Run-Installer (Join-Path $failureBuild "AsukaLens $version.exe") 'locked.log') -ne 0) 'locked payload aborts installation' }
    finally { $locked.Dispose() }
    $failureLog=[IO.File]::ReadAllText((Join-Path $root 'locked.log'))
    Pass (@($failureLog.Split('-- File entry --') | Where-Object { $_.Contains($probeName) -and $_.Contains('Successfully installed the file.') }).Count -eq 1) 'failure occurs after the probe was actually overwritten'
    Pass ([IO.File]::ReadAllText((Join-Path $installation $probeName)) -eq 'previous synthetic bytes') 'rollback restores a file replaced before the failure'
    Pass ((Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq $version.Substring(1) -and
        @($shortcuts | Where-Object { Test-Path -LiteralPath $_ }).Count -eq 2 -and
        (Test-Path -LiteralPath (Join-Path $installation 'unins000.exe'))) 'failed replacement preserves registration shortcuts and uninstaller'
    Pass (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'InstallerRecovery-AsukaLens.LocalTest'))) 'successful recovery removes its backup files'
    Remove-Item -LiteralPath (Join-Path $installation $probeName)
    Assert-InstalledPayload
    Pass $true 'old payload remains intact after failed replacement'
    [IO.File]::WriteAllText((Join-Path $installation 'unowned.txt'),'installer must not delete unknown files')
    Pass ((Run-Installer (Join-Path $installation 'unins000.exe') 'uninstall.log') -eq 0) 'uninstall succeeds'
    Pass (-not (Test-Path -LiteralPath $registry) -and @($shortcuts | Where-Object { Test-Path -LiteralPath $_ }).Count -eq 0) 'registration and shortcuts removed'
    Pass ((Test-Path -LiteralPath $sentinel) -and (Test-Path -LiteralPath (Join-Path $installation 'unowned.txt'))) 'user data and unowned files preserved'
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    $result=[ordered]@{ schema=1; version=$version; local_test=$true; elevated=$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);
        passed=$script:checks.ToArray(); clean_machine_without_dotnet='not tested; local bundle resolution verified';
        historical_version_migration='not tested; synthetic older wrapper used'; formal_release=$false }
    Write-ReleaseJson $result (Join-Path $root 'acceptance.json')
    Write-Host ('Acceptance evidence: ' + $root)
} finally {
    # Do not delete an active installation; preserve it for repair if its uninstaller fails.
    if (Test-Path -LiteralPath $registry) {
        $uninstall=Join-Path $installation 'unins000.exe'
        if (Test-Path -LiteralPath $uninstall) { Run-Installer $uninstall 'cleanup-uninstall.log' | Out-Null }
    }
    if (Test-Path -LiteralPath $sentinel) { Remove-Item -LiteralPath $sentinel }
    if (-not $dataWasPresent -and @(Get-ChildItem -LiteralPath $dataRoot -Force).Count -eq 0) { Remove-Item -LiteralPath $dataRoot }
    # Evidence/output is intentionally left for review and controlled end-of-iteration cleanup.
}
