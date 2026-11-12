#requires -Version 7.4
# Pure synthetic summary checks. No source owner, volume, VHD or raw bytes are opened.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:checks++; Write-Output ('PASS NTFS bitmap bootstrap evidence ' + $Name)
}
function Load-PureFunction([string]$RelativePath, [string]$Name, [bool]$OnlyFunction = $true) {
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath (Join-Path $PSScriptRoot $RelativePath)).Path,[ref]$tokens,[ref]$errors)
    Check ($errors.Count -eq 0) ('syntax '+$Name)
    $functions=@($ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -ceq $Name})
    Check ($functions.Count -eq 1 -and (-not $OnlyFunction -or $ast.EndBlock.Statements.Count -eq 1)) ('pure function '+$Name)
    return $functions[0].Extent.Text
}
. ([scriptblock]::Create((Load-PureFunction '../scripts/ntfs-prototype/BitmapBootstrapEvidence.ps1' 'ConvertTo-BitmapBootstrapEvidence' | Select-Object -Last 1)))
. ([scriptblock]::Create((Load-PureFunction '../scripts/ntfs-prototype/MftEnumerationEvidence.ps1' 'ConvertTo-MftEnumerationEvidence' | Select-Object -Last 1)))
. ([scriptblock]::Create((Load-PureFunction '../scripts/ntfs-prototype/KernelMatrixEvidence.ps1' 'Assert-UniqueJsonKeys' $false | Select-Object -Last 1)))

function Fixture([string]$Shape='ResidentCapacityOnly') {
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
    $enumeration=[pscustomobject]@{ValidBytes=[long]65536;ExpectedSlots=64;VisitedSlots=64;Decoded=40;Free=16;Unsupported=4;Invalid=4;
        LookupRequired=20;UnknownLogical=2;UnknownAllocated=16;Pages=1;CompletedCursor=$true;MatchedObjects=21;
        UnknownAllocationObjects=14;MftRuns=1;ReadCalls=2;RequestedBytes=[long]66560;PhysicalSectorBytes=512;
        LockAcquired=$true;UnlockSucceeded=$true;PostNativeStable=$true;Cases=$cases;AllZeroInvalid=2;CodeCounts=@(
            [pscustomobject]@{Status='Decoded';Code='Decoded';Count=40},[pscustomobject]@{Status='Free';Code='FreeRecord';Count=16},
            [pscustomobject]@{Status='Invalid';Code='Signature';Count=4},[pscustomobject]@{Status='Unsupported';Code='AttributeType';Count=4})}
    $range=[pscustomobject]@{Revision='bounded-mft-source-v2';EvidenceKind='NativeOwnedFixture';GuardOwned=$true;
        TokenBound=$true;MapBound=$true;Closed=$true;RejectedReads=0;Enumeration=$enumeration}
    $bitmap=[pscustomobject]@{Status=$Shape;Code='BitmapResidentComplete';ExpectedSlots=64;CoveredSlots=64;PayloadBytes=8;NonresidentMetadata=$null}
    switch -CaseSensitive ($Shape) {
        'Missing' {$bitmap.Code='BitmapMissing';$bitmap.CoveredSlots=0;$bitmap.PayloadBytes=0}
        'ResidentPartial' {$bitmap.Code='BitmapResidentPartial';$bitmap.CoveredSlots=8;$bitmap.PayloadBytes=1}
        'Deferred' {$bitmap.Code='BitmapNonresident';$bitmap.CoveredSlots=0;$bitmap.PayloadBytes=0;
            $bitmap.NonresidentMetadata=[pscustomobject]@{LogicalBytes=[long]512;InitializedBytes=[long]512;AllocatedBytes=[long]4096}}
    }
    return [pscustomobject]@{Revision='bitmap-bootstrap-source-v1';Stage='ObservedShapeOnly';Range=$range;Bitmap=$bitmap}
}
function Reject([scriptblock]$Mutation, [string]$Name, [string]$Shape='ResidentCapacityOnly') {
    $value=Fixture $Shape; & $Mutation $value
    $message=$null; try { ConvertTo-BitmapBootstrapEvidence $value | Out-Null } catch {$message=$_.Exception.Message}
    Check ($message -ceq 'BITMAP_BOOTSTRAP_EVIDENCE_INVALID') $Name
}
function RejectValue($Value,[string]$Name) {
    $message=$null; try { ConvertTo-BitmapBootstrapEvidence $Value | Out-Null } catch {$message=$_.Exception.Message}
    Check ($message -ceq 'BITMAP_BOOTSTRAP_EVIDENCE_INVALID') $Name
}
function Set-Slots($Value,[int]$Slots) {
    $range=$Value.Range.Enumeration
    $range.ValidBytes=[long]$Slots*1024;$range.ExpectedSlots=$Slots;$range.VisitedSlots=$Slots
    $range.Decoded=22;$range.Free=$Slots-22;$range.Invalid=0;$range.Unsupported=0
    $range.LookupRequired=16;$range.UnknownLogical=0;$range.UnknownAllocated=14;$range.AllZeroInvalid=0
    $range.Pages=[int][Math]::Ceiling($Slots/64);$range.ReadCalls=$range.Pages+1;$range.RequestedBytes=$range.ValidBytes+1024
    $range.CodeCounts=@([pscustomobject]@{Status='Decoded';Code='Decoded';Count=22})
    if($range.Free -gt 0) {$range.CodeCounts+=@([pscustomobject]@{Status='Free';Code='FreeRecord';Count=$range.Free})}
    $Value.Bitmap.ExpectedSlots=$Slots
}

foreach($shape in @('Missing','ResidentPartial','ResidentCapacityOnly','Deferred')) {
    $safe=ConvertTo-BitmapBootstrapEvidence (Fixture $shape)
    Check ($safe.Bitmap.Status -ceq $shape -and $safe.Stage -ceq 'ObservedShapeOnly') 'each shape retains observation-only semantics'
    Check ($safe.Range.Revision -ceq 'bounded-mft-source-v2' -and $safe.Range.EvidenceKind -ceq 'NativeOwnedFixture' -and
        $safe.Range.Enumeration.UnknownAllocationObjects -eq 14) 'old range uncertainty and native-source contract remain intact'
}
$safe=ConvertTo-BitmapBootstrapEvidence (Fixture)
Check (($safe.Keys -join ',') -ceq 'Revision,Stage,Range,Bitmap') 'canonical independent envelope'
Check (($safe.Bitmap.Keys -join ',') -ceq 'Status,Code,ExpectedSlots,CoveredSlots,PayloadBytes,NonresidentMetadata') 'canonical bitmap shape fields'
Check ($safe.Bitmap.Status -ceq 'ResidentCapacityOnly' -and $safe.Bitmap.Code -ceq 'BitmapResidentComplete') 'internal Complete is renamed to capacity-only public status'
Check ($safe.Range.Enumeration.ReadCalls -eq 2 -and $safe.Range.Enumeration.RequestedBytes -eq 66560) 'observation adds no synthetic read count or budget'
$input=Fixture 'Deferred';$safe=ConvertTo-BitmapBootstrapEvidence $input
Check (($safe.Bitmap.NonresidentMetadata.Keys -join ',') -ceq 'LogicalBytes,InitializedBytes,AllocatedBytes') 'canonical bounded nonresident lengths'
$input.Bitmap.NonresidentMetadata.LogicalBytes=0;$input.Range.Enumeration.CodeCounts[0].Count=1
Check ($safe.Bitmap.NonresidentMetadata.LogicalBytes -eq 512 -and $safe.Range.Enumeration.CodeCounts[0].Count -eq 40) 'deep copies do not alias nested input'
$safe.Bitmap.NonresidentMetadata.AllocatedBytes=8192
Check ($input.Bitmap.NonresidentMetadata.AllocatedBytes -eq 4096) 'output mutation does not change input metadata'
$safe=ConvertTo-BitmapBootstrapEvidence (Fixture 'Deferred')
$json=$safe | ConvertTo-Json -Depth 12
Assert-UniqueJsonKeys $json
Check ((ConvertTo-BitmapBootstrapEvidence ($json | ConvertFrom-Json)).Bitmap.Code -ceq 'BitmapNonresident') 'deep JSON roundtrip'
Check ($json -cnotmatch 'PayloadBody|CopyPayload|RawRecord|SourceToken|MapEpoch|VolumeGuid|DevicePath|Reference|Sha256|Lcn|RunOffset|AllocatedRecords|VerifiedAllocation') 'output excludes bytes identities addresses and actual-allocation claims'

foreach($code in @('BitmapSeedUnsupported','BitmapExtent','BitmapMultiple','BitmapNamed','BitmapFlags','BitmapResidentPayloadBudget','BitmapNonresidentPayloadBudget','BitmapNonresident')) {
    $value=Fixture 'Deferred';$value.Bitmap.Code=$code
    if($code -cne 'BitmapNonresident') {$value.Bitmap.NonresidentMetadata=$null}
    Check ((ConvertTo-BitmapBootstrapEvidence $value).Bitmap.Code -ceq $code) 'each fixed Deferred code is accepted only without inferred allocation'
}
foreach($code in @('BitmapResidentPayloadBudget','BitmapNonresidentPayloadBudget')) {
    $value=Fixture 'Deferred';$value.Bitmap.Code=$code;$value.Bitmap.NonresidentMetadata=$null
    $safe=ConvertTo-BitmapBootstrapEvidence $value
    Check ($safe.Bitmap.Status -ceq 'Deferred' -and $safe.Bitmap.CoveredSlots -eq 0 -and $safe.Bitmap.PayloadBytes -eq 0 -and
        $null -eq $safe.Bitmap.NonresidentMetadata) 'resident and nonresident budget codes preserve shape without inferred payload or lengths'
    Reject {param($v) $v.Bitmap.Code=$code;$v.Bitmap.NonresidentMetadata=$null;$v.Bitmap.PayloadBytes=1} 'over-budget shape cannot publish partial payload length' 'Deferred'
    Reject {param($v) $v.Bitmap.Code=$code;$v.Bitmap.NonresidentMetadata=$null;$v.Bitmap.CoveredSlots=1} 'over-budget shape cannot claim covered slots' 'Deferred'
}
Reject {param($v) $v.Bitmap.Code='BitmapPayloadBudget';$v.Bitmap.NonresidentMetadata=$null} 'generic budget code cannot hide resident versus nonresident form' 'Deferred'
foreach($slots in @(22,64,4096)) {
    $value=Fixture;Set-Slots $value $slots
    $value.Bitmap.PayloadBytes=[int][Math]::Ceiling($slots/8);$value.Bitmap.CoveredSlots=$slots
    Check ((ConvertTo-BitmapBootstrapEvidence $value).Bitmap.CoveredSlots -eq $slots) 'complete resident capacity accepts exact bounded endpoints'
    $value.Bitmap.PayloadBytes=512
    Check ((ConvertTo-BitmapBootstrapEvidence $value).Bitmap.CoveredSlots -eq $slots) 'excess resident capacity is clamped to declared range'
    $value=Fixture 'ResidentPartial';Set-Slots $value $slots;$value.Bitmap.PayloadBytes=0;$value.Bitmap.CoveredSlots=0
    Check ((ConvertTo-BitmapBootstrapEvidence $value).Bitmap.Status -ceq 'ResidentPartial') 'empty resident body stays partial rather than known zero'
    $value.Bitmap.PayloadBytes=[int][Math]::Floor(($slots-1)/8);$value.Bitmap.CoveredSlots=$value.Bitmap.PayloadBytes*8
    Check ((ConvertTo-BitmapBootstrapEvidence $value).Bitmap.CoveredSlots -lt $slots) 'largest partial byte capacity stays explicitly incomplete'
}
foreach($triple in @(@(0,0,0),@(1,0,4096),@(511,511,4096),@(512,0,4096),@(512,512,268435456))) {
    $value=Fixture 'Deferred';$item=$value.Bitmap.NonresidentMetadata
    $item.LogicalBytes=[long]$triple[0];$item.InitializedBytes=[long]$triple[1];$item.AllocatedBytes=[long]$triple[2]
    Check ((ConvertTo-BitmapBootstrapEvidence $value).Bitmap.NonresidentMetadata.AllocatedBytes -eq $triple[2]) 'nonresident metadata accepts finite length and fixture-volume endpoints'
}

RejectValue $null 'null envelope rejected'
RejectValue @{} 'hashtable envelope rejected'
RejectValue @((Fixture),(Fixture)) 'array envelope rejected'
foreach($field in @('Revision','Stage','Range','Bitmap')) { Reject {param($v) $v.PSObject.Properties.Remove($field)} ('missing envelope '+$field) }
foreach($field in @('Status','Code','ExpectedSlots','CoveredSlots','PayloadBytes','NonresidentMetadata')) { Reject {param($v) $v.Bitmap.PSObject.Properties.Remove($field)} ('missing shape '+$field) }
foreach($field in @('LogicalBytes','InitializedBytes','AllocatedBytes')) { Reject {param($v) $v.Bitmap.NonresidentMetadata.PSObject.Properties.Remove($field)} ('missing nonresident length '+$field) 'Deferred' }
foreach($bad in @('bitmap-bootstrap-source-v2','BITMAP-BOOTSTRAP-SOURCE-V1','bounded-mft-source-v2',1,$true,$null)) { Reject {param($v) $v.Revision=$bad} 'revision value and type strict' }
foreach($bad in @('VerifiedAllocation','SnapshotComplete','Observedshapeonly','Synthetic','',1,$true,$null)) { Reject {param($v) $v.Stage=$bad} 'stage cannot claim allocation or snapshot completion' }
foreach($bad in @('Invalid','ResidentComplete','Unknown','residentcapacityonly','BitmapResidentComplete','',1,$true,$null)) { Reject {param($v) $v.Bitmap.Status=$bad} 'unknown invalid internal or mistyped status rejected' }
foreach($bad in @('BitmapResidentPartial','BitmapMissing','BitmapNonresident','SyntheticPrivateCode','bitmapresidentcomplete','',1,$true,$null)) { Reject {param($v) $v.Bitmap.Code=$bad} 'resident status-code pair exact' }
foreach($shape in @('Missing','ResidentPartial','Deferred')) {
    Reject {param($v) $v.Bitmap.Code='BitmapResidentComplete'} 'cross-shape code cannot reuse complete label' $shape
}
foreach($code in @('BitmapInput','BitmapGeometry','BitmapSlotBudget','BitmapSeedInvalid','BitmapAttributeBounds','BitmapNonresidentBounds')) {
    Reject {param($v) $v.Bitmap.Status='Invalid';$v.Bitmap.Code=$code} 'Invalid locator result never enters successful shape summary'
    Reject {param($v) $v.Bitmap.Code=$code} 'Deferred cannot launder an Invalid locator code' 'Deferred'
}
foreach($field in @('ExpectedSlots','CoveredSlots','PayloadBytes')) {
    foreach($bad in @(-1,[long]::MaxValue,'1',$true,[double]1,[decimal]1,$null)) { Reject {param($v) $v.Bitmap.$field=$bad} ('shape integer type and budget '+$field) }
}
foreach($bad in @(0,21,63,65,4097)) { Reject {param($v) $v.Bitmap.ExpectedSlots=$bad} 'expected slots bind the exact range' }
foreach($bad in @(0,63,65,4097)) { Reject {param($v) $v.Bitmap.CoveredSlots=$bad} 'complete coverage equals capacity and range' }
foreach($bad in @(0,7,513)) { Reject {param($v) $v.Bitmap.PayloadBytes=$bad} 'complete payload byte budget and capacity checked' }
Reject {param($v) $v.Bitmap.CoveredSlots=7} 'partial coverage cannot omit a declared byte bit' 'ResidentPartial'
Reject {param($v) $v.Bitmap.PayloadBytes=8;$v.Bitmap.CoveredSlots=64} 'partial status cannot hide complete capacity' 'ResidentPartial'
Reject {param($v) $v.Bitmap.PayloadBytes=0;$v.Bitmap.CoveredSlots=1} 'zero-byte body cannot claim positive coverage' 'ResidentPartial'
foreach($shape in @('Missing','Deferred')) {
    Reject {param($v) $v.Bitmap.CoveredSlots=1} 'unextracted shape cannot claim coverage' $shape
    Reject {param($v) $v.Bitmap.PayloadBytes=1} 'unextracted shape cannot publish payload length' $shape
}
foreach($shape in @('Missing','ResidentPartial','ResidentCapacityOnly')) {
    Reject {param($v) $v.Bitmap.NonresidentMetadata=[pscustomobject]@{LogicalBytes=1;InitializedBytes=1;AllocatedBytes=4096}} 'resident or missing shape cannot attach nonresident lengths' $shape
}
foreach($code in @('BitmapSeedUnsupported','BitmapExtent','BitmapMultiple','BitmapNamed','BitmapFlags','BitmapResidentPayloadBudget','BitmapNonresidentPayloadBudget')) {
    Reject {param($v) $v.Bitmap.Code=$code} 'other Deferred codes cannot carry nonresident metadata' 'Deferred'
}
Reject {param($v) $v.Bitmap | Add-Member -NotePropertyName Form -NotePropertyValue 'Nonresident'} 'internal form cannot extend the six-field public bitmap contract'
foreach($bad in @($null,@{},@([pscustomobject]@{LogicalBytes=1;InitializedBytes=1;AllocatedBytes=4096}),@([pscustomobject]@{},[pscustomobject]@{}))) {
    Reject {param($v) $v.Bitmap.NonresidentMetadata=$bad} 'nonresident metadata requires one exact object' 'Deferred'
}
foreach($field in @('LogicalBytes','InitializedBytes','AllocatedBytes')) {
    foreach($bad in @(-1,[long]::MaxValue,'0',$true,[double]0,[decimal]0,$null)) { Reject {param($v) $v.Bitmap.NonresidentMetadata.$field=$bad} ('nonresident integer type and budget '+$field) 'Deferred' }
}
Reject {param($v) $v.Bitmap.NonresidentMetadata.LogicalBytes=513} 'nonresident logical byte 513 rejected' 'Deferred'
Reject {param($v) $v.Bitmap.NonresidentMetadata.InitializedBytes=513} 'initialized length cannot exceed logical length' 'Deferred'
foreach($bad in @(0,511,512,4095,4097,268435457)) { Reject {param($v) $v.Bitmap.NonresidentMetadata.AllocatedBytes=$bad} 'allocation must cover logical length within aligned fixture volume' 'Deferred' }

foreach($level in @('Envelope','Bitmap','Metadata','Range','Enumeration','Case','CodeCount')) {
    foreach($field in @('Raw','Payload','SourceToken','MapEpoch','Reference','Path','Lcn','Sha256','VerifiedAllocation')) {
        Reject {
            param($v)
            $object=switch($level) {'Envelope' {$v};'Bitmap' {$v.Bitmap};'Metadata' {$v.Bitmap.NonresidentMetadata};
                'Range' {$v.Range};'Enumeration' {$v.Range.Enumeration};'Case' {$v.Range.Enumeration.Cases[0]};'CodeCount' {$v.Range.Enumeration.CodeCounts[0]}}
            $object | Add-Member -NotePropertyName $field -NotePropertyValue 'SyntheticPrivateMarker'
        } ('extra private or proof field rejected at '+$level) 'Deferred'
    }
}
foreach($level in @('Envelope','Bitmap','Metadata')) {
    Reject {
        param($v)
        $object=switch($level) {'Envelope' {$v};'Bitmap' {$v.Bitmap};'Metadata' {$v.Bitmap.NonresidentMetadata}}
        $field=switch($level) {'Envelope' {'Stage'};'Bitmap' {'Status'};'Metadata' {'LogicalBytes'}}
        $old=$object.$field;$object.PSObject.Properties.Remove($field)
        $object | Add-Member -NotePropertyName $field.ToLowerInvariant() -NotePropertyValue $old
    } ('field casing exact at '+$level) 'Deferred'
    $script:accessed=$false
    Reject {
        param($v)
        $object=switch($level) {'Envelope' {$v};'Bitmap' {$v.Bitmap};'Metadata' {$v.Bitmap.NonresidentMetadata}}
        $field=switch($level) {'Envelope' {'Stage'};'Bitmap' {'Status'};'Metadata' {'LogicalBytes'}}
        $object.PSObject.Properties.Remove($field)
        $object | Add-Member -MemberType ScriptProperty -Name $field -Value {$script:accessed=$true;throw 'SyntheticPrivateMarker'}
    } ('active property rejected at '+$level) 'Deferred'
    Check (-not $script:accessed) 'property shape checked before invoking active accessor'
}
foreach($bad in @('SyntheticFixture','NativeOwnedFixture-Private','nativeownedfixture','ValidatedImmutable','VSS',$true,$null)) { Reject {param($v) $v.Range.EvidenceKind=$bad} 'synthetic or unrecognized evidence cannot claim Native source' }
Reject {param($v) $v.Range.EvidenceKind='SyntheticFixture';$v.Range.GuardOwned=$false} 'actual synthetic factory shape rejected even with valid bitmap metadata'
foreach($field in @('GuardOwned','TokenBound','MapBound','Closed')) {
    foreach($bad in @($false,'true',1,$null)) { Reject {param($v) $v.Range.$field=$bad} ('source lifecycle assertion strict '+$field) }
}
foreach($bad in @('bounded-mft-source-v1','bounded-mft-source-v3','bitmap-bootstrap-source-v1')) { Reject {param($v) $v.Range.Revision=$bad} 'old or unrelated range versions cannot mix' }
foreach($field in @('LockAcquired','UnlockSucceeded','PostNativeStable','CompletedCursor')) { Reject {param($v) $v.Range.Enumeration.$field=$false} ('range failure cannot enter observation success '+$field) }
Reject {param($v) $v.Range.Enumeration.VisitedSlots=63} 'incomplete range cannot be hidden by sufficient bitmap capacity'
Reject {param($v) $v.Range.Enumeration.RequestedBytes++} 'new shape cannot extend old raw byte budget'
Reject {param($v) $v.Range.Enumeration.ReadCalls=257} 'new shape cannot extend old read-call budget'
Reject {param($v) $v.Range.Enumeration.Cases[0].IdentityMatched=$false} 'target mismatch rejects whole observation'
Reject {param($v) $v.Range.Enumeration.Cases[3].AllocationCheck='Matched'} 'unknown special allocation cannot become known through shape observation'
Reject {param($v) $v.Range.Enumeration.CodeCounts[0].Code='SyntheticPrivateMarker'} 'unknown decoder code remains rejected by unchanged v2 converter'
Reject {param($v) $v.Range.Enumeration.AllZeroInvalid=5} 'all-zero observation cannot exceed strict Signature evidence'

# Duplicate keys must be rejected before ConvertFrom-Json can collapse them.
$duplicates=@(
    '{"Revision":"a","Revision":"b"}', '{"Stage":"a","stage":"b"}',
    '{"Bitmap":{"Status":"a","status":"b"}}', '{"Bitmap":{"Code":"a","Code":"b"}}',
    '{"Bitmap":{"NonresidentMetadata":{"LogicalBytes":0,"logicalBytes":1}}}',
    '{"Range":{"EvidenceKind":"NativeOwnedFixture","evidenceKind":"SyntheticFixture"}}',
    '{"Range":{"Enumeration":{"ExpectedSlots":64,"expectedSlots":65}}}')
foreach($duplicate in $duplicates) {
    $message=$null;try {Assert-UniqueJsonKeys $duplicate} catch {$message=$_.Exception.Message}
    Check ($null -ne $message -and $message -notmatch 'SyntheticPrivateMarker') 'duplicate or case-variant JSON keys rejected before conversion'
}
Write-Output ('RESULT: PASS ('+$checks+' checks)')
