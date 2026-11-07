#requires -Version 7.4
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../scripts/release/Release.Common.ps1')
$parent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
$root = Join-Path $parent ('release-check-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$script:passed = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:passed++; Write-Host ('PASS ' + $Name)
}
function Reject([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Check $rejected $Name
}
try {
    foreach ($script in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../scripts/release') -Filter '*.ps1') {
        $tokens=$null; $errors=$null
        [Management.Automation.Language.Parser]::ParseFile($script.FullName,[ref]$tokens,[ref]$errors) | Out-Null
        Check ($errors.Count -eq 0) ('syntax: ' + $script.Name)
    }
    Check ((Assert-ReleaseVersion 'v1.4.2') -eq '1.4.2') 'canonical version'
    foreach ($bad in @('1.4.2','v1.2','v01.2.3','v1.2.3-preview','v65536.0.0','v1.2.3/../')) {
        Reject { Assert-ReleaseVersion $bad } ('reject version: ' + $bad)
    }
    foreach ($bad in @('../escape','a/../escape','/root','C:/root','a\b','a//b','a./b','NUL.txt','src/file:stream')) {
        Reject { Assert-SafeRelativePath $bad } 'reject unsafe path'
    }
    foreach ($bad in @('bin/app.dll','artifacts/package.zip','.git/config','diagnostics/preferences.json','user.pfx','background.dat','problem-notes.json')) {
        Reject { Assert-SourcePath $bad } 'reject private/generated source'
    }
    Assert-SourcePath 'src/Diagnostics/DiagnosticService.cs'
    Check $true 'production diagnostic source allowed'
    $source = Join-Path $root 'source'
    [IO.Directory]::CreateDirectory((Join-Path $source '目录')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $source '目录/示例.txt'), 'synthetic source')
    [IO.File]::WriteAllBytes((Join-Path $source 'empty.txt'), [byte[]]@())
    [IO.File]::WriteAllText((Join-Path $source '.gitignore'), 'bin/')
    $zip=Join-Path $root 'source.zip'; $tar=Join-Path $root 'source.tar.gz'
    New-SourceArchives $source $zip $tar
    Assert-SameInventory @(Get-FileInventory $source) @(Get-ArchiveInventory $zip)
    Assert-SameInventory @(Get-ArchiveInventory $zip) @(Get-ArchiveInventory $tar -TarGz)
    Check $true 'ZIP/tar.gz exact roundtrip including unicode, empty and dot files'
    $before=(Get-FileHash -LiteralPath $zip).Hash
    Reject { New-SourceArchives $source $zip $tar } 'archives cannot overwrite'
    Check ((Get-FileHash -LiteralPath $zip).Hash -eq $before) 'previous archive preserved'
    $badZip=Join-Path $root 'unsafe.zip'
    $container=[IO.Compression.ZipFile]::Open($badZip,[IO.Compression.ZipArchiveMode]::Create)
    try { $container.CreateEntry('../escape.txt') | Out-Null } finally { $container.Dispose() }
    Reject { Get-ArchiveInventory $badZip } 'ZIP traversal rejected before extraction'
    $duplicate=Join-Path $root 'duplicate.zip'
    $container=[IO.Compression.ZipFile]::Open($duplicate,[IO.Compression.ZipArchiveMode]::Create)
    try { $container.CreateEntry('A.txt') | Out-Null; $container.CreateEntry('a.txt') | Out-Null } finally { $container.Dispose() }
    Reject { Get-ArchiveInventory $duplicate } 'Windows case collision rejected'
    Reject { Invoke-ReleaseTool (Join-Path $PSHOME 'pwsh.exe') @('-NoProfile','-Command','exit 7') } 'external failure propagated'
    Reject { Remove-OwnedDirectory $root $source } 'cleanup cannot escape owner'
    $link=Join-Path $source 'link'
    New-Item -ItemType Junction -Path $link -Target $root | Out-Null
    try { Reject { Assert-SafeTree $source } 'junction rejected without recursion' }
    finally { Remove-Item -LiteralPath $link -Force }
    # A real isolated Git fixture proves clean formal snapshots come from committed bytes.
    $repo=Join-Path $root 'repo'; [IO.Directory]::CreateDirectory($repo) | Out-Null
    Invoke-ReleaseTool git @('init','--quiet',$repo)
    [IO.File]::WriteAllText((Join-Path $repo 'README.md'), "fixture`n")
    Invoke-ReleaseTool git @('-C',$repo,'add','README.md')
    Invoke-ReleaseTool git @('-C',$repo,'-c','user.name=Release Fixture','-c','user.email=fixture@example.invalid','commit','--quiet','-m','fixture')
    $commit=(& git -C $repo rev-parse HEAD).Trim()
    $snapshot=Join-Path $root 'snapshot'
    New-SourceSnapshot $repo $commit $snapshot
    Check ([IO.File]::ReadAllText((Join-Path $snapshot 'README.md')) -eq "fixture`n") 'formal snapshot committed bytes'
    [IO.File]::WriteAllText((Join-Path $repo 'README.md'), 'changed')
    Reject { New-SourceSnapshot $repo $commit (Join-Path $root 'dirty') } 'formal dirty checkout rejected'
    New-SourceSnapshot $repo $commit (Join-Path $root 'local') -LocalTest
    Check ([IO.File]::ReadAllText((Join-Path $root 'local/README.md')) -eq 'changed') 'local snapshot explicitly includes working changes'
    Assert-DraftState @()
    Check $true 'new draft allowed by state guard'
    Reject { Assert-DraftState @([pscustomobject]@{draft=$false}) -Resume } 'published release immutable'
    Reject { Assert-DraftState @([pscustomobject]@{draft=$true}) } 'draft resume must be explicit'
    Assert-DraftState @([pscustomobject]@{draft=$true}) -Resume
    Check $true 'explicit draft resume'
    $upload=Join-Path $root 'source.zip'
    $digest='sha256:'+(Get-FileHash -LiteralPath $upload).Hash.ToLowerInvariant()
    Check (@(Get-MissingDraftFiles @($upload) @()).Count -eq 1) 'missing draft asset selected'
    Check (@(Get-MissingDraftFiles @($upload) @([pscustomobject]@{name='source.zip';digest=$digest})).Count -eq 0) 'identical asset not uploaded again'
    Reject { Get-MissingDraftFiles @($upload) @([pscustomobject]@{name='source.zip';digest='sha256:changed'}) } 'changed draft asset cannot be overwritten'
    Reject { Get-MissingDraftFiles @($upload) @([pscustomobject]@{name='unexpected.zip';digest=$digest}) } 'unrelated draft asset rejected'
    Write-Host ('PASS release script checks: ' + $script:passed)
} finally { Remove-OwnedDirectory $root $parent }
