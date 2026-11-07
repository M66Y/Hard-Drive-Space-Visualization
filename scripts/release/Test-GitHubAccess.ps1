#requires -Version 7.4
[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$report=[ordered]@{schema=1;repository='M66Y/Hard-Drive-Space-Visualization';cli_available=$false;version=$null;
    required_flags=$false;authenticated=$false;repository_readable=$false;reported_push_permission=$false;
    release_count=$null;telemetry_disabled=$true;credentials_exported=$false;upload_tested=$false;status='tool-unavailable'}
function Invoke-Probe([string[]]$Arguments) {
    $start=[Diagnostics.ProcessStartInfo]::new($script:gh)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach($arg in $Arguments) { $start.ArgumentList.Add($arg) }
    foreach($name in @('GH_NO_UPDATE_NOTIFIER','GH_NO_EXTENSION_UPDATE_NOTIFIER','GH_PROMPT_DISABLED')) { $start.Environment[$name]='1' }
    $start.Environment['GH_TELEMETRY']='false'
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); $process.WaitForExit(); throw 'Probe timed out.' }
        # Keep raw process output in memory; the public report is reconstructed from fixed fields below.
        $text=$stdout.GetAwaiter().GetResult(); [void]$stderr.GetAwaiter().GetResult()
        return [pscustomobject]@{exit_code=$process.ExitCode;text=$text}
    } finally { $process.Dispose() }
}
try {
    $script:gh=Resolve-ReleaseGitHubCli
    $report.cli_available=$true; $report.status='tool-check-failed'
    $version=Invoke-Probe @('--version')
    $match=[regex]::Match($version.text,'^gh version ([0-9]+\.[0-9]+\.[0-9]+)(?:\s|$)')
    if ($version.exit_code -ne 0 -or -not $match.Success) { throw 'CLI version unavailable.' }
    $report.version=$match.Groups[1].Value
    $api=Invoke-Probe @('api','--help'); $create=Invoke-Probe @('release','create','--help')
    if ($api.exit_code -ne 0 -or $create.exit_code -ne 0 -or -not $api.text.Contains('--paginate') -or
        -not $api.text.Contains('--slurp') -or -not $create.text.Contains('--verify-tag')) { throw 'Required CLI flags unavailable.' }
    $report.required_flags=$true; $report.status='authentication-required'
    $auth=Invoke-Probe @('auth','status','--active','--hostname','github.com','--no-refresh')
    if ($auth.exit_code -eq 0) {
        $report.authenticated=$true; $report.status='repository-check-failed'
        $repo=Invoke-Probe @('api',('repos/'+$report.repository),'--hostname','github.com','-H','X-GitHub-Api-Version: 2026-03-10')
        if ($repo.exit_code -ne 0) { throw 'Repository access unavailable.' }
        $data=$repo.text | ConvertFrom-Json
        if ($data.full_name -cne $report.repository -or $data.permissions.push -isnot [bool]) { throw 'Unexpected repository response.' }
        $report.repository_readable=$true; $report.reported_push_permission=$data.permissions.push
        $releases=Invoke-Probe @('api',('repos/'+$report.repository+'/releases'),'--hostname','github.com','--paginate','--slurp','-H','X-GitHub-Api-Version: 2026-03-10')
        if ($releases.exit_code -ne 0) { throw 'Release listing unavailable.' }
        $pages=ConvertFrom-Json -InputObject $releases.text -NoEnumerate
        $report.release_count=@($pages | ForEach-Object { foreach($entry in $_) { $entry } }).Count
        $report.status=if($report.reported_push_permission){'read-checks-passed'}else{'push-permission-missing'}
    }
} catch {
    # Keep the last fixed stage code; never print raw exceptions, output, account names or tokens.
}
[pscustomobject]$report
