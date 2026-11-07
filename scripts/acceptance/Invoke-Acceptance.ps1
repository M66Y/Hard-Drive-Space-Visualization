#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Inspect','Start','Record','Export','Cleanup')][string]$Action,
    [Parameter(Mandatory)][string]$Workspace,
    [string]$Assets,
    [string]$ManifestSha256,
    [string]$Check,
    [string]$Result,
    [string]$Provenance,
    [string]$Output
)
$ErrorActionPreference='Stop'
$session=$null
try {
    . (Join-Path $PSScriptRoot 'Acceptance.Common.ps1')
    if ($Action -eq 'Prepare') {
        New-AcceptanceWorkspace $Assets $ManifestSha256 $Workspace
        Write-Host 'Prepared. All manual checks remain pending. Read docs/offline-acceptance.md before starting.'
        Write-Host ('Synthetic scan directory: '+(Join-Path $Workspace 'sample'))
    } else {
        $session=Open-AcceptanceSession $Workspace -Cleanup:($Action -eq 'Cleanup')
        switch ($Action) {
            'Inspect' { Get-AcceptanceReport $session | ConvertTo-Json -Depth 16 }
            'Start' { Start-AcceptanceApp $session; Write-Host 'Started with isolated data. Record actual observations; launch is not a pass.' }
            'Record' { Set-AcceptanceResult $session $Check $Result $Provenance; Write-Host 'Operator observation saved.' }
            'Export' {
                $target=Assert-AcceptancePath $Output
                if ($target.StartsWith($session.root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'ACCEPT_EXPORT_OUTSIDE_WORKSPACE_REQUIRED' }
                Write-AcceptanceJson (Get-AcceptanceReport $session) $target -New
                Write-Host 'Whitelisted observations exported. Release gates remain unevaluated.'
            }
            'Cleanup' { Remove-AcceptanceWorkspace $session; Write-Host 'Owned workspace removed; source assets preserved.' }
        }
    }
} catch {
    $code=if ($_.Exception.Message -cmatch '^ACCEPT_[A-Z_]+$') { $_.Exception.Message } else { 'ACCEPT_OPERATION_FAILED' }
    [Console]::Error.WriteLine(('Acceptance failed: '+$code))
    exit 1
} finally { if ($session) { $session.lock.Dispose() } }
