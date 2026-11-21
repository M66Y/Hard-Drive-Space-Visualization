param(
    [Parameter(Mandatory=$true)][string[]]$TracePath,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
function Require([bool]$condition, [string]$code) {
    if (-not $condition) { throw $code }
}
$summaries = @(foreach ($path in $TracePath) {
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    Require ($report.schema -eq 'resize-diagnostic-trace-v4-mapping-audit') 'WrongAuditSchema'
    Require ($report.mapping_audit.enabled -and $report.pixels_enabled -and $report.native_dib_frame) 'AuditNotEnabled'
    Require (-not $report.truncated -and -not $report.hooks_limited -and $report.observation_failures -eq 0 -and
        $report.mapping_audit.dropped -eq 0 -and $report.rejected_stages -eq 0) 'IncompleteObservation'
    $mapping = @($report.mapping_audit.mapping)
    $root = @($report.mapping_audit.root_witness)
    $mode = $report.records[-1].Mode
    Require ($mode -in @('All','Combo','Grid')) 'UnexpectedMode'
    foreach ($entry in $mapping) {
        Require ($entry.failure -eq 'TargetOrigin' -and $entry.target_owned -and
            $entry.target_control_kind -eq 'Combo' -and $entry.printClient -and $entry.onPrint -and $entry.comboMessage) 'FallbackNotComboPrintClient'
        Require ($entry.originsValid -and $entry.window_x -eq 0 -and $entry.window_y -eq 0 -and
            $entry.map_mode -eq 1 -and $entry.dc_kind -eq 3) 'UnexpectedTargetMap'
        Require ($entry.viewport_x -eq -$entry.target_control_bounds.X -and
            $entry.viewport_y -eq -$entry.target_control_bounds.Y) 'OffsetNotChildTranslation'
        Require ($entry.clip_kind -in @(2,3) -and $entry.clip.X -ge $entry.target_control_bounds.X -and
            $entry.clip.Y -ge $entry.target_control_bounds.Y -and
            $entry.clip.Right -le $entry.target_control_bounds.Right -and
            $entry.clip.Bottom -le $entry.target_control_bounds.Bottom) 'ClipOutsideCombo'
    }
    $wrong = @($root | Where-Object { $_.phase -eq 'AfterChildren' -and $_.valid -and
        $_.owner_kind -eq 'Root' -and -not $_.matches })
    $explained = 0
    foreach ($point in $wrong) {
        $same = @($root | Where-Object { $_.geometry -eq $point.geometry })
        $before = @($same | Where-Object phase -eq 'BeforeChildren')
        $afterGrid = @($same | Where-Object phase -eq 'AfterGridBounds')
        $later = @($same | Where-Object { $_.phase -eq 'PaintCopied' -and -not $_.printClient -and $_.valid -and $_.owner_kind -eq 'Root' })
        Require ($before.Count -eq 1 -and $afterGrid.Count -eq 1 -and $later.Count -gt 0) 'MissingWitnessPhase'
        Require ($before[0].owner_kind -eq 'Grid' -and -not $before[0].valid -and
            $null -ne $before[0].childPixel -and $before[0].childPixel -ne 4294967295) 'MissingOldGridPixel'
        Require ($point.pixel -eq $before[0].childPixel -and $point.pixel -ne $point.expected -and
            $point.expected -eq $before[0].expected) 'NotOldGridResidual'
        Require ($afterGrid[0].valid -and $afterGrid[0].owner_kind -eq 'Root' -and
            $afterGrid[0].pixel -eq $point.pixel -and -not $afterGrid[0].matches) 'NoGridMoveTransition'
        Require ($before[0].grid_bounds[0].X -le $point.x -and $before[0].grid_bounds[0].Right -gt $point.x -and
            $before[0].grid_bounds[0].Y -le $point.y -and $before[0].grid_bounds[0].Bottom -gt $point.y -and
            $afterGrid[0].grid_bounds[0].Right -le $point.x) 'GridCoverageNotMovedOffWitness'
        Require ($later[0].matches -and $later[0].expected -eq $point.expected) 'NaturalRootPaintNotRestored'
        $explained++
    }
    if ($mode -eq 'Combo') { Require ($wrong.Count -eq 0 -and $mapping.Count -eq 26) 'ComboControlMismatch' }
    elseif ($mode -eq 'Grid') { Require ($wrong.Count -eq 6 -and $mapping.Count -eq 0) 'GridControlMismatch' }
    else { Require ($wrong.Count -eq 6 -and $mapping.Count -eq 26) 'AllMismatchNotReproduced' }
    $mappingGroups = @($mapping | Group-Object viewport_x,viewport_y)
    if ($mapping.Count -gt 0) { Require ($mappingGroups.Count -eq 2 -and @($mappingGroups | Where-Object Count -ne 13).Count -eq 0) 'UnexpectedComboCounts' }
    [ordered]@{
        trace = Split-Path $path -Leaf; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        mode = $mode; dpi = $root[0].dpi
        mapping_rejections = $mapping.Count; combo_printclient_attributed = $mapping.Count
        child_offset_and_clip_verified = $true; geometry_root_mismatches = $wrong.Count
        old_grid_residual_explained = $explained; later_natural_root_paint_restored = $explained
        truncated = $false; observation_failures = 0
    }
})
[ordered]@{ revision=1; scope='C49-owned-synthetic-GDI-root-witness-and-target-mapping';
    diagnosis_only=$true; pixel_guard_relaxed=$false; normal_app_modified=$false;
    screen_no_flash_proven=$false; runs=$summaries } | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output ('PASS mapping evidence: reports={0}; mapping_rejections={1}; old_grid_residuals={2}' -f
    $summaries.Count, ($summaries.mapping_rejections | Measure-Object -Sum).Sum,
    ($summaries.old_grid_residual_explained | Measure-Object -Sum).Sum)
