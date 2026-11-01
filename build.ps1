param([switch]$Test)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $env:TEMP 'DiskAtlas-dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
Push-Location $PSScriptRoot
$stage = Join-Path $PSScriptRoot ('artifacts\publish-' + [Guid]::NewGuid().ToString('N'))
try {
    $running = Get-Process 'AsukaLens','空间透镜','DiskAtlas' -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and ($_.Path -eq (Join-Path $PSScriptRoot '软件\AsukaLens.exe') -or $_.Path -eq (Join-Path $PSScriptRoot '软件\空间透镜.exe') -or $_.Path -eq (Join-Path $PSScriptRoot 'bin\Release\net9.0-windows\DiskAtlas.exe'))
    }
    if ($running) { throw 'Close this project''s running app before building, then try again.' }
    if ($Test) {
        dotnet run --project tests/DiskAtlas.Tests.csproj --configuration Release -p:TargetPlatformDisplayName=Windows -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
        dotnet tests/bin/Release/net9.0-windows/DiskAtlas.Tests.dll --advanced-tests
        if ($LASTEXITCODE -ne 0) { throw 'Advanced tests failed.' }
    }
    dotnet publish DiskAtlas.csproj --configuration Release --output $stage --nologo -p:TargetPlatformDisplayName=Windows -p:NuGetAudit=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. If the app is running, close it and try again.' }
    Move-Item -LiteralPath (Join-Path $stage 'DiskAtlas.exe') -Destination (Join-Path $stage 'AsukaLens.exe')
    foreach ($document in @('使用说明.txt', '优化审查记录.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $document) -Destination (Join-Path $stage $document)
    }
    $release = Join-Path $PSScriptRoot '软件'
    New-Item -ItemType Directory -Path $release -Force | Out-Null
    $files = @('AsukaLens.exe', 'DiskAtlas.dll', 'DiskAtlas.deps.json', 'DiskAtlas.runtimeconfig.json', '使用说明.txt', '优化审查记录.md')
    foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $stage $file) -Destination (Join-Path $release $file) -Force }
    [xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'DiskAtlas.csproj') -Raw -Encoding UTF8
    $version = [string]$project.Project.PropertyGroup.Version
    $archive = Join-Path $PSScriptRoot ("AsukaLens-$version-Windows.zip")
    Compress-Archive -LiteralPath @($files | ForEach-Object { Join-Path $stage $_ }) -DestinationPath $archive -Force
    Write-Host "Ready: $release\AsukaLens.exe"
    Write-Host "Package: $archive"
}
finally {
    $stageAbsolute = [System.IO.Path]::GetFullPath($stage)
    $stagePrefix = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts\publish-'))
    if (-not $stageAbsolute.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected staging cleanup path' }
    if (Test-Path -LiteralPath $stageAbsolute) { Remove-Item -LiteralPath $stageAbsolute -Recurse -Force }
    Pop-Location
}
