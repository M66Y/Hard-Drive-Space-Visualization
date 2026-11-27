#requires -Version 7.4
# Pure stdout validation for one ordinary-desktop FO pair. The caller also
# loads KernelMatrixEvidence.ps1 for the existing strict JSON key validator.
function ConvertTo-NtfsProductTimingFixtureEvidence([string]$Report, [int]$ExitCode) {
    $invalid = 'PRODUCT_TIMING_FIXTURE_EVIDENCE_INVALID'
    try {
        function Assert-TimingFields($Value, [string[]]$Fields) {
            if ($Value -isnot [System.Collections.IDictionary] -or $Value.Count -ne $Fields.Length) { throw $invalid }
            foreach ($key in $Value.Keys) { if ($key -cnotin $Fields) { throw $invalid } }
        }
        function Assert-TimingFlag($Value, [bool]$Expected) {
            if ($Value -isnot [bool] -or $Value -ne $Expected) { throw $invalid }
        }
        function Assert-TimingInteger($Value, [long]$Minimum, [long]$Maximum) {
            if (($Value -isnot [int] -and $Value -isnot [long]) -or $Value -lt $Minimum -or $Value -gt $Maximum) { throw $invalid }
        }
        function Assert-TimingNumber($Value, [double]$Maximum, [switch]$Optional) {
            if ($Optional -and $null -eq $Value) { return }
            if ($Value -isnot [double] -and $Value -isnot [int] -and $Value -isnot [long]) { throw $invalid }
            if (-not [double]::IsFinite([double]$Value) -or $Value -lt 0 -or $Value -gt $Maximum) { throw $invalid }
        }
        function Convert-TimingResources($Value) {
            $fields = @('cpu_ms','sampled_peak_working_set_bytes','process_read_bytes','process_write_bytes',
                'start_working_set_bytes','end_working_set_bytes','samples')
            Assert-TimingFields $Value $fields
            Assert-TimingNumber $Value.cpu_ms 1000000000 -Optional
            Assert-TimingInteger $Value.samples 0 10000
            foreach ($field in $fields[1..5]) {
                if ($null -ne $Value[$field]) { Assert-TimingInteger $Value[$field] 0 1PB }
            }
            if (($Value.samples -eq 0) -ne ($null -eq $Value.sampled_peak_working_set_bytes)) { throw $invalid }
            $resources = [ordered]@{}
            foreach ($field in $fields) { $resources[$field] = $Value[$field] }
            return $resources
        }
        $bridgeStages = @('Bridge_LaunchStarting','Bridge_LaunchReturned','Bridge_Connected','Bridge_ResultDecoded')
        $fastStages = @('Fast_Preparing','Fast_Snapshot','Fast_Discovering','Fast_Reading','Fast_Supplementing','Fast_Building','Fast_Cleanup')
        $ordinaryStages = @('Ordinary_Preparing','Ordinary_Scanning','Ordinary_Verifying','Ordinary_Reusing','Ordinary_Finalizing')
        $failureCodes = @('AppHostRequired','Unsupported','PermissionDenied','LimitExceeded','SourceChanged','SnapshotFailed',
            'ReadFailed','Cancelled','ProtocolError','CleanupUnconfirmed','InternalError','TimingInputOrFingerprint','TimingFailed')

        function Assert-TimingNextStage([string]$Stage, [string]$Mode, $Stages) {
            if ($Stages.Count -ge 127 -or $Stages.Contains('UiHandoff') -or
                ($Stages.Count -gt 0 -and $Stage -ceq $Stages[$Stages.Count-1])) { throw $invalid }
            if ($Mode -ceq 'O') {
                if ($Stages.Count -eq 0) {
                    if ($Stage -cne 'Ordinary_Start') { throw $invalid }
                } elseif ($Stage -cne 'UiHandoff' -and $Stage -cnotin $ordinaryStages) { throw $invalid }
            } elseif ($Stage -cin $bridgeStages) {
                $step = [array]::IndexOf($bridgeStages, $Stage)
                if ($Stages.Contains($Stage) -or ($step -eq 0 -and $Stages.Count -ne 0) -or
                    ($step -gt 0 -and -not $Stages.Contains($bridgeStages[$step-1])) -or
                    ($step -lt 3 -and $Stages.Count -ne $step) -or
                    ($step -eq 3 -and ($Stages[$Stages.Count-1] -cne 'Fast_Cleanup' -or -not $Stages.Contains('Fast_Building')))) { throw $invalid }
            } elseif ($Stage -cin $fastStages) {
                if (-not $Stages.Contains('Bridge_Connected') -or $Stages.Contains('Bridge_ResultDecoded') -or
                    $Stages.Contains('Fast_Cleanup')) { throw $invalid }
                $previous = $Stages[$Stages.Count-1]
                switch -CaseSensitive ($Stage) {
                    'Fast_Preparing' { if ($previous -cne 'Bridge_Connected') { throw $invalid } }
                    'Fast_Snapshot' { if ($previous -cnotin @('Bridge_Connected','Fast_Preparing')) { throw $invalid } }
                    'Fast_Reading' { if ($previous -cnotin @('Fast_Snapshot','Fast_Discovering')) { throw $invalid } }
                    'Fast_Discovering' { if ($previous -cne 'Fast_Reading' -or $Stages.Contains($Stage)) { throw $invalid } }
                    'Fast_Supplementing' { if ($previous -cne 'Fast_Reading' -or -not $Stages.Contains('Fast_Discovering')) { throw $invalid } }
                    'Fast_Building' { if ($previous -cnotin @('Fast_Reading','Fast_Supplementing') -or -not $Stages.Contains('Fast_Discovering')) { throw $invalid } }
                    'Fast_Cleanup' { if (-not $Stages.Contains('Fast_Snapshot')) { throw $invalid } }
                }
            } elseif ($Stage -cne 'UiHandoff' -or -not $Stages.Contains('Bridge_ResultDecoded')) { throw $invalid }
        }
        function Convert-TimingSample($Value, [string]$Mode, $Stages) {
            Assert-TimingFields $Value @('revision','kind','mode','total_ms','coordinator_available_ms',
                'ui_handoff_query_and_final_paint_ms','bridge_ms','publication_validation_ms','first_observed_preview_ms','intervals',
                'transitions','dropped_transition_details','ui_resources','helper_resources_at_result','helper_resource_scope',
                'launch_wait_includes_consent_and_startup','human_wait_separately_measurable','phases_observed_at_client','cleanup_transfer_decode_combined',
                'ui_drawing_included','synchronous_final_paint_included','dwm_presentation_measured','initialization_excluded','audit_added',
                'cleanup_confirmed','fallback','physical_io_measured','resource_sample_interval_ms','gc_between_runs','compact_diagnostics_retained_between_runs')
            $isFast = $Mode -ceq 'F'
            foreach ($field in @('ui_drawing_included','synchronous_final_paint_included','initialization_excluded','cleanup_confirmed',
                'phases_observed_at_client','gc_between_runs','compact_diagnostics_retained_between_runs')) { Assert-TimingFlag $Value[$field] $true }
            foreach ($field in @('human_wait_separately_measurable','dwm_presentation_measured','audit_added','fallback',
                'physical_io_measured')) { Assert-TimingFlag $Value[$field] $false }
            Assert-TimingFlag $Value.launch_wait_includes_consent_and_startup $isFast
            Assert-TimingFlag $Value.cleanup_transfer_decode_combined $isFast
            Assert-TimingInteger $Value.resource_sample_interval_ms 250 250
            Assert-TimingInteger $Value.dropped_transition_details 0 1000000
            Assert-TimingNumber $Value.total_ms 780000
            Assert-TimingNumber $Value.coordinator_available_ms $Value.total_ms
            Assert-TimingNumber $Value.ui_handoff_query_and_final_paint_ms $Value.total_ms
            Assert-TimingNumber $Value.first_observed_preview_ms $Value.total_ms -Optional
            if ([math]::Abs($Value.total_ms - ($Value.coordinator_available_ms + $Value.ui_handoff_query_and_final_paint_ms)) -gt 1) { throw $invalid }
            if ($Value.helper_resource_scope -isnot [string] -or $Value.helper_resource_scope -cne
                'launch returned to decoded result; excludes earlier initialization and later shutdown') { throw $invalid }
            if ($isFast) {
                Assert-TimingNumber $Value.bridge_ms $Value.coordinator_available_ms
                Assert-TimingNumber $Value.publication_validation_ms $Value.coordinator_available_ms
            } elseif ($null -ne $Value.bridge_ms -or $null -ne $Value.publication_validation_ms -or $null -ne $Value.helper_resources_at_result) { throw $invalid }

            if ($Value.transitions -isnot [array] -or $Value.transitions.Length -ne $Stages.Count+1 -or
                $Value.transitions.Length -gt 128 -or ($Value.dropped_transition_details -gt 0 -and $Value.transitions.Length -ne 128)) { throw $invalid }
            if ($Value.dropped_transition_details -eq 0 -and ($Stages.Count -lt 2 -or $Stages[$Stages.Count-1] -cne 'UiHandoff')) { throw $invalid }
            $prior = [double]0
            for ($i = 0; $i -lt $Value.transitions.Length; $i++) {
                $transition = $Value.transitions[$i]
                Assert-TimingFields $transition @('phase','at_ms')
                $expected = if ($i -eq 0) { 'UiStart' } else { $Stages[$i-1] }
                Assert-TimingNumber $transition.at_ms $Value.total_ms
                if ($transition.phase -isnot [string] -or $transition.phase -cne $expected -or
                    $transition.at_ms -lt $prior -or ($i -eq 0 -and $transition.at_ms -ne 0)) { throw $invalid }
                $prior = [double]$transition.at_ms
            }
            $allowed = @('UiStart','UiHandoff') + $(if ($isFast) { $bridgeStages + $fastStages } else { @('Ordinary_Start') + $ordinaryStages })
            if ($Value.intervals -isnot [System.Collections.IDictionary] -or $Value.intervals.Count -gt $allowed.Length) { throw $invalid }
            $intervals = [ordered]@{}
            $intervalTotal = [double]0
            foreach ($phase in $Value.intervals.Keys) {
                if ($phase -cnotin $allowed) { throw $invalid }
                Assert-TimingNumber $Value.intervals[$phase] $Value.total_ms
                $intervalTotal += $Value.intervals[$phase]
                $intervals[$phase] = [double]$Value.intervals[$phase]
            }
            foreach ($stage in @('UiStart','UiHandoff') + @($Stages)) {
                if ($stage -cnotin @($intervals.Keys)) { throw $invalid }
            }
            if ([math]::Abs($intervalTotal - $Value.total_ms) -gt 1) { throw $invalid }
            $ui = Convert-TimingResources $Value.ui_resources
            $helper = if ($null -ne $Value.helper_resources_at_result) { Convert-TimingResources $Value.helper_resources_at_result }
            return [ordered]@{total_ms=[double]$Value.total_ms
                coordinator_available_ms=[double]$Value.coordinator_available_ms
                ui_handoff_query_and_final_paint_ms=[double]$Value.ui_handoff_query_and_final_paint_ms
                bridge_ms=$Value.bridge_ms; publication_validation_ms=$Value.publication_validation_ms
                first_observed_preview_ms=$Value.first_observed_preview_ms
                intervals=$intervals; ui_resources=$ui; helper_resources_at_result=$helper}
        }

        if ($ExitCode -notin @(0,1,3) -or [string]::IsNullOrEmpty($Report) -or
            [Text.Encoding]::UTF8.GetByteCount($Report) -gt 262144) { throw $invalid }
        if ($Report.EndsWith("`r`n")) { $Report = $Report.Substring(0,$Report.Length-2) }
        elseif ($Report.EndsWith("`n")) { $Report = $Report.Substring(0,$Report.Length-1) }
        $lines = $Report -split '\r?\n'
        if ($lines.Count -gt 258) { throw $invalid }
        $fast = $null
        $ordinary = $null
        $comparison = $null
        $terminal = $null
        $fastStagesSeen = [Collections.Generic.List[string]]::new()
        $ordinaryStagesSeen = [Collections.Generic.List[string]]::new()
        foreach ($line in $lines) {
            if ($null -ne $terminal) { throw $invalid }
            Assert-UniqueJsonKeys $line
            $row = ConvertFrom-Json -InputObject $line -AsHashtable -NoEnumerate -Depth 16
            if ($row -isnot [System.Collections.IDictionary] -or $row['revision'] -isnot [string] -or $row['kind'] -isnot [string]) { throw $invalid }
            switch -CaseSensitive ($row['kind']) {
                'stage' {
                    Assert-TimingFields $row @('revision','kind','mode','stage')
                    if ($row.revision -cne 'personal-ntfs-product-timing-v1' -or $null -ne $ordinary -or
                        $row.mode -isnot [string] -or $row.stage -isnot [string]) { throw $invalid }
                    $mode = if ($null -eq $fast) { 'F' } else { 'O' }
                    if ($row.mode -cne $mode) { throw $invalid }
                    if ($mode -ceq 'F') { Assert-TimingNextStage $row.stage $mode $fastStagesSeen; $fastStagesSeen.Add($row.stage) }
                    else { Assert-TimingNextStage $row.stage $mode $ordinaryStagesSeen; $ordinaryStagesSeen.Add($row.stage) }
                }
                'sample' {
                    if ($row.revision -cne 'personal-ntfs-product-timing-v1' -or $null -ne $ordinary -or $row['mode'] -isnot [string]) { throw $invalid }
                    if ($null -eq $fast) {
                        if ($row.mode -cne 'F') { throw $invalid }
                        $fast = Convert-TimingSample $row 'F' $fastStagesSeen
                    } else {
                        if ($row.mode -cne 'O') { throw $invalid }
                        $ordinary = Convert-TimingSample $row 'O' $ordinaryStagesSeen
                    }
                }
                'comparison' {
                    $equalFields = @('included_scope_equal','root_metrics_equal','exclusions_equal','exclusion_details_complete',
                        'other_issues_absent','allocation_known')
                    $differenceFields = @('added_entries_observed','removed_entries_observed','entry_type_changes_observed',
                        'identity_changes_observed','file_logical_changes_observed','file_allocation_changes_observed','file_link_changes_observed',
                        'changed_entry_mtime_after_snapshot')
                    Assert-TimingFields $row (@('revision','kind','left','right','all_equal','changes_or_scope_difference_observed',
                            'snapshot_mtime_check_available','difference_categories_prove_external_changes')+$equalFields+$differenceFields)
                    if ($row.revision -cne 'personal-ntfs-timing-v2' -or $row.left -isnot [string] -or $row.right -isnot [string] -or
                        $row.left -cne 'F' -or $row.right -cne 'O' -or $null -eq $ordinary -or $null -ne $comparison) { throw $invalid }
                    $allEqual = $true
                    foreach ($field in $equalFields) {
                        if ($row[$field] -isnot [bool]) { throw $invalid }
                        $allEqual = $allEqual -and $row[$field]
                    }
                    foreach ($field in $differenceFields) {
                        if ($row[$field] -isnot [bool] -or ($allEqual -and $row[$field])) { throw $invalid }
                    }
                    Assert-TimingFlag $row.all_equal $allEqual
                    Assert-TimingFlag $row.changes_or_scope_difference_observed (-not $allEqual)
                    Assert-TimingFlag $row.difference_categories_prove_external_changes $false
                    Assert-TimingFlag $row.snapshot_mtime_check_available $true
                    $comparison = $row
                }
                'complete' {
                    if ($row.revision -cne 'personal-ntfs-timing-v2' -or $null -eq $comparison) { throw $invalid }
                    $fields = @('revision','kind','order','compared','all_equal','requested_order_finished','performance_conclusion_allowed','files_written')
                    if ($comparison.all_equal) { $fields += @('diagnostic_pair_only','stable_speedup_claim_allowed','ui_drawing_included') }
                    Assert-TimingFields $row $fields
                    if ($row.order -isnot [string] -or $row.order -cne 'FO' -or $ExitCode -ne $(if ($comparison.all_equal) { 0 } else { 3 })) { throw $invalid }
                    Assert-TimingFlag $row.compared $true
                    Assert-TimingFlag $row.all_equal $comparison.all_equal
                    Assert-TimingFlag $row.requested_order_finished $true
                    Assert-TimingFlag $row.performance_conclusion_allowed $false
                    Assert-TimingFlag $row.files_written $false
                    if ($comparison.all_equal) {
                        Assert-TimingFlag $row.diagnostic_pair_only $true
                        Assert-TimingFlag $row.stable_speedup_claim_allowed $false
                        Assert-TimingFlag $row.ui_drawing_included $true
                    }
                    $terminal = [ordered]@{status=$(if ($comparison.all_equal) {'comparable'} else {'not_comparable'})
                        comparable=[bool]$comparison.all_equal;cleanup_confirmed=$true;code=$null}
                }
                'failed' {
                    Assert-TimingFields $row @('revision','kind','code','stage','cleanup_confirmed','further_runs_started','performance_conclusion_allowed')
                    if ($row.revision -cne 'personal-ntfs-timing-v2' -or $ExitCode -ne 1 -or $null -ne $comparison -or
                        $row.code -isnot [string] -or $row.code -cnotin $failureCodes -or $row.stage -isnot [string] -or
                        $row.stage -cnotin (@('Preparing','UiHandoff','FingerprintOutsideTiming','Ordinary_Start')+
                            $bridgeStages+$fastStages+$ordinaryStages+@('Snapshot','Discovering','Reading','Supplementing','Building','Cleanup'))) { throw $invalid }
                    if ($row.cleanup_confirmed -isnot [bool]) { throw $invalid }
                    Assert-TimingFlag $row.further_runs_started $false
                    Assert-TimingFlag $row.performance_conclusion_allowed $false
                    # A generic failure cannot prove cleanup of an unfinished
                    # fast scan. A validated fast sample attests that its
                    # already-cleaned snapshot precedes any ordinary failure.
                    $terminal = [ordered]@{status='failed';comparable=$false;cleanup_confirmed=($null -ne $fast);code=$row.code}
                }
                default { throw $invalid }
            }
        }
        if ($null -eq $terminal) { throw $invalid }
        return [ordered]@{status=$terminal.status;comparable=$terminal.comparable
            cleanup_confirmed=$terminal.cleanup_confirmed;code=$terminal.code
            fast_ms=$(if ($null -ne $fast) {$fast.total_ms})
            ordinary_ms=$(if ($null -ne $ordinary) {$ordinary.total_ms})
            fast=$fast;ordinary=$ordinary}
    } catch { throw $invalid }
}
