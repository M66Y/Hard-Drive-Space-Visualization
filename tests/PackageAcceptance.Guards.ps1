# Identity selection is explicit. Formal candidate testing never adopts an existing installation.
function Get-PackageAcceptanceProfile([bool]$LocalTest, [bool]$FormalCandidate) {
    if ($LocalTest -eq $FormalCandidate) { throw 'Use LOCAL TEST assets by default, or explicitly opt into -FormalCandidate for committed candidate assets.' }
    $name = if ($LocalTest) { 'AsukaLens (LOCAL TEST)' } else { 'AsukaLens' }
    $id = if ($LocalTest) { 'AsukaLens.LocalTest' } else { 'AsukaLens.8E3208AB-7151-4AC5-983C-22C5529B8746' }
    $key = 'Software/Microsoft/Windows/CurrentVersion/Uninstall/' + $id + '_is1'
    $registry = 'HKCU:/' + $key
    $shortcuts = @('Programs','DesktopDirectory' | ForEach-Object { Join-Path ([Environment]::GetFolderPath($_)) ($name + '.lnk') })
    $recovery = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('AsukaLens/InstallerRecovery-' + $id)
    $locations = @($registry, ('HKLM:/' + $key), ('HKCU:/Software/WOW6432Node/' + $key.Substring(9)), ('HKLM:/Software/WOW6432Node/' + $key.Substring(9)))
    $locations += $shortcuts
    $locations += @('CommonPrograms','CommonDesktopDirectory' | ForEach-Object { Join-Path ([Environment]::GetFolderPath($_)) ($name + '.lnk') })
    $locations += Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Programs/' + $name)
    $locations += $recovery
    return [pscustomobject]@{ Name=$name; Id=$id; Registry=$registry; Shortcuts=$shortcuts; Recovery=$recovery; Locations=$locations }
}

function Assert-PackageAcceptanceLocationsEmpty($Profile, [scriptblock]$Exists = { param($Path) Test-Path -LiteralPath $Path -ErrorAction Stop }) {
    foreach ($path in $Profile.Locations) {
        if (& $Exists $path) { throw 'Existing installation, shortcut, default directory or recovery data detected. Candidate acceptance refuses to replace it.' }
    }
}

function Assert-PackageAcceptanceInstallOwned([string]$RegisteredPath, [string]$ExpectedPath) {
    if ([string]::IsNullOrWhiteSpace($RegisteredPath) -or
        -not [IO.Path]::GetFullPath($RegisteredPath).TrimEnd('\','/').Equals(
            [IO.Path]::GetFullPath($ExpectedPath).TrimEnd('\','/'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installation ownership changed; refusing automatic uninstall.'
    }
}
