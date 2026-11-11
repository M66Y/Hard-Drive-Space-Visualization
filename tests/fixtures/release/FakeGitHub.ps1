# An offline gh replacement. Only the upload-flow tests start this script.
# No network commands are implemented; unknown commands fail closed.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GH_TELEMETRY -cne 'false') { throw 'Project GitHub invocations must disable telemetry.' }
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$root = $env:ASUKALENS_DRAFT_TEST_ROOT
if (-not $root -or -not (Test-Path -LiteralPath (Join-Path $root 'offline-fixture'))) { throw 'Missing offline fixture marker.' }
$statePath = Join-Path $root 'state.json'
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable
$arguments = @($args)
function Save-State { [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 12)) }
function Reply($Value) { Save-State; ConvertTo-Json -InputObject $Value -Depth 12 -Compress; exit 0 }
function Fail([string]$Reason) { Save-State; [Console]::Error.WriteLine($Reason); exit 23 }
function Option([string]$Name) {
    $index = [Array]::IndexOf($arguments, $Name)
    if ($index -lt 0 -or $index + 1 -ge $arguments.Count) { Fail ('Missing option: ' + $Name) }
    return $arguments[$index + 1]
}
$state.calls += ,$arguments
$repository = 'M66Y/Hard-Drive-Space-Visualization'
if ($arguments[0] -eq 'api') {
    if ((Option '--hostname') -cne 'github.com' -or (Option '-H') -cne 'X-GitHub-Api-Version: 2026-03-10') { Fail 'Wrong API host or version.' }
    $endpoint = $arguments[1]
    if ($endpoint -ceq "repos/$repository/commits/$($state.commit)") {
        Reply @{ sha=$(if ($state.scenario -eq 'wrong-commit') { 'f' * 40 } else { $state.commit }) }
    }
    if ($endpoint -ceq "repos/$repository/git/ref/tags/$($state.version)") {
        if ($state.scenario -eq 'missing-tag') { Fail 'Synthetic missing tag.' }
        Reply @{ object=@{ type='tag'; sha=('a' * 40) } }
    }
    if ($endpoint -ceq "repos/$repository/git/tags/$('a' * 40)") {
        Reply @{ object=@{ type='commit'; sha=$(if ($state.scenario -eq 'wrong-tag') { 'f' * 40 } else { $state.commit }) } }
    }
    if ($endpoint -ceq "repos/$repository/releases") {
        if ($state.scenario -eq 'list-error') { Fail 'Synthetic API error; not a missing draft.' }
        if ('--paginate' -notin $arguments -or '--slurp' -notin $arguments) { Fail 'Draft lookup must use all pages.' }
        Save-State
        # Put the matching release on a second page to exercise pagination flattening.
        if ($null -eq $state.release) { '[[],[]]' }
        else { '[[],[' + (ConvertTo-Json -InputObject $state.release -Depth 12 -Compress) + ']]' }
        exit 0
    }
    if ($endpoint -ceq "repos/$repository/releases/42") {
        if ($null -eq $state.release) { Fail 'Missing synthetic draft.' }
        Reply $state.release
    }
    Fail 'Unexpected API endpoint.'
}
if ($arguments[0] -ne 'release' -or (Option '--repo') -cne "github.com/$repository" -or $arguments[2] -cne $state.version) { Fail 'Unexpected target.' }
switch ($arguments[1]) {
    'create' {
        if ($null -ne $state.release -or '--draft' -notin $arguments -or '--verify-tag' -notin $arguments) { Fail 'Unsafe create.' }
        $state.release = @{ id=42; draft=$true; tag_name=$state.version; name=(Option '--title'); body=[IO.File]::ReadAllText((Option '--notes-file')); assets=@() }
        $state.created++
        Reply @{ created=$true }
    }
    'upload' {
        if ($null -eq $state.release -or -not $state.release.draft -or '--clobber' -in $arguments) { Fail 'Unsafe upload.' }
        if ($state.scenario -eq 'interrupt' -and $state.uploaded.Count -eq 1 -and -not $state.interrupted) {
            $state.interrupted = $true; Fail 'Synthetic interrupted upload.'
        }
        $file = $arguments[3]; $name = [IO.Path]::GetFileName($file).Replace(' ','.')
        if (@($state.release.assets | Where-Object { $_.name -ceq $name }).Count) { Fail 'Duplicate upload.' }
        [IO.File]::Copy($file, (Join-Path $root "server/$name"), $false)
        $state.release.assets += @{name=$name; digest=('sha256:' + (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant())}
        $state.uploaded += $name
        if ($state.scenario -eq 'publish-mid-upload') { $state.release.draft=$false }
        Reply @{ uploaded=$name }
    }
    'download' {
        $destination = Option '--dir'
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'server') -File) { [IO.File]::Copy($file.FullName, (Join-Path $destination $file.Name), $false) }
        if ($state.scenario -eq 'corrupt-download') { [IO.File]::AppendAllText((Join-Path $destination "AsukaLens.$($state.version).zip"), 'CORRUPT') }
        if ($state.scenario -eq 'missing-download') { Remove-Item -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') }
        if ($state.scenario -eq 'extra-download') { [IO.File]::WriteAllText((Join-Path $destination 'unexpected.txt'), 'synthetic extra') }
        if ($state.scenario -eq 'publish-after-download') { $state.release.draft=$false }
        $state.downloaded++
        Reply @{ downloaded=$true }
    }
    default { Fail 'Unsupported command; the fake cannot publish, edit or delete a release.' }
}
