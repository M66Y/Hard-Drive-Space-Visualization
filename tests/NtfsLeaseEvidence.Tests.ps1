#requires -Version 7.4
# Pure-memory evidence validation. Never mount, format, or query a disk.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:checks++; Write-Output ('PASS NTFS lease evidence ' + $Name)
}
$source = Join-Path $PSScriptRoot '../scripts/ntfs-prototype/LeaseEvidence.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $source).Path, [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'converter script syntax'
$functions = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'ConvertTo-LeaseEvidence'}, $false))
Check ($functions.Count -eq 1) 'extract only evidence converter'
. ([scriptblock]::Create($functions[0].Extent.Text))

function Fixture {
    [pscustomobject]@{
        Objects=3;ResidentObjects=1;DirectObjects=2;UnsupportedExcluded=2;DoubleReadCalls=2;LeaseCalls=1;LeaseHandles=1;LeaseSnapshots=2
        Samples=@(1..6 | ForEach-Object {
            [pscustomobject]@{Round=$_;Order=$(if ($_ % 2 -eq 1) {'double-first'} else {'lease-first'});
                DoubleMilliseconds=2.5;LeaseMilliseconds=1.25;DoubleNativeMilliseconds=1.5;LeaseNativeMilliseconds=0.5}
        })
    }
}
function Reject([scriptblock]$Mutation, [string]$Name) {
    $value=Fixture; & $Mutation $value
    $message=$null
    try { ConvertTo-LeaseEvidence $value | Out-Null } catch { $message=$_.Exception.Message }
    Check ($message -ceq 'LEASE_EVIDENCE_INVALID') $Name
}
$valid=Fixture
$safe=ConvertTo-LeaseEvidence $valid
Check ($safe.Objects -eq 3 -and $safe.Samples.Count -eq 6) 'valid evidence preserved'
Check (($safe.Keys -join ',') -ceq 'Objects,ResidentObjects,DirectObjects,UnsupportedExcluded,DoubleReadCalls,LeaseCalls,LeaseHandles,LeaseSnapshots,Samples') 'canonical root field order'
Check (($safe.Samples[0].Keys -join ',') -ceq 'Round,Order,DoubleMilliseconds,LeaseMilliseconds,DoubleNativeMilliseconds,LeaseNativeMilliseconds') 'canonical sample field order'
$valid.Objects=99; $valid.Samples[0].DoubleMilliseconds=99
Check ($safe.Objects -eq 3 -and $safe.Samples[0].DoubleMilliseconds -eq 2.5) 'output is a new deep whitelist copy'
$json=$safe | ConvertTo-Json -Depth 5
$roundTrip=ConvertTo-LeaseEvidence ($json | ConvertFrom-Json)
Check ($roundTrip.DoubleReadCalls -eq 2 -and $roundTrip.LeaseSnapshots -eq 2) 'JSON integer and double roundtrip'

Reject {param($v) $v.Objects=4} 'object partition relationship'
Reject {param($v) $v.DoubleReadCalls=1} 'double reader count relationship'
Reject {param($v) $v.LeaseCalls=2} 'lease reader count relationship'
Reject {param($v) $v.LeaseHandles=2} 'lease handle count relationship'
Reject {param($v) $v.LeaseSnapshots=1} 'lease snapshot count relationship'
Reject {param($v) $v.Objects=4097} 'object upper limit'
Reject {param($v) $v.Objects=0} 'empty experiment rejected'
Reject {param($v) $v.ResidentObjects=-1} 'negative resident count'
Reject {param($v) $v.DirectObjects=4} 'direct count cannot exceed objects'
Reject {param($v) $v.UnsupportedExcluded=4097} 'excluded count upper limit'
Reject {param($v) $v.UnsupportedExcluded=4094} 'input object limit includes excluded objects'
Reject {param($v) $v.DoubleReadCalls=8193} 'double reader count upper limit'
Reject {param($v) $v.LeaseCalls=4097} 'lease count upper limit'
Reject {param($v) $v.LeaseHandles=-1} 'negative handle count'
Reject {param($v) $v.LeaseSnapshots=8193} 'snapshot count upper limit'
Reject {param($v) $v.Objects='3'} 'numeric strings rejected'
Reject {param($v) $v.Objects=[double]3} 'floating count type rejected'
Reject {param($v) $v.Objects=$true} 'boolean count rejected'
Reject {param($v) $v.Samples=$v.Samples[0..4]} 'missing timed round rejected'
Reject {param($v) $v.Samples=@($v.Samples)+@($v.Samples[0])} 'extra timed round rejected'
Reject {param($v) $v.Samples[0].Round=0} 'round lower bound'
Reject {param($v) $v.Samples[5].Round=7} 'round upper bound'
Reject {param($v) $v.Samples[0].Round=2} 'out of sequence round rejected'
Reject {param($v) $v.Samples[1].Round=1} 'duplicate round rejected'
Reject {param($v) $v.Samples[0].Order='lease-first'} 'run order starts double first'
Reject {param($v) $v.Samples[1].Order='double-first'} 'run order must alternate'
Reject {param($v) $v.Samples[0].Order='DOUBLE-FIRST'} 'run order has exact spelling'
Reject {param($v) $v.Samples[0].Order=$true} 'run order requires string'
Reject {param($v) $v.Samples[0].DoubleMilliseconds=[double]::NaN} 'NaN rejected'
Reject {param($v) $v.Samples[0].LeaseMilliseconds=[double]::PositiveInfinity} 'positive infinity rejected'
Reject {param($v) $v.Samples[0].LeaseNativeMilliseconds=[double]::NegativeInfinity} 'negative infinity rejected'
Reject {param($v) $v.Samples[0].DoubleNativeMilliseconds=-0.1} 'negative callback duration rejected'
Reject {param($v) $v.Samples[0].DoubleMilliseconds='2.5'} 'duration strings rejected'
Reject {param($v) $v.Samples[0].DoubleMilliseconds=$false} 'duration booleans rejected'
Reject {param($v) $v.Samples[0].DoubleNativeMilliseconds=3} 'double callback duration cannot exceed batch'
Reject {param($v) $v.Samples[0].LeaseNativeMilliseconds=2} 'lease callback duration cannot exceed batch'
Reject {param($v) $v | Add-Member -NotePropertyName Path -NotePropertyValue 'synthetic-private-marker'} 'unexpected private root field rejected'
Reject {param($v) $v.Samples[0] | Add-Member -NotePropertyName Path -NotePropertyValue 'synthetic-private-marker'} 'unexpected private sample field rejected'
Reject {param($v) $v.PSObject.Properties.Remove('Objects')} 'missing root field rejected'
Reject {param($v) $v.Samples[0].PSObject.Properties.Remove('Round')} 'missing sample field rejected'
Reject {param($v) $v.Samples=$null} 'null sample array rejected'
Reject {param($v) $v.Samples='synthetic-private-marker'} 'sample string rejected'
Reject {param($v) $v.Samples[0]=$null} 'null sample rejected'
Reject {param($v) $v.Samples[0]=@{Round=1}} 'hashtable sample rejected'
Reject {param($v) $v.Samples[0].PSObject.Properties.Remove('Order'); $v.Samples[0] | Add-Member -NotePropertyName order -NotePropertyValue 'double-first'} 'field spelling is case sensitive'

$none=Fixture; $none.ResidentObjects=0; $none.DirectObjects=3; $none.DoubleReadCalls=0; $none.LeaseCalls=0; $none.LeaseHandles=0; $none.LeaseSnapshots=0
Check ((ConvertTo-LeaseEvidence $none).LeaseCalls -eq 0) 'zero resident workload supported'
$large=Fixture; $large.Objects=4096; $large.ResidentObjects=4096; $large.DirectObjects=0; $large.UnsupportedExcluded=0
$large.DoubleReadCalls=8192; $large.LeaseCalls=4096; $large.LeaseHandles=4096; $large.LeaseSnapshots=8192
Check ((ConvertTo-LeaseEvidence $large).Objects -eq 4096) 'maximum bounded workload accepted'
$zero=Fixture
foreach ($sample in $zero.Samples) { $sample.DoubleMilliseconds=0; $sample.LeaseMilliseconds=0; $sample.DoubleNativeMilliseconds=0; $sample.LeaseNativeMilliseconds=0 }
Check ((ConvertTo-LeaseEvidence $zero).Samples[0].DoubleMilliseconds -eq 0) 'zero durations accepted'
Write-Output ('PASS NTFS lease evidence checks: '+$checks+'; virtual disk operations: none')
