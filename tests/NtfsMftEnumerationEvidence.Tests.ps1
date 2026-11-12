#requires -Version 7.4
# Pure summary checks; no adapter, native storage, VHD or raw record is opened.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) {throw ('FAIL ' + $Name)}
    $script:checks++; Write-Output ('PASS NTFS MFT enumeration evidence ' + $Name)
}
$source = Join-Path $PSScriptRoot '../scripts/ntfs-prototype/MftEnumerationEvidence.ps1'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $source).Path,[ref]$tokens,[ref]$errors)
Check ($errors.Count -eq 0) 'converter syntax'
$functions=@($ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.FunctionDefinitionAst]})
Check ($functions.Count -eq 1 -and $ast.EndBlock.Statements.Count -eq 1) 'dot source defines only a pure independent converter'
. ([scriptblock]::Create($functions[0].Extent.Text))
$jsonGuard=Join-Path $PSScriptRoot '../scripts/ntfs-prototype/KernelMatrixEvidence.ps1'
$guardAst=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $jsonGuard).Path,[ref]$tokens,[ref]$errors)
Check ($errors.Count -eq 0) 'shared JSON guard syntax'
$guard=@($guardAst.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -ceq 'Assert-UniqueJsonKeys'})
Check ($guard.Count -eq 1) 'shared duplicate-key guard reused'
. ([scriptblock]::Create($guard[0].Extent.Text))

function Fixture {
    $cases=@(
        foreach($phase in @('A','B','C')) {
            $lengths=if($phase -ceq 'A') {@(8,24,64)} else {@(8,64)}
            foreach($kind in @('Ordinary','Compressed','Sparse')) {
                foreach($length in $lengths) {
                    [pscustomobject]@{CaseId="$phase-$kind-$length";Kind=$kind;NameLength=$length;RawCode='Decoded';
                        IdentityMatched=$true;LogicalMatched=$true;LinksMatched=$true;NamesMatched=$true;
                        AllocationCheck=$(if($kind -ceq 'Ordinary') {'Matched'} else {'Unknown'})}
                }
            }
        }
    )
    $range=[pscustomobject]@{ValidBytes=[long]65536;ExpectedSlots=64;VisitedSlots=64;Decoded=40;Free=16;Unsupported=4;Invalid=4;
        LookupRequired=20;UnknownLogical=2;UnknownAllocated=16;Pages=1;CompletedCursor=$true;MatchedObjects=21;
        UnknownAllocationObjects=14;MftRuns=1;ReadCalls=2;RequestedBytes=[long]66560;PhysicalSectorBytes=512;
        LockAcquired=$true;UnlockSucceeded=$true;PostNativeStable=$true;Cases=$cases;AllZeroInvalid=2;CodeCounts=@(
            [pscustomobject]@{Status='Decoded';Code='Decoded';Count=40},
            [pscustomobject]@{Status='Free';Code='FreeRecord';Count=16},
            [pscustomobject]@{Status='Invalid';Code='Signature';Count=4},
            [pscustomobject]@{Status='Unsupported';Code='AttributeType';Count=4})}
    return [pscustomobject]@{Revision='bounded-mft-source-v2';EvidenceKind='NativeOwnedFixture';GuardOwned=$true;
        TokenBound=$true;MapBound=$true;Closed=$true;RejectedReads=0;Enumeration=$range}
}
function Reject([scriptblock]$Mutation,[string]$Name) {
    $value=Fixture; & $Mutation $value
    $message=$null; try {ConvertTo-MftEnumerationEvidence $value | Out-Null} catch {$message=$_.Exception.Message}
    Check ($message -ceq 'MFT_ENUMERATION_EVIDENCE_INVALID') $Name
}
function RejectValue($Value,[string]$Name) {
    $message=$null; try {ConvertTo-MftEnumerationEvidence $Value | Out-Null} catch {$message=$_.Exception.Message}
    Check ($message -ceq 'MFT_ENUMERATION_EVIDENCE_INVALID') $Name
}

$value=Fixture; $safe=ConvertTo-MftEnumerationEvidence $value
Check (($safe.Keys -join ',') -ceq 'Revision,EvidenceKind,GuardOwned,TokenBound,MapBound,Closed,RejectedReads,Enumeration') 'canonical source fields'
Check (($safe.Enumeration.Keys -join ',') -ceq 'ValidBytes,ExpectedSlots,VisitedSlots,Decoded,Free,Unsupported,Invalid,LookupRequired,UnknownLogical,UnknownAllocated,Pages,CompletedCursor,MatchedObjects,UnknownAllocationObjects,MftRuns,ReadCalls,RequestedBytes,PhysicalSectorBytes,LockAcquired,UnlockSucceeded,PostNativeStable,Cases,AllZeroInvalid,CodeCounts') 'canonical range fields'
Check (($safe.Enumeration.Cases[0].Keys -join ',') -ceq 'CaseId,Kind,NameLength,RawCode,IdentityMatched,LogicalMatched,LinksMatched,NamesMatched,AllocationCheck') 'canonical target fields'
Check ($safe.Enumeration.ReadCalls -eq 2 -and $safe.Enumeration.ReadCalls -lt 22) 'batched range does not inherit the old twenty-two-call minimum'
Check ($safe.Enumeration.Unsupported -eq 4 -and $safe.Enumeration.Invalid -eq 4 -and $safe.Enumeration.Free -eq 16) 'strict rejected classifications remain separate from free slots'
Check ($safe.Enumeration.UnknownLogical -eq 2 -and $safe.Enumeration.UnknownAllocated -eq 16 -and $safe.Enumeration.UnknownAllocationObjects -eq 14) 'range uncertainty and target uncertainty have separate counters'
Check (@($safe.Enumeration.Cases | Where-Object AllocationCheck -CEQ 'Unknown').Count -eq 14) 'special targets never become known zero allocations'
$value.Enumeration.Invalid=0; $value.Enumeration.Cases[0].CaseId='private-marker'; $value.RejectedReads=99
Check ($safe.Enumeration.Invalid -eq 4 -and $safe.Enumeration.Cases[0].CaseId -ceq 'A-Ordinary-8' -and $safe.RejectedReads -eq 0) 'deep output does not alias caller objects'
$safe.Enumeration.Cases[0].CaseId='output-marker'
Check ($value.Enumeration.Cases[0].CaseId -ceq 'private-marker') 'mutating safe output does not mutate input'
$safe=ConvertTo-MftEnumerationEvidence (Fixture)
$json=$safe | ConvertTo-Json -Depth 8
Assert-UniqueJsonKeys $json
Check ((ConvertTo-MftEnumerationEvidence ($json | ConvertFrom-Json)).Enumeration.Cases.Count -eq 21) 'deep JSON roundtrip'
Check ($json -cnotmatch 'private-marker|output-marker|SourceToken|MapEpoch|Reference|SlotId|VolumeGuid|DevicePath|Sha256|PhysicalOffset|TargetRecords') 'output excludes private identifiers and raw-record tables'
$lower=Fixture; $r=$lower.Enumeration
$r.ValidBytes=[long]22528; $r.ExpectedSlots=22; $r.VisitedSlots=22; $r.Decoded=22; $r.Free=0; $r.Unsupported=0; $r.Invalid=0
$r.LookupRequired=16; $r.UnknownLogical=0; $r.UnknownAllocated=14; $r.RequestedBytes=[long]23552
$r.AllZeroInvalid=0; $r.CodeCounts=@([pscustomobject]@{Status='Decoded';Code='Decoded';Count=22})
Check ((ConvertTo-MftEnumerationEvidence $lower).Enumeration.ExpectedSlots -eq 22) 'minimum finite target and bootstrap coverage accepted'
$upper=Fixture; $r=$upper.Enumeration
$r.ValidBytes=[long]4194304; $r.ExpectedSlots=4096; $r.VisitedSlots=4096; $r.Decoded=22; $r.Free=4074; $r.Unsupported=0; $r.Invalid=0
$r.LookupRequired=16; $r.UnknownLogical=0; $r.UnknownAllocated=14; $r.Pages=64; $r.MftRuns=128; $r.ReadCalls=256
$r.RequestedBytes=[long]4195328; $r.PhysicalSectorBytes=65536; $upper.RejectedReads=128
$r.AllZeroInvalid=0; $r.CodeCounts=@([pscustomobject]@{Status='Decoded';Code='Decoded';Count=22},[pscustomobject]@{Status='Free';Code='FreeRecord';Count=4074})
Check ((ConvertTo-MftEnumerationEvidence $upper).Enumeration.RequestedBytes -eq 4195328) 'maximum bounded coverage and IO budgets accepted'
$twoPages=Fixture; $r=$twoPages.Enumeration
$r.ValidBytes=[long]66560; $r.ExpectedSlots=65; $r.VisitedSlots=65; $r.Free=17; $r.Pages=2; $r.ReadCalls=3; $r.RequestedBytes=[long]67584
$r.CodeCounts[1].Count=17
Check ((ConvertTo-MftEnumerationEvidence $twoPages).Enumeration.Pages -eq 2) 'short last page uses exact record-aligned remainder'
foreach($sector in @(512,1024,2048,4096,8192,16384,32768,65536)) {
    $value=Fixture; $value.Enumeration.PhysicalSectorBytes=$sector
    Check ((ConvertTo-MftEnumerationEvidence $value).Enumeration.PhysicalSectorBytes -eq $sector) 'physical alignment power-of-two range accepted'
}
$reordered=Fixture; $reordered.Enumeration.Cases=@($reordered.Enumeration.Cases | Sort-Object CaseId -Descending)
Check ((ConvertTo-MftEnumerationEvidence $reordered).Enumeration.Cases.Count -eq 21) 'target set membership is independent of row order'
Check (($safe.Enumeration.CodeCounts[0].Keys -join ',') -ceq 'Status,Code,Count') 'canonical diagnostic row fields'
Check ($safe.Revision -ceq 'bounded-mft-source-v2' -and $safe.Enumeration.AllZeroInvalid -eq 2 -and
    $safe.Enumeration.CodeCounts.Count -eq 4) 'v2 preserves separate all-zero observation and strict code partitions'
$copyInput=Fixture; $copyOutput=ConvertTo-MftEnumerationEvidence $copyInput
$copyInput.Enumeration.CodeCounts[0].Count=1; $copyInput.Enumeration.AllZeroInvalid=0
Check ($copyOutput.Enumeration.CodeCounts[0].Count -eq 40 -and $copyOutput.Enumeration.AllZeroInvalid -eq 2) 'new diagnostics are deeply copied without input aliases'
$invalidCodes=@('AllocationGeometry','AttributeHeader','AttributeLength','AttributeName','DataSize','DuplicateAttributeId',
    'FileName','FixupMismatch','MappingBounds','MappingPairs','MissingEndMarker','RecordHeader','RecordLength','RecordNumber',
    'ResidentBounds','Sequence','Signature','SparseMappingFlags','StandardInformation')
$unsupportedCodes=@('AttributeFlags','AttributeForm','AttributeLimit','AttributeType','ClusterGeometry','FixupStride',
    'MetadataFlags','MultipleUnnamedData','NameLimit','NtfsVersion','RecordFlags')
foreach($code in $invalidCodes) {
    $value=Fixture; $value.Enumeration.AllZeroInvalid=0; $value.Enumeration.CodeCounts[2].Code=$code
    Check ((ConvertTo-MftEnumerationEvidence $value).Enumeration.CodeCounts[2].Code -ceq $code) 'each current strict Invalid code is accepted with its proper status'
}
foreach($code in $unsupportedCodes) {
    $value=Fixture; $value.Enumeration.CodeCounts[3].Code=$code
    Check ((ConvertTo-MftEnumerationEvidence $value).Enumeration.CodeCounts[3].Code -ceq $code) 'each current strict Unsupported code is accepted with its proper status'
}
$allPairs=Fixture; $r=$allPairs.Enumeration
$r.Decoded=33; $r.Free=1; $r.Invalid=19; $r.Unsupported=11; $r.AllZeroInvalid=1
$r.CodeCounts=@([pscustomobject]@{Status='Decoded';Code='Decoded';Count=33},[pscustomobject]@{Status='Free';Code='FreeRecord';Count=1})+
    @($invalidCodes|ForEach-Object {[pscustomobject]@{Status='Invalid';Code=$_;Count=1}})+
    @($unsupportedCodes|ForEach-Object {[pscustomobject]@{Status='Unsupported';Code=$_;Count=1}})
Check ((ConvertTo-MftEnumerationEvidence $allPairs).Enumeration.CodeCounts.Count -eq 32) 'all thirty-two exact pairs coexist in enum and ordinal order'
$maximumReason=Fixture; $r=$maximumReason.Enumeration
$r.ValidBytes=[long]4194304; $r.ExpectedSlots=4096; $r.VisitedSlots=4096; $r.Decoded=4096; $r.Free=0; $r.Invalid=0; $r.Unsupported=0
$r.Pages=64; $r.ReadCalls=65; $r.RequestedBytes=[long]4195328; $r.AllZeroInvalid=0
$r.CodeCounts=@([pscustomobject]@{Status='Decoded';Code='Decoded';Count=[long]4096})
Check ((ConvertTo-MftEnumerationEvidence $maximumReason).Enumeration.CodeCounts[0].Count -eq 4096) 'maximum exact reason count and absent zero-count statuses accepted'
foreach($count in @(0,4)) {
    $value=Fixture; $value.Enumeration.AllZeroInvalid=$count
    Check ((ConvertTo-MftEnumerationEvidence $value).Enumeration.AllZeroInvalid -eq $count) 'all-zero Invalid observation accepts its exact subset endpoints'
}

RejectValue $null 'null source rejected'
RejectValue @{} 'hashtable source rejected'
RejectValue @((Fixture),(Fixture)) 'source array rejected'
$rootFields=@('Revision','EvidenceKind','GuardOwned','TokenBound','MapBound','Closed','RejectedReads','Enumeration')
$rangeFields=@('ValidBytes','ExpectedSlots','VisitedSlots','Decoded','Free','Unsupported','Invalid','LookupRequired','UnknownLogical',
    'UnknownAllocated','Pages','CompletedCursor','MatchedObjects','UnknownAllocationObjects','MftRuns','ReadCalls','RequestedBytes',
    'PhysicalSectorBytes','LockAcquired','UnlockSucceeded','PostNativeStable','Cases','AllZeroInvalid','CodeCounts')
$caseFields=@('CaseId','Kind','NameLength','RawCode','IdentityMatched','LogicalMatched','LinksMatched','NamesMatched','AllocationCheck')
foreach($field in $rootFields) {Reject {param($v) $v.PSObject.Properties.Remove($field)} ('missing source field '+$field)}
foreach($field in $rangeFields) {Reject {param($v) $v.Enumeration.PSObject.Properties.Remove($field)} ('missing range field '+$field)}
foreach($field in $caseFields) {Reject {param($v) $v.Enumeration.Cases[0].PSObject.Properties.Remove($field)} ('missing target field '+$field)}
foreach($field in @('SourceToken','Epoch','SlotId','Reference','Volume','DevicePath','Path','Raw','Sha256','TargetRecords','TotalAllocatedBytes','DiskCorruption')) {
    Reject {param($v) $v | Add-Member -NotePropertyName $field -NotePropertyValue 'private-marker'} ('source injection '+$field)
    Reject {param($v) $v.Enumeration | Add-Member -NotePropertyName $field -NotePropertyValue 'private-marker'} ('range injection '+$field)
    Reject {param($v) $v.Enumeration.Cases[0] | Add-Member -NotePropertyName $field -NotePropertyValue 'private-marker'} ('target injection '+$field)
}
Reject {param($v) $v.PSObject.Properties.Remove('Closed');$v|Add-Member -NotePropertyName closed -NotePropertyValue $true} 'source field casing exact'
Reject {param($v) $v.Enumeration.PSObject.Properties.Remove('Decoded');$v.Enumeration|Add-Member -NotePropertyName decoded -NotePropertyValue 40} 'range field casing exact'
Reject {param($v) $v.Enumeration.Cases[0].PSObject.Properties.Remove('CaseId');$v.Enumeration.Cases[0]|Add-Member -NotePropertyName caseId -NotePropertyValue 'A-Ordinary-8'} 'target field casing exact'
Reject {param($v) $v.PSObject.Properties.Remove('Closed');$v|Add-Member -MemberType ScriptProperty -Name Closed -Value {throw 'private-marker'}} 'active source property rejected before evaluation'
Reject {param($v) $v.Enumeration.PSObject.Properties.Remove('Decoded');$v.Enumeration|Add-Member -MemberType ScriptProperty -Name Decoded -Value {throw 'private-marker'}} 'active range property rejected before evaluation'
Reject {param($v) $v.Enumeration.Cases[0].PSObject.Properties.Remove('RawCode');$v.Enumeration.Cases[0]|Add-Member -MemberType ScriptProperty -Name RawCode -Value {throw 'private-marker'}} 'active target property rejected before evaluation'
foreach($bad in @('bounded-mft-source-v1','bounded-mft-source-v3','BOUNDED-MFT-SOURCE-V2',$true,1,$null)) {Reject {param($v) $v.Revision=$bad} 'source revision value and type strict'}
foreach($bad in @('SyntheticFixture','NativeOwnedFixture-Private','nativeownedfixture','VSS','ValidatedImmutable',$true,$null)) {Reject {param($v) $v.EvidenceKind=$bad} 'synthetic or unknown source cannot mint native success'}
Reject {param($v) $v.EvidenceKind='SyntheticFixture';$v.GuardOwned=$false} 'actual synthetic source-report shape rejected'
foreach($field in @('GuardOwned','TokenBound','MapBound','Closed')) {
    foreach($bad in @($false,'true',1,$null,@($true))) {Reject {param($v) $v.$field=$bad} ('source boolean strict '+$field)}
}
foreach($bad in @(-1,129,'0',$true,[double]0,[decimal]0,$null)) {Reject {param($v) $v.RejectedReads=$bad} 'rejected read count bounded and integer'}
Reject {param($v) $v.Enumeration=$null} 'missing enumeration rejected'
Reject {param($v) $v.Enumeration=@{}} 'hashtable enumeration rejected'
foreach($field in @('ValidBytes','ExpectedSlots','VisitedSlots','Decoded','Free','Unsupported','Invalid','LookupRequired',
    'UnknownLogical','UnknownAllocated','Pages','MatchedObjects','UnknownAllocationObjects','MftRuns','ReadCalls','RequestedBytes','PhysicalSectorBytes','AllZeroInvalid')) {
    foreach($bad in @(-1,[long]::MaxValue,'1',$true,[double]1,[decimal]1,$null)) {Reject {param($v) $v.Enumeration.$field=$bad} ('range numeric boundary '+$field)}
}
Reject {param($v) $v.Enumeration.ValidBytes=21504} 'valid range below target and bootstrap minimum rejected'
Reject {param($v) $v.Enumeration.ValidBytes=4195328} 'range above 4096 slots rejected before success'
Reject {param($v) $v.Enumeration.ValidBytes=65537} 'valid bytes whole-record alignment required'
Reject {param($v) $v.Enumeration.ExpectedSlots=63} 'expected slot count matches valid bytes'
Reject {param($v) $v.Enumeration.VisitedSlots=63} 'truncated cursor cannot claim full range'
Reject {param($v) $v.Enumeration.Decoded=21;$v.Enumeration.Free=35} 'at least bootstrap and known targets must decode'
Reject {param($v) $v.Enumeration.Free=17} 'strict classification sum equals visited slots'
Reject {param($v) $v.Enumeration.Unsupported=65} 'individual strict classification cannot exceed visited slots'
Reject {param($v) $v.Enumeration.LookupRequired=15} 'known target types impose the minimum lookup-needed count'
Reject {param($v) $v.Enumeration.LookupRequired=41} 'lookup-needed count bounded by decoded records'
Reject {param($v) $v.Enumeration.UnknownLogical=21} 'logical uncertainty must have a lookup reason'
Reject {param($v) $v.Enumeration.UnknownAllocated=13} 'known special targets remain globally unknown'
Reject {param($v) $v.Enumeration.UnknownAllocated=21} 'allocation uncertainty must have a lookup reason'
Reject {param($v) $v.Enumeration.Pages=2} 'page count uses the exact bound length'
Reject {param($v) $v.Enumeration.MatchedObjects=20} 'all known Native targets required'
Reject {param($v) $v.Enumeration.UnknownAllocationObjects=0} 'target unknown cannot be relabeled as known zero'
Reject {param($v) $v.Enumeration.MftRuns=0} 'MFT run count positive'
Reject {param($v) $v.Enumeration.MftRuns=129} 'MFT run count bounded'
Reject {param($v) $v.Enumeration.ReadCalls=1} 'bootstrap and nonempty range require separate calls'
Reject {param($v) $v.Enumeration.ReadCalls=257} 'new mode call budget maximum'
Reject {param($v) $v.Enumeration.Pages=2;$v.Enumeration.ValidBytes=66560;$v.Enumeration.ExpectedSlots=65;$v.Enumeration.VisitedSlots=65;$v.Enumeration.Free=17;$v.Enumeration.RequestedBytes=67584} 'read calls cover each page plus bootstrap'
Reject {param($v) $v.Enumeration.RequestedBytes=65536} 'requested bytes include the bootstrap'
Reject {param($v) $v.Enumeration.RequestedBytes=67584} 'requested bytes do not hide an overread'
foreach($bad in @(0,256,768,1025,65537)) {Reject {param($v) $v.Enumeration.PhysicalSectorBytes=$bad} 'physical alignment geometry bounded and power of two'}
foreach($field in @('CompletedCursor','LockAcquired','UnlockSucceeded','PostNativeStable')) {
    foreach($bad in @($false,'true',1,$null,@($true))) {Reject {param($v) $v.Enumeration.$field=$bad} ('range lifecycle boolean strict '+$field)}
}
Reject {param($v) $v.Enumeration.Cases=$null} 'null target list rejected'
Reject {param($v) $v.Enumeration.Cases=$v.Enumeration.Cases[0]} 'scalar target list rejected'
Reject {param($v) $v.Enumeration.Cases=$v.Enumeration.Cases[0..19]} 'missing target rejected'
Reject {param($v) $v.Enumeration.Cases=@($v.Enumeration.Cases)+@($v.Enumeration.Cases[0])} 'extra target rejected'
Reject {param($v) $v.Enumeration.Cases[0]=$null} 'null target rejected'
Reject {param($v) $v.Enumeration.Cases[0]=@{}} 'hashtable target rejected'
Reject {param($v) $v.Enumeration.Cases[1].CaseId=$v.Enumeration.Cases[0].CaseId} 'duplicate target cannot conceal missing identity'
foreach($bad in @('A-ordinary-8','A-Ordinary-8-Flushed','B-Ordinary-24','../private-marker',$true,$null)) {Reject {param($v) $v.Enumeration.Cases[0].CaseId=$bad} 'case identity fixed and controlled'}
Reject {param($v) $v.Enumeration.Cases[0].Kind='Sparse'} 'kind matches the fixed case'
Reject {param($v) $v.Enumeration.Cases[0].NameLength=24} 'name length matches the fixed case'
foreach($bad in @('8',$true,[double]8,[decimal]8,$null)) {Reject {param($v) $v.Enumeration.Cases[0].NameLength=$bad} 'target numeric type strict'}
foreach($bad in @('decoded','FixupMismatch','Unknown','private-marker',$true,$null)) {Reject {param($v) $v.Enumeration.Cases[0].RawCode=$bad} 'known target must strictly decode'}
foreach($field in @('IdentityMatched','LogicalMatched','LinksMatched','NamesMatched')) {
    foreach($bad in @($false,'true',1,$null,@($true))) {Reject {param($v) $v.Enumeration.Cases[0].$field=$bad} ('target proof boolean strict '+$field)}
}
Reject {param($v) $v.Enumeration.Cases[0].AllocationCheck='Unknown'} 'ordinary target allocation match retained'
Reject {param($v) $v.Enumeration.Cases[3].AllocationCheck='Matched'} 'special target allocation uncertainty retained'
foreach($rawJson in @('{"Closed":true,"Closed":true}','{"Closed":true,"closed":true}',
    '{"Enumeration":{"VisitedSlots":64,"visitedSlots":64}}','{"Enumeration":{"Cases":[{"CaseId":"A","caseid":"B"}]}}',
    '{"Enumeration":{"CodeCounts":[{"Status":"Invalid","status":"Invalid"}]}}',
    '{"Enumeration":{"CodeCounts":[{"Code":"Signature","code":"Signature"}]}}')) {
    $message=$null;try {Assert-UniqueJsonKeys $rawJson} catch {$message=$_.Exception.Message}
    Check ($message -ceq 'MATRIX_JSON_INVALID') 'shared duplicate-key guard rejects case variants at every depth'
}
$legacy=Fixture; $legacy.Revision='bounded-mft-source-v1'
$legacy.Enumeration.PSObject.Properties.Remove('AllZeroInvalid'); $legacy.Enumeration.PSObject.Properties.Remove('CodeCounts')
RejectValue $legacy 'v1 without diagnostics is not silently upgraded or reinterpreted'
Reject {param($v) $v.Enumeration.CodeCounts=$null} 'null reason table rejected'
Reject {param($v) $v.Enumeration.CodeCounts=@()} 'empty reason table rejected for nonempty coverage'
Reject {param($v) $v.Enumeration.CodeCounts=$v.Enumeration.CodeCounts[0]} 'scalar reason table rejected'
Reject {param($v) $v.Enumeration.CodeCounts=@($v.Enumeration.CodeCounts[0])*129} 'reason table has a hard bounded item count'
Reject {param($v) $v.Enumeration.CodeCounts[0]=$null} 'null reason row rejected'
Reject {param($v) $v.Enumeration.CodeCounts[0]=@{Status='Decoded';Code='Decoded';Count=40}} 'hashtable reason row rejected'
foreach($field in @('Status','Code','Count')) {Reject {param($v) $v.Enumeration.CodeCounts[0].PSObject.Properties.Remove($field)} ('missing reason field '+$field)}
foreach($field in @('Path','Reference','SlotId','Raw','Sha256','Exception','Message','SourceToken')) {
    Reject {param($v) $v.Enumeration.CodeCounts[0]|Add-Member -NotePropertyName $field -NotePropertyValue 'private-marker'} ('reason injection '+$field)
}
Reject {param($v) $v.Enumeration.CodeCounts[0].PSObject.Properties.Remove('Status');$v.Enumeration.CodeCounts[0]|Add-Member -NotePropertyName status -NotePropertyValue 'Decoded'} 'reason field casing exact'
Reject {param($v) $v.Enumeration.CodeCounts[0].PSObject.Properties.Remove('Count');$v.Enumeration.CodeCounts[0]|Add-Member -MemberType ScriptProperty -Name Count -Value {throw 'private-marker'}} 'active reason property rejected before evaluation'
Reject {param($v) $v.Enumeration|Add-Member -NotePropertyName InvalidAllZero -NotePropertyValue 2} 'alternate all-zero field spelling rejected'
Reject {param($v) $v.Enumeration|Add-Member -NotePropertyName Reasons -NotePropertyValue @()} 'alternate reason table spelling rejected'
foreach($bad in @('decoded','Unknown',$true,1,$null,@('Decoded'))) {Reject {param($v) $v.Enumeration.CodeCounts[0].Status=$bad} 'reason status type and enum exact'}
foreach($bad in @('decoded','Signature','private-marker',"Decoded`nprivate-marker",$true,1,$null,@('Decoded'))) {Reject {param($v) $v.Enumeration.CodeCounts[0].Code=$bad} 'reason code type and status pairing exact'}
Reject {param($v) $v.Enumeration.CodeCounts[2].Code='AttributeType'} 'Unsupported code cannot be relabeled Invalid'
Reject {param($v) $v.Enumeration.CodeCounts[3].Code='FixupMismatch'} 'Invalid code cannot be relabeled Unsupported'
foreach($bad in @(-1,0,4097,[long]::MaxValue,'40',$true,[double]40,[decimal]40,$null,@(40))) {Reject {param($v) $v.Enumeration.CodeCounts[0].Count=$bad} 'reason count positive bounded integer'}
Reject {param($v) $v.Enumeration.CodeCounts[0].Count=39} 'reason counts sum to the visited range'
Reject {param($v) $v.Enumeration.CodeCounts[1].Count=15;$v.Enumeration.CodeCounts[2].Count=5} 'reason status subtotals match the four strict classes even when total matches'
Reject {param($v) $v.Enumeration.CodeCounts=@(
    [pscustomobject]@{Status='Decoded';Code='Decoded';Count=20},[pscustomobject]@{Status='Decoded';Code='Decoded';Count=20})+@($v.Enumeration.CodeCounts[1..3])} 'split duplicate pair cannot conceal itself behind valid totals'
Reject {param($v) $v.Enumeration.CodeCounts=@($v.Enumeration.CodeCounts[0],$v.Enumeration.CodeCounts[1],$v.Enumeration.CodeCounts[3],$v.Enumeration.CodeCounts[2])} 'reason status order follows enum rather than report field order'
Reject {param($v) $v.Enumeration.CodeCounts=@($v.Enumeration.CodeCounts[0],$v.Enumeration.CodeCounts[1],
    [pscustomobject]@{Status='Invalid';Code='Signature';Count=3},[pscustomobject]@{Status='Invalid';Code='DataSize';Count=1},$v.Enumeration.CodeCounts[3])} 'within-status codes require strict ordinal order'
Reject {param($v) $v.Enumeration.AllZeroInvalid=5} 'all-zero count is bounded by strict Invalid count'
Reject {param($v) $v.Enumeration.AllZeroInvalid=2;$v.Enumeration.CodeCounts=@($v.Enumeration.CodeCounts[0],$v.Enumeration.CodeCounts[1],
    [pscustomobject]@{Status='Invalid';Code='FixupMismatch';Count=3},[pscustomobject]@{Status='Invalid';Code='Signature';Count=1},$v.Enumeration.CodeCounts[3])} 'all-zero count cannot exceed Invalid Signature subset'
Reject {param($v) $v.Enumeration.CodeCounts[2].Code='FixupMismatch'} 'all-zero observation requires the Signature subset instead of zero reclassification'
Write-Output ('PASS NTFS MFT enumeration evidence checks: '+$checks)
