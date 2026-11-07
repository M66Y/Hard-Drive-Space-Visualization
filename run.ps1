[CmdletBinding()]
param([switch]$BuildOnly)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'DiskAtlas.csproj'
[xml]$definition = Get-Content -LiteralPath $project -Raw -Encoding UTF8
$framework = [string]$definition.Project.PropertyGroup.TargetFramework
$assembly = [string]$definition.Project.PropertyGroup.AssemblyName
if ($framework -notmatch '^[A-Za-z0-9._-]+$' -or $assembly -notmatch '^[A-Za-z0-9._-]+$') {
    throw '项目的运行目标或程序名称无效。'
}
$output = Join-Path $PSScriptRoot ('bin\Release\' + $framework)
$executable = Join-Path $output ($assembly + '.exe')
$dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $dotnet) { throw '未找到 .NET SDK。请按 README 安装 .NET 10 SDK 后重试。' }

# Serialize launch/build within this checkout without writing a persistent lock.
$sha = [Security.Cryptography.SHA256]::Create()
try { $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($PSScriptRoot.ToUpperInvariant()))).Replace('-', '') }
finally { $sha.Dispose() }
$mutex = [Threading.Mutex]::new($false, ('Local\AsukaLens.DevelopmentLaunch.' + $key))
$held = $false
$oldTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$oldCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
Push-Location $PSScriptRoot
try {
    try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
    if (-not $held) { throw '另一个开发版构建或启动任务正在执行，请等待它完成。' }
    $running = Get-Process -Name $assembly -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and [string]::Equals($_.Path, $executable, [StringComparison]::OrdinalIgnoreCase) }
        catch { $false }
    }
    if ($running) { throw '当前开发版仍在运行。请先关闭它，再构建并启动最新源码。' }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    & $dotnet.Source build $project --configuration Release --output $output --nologo -p:TargetPlatformDisplayName=Windows -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw '开发版构建失败，未启动旧程序。请查看上面的构建错误。' }
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw '构建未产生预期的启动文件，未启动程序。' }
    if ($BuildOnly) { Write-Host '开发预览构建完成。'; return }
    # This is the interactive application the user requested, not a background helper.
    Start-Process -FilePath $executable -WorkingDirectory $PSScriptRoot | Out-Null
    Write-Host '已启动 AsukaLens 开发版。版本与构建标识见“诊断 → 开发记录说明”。'
}
finally {
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', $oldTelemetry, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_GENERATE_ASPNET_CERTIFICATE', $oldCertificate, 'Process')
    if ($held) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
    Pop-Location
}
