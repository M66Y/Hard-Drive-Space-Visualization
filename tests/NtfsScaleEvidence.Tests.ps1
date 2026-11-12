#requires -Version 7.4
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../scripts/ntfs-prototype/ScaleEvidence.ps1')
$script:checks=0
function Check([bool]$Condition) { if(-not $Condition){throw 'FAIL scale evidence'}; $script:checks++ }
function Fixture([int]$Count=10000) {
    $samples=@(); $order=@('RawMft','Directory','Directory','RawMft','RawMft','Directory')
    for($n=0;$n -lt 6;$n++) {
        $raw=$order[$n] -ceq 'RawMft'
        $samples+= [ordered]@{Round=[int][Math]::Floor($n/2)+1;Algorithm=$order[$n];Milliseconds=150.5;
            CpuMilliseconds=100.5;ManagedAllocatedBytes=100000;RawReadCalls=$(if($raw){100}else{0});
            RawRequestedBytes=$(if($raw){6553600}else{0});NativeFallbackObjects=$(if($raw){$Count/10}else{0});
            NativeBeforeReads=$(if($raw){$Count/10}else{0});NativeAfterReads=$(if($raw){$Count/10}else{0});
            Matched=$true;SampledPeakWorkingSetBytes=100000000;WorkingSetSamples=10}
    }
    # JSON conversion gives the same integer/number forms as the native runner.
    return ([ordered]@{Revision='ntfs-scale-benchmark-v1';Workload=[ordered]@{Files=$Count;Directories=100;
        HardlinkAliases=10;ResidentFiles=[int]($Count/10);NonresidentFiles=[int](8*$Count/10);EmptyFiles=[int]($Count/10)};
        PreparationMilliseconds=1000.5;Samples=$samples;Matched=$true;WorkingSetSampleIntervalMilliseconds=20;
        ResultCounts=[ordered]@{FileObjects=$Count;FileEntries=$Count+10;Directories=100;
            LogicalBytes=[long]($Count/10*32+8*$Count/10*1024+10240);AllocatedBytes=[long](8*$Count/10*4096);UnknownAllocationFiles=0}} |
        ConvertTo-Json -Depth 8 | ConvertFrom-Json)
}
function Reject([scriptblock]$Change,[int]$Count=10000) {
    $value=Fixture $Count; & $Change $value
    $caught=$null
    try { ConvertTo-NtfsScaleEvidence $value $Count | Out-Null } catch { $caught=$_.Exception.Message }
    Check ($caught -ceq 'SCALE_EVIDENCE_INVALID')
}
foreach($count in @(10000,100000)) {
    $value=Fixture $count
    $good=ConvertTo-NtfsScaleEvidence $value $count
    Check ($good.Workload.Files -eq $count -and $good.Samples.Count -eq 6)
    $again=$good|ConvertTo-Json -Depth 8|ConvertFrom-Json
    Check ((ConvertTo-NtfsScaleEvidence $again $count).Matched)
}
foreach($field in @('Revision','Workload','PreparationMilliseconds','Samples','Matched','WorkingSetSampleIntervalMilliseconds','ResultCounts')) {
    Reject {param($v) $v.PSObject.Properties.Remove($field)}
}
Reject {param($v) $v | Add-Member NoteProperty PrivatePath 'PRIVATE'}
Reject {param($v) $v.Revision='private exception text'}
Reject {param($v) $v.Matched=$false}
Reject {param($v) $v.Matched='true'}
Reject {param($v) $v.WorkingSetSampleIntervalMilliseconds=1}
Reject {param($v) $v.PreparationMilliseconds=[double]::NaN}
Reject {param($v) $v.PreparationMilliseconds=[double]::PositiveInfinity}
Reject {param($v) $v.PreparationMilliseconds=-1}
Reject {param($v) $v.Samples=@($v.Samples[0])}
Reject {param($v) $v.Workload.Files=100000}
foreach($field in @('Directories','HardlinkAliases','ResidentFiles','NonresidentFiles','EmptyFiles')) {
    Reject {param($v) $v.Workload.$field++}
    Reject {param($v) $v.Workload.$field='1'}
}
Reject {param($v) $v.Workload | Add-Member NoteProperty Names @('PRIVATE')}
foreach($field in @('FileObjects','FileEntries','Directories','LogicalBytes','UnknownAllocationFiles')) {
    Reject {param($v) $v.ResultCounts.$field++}
    Reject {param($v) $v.ResultCounts.$field='1'}
}
Reject {param($v) $v.ResultCounts.AllocatedBytes=-1}
Reject {param($v) $v.ResultCounts.AllocatedBytes=1073741825}
Reject {param($v) $v.ResultCounts.AllocatedBytes='1'}
$nonzeroResident=Fixture; $nonzeroResident.ResultCounts.AllocatedBytes+=32000
Check ((ConvertTo-NtfsScaleEvidence $nonzeroResident 10000).ResultCounts.AllocatedBytes -eq $nonzeroResident.ResultCounts.AllocatedBytes)
Reject {param($v) $v.ResultCounts | Add-Member NoteProperty Names @('PRIVATE')}
foreach($field in @('Round','Algorithm','Milliseconds','CpuMilliseconds','ManagedAllocatedBytes','RawReadCalls',
    'RawRequestedBytes','NativeFallbackObjects','NativeBeforeReads','NativeAfterReads','Matched','SampledPeakWorkingSetBytes','WorkingSetSamples')) {
    Reject {param($v) $v.Samples[0].PSObject.Properties.Remove($field)}
}
Reject {param($v) $v.Samples[0] | Add-Member NoteProperty RecordBytes 'PRIVATE'}
Reject {param($v) $v.Samples[0].Algorithm='Directory'}
Reject {param($v) $v.Samples[2].Round=3}
Reject {param($v) $v.Samples[0].Matched=$false}
foreach($field in @('Milliseconds','CpuMilliseconds')) {
    foreach($bad in @(-1,[double]::NaN,[double]::PositiveInfinity,'1')) {
        Reject {param($v) $v.Samples[0].$field=$bad}
    }
}
Reject {param($v) $v.Samples[0].Milliseconds=0}
foreach($field in @('RawReadCalls','RawRequestedBytes','NativeFallbackObjects','NativeBeforeReads','NativeAfterReads',
    'ManagedAllocatedBytes','SampledPeakWorkingSetBytes','WorkingSetSamples')) {
    Reject {param($v) $v.Samples[0].$field=-1}
    Reject {param($v) $v.Samples[0].$field='1'}
}
Reject {param($v) $v.Samples[0].RawReadCalls=8193}
Reject {param($v) $v.Samples[0].RawRequestedBytes=268500993}
Reject {param($v) $v.Samples[0].NativeFallbackObjects=32769}
Reject {param($v) $v.Samples[0].NativeFallbackObjects=0}
Reject {param($v) $v.Samples[0].NativeBeforeReads--}
Reject {param($v) $v.Samples[0].NativeAfterReads--}
Reject {param($v) $v.Samples[0].SampledPeakWorkingSetBytes=0}
Reject {param($v) $v.Samples[0].WorkingSetSamples=0}
foreach($field in @('RawReadCalls','RawRequestedBytes','NativeFallbackObjects','NativeBeforeReads','NativeAfterReads')) {
    Reject {param($v) $v.Samples[1].$field=1}
}
Write-Output ('PASS NTFS scale evidence: '+$script:checks+' checks; no storage operations')
