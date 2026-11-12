#requires -Version 7.4
# Pure-memory validation; never mount a virtual disk or perform a native query.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:checks++; Write-Output ('PASS NTFS special cost evidence ' + $Name)
}
$source = Join-Path $PSScriptRoot '../scripts/ntfs-prototype/SpecialCostEvidence.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $source).Path, [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'converter script syntax'
$functions = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'ConvertTo-SpecialCostEvidence'}, $false))
Check ($functions.Count -eq 1 -and $ast.EndBlock.Statements.Count -eq 1) 'dot source defines only converter'
. ([scriptblock]::Create($functions[0].Extent.Text))

function Fixture {
    [pscustomobject]@{
        Objects=4;ReferenceCalls=4;LeaseCalls=4;HandlesOpened=4;SnapshotReads=8
        Samples=@(1..6 | ForEach-Object {
            [pscustomobject]@{Round=$_;Order=$(if ($_ % 2 -eq 1) {'reference-first'} else {'lease-first'});ReferenceMilliseconds=1.25;LeaseMilliseconds=2.5}
        })
    }
}
function Reject([scriptblock]$Mutation, [string]$Name) {
    $value=Fixture; & $Mutation $value
    $message=$null
    try { ConvertTo-SpecialCostEvidence $value | Out-Null } catch { $message=$_.Exception.Message }
    Check ($message -ceq 'SPECIAL_COST_EVIDENCE_INVALID') $Name
}
function RejectRoot($Value, [string]$Name) {
    $message=$null
    try { ConvertTo-SpecialCostEvidence $Value | Out-Null } catch { $message=$_.Exception.Message }
    Check ($message -ceq 'SPECIAL_COST_EVIDENCE_INVALID') $Name
}
$valid=Fixture
$safe=ConvertTo-SpecialCostEvidence $valid
Check ($safe.Objects -eq 4 -and $safe.Samples.Count -eq 6 -and $safe.Samples[5].Round -eq 6) 'valid fixed workload and rounds preserved'
Check (($safe.Keys -join ',') -ceq 'Objects,ReferenceCalls,LeaseCalls,HandlesOpened,SnapshotReads,Samples') 'canonical root field order'
Check (($safe.Samples[0].Keys -join ',') -ceq 'Round,Order,ReferenceMilliseconds,LeaseMilliseconds') 'canonical sample field order'
$valid.Objects=99; $valid.Samples[0].Round=99; $valid.Samples[0].ReferenceMilliseconds=99
Check ($safe.Objects -eq 4 -and $safe.Samples[0].Round -eq 1 -and $safe.Samples[0].ReferenceMilliseconds -eq 1.25) 'deep whitelist copy is independent'
$roundtrip=ConvertTo-SpecialCostEvidence (($safe | ConvertTo-Json -Depth 5) | ConvertFrom-Json)
Check ($roundtrip.SnapshotReads -eq 8 -and $roundtrip.Samples[1].Order -ceq 'lease-first') 'JSON roundtrip supported'
RejectRoot $null 'null root rejected'
RejectRoot 'private-marker' 'string root rejected'
RejectRoot @{Objects=4} 'hashtable root rejected'
RejectRoot @((Fixture)) 'array root rejected'

foreach ($field in @('Objects','ReferenceCalls','LeaseCalls','HandlesOpened','SnapshotReads')) {
    Reject {param($v) $v.$field=-1} ('negative '+$field)
    Reject {param($v) $v.$field=0} ('zero '+$field)
    Reject {param($v) $v.$field=[long]::MaxValue} ('oversized '+$field)
}
Reject {param($v) $v.Objects=3} 'exact object lower bound'
Reject {param($v) $v.Objects=5} 'exact object upper bound'
Reject {param($v) $v.ReferenceCalls=5} 'exact reference calls'
Reject {param($v) $v.LeaseCalls=5} 'exact lease calls'
Reject {param($v) $v.HandlesOpened=5} 'exact opened handles'
Reject {param($v) $v.SnapshotReads=7} 'exact snapshot lower bound'
Reject {param($v) $v.SnapshotReads=9} 'exact snapshot upper bound'
Reject {param($v) $v.Objects='4'} 'numeric string count rejected'
Reject {param($v) $v.ReferenceCalls=[double]4} 'floating count rejected'
Reject {param($v) $v.LeaseCalls=$true} 'boolean count rejected'
Reject {param($v) $v.HandlesOpened=[decimal]4} 'decimal count rejected'
Reject {param($v) $v|Add-Member -NotePropertyName Path -NotePropertyValue 'private-marker'} 'private root field rejected'
Reject {param($v) $v|Add-Member -NotePropertyName Identity -NotePropertyValue 'private-marker'} 'identity root field rejected'
Reject {param($v) $v.PSObject.Properties.Remove('Objects')} 'missing root field rejected'
Reject {param($v) $v.PSObject.Properties.Remove('Objects');$v|Add-Member -NotePropertyName objects -NotePropertyValue 4} 'root field casing exact'
Reject {param($v) $v.Samples=$null} 'null samples rejected'
Reject {param($v) $v.Samples='private-marker'} 'string samples rejected'
Reject {param($v) $v.Samples=[Collections.Generic.List[object]]::new()} 'nonarray samples rejected'
Reject {param($v) $v.Samples=$v.Samples[0..4]} 'lost round rejected'
Reject {param($v) $v.Samples=@($v.Samples)+@($v.Samples[0])} 'extra round rejected'
Reject {param($v) $v.Samples[0]=$null} 'null sample rejected'
Reject {param($v) $v.Samples[0]=@{Round=1}} 'hashtable sample rejected'
Reject {param($v) $v.Samples[0].Round=0} 'round lower bound'
Reject {param($v) $v.Samples[5].Round=7} 'round upper bound'
Reject {param($v) $v.Samples[0].Round=2} 'round order exact'
Reject {param($v) $v.Samples[1].Round=1} 'duplicate round rejected'
Reject {param($v) $v.Samples[0].Round='1'} 'numeric string round rejected'
Reject {param($v) $v.Samples[0].Round=[double]1} 'floating round rejected'
Reject {param($v) $v.Samples[0].Order='lease-first'} 'odd order starts reference first'
Reject {param($v) $v.Samples[1].Order='reference-first'} 'even order starts lease first'
Reject {param($v) $v.Samples[0].Order='REFERENCE-FIRST'} 'order casing exact'
Reject {param($v) $v.Samples[0].Order=$true} 'boolean order rejected'
Reject {param($v) $v.Samples[0]|Add-Member -NotePropertyName Path -NotePropertyValue 'private-marker'} 'private sample field rejected'
Reject {param($v) $v.Samples[0]|Add-Member -NotePropertyName Exception -NotePropertyValue 'private-marker'} 'exception sample field rejected'
Reject {param($v) $v.Samples[0].PSObject.Properties.Remove('LeaseMilliseconds')} 'missing sample field rejected'
Reject {param($v) $v.Samples[0].PSObject.Properties.Remove('Order');$v.Samples[0]|Add-Member -NotePropertyName order -NotePropertyValue 'reference-first'} 'sample field casing exact'
foreach ($field in @('ReferenceMilliseconds','LeaseMilliseconds')) {
    Reject {param($v) $v.Samples[0].$field=-0.1} ('negative '+$field)
    Reject {param($v) $v.Samples[0].$field=[double]::NaN} ('NaN '+$field)
    Reject {param($v) $v.Samples[0].$field=[double]::PositiveInfinity} ('positive infinity '+$field)
    Reject {param($v) $v.Samples[0].$field=[double]::NegativeInfinity} ('negative infinity '+$field)
    Reject {param($v) $v.Samples[0].$field='1.25'} ('numeric string '+$field)
    Reject {param($v) $v.Samples[0].$field=$true} ('boolean '+$field)
    Reject {param($v) $v.Samples[0].$field=$null} ('null '+$field)
}
$zero=Fixture
foreach ($sample in $zero.Samples) { $sample.ReferenceMilliseconds=0; $sample.LeaseMilliseconds=0 }
Check ((ConvertTo-SpecialCostEvidence $zero).Samples[0].LeaseMilliseconds -eq 0) 'valid zero duration preserved'
$finite=Fixture; $finite.Samples[0].ReferenceMilliseconds=[double]::MaxValue
Check ((ConvertTo-SpecialCostEvidence $finite).Samples[0].ReferenceMilliseconds -eq [double]::MaxValue) 'finite duration has no invented speed bound'
$integers=Fixture; $integers.Samples[0].ReferenceMilliseconds=[int]1;$integers.Samples[0].LeaseMilliseconds=[long]2
Check ((ConvertTo-SpecialCostEvidence $integers).Samples[0].LeaseMilliseconds -eq 2) 'JSON integer duration types accepted'
Write-Output ('PASS NTFS special cost evidence checks: '+$checks+'; virtual disk operations: none')
