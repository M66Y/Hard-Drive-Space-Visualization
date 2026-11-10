#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ReleaseVersion([string]$Version) {
    if ($Version -notmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Use vX.X.X without leading zeroes.' }
    foreach ($part in $Version.Substring(1).Split('.')) {
        if ([long]$part -gt 65535) { throw 'Version components must fit Windows version fields.' }
    }
    return $Version.Substring(1)
}

function Assert-SafeRelativePath([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.StartsWith('/') -or $Name.Contains('\')) { throw 'Invalid relative path.' }
    foreach ($part in $Name.Split('/')) {
        if ($part -in @('', '.', '..') -or $part -match '[<>:"|?*\x00-\x1f]' -or $part -match '[. ]$' -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'Unsafe archive path.' }
    }
}

function Assert-SourcePath([string]$Name) {
    Assert-SafeRelativePath $Name
    if ($Name -match '(?i)(^|/)(\.git|\.codex|\.agents|\.aws|bin|obj|artifacts|软件|node_modules)(/|$)' -or $Name -match '^(?i:diagnostics)/' -or
        $Name -match '(?i)(\.log|\.dmp|\.user|\.pfx|\.pem|\.key|\.exe|\.zip|\.tar\.gz)$' -or
        $Name -match '(?i)(^|/)(background\.dat.*|problem-notes\.json.*|runtime-.*\.jsonl|\.env.*)$') {
        throw 'Source contains an excluded file.'
    }
}

function Assert-NoReparse([string]$Path) {
    $item = [IO.Path]::GetFullPath($Path)
    while ($item) {
        if ((Test-Path -LiteralPath $item) -and ((Get-Item -LiteralPath $item -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Reparse points are not allowed in release paths.'
        }
        $item = [IO.Path]::GetDirectoryName($item)
    }
}

function Assert-SafeTree([string]$Path) {
    Assert-NoReparse $Path
    # Enumerate one level at a time so a junction is rejected before recursion.
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release tree contains a link.' }
        if ($item.PSIsContainer) { Assert-SafeTree $item.FullName }
    }
}

function Remove-OwnedDirectory([string]$Path, [string]$Parent) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup boundary violation.' }
    if (Test-Path -LiteralPath $absolute) {
        Assert-SafeTree $absolute
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}

function Invoke-ReleaseTool([string]$File, [string[]]$Arguments) {
    & $File @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw ('External tool failed: ' + [IO.Path]::GetFileName($File) + ', exit ' + $LASTEXITCODE) }
}

function Resolve-ReleaseGitHubCli {
    $command=Get-Command gh -CommandType Application -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $pin=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'github-cli.json') -Raw | ConvertFrom-Json
    if ($pin.schema -ne 1 -or $pin.version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or $pin.exe_sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid GitHub CLI pin.' }
    $file=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot ("../../artifacts/release-tools/gh-$($pin.version)/bin/gh.exe")))
    Assert-NoReparse $file
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'GitHub CLI unavailable. Prepare the verified portable tool or provide gh on PATH.' }
    if ((Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant() -ne $pin.exe_sha256) { throw 'Portable GitHub CLI checksum mismatch.' }
    return $file
}

function Invoke-ReleaseGitHub([string]$File,[string[]]$Arguments) {
    $previous=[Environment]::GetEnvironmentVariable('GH_TELEMETRY','Process')
    try {
        [Environment]::SetEnvironmentVariable('GH_TELEMETRY','false','Process')
        & $File @Arguments
        if ($LASTEXITCODE -ne 0) { throw 'GitHub command failed. Inspect the operation state before retrying.' }
    } finally { [Environment]::SetEnvironmentVariable('GH_TELEMETRY',$previous,'Process') }
}

function Get-FileInventory([string]$Directory) {
    Assert-SafeTree $Directory
    $root = [IO.Path]::GetFullPath($Directory)
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | Sort-Object FullName)
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        Assert-SafeRelativePath $relative
        [ordered]@{ path=$relative; bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
}

function Write-ReleaseJson($Value, [string]$Path) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12) + "`n", [Text.UTF8Encoding]::new($false))
}

function New-SourceSnapshot([string]$Repository, [string]$Commit, [string]$Destination, [switch]$LocalTest) {
    if (Test-Path -LiteralPath $Destination) { throw 'Snapshot destination already exists.' }
    $head = (& git -C $Repository rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $Commit -notmatch '^[0-9a-f]{40}$') { throw 'An explicit full commit is required.' }
    $resolved = & git -C $Repository rev-parse --verify ($Commit + '^{commit}')
    if ($LASTEXITCODE -ne 0 -or $resolved.Trim() -ne $Commit) { throw 'Commit is unavailable.' }
    foreach ($entry in (& git -C $Repository ls-tree -r $Commit)) {
        if ($entry -notmatch '^100(644|755) blob ') { throw 'Source commit contains a link or submodule.' }
    }
    if ($LocalTest) {
        if ($head -ne $Commit) { throw 'Local tests must identify the current HEAD as their baseline.' }
        $names = (& git -C $Repository -c core.quotepath=false ls-files --cached --others --exclude-standard -z) -join "`n"
    } else {
        if ($head -ne $Commit -or (& git -C $Repository status --porcelain)) { throw 'Formal packages require a clean checkout at the requested commit.' }
        $names = (& git -C $Repository -c core.quotepath=false ls-tree -r --name-only -z $Commit) -join "`n"
    }
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate source files.' }
    $paths = @($names.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $paths) {
        Assert-SourcePath $name
        if (-not $seen.Add($name)) { throw 'Duplicate source path.' }
        Assert-NoReparse (Join-Path $Repository $name)
    }
    New-Item -ItemType Directory -Path $Destination | Out-Null
    if ($LocalTest) {
        foreach ($name in $paths) {
            $target = Join-Path $Destination $name
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.File]::Copy((Join-Path $Repository $name), $target, $false)
        }
    } else {
        # archive reads committed blobs, not autocrlf-transformed working-tree bytes.
        $archive = $Destination + '.zip'
        try {
            Invoke-ReleaseTool git @('-C', $Repository, '-c', 'core.autocrlf=false', 'archive', '--format=zip', ('--output=' + $archive), $Commit)
            [IO.Compression.ZipFile]::ExtractToDirectory($archive, $Destination)
        } finally { if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive } }
    }
    Assert-SafeTree $Destination
}

function New-SourceArchives([string]$Source, [string]$Zip, [string]$TarGz) {
    [IO.Compression.ZipFile]::CreateFromDirectory($Source, $Zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $file = [IO.File]::Open($TarGz, [IO.FileMode]::CreateNew)
    try {
        $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal, $true)
        try { [System.Formats.Tar.TarFile]::CreateFromDirectory($Source, $gzip, $false) }
        finally { $gzip.Dispose() }
    } finally { $file.Dispose() }
}

function Get-ArchiveInventory([string]$Path, [switch]$TarGz) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $result = [Collections.Generic.List[object]]::new()
    function Add-Entry([string]$Name, [long]$Length, [IO.Stream]$Stream) {
        $Name = $Name -replace '^\./',''
        Assert-SafeRelativePath $Name
        if (-not $seen.Add($Name)) { throw 'Duplicate archive entry.' }
        if ($null -eq $Stream -and $Length -ne 0) { throw 'Missing archive data stream.' }
        $sha = [Security.Cryptography.SHA256]::HashData($(if ($null -eq $Stream) { [IO.Stream]::Null } else { $Stream }))
        $result.Add([ordered]@{ path=$Name; bytes=$Length; sha256=[Convert]::ToHexString($sha).ToLowerInvariant() })
    }
    if ($TarGz) {
        $file = [IO.File]::OpenRead($Path)
        $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Decompress)
        $reader = [System.Formats.Tar.TarReader]::new($gzip)
        try {
            while ($entry = $reader.GetNextEntry()) {
                if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) {
                    $name = $entry.Name -replace '^\./',''
                    if ($name.TrimEnd('/')) { Assert-SafeRelativePath $name.TrimEnd('/') }
                    continue
                }
                if ($entry.EntryType -notin @([System.Formats.Tar.TarEntryType]::RegularFile, [System.Formats.Tar.TarEntryType]::V7RegularFile)) { throw 'Unsupported tar entry type.' }
                Add-Entry $entry.Name $entry.Length $entry.DataStream
            }
        } finally { $reader.Dispose(); $gzip.Dispose(); $file.Dispose() }
    } else {
        $zip = [IO.Compression.ZipFile]::OpenRead($Path)
        try {
            foreach ($entry in $zip.Entries) {
                if ($entry.FullName.EndsWith('/')) { Assert-SafeRelativePath $entry.FullName.TrimEnd('/'); continue }
                if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'ZIP links are not allowed.' }
                $stream = $entry.Open()
                try { Add-Entry $entry.FullName $entry.Length $stream } finally { $stream.Dispose() }
            }
        } finally { $zip.Dispose() }
    }
    return $result.ToArray()
}

function Assert-SameInventory($Expected, $Actual) {
    $expectedList = @($Expected | Sort-Object { $_.path })
    $actualList = @($Actual | Sort-Object { $_.path })
    if ($expectedList.Count -ne $actualList.Count) { throw 'Archive file count mismatch.' }
    for ($i=0; $i -lt $expectedList.Count; $i++) {
        if ($expectedList[$i].path -cne $actualList[$i].path -or $expectedList[$i].sha256 -ne $actualList[$i].sha256 -or
            $expectedList[$i].bytes -ne $actualList[$i].bytes) { throw 'Archive file content mismatch.' }
    }
}

function Assert-DraftState($Releases, [switch]$Resume) {
    $list = @($Releases)
    if ($list.Count -gt 1 -or ($list.Count -eq 1 -and (-not $list[0].draft -or -not $Resume))) {
        throw 'Release exists; published snapshots cannot be changed, draft resumes must be explicit.'
    }
}

function Assert-ReleaseNotes([string]$Text, $Manifest, [switch]$RequireCleanEnvDisclosure) {
    [void](Assert-ReleaseVersion $Manifest.version)
    if ($Manifest.commit -cnotmatch '^[0-9a-f]{40}$' -or $Manifest.runtime -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Invalid notes identity.' }
    if ([string]::IsNullOrWhiteSpace($Text) -or $Text.Length -gt 100000 -or
        $Text -match '\{\{[^{}]*\}\}|REPLACE_WITH_[A-Z_]+|\bTODO\b|\bTBD\b|待填写') { throw 'Release notes contain missing content or unresolved placeholders.' }
    # Comments and fenced examples cannot satisfy actual release-note sections/links.
    $visible=[regex]::Replace($Text,'(?s)<!--.*?-->','')
    if ($visible.Contains('<!--') -or $visible.Contains('-->')) { throw 'Unclosed release-note comment.' }
    $lines=[Collections.Generic.List[string]]::new(); $fence=$null
    foreach ($line in $visible.Replace("`r`n","`n").Split("`n")) {
        $match=[regex]::Match($line,'^ {0,3}(`{3,}|~{3,})(.*)$')
        if ($null -ne $fence) {
            if ($match.Success -and $match.Groups[1].Value[0] -eq $fence[0] -and
                $match.Groups[1].Value.Length -ge $fence.Length -and [string]::IsNullOrWhiteSpace($match.Groups[2].Value)) { $fence=$null }
            continue
        }
        if ($match.Success) { $fence=$match.Groups[1].Value; continue }
        $lines.Add($line)
    }
    if ($null -ne $fence) { throw 'Unclosed release-note code fence.' }
    $visible=$lines -join "`n"
    $titles=[regex]::Matches($visible,'(?m)^# ([^\n]+)$')
    if ($titles.Count -ne 1 -or $titles[0].Groups[1].Value.TrimEnd() -cne ('AsukaLens '+$Manifest.version)) { throw 'Provide the exact AsukaLens release title.' }
    $languages=[regex]::Matches($visible,'(?m)^## ([^\n]+)$')
    if ($languages.Count -ne 2 -or (Compare-Object @('English','简体中文') @($languages | ForEach-Object {$_.Groups[1].Value.TrimEnd()}) -CaseSensitive)) {
        throw 'Provide exactly one Simplified Chinese and one English section.'
    }
    $base='https://github.com/M66Y/Hard-Drive-Space-Visualization'
    $downloadBase=$base+'/releases/download/'+$Manifest.version+'/'
    $files=@("AsukaLens $($Manifest.version).zip","AsukaLens $($Manifest.version).exe","AsukaLens $($Manifest.version)-source.zip",
        "AsukaLens $($Manifest.version)-source.tar.gz",'SHA256SUMS.txt','release-manifest.json')
    $expectedLinks=@($files | ForEach-Object { $downloadBase+[Uri]::EscapeDataString($_) })
    for ($i=0;$i -lt $languages.Count;$i++) {
        $end=if ($i+1 -lt $languages.Count) {$languages[$i+1].Index} else {$visible.Length}
        $section=$visible.Substring($languages[$i].Index+$languages[$i].Length,$end-$languages[$i].Index-$languages[$i].Length)
        $chinese=$languages[$i].Groups[1].Value.TrimEnd() -ceq '简体中文'
        $required=if ($chinese) {@('更新内容','修复','已知限制','适用环境与依赖','安装与便携使用','下载与校验','源码与许可')}
            else {@('Changes','Fixes','Known limitations','Requirements','Installation and portable use','Downloads and checksums','Source and licensing')}
        $headings=[regex]::Matches($section,'(?m)^### ([^\n]+)$'); $bodies=@{}
        for ($h=0;$h -lt $headings.Count;$h++) {
            $name=$headings[$h].Groups[1].Value.TrimEnd()
            if ($bodies.ContainsKey($name)) { throw 'Duplicate release-note subsection.' }
            $limit=if ($h+1 -lt $headings.Count) {$headings[$h+1].Index} else {$section.Length}
            $bodies[$name]=$section.Substring($headings[$h].Index+$headings[$h].Length,$limit-$headings[$h].Index-$headings[$h].Length).Trim()
        }
        foreach ($name in $required) {
            if ($name -cnotin @($bodies.Keys) -or [string]::IsNullOrWhiteSpace($bodies[$name])) { throw 'Release notes are missing a required subsection or its content.' }
        }
        if ($RequireCleanEnvDisclosure) {
            $disclosure = if ($chinese) { '干净 Windows 10 和 Windows 11 环境尚未验收。' } else { 'Clean Windows 10 and Windows 11 environments have not been validated.' }
            if (-not $bodies[$required[2]].Contains($disclosure)) { throw 'The clean-environment exception must be disclosed in both known-limitations sections.' }
        }
        $requirements=$bodies[$required[3]]
        $runtimeToken='\.NET '+[regex]::Escape($Manifest.runtime)+'(?![A-Za-z0-9_.+-])'
        if (-not $requirements.Contains('Windows 11 x64') -or -not $requirements.Contains('Windows 10 22H2 x64') -or $requirements -cnotmatch $runtimeToken -or
            $requirements -notmatch $(if ($chinese) {'内含|包含'} else {'(?i)include|bundled'}) -or
            $requirements -notmatch $(if ($chinese) {'签名'} else {'(?i)sign(?:ed|ing|ature)'})) { throw 'State the platform, bundled runtime version and signing status in both languages.' }
        $links=@([regex]::Matches($bodies[$required[5]],'(?<!!)\[[^\]\n]+\]\((https://[^\s)]+)\)') | ForEach-Object {$_.Groups[1].Value})
        foreach ($link in $expectedLinks) {
            if (@($links | Where-Object { $_ -ceq $link }).Count -ne 1) { throw 'Provide all six canonical asset/checksum links in each language.' }
        }
        if (@($links | Where-Object { $_.Contains('/releases/download/') -and $_ -cnotin $expectedLinks }).Count) { throw 'Unexpected release asset link.' }
        $commitLink=$base+'/tree/'+$Manifest.commit
        if ($bodies[$required[6]] -cnotmatch ('\]\('+[regex]::Escape($commitLink)+'\)')) { throw 'Source notes must link to the exact release commit.' }
    }
}

function Get-MissingDraftFiles([string[]]$Files, $Assets) {
    $names = @($Files | ForEach-Object { [IO.Path]::GetFileName($_) })
    foreach ($asset in $Assets) {
        if ($asset.name -cnotin $names) { throw 'Unexpected draft asset; review the draft without overwriting it.' }
    }
    foreach ($file in $Files) {
        $name = [IO.Path]::GetFileName($file)
        $matches = @($Assets | Where-Object { $_.name -ceq $name })
        if ($matches.Count -gt 1) { throw 'Duplicate draft asset.' }
        if ($matches.Count -eq 1) {
            $digest = 'sha256:' + (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
            if ($matches[0].digest -ne $digest) { throw 'Existing draft asset differs; refusing overwrite.' }
        } else { $file }
    }
}
