#requires -Version 7.4
# Pure-memory whitelist tests. No virtual disk, file fixture, or native query.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:checks++; Write-Output ('PASS NTFS special evidence ' + $Name)
}
$source = Join-Path $PSScriptRoot '../scripts/ntfs-prototype/SpecialEvidence.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $source).Path, [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'converter script syntax'
$functions = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'ConvertTo-SpecialEvidence'}, $false))
Check ($functions.Count -eq 1 -and $ast.EndBlock.Statements.Count -eq 1) 'dot source defines only evidence converter'
. ([scriptblock]::Create($functions[0].Extent.Text))

function Fixture {
    [pscustomobject]@{
        Objects=5;CompressedObjects=2;SparseObjects=2;UnsupportedObjects=1;ResolvedObjects=4;FailedObjects=0
        LeaseCalls=4;HandlesOpened=4;SnapshotReads=8
        Cases=@(
            [pscustomobject]@{Kind='CompressedZero';LogicalBytes=1048576;AllocatedBytes=4096;Source='HandleLookup'},
            [pscustomobject]@{Kind='CompressedRandom';LogicalBytes=1048576;AllocatedBytes=1048576;Source='HandleLookup'},
            [pscustomobject]@{Kind='SparseHole';LogicalBytes=8388608;AllocatedBytes=0;Source='HandleLookup'},
            [pscustomobject]@{Kind='SparseWritten';LogicalBytes=8388608;AllocatedBytes=8192;Source='HandleLookup'}
        )
    }
}
function Reject([scriptblock]$Mutation, [string]$Name) {
    $value=Fixture; & $Mutation $value
    $message=$null
    try { ConvertTo-SpecialEvidence $value | Out-Null } catch { $message=$_.Exception.Message }
    Check ($message -ceq 'SPECIAL_EVIDENCE_INVALID') $Name
}
$valid=Fixture
$safe=ConvertTo-SpecialEvidence $valid
Check ($safe.Objects -eq 5 -and $safe.Cases.Count -eq 4 -and $safe.Cases[2].AllocatedBytes -eq 0) 'valid evidence and known zero preserved'
Check (($safe.Keys -join ',') -ceq 'Objects,CompressedObjects,SparseObjects,UnsupportedObjects,ResolvedObjects,FailedObjects,LeaseCalls,HandlesOpened,SnapshotReads,Cases') 'canonical root field order'
Check (($safe.Cases[0].Keys -join ',') -ceq 'Kind,LogicalBytes,AllocatedBytes,Source') 'canonical case field order'
$valid.Objects=99; $valid.Cases[0].LogicalBytes=99; $valid.Cases[0].Kind='private-marker'
Check ($safe.Objects -eq 5 -and $safe.Cases[0].LogicalBytes -eq 1048576 -and $safe.Cases[0].Kind -ceq 'CompressedZero') 'output is an independent deep whitelist copy'
$roundtrip=ConvertTo-SpecialEvidence (($safe | ConvertTo-Json -Depth 5) | ConvertFrom-Json)
Check ($roundtrip.ResolvedObjects -eq 4 -and $roundtrip.Cases[3].AllocatedBytes -eq 8192) 'JSON roundtrip preserves bounded counts and bytes'

Reject {param($v) $v.Objects=0} 'empty object batch rejected'
Reject {param($v) $v.Objects=17} 'object upper bound'
Reject {param($v) $v.Objects=6} 'object partition relationship'
Reject {param($v) $v.CompressedObjects=1} 'compressed resolved relationship'
Reject {param($v) $v.SparseObjects=1} 'sparse resolved relationship'
Reject {param($v) $v.UnsupportedObjects=2} 'unsupported object relationship'
Reject {param($v) $v.ResolvedObjects=3} 'resolved object relationship'
Reject {param($v) $v.FailedObjects=1} 'successful evidence cannot conceal failures'
Reject {param($v) $v.LeaseCalls=3} 'lease call relationship'
Reject {param($v) $v.HandlesOpened=3} 'handle relationship'
Reject {param($v) $v.SnapshotReads=7} 'snapshot relationship'
Reject {param($v) $v.SnapshotReads=33} 'snapshot upper bound'
Reject {param($v) $v.Objects=[long]::MaxValue} 'overflow-sized counts rejected before arithmetic'
foreach ($field in @('Objects','CompressedObjects','SparseObjects','UnsupportedObjects','ResolvedObjects','FailedObjects','LeaseCalls','HandlesOpened','SnapshotReads')) {
    Reject {param($v) $v.$field=-1} ('negative count '+$field)
}
Reject {param($v) $v.Objects='5'} 'numeric string count rejected'
Reject {param($v) $v.Objects=[double]5} 'floating count rejected'
Reject {param($v) $v.Objects=$true} 'boolean count rejected'
Reject {param($v) $v.Objects=[decimal]5} 'decimal count rejected'
Reject {param($v) $v | Add-Member -NotePropertyName Path -NotePropertyValue 'private-marker'} 'private root field rejected'
Reject {param($v) $v | Add-Member -NotePropertyName Identity -NotePropertyValue 'private-marker'} 'private identity field rejected'
Reject {param($v) $v.PSObject.Properties.Remove('Objects')} 'missing root field rejected'
Reject {param($v) $v.PSObject.Properties.Remove('Objects');$v|Add-Member -NotePropertyName objects -NotePropertyValue 5} 'root field casing is exact'
Reject {param($v) $v.Cases=$null} 'null cases rejected'
Reject {param($v) $v.Cases='private-marker'} 'string cases rejected'
Reject {param($v) $v.Cases=[Collections.Generic.List[object]]::new()} 'nonarray cases rejected'
Reject {param($v) $v.Cases=$v.Cases[0..2]} 'lost case rejected'
Reject {param($v) $v.Cases=@($v.Cases)+@($v.Cases[0])} 'extra case rejected'
Reject {param($v) $v.Cases[0]=$null} 'null case rejected'
Reject {param($v) $v.Cases[0]=@{Kind='CompressedZero'}} 'hashtable case rejected'
Reject {param($v) $v.Cases[0]|Add-Member -NotePropertyName Path -NotePropertyValue 'private-marker'} 'private case field rejected'
Reject {param($v) $v.Cases[0]|Add-Member -NotePropertyName Exception -NotePropertyValue 'private-marker'} 'exception case field rejected'
Reject {param($v) $v.Cases[0].PSObject.Properties.Remove('Source')} 'missing case field rejected'
Reject {param($v) $v.Cases[0].PSObject.Properties.Remove('Source');$v.Cases[0]|Add-Member -NotePropertyName source -NotePropertyValue 'HandleLookup'} 'case field casing is exact'
Reject {param($v) $v.Cases[1].Kind='CompressedZero'} 'duplicate kind rejected'
Reject {param($v) $v.Cases[0].Kind='compressedZero'} 'kind casing is exact'
Reject {param($v) $v.Cases[0].Kind='Resident'} 'unknown kind rejected'
Reject {param($v) $v.Cases[0].Kind=1} 'numeric kind rejected'
Reject {param($v) $v.Cases[0].Source='Record'} 'record source rejected'
Reject {param($v) $v.Cases[0].Source='handlelookup'} 'source casing is exact'
Reject {param($v) $v.Cases[0].Source=1} 'numeric source rejected'
Reject {param($v) $v.Cases[0].LogicalBytes=-1} 'negative logical bytes rejected'
Reject {param($v) $v.Cases[0].AllocatedBytes=-1} 'negative allocation rejected'
Reject {param($v) $v.Cases[0].LogicalBytes='1048576'} 'numeric string bytes rejected'
Reject {param($v) $v.Cases[0].AllocatedBytes=[double]4096} 'floating byte count rejected'
Reject {param($v) $v.Cases[0].LogicalBytes=$true} 'boolean byte count rejected'
Reject {param($v) $v.Cases[0].AllocatedBytes=[double]::NaN} 'NaN bytes rejected'
Reject {param($v) $v.Cases[0].AllocatedBytes=[double]::PositiveInfinity} 'infinite bytes rejected'
Reject {param($v) $v.Cases[0].LogicalBytes=[bigint]::Parse('9223372036854775808')} 'integer overflow bytes rejected'
Reject {param($v) $v.CompressedObjects=1;$v.SparseObjects=3} 'case category counts must match kinds'

$large=Fixture; $large.Objects=16; $large.UnsupportedObjects=12
Check ((ConvertTo-SpecialEvidence $large).Objects -eq 16) 'maximum object count accepted'
$max=Fixture; $max.Cases[0].LogicalBytes=[long]::MaxValue;$max.Cases[0].AllocatedBytes=[long]::MaxValue
$maxSafe=ConvertTo-SpecialEvidence $max
Check ($maxSafe.Cases[0].LogicalBytes -ceq [long]::MaxValue -and $maxSafe.Cases[0].AllocatedBytes -ceq [long]::MaxValue) 'int64 maximum bytes preserved without double conversion'
$rounded=Fixture; $rounded.Cases[0].LogicalBytes=1;$rounded.Cases[0].AllocatedBytes=4096
Check ((ConvertTo-SpecialEvidence $rounded).Cases[0].AllocatedBytes -eq 4096) 'allocation may exceed logical bytes'
$none=Fixture; $none.Objects=1;$none.CompressedObjects=0;$none.SparseObjects=0;$none.UnsupportedObjects=1;$none.ResolvedObjects=0
$none.LeaseCalls=0;$none.HandlesOpened=0;$none.SnapshotReads=0;$none.Cases=@()
Check ((ConvertTo-SpecialEvidence $none).Cases.Count -eq 0) 'unsupported-only batch has no fabricated resolved case'
$partial=Fixture; $partial.Objects=2;$partial.CompressedObjects=1;$partial.SparseObjects=1;$partial.UnsupportedObjects=0;$partial.ResolvedObjects=2
$partial.LeaseCalls=2;$partial.HandlesOpened=2;$partial.SnapshotReads=4;$partial.Cases=@($partial.Cases[0],$partial.Cases[2])
Check ((ConvertTo-SpecialEvidence $partial).Cases.Count -eq 2) 'subset of supported case kinds accepted'
Write-Output ('PASS NTFS special evidence checks: '+$checks+'; virtual disk operations: none')
