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
