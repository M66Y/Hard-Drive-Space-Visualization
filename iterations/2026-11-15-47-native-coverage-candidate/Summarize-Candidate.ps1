param([Parameter(Mandatory=$true)][string]$TraceDirectory,[Parameter(Mandatory=$true)][string]$OutputFile)
$ErrorActionPreference='Stop'
$observedLabels=@{
 'resize-5432f7618e1b4370adff7c5eaab2fa02.json'='canvas-initial-background-only'
 'resize-bac2d272004145f78d964fc76dbc8fa0.json'='canvas-baseline'
 'mainform-f45166cf1a114c0986d134c67447126c.json'='mainform-observe-baseline'
 'mainform-4173b3e000784006a72c010572481591.json'='mainform-background-and-panel-candidate'
 'resize-b5701a4bcabd4946819ec9a7e5a5c9d9.json'='all-initial-background-only'
 'resize-0fb249f027f54ff08652560b82d40e8b.json'='rejected-prepared-scene-erase-dc-clipped'
 'resize-234f73ce0cd348d4a94e7f5306a9ae4f.json'='canvas-prepared-full-root-dc'
 'mainform-714d634d04b644dfbd6705a9a9180d36.json'='mainform-title-audit-v1-pixels'
 'mainform-2d89c5c298e14596b589e55f7654cd0a.json'='mainform-title-audit-v1-compact'
 'resize-dacd85a1ff4848339760c634acad8c3d.json'='all-prepared-full-root-dc'
}
$rows=foreach($file in Get-ChildItem -LiteralPath $TraceDirectory -Filter '*.json' -File|Where-Object {$observedLabels.ContainsKey($_.Name)}|Sort-Object LastWriteTime) {
 $j=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json
 $preStage=if($file.Name.StartsWith('mainform-')){'NativeSizeReturned'}else{'GeometryAfterChildren'}
 $pre=@($j.records|Where-Object Stage -eq $preStage|ForEach-Object {$_.Pixels}|Where-Object {$_.Valid -and $_.Location -eq 'RootMinusChildren'})
 $all=@($j.records|ForEach-Object {$_.Pixels}|Where-Object Valid)
 [ordered]@{label=$observedLabels[$file.Name];sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash;
  strategy=$j.coverage_strategy;pixels_enabled=$j.pixels_enabled;records=$j.records_count;truncated=$j.truncated;failures=$j.observation_failures;
  pre_stage=$preStage;valid_pre_root=$pre.Count;black_pre_root=@($pre|Where-Object Black).Count;
  pre_root_matches_frame=@($pre|Where-Object {$_.MatchesFrame -eq $true}).Count;
  valid_all=$all.Count;black_all=@($all|Where-Object Black).Count}
}
[ordered]@{revision=1;iteration='C47';user_review='failed-partial-improvement';ordinary_application_integrated=$false;
 evidence_boundary='owned-GDI-samples-not-continuous-DWM-frames; no-zero-black-acceptance-inference';
 runs=@($rows)} | ConvertTo-Json -Depth 6|Set-Content -LiteralPath $OutputFile -Encoding UTF8
Write-Output ('SUMMARY runs={0}; user_review=failed-partial-improvement' -f @($rows).Count)
