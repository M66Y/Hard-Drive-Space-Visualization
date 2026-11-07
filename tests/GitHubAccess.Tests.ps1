#requires -Version 7.4
. (Join-Path $PSScriptRoot '../scripts/release/Release.Common.ps1')
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$parent=Join-Path $repository 'artifacts'
$root=Join-Path $parent ('github-access-check-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$oldPath=$env:PATH; $oldMode=$env:ASUKALENS_GH_PROBE_FIXTURE
$oldTelemetry=[Environment]::GetEnvironmentVariable('GH_TELEMETRY','Process')
$checks=[Collections.Generic.List[string]]::new()
function Check([bool]$Condition,[string]$Name) {
    if (-not $Condition) { throw ('FAIL '+$Name) }
    $checks.Add($Name); Write-Host ('PASS '+$Name)
}
try {
    $scripts=Join-Path $root 'scripts/release'; $binaryDir=Join-Path $root 'artifacts/release-tools/gh-2.103.3/bin'
    [IO.Directory]::CreateDirectory($scripts) | Out-Null
    [IO.Directory]::CreateDirectory($binaryDir) | Out-Null
    foreach($file in @('Release.Common.ps1','Test-GitHubAccess.ps1','github-cli.json')) { Copy-Item -LiteralPath (Join-Path $repository "scripts/release/$file") -Destination $scripts }
    $source=@'
using System;
public static class ProbeFixture {
    public static int Main(string[] args) {
        if (Environment.GetEnvironmentVariable("GH_TELEMETRY") != "false") return 97;
        string mode = Environment.GetEnvironmentVariable("ASUKALENS_GH_PROBE_FIXTURE");
        string command = String.Join(" ", args);
        if (command == "--version") { Console.WriteLine("gh version 2.103.3 (fixture)"); return 0; }
        if (command == "api --help") { Console.WriteLine(mode == "missing-flags" ? "api" : "--paginate --slurp"); return 0; }
        if (command == "release create --help") { Console.WriteLine("--verify-tag"); return 0; }
        if (command == "auth status --active --hostname github.com --no-refresh") {
            Console.Error.WriteLine("SYNTHETIC_PRIVATE_ACCOUNT token=synthetic-token path=X:/private-fixture");
            return mode == "logged-out" ? 1 : 0;
        }
        if (command.StartsWith("api repos/M66Y/Hard-Drive-Space-Visualization/releases ")) {
            if (mode == "release-failure") return 23;
            Console.WriteLine("[[],[{\"id\":1},{\"id\":2}]]"); return 0;
        }
        if (command.StartsWith("api repos/M66Y/Hard-Drive-Space-Visualization ")) {
            if (mode == "repo-failure") return 23;
            if (mode == "invalid-json") { Console.WriteLine("SYNTHETIC_PRIVATE_ACCOUNT invalid-json"); return 0; }
            Console.WriteLine("{\"full_name\":\"M66Y/Hard-Drive-Space-Visualization\",\"permissions\":{\"push\":" +
                (mode == "no-push" ? "false" : "true") + "}}"); return 0;
        }
        return 99; // No write operations or network implementation.
    }
}
'@
    $sourcePath=Join-Path $root 'fixture.cs'; [IO.File]::WriteAllText($sourcePath,$source)
    $compile=Join-Path $root 'compile.ps1'
    [IO.File]::WriteAllText($compile,"param([string]`$Source,[string]`$Output)`n`$ErrorActionPreference='Stop'`nAdd-Type -TypeDefinition ([IO.File]::ReadAllText(`$Source)) -OutputAssembly `$Output -OutputType ConsoleApplication`n")
    $binary=Join-Path $binaryDir 'gh.exe'
    & (Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe') -NoProfile -NonInteractive -File $compile -Source $sourcePath -Output $binary
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic CLI compilation failed' }
    $pin=Get-Content -LiteralPath (Join-Path $scripts 'github-cli.json') -Raw | ConvertFrom-Json
    $pin.exe_sha256=(Get-FileHash -LiteralPath $binary).Hash.ToLowerInvariant()
    Write-ReleaseJson $pin (Join-Path $scripts 'github-cli.json')
    $env:PATH=(Join-Path $env:WINDIR 'System32')+';'+$PSHOME
    $env:GH_TELEMETRY='log'
    $probe=Join-Path $scripts 'Test-GitHubAccess.ps1'
    foreach($case in @(
        @{mode='success';status='read-checks-passed';auth=$true;read=$true;push=$true},
        @{mode='logged-out';status='authentication-required';auth=$false;read=$false;push=$false},
        @{mode='no-push';status='push-permission-missing';auth=$true;read=$true;push=$false},
        @{mode='repo-failure';status='repository-check-failed';auth=$true;read=$false;push=$false},
        @{mode='invalid-json';status='repository-check-failed';auth=$true;read=$false;push=$false},
        @{mode='release-failure';status='repository-check-failed';auth=$true;read=$true;push=$true},
        @{mode='missing-flags';status='tool-check-failed';auth=$false;read=$false;push=$false}
    )) {
        $env:ASUKALENS_GH_PROBE_FIXTURE=$case.mode
        $report=& $probe
        Check ($report.status -eq $case.status -and $report.authenticated -eq $case.auth -and
            $report.repository_readable -eq $case.read -and $report.reported_push_permission -eq $case.push -and -not $report.upload_tested) ('probe state '+$case.mode)
        $json=$report | ConvertTo-Json -Depth 5
        Check ($json -notmatch 'SYNTHETIC_PRIVATE_ACCOUNT|synthetic-token|private-fixture' -and $env:GH_TELEMETRY -ceq 'log') ('private output withheld and caller unchanged '+$case.mode)
        if ($case.mode -eq 'success') { Check ($report.release_count -eq 2) 'paginated releases counted' }
    }
    [IO.File]::AppendAllText($binary,'changed')
    $report=& $probe
    Check (-not $report.cli_available -and $report.status -eq 'tool-unavailable') 'changed portable executable rejected'
    Remove-Item -LiteralPath $binary
    $report=& $probe
    Check (-not $report.cli_available -and -not $report.authenticated) 'missing portable executable rejected'
    Write-Host ('PASS GitHub access checks: '+$checks.Count)
} finally {
    [Environment]::SetEnvironmentVariable('PATH',$oldPath,'Process')
    [Environment]::SetEnvironmentVariable('ASUKALENS_GH_PROBE_FIXTURE',$oldMode,'Process')
    [Environment]::SetEnvironmentVariable('GH_TELEMETRY',$oldTelemetry,'Process')
    Remove-OwnedDirectory $root $parent
}
