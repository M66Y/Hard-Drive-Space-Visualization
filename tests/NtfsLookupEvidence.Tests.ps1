#requires -Version 7.4
# Pure-memory whitelist tests. Never invoke the fixture script's top-level code.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:checks++; Write-Output ('PASS NTFS lookup evidence ' + $Name)
}
$source = Join-Path $PSScriptRoot '../scripts/ntfs-prototype/Invoke-NtfsFixture.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $source).Path, [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'fixture script syntax'
$functions = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'ConvertTo-LookupEvidence'}, $false))
Check ($functions.Count -eq 1) 'extract only evidence converter'
. ([scriptblock]::Create($functions[0].Extent.Text))

function Fixture {
    [pscustomobject]@{
        Objects=3;ResidentObjects=1;DirectObjects=2;UnsupportedExcluded=2;SelectiveNativeCalls=2;AllHandleNativeCalls=6
        Samples=@(1..6 | ForEach-Object {
            [pscustomobject]@{Round=$_;Order=$(if ($_ % 2 -eq 1) {'selective-first'} else {'handle-first'});
                SelectiveMilliseconds=1.25;AllHandleMilliseconds=2.5;SelectiveNativeMilliseconds=0.5;AllHandleNativeMilliseconds=1.5}
        })
        ResidentExamples=@([pscustomobject]@{LogicalBytes=17;LookupAllocatedBytes=24})
    }
}
function Reject([scriptblock]$Mutation, [string]$Name) {
    $value=Fixture; & $Mutation $value
    $message=$null
    try { ConvertTo-LookupEvidence $value | Out-Null } catch { $message=$_.Exception.Message }
    Check ($message -ceq 'LOOKUP_EVIDENCE_INVALID') $Name
}
$valid=Fixture
$safe=ConvertTo-LookupEvidence $valid
Check ($safe.Objects -eq 3 -and $safe.Samples.Count -eq 6 -and $safe.ResidentExamples.Count -eq 1) 'valid evidence preserved'
Check (($safe.Keys -join ',') -ceq 'Objects,ResidentObjects,DirectObjects,UnsupportedExcluded,SelectiveNativeCalls,AllHandleNativeCalls,Samples,ResidentExamples') 'canonical root field order'
Check (($safe.Samples[0].Keys -join ',') -ceq 'Round,Order,SelectiveMilliseconds,AllHandleMilliseconds,SelectiveNativeMilliseconds,AllHandleNativeMilliseconds') 'canonical sample field order'
$valid.Samples[0].SelectiveMilliseconds=99
$valid.ResidentExamples[0].LogicalBytes=99
Check ($safe.Samples[0].SelectiveMilliseconds -eq 1.25 -and $safe.ResidentExamples[0].LogicalBytes -eq 17) 'output is a new deep whitelist copy'
$json=$safe | ConvertTo-Json -Depth 5
$roundTrip=ConvertTo-LookupEvidence ($json | ConvertFrom-Json)
Check ($roundTrip.SelectiveNativeCalls -eq 2) 'JSON integer and double roundtrip'

Reject {param($v) $v.Objects=4} 'object partition relationship'
Reject {param($v) $v.SelectiveNativeCalls=1} 'selective reader count relationship'
Reject {param($v) $v.AllHandleNativeCalls=5} 'control reader count relationship'
Reject {param($v) $v.Objects=4097} 'object upper limit'
Reject {param($v) $v.Objects=0} 'empty experiment rejected'
Reject {param($v) $v.ResidentObjects=-1} 'negative resident count'
Reject {param($v) $v.DirectObjects=4} 'direct count cannot exceed objects'
Reject {param($v) $v.UnsupportedExcluded=4097} 'excluded count upper limit'
Reject {param($v) $v.SelectiveNativeCalls=8193} 'reader count upper limit'
Reject {param($v) $v.Objects='3'} 'numeric strings rejected'
Reject {param($v) $v.Objects=[double]3} 'fractional count type rejected'
Reject {param($v) $v.Objects=$true} 'boolean count rejected'
Reject {param($v) $v.Samples=$v.Samples[0..4]} 'missing timed round rejected'
Reject {param($v) $v.Samples=@($v.Samples)+@($v.Samples[0])} 'extra timed round rejected'
Reject {param($v) $v.Samples[0].Round=2} 'out of sequence round rejected'
Reject {param($v) $v.Samples[0].Order='handle-first'} 'run order must alternate from selective first'
Reject {param($v) $v.Samples[0].Order='SELECTIVE-FIRST'} 'run order has exact spelling'
Reject {param($v) $v.Samples[0].SelectiveMilliseconds=[double]::NaN} 'NaN rejected'
Reject {param($v) $v.Samples[0].AllHandleMilliseconds=[double]::PositiveInfinity} 'positive infinity rejected'
Reject {param($v) $v.Samples[0].AllHandleNativeMilliseconds=[double]::NegativeInfinity} 'negative infinity rejected'
Reject {param($v) $v.Samples[0].SelectiveNativeMilliseconds=-0.1} 'negative callback duration rejected'
Reject {param($v) $v.Samples[0].SelectiveMilliseconds='0.1'} 'duration strings rejected'
Reject {param($v) $v.Samples[0].SelectiveMilliseconds=$false} 'duration booleans rejected'
Reject {param($v) $v | Add-Member -NotePropertyName Path -NotePropertyValue 'synthetic-private-marker'} 'unexpected root fields rejected'
Reject {param($v) $v.Samples[0] | Add-Member -NotePropertyName Path -NotePropertyValue 'synthetic-private-marker'} 'unexpected sample fields rejected'
Reject {param($v) $v.ResidentExamples[0] | Add-Member -NotePropertyName Path -NotePropertyValue 'synthetic-private-marker'} 'unexpected resident fields rejected'
Reject {param($v) $v.PSObject.Properties.Remove('Objects')} 'missing root field rejected'
Reject {param($v) $v.Samples[0].PSObject.Properties.Remove('Round')} 'missing sample field rejected'
Reject {param($v) $v.ResidentExamples[0].LogicalBytes=-1} 'negative example logical size rejected'
Reject {param($v) $v.ResidentExamples[0].LookupAllocatedBytes=-1} 'negative example allocation rejected'
Reject {param($v) $v.ResidentExamples[0].LookupAllocatedBytes=[double]1} 'example size requires integer type'
Reject {param($v) $v.ResidentExamples=@(1..65 | ForEach-Object {[pscustomobject]@{LogicalBytes=1;LookupAllocatedBytes=8}})} 'example array upper limit'
Reject {param($v) $v.ResidentExamples=@($v.ResidentExamples[0],$v.ResidentExamples[0])} 'examples cannot exceed resident objects'
Reject {param($v) $v.ResidentExamples=$null} 'missing example array rejected'
Reject {param($v) $v.Samples=$null} 'missing sample array rejected'

$none=Fixture; $none.ResidentObjects=0; $none.DirectObjects=3; $none.SelectiveNativeCalls=0; $none.ResidentExamples=@()
Check ((ConvertTo-LookupEvidence $none).ResidentExamples.Count -eq 0) 'zero resident workload supported'
$large=Fixture; $large.Objects=4096; $large.ResidentObjects=4096; $large.DirectObjects=0; $large.SelectiveNativeCalls=8192; $large.AllHandleNativeCalls=8192
$large.ResidentExamples=@(1..64 | ForEach-Object {[pscustomobject]@{LogicalBytes=1;LookupAllocatedBytes=8}})
Check ((ConvertTo-LookupEvidence $large).Objects -eq 4096) 'maximum bounded workload accepted'
$maximum=Fixture; $maximum.ResidentExamples[0].LogicalBytes=[long]::MaxValue; $maximum.ResidentExamples[0].LookupAllocatedBytes=[long]::MaxValue
Check ((ConvertTo-LookupEvidence $maximum).ResidentExamples[0].LogicalBytes -eq [long]::MaxValue) 'integer example bounds do not coerce to double'
Write-Output ('PASS NTFS lookup evidence checks: '+$checks+'; virtual disk operations: none')
