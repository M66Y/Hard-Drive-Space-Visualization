#requires -Version 7.4
# Pure invented JSON-shaped values, not a VSS provider or cleanup proof.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$checks=0
function Check([bool]$Condition,[string]$Name) {
    if(-not $Condition){throw ('FAIL '+$Name)}
    $script:checks++;Write-Output ('PASS NTFS VSS fixture evidence '+$Name)
}
$tokens=$null;$errors=$null
$source=Join-Path $PSScriptRoot '../scripts/ntfs-prototype/VssFixtureEvidence.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $source).Path,[ref]$tokens,[ref]$errors)
Check ($errors.Count -eq 0) 'converter syntax'
$functions=@($ast.EndBlock.Statements|Where-Object {$_ -is [Management.Automation.Language.FunctionDefinitionAst]})
Check ($functions.Count -eq 2 -and $ast.EndBlock.Statements.Count -eq 2 -and
    ($functions.Name -join ',') -ceq 'ConvertTo-VssFixtureEvidence,ConvertTo-NtfsVssFixtureEnvelope') 'only two pure converters are defined'
foreach($function in $functions){. ([scriptblock]::Create($function.Extent.Text))}
$guardSource=Join-Path $PSScriptRoot '../scripts/ntfs-prototype/KernelMatrixEvidence.ps1'
$guardAst=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $guardSource).Path,[ref]$tokens,[ref]$errors)
Check ($errors.Count -eq 0) 'shared JSON guard syntax'
$guard=@($guardAst.EndBlock.Statements|Where-Object {$_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -ceq 'Assert-UniqueJsonKeys'})
Check ($guard.Count -eq 1) 'existing duplicate-key guard is reused'
. ([scriptblock]::Create($guard[0].Extent.Text))
function Report {
    return [pscustomobject]@{Revision='fixture-vss-v1';Stage='SnapshotReadVerified';CreatedSnapshots=1;DeletedSnapshots=1;
        FilesCompared=3;RawRecordsCompared=4;RawReadCalls=4;RawRequestedBytes=[long]4096;PropertiesBound=$true;
        SnapshotFilesStable=$true;LiveChangesObserved=$true;GeometryVerified=$true;RawRecordsMatched=$true;CleanupConfirmed=$true}
}
function Envelope([bool]$Failure=$false,[bool]$Cleaned=$true) {
    if($Failure){return [pscustomobject]@{schema=1;status='failed';stage='VssRawOpen';checks=0;failures=1;code='VssRawOpen';
        native_error=-2147024891;cleanup_confirmed=$Cleaned;vss_evidence=$null}}
    return [pscustomobject]@{schema=1;status='vss_verified';stage='SnapshotReadVerified';checks=30;failures=0;code='None';
        native_error=0;cleanup_confirmed=$true;vss_evidence=(Report)}
}
function RejectReport([scriptblock]$Mutation,[string]$Name) {
    $value=Report;& $Mutation $value;$message=$null
    try{ConvertTo-VssFixtureEvidence $value|Out-Null}catch{$message=$_.Exception.Message}
    Check ($message -ceq 'VSS_FIXTURE_EVIDENCE_INVALID') $Name
}
function RejectEnvelope([scriptblock]$Mutation,[string]$Name,[bool]$Failure=$false) {
    $value=Envelope $Failure;& $Mutation $value;$message=$null;$cleanupGate=$false
    try{$safe=ConvertTo-NtfsVssFixtureEnvelope $value;$cleanupGate=$safe.cleanup_confirmed}catch{$message=$_.Exception.Message}
    Check ($message -ceq 'VSS_FIXTURE_ENVELOPE_INVALID' -and -not $cleanupGate) $Name
}
$safe=ConvertTo-VssFixtureEvidence (Report)
Check (($safe.Keys -join ',') -ceq 'Revision,Stage,CreatedSnapshots,DeletedSnapshots,FilesCompared,RawRecordsCompared,RawReadCalls,RawRequestedBytes,PropertiesBound,SnapshotFilesStable,LiveChangesObserved,GeometryVerified,RawRecordsMatched,CleanupConfirmed') 'canonical safe report fields'
Check ($safe.CreatedSnapshots -eq 1 -and $safe.DeletedSnapshots -eq 1 -and $safe.FilesCompared -eq 3 -and $safe.RawRecordsCompared -eq 4) 'only one owned snapshot and the fixed comparison scope are represented'
$value=Envelope;$safe=ConvertTo-NtfsVssFixtureEnvelope $value
Check (($safe.Keys -join ',') -ceq 'schema,status,stage,checks,failures,code,native_error,cleanup_confirmed,vss_evidence') 'canonical complete cleanup envelope'
Check ($safe.status -ceq 'vss_verified' -and $safe.cleanup_confirmed -and $safe.vss_evidence.CleanupConfirmed) 'verified status requires both confirmed cleanup and complete deep evidence'
$value.vss_evidence.RawReadCalls=16;$value.cleanup_confirmed=$false
Check ($safe.vss_evidence.RawReadCalls -eq 4 -and $safe.cleanup_confirmed) 'accepted output does not alias the input evidence'
$json=$safe|ConvertTo-Json -Depth 10
Assert-UniqueJsonKeys $json
Check ((ConvertTo-NtfsVssFixtureEnvelope ($json|ConvertFrom-Json)).status -ceq 'vss_verified') 'complete envelope JSON roundtrip'
Check ($json -cnotmatch '"(?:SnapshotId|SnapshotSet|ProviderId|DeviceObject|VolumeGuid|RawRecord|Payload|Reference|SourceToken|Path)"\s*:') 'serialized evidence contains no source addresses identities names or bytes'
foreach($calls in @(4,8,16)) {
    foreach($bytes in @(4096,8192,16384)) {
        if($bytes -lt $calls*512){continue}
        $value=Report;$value.RawReadCalls=$calls;$value.RawRequestedBytes=[long]$bytes
        Check ((ConvertTo-VssFixtureEvidence $value).RawRequestedBytes -eq $bytes) 'raw request count and aligned byte endpoints accepted'
    }
}
foreach($counter in @(1,1000)) {
    $value=Envelope;$value.checks=$counter
    Check ((ConvertTo-NtfsVssFixtureEnvelope $value).checks -eq $counter) 'successful check count is finite and nonzero'
}
$codes=@('VssFixtureGuard','VssFixtureCreate','VssComInitialize','VssComSecurity','VssCreateBackup','VssInitializeBackup',
    'VssContext','VssVolumeUnsupported','VssStartSet','VssAddVolume','VssCreateSnapshot','VssAsyncWait','VssAsyncStatus',
    'VssAsyncTimeout','VssCancelled','VssProperties','VssSnapshotBinding','VssSnapshotNative','VssLiveMutation',
    'VssRawOpen','VssRawGeometry','VssRawRead','VssRawMap','VssRawCompare','VssCleanup','VssUnexpected')
foreach($code in $codes) {
    foreach($cleaned in @($false,$true)) {
        $value=Envelope $true $cleaned;$value.code=$code;$value.stage=$code
        $safe=ConvertTo-NtfsVssFixtureEnvelope $value
        Check ($safe.status -ceq 'failed' -and $null -eq $safe.vss_evidence -and $safe.cleanup_confirmed -eq $cleaned) 'fixed failure category preserves cleanup confirmation independently of verified status'
    }
}
foreach($errorCode in @([int]::MinValue,-1,0,[int]::MaxValue)) {
    $value=Envelope $true;$value.native_error=$errorCode
    Check ((ConvertTo-NtfsVssFixtureEnvelope $value).native_error -eq $errorCode) 'signed HRESULT and Win32 numeric evidence stay bounded'
}
foreach($count in @(0,1000)) {
    $value=Envelope $true;$value.checks=$count
    Check ((ConvertTo-NtfsVssFixtureEnvelope $value).checks -eq $count) 'failure can occur before the first comparison'
}
foreach($field in @((Report).PSObject.Properties.Name)) {RejectReport {param($v)$v.PSObject.Properties.Remove($field)} ('missing report field '+$field)}
foreach($field in @((Envelope).PSObject.Properties.Name)) {RejectEnvelope {param($v)$v.PSObject.Properties.Remove($field)} ('missing envelope field blocks cleanup '+$field)}
foreach($field in @('Revision','Stage')) {
    foreach($bad in @('SyntheticFixture','SnapshotCreated','fixture-vss-v2','snapshotreadverified','',1,$true,$null)) {
        RejectReport {param($v)$v.$field=$bad} 'unknown simulated or mistyped report identity rejected'
    }
}
foreach($field in @('CreatedSnapshots','DeletedSnapshots','FilesCompared','RawRecordsCompared','RawReadCalls','RawRequestedBytes')) {
    foreach($bad in @(-1,[long]::MaxValue,'1',$true,[double]1,[decimal]1,$null)) {RejectReport {param($v)$v.$field=$bad} ('report bounded integer '+$field)}
}
foreach($pair in @(@('CreatedSnapshots',0),@('CreatedSnapshots',2),@('DeletedSnapshots',0),@('DeletedSnapshots',2),
    @('FilesCompared',2),@('FilesCompared',4),@('RawRecordsCompared',3),@('RawRecordsCompared',5),
    @('RawReadCalls',3),@('RawReadCalls',17),@('RawRequestedBytes',3584),@('RawRequestedBytes',16896),@('RawRequestedBytes',4097))) {
    RejectReport {param($v)$v.($pair[0])=$pair[1]} 'snapshot file record and raw read boundaries are exact'
}
RejectReport {param($v)$v.RawReadCalls=16;$v.RawRequestedBytes=4096} 'request count cannot exceed its minimum sector-byte total'
foreach($field in @('PropertiesBound','SnapshotFilesStable','LiveChangesObserved','GeometryVerified','RawRecordsMatched','CleanupConfirmed')) {
    foreach($bad in @($false,'true',1,$null,@($true))) {RejectReport {param($v)$v.$field=$bad} ('report boolean must be explicitly true '+$field)}
}
foreach($field in @('schema','checks','failures','native_error')) {
    foreach($bad in @('1',$true,[double]1,[decimal]1,$null)) {RejectEnvelope {param($v)$v.$field=$bad} ('envelope integer type blocks cleanup '+$field)}
}
foreach($bad in @(0,2,-1,[long]::MaxValue)) {RejectEnvelope {param($v)$v.schema=$bad} 'unknown envelope schema blocks cleanup'}
foreach($bad in @('vss_verified ', 'VSS_VERIFIED','passed','bitmap_observed','fixture_scanned','',1,$true,$null)) {RejectEnvelope {param($v)$v.status=$bad} 'unrelated mode or status cannot grant VSS cleanup'}
foreach($bad in @('SnapshotCreated','VssRawOpen','snapshotreadverified','',1,$true,$null)) {RejectEnvelope {param($v)$v.stage=$bad} 'successful stage is exact'}
foreach($bad in @('VssUnexpected','OperationFailed','none','',1,$true,$null)) {RejectEnvelope {param($v)$v.code=$bad} 'successful code must be None'}
foreach($bad in @(-1,1,[int]::MinValue,[int]::MaxValue)) {RejectEnvelope {param($v)$v.native_error=$bad} 'successful envelope cannot contain a native error'}
foreach($bad in @(-1,0,1001,[long]::MaxValue)) {RejectEnvelope {param($v)$v.checks=$bad} 'successful checks must be within the finite nonzero range'}
foreach($bad in @(-1,1,2)) {RejectEnvelope {param($v)$v.failures=$bad} 'successful envelope cannot contain failures'}
foreach($bad in @($false,'true',1,$null,@($true))) {RejectEnvelope {param($v)$v.cleanup_confirmed=$bad} 'unconfirmed or mistyped cleanup cannot permit success or detach'}
RejectEnvelope {param($v)$v.vss_evidence.CleanupConfirmed=$false} 'nested cleanup failure blocks top-level true confirmation'
RejectEnvelope {param($v)$v.vss_evidence.PropertiesBound=$false} 'deep source-property failure blocks top-level true confirmation'
foreach($bad in @($null,@{},@(),1,'SyntheticPrivateMarker')) {RejectEnvelope {param($v)$v.vss_evidence=$bad} 'missing or malformed deep evidence blocks cleanup'}
foreach($bad in @('None','VssUnknown','SyntheticPrivateMarker','vssrawopen','',1,$true,$null)) {RejectEnvelope {param($v)$v.code=$bad;$v.stage=$bad} 'unknown failure code cannot authorize cleanup' $true}
RejectEnvelope {param($v)$v.stage='VssRawRead'} 'failure stage and code must agree' $true
foreach($bad in @(-1,0,2,1001)) {RejectEnvelope {param($v)$v.failures=$bad} 'failed envelope requires exactly one failure' $true}
foreach($bad in @(-1,1001,[long]::MaxValue)) {RejectEnvelope {param($v)$v.checks=$bad} 'failed checks remain finite' $true}
foreach($bad in @(-2147483649L,2147483648L,[long]::MaxValue)) {RejectEnvelope {param($v)$v.native_error=$bad} 'failure HRESULT cannot escape signed int range' $true}
foreach($bad in @('true',1,$null,@($false))) {RejectEnvelope {param($v)$v.cleanup_confirmed=$bad} 'failed cleanup confirmation must still be a real boolean' $true}
RejectEnvelope {param($v)$v.vss_evidence=Report} 'failed envelope cannot preserve successful snapshot evidence' $true
$safe=ConvertTo-NtfsVssFixtureEnvelope (Envelope $true $false)
Check (-not $safe.cleanup_confirmed -and $safe.status -ceq 'failed') 'explicit unconfirmed failure is a valid retained-image outcome and never a cleanup authorization'
foreach($level in @('Report','Envelope')) {
    foreach($field in @('Path','DeviceObject','SnapshotId','SnapshotSetId','ProviderId','VolumeGuid','SourceToken','Raw','Payload','Name','native_operation','EvidenceKind')) {
        if($level -ceq 'Report'){RejectReport {param($v)$v|Add-Member -NotePropertyName $field -NotePropertyValue 'SyntheticPrivateMarker'} 'extra source or proof report field rejected'}
        else{RejectEnvelope {param($v)$v|Add-Member -NotePropertyName $field -NotePropertyValue 'SyntheticPrivateMarker'} 'extra source or proof envelope field blocks cleanup'}
    }
}
RejectEnvelope {param($v)$v.vss_evidence|Add-Member -NotePropertyName SnapshotId -NotePropertyValue 'SyntheticPrivateMarker'} 'deep source identifier injection blocks cleanup'
foreach($field in @((Report).PSObject.Properties.Name)) {
    RejectReport {param($v)$old=$v.$field;$v.PSObject.Properties.Remove($field);$v|Add-Member -NotePropertyName $field.ToLowerInvariant() -NotePropertyValue $old} 'report field casing exact'
}
foreach($field in @((Envelope).PSObject.Properties.Name)) {
    RejectEnvelope {param($v)$old=$v.$field;$v.PSObject.Properties.Remove($field);$v|Add-Member -NotePropertyName $field.ToUpperInvariant() -NotePropertyValue $old} 'envelope field casing exact'
}
foreach($field in @('Revision','CleanupConfirmed')) {
    $script:accessed=$false
    RejectReport {param($v)$v.PSObject.Properties.Remove($field);$v|Add-Member -MemberType ScriptProperty -Name $field -Value {$script:accessed=$true;throw 'SyntheticPrivateMarker'}} 'active report property rejected before invocation'
    Check (-not $script:accessed) 'report field inspection cannot invoke an active property'
}
foreach($field in @('status','cleanup_confirmed','vss_evidence')) {
    $script:accessed=$false
    RejectEnvelope {param($v)$v.PSObject.Properties.Remove($field);$v|Add-Member -MemberType ScriptProperty -Name $field -Value {$script:accessed=$true;throw 'SyntheticPrivateMarker'}} 'active envelope property cannot authorize cleanup'
    Check (-not $script:accessed) 'envelope field inspection cannot invoke an active property'
}
foreach($value in @($null,@{},@(),1,'SyntheticPrivateMarker')) {
    $message=$null;try{ConvertTo-VssFixtureEvidence $value|Out-Null}catch{$message=$_.Exception.Message}
    Check ($message -ceq 'VSS_FIXTURE_EVIDENCE_INVALID') 'root report shape is exact'
    $message=$null;try{ConvertTo-NtfsVssFixtureEnvelope $value|Out-Null}catch{$message=$_.Exception.Message}
    Check ($message -ceq 'VSS_FIXTURE_ENVELOPE_INVALID') 'root envelope shape cannot authorize cleanup'
}
$duplicates=@('{"cleanup_confirmed":false,"cleanup_confirmed":true}', '{"cleanup_confirmed":false,"CLEANUP_CONFIRMED":true}',
    '{"status":"failed","Status":"vss_verified"}', '{"vss_evidence":{"CleanupConfirmed":false,"cleanupConfirmed":true}}',
    '{"vss_evidence":{"Revision":"fixture-vss-v1","Revision":"SyntheticFixture"}}')
foreach($duplicate in $duplicates){$caught=$false;try{Assert-UniqueJsonKeys $duplicate}catch{$caught=$true};Check $caught 'duplicate and case-variant JSON keys cannot be collapsed before cleanup validation'}
Write-Output ('RESULT: PASS ('+$checks+' checks)')
