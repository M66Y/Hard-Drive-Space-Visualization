#requires -Version 7.4
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PackageAcceptance.Guards.ps1')
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
Reject { Get-PackageAcceptanceProfile $false $false } 'formal assets require explicit candidate opt-in'
Reject { Get-PackageAcceptanceProfile $true $true } 'formal opt-in cannot silently test LOCAL TEST assets'
$local = Get-PackageAcceptanceProfile $true $false
$formal = Get-PackageAcceptanceProfile $false $true
Check ($local.Id -eq 'AsukaLens.LocalTest' -and $formal.Id -eq 'AsukaLens.8E3208AB-7151-4AC5-983C-22C5529B8746') 'separate exact installer identities'
foreach ($profile in @($local,$formal)) {
    Check ($profile.Locations.Count -eq 10 -and @($profile.Locations | Select-Object -Unique).Count -eq 10) 'all registry views, shortcuts, default installation and recovery checked'
    Assert-PackageAcceptanceLocationsEmpty $profile { param($Path) $false }
    Check $true 'empty state allows acceptance'
    foreach ($occupied in $profile.Locations) {
        Reject { Assert-PackageAcceptanceLocationsEmpty $profile { param($Path) $Path -eq $occupied } } 'any prior ownership blocks acceptance'
    }
    Reject { Assert-PackageAcceptanceLocationsEmpty $profile { throw 'read denied' } } 'inventory failure cannot permit installation'
}
$owned = Join-Path $PSScriptRoot '../artifacts/owned-installation'
Assert-PackageAcceptanceInstallOwned ([IO.Path]::GetFullPath($owned) + '\') $owned
Check $true 'owned registered installation allows cleanup'
Reject { Assert-PackageAcceptanceInstallOwned '' $owned } 'missing registered location blocks cleanup'
Reject { Assert-PackageAcceptanceInstallOwned ($owned + '-unowned') $owned } 'neighboring installation blocks cleanup'
Write-Host "$script:passed acceptance guard checks passed."
