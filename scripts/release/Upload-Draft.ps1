#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$Notes,
    [Parameter(Mandatory)][string]$Acceptance,
    [switch]$Resume
)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$Directory = (Resolve-Path -LiteralPath $Directory).Path
& (Join-Path $PSScriptRoot 'Test-Release.ps1') -Directory $Directory | Out-Host
$manifest = Get-Content -LiteralPath (Join-Path $Directory 'release-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.local_test) { throw 'Local-test assets can never be uploaded by this script.' }
$repository = 'M66Y/Hard-Drive-Space-Visualization'
$qualifiedRepository = 'github.com/' + $repository
$notesText = Get-Content -LiteralPath $Notes -Raw -Encoding utf8
Assert-ReleaseNotes $notesText $manifest
$accepted = Get-Content -LiteralPath $Acceptance -Raw | ConvertFrom-Json
if ($accepted.commit -ne $manifest.commit -or $accepted.manifest_sha256 -ne (Get-FileHash -LiteralPath (Join-Path $Directory 'release-manifest.json')).Hash.ToLowerInvariant()) { throw 'Acceptance does not cover these assets.' }
foreach ($check in @('clean_windows_10_no_dotnet','clean_windows_11_no_dotnet','portable_workflow','install_upgrade_uninstall','failure_recovery','source_builds','privacy_and_licenses')) {
    if ($accepted.checks.$check -ne 'passed') { throw ('Package acceptance is incomplete: ' + $check) }
}
if ($WhatIfPreference) {
    $PSCmdlet.ShouldProcess("$repository / $($manifest.version)",'Create or resume a draft; remote commit/tag/draft checks will run on execution') | Out-Null
    return
}
$gh = Resolve-ReleaseGitHubCli
function Gh-Json([string[]]$Arguments) {
    $text = Invoke-ReleaseGitHub $gh ($Arguments + @('--hostname','github.com','-H','X-GitHub-Api-Version: 2026-03-10'))
    return ($text -join "`n" | ConvertFrom-Json)
}
function Assert-MatchingDraft($Draft) {
    Assert-DraftState @($Draft) -Resume
    # GitHub may normalize Markdown newlines. Other content must still match the reviewed notes.
    $actualNotes = ([string]$Draft.body).Replace("`r`n", "`n").TrimEnd([char]10)
    $expectedNotes = $notesText.Replace("`r`n", "`n").TrimEnd([char]10)
    if ($Draft.tag_name -cne $manifest.version -or $Draft.name -cne ('AsukaLens ' + $manifest.version) -or $actualNotes -cne $expectedNotes) {
        throw 'Draft tag, title or release notes differ. Review the existing draft; no automatic overwrite is allowed.'
    }
}
$remote = Gh-Json @('api',"repos/$repository/commits/$($manifest.commit)")
if ($remote.sha -ne $manifest.commit) { throw 'Release commit is not available in the target repository.' }
# A pre-existing exact tag is required: this script cannot create or move tags.
$tag = Gh-Json @('api',"repos/$repository/git/ref/tags/$($manifest.version)")
$tagObject = $tag.object
if ($tagObject.type -eq 'tag') { $tagObject = (Gh-Json @('api',"repos/$repository/git/tags/$($tagObject.sha)")).object }
if ($tagObject.type -ne 'commit' -or $tagObject.sha -ne $manifest.commit) { throw 'Tag points to another commit.' }
$releases = Gh-Json @('api',"repos/$repository/releases",'--paginate','--slurp')
$existing = @($releases | ForEach-Object { foreach ($release in $_) { $release } } | Where-Object { $_.tag_name -eq $manifest.version })
Assert-DraftState $existing -Resume:$Resume
if ($existing.Count -eq 1) { Assert-MatchingDraft $existing[0] }
$uploads = @($manifest.assets | ForEach-Object { Join-Path $Directory $_.path }) + @((Join-Path $Directory 'SHA256SUMS.txt'),(Join-Path $Directory 'release-manifest.json'))
if (-not $PSCmdlet.ShouldProcess("$repository / $($manifest.version)",'Create or resume a draft and upload verified assets; never publish')) { return }
if ($existing.Count -eq 0) {
    Invoke-ReleaseGitHub $gh @('release','create',$manifest.version,'--repo',$qualifiedRepository,'--draft','--verify-tag','--title',('AsukaLens '+$manifest.version),'--notes-file',(Resolve-Path -LiteralPath $Notes).Path) | Out-Host
}
# The by-tag REST endpoint targets published releases; list with push credentials to locate drafts.
$pages = Gh-Json @('api',"repos/$repository/releases",'--paginate','--slurp')
$drafts = @($pages | ForEach-Object { foreach ($release in $_) { $release } } | Where-Object { $_.tag_name -eq $manifest.version })
if ($drafts.Count -ne 1) { throw 'The expected draft was not returned; check authentication and repository permissions.' }
$draft = $drafts[0]
Assert-MatchingDraft $draft
$missing = @(Get-MissingDraftFiles $uploads $draft.assets)
foreach ($file in $missing) {
    $current = Gh-Json @('api',"repos/$repository/releases/$($draft.id)")
    Assert-MatchingDraft $current
    Invoke-ReleaseGitHub $gh @('release','upload',$manifest.version,$file,'--repo',$qualifiedRepository) | Out-Host
}
$download = Join-Path $Directory ('.verify-download-' + [guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($download) | Out-Null
    Invoke-ReleaseGitHub $gh @('release','download',$manifest.version,'--repo',$qualifiedRepository,'--dir',$download) | Out-Host
    foreach ($file in $uploads) {
        if ((Get-FileHash -LiteralPath $file).Hash -ne (Get-FileHash -LiteralPath (Join-Path $download ([IO.Path]::GetFileName($file)))).Hash) { throw 'Downloaded draft asset differs.' }
    }
    & (Join-Path $PSScriptRoot 'Test-Release.ps1') -Directory $download | Out-Host
} finally { Remove-OwnedDirectory $download $Directory }
$current = Gh-Json @('api',"repos/$repository/releases/$($draft.id)")
Assert-MatchingDraft $current
Write-Host 'Draft assets downloaded and verified. Release remains unpublished.'
