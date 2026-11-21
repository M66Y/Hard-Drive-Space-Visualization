param(
    [Parameter(Mandatory=$true)][string]$TraceDirectory,
    [Parameter(Mandatory=$true)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
# Fixed owned-window captures from this investigation. Never collect new data.
$runs = @(
    @('canvas30-timing', 'resize-40be459e61134fcd99509b14c16f8bcd.json', 'replay-c6170beedc724a67882fefa3f17e0669.json'),
    @('canvas30-pixels', 'resize-c3565cd57f52443584497010f1a92433.json', 'replay-f1d045c1b0db41e6943980561e597081.json'),
    @('mainform30-truncated', 'mainform-a11f32e165d54588989b58faab040149.json', 'replay-bbfd41e999d3434aa1d0148bbc873d50.json'),
    @('mainform30-alternating', 'mainform-fcf72b9358e04301a49e5e24731e146a.json', 'replay-f262401e350f4c848eeeed4c0d80920f.json'),
    @('canvas100-alternating', 'resize-8d4eefa9c76e4b40b481ac3adf729ae1.json', 'replay-11f893576f9d41dbbfdaeb33c617b394.json'),
    @('mainform100-alternating', 'mainform-1440a33208de418a80b47143fd8877d9.json', 'replay-8ea2131618e841c889277f8c1b5f061c.json')
)
function Quantiles($values) {
    $a = @($values | Sort-Object)
    if ($a.Count -eq 0) { return $null }
    [ordered]@{ count=$a.Count; p50_ms=[math]::Round($a[[int][math]::Floor($a.Count/2)],3); p95_ms=[math]::Round($a[[int][math]::Floor(($a.Count-1)*.95)],3); max_ms=[math]::Round($a[-1],3) }
}
$results = foreach($run in $runs) {
    $path=Join-Path $TraceDirectory $run[1]
    $j=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $replay=Get-Content -LiteralPath (Join-Path $TraceDirectory $run[2]) -Raw | ConvertFrom-Json
    $freq=[double]$j.timestamp_frequency
    $records=@($j.records)
    $span=($records[-1].Timestamp-$records[0].Timestamp)*1000/$freq
    $black=@(); $post=@(); $brackets=@(); $outsideOld=0; $paired=0; $matched=0
    foreach($rec in $records) {
        foreach($p in $rec.Pixels) {
            if($p.Valid -and $p.Black) { $black+=@{record=$rec;point=$p} }
            if($p.Valid -and ($rec.Stage -eq 'FrameCopied' -or $rec.Stage -eq 'RootPaintReturned')) { $post+=@{record=$rec;point=$p} }
        }
    }
    foreach($item in $black) {
        $r=$item.record; $p=$item.point
        if($r.PresentedLayout) { $old=$r.PresentedLayout } else { $old=$r.ManagedRootClient }
        if($p.X -ge $old.Width -or $p.Y -ge $old.Height) { $outsideOld++ }
        $found=$post | Where-Object { $_.record.Geometry -eq $r.Geometry -and $_.record.Sequence -gt $r.Sequence -and $_.point.X -eq $p.X -and $_.point.Y -eq $p.Y } | Select-Object -First 1
        if($found -and -not $found.point.Black) {
            $paired++; if($found.point.MatchesFrame -eq $true) { $matched++ }
            $brackets+=($found.record.Timestamp-$r.Timestamp)*1000/$freq
        }
    }
    $messages=@{}; $stack=New-Object System.Collections.Generic.List[object]
    foreach($r in $records) {
        if($r.Stage -eq 'MessageEnter') { $stack.Add(@{r=$r; child=0.0}); continue }
        if($r.Stage -ne 'MessageExit' -or $stack.Count -eq 0) { continue }
        $top=$stack[$stack.Count-1]
        if($top.r.SurfaceId -ne $r.SurfaceId -or $top.r.Message -ne $r.Message) { continue }
        $stack.RemoveAt($stack.Count-1)
        $duration=($r.Timestamp-$top.r.Timestamp)*1000/$freq
        if($stack.Count -gt 0) { $stack[$stack.Count-1].child+=$duration }
        $key=('{0}:{1}:{2}' -f $r.Surface,$r.SurfaceId,$r.Message)
        if(-not $messages.ContainsKey($key)) { $messages[$key]=@{count=0;inclusive=0.0;exclusive=0.0} }
        $messages[$key].count++; $messages[$key].inclusive+=$duration; $messages[$key].exclusive+=[math]::Max(0,$duration-$top.child)
    }
    $hot=@($messages.GetEnumerator() | Sort-Object {$_.Value.exclusive} -Descending | Select-Object -First 6 | ForEach-Object {
        [ordered]@{surface_message=$_.Key; count=$_.Value.count; inclusive_ms=[math]::Round($_.Value.inclusive,3); exclusive_tracked_ms=[math]::Round($_.Value.exclusive,3)}
    })
    [ordered]@{
        label=$run[0]; trace_sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        schema=$j.schema; pixels_enabled=$j.pixels_enabled; records=$records.Count; truncated=$j.truncated; observation_failures=$j.observation_failures
        span_ms=[math]::Round($span,3); diagnostic_ms=[math]::Round($j.diagnostic_ticks*1000/$freq,3)
        diagnostic_fraction=[math]::Round($j.diagnostic_ticks*1000/$freq/$span,5)
        requested_interval_ms=$replay.requested_interval_ms; replay_steps=$replay.requested_steps
        actual_gap_p50_ms=[math]::Round($replay.actual_gap_p50_ms,3); actual_gap_p95_ms=[math]::Round($replay.actual_gap_p95_ms,3)
        valid_black_points=$black.Count; black_outside_old_layout=$outsideOld; paired_later_nonblack=$paired; paired_matches_prototype_frame=$matched
        black_stages=@($black|ForEach-Object {$_.record.Stage}|Group-Object|ForEach-Object {@{stage=$_.Name;count=$_.Count}})
        pre_to_post_sample_bracket=Quantiles $brackets
        largest_tracked_message_exclusive=$hot
    }
}
$summary=[ordered]@{
    revision=1; investigation='C46'; production_drawing_changed=$false; capture='owned-HWND-GDI-surface-only'
    native_mouse_drag=$false; system_settings_changed=$false; private_directory_scan=$false
    timing_boundary='requested-cadence-is-not-actual-FPS; pixel-brackets-are-not-continuous-black-duration'
    runs=@($results)
}
$summary | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $OutputFile -Encoding UTF8
Write-Output ('SUMMARY: runs={0}; output_written=True' -f $results.Count)
