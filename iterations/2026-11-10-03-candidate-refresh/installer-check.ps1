#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$CandidateDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Assert-NoReparse([string]$path) {
    $item = [IO.Path]::GetFullPath($path)
    while ($item) {
        if ((Test-Path -LiteralPath $item) -and ((Get-Item -LiteralPath $item -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse path rejected.' }
        $item = [IO.Path]::GetDirectoryName($item)
    }
}

# Synthetic wrappers exercise upgrade and rollback; they are not historical releases.
$id = 'AsukaLens.8E3208AB-7151-4AC5-983C-22C5529B8746'
$key = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $id + '_is1'
$registry = 'HKCU:\' + $key
$name = 'AsukaLens'
$defaultDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\AsukaLens'
$recovery = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('AsukaLens\InstallerRecovery-' + $id)
$locations = @($registry, ('HKLM:\' + $key), ('HKCU:\Software\WOW6432Node\' + $key.Substring(9)), ('HKLM:\Software\WOW6432Node\' + $key.Substring(9)), $defaultDir, $recovery)
foreach ($folder in @('Programs','DesktopDirectory','CommonPrograms','CommonDesktopDirectory')) {
    $locations += Join-Path ([Environment]::GetFolderPath($folder)) ($name + '.lnk')
}
foreach ($location in $locations) {
    if (Test-Path -LiteralPath $location -ErrorAction Stop) { throw 'Existing AsukaLens installation, shortcut or recovery state found; test stopped without replacing it.' }
}
if (Get-Process -Name AsukaLens,DiskAtlas -ErrorAction SilentlyContinue) { throw 'Close AsukaLens before running this test.' }
$candidate = Join-Path $CandidateDirectory 'AsukaLens v1.5.0.exe'
$manifest = Join-Path $CandidateDirectory 'release-manifest.json'
$old = Join-Path $PSScriptRoot 'older\AsukaLens v1.4.1.exe'
$failure = Join-Path $PSScriptRoot 'failure\AsukaLens v1.5.0.exe'
$expected = @(
    @($candidate,'4ce6ef2e768bbfb34d0277658c00cd984eac15bbcb2258e6b74bd3aabb00375a'),
    @($manifest,'198d6dcf860ede30d418ac674a6c4c91fd3773d41c526d4d49d57da177968fce'),
    @($old,'8e6d6458013f13f9effcde1cae2fc86e4627b5b09f4429e2ce8cce11e174f955'),
    @($failure,'4ca7c3dc9bad616a2921126ecb2f55916b4e63be3fc29e119ad155eef5351cf0')
)
foreach ($item in $expected) {
    Assert-NoReparse $item[0]
    if ((Get-FileHash -LiteralPath $item[0] -Algorithm SHA256).Hash.ToLowerInvariant() -cne $item[1]) { throw 'Test input checksum mismatch.' }
}
$release = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
if ($release.commit -cne '2d54a681986127e3c966eca03b1d72ef3a33f9aa' -or $release.version -cne 'v1.5.0' -or $release.local_test -ne $false) { throw 'Candidate identity mismatch.' }
$exeRecord = @($release.payload_files | Where-Object { $_.path -ceq 'AsukaLens.exe' })
if ($exeRecord.Count -ne 1) { throw 'Candidate payload manifest invalid.' }
$root = Join-Path ([IO.Path]::GetFullPath($env:TEMP)) ('AsukaLensInstallerCheck-' + [guid]::NewGuid().ToString('N'))
$installation = Join-Path $root 'installed'
Assert-NoReparse $root
[IO.Directory]::CreateDirectory($root) | Out-Null
$checks = New-Object 'System.Collections.Generic.List[string]'
function Check([bool]$value,[string]$label) {
    if (-not $value) { throw ('FAILED: ' + $label) }
    $checks.Add($label); Write-Host ('PASS: ' + $label)
}
function Invoke-Setup([string]$file,[string]$log,[string[]]$extra = @()) {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LOG="' + (Join-Path $root $log) + '"')) + $extra
    $p = Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $p.WaitForExit(90000)) { throw 'Installer timed out; process was not terminated.' }
    return $p.ExitCode
}
try {
    Check ((Invoke-Setup $old 'old.log' @(('/DIR="' + $installation + '"'),'/TASKS=desktopicon')) -eq 0) 'synthetic older version installed'
    Check ((Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq '1.4.1') 'old version registration'
    Check ((Invoke-Setup $candidate 'upgrade.log') -eq 0) 'candidate upgrade installed'
    Check ((Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq '1.5.0') 'candidate version registration'
    Check ((Get-FileHash -LiteralPath (Join-Path $installation 'AsukaLens.exe')).Hash.ToLowerInvariant() -ceq $exeRecord[0].sha256) 'candidate application bytes match manifest'
    Check ((Invoke-Setup $old 'downgrade.log') -ne 0) 'downgrade rejected'
    $probe = Join-Path $installation '000-recovery-probe.txt'
    [IO.File]::WriteAllText($probe,'previous synthetic bytes',[Text.UTF8Encoding]::new($false))
    $locked = [IO.File]::Open((Join-Path $installation 'AsukaLens.exe'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
    try { Check ((Invoke-Setup $failure 'failure.log') -ne 0) 'locked replacement rejected' } finally { $locked.Dispose() }
    $log = [IO.File]::ReadAllText((Join-Path $root 'failure.log'))
    Check (@([regex]::Split($log,[regex]::Escape('-- File entry --')) | Where-Object { $_.Contains('000-recovery-probe.txt') -and $_.Contains('Successfully installed the file.') }).Count -eq 1) 'synthetic probe was overwritten before failure'
    Check ([IO.File]::ReadAllText($probe) -ceq 'previous synthetic bytes') 'rollback restored previous probe bytes'
    Check ((Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq '1.5.0' -and (Test-Path -LiteralPath (Join-Path $installation 'unins000.exe'))) 'registration and uninstaller retained'
    Check (-not (Test-Path -LiteralPath $recovery)) 'recovery backup cleared after successful rollback'
    Assert-NoReparse $probe
    Remove-Item -LiteralPath $probe
    Check ((Get-FileHash -LiteralPath (Join-Path $installation 'AsukaLens.exe')).Hash.ToLowerInvariant() -ceq $exeRecord[0].sha256) 'candidate application remains intact'
    Check ((Invoke-Setup (Join-Path $installation 'unins000.exe') 'uninstall.log') -eq 0) 'synthetic installation uninstalled'
    Check (-not (Test-Path -LiteralPath $registry)) 'test registration removed'
    Write-Host ('RESULT: PASS (' + $checks.Count + ' checks). Logs are local and contain paths; do not share them.')
} finally {
    if (Test-Path -LiteralPath $registry) {
        $registered = [string](Get-ItemProperty -LiteralPath $registry).InstallLocation
        if ([string]::IsNullOrWhiteSpace($registered) -or -not [IO.Path]::GetFullPath($registered).TrimEnd('\').Equals([IO.Path]::GetFullPath($installation).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) {
            Write-Warning 'Installation ownership is unclear; automatic cleanup refused.'
        } else {
            $uninstaller = Join-Path $installation 'unins000.exe'
            if (Test-Path -LiteralPath $uninstaller) { [void](Invoke-Setup $uninstaller 'cleanup.log') }
        }
    }
}
