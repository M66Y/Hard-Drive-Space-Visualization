#requires -Version 7.4
# Pure in-memory evidence checks: no VHD, raw record, device or personal data access.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:checks++; Write-Output ('PASS NTFS raw comparison evidence ' + $Name)
}
$source = Join-Path $PSScriptRoot '../scripts/ntfs-prototype/RawComparisonEvidence.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $source).Path, [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'converter script syntax'
$functions = @($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] })
Check ($functions.Count -eq 1 -and $ast.EndBlock.Statements.Count -eq 1) 'dot source defines only the pure converter'
. ([scriptblock]::Create($functions[0].Extent.Text))
$matrixSource = Join-Path $PSScriptRoot '../scripts/ntfs-prototype/KernelMatrixEvidence.ps1'
$matrixAst = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $matrixSource).Path, [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'shared raw JSON guard syntax'
$uniqueFunction = @($matrixAst.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -ceq 'Assert-UniqueJsonKeys' })
Check ($uniqueFunction.Count -eq 1) 'existing raw JSON uniqueness guard is reused'
. ([scriptblock]::Create($uniqueFunction[0].Extent.Text))

function Fixture {
    $cases = @(
        foreach ($phase in @('A','B','C')) {
            $lengths = if ($phase -ceq 'A') { @(8,24,64) } else { @(8,64) }
            foreach ($kind in @('Ordinary','Compressed','Sparse')) {
                foreach ($length in $lengths) {
                    [pscustomobject]@{CaseId="$phase-$kind-$length";Kind=$kind;NameLength=$length;
                        KernelCode='Decoded';RawCode='Decoded';IdentityMatched=$true;LogicalMatched=$true;
                        LinksMatched=$true;NamesMatched=$true;AllocationCheck=$(if ($kind -ceq 'Ordinary') {'Matched'} else {'Unknown'})}
                }
            }
        }
    )
    [pscustomobject]@{Revision='raw-records-v1';Objects=21;KernelRequests=63;KernelDecoded=63;KernelRejected=0;
        RawRecords=22;RawDecoded=22;MatchedObjects=21;UnknownAllocationObjects=14;MftRuns=1;ReadCalls=22;
        RequestedBytes=22528;PhysicalSectorBytes=512;LockAcquired=$true;UnlockSucceeded=$true;PostNativeStable=$true;Cases=$cases}
}
function Reject([scriptblock]$Mutation, [string]$Name) {
    $value = Fixture; & $Mutation $value
    $message = $null
    try { ConvertTo-RawComparisonEvidence $value | Out-Null } catch { $message = $_.Exception.Message }
    Check ($message -ceq 'RAW_COMPARISON_EVIDENCE_INVALID') $Name
}
function RejectValue($Value, [string]$Name) {
    $message = $null
    try { ConvertTo-RawComparisonEvidence $Value | Out-Null } catch { $message = $_.Exception.Message }
    Check ($message -ceq 'RAW_COMPARISON_EVIDENCE_INVALID') $Name
}

$value = Fixture; $safe = ConvertTo-RawComparisonEvidence $value
Check ($safe.Objects -eq 21 -and $safe.Cases.Count -eq 21 -and $safe.RawDecoded -eq 22) 'complete fixed raw comparison preserved'
Check (($safe.Keys -join ',') -ceq 'Revision,Objects,KernelRequests,KernelDecoded,KernelRejected,RawRecords,RawDecoded,MatchedObjects,UnknownAllocationObjects,MftRuns,ReadCalls,RequestedBytes,PhysicalSectorBytes,LockAcquired,UnlockSucceeded,PostNativeStable,Cases') 'canonical root fields'
Check (($safe.Cases[0].Keys -join ',') -ceq 'CaseId,Kind,NameLength,KernelCode,RawCode,IdentityMatched,LogicalMatched,LinksMatched,NamesMatched,AllocationCheck') 'canonical case fields'
Check (@($safe.Cases | Where-Object AllocationCheck -CEQ 'Unknown').Count -eq 14) 'unknown special allocation is preserved rather than changed to zero or matched'
Check (@($safe.Cases | Where-Object AllocationCheck -CEQ 'Matched').Count -eq 7) 'only seven ordinary allocations are matched'
$value.Cases[0].CaseId = 'private-marker'; $value.KernelDecoded = 0; $value.Cases[1].AllocationCheck = 'Unknown'
Check ($safe.Cases[0].CaseId -ceq 'A-Ordinary-8' -and $safe.KernelDecoded -eq 63 -and $safe.Cases[1].AllocationCheck -ceq 'Matched') 'deep whitelist output is independent of the input'
$json = $safe | ConvertTo-Json -Depth 6
Assert-UniqueJsonKeys $json
Check ((ConvertTo-RawComparisonEvidence ($json | ConvertFrom-Json)).Cases.Count -eq 21) 'unique-key JSON roundtrip'
$mixed = Fixture; $mixed.KernelDecoded = 36; $mixed.KernelRejected = 27
foreach ($row in $mixed.Cases | Where-Object NameLength -EQ 64) { $row.KernelCode = 'FixupMismatch' }
Check ((ConvertTo-RawComparisonEvidence $mixed).KernelRejected -eq 27) 'kernel rejection remains valid raw comparison evidence'
$mixed.KernelDecoded = 42; $mixed.KernelRejected = 21
foreach ($row in $mixed.Cases) { $row.KernelCode = 'Decoded' }
Check ((ConvertTo-RawComparisonEvidence $mixed).KernelDecoded -eq 42) 'last-stage case codes do not replace all-three-stage aggregate counts'
$lower = Fixture; $lower.KernelDecoded = 0; $lower.KernelRejected = 63
foreach ($row in $lower.Cases) { $row.KernelCode = 'FixupMismatch' }
Check ((ConvertTo-RawComparisonEvidence $lower).KernelDecoded -eq 0) 'all kernel observations may remain rejected while raw comparison succeeds'
$bounds = Fixture; $bounds.MftRuns = 128; $bounds.ReadCalls = 128; $bounds.RequestedBytes = [long]2097152; $bounds.PhysicalSectorBytes = 65536
Check ((ConvertTo-RawComparisonEvidence $bounds).RequestedBytes -eq 2097152) 'maximum allowed bounded IO summary preserved'
foreach ($sector in @(512,1024,2048,4096,8192,16384,32768,65536)) {
    $bounds.PhysicalSectorBytes = $sector
    Check ((ConvertTo-RawComparisonEvidence $bounds).PhysicalSectorBytes -eq $sector) 'physical sector power-of-two range accepted'
}
$shuffled = Fixture; $shuffled.Cases = @($shuffled.Cases | Sort-Object CaseId -Descending)
Check ((ConvertTo-RawComparisonEvidence $shuffled).Cases.Count -eq 21) 'complete case membership does not depend on row order'
foreach ($code in @('Decoded','FreeRecord','RecordNumber','RecordLength','Signature','RecordHeader','FixupMismatch','Sequence',
    'AttributeHeader','AttributeLength','DuplicateAttributeId','AttributeName','ResidentBounds','MappingBounds','MappingPairs',
    'SparseMappingFlags','StandardInformation','FileName','DataSize','MissingEndMarker','AllocationGeometry','NtfsVersion',
    'FixupStride','ClusterGeometry','RecordFlags','AttributeLimit','AttributeForm','AttributeFlags','MetadataFlags','NameLimit',
    'MultipleUnnamedData','AttributeType')) {
    $outcome = Fixture; $outcome.Cases[0].KernelCode = $code
    Check ((ConvertTo-RawComparisonEvidence $outcome).Cases[0].KernelCode -ceq $code) 'strict decoder fixed kernel outcome accepted'
}

RejectValue $null 'null root rejected'
RejectValue @{} 'hashtable root rejected'
RejectValue @((Fixture),(Fixture)) 'array root rejected'
foreach ($field in @('Path','Volume','Identity','Reference','Payload','Sha256','Exception','Skipped')) {
    Reject {param($v) $v | Add-Member -NotePropertyName $field -NotePropertyValue 'private-marker'} ('root injection ' + $field)
    Reject {param($v) $v.Cases[0] | Add-Member -NotePropertyName $field -NotePropertyValue 'private-marker'} ('case injection ' + $field)
}
$rootFields = @('Revision','Objects','KernelRequests','KernelDecoded','KernelRejected','RawRecords','RawDecoded','MatchedObjects',
    'UnknownAllocationObjects','MftRuns','ReadCalls','RequestedBytes','PhysicalSectorBytes','LockAcquired','UnlockSucceeded','PostNativeStable','Cases')
$caseFields = @('CaseId','Kind','NameLength','KernelCode','RawCode','IdentityMatched','LogicalMatched','LinksMatched','NamesMatched','AllocationCheck')
foreach ($field in $rootFields) { Reject {param($v) $v.PSObject.Properties.Remove($field)} ('missing root ' + $field) }
foreach ($field in $caseFields) { Reject {param($v) $v.Cases[0].PSObject.Properties.Remove($field)} ('missing case ' + $field) }
Reject {param($v) $v.PSObject.Properties.Remove('Objects'); $v | Add-Member -NotePropertyName objects -NotePropertyValue 21} 'root field casing exact'
Reject {param($v) $v.Cases[0].PSObject.Properties.Remove('CaseId'); $v.Cases[0] | Add-Member -NotePropertyName caseId -NotePropertyValue 'A-Ordinary-8'} 'case field casing exact'
Reject {param($v) $v.PSObject.Properties.Remove('Objects'); $v | Add-Member -MemberType ScriptProperty -Name Objects -Value {throw 'private-marker'}} 'active root property rejected before evaluation'
Reject {param($v) $v.Cases[0].PSObject.Properties.Remove('NameLength'); $v.Cases[0] | Add-Member -MemberType ScriptProperty -Name NameLength -Value {throw 'private-marker'}} 'active case property rejected before evaluation'
Reject {param($v) $v.Revision = 'raw-records-v2'} 'unknown revision'
Reject {param($v) $v.Revision = $true} 'revision type'
foreach ($field in @('Objects','KernelRequests','KernelDecoded','KernelRejected','RawRecords','RawDecoded','MatchedObjects',
    'UnknownAllocationObjects','MftRuns','ReadCalls','RequestedBytes','PhysicalSectorBytes')) {
    foreach ($bad in @(-1,[long]::MaxValue,'21',$true,[double]21,[decimal]21,$null)) {
        Reject {param($v) $v.$field = $bad} ('numeric root boundary ' + $field)
    }
}
Reject {param($v) $v.Objects = 20} 'fixed object count'
Reject {param($v) $v.KernelRequests = 62} 'fixed kernel request count'
Reject {param($v) $v.KernelDecoded = 62} 'kernel aggregate partition sum'
Reject {param($v) $v.KernelRejected = 1} 'kernel aggregate rejection partition sum'
Reject {param($v) $v.RawRecords = 21} 'raw records include MFT plus all objects'
Reject {param($v) $v.RawDecoded = 21} 'raw decode must be complete'
Reject {param($v) $v.MatchedObjects = 20} 'all object comparisons required'
Reject {param($v) $v.UnknownAllocationObjects = 0} 'unknown allocation count cannot be relabeled as known zero'
Reject {param($v) $v.UnknownAllocationObjects = 13} 'unknown allocation count exact'
Reject {param($v) $v.MftRuns = 0} 'MFT run minimum'
Reject {param($v) $v.MftRuns = 129} 'MFT run maximum'
Reject {param($v) $v.ReadCalls = 21} 'raw call minimum'
Reject {param($v) $v.ReadCalls = 129} 'raw call maximum'
Reject {param($v) $v.RequestedBytes = 22016} 'requested byte minimum'
Reject {param($v) $v.RequestedBytes = 2097664} 'requested byte maximum'
Reject {param($v) $v.RequestedBytes = 22529} 'requested bytes require whole 512-byte strides'
foreach ($sector in @(0,256,768,1025,65537,131072)) { Reject {param($v) $v.PhysicalSectorBytes = $sector} 'physical sector geometry range and power of two' }
foreach ($field in @('LockAcquired','UnlockSucceeded','PostNativeStable')) {
    foreach ($bad in @($false,'true',1,$null,@($true))) { Reject {param($v) $v.$field = $bad} ('required root boolean ' + $field) }
}
Reject {param($v) $v.Cases = $null} 'null cases'
Reject {param($v) $v.Cases = $v.Cases[0]} 'scalar cases'
Reject {param($v) $v.Cases = $v.Cases[0..19]} 'lost case'
Reject {param($v) $v.Cases = @($v.Cases) + @($v.Cases[0])} 'extra case'
Reject {param($v) $v.Cases[0] = $null} 'null case'
Reject {param($v) $v.Cases[0] = @{CaseId='A-Ordinary-8'}} 'hashtable case'
Reject {param($v) $v.Cases[1].CaseId = $v.Cases[0].CaseId} 'duplicate fixed case'
Reject {param($v) $v.Cases[0].CaseId = 'A-ordinary-8'} 'case identity exact casing'
Reject {param($v) $v.Cases[0].CaseId = 'A-Ordinary-8-Flushed'} 'read-stage suffix not part of raw case identity'
Reject {param($v) $v.Cases[0].CaseId = 'B-Ordinary-24'} 'unknown fixed case'
Reject {param($v) $v.Cases[0].CaseId = '../private-marker'} 'path-like case identity'
Reject {param($v) $v.Cases[0].Kind = 'Sparse'} 'kind matches case identity'
Reject {param($v) $v.Cases[0].Kind = 'ordinary'} 'kind exact casing'
Reject {param($v) $v.Cases[0].NameLength = 64} 'length matches case identity'
foreach ($bad in @('8',$true,[double]8,[decimal]8,$null)) { Reject {param($v) $v.Cases[0].NameLength = $bad} 'case numeric type strict' }
foreach ($bad in @('decoded','Error','Decoded`nprivate-marker','AttributeList',$true,$null)) {
    Reject {param($v) $v.Cases[0].KernelCode = $bad} 'kernel result fixed code whitelist'
}
foreach ($bad in @('decoded','FixupMismatch','Unknown',$true,$null)) { Reject {param($v) $v.Cases[0].RawCode = $bad} 'raw result must be strict Decoded' }
foreach ($field in @('IdentityMatched','LogicalMatched','LinksMatched','NamesMatched')) {
    foreach ($bad in @($false,'true',1,$null,@($true))) { Reject {param($v) $v.Cases[0].$field = $bad} ('required case boolean ' + $field) }
}
foreach ($bad in @('Unknown','Zero','matched',0,$true,$null)) { Reject {param($v) $v.Cases[0].AllocationCheck = $bad} 'ordinary allocation requires Matched' }
foreach ($bad in @('Matched','Zero','unknown',0,$true,$null)) { Reject {param($v) $v.Cases[3].AllocationCheck = $bad} 'special allocation remains Unknown' }
foreach ($rawJson in @('{"Objects":21,"Objects":21}', '{"Objects":21,"objects":21}',
    '{"Cases":[{"CaseId":"A","CaseId":"B"}]}', '{"Cases":[{"CaseId":"A","caseid":"B"}]}')) {
    $message = $null
    try { Assert-UniqueJsonKeys $rawJson } catch { $message = $_.Exception.Message }
    Check ($message -ceq 'MATRIX_JSON_INVALID') 'shared wrapper guard rejects duplicate or case-variant JSON keys'
}
Write-Output ('PASS NTFS raw comparison evidence checks: ' + $checks)
