#requires -Version 7.4
# Pure-memory report checks; never opens a volume, source adapter or real fixture.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:checks++; Write-Output ('PASS NTFS source evidence ' + $Name)
}
function Load-PureFunction([string]$Relative, [string]$Name, [int]$ExpectedFunctions) {
    $path = Join-Path $PSScriptRoot $Relative
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $path).Path, [ref]$tokens, [ref]$errors)
    Check ($errors.Count -eq 0) ('pure script syntax ' + $Name)
    $definitions = @($ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.FunctionDefinitionAst]})
    Check ($definitions.Count -eq $ExpectedFunctions -and $ast.EndBlock.Statements.Count -eq $ExpectedFunctions) ('no executable top-level operations ' + $Name)
    $selected = @($definitions | Where-Object Name -CEQ $Name)
    Check ($selected.Count -eq 1) ('identify pure function ' + $Name)
    return [scriptblock]::Create($selected[0].Extent.Text)
}
# Dot-source in this script scope, not in Load-PureFunction's local scope.
. (Load-PureFunction '../scripts/ntfs-prototype/RawComparisonEvidence.ps1' 'ConvertTo-RawComparisonEvidence' 1 | Select-Object -Last 1)
. (Load-PureFunction '../scripts/ntfs-prototype/SourceAdapterEvidence.ps1' 'ConvertTo-SourceAdapterEvidence' 1 | Select-Object -Last 1)
. (Load-PureFunction '../scripts/ntfs-prototype/KernelMatrixEvidence.ps1' 'Assert-UniqueJsonKeys' 2 | Select-Object -Last 1)

function Fixture {
    $cases = @(
        foreach ($phase in @('A','B','C')) {
            $lengths = if ($phase -ceq 'A') {@(8,24,64)} else {@(8,64)}
            foreach ($kind in @('Ordinary','Compressed','Sparse')) {
                foreach ($length in $lengths) {
                    [pscustomobject]@{CaseId="$phase-$kind-$length";Kind=$kind;NameLength=$length;
                        KernelCode=$(if ($length -eq 64) {'FixupMismatch'} else {'Decoded'});RawCode='Decoded';
                        IdentityMatched=$true;LogicalMatched=$true;LinksMatched=$true;NamesMatched=$true;
                        AllocationCheck=$(if ($kind -ceq 'Ordinary') {'Matched'} else {'Unknown'})}
                }
            }
        }
    )
    $comparison = [pscustomobject]@{Revision='raw-records-v1';Objects=21;KernelRequests=63;KernelDecoded=36;KernelRejected=27;
        RawRecords=22;RawDecoded=22;MatchedObjects=21;UnknownAllocationObjects=14;MftRuns=1;ReadCalls=22;
        RequestedBytes=22528;PhysicalSectorBytes=512;LockAcquired=$true;UnlockSucceeded=$true;PostNativeStable=$true;Cases=$cases}
    return [pscustomobject]@{Revision='fixture-source-v1';EvidenceKind='NativeOwnedFixture';GuardOwned=$true;
        TokenBound=$true;MapBound=$true;Closed=$true;RejectedReads=0;Comparison=$comparison}
}
function Reject([scriptblock]$Mutation, [string]$Name) {
    $value = Fixture; & $Mutation $value
    $message = $null
    try { ConvertTo-SourceAdapterEvidence $value | Out-Null } catch {$message=$_.Exception.Message}
    Check ($message -ceq 'SOURCE_ADAPTER_EVIDENCE_INVALID') $Name
}
function RejectValue($Value, [string]$Name) {
    $message = $null
    try { ConvertTo-SourceAdapterEvidence $Value | Out-Null } catch {$message=$_.Exception.Message}
    Check ($message -ceq 'SOURCE_ADAPTER_EVIDENCE_INVALID') $Name
}

$value = Fixture; $safe = ConvertTo-SourceAdapterEvidence $value
Check (($safe.Keys -join ',') -ceq 'Revision,EvidenceKind,GuardOwned,TokenBound,MapBound,Closed,RejectedReads,Comparison') 'exact canonical source fields'
Check ($safe.Comparison.RawDecoded -eq 22 -and $safe.Comparison.MatchedObjects -eq 21 -and $safe.Comparison.UnknownAllocationObjects -eq 14) 'nested finite fixture contract preserved'
Check (@($safe.Comparison.Cases | Where-Object AllocationCheck -CEQ 'Unknown').Count -eq 14) 'special allocations stay unknown in nested output'
Check ($safe.RejectedReads -eq 0) 'zero observed rejections is a count rather than a claim of exercised rejection paths'
$value.RejectedReads=99; $value.Comparison.Cases[0].CaseId='private-marker'; $value.Comparison.KernelRejected=0
Check ($safe.RejectedReads -eq 0 -and $safe.Comparison.KernelRejected -eq 27 -and $safe.Comparison.Cases[0].CaseId -ceq 'A-Ordinary-8') 'nested output is independent of caller mutations'
$safe.Comparison.Cases[0].CaseId='different-marker'
Check ($value.Comparison.Cases[0].CaseId -ceq 'private-marker') 'output mutation cannot alter input objects'
$value=Fixture; $safe=ConvertTo-SourceAdapterEvidence $value
$json=$safe|ConvertTo-Json -Depth 8
Assert-UniqueJsonKeys $json
Check ((ConvertTo-SourceAdapterEvidence ($json|ConvertFrom-Json)).Comparison.Cases.Count -eq 21) 'deep evidence JSON roundtrip'
Check ($json -cnotmatch 'private-marker|different-marker|SourceToken|MapEpoch|Reference|Sha256|VolumeGuid|DevicePath') 'canonical synthetic output contains no original source identities or test injection text'
foreach ($count in @(0,1,128,[long]128)) {
    $value=Fixture; $value.RejectedReads=$count
    Check ((ConvertTo-SourceAdapterEvidence $value).RejectedReads -eq $count) 'rejection count bounded endpoints accepted'
}
RejectValue $null 'null source report rejected'
RejectValue @{} 'hashtable source report rejected'
RejectValue @((Fixture),(Fixture)) 'array source report rejected'
foreach ($field in @('Revision','EvidenceKind','GuardOwned','TokenBound','MapBound','Closed','RejectedReads','Comparison')) {
    Reject {param($v) $v.PSObject.Properties.Remove($field)} ('missing source field ' + $field)
}
foreach ($field in @('SourceToken','Token','MapEpoch','Epoch','Path','Volume','Reference','Identity','Raw','Sha256','Exception','Skipped')) {
    Reject {param($v) $v | Add-Member -NotePropertyName $field -NotePropertyValue 'private-marker'} ('source identity injection ' + $field)
    Reject {param($v) $v.Comparison | Add-Member -NotePropertyName $field -NotePropertyValue 'private-marker'} ('nested identity injection ' + $field)
    Reject {param($v) $v.Comparison.Cases[0] | Add-Member -NotePropertyName $field -NotePropertyValue 'private-marker'} ('case identity injection ' + $field)
}
Reject {param($v) $v.PSObject.Properties.Remove('GuardOwned');$v | Add-Member -NotePropertyName guardOwned -NotePropertyValue $true} 'source field casing exact'
Reject {param($v) $v.PSObject.Properties.Remove('Closed');$v | Add-Member -MemberType ScriptProperty -Name Closed -Value {throw 'private-marker'}} 'active source property rejected without evaluation'
foreach ($bad in @('fixture-source-v2','FIXTURE-SOURCE-V1',$true,1,$null,@('fixture-source-v1'))) {
    Reject {param($v) $v.Revision=$bad} 'revision value and type exact'
}
foreach ($bad in @('SyntheticFixture','NativeOwnedFixture-Private','nativeownedfixture','ValidatedImmutable','VSS',$true,1,$null)) {
    Reject {param($v) $v.EvidenceKind=$bad} 'synthetic or unknown evidence cannot mint a real fixture report'
}
Reject {param($v) $v.EvidenceKind='SyntheticFixture';$v.GuardOwned=$false} 'actual synthetic test-report shape is rejected by native success converter'
foreach ($field in @('GuardOwned','TokenBound','MapBound','Closed')) {
    foreach ($bad in @($false,'true',1,$null,@($true))) {Reject {param($v) $v.$field=$bad} ('required source boolean ' + $field)}
}
foreach ($bad in @(-1,129,[long]::MaxValue,'0',$true,[double]0,[decimal]0,$null,@(0))) {
    Reject {param($v) $v.RejectedReads=$bad} 'rejection count range and numeric type strict'
}
Reject {param($v) $v.Comparison=$null} 'missing nested comparison rejected'
Reject {param($v) $v.Comparison=@{}} 'hashtable nested comparison rejected'
Reject {param($v) $v.Comparison.RawDecoded=21} 'incomplete strict raw decoding cannot publish source success'
Reject {param($v) $v.Comparison.MatchedObjects=20} 'missing Native object match cannot publish source success'
Reject {param($v) $v.Comparison.UnknownAllocationObjects=0} 'unknown allocations cannot become known zero'
Reject {param($v) $v.Comparison.ReadCalls=129} 'nested read budget remains bounded'
Reject {param($v) $v.Comparison.RequestedBytes=2097664} 'nested byte budget remains bounded'
foreach ($field in @('LockAcquired','UnlockSucceeded','PostNativeStable')) {
    Reject {param($v) $v.Comparison.$field=$false} ('nested source lifecycle proof required ' + $field)
}
Reject {param($v) $v.Comparison.Cases=$v.Comparison.Cases[0..19]} 'all known targets required'
Reject {param($v) $v.Comparison.Cases[1].CaseId=$v.Comparison.Cases[0].CaseId} 'duplicate target cannot hide a missing target'
Reject {param($v) $v.Comparison.Cases[0].KernelCode='PrivateMarker123'} 'nested kernel code semantic whitelist preserved'
Reject {param($v) $v.Comparison.Cases[0].RawCode='FixupMismatch'} 'nested strict failure cannot be published as source success'
foreach ($field in @('IdentityMatched','LogicalMatched','LinksMatched','NamesMatched')) {
    Reject {param($v) $v.Comparison.Cases[0].$field=$false} ('nested target proof required ' + $field)
}
Reject {param($v) $v.Comparison.Cases[3].AllocationCheck='Matched'} 'special allocation uncertainty survives source wrapping'
foreach ($rawJson in @('{"Closed":true,"Closed":true}', '{"Closed":true,"closed":true}',
    '{"Comparison":{"RawDecoded":22,"rawDecoded":22}}','{"Comparison":{"Cases":[{"CaseId":"A","caseid":"B"}]}}')) {
    $message=$null;try {Assert-UniqueJsonKeys $rawJson} catch {$message=$_.Exception.Message}
    Check ($message -ceq 'MATRIX_JSON_INVALID') 'shared wrapper guard rejects duplicate or case-variant JSON keys at every depth'
}
Write-Output ('PASS NTFS source evidence checks: ' + $checks)
