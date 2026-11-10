#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$Compiler,
    [switch]$LocalTest,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../../artifacts/releases')
)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$numeric = Assert-ReleaseVersion $Version
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ($OutputRoot.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
    -not $OutputRoot.StartsWith((Join-Path $repository 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Inside the checkout, release output must be below artifacts/.'
}
if ($OutputRoot -eq $repository) { throw 'Cannot use the repository root for release output.' }
$Compiler = (Resolve-Path -LiteralPath $Compiler).Path
Assert-NoReparse $OutputRoot
Assert-NoReparse $Compiler
$tools = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'toolchain.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $Compiler).Hash.ToLowerInvariant() -ne $tools.iscc_sha256) { throw 'Compiler does not match the pinned toolchain.' }
$compilerVersion = (& $Compiler --version).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -ne $tools.inno_version) { throw 'Unexpected compiler version.' }
$compilerLicense = Join-Path (Split-Path -Parent $Compiler) 'License.txt'
if ((Get-FileHash -LiteralPath $compilerLicense).Hash.ToLowerInvariant() -ne $tools.inno_license_sha256) { throw 'Compiler license does not match.' }
$destination = Join-Path $OutputRoot ($Version + $(if ($LocalTest) { '-local-test' } else { '' }))
if (Test-Path -LiteralPath $destination) { throw 'Snapshot already exists. Choose a new version or explicitly remove your disposable local-test output.' }
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$lock = [IO.File]::Open(($destination + '.lock'), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$stage = Join-Path $OutputRoot ('.stage-' + [guid]::NewGuid().ToString('N'))
$oldTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$oldCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    $source = Join-Path $stage 'source'
    New-SourceSnapshot $repository $Commit $source -LocalTest:$LocalTest
    [xml]$project = Get-Content -LiteralPath (Join-Path $source 'DiskAtlas.csproj') -Raw
    if ([string]$project.Project.PropertyGroup.Version -ne $numeric) { throw 'Requested version does not match DiskAtlas.csproj.' }
    $sourceFiles = @(Get-FileInventory $source)
    $out = Join-Path $stage 'assets'
    $payload = Join-Path $stage 'payload'
    [IO.Directory]::CreateDirectory($out) | Out-Null
    New-SourceArchives $source (Join-Path $out "AsukaLens $Version-source.zip") (Join-Path $out "AsukaLens $Version-source.tar.gz")
    $cache = Join-Path $repository 'artifacts/release-cache'
    Assert-NoReparse $cache
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    Push-Location $source
    try {
        $sdk = (& dotnet --version).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'SDK unavailable.' }
        Invoke-ReleaseTool dotnet @('publish','DiskAtlas.csproj','-c','Release','-r',$tools.rid,'--self-contained','true','--output',$payload,
            '--nologo','-p:TargetPlatformDisplayName=Windows','-p:NuGetAudit=false','-p:DebugType=None','-p:DebugSymbols=false',
            '-p:PublishSingleFile=false','-p:PublishTrimmed=false','-p:PublishAot=false',('-p:RuntimeFrameworkVersion='+$tools.runtime),
            ('-p:RestorePackagesPath='+$cache),('-p:RestoreConfigFile='+(Join-Path $PSScriptRoot 'NuGet.release.config')),
            ('-p:SourceRevisionId='+$Commit))
    } finally { Pop-Location }
    Move-Item -LiteralPath (Join-Path $payload 'DiskAtlas.exe') -Destination (Join-Path $payload 'AsukaLens.exe')
    foreach ($name in @('LICENSE','NOTICE.md','licenses/CC-BY-NC-4.0.txt','assets/README.md','使用说明.txt','docs/diagnostics.md')) {
        $target = Join-Path $payload $name
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination $target
    }
    Copy-Item -LiteralPath (Join-Path $source 'assets/app.ico') -Destination (Join-Path $payload 'app.ico')
    $licenseRoot = Join-Path $payload 'licenses/runtime'
    foreach ($pack in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
        $packRoot = Join-Path $cache ($pack + '/' + $tools.runtime)
        $licenses = @(Get-ChildItem -LiteralPath $packRoot -File | Where-Object { $_.Name -match '(?i)license|third.*notices' })
        $minimum = if ($pack -like 'microsoft.netcore.*') { 2 } else { 1 }
        if ($licenses.Count -lt $minimum) { throw 'Runtime license / notices missing.' }
        $target = Join-Path $licenseRoot $pack
        [IO.Directory]::CreateDirectory($target) | Out-Null
        foreach ($license in $licenses) { Copy-Item -LiteralPath $license.FullName -Destination $target }
    }
    Copy-Item -LiteralPath $compilerLicense -Destination (Join-Path $payload 'licenses/Inno-Setup.txt')
    $notice = @"
AsukaLens $Version — M66Y
Windows 10 22H2 x64 and Windows 11 x64; .NET $($tools.runtime) runtime included.
Noncommercial use: project code uses PolyForm Noncommercial 1.0.0.
Documentation/artwork use CC BY-NC 4.0. Third-party components retain their own licenses.
See LICENSE, NOTICE.md and licenses/. AI-generated artwork by M66Y.
Unzip the entire portable folder and launch AsukaLens.exe. No administrator rights are required.
Settings, backgrounds and diagnostics remain in the current user's AppData, including portable mode.
Uninstall removes program files and shortcuts; it keeps user data. Close the app before upgrading.

简体中文：本版本内含 .NET 运行时。便携版完整解压后运行 AsukaLens.exe。
仅供非商业用途，许可和署名见 LICENSE、NOTICE.md 和 licenses 目录。
安装仅针对当前用户；升级前请关闭软件，卸载保留背景、诊断与问题记录。
便携版设置仍保存于当前用户 AppData，详细操作见“使用说明.txt”。
"@
    if ($LocalTest) { $notice = "LOCAL TEST ONLY / 仅供本机验收，不得发布`r`n" + $notice }
    [IO.File]::WriteAllText((Join-Path $payload 'INSTALL-NOTICE.txt'), $notice, [Text.UTF8Encoding]::new($true))
    Write-ReleaseJson ([ordered]@{ schema=1; version=$Version; commit=$Commit; local_test=[bool]$LocalTest; runtime=$tools.runtime }) (Join-Path $payload 'build-info.json')
    $payloadFiles = @(Get-FileInventory $payload)
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, (Join-Path $out "AsukaLens $Version.zip"), [IO.Compression.CompressionLevel]::Optimal, $false)
    Invoke-ReleaseTool $Compiler @('--quiet','--no-ide-signtools',('--define=AppVersion='+$numeric),('--define=PayloadDir='+$payload),
        ('--define=OutputDir='+$out),('--define=LocalTest='+[int][bool]$LocalTest),(Join-Path $PSScriptRoot 'AsukaLens.iss'))
    $assets = @(Get-FileInventory $out)
    $manifest = [ordered]@{ schema=1; product='AsukaLens'; version=$Version; commit=$Commit; local_test=[bool]$LocalTest;
        source_kind=$(if ($LocalTest) {'working-tree'} else {'git-commit'}); rid=$tools.rid; self_contained=$true;
        sdk=$sdk; runtime=$tools.runtime; inno=$compilerVersion; iscc_sha256=$tools.iscc_sha256;
        assets=$assets; source_files=$sourceFiles; payload_files=$payloadFiles }
    Write-ReleaseJson $manifest (Join-Path $out 'release-manifest.json')
    [IO.File]::WriteAllLines((Join-Path $out 'SHA256SUMS.txt'), @($assets | ForEach-Object { $_.sha256 + '  ' + $_.path }), [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'Test-Release.ps1') -Directory $out | Out-Host
    # Atomic promotion within one parent: incomplete runs never occupy a snapshot name.
    Move-Item -LiteralPath $out -Destination $destination
    Write-Host ('Verified local assets: ' + $destination)
} finally {
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT',$oldTelemetry,'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_GENERATE_ASPNET_CERTIFICATE',$oldCertificate,'Process')
    try { Remove-OwnedDirectory $stage $OutputRoot }
    finally { $lock.Dispose(); Remove-Item -LiteralPath ($destination + '.lock') }
}
