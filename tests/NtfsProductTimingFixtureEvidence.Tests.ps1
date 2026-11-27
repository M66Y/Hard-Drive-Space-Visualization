#requires -Version 7.4
# Synthetic JSON records and in-memory checks only.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../scripts/ntfs-prototype/KernelMatrixEvidence.ps1')
. (Join-Path $PSScriptRoot '../scripts/ntfs-prototype/ProductTimingFixtureEvidence.ps1')
$checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:checks++
    Write-Output ('PASS NTFS product evidence ' + $Name)
}
function Reject([scriptblock]$Operation, [string]$Name) {
    $message = $null
    try { & $Operation | Out-Null } catch { $message = $_.Exception.Message }
    Check ($message -ceq 'PRODUCT_TIMING_FIXTURE_EVIDENCE_INVALID') $Name
}
function New-TestRecords([string]$Mode, [switch]$DropTransition, [switch]$RepeatPhase) {
    $fast = $Mode -ceq 'F'
    $stages = if ($fast) {
        @('Bridge_LaunchStarting','Bridge_LaunchReturned','Bridge_Connected','Fast_Snapshot','Fast_Reading','Fast_Discovering',
            'Fast_Reading','Fast_Supplementing','Fast_Building','Fast_Cleanup','Bridge_ResultDecoded','UiHandoff')
    } elseif ($DropTransition) {
        @('Ordinary_Start') + @((1..63 | ForEach-Object { 'Ordinary_Scanning'; 'Ordinary_Finalizing' }))
    } elseif ($RepeatPhase) {
        @('Ordinary_Start','Ordinary_Scanning','Ordinary_Finalizing','Ordinary_Scanning','UiHandoff')
    } else {
        @('Ordinary_Start','Ordinary_Preparing','Ordinary_Scanning','Ordinary_Finalizing','UiHandoff')
    }
    $revision = 'personal-ntfs-product-timing-v1'
    $intervals = [ordered]@{UiStart=2.0}
    $transitions = @([ordered]@{phase='UiStart';at_ms=0.0})
    $at = 2
    foreach ($stage in $stages) {
        [ordered]@{revision=$revision;kind='stage';mode=$Mode;stage=$stage}
        if (-not $intervals.Contains($stage)) { $intervals[$stage] = 0.0 }
        $intervals[$stage] += 2
        $transitions += [ordered]@{phase=$stage;at_ms=$at}
        $at += 2
    }
    if ($DropTransition) { $intervals.UiHandoff = 2; $at += 2 }
    $resources = [ordered]@{cpu_ms=10.5;sampled_peak_working_set_bytes=5000;process_read_bytes=1024;process_write_bytes=$null
        start_working_set_bytes=$null;end_working_set_bytes=4000;samples=2}
    [ordered]@{revision=$revision;kind='sample';mode=$Mode
        total_ms=$at;coordinator_available_ms=($at-2);ui_handoff_query_and_final_paint_ms=2
        bridge_ms=$(if($fast){4.5});publication_validation_ms=$(if($fast){0.5});first_observed_preview_ms=$null
        intervals=$intervals;transitions=$transitions;dropped_transition_details=$(if($DropTransition){1}else{0})
        ui_resources=$resources;helper_resources_at_result=$(if($fast){$resources})
        helper_resource_scope='launch returned to decoded result; excludes earlier initialization and later shutdown'
        launch_wait_includes_consent_and_startup=$fast;human_wait_separately_measurable=$false;phases_observed_at_client=$true;cleanup_transfer_decode_combined=$fast
        ui_drawing_included=$true;synchronous_final_paint_included=$true;dwm_presentation_measured=$false
        initialization_excluded=$true;audit_added=$false;cleanup_confirmed=$true;fallback=$false;physical_io_measured=$false
        resource_sample_interval_ms=250;gc_between_runs=$true;compact_diagnostics_retained_between_runs=$true}
}
function New-TestComparison([switch]$Different, [string]$Left='F', [string]$Right='O') {
    return [ordered]@{revision='personal-ntfs-timing-v2';kind='comparison';left=$Left;right=$Right
        included_scope_equal=(-not $Different);root_metrics_equal=(-not $Different);exclusions_equal=$true;exclusion_details_complete=$true
        other_issues_absent=$true;allocation_known=$true;all_equal=(-not $Different);changes_or_scope_difference_observed=[bool]$Different
        added_entries_observed=$false;removed_entries_observed=$false;entry_type_changes_observed=$false;identity_changes_observed=$false
        file_logical_changes_observed=[bool]$Different;file_allocation_changes_observed=$false;file_link_changes_observed=$false
        snapshot_mtime_check_available=($Left -ceq 'F' -or $Right -ceq 'F');changed_entry_mtime_after_snapshot=$false;difference_categories_prove_external_changes=$false}
}
function New-TestComplete([switch]$Different, [string]$Order='FO', [switch]$Incomplete) {
    $row = [ordered]@{revision='personal-ntfs-timing-v2';kind='complete';order=$Order;compared=$true;all_equal=(-not $Different)
        requested_order_finished=(-not $Incomplete);performance_conclusion_allowed=$false;files_written=$false}
    if (-not $Different) {
        $row.diagnostic_pair_only=$Order.Length -eq 2
        $row.stable_speedup_claim_allowed=$false
        $row.ui_drawing_included=$true
    }
    return $row
}
function New-TestFailure([bool]$Cleanup, [string]$Stage='Preparing') {
    return [ordered]@{revision='personal-ntfs-timing-v2';kind='failed';code='TimingFailed';stage=$Stage
        cleanup_confirmed=$Cleanup;further_runs_started=$false;performance_conclusion_allowed=$false}
}
function Join-TestRecords([object[]]$Rows) {
    return (($Rows | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 12 -Compress }) -join "`n") + "`n"
}
function New-TestPair([switch]$Different) {
    @(New-TestRecords F) + @(New-TestRecords O) + @((New-TestComparison -Different:$Different),(New-TestComplete -Different:$Different))
}
function New-TestGroup([int]$DifferenceAt=0) {
    $order = 'FOOF'
    for ($index = 0; $index -lt $order.Length; $index++) {
        $mode = [string]$order[$index]
        New-TestRecords $mode
        if ($index -eq 0) { continue }
        $different = $index -eq $DifferenceAt
        New-TestComparison -Left ([string]$order[$index-1]) -Right $mode -Different:$different
        if ($different) {
            New-TestComplete -Order $order -Different -Incomplete:($index -lt $order.Length-1)
            return
        }
    }
    New-TestComplete -Order $order
}

$rows = @(New-TestPair)
$report = Join-TestRecords $rows
$value = ConvertTo-NtfsProductTimingFixtureEvidence $report 0
Check ($value.status -ceq 'comparable' -and $value.comparable -and $value.cleanup_confirmed) 'valid FO pair'
Check ($value.fast_ms -eq 26 -and $value.ordinary_ms -eq 12 -and $value.fast.bridge_ms -eq 4.5) 'reported timing survives validation'
Check ($value.fast.intervals.Fast_Reading -eq 4.0 -and $value.fast.ui_resources.cpu_ms -eq 10.5 -and
    $null -eq $value.ordinary.helper_resources_at_result) 'fixed stage and resource metrics retained'
Check ((ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace("`n","`r`n")) 0).comparable) 'Windows JSON lines accepted'
Check ((ConvertTo-NtfsProductTimingFixtureEvidence $report.TrimEnd() 0).comparable) 'last newline optional'
$incomparable = ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords @(New-TestPair -Different)) 3
Check ($incomparable.status -ceq 'not_comparable' -and -not $incomparable.comparable -and $incomparable.cleanup_confirmed) 'incomparable results still settle their snapshot'
$failed = ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords @((New-TestFailure $true))) 1
Check ($failed.status -ceq 'failed' -and -not $failed.comparable -and -not $failed.cleanup_confirmed) 'generic failure before fast sample cannot authorize cleanup'
$partial = @(New-TestRecords F)[0..5] + @((New-TestFailure $true 'Fast_Reading'))
Check (-not (ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords $partial) 1).cleanup_confirmed) 'partial fast trace with asserted cleanup remains conservative'
$afterFast = @(New-TestRecords F) + @((New-TestRecords O)[0],(New-TestFailure $false 'Ordinary_Start'))
$failedAfterFast = ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords $afterFast) 1
Check ($failedAfterFast.cleanup_confirmed -and -not $failedAfterFast.comparable -and $null -eq $failedAfterFast.ordinary_ms) 'validated fast sample settles snapshot before ordinary failure'
$cappedRows = @(New-TestRecords F) + @(New-TestRecords O -DropTransition) + @((New-TestComparison),(New-TestComplete))
Check ((ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords $cappedRows) 0).comparable) 'bounded transition truncation retains total phase timing'
$repeatedRows = @(New-TestRecords F) + @(New-TestRecords O -RepeatPhase) + @((New-TestComparison),(New-TestComplete))
Check ((ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords $repeatedRows) 0).comparable) 'naturally repeated non-adjacent phases are accepted'

Reject { ConvertTo-NtfsProductTimingFixtureEvidence $report 3 } 'exit code and terminal agree'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence $report 7 } 'unexpected exit code rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report+"`n") 0 } 'blank trailing record rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report+'null') 0 } 'unexpected records after terminal rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence '' 0 } 'empty report rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ('x' * 262145) 0 } 'encoded report over budget rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ('x' * 65537) 0 } 'single encoded line over budget rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ('{' + $report) 0 } 'invalid JSON rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"kind":"complete"','"kind":"complete","Kind":"complete"')) 0 } 'duplicate and case-alias keys rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"phase":"UiStart"','"phase":"UiStart","phase":"UiStart"')) 0 } 'nested duplicate keys rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords $rows[0..($rows.Length-2)]) 0 } 'missing terminal rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords (@($rows[0]) + $rows)) 0 } 'duplicate first stage rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords (@($rows[1],$rows[0]) + $rows[2..($rows.Length-1)])) 0 } 'swapped fast bridge sequence rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords ($rows[0..6] + @($rows[8],$rows[7]) + $rows[9..($rows.Length-1)])) 0 } 'swapped fast supplement and build rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords ($rows[0..($rows.Length-3)] + @($rows[-1]))) 0 } 'missing comparison rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords ($rows[0..($rows.Length-2)] + $rows[-2..-1])) 0 } 'duplicate comparison rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords (@(New-TestRecords O) + @(New-TestRecords F) + $rows[-2..-1])) 0 } 'OF cannot pass FO contract'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"order":"FO"','"order":"FOOF"')) 0 } 'longer order rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"personal-ntfs-product-timing-v1"','"personal-ntfs-timing-v2"')) 0 } 'non-product trace rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"total_ms":26','"total_ms":800000')) 0 } 'elapsed budget exceeded'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"total_ms":26','"total_ms":1e999')) 0 } 'non-finite elapsed rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"total_ms":26','"total_ms":"26"')) 0 } 'quoted number rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"total_ms":26','"total_ms":-1')) 0 } 'negative elapsed rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"total_ms":26','"total_ms":28')) 0 } 'timing totals inconsistent'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"UiStart":2.0','"UiStart":8.0')) 0 } 'stage interval totals inconsistent'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"Fast_Reading":4.0','"Fast_Unknown":4.0')) 0 } 'unknown nested stage rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"at_ms":2','"at_ms":-1')) 0 } 'invalid elapsed transition rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"fallback":false','"fallback":true')) 0 } 'fast fallback rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"cleanup_confirmed":true','"cleanup_confirmed":false')) 0 } 'uncleaned fast sample rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"ui_drawing_included":true','"ui_drawing_included":false')) 0 } 'UI flag required'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"synchronous_final_paint_included":true','"synchronous_final_paint_included":false')) 0 } 'final paint flag required'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"diagnostic_pair_only":true','"diagnostic_pair_only":false')) 0 } 'diagnostic-only terminal flag required'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"stable_speedup_claim_allowed":false','"stable_speedup_claim_allowed":true')) 0 } 'stable speedup not asserted from single pair'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"files_written":false','"files_written":true')) 0 } 'unexpected runner write flag rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"included_scope_equal":true','"included_scope_equal":false')) 0 } 'comparison flags corroborate equality'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"right":"O"','"right":"F"')) 0 } 'comparison modes verified'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"cpu_ms":10.5','"cpu_ms":10.5,"path":"synthetic"')) 0 } 'unexpected resource field rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"samples":2','"samples":12000')) 0 } 'resource sampling budget enforced'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"samples":2','"samples":0')) 0 } 'resource peak must have observations'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"UiHandoff","at_ms":24','"Ordinary_Start","at_ms":24')) 0 } 'nested sequence and stage stream must match'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($report.Replace('"stage":"UiHandoff"','"stage":"SyntheticPath"')) 0 } 'unknown top-level stage rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords (@((New-TestFailure $true),(New-TestFailure $true)))) 1 } 'terminal failure cannot be repeated'

$group = @(New-TestGroup)
$balancedReport = Join-TestRecords $group
$balanced = ConvertTo-NtfsProductTimingFixtureEvidence $balancedReport 0 -Order FOOF
Check ($balanced.status -ceq 'comparable' -and $balanced.cleanup_confirmed -and
    ($balanced.samples.mode -join '') -ceq 'FOOF' -and $balanced.completed_comparisons -eq 3) 'valid four-run group preserves sequence and three comparisons'
Check ($balanced.samples[0].sample.total_ms -eq $balanced.fast_ms -and
    $balanced.samples[1].sample.total_ms -eq $balanced.ordinary_ms -and
    $balanced.samples[3].sample.bridge_ms -eq 4.5) 'first-run summary and repeat samples remain available'
Check ($value.samples.Count -eq 2 -and $value.completed_comparisons -eq 1) 'FO reports the same pair with one validated comparison'

foreach ($differenceAt in 1..3) {
    $differentGroup = @(New-TestGroup -DifferenceAt $differenceAt)
    $different = ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords $differentGroup) 3 -Order FOOF
    Check ($different.status -ceq 'not_comparable' -and $different.cleanup_confirmed -and
        $different.completed_comparisons -eq $differenceAt -and $different.samples.Count -eq ($differenceAt+1)) "difference at comparison $differenceAt stops with owned snapshots settled"
}
$firstPair = @(New-TestRecords F) + @(New-TestRecords O) + @((New-TestComparison))
$thirdSample = $firstPair + @(New-TestRecords O)
$throughThird = $thirdSample + @((New-TestComparison -Left O -Right O))
$throughFourth = $throughThird + @(New-TestRecords F)
$duringOrdinary = $firstPair + @((New-TestRecords O)[0],(New-TestFailure $false 'Ordinary_Start'))
$duringSecondFast = $throughThird + @( (New-TestRecords F)[0..5] ) + @((New-TestFailure $true 'Fast_Reading'))
$afterSecondFast = $throughFourth + @((New-TestFailure $false 'FingerprintOutsideTiming'))
$afterThirdSample = $thirdSample + @((New-TestFailure $false 'FingerprintOutsideTiming'))
Check ((ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords $duringOrdinary) 1 -Order FOOF).cleanup_confirmed) 'failure in second ordinary run keeps confirmed first snapshot cleanup'
Check (-not (ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords $duringSecondFast) 1 -Order FOOF).cleanup_confirmed) 'first fast sample cannot authorize cleanup of second fast run'
Check (-not (ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords ($throughThird + @((New-TestFailure $true 'FingerprintOutsideTiming')))) 1 -Order FOOF).cleanup_confirmed) 'an unobserved second fast launch remains uncertain'
Check ((ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords $afterSecondFast) 1 -Order FOOF).cleanup_confirmed) 'fourth sample settles second snapshot even if fingerprint fails'
Check ((ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords $afterThirdSample) 1 -Order FOOF).cleanup_confirmed) 'pending third-run comparison precedes second fast launch'

Reject { ConvertTo-NtfsProductTimingFixtureEvidence $balancedReport 0 } 'FOOF requires explicit opt-in'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence $balancedReport 0 -Order FOO } 'arbitrary order rejected'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($balancedReport.Replace('"diagnostic_pair_only":false','"diagnostic_pair_only":true')) 0 -Order FOOF } 'four-run group cannot assert pair-only mode'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($balancedReport.Replace('"stable_speedup_claim_allowed":false','"stable_speedup_claim_allowed":true')) 0 -Order FOOF } 'balanced run cannot assert stable speedup'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($balancedReport.Replace('"snapshot_mtime_check_available":false','"snapshot_mtime_check_available":true')) 0 -Order FOOF } 'OO comparison cannot invent a snapshot mtime'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($balancedReport.Replace('"left":"O","right":"F"','"left":"F","right":"O"')) 0 -Order FOOF } 'third comparison must be OF in order'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords ($firstPair + @((New-TestComplete -Order FOOF)))) 0 -Order FOOF } 'matched prefix cannot report success'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords ($thirdSample + @(New-TestRecords F) + $group[-2..-1])) 0 -Order FOOF } 'next run cannot skip OO comparison'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords ($throughFourth + @((New-TestComplete -Order FOOF)))) 0 -Order FOOF } 'group needs final OF comparison'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ($balancedReport + '{"kind":"sample"}') 0 -Order FOOF } 'reject group records after terminal'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords (@(New-TestGroup -DifferenceAt 1)[0..($firstPair.Length-1)] + @(New-TestRecords O) + @((New-TestComplete -Order FOOF -Different -Incomplete)))) 3 -Order FOOF } 'no next run can follow a mismatch'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence (Join-TestRecords ($throughThird + @((New-TestComplete -Order FOOF -Different -Incomplete)))) 3 -Order FOOF } 'forged mismatch cannot stop matched prefix'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ('x' * 524289) 0 -Order FOOF } 'group report byte limit'
Reject { ConvertTo-NtfsProductTimingFixtureEvidence ("{}`n" * 517) 0 -Order FOOF } 'group report row limit'
Write-Output ('RESULT: PASS (' + $checks + ' checks)')
