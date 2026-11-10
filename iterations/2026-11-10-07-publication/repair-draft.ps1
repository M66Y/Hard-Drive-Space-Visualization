#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Notes,[Parameter(Mandatory)][string]$Acceptance)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$ErrorActionPreference = 'Stop'
$Directory = (Resolve-Path -LiteralPath $Directory).Path
& (Join-Path $PSScriptRoot 'Upload-Draft.ps1') -Directory $Directory -Notes $Notes -Acceptance $Acceptance -AllowCleanEnvironmentWaiver -WhatIf | Out-Host
$manifest = Get-Content -LiteralPath (Join-Path $Directory 'release-manifest.json') -Raw | ConvertFrom-Json
$accepted = Get-Content -LiteralPath $Acceptance -Raw | ConvertFrom-Json
$hash = (Get-FileHash -LiteralPath (Join-Path $Directory 'release-manifest.json')).Hash.ToLowerInvariant()
if ($manifest.local_test -or $accepted.commit -cne $manifest.commit -or $accepted.manifest_sha256 -cne $hash) { throw 'Acceptance does not match the original candidate.' }
if ($manifest.version -cne 'v1.5.0' -or $manifest.commit -cne '4d473747f88370a1b411129743e2169229c78dcc') { throw 'This repair is restricted to the reviewed candidate.' }
$notesText = Get-Content -LiteralPath $Notes -Raw -Encoding utf8
Assert-ReleaseNotes $notesText $manifest -RequireCleanEnvDisclosure
$repo='M66Y/Hard-Drive-Space-Visualization'
$gh=Resolve-ReleaseGitHubCli
function GhJson([string[]]$Arguments) { $v=Invoke-ReleaseGitHub $gh ($Arguments + @('--hostname','github.com','-H','X-GitHub-Api-Version: 2026-03-10')); return ($v -join "`n" | ConvertFrom-Json) }
$pages=GhJson @('api',"repos/$repo/releases",'--paginate','--slurp')
$found=@($pages | ForEach-Object { foreach($r in $_) { $r } } | Where-Object { $_.tag_name -ceq $manifest.version })
if ($found.Count -ne 1 -or -not $found[0].draft -or $found[0].name -cne ('AsukaLens '+$manifest.version)) { throw 'Expected a single matching unpublished draft.' }
$draft=$found[0]
$normalize = { param($s) ([string]$s).Replace("`r`n","`n").TrimEnd([char]10) }
if ((& $normalize $draft.body) -cne (& $normalize $notesText)) { throw 'Draft notes differ from reviewed notes.' }
$tag=GhJson @('api',"repos/$repo/git/ref/tags/$($manifest.version)")
$object=$tag.object
if ($object.type -ceq 'tag') { $object=(GhJson @('api',"repos/$repo/git/tags/$($object.sha)")).object }
if ($object.type -cne 'commit' -or $object.sha -cne $manifest.commit) { throw 'Tag does not match the candidate.' }
$expected=@{}
foreach($asset in $manifest.assets) {
    $name=$asset.path.Replace('AsukaLens v','AsukaLens.v')
    $expected[$name]=$asset.sha256
}
foreach($name in @('release-manifest.json','SHA256SUMS.txt')) { $expected[$name]=(Get-FileHash -LiteralPath (Join-Path $Directory $name)).Hash.ToLowerInvariant() }
if (@($draft.assets).Count -ne 6) { throw 'Draft must contain exactly six assets.' }
foreach($asset in $draft.assets) {
    if (-not $expected.ContainsKey($asset.name) -or $asset.digest -cne ('sha256:'+$expected[$asset.name])) { throw 'Draft has an unexpected or modified asset.' }
}
if (-not $PSCmdlet.ShouldProcess($repo,'Repair only draft metadata and release links for GitHub filename normalization')) { return }
$temp=Join-Path $Directory ('.github-name-repair-'+[guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($temp) | Out-Null
    # Download all original draft assets and verify every byte before replacing metadata.
    Invoke-ReleaseGitHub $gh @('release','download',$manifest.version,'--repo',('github.com/'+$repo),'--dir',$temp) | Out-Host
    foreach($entry in $expected.GetEnumerator()) { if ((Get-FileHash -LiteralPath (Join-Path $temp $entry.Key)).Hash.ToLowerInvariant() -cne $entry.Value) { throw 'Downloaded asset checksum mismatch.' } }
    foreach($asset in $manifest.assets) { $asset.path=$asset.path.Replace('AsukaLens v','AsukaLens.v') }
    $newManifest=Join-Path $temp 'new-manifest.json'
    $newSums=Join-Path $temp 'new-sums.txt'
    $newNotes=Join-Path $temp 'new-notes.md'
    [IO.File]::WriteAllText($newManifest,($manifest | ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllLines($newSums,[string[]]@($manifest.assets | ForEach-Object { $_.sha256+'  '+$_.path }),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($newNotes,$notesText.Replace('AsukaLens%20v1.5.0','AsukaLens.v1.5.0').Replace('AsukaLens v1.5.0.', 'AsukaLens.v1.5.0.').Replace('AsukaLens v1.5.0-', 'AsukaLens.v1.5.0-'),[Text.UTF8Encoding]::new($false))
    $current=GhJson @('api',"repos/$repo/releases/$($draft.id)")
    if (-not $current.draft -or (& $normalize $current.body) -cne (& $normalize $notesText)) { throw 'Draft changed during verification.' }
    foreach($name in @('release-manifest.json','SHA256SUMS.txt')) {
        $match=@($current.assets | Where-Object { $_.name -ceq $name })
        if ($match.Count -ne 1 -or $match[0].digest -cne ('sha256:'+$expected[$name])) { throw 'Draft metadata changed.' }
        Invoke-ReleaseGitHub $gh @('api','--method','DELETE',"repos/$repo/releases/assets/$($match[0].id)") | Out-Null
    }
    Copy-Item -LiteralPath $newManifest -Destination (Join-Path $temp 'release-manifest.json') -Force
    Copy-Item -LiteralPath $newSums -Destination (Join-Path $temp 'SHA256SUMS.txt') -Force
    foreach($name in @('release-manifest.json','SHA256SUMS.txt')) { Invoke-ReleaseGitHub $gh @('release','upload',$manifest.version,(Join-Path $temp $name),'--repo',('github.com/'+$repo)) | Out-Host }
    Invoke-ReleaseGitHub $gh @('release','edit',$manifest.version,'--repo',('github.com/'+$repo),'--notes-file',$newNotes) | Out-Host
    $expected['release-manifest.json']=(Get-FileHash -LiteralPath (Join-Path $temp 'release-manifest.json')).Hash.ToLowerInvariant()
    $expected['SHA256SUMS.txt']=(Get-FileHash -LiteralPath (Join-Path $temp 'SHA256SUMS.txt')).Hash.ToLowerInvariant()
    $verify=Join-Path $temp 'verify'
    [IO.Directory]::CreateDirectory($verify) | Out-Null
    Invoke-ReleaseGitHub $gh @('release','download',$manifest.version,'--repo',('github.com/'+$repo),'--dir',$verify) | Out-Host
    if (@(Get-ChildItem -LiteralPath $verify -File).Count -ne 6) { throw 'Unexpected downloaded asset count.' }
    foreach($entry in $expected.GetEnumerator()) { if ((Get-FileHash -LiteralPath (Join-Path $verify $entry.Key)).Hash.ToLowerInvariant() -cne $entry.Value) { throw 'Repaired download checksum mismatch.' } }
    $current=GhJson @('api',"repos/$repo/releases/$($draft.id)")
    if (-not $current.draft -or (& $normalize $current.body) -cne (& $normalize ([IO.File]::ReadAllText($newNotes)))) { throw 'Final draft state differs.' }
    foreach($a in $current.assets) { if (-not $expected.ContainsKey($a.name) -or $a.digest -cne ('sha256:'+$expected[$a.name])) { throw 'Final remote asset differs.' } }
    Write-Host 'Repaired draft assets downloaded and verified; draft remains unpublished.'
} finally { Remove-OwnedDirectory $temp $Directory }
