#requires -Version 7.4
# Pure invented JSON-shaped values. No VSS, UAC, native volume or filesystem scan.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$checks=0
function Check([bool]$Condition,[string]$Name) {
    if (-not $Condition) { throw ('FAIL '+$Name) }
    $script:checks++;Write-Output ('PASS NTFS VSS scale evidence '+$Name)
}
$tokens=$null;$errors=$null
$source=Join-Path $PSScriptRoot '../scripts/ntfs-prototype/VssScaleEvidence.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $source).Path,[ref]$tokens,[ref]$errors)
Check ($errors.Count -eq 0) 'converter syntax'
$functions=@($ast.EndBlock.Statements|Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] })
Check ($functions.Count -eq 2 -and $ast.EndBlock.Statements.Count -eq 2 -and
    ($functions.Name -join ',') -ceq 'ConvertTo-NtfsVssScaleEvidence,ConvertTo-NtfsVssScaleEnvelope') 'only two pure converters are defined'
foreach ($function in $functions) { . ([scriptblock]::Create($function.Extent.Text)) }
$guardSource=Join-Path $PSScriptRoot '../scripts/ntfs-prototype/KernelMatrixEvidence.ps1'
$guardAst=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $guardSource).Path,[ref]$tokens,[ref]$errors)
$guard=@($guardAst.EndBlock.Statements|Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -ceq 'Assert-UniqueJsonKeys' })
Check ($errors.Count -eq 0 -and $guard.Count -eq 1) 'existing duplicate JSON guard reused'
. ([scriptblock]::Create($guard[0].Extent.Text))
function Report([int]$Files=10000) {
    $samples=@();$order=@('RawMft','Directory','Directory','RawMft','RawMft','Directory')
    for ($i=0;$i -lt 6;$i++) {
        $raw=$order[$i] -ceq 'RawMft'
        $phases=[pscustomobject]@{OwnerMilliseconds=10.0;NamespaceMilliseconds=0.0;RawReadMilliseconds=0.0;ParseMilliseconds=0.0;
            IndexMilliseconds=0.0;NativeMilliseconds=0.0;ProjectionMilliseconds=0.0;CleanupMilliseconds=2.0;BackendMilliseconds=20.0;DirectoryMilliseconds=0.0}
        if ($raw) { $phases.NamespaceMilliseconds=5.0;$phases.RawReadMilliseconds=2.0;$phases.ParseMilliseconds=3.0;
            $phases.IndexMilliseconds=1.0;$phases.NativeMilliseconds=5.0;$phases.ProjectionMilliseconds=1.0 }
        else { $phases.DirectoryMilliseconds=20.0 }
        $samples+= [pscustomobject]@{Round=[int][Math]::Floor($i/2)+1;Algorithm=$order[$i];Milliseconds=40.0;CpuMilliseconds=20.0;
            ManagedAllocatedBytes=[long]1000;RawReadCalls=$(if($raw){8}else{0});RawRequestedBytes=$(if($raw){[long]65536}else{[long]0});
            NativeFallbackObjects=$(if($raw){[int]($Files/10)}else{0});ScopeDecodedRecords=$(if($raw){$Files+101}else{0});
            NamespaceEntries=$(if($raw){$Files+111}else{0});NamespaceComplete=$raw;Matched=$true;Closed=$true;
            SampledPeakWorkingSetBytes=[long]1048576;WorkingSetSamples=2;Phases=$phases;NativeSupplementCalls=$(if($raw){[int]($Files/10)}else{0})}
    }
    return [pscustomobject]@{Revision='vss-scale-benchmark-v1';Workload=[pscustomobject]@{Files=$Files;Directories=100;HardlinkAliases=10;
        ResidentFiles=[int]($Files/10);NonresidentFiles=[int](8*$Files/10);EmptyFiles=[int]($Files/10)};
        PreparationMilliseconds=5.0;Samples=$samples;Matched=$true;WorkingSetSampleIntervalMilliseconds=20;
        ResultCounts=[pscustomobject]@{FileObjects=$Files;FileEntries=$Files+10;Directories=100;
            LogicalBytes=[long]($Files/10*32+8*$Files/10*1024+10240);AllocatedBytes=[long]40960000;UnknownAllocationFiles=0};
        SnapshotSessions=6;RawSessions=3;TotalRawReadCalls=24;TotalRawRequestedBytes=[long]196608}
}
function Envelope([bool]$Failure=$false,[bool]$Cleaned=$true) {
    if ($Failure) { return [pscustomobject]@{schema=1;status='failed';stage='RawRead';checks=0;failures=1;code='VssScaleSourceFailed';
        native_error=-2147024891;cleanup_confirmed=$Cleaned;vss_scale=$null} }
    return [pscustomobject]@{schema=1;status='vss_scale_benchmarked';stage='Complete';checks=6;failures=0;code='None';native_error=0;
        cleanup_confirmed=$true;vss_scale=(Report)}
}
function RejectReport([scriptblock]$Mutation,[string]$Name) {
    $value=Report;& $Mutation $value;$message=$null
    try { ConvertTo-NtfsVssScaleEvidence $value 10000|Out-Null } catch { $message=$_.Exception.Message }
    Check ($message -ceq 'VSS_SCALE_EVIDENCE_INVALID') $Name
}
function RejectEnvelope([scriptblock]$Mutation,[string]$Name,[bool]$Failure=$false) {
    $value=Envelope $Failure;& $Mutation $value;$message=$null;$cleanupGate=$false
    try { $safe=ConvertTo-NtfsVssScaleEnvelope $value 10000;$cleanupGate=$safe.cleanup_confirmed } catch { $message=$_.Exception.Message }
    Check ($message -ceq 'VSS_SCALE_ENVELOPE_INVALID' -and -not $cleanupGate) $Name
}
foreach ($files in @(10000,100000)) {
    $safe=ConvertTo-NtfsVssScaleEvidence (Report $files) $files
    Check ($safe.SnapshotSessions -eq 6 -and $safe.RawSessions -eq 3 -and $safe.ResultCounts.FileObjects -eq $files) 'both fixed sizes require six independent snapshot sessions'
    Check (($safe.Samples.Algorithm -join ',') -ceq 'RawMft,Directory,Directory,RawMft,RawMft,Directory') 'three alternating independent snapshot pairs are canonical'
    Check ($safe.TotalRawReadCalls -eq 24 -and $safe.TotalRawRequestedBytes -eq 196608) 'three raw session resources are separately summed'
}
$value=Envelope;$safe=ConvertTo-NtfsVssScaleEnvelope $value 10000
Check (($safe.Keys -join ',') -ceq 'schema,status,stage,checks,failures,code,native_error,cleanup_confirmed,vss_scale') 'canonical complete cleanup envelope'
Check ($safe.cleanup_confirmed -and $safe.status -ceq 'vss_scale_benchmarked' -and $safe.vss_scale.Samples.Closed -notcontains $false) 'benchmark success requires every sample closed and top cleanup true'
$value.vss_scale.Samples[0].Phases.RawReadMilliseconds=12;$value.vss_scale.Workload.Files=1
Check ($safe.vss_scale.Samples[0].Phases.RawReadMilliseconds -eq 2 -and $safe.vss_scale.Workload.Files -eq 10000) 'accepted nested evidence never aliases input'
$json=$safe|ConvertTo-Json -Depth 15;Assert-UniqueJsonKeys $json
Check ((ConvertTo-NtfsVssScaleEnvelope ($json|ConvertFrom-Json) 10000).cleanup_confirmed) 'full JSON envelope roundtrip preserves explicit cleanup gate'
Check ($json -cnotmatch '"(?:Path|SnapshotId|SnapshotSetId|DeviceObject|Volume|SourceToken|Reference|RawRecord|Payload|Name|Epoch|Hash)"\s*:') 'safe evidence contains no source addresses identities names or raw data'
foreach ($files in @(0,9999,10001,100001)) {
    $caught=$false;try { ConvertTo-NtfsVssScaleEvidence (Report) $files|Out-Null } catch { $caught=$_.Exception.Message -ceq 'VSS_SCALE_EVIDENCE_INVALID' }
    Check $caught 'unknown workload cannot reuse a benchmark schema'
}
foreach ($field in @((Report).PSObject.Properties.Name)) { RejectReport {param($v)$v.PSObject.Properties.Remove($field)} 'missing report field rejected' }
foreach ($field in @((Envelope).PSObject.Properties.Name)) { RejectEnvelope {param($v)$v.PSObject.Properties.Remove($field)} 'missing envelope field blocks cleanup' }
foreach ($level in @('Workload','ResultCounts')) {
    foreach ($field in @((Report).$level.PSObject.Properties.Name)) {
        RejectReport {param($v)$v.$level.PSObject.Properties.Remove($field)} 'missing count field rejected'
        foreach ($bad in @(-1,[long]::MaxValue,'1',$true,[double]1,[decimal]1,$null)) { RejectReport {param($v)$v.$level.$field=$bad} 'count integer type or range rejected' }
    }
}
foreach ($field in @('SnapshotSessions','RawSessions','TotalRawReadCalls','TotalRawRequestedBytes','WorkingSetSampleIntervalMilliseconds')) {
    foreach ($bad in @(-1,[long]::MaxValue,'1',$true,[double]1,[decimal]1,$null)) { RejectReport {param($v)$v.$field=$bad} 'report resource integer type or range rejected' }
}
foreach ($bad in @('ntfs-scale-benchmark-v1','fixture-vss-v1','vss-scale-benchmark-v2','VSS-scale-benchmark-v1','SyntheticFixture','',1,$null)) { RejectReport {param($v)$v.Revision=$bad} 'old simulated or foreign report cannot borrow success' }
foreach ($field in @('Matched')) { foreach ($bad in @($false,'true',1,$null)) { RejectReport {param($v)$v.$field=$bad} 'report truth flags are real true booleans' } }
foreach ($bad in @(-1,900001,[double]::NaN,[double]::PositiveInfinity,[double]::NegativeInfinity,'1',$true,[decimal]1,$null)) {
    RejectReport {param($v)$v.PreparationMilliseconds=$bad} 'preparation elapsed is finite nonnegative bounded time'
}
foreach ($bad in @($null,@(),@((Report).Samples[0]),[pscustomobject]@{},'SyntheticPrivateMarker')) { RejectReport {param($v)$v.Samples=$bad} 'exact six sample array required' }
RejectReport {param($v)$v.Samples+= $v.Samples[0]} 'extra seventh sample rejected'
for ($index=0;$index -lt 6;$index++) {
    foreach ($field in @((Report).Samples[$index].PSObject.Properties.Name)) { RejectReport {param($v)$v.Samples[$index].PSObject.Properties.Remove($field)} 'missing sample field rejected' }
    foreach ($field in @('Matched','Closed')) { foreach ($bad in @($false,'true',1,$null)) { RejectReport {param($v)$v.Samples[$index].$field=$bad} 'every sample match and closure is explicit true' } }
    foreach ($bad in @(0,4,'1',[double]1,$null)) { RejectReport {param($v)$v.Samples[$index].Round=$bad} 'sample pair round identity exact' }
    foreach ($bad in @('RawMft ','rawmft','Directory ','ntfs-scale','',1,$null)) { RejectReport {param($v)$v.Samples[$index].Algorithm=$bad} 'sample backend identity exact' }
}
foreach ($field in @('Milliseconds','CpuMilliseconds')) {
    foreach ($bad in @(-1,900001,[double]::NaN,[double]::PositiveInfinity,[double]::NegativeInfinity,'1',$true,[decimal]1,$null)) {
        RejectReport {param($v)$v.Samples[0].$field=$bad} 'sample time finite and bounded'
    }
}
RejectReport {param($v)$v.Samples[0].Milliseconds=0} 'whole owner backend cleanup wall is positive'
foreach ($field in @('ManagedAllocatedBytes','RawReadCalls','RawRequestedBytes','NativeFallbackObjects','ScopeDecodedRecords','NamespaceEntries','SampledPeakWorkingSetBytes','WorkingSetSamples','NativeSupplementCalls')) {
    foreach ($bad in @(-1,'1',$true,[double]1,[decimal]1,$null)) { RejectReport {param($v)$v.Samples[0].$field=$bad} 'sample resource type and sign exact' }
}
foreach ($pair in @(@('RawReadCalls',0),@('RawReadCalls',8193),@('RawRequestedBytes',512),@('RawRequestedBytes',268501504L),@('RawRequestedBytes',65537),
    @('NativeFallbackObjects',10102),@('ScopeDecodedRecords',10102),@('NamespaceEntries',10110),@('NamespaceEntries',10112),
    @('SampledPeakWorkingSetBytes',0),@('SampledPeakWorkingSetBytes',8589934593L),@('WorkingSetSamples',0),@('WorkingSetSamples',45003))) {
    RejectReport {param($v)$v.Samples[0].($pair[0])=$pair[1]} 'sample resource hard boundaries enforced'
}
RejectReport {param($v)$v.Samples[0].RawReadCalls=200;$v.Samples[0].RawRequestedBytes=65536} 'raw calls require at least one sector per request'
RejectReport {param($v)$v.Samples[0].RawReadCalls=1;$v.Samples[0].RawRequestedBytes=66048} 'one raw request cannot exceed its bounded block'
RejectReport {param($v)$v.Samples[0].NativeSupplementCalls++} 'native supplements counted once per fallback object'
RejectReport {param($v)$v.TotalRawReadCalls++} 'aggregate raw call count must equal exactly three sessions'
RejectReport {param($v)$v.TotalRawRequestedBytes+=512} 'aggregate raw bytes must equal exactly three sessions'
RejectReport {param($v)$v.Samples[0].NamespaceComplete=$false} 'raw selected scope requires complete namespace authority'
RejectReport {param($v)$v.Samples[1].NamespaceComplete=$true} 'directory sample does not borrow raw namespace proof'
foreach ($index in @(1,2,5)) {
    foreach ($field in @('RawReadCalls','RawRequestedBytes','NativeFallbackObjects','ScopeDecodedRecords','NamespaceEntries','NativeSupplementCalls')) {
        RejectReport {param($v)$v.Samples[$index].$field=1} 'directory sample reports no raw or supplemental work'
    }
}
$phaseNames=@((Report).Samples[0].Phases.PSObject.Properties.Name)
foreach ($field in $phaseNames) {
    RejectReport {param($v)$v.Samples[0].Phases.PSObject.Properties.Remove($field)} 'missing phase rejected'
    foreach ($bad in @(-1,900001,[double]::NaN,[double]::PositiveInfinity,'1',$true,[decimal]1,$null)) {
        RejectReport {param($v)$v.Samples[0].Phases.$field=$bad} 'phase elapsed finite nonnegative bounded time'
    }
}
RejectReport {param($v)$v.Samples[0].Phases.OwnerMilliseconds=30} 'owner backend cleanup cannot exceed measured whole wall'
foreach ($field in @('NamespaceMilliseconds','RawReadMilliseconds','ParseMilliseconds','IndexMilliseconds','NativeMilliseconds','ProjectionMilliseconds')) {
    RejectReport {param($v)$v.Samples[0].Phases.$field=22} 'nested raw component cannot exceed backend wall'
    RejectReport {param($v)$v.Samples[1].Phases.$field=1} 'directory sample has no raw component phase'
}
RejectReport {param($v)$v.Samples[0].Phases.DirectoryMilliseconds=1} 'raw sample cannot claim a directory backend timing'
RejectReport {param($v)$v.Samples[1].Phases.DirectoryMilliseconds=22} 'directory timing stays within backend wall'
$value=Report
foreach ($field in @('NamespaceMilliseconds','RawReadMilliseconds','ParseMilliseconds','IndexMilliseconds','NativeMilliseconds','ProjectionMilliseconds')) { $value.Samples[0].Phases.$field=20.0 }
Check ((ConvertTo-NtfsVssScaleEvidence $value 10000).Samples[0].Phases.BackendMilliseconds -eq 20) 'nested phases are not incorrectly added to the whole wall'
foreach ($fallback in @(0,10101)) {
    $value=Report;$value.Samples[0].NativeFallbackObjects=$fallback;$value.Samples[0].NativeSupplementCalls=$fallback
    Check ((ConvertTo-NtfsVssScaleEvidence $value 10000).Samples[0].NativeFallbackObjects -eq $fallback) 'supplements preserve actual provenance cost without guessing resident allocation'
}
foreach ($allocated in @(0L,1073741824L)) {
    $value=Report;$value.ResultCounts.AllocatedBytes=$allocated
    Check ((ConvertTo-NtfsVssScaleEvidence $value 10000).ResultCounts.AllocatedBytes -eq $allocated) 'space totals are measured values rather than resident zero assumptions'
}
$value=Report
foreach ($index in @(0,3,4)) {$value.Samples[$index].RawReadCalls=8192;$value.Samples[$index].RawRequestedBytes=268500992L}
$value.TotalRawReadCalls=24576;$value.TotalRawRequestedBytes=805502976L
Check ((ConvertTo-NtfsVssScaleEvidence $value 10000).TotalRawRequestedBytes -eq 805502976L) 'three owner budgets summed separately never reset or confused with one owner cap'
foreach ($level in @('Report','Workload','ResultCounts','Sample','Phases','Envelope')) {
    foreach ($field in @('Path','SnapshotId','SnapshotSetId','DeviceObject','Volume','SourceToken','Reference','RawRecord','Payload','Name','Hash','Epoch','NativeCalls')) {
        if ($level -ceq 'Envelope') { RejectEnvelope {param($v)$v|Add-Member -NotePropertyName $field -NotePropertyValue 'SyntheticPrivateMarker'} 'extra envelope metadata cannot authorize cleanup' }
        else { RejectReport {param($v)$target=switch($level){'Report'{$v};'Workload'{$v.Workload};'ResultCounts'{$v.ResultCounts};'Sample'{$v.Samples[0]};'Phases'{$v.Samples[0].Phases}};
            $target|Add-Member -NotePropertyName $field -NotePropertyValue 'SyntheticPrivateMarker'} 'deep extra field rejected without private output' }
    }
}
foreach ($field in @('schema','checks','failures','native_error')) {
    foreach ($bad in @('1',$true,[double]1,[decimal]1,$null)) { RejectEnvelope {param($v)$v.$field=$bad} 'envelope integers must be actual bounded integers' }
}
foreach ($bad in @(0,2,-1,[long]::MaxValue)) { RejectEnvelope {param($v)$v.schema=$bad} 'foreign envelope schema blocks cleanup' }
foreach ($bad in @('vss_verified','benchmarked','vss_scale_benchmarked ','VSS_SCALE_BENCHMARKED','fixture_scanned',1,$null)) { RejectEnvelope {param($v)$v.status=$bad} 'other modes and case variants cannot borrow cleanup authority' }
foreach ($bad in @('SnapshotReadVerified','complete','Comparison','',1,$null)) { RejectEnvelope {param($v)$v.stage=$bad} 'successful stage exact' }
foreach ($bad in @('VssScaleFailed','none','',1,$null)) { RejectEnvelope {param($v)$v.code=$bad} 'successful code exact None' }
foreach ($bad in @(-1,1,[int]::MinValue,[int]::MaxValue)) { RejectEnvelope {param($v)$v.native_error=$bad} 'successful native error is zero' }
foreach ($bad in @(-1,0,5,7,[long]::MaxValue)) { RejectEnvelope {param($v)$v.checks=$bad} 'successful checks prove all six sample attempts' }
foreach ($bad in @(-1,1,2)) { RejectEnvelope {param($v)$v.failures=$bad} 'success cannot contain a failure' }
foreach ($bad in @($false,'true',1,$null)) { RejectEnvelope {param($v)$v.cleanup_confirmed=$bad} 'unconfirmed or mistyped cleanup blocks success and detach' }
foreach ($bad in @($null,@{},@(),1,'SyntheticPrivateMarker')) { RejectEnvelope {param($v)$v.vss_scale=$bad} 'missing deep report blocks cleanup' }
RejectEnvelope {param($v)$v.vss_scale.Samples[5].Closed=$false} 'one unclosed session blocks top-level cleanup true'
$codes=@('VssScalePreparationFailed','VssScaleSourceFailed','VssScaleComparisonFailed','VssScaleCancelled','VssScaleCleanupFailed','VssScaleFailed')
$stages=@('Preparation','SnapshotCreate','Namespace','RawRead','Parse','Index','Native','Projection','Directory','Cleanup','Comparison','Complete')
foreach ($code in $codes) { foreach ($stage in $stages) { foreach ($cleaned in @($false,$true)) {
    $value=Envelope $true $cleaned;$value.code=$code;$value.stage=$stage
    $safe=ConvertTo-NtfsVssScaleEnvelope $value 10000
    Check ($safe.status -ceq 'failed' -and $null -eq $safe.vss_scale -and $safe.cleanup_confirmed -eq $cleaned) 'controlled failure never retains successful benchmark evidence'
} } }
foreach ($native in @([int]::MinValue,-1,0,[int]::MaxValue)) {
    $value=Envelope $true;$value.native_error=$native
    Check ((ConvertTo-NtfsVssScaleEnvelope $value 10000).native_error -eq $native) 'failure numeric native evidence stays signed int without text'
}
foreach ($count in @(0,6)) { $value=Envelope $true;$value.checks=$count;Check ((ConvertTo-NtfsVssScaleEnvelope $value 10000).checks -eq $count) 'failure count may cover a bounded completed prefix but no report' }
foreach ($bad in @('VssUnexpected','VssScaleUnknown','SyntheticPrivateMarker','vssscalefailed','None','',1,$null)) { RejectEnvelope {param($v)$v.code=$bad} 'unknown failure code blocks cleanup' $true }
foreach ($bad in @('RawRead ','rawread','VssScaleSourceFailed','',1,$null)) { RejectEnvelope {param($v)$v.stage=$bad} 'unknown failure phase blocks cleanup' $true }
foreach ($bad in @(-1,7,[long]::MaxValue)) { RejectEnvelope {param($v)$v.checks=$bad} 'failure progress prefix remains bounded' $true }
foreach ($bad in @(-1,0,2)) { RejectEnvelope {param($v)$v.failures=$bad} 'failure envelope has exactly one primary failure' $true }
foreach ($bad in @(-2147483649L,2147483648L)) { RejectEnvelope {param($v)$v.native_error=$bad} 'failure native number cannot escape int range' $true }
foreach ($bad in @('true',1,$null)) { RejectEnvelope {param($v)$v.cleanup_confirmed=$bad} 'failure cleanup must remain an explicit boolean' $true }
RejectEnvelope {param($v)$v.vss_scale=Report} 'failed envelope cannot preserve benchmark results' $true
foreach ($level in @('Report','Envelope','Phases','Sample')) {
    $fields=switch($level){'Report'{@((Report).PSObject.Properties.Name)};'Envelope'{@((Envelope).PSObject.Properties.Name)};
        'Phases'{@((Report).Samples[0].Phases.PSObject.Properties.Name)};'Sample'{@((Report).Samples[0].PSObject.Properties.Name)}}
    foreach ($field in $fields) {
        if($level -ceq 'Envelope'){RejectEnvelope {param($v)$old=$v.$field;$v.PSObject.Properties.Remove($field);$v|Add-Member -NotePropertyName $field.ToUpperInvariant() -NotePropertyValue $old} 'envelope field spelling exact'}
        else{RejectReport {param($v)$target=switch($level){'Report'{$v};'Phases'{$v.Samples[0].Phases};'Sample'{$v.Samples[0]}};
            $old=$target.$field;$target.PSObject.Properties.Remove($field);$target|Add-Member -NotePropertyName $field.ToLowerInvariant() -NotePropertyValue $old} 'deep field spelling exact'}
    }
}
foreach ($level in @('Report','Envelope','Sample','Phases')) {
    $script:accessed=$false
    if($level -ceq 'Envelope'){RejectEnvelope {param($v)$v.PSObject.Properties.Remove('cleanup_confirmed');$v|Add-Member -MemberType ScriptProperty -Name cleanup_confirmed -Value {$script:accessed=$true;throw 'SyntheticPrivateMarker'}} 'active envelope property blocks cleanup'}
    else{RejectReport {param($v)$target=switch($level){'Report'{$v};'Sample'{$v.Samples[0]};'Phases'{$v.Samples[0].Phases}};
        $field=@($target.PSObject.Properties.Name)[0];$target.PSObject.Properties.Remove($field);$target|Add-Member -MemberType ScriptProperty -Name $field -Value {$script:accessed=$true;throw 'SyntheticPrivateMarker'}} 'active deep property rejected before invocation'}
    Check (-not $script:accessed) 'field inspection never invokes untrusted getter'
}
foreach ($bad in @($null,@{},@(),1,'SyntheticPrivateMarker')) {
    $message=$null;try { ConvertTo-NtfsVssScaleEvidence $bad 10000|Out-Null } catch {$message=$_.Exception.Message}
    Check ($message -ceq 'VSS_SCALE_EVIDENCE_INVALID') 'root report shape exact'
    $message=$null;try { ConvertTo-NtfsVssScaleEnvelope $bad 10000|Out-Null } catch {$message=$_.Exception.Message}
    Check ($message -ceq 'VSS_SCALE_ENVELOPE_INVALID') 'root envelope shape cannot authorize cleanup'
}
foreach ($duplicate in @('{"cleanup_confirmed":false,"cleanup_confirmed":true}', '{"cleanup_confirmed":false,"CLEANUP_CONFIRMED":true}',
    '{"vss_scale":{"Revision":"vss-scale-benchmark-v1","revision":"fixture-vss-v1"}}',
    '{"vss_scale":{"Samples":[{"Phases":{"BackendMilliseconds":20,"backendmilliseconds":0}}]}}')) {
    $caught=$false;try { Assert-UniqueJsonKeys $duplicate } catch {$caught=$true}
    Check $caught 'duplicate JSON cannot collapse into cleanup or benchmark success'
}
Write-Output ('RESULT: PASS ('+$checks+' checks)')
