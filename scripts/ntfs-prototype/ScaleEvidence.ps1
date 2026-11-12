#requires -Version 7.4
# Transport whitelist only. This cannot establish backing-volume provenance.
function ConvertTo-NtfsScaleEvidence($Value, [int]$ExpectedFiles) {
    try {
        function Fields($Object, [string[]]$Names) {
            if ($Object -isnot [pscustomobject]) { throw 'SCALE_EVIDENCE_INVALID' }
            $actual = @($Object.PSObject.Properties)
            if ($actual.Count -ne $Names.Count -or (Compare-Object $Names @($actual.Name) -CaseSensitive) -or
                @($actual | Where-Object MemberType -ne NoteProperty).Count) { throw 'SCALE_EVIDENCE_INVALID' }
        }
        function Integer($Number, [long]$Min, [long]$Max) {
            if (($Number -isnot [int] -and $Number -isnot [long]) -or $Number -lt $Min -or $Number -gt $Max) {
                throw 'SCALE_EVIDENCE_INVALID'
            }
        }
        function Time($Number, [bool]$Positive = $false) {
            if (($Number -isnot [int] -and $Number -isnot [long] -and $Number -isnot [double]) -or
                -not [double]::IsFinite([double]$Number) -or $Number -lt 0 -or $Number -gt 900000 -or
                ($Positive -and $Number -le 0)) { throw 'SCALE_EVIDENCE_INVALID' }
        }
        function TrueValue($Flag) { if ($Flag -isnot [bool] -or -not $Flag) { throw 'SCALE_EVIDENCE_INVALID' } }
        if ($ExpectedFiles -notin @(10000,100000)) { throw 'SCALE_EVIDENCE_INVALID' }
        Fields $Value @('Revision','Workload','PreparationMilliseconds','Samples','Matched','WorkingSetSampleIntervalMilliseconds','ResultCounts')
        if ($Value.Revision -isnot [string] -or $Value.Revision -cne 'ntfs-scale-benchmark-v1') { throw 'SCALE_EVIDENCE_INVALID' }
        Integer $Value.WorkingSetSampleIntervalMilliseconds 20 20
        Time $Value.PreparationMilliseconds
        TrueValue $Value.Matched
        $w=$Value.Workload
        Fields $w @('Files','Directories','HardlinkAliases','ResidentFiles','NonresidentFiles','EmptyFiles')
        Integer $w.Files $ExpectedFiles $ExpectedFiles
        Integer $w.Directories 100 100
        Integer $w.HardlinkAliases 10 10
        Integer $w.ResidentFiles ($ExpectedFiles/10) ($ExpectedFiles/10)
        Integer $w.NonresidentFiles (8*$ExpectedFiles/10) (8*$ExpectedFiles/10)
        Integer $w.EmptyFiles ($ExpectedFiles/10) ($ExpectedFiles/10)
        $counts=$Value.ResultCounts
        Fields $counts @('FileObjects','FileEntries','Directories','LogicalBytes','AllocatedBytes','UnknownAllocationFiles')
        Integer $counts.FileObjects $ExpectedFiles $ExpectedFiles
        Integer $counts.FileEntries ($ExpectedFiles+10) ($ExpectedFiles+10)
        Integer $counts.Directories 100 100
        $logical=[long]($ExpectedFiles/10*32 + 8*$ExpectedFiles/10*1024 + 10*1024)
        Integer $counts.LogicalBytes $logical $logical
        # NativeStorage is the comparison contract. Resident allocation is not
        # assumed to be zero or cluster-rounded by this transport validator.
        Integer $counts.AllocatedBytes 0 1073741824
        $allocated=[long]$counts.AllocatedBytes
        Integer $counts.UnknownAllocationFiles 0 0
        if ($Value.Samples -isnot [array] -or $Value.Samples.Count -ne 6) { throw 'SCALE_EVIDENCE_INVALID' }
        $samples=[Collections.Generic.List[object]]::new()
        $order=@('RawMft','Directory','Directory','RawMft','RawMft','Directory')
        for ($n=0; $n -lt 6; $n++) {
            $s=$Value.Samples[$n]
            Fields $s @('Round','Algorithm','Milliseconds','CpuMilliseconds','ManagedAllocatedBytes','RawReadCalls',
                'RawRequestedBytes','NativeFallbackObjects','NativeBeforeReads','NativeAfterReads','Matched',
                'SampledPeakWorkingSetBytes','WorkingSetSamples')
            $round=[int][Math]::Floor($n/2)+1
            Integer $s.Round $round $round
            if ($s.Algorithm -isnot [string] -or $s.Algorithm -cne $order[$n]) { throw 'SCALE_EVIDENCE_INVALID' }
            Time $s.Milliseconds $true
            Time $s.CpuMilliseconds
            Integer $s.ManagedAllocatedBytes 0 ([long]::MaxValue)
            Integer $s.SampledPeakWorkingSetBytes 1 8589934592
            Integer $s.WorkingSetSamples 1 45002
            TrueValue $s.Matched
            if ($s.Algorithm -ceq 'RawMft') {
                Integer $s.RawReadCalls 1 8192
                Integer $s.RawRequestedBytes 1024 268500992
                Integer $s.NativeFallbackObjects 0 32768
                Integer $s.NativeBeforeReads 0 300000
                Integer $s.NativeAfterReads 0 300000
                if ($s.NativeFallbackObjects -ne $ExpectedFiles/10 -or
                    $s.NativeBeforeReads -ne $s.NativeFallbackObjects -or $s.NativeAfterReads -ne $s.NativeFallbackObjects) {
                    throw 'SCALE_EVIDENCE_INVALID'
                }
            } else {
                foreach ($p in @('RawReadCalls','RawRequestedBytes','NativeFallbackObjects','NativeBeforeReads','NativeAfterReads')) {
                    Integer $s.$p 0 0
                }
            }
            $samples.Add([ordered]@{Round=$round;Algorithm=$order[$n];Milliseconds=[double]$s.Milliseconds;
                CpuMilliseconds=[double]$s.CpuMilliseconds;ManagedAllocatedBytes=[long]$s.ManagedAllocatedBytes;
                RawReadCalls=[int]$s.RawReadCalls;RawRequestedBytes=[long]$s.RawRequestedBytes;
                NativeFallbackObjects=[int]$s.NativeFallbackObjects;NativeBeforeReads=[int]$s.NativeBeforeReads;
                NativeAfterReads=[int]$s.NativeAfterReads;Matched=$true;
                SampledPeakWorkingSetBytes=[long]$s.SampledPeakWorkingSetBytes;WorkingSetSamples=[int]$s.WorkingSetSamples})
        }
        return [ordered]@{Revision='ntfs-scale-benchmark-v1';Workload=[ordered]@{Files=$ExpectedFiles;Directories=100;
            HardlinkAliases=10;ResidentFiles=[int]($ExpectedFiles/10);NonresidentFiles=[int](8*$ExpectedFiles/10);EmptyFiles=[int]($ExpectedFiles/10)};
            PreparationMilliseconds=[double]$Value.PreparationMilliseconds;Samples=$samples.ToArray();Matched=$true;
            WorkingSetSampleIntervalMilliseconds=20;ResultCounts=[ordered]@{FileObjects=$ExpectedFiles;FileEntries=$ExpectedFiles+10;
                Directories=100;LogicalBytes=$logical;AllocatedBytes=$allocated;UnknownAllocationFiles=0}}
    } catch { throw 'SCALE_EVIDENCE_INVALID' }
}
