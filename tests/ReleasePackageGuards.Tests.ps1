#requires -Version 7.4
param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Compiler)
. (Join-Path $PSScriptRoot '../scripts/release/Release.Common.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Directory = (Resolve-Path -LiteralPath $Directory).Path
$Compiler = (Resolve-Path -LiteralPath $Compiler).Path
$manifest = Get-Content -LiteralPath (Join-Path $Directory 'release-manifest.json') -Raw | ConvertFrom-Json
if (-not $manifest.local_test) { throw 'Use disposable local-test assets.' }
$parent = Join-Path $repo 'artifacts'
$root = Join-Path $parent ('release-guards-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$script:passed=0
function Reject-Command([string[]]$Arguments, [string]$Pattern, [string]$Name) {
    $output = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile @Arguments 2>&1
    if ($LASTEXITCODE -eq 0 -or (($output -join "`n") -notmatch $Pattern)) { throw ('Guard failed: ' + $Name) }
    $script:passed++; Write-Host ('PASS ' + $Name)
}
try {
    $builder=Join-Path $repo 'scripts/release/New-Release.ps1'
    $commit=(& git -C $repo rev-parse HEAD).Trim()
    $before=(Get-FileHash -LiteralPath (Join-Path $Directory 'release-manifest.json')).Hash
    Reject-Command @('-File',$builder,'-Version',$manifest.version,'-Commit',$commit,'-Compiler',$Compiler,'-LocalTest','-OutputRoot',(Split-Path -Parent $Directory)) 'Snapshot already exists' 'duplicate snapshot rejected'
    if ((Get-FileHash -LiteralPath (Join-Path $Directory 'release-manifest.json')).Hash -ne $before) { throw 'Existing snapshot changed.' }
    $script:passed++; Write-Host 'PASS existing snapshot unchanged'
    if (& git -C $repo status --porcelain) {
        Reject-Command @('-File',$builder,'-Version',$manifest.version,'-Commit',$commit,'-Compiler',$Compiler,'-OutputRoot',$root) 'clean checkout' 'formal dirty checkout rejected before build'
    }
    Reject-Command @('-File',$builder,'-Version','v65535.65535.65535','-Commit',$commit,'-Compiler',$Compiler,'-LocalTest','-OutputRoot',$root) 'does not match DiskAtlas.csproj' 'project version mismatch rejected'
    Reject-Command @('-File',$builder,'-Version',$manifest.version,'-Commit',$commit,'-Compiler',(Join-Path $root 'absent.exe'),'-LocalTest','-OutputRoot',$root) 'does not exist' 'missing compiler fails before staging'
    Reject-Command @('-File',(Join-Path $repo 'scripts/release/Upload-Draft.ps1'),'-Directory',$Directory,'-Notes','unused.md','-Acceptance','unused.json','-WhatIf') 'Local-test assets can never be uploaded' 'test package upload rejected before credentials or network'
    $corrupt=Join-Path $root 'corrupt'; [IO.Directory]::CreateDirectory($corrupt) | Out-Null
    Copy-Item -LiteralPath (Join-Path $Directory 'release-manifest.json'),(Join-Path $Directory 'SHA256SUMS.txt') -Destination $corrupt
    foreach ($asset in $manifest.assets) { [IO.File]::WriteAllBytes((Join-Path $corrupt $asset.path),[byte[]]@(0)) }
    Reject-Command @('-File',(Join-Path $repo 'scripts/release/Test-Release.ps1'),'-Directory',$corrupt) 'Asset checksum mismatch' 'corrupt assets rejected'
    [IO.File]::WriteAllText((Join-Path $corrupt 'unexpected.txt'),'extra')
    Reject-Command @('-File',(Join-Path $repo 'scripts/release/Test-Release.ps1'),'-Directory',$corrupt) 'Unexpected files' 'extra release files rejected'
    if (@(Get-ChildItem -LiteralPath $root -Force | Where-Object { $_.Name -like '.stage-*' -or $_.Name -like '*.lock' }).Count) { throw 'Failed build left a stage or lock.' }
    $script:passed++; Write-Host 'PASS failed builds cleaned stages and locks'
    Write-Host ('PASS package guards: '+$script:passed)
} finally { Remove-OwnedDirectory $root $parent }
