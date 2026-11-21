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
        $report.mapping_audit.dropped -eq 0 -and $report.rejected_stages -eq 0 -and
        $report.wrong_thread_calls -eq 0) 'IncompleteObservation'
    $mapping = @($report.mapping_audit.mapping)
    $root = @($report.mapping_audit.root_witness)
    $mode = $report.records[-1].Mode
    Require ($mode -in @('All','Combo','Grid')) 'UnexpectedMode'
    foreach ($entry in $mapping) {
        Require ($entry.failure -eq 'None' -and $entry.target_owned -and $entry.target_control_kind -eq 'Combo' -and
            $entry.printClient -and $entry.onPrint -and $entry.comboMessage) 'MappedPrintNotSuccessful'
        Require ($entry.originsValid -and $entry.window_x -eq 0 -and $entry.window_y -eq 0 -and
            $entry.map_mode -eq 1 -and $entry.dc_kind -eq 3) 'UnexpectedTargetMap'
        Require ($entry.viewport_x -eq -$entry.target_control_bounds.X -and
            $entry.viewport_y -eq -$entry.target_control_bounds.Y) 'OffsetNotChildTranslation'
        Require ($entry.clip_kind -in @(2,3) -and $entry.clip.X -ge $entry.target_control_bounds.X -and
            $entry.clip.Y -ge $entry.target_control_bounds.Y -and
            $entry.clip.Right -le $entry.target_control_bounds.Right -and
            $entry.clip.Bottom -le $entry.target_control_bounds.Bottom) 'ClipOutsideCombo'
    }
    Require (@($root | Where-Object { $_.phase -eq 'PaintCopied' -and $_.printClient }).Count -eq 0) 'PrintMisreportedAsRoot'
    $post = @($root | Where-Object phase -eq 'AfterChildren')
    Require ($post.Count -eq 11 -and @($post.geometry | Select-Object -Unique).Count -eq 11) 'MissingGeometryWitness'
    Require (@($post | Where-Object { -not $_.valid -or $_.owner_kind -ne 'Root' -or -not $_.matches }).Count -eq 0) 'ExposedRootNotRestored'
    $restored = 0
    foreach ($point in $post) {
        $same = @($root | Where-Object { $_.geometry -eq $point.geometry })
        $before = @($same | Where-Object phase -eq 'BeforeChildren')
        $moved = @($same | Where-Object phase -eq 'AfterGridBounds')
        Require ($before.Count -eq 1) 'MissingBeforeChildren'
        if ($before[0].owner_kind -ne 'Grid') { continue }
        Require ($moved.Count -eq 1 -and -not $before[0].valid -and $null -ne $before[0].childPixel -and
            $before[0].childPixel -ne 4294967295) 'MissingOldGridWitness'
        Require ($moved[0].valid -and $moved[0].owner_kind -eq 'Root' -and
            $moved[0].pixel -eq $before[0].childPixel -and -not $moved[0].matches) 'GridMoveNotObserved'
        Require ($point.pixel -eq $point.expected -and $point.expected -eq $moved[0].expected -and
            $point.pixel -ne $moved[0].pixel) 'NoRepairBetweenMoveAndGeometryEnd'
        Require (@($same | Where-Object { $_.phase -eq 'PaintCopied' -and $_.sequence -gt $moved[0].sequence -and
            $_.sequence -lt $point.sequence }).Count -eq 0) 'NaturalPaintMaskedRepair'
        $restored++
    }
    if ($mode -eq 'Combo') { Require ($mapping.Count -eq 26 -and $restored -eq 0) 'ComboControlMismatch' }
    elseif ($mode -eq 'Grid') { Require ($mapping.Count -eq 0 -and $restored -eq 6) 'GridControlMismatch' }
    else { Require ($mapping.Count -eq 26 -and $restored -eq 6) 'AllControlMismatch' }
    $groups = @($mapping | Group-Object viewport_x,viewport_y)
    if ($mapping.Count -gt 0) { Require ($groups.Count -eq 2 -and @($groups | Where-Object Count -ne 13).Count -eq 0) 'UnexpectedComboCounts' }
    $geometryPixels = @($report.records | Where-Object Stage -eq 'GeometryAfterChildren' |
        ForEach-Object { $_.Pixels } | Where-Object Location -eq 'RootMinusChildren')
    Require ($geometryPixels.Count -eq 44 -and @($geometryPixels | Where-Object {
        -not $_.Valid -or $_.Black -or -not $_.MatchesFrame }).Count -eq 0) 'OriginalRootSampleCoverageFailed'
    [ordered]@{
        trace = Split-Path $path -Leaf; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        mode = $mode; dpi = $root[0].dpi; mapped_combo_copies = $mapping.Count; mapping_rejections = 0
        post_geometry_witnesses = $post.Count; mismatches = 0; old_grid_witnesses_repaired_before_paint = $restored
        original_geometry_root_samples = $geometryPixels.Count; root_matches = $geometryPixels.Count
        print_as_root_presentations = 0; observation_failures = 0; truncated = $false
    }
})
[ordered]@{ revision=1; scope='C50-owned-synthetic-GDI-mapping-and-vacated-region-repair';
    normal_app_modified=$false; default_enabled=$false; screen_no_flash_proven=$false;
    runs=$summaries } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output ('PASS mapping repair evidence: reports={0}; mapped_copies={1}; repaired_grid_witnesses={2}' -f
    $summaries.Count, ($summaries.mapped_combo_copies | Measure-Object -Sum).Sum,
    ($summaries.old_grid_witnesses_repaired_before_paint | Measure-Object -Sum).Sum)
