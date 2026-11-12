#requires -Version 7.4
# Transport validation only. This never creates source or cleanup provenance.
# Reject duplicate JSON keys before converting the parsed envelope.
function ConvertTo-NtfsVssScaleEvidence($Value, [int]$ExpectedFiles) {
    try {
        function Fields($Object, [string[]]$Names) {
            if ($Object -isnot [pscustomobject]) { throw 'VSS_SCALE_EVIDENCE_INVALID' }
            $actual=@($Object.PSObject.Properties)
            if ($actual.Count -ne $Names.Count -or (Compare-Object $Names @($actual.Name) -CaseSensitive) -or
                @($actual|Where-Object MemberType -ne NoteProperty).Count) { throw 'VSS_SCALE_EVIDENCE_INVALID' }
        }
        function Integer($Number, [long]$Min, [long]$Max) {
            if (($Number -isnot [int] -and $Number -isnot [long]) -or $Number -lt $Min -or $Number -gt $Max) { throw 'VSS_SCALE_EVIDENCE_INVALID' }
        }
        function Time($Number, [bool]$Positive=$false) {
            if (($Number -isnot [int] -and $Number -isnot [long] -and $Number -isnot [double]) -or
                -not [double]::IsFinite([double]$Number) -or $Number -lt 0 -or $Number -gt 900000 -or
                ($Positive -and $Number -le 0)) { throw 'VSS_SCALE_EVIDENCE_INVALID' }
        }
        function Boolean($Flag, [bool]$Expected) {
            if ($Flag -isnot [bool] -or $Flag -ne $Expected) { throw 'VSS_SCALE_EVIDENCE_INVALID' }
        }
        if ($ExpectedFiles -notin @(10000,100000)) { throw 'VSS_SCALE_EVIDENCE_INVALID' }
        Fields $Value @('Revision','Workload','PreparationMilliseconds','Samples','Matched','WorkingSetSampleIntervalMilliseconds',
            'ResultCounts','SnapshotSessions','RawSessions','TotalRawReadCalls','TotalRawRequestedBytes')
        if ($Value.Revision -isnot [string] -or $Value.Revision -cne 'vss-scale-benchmark-v1') { throw 'VSS_SCALE_EVIDENCE_INVALID' }
        Time $Value.PreparationMilliseconds
        Integer $Value.WorkingSetSampleIntervalMilliseconds 20 20
        Integer $Value.SnapshotSessions 6 6
        Integer $Value.RawSessions 3 3
        Boolean $Value.Matched $true
        $w=$Value.Workload
        Fields $w @('Files','Directories','HardlinkAliases','ResidentFiles','NonresidentFiles','EmptyFiles')
        Integer $w.Files $ExpectedFiles $ExpectedFiles
        Integer $w.Directories 100 100
        Integer $w.HardlinkAliases 10 10
        Integer $w.ResidentFiles ($ExpectedFiles/10) ($ExpectedFiles/10)
        Integer $w.NonresidentFiles (8*$ExpectedFiles/10) (8*$ExpectedFiles/10)
        Integer $w.EmptyFiles ($ExpectedFiles/10) ($ExpectedFiles/10)
        $c=$Value.ResultCounts
        Fields $c @('FileObjects','FileEntries','Directories','LogicalBytes','AllocatedBytes','UnknownAllocationFiles')
        Integer $c.FileObjects $ExpectedFiles $ExpectedFiles
        Integer $c.FileEntries ($ExpectedFiles+10) ($ExpectedFiles+10)
        Integer $c.Directories 100 100
        $logical=[long]($ExpectedFiles/10*32+8*$ExpectedFiles/10*1024+10*1024)
        Integer $c.LogicalBytes $logical $logical
        # NativeStorage is the comparison contract; do not guess resident allocation.
        Integer $c.AllocatedBytes 0 1073741824
        Integer $c.UnknownAllocationFiles 0 0
        if ($Value.Samples -isnot [array] -or $Value.Samples.Count -ne 6) { throw 'VSS_SCALE_EVIDENCE_INVALID' }
        $samples=[Collections.Generic.List[object]]::new()
        $order=@('RawMft','Directory','Directory','RawMft','RawMft','Directory')
        $phaseFields=@('OwnerMilliseconds','NamespaceMilliseconds','RawReadMilliseconds','ParseMilliseconds','IndexMilliseconds',
            'NativeMilliseconds','ProjectionMilliseconds','CleanupMilliseconds','BackendMilliseconds','DirectoryMilliseconds')
        [long]$totalCalls=0;[long]$totalBytes=0
        for ($n=0;$n -lt 6;$n++) {
            $s=$Value.Samples[$n]
            Fields $s @('Round','Algorithm','Milliseconds','CpuMilliseconds','ManagedAllocatedBytes','RawReadCalls','RawRequestedBytes',
                'NativeFallbackObjects','ScopeDecodedRecords','NamespaceEntries','NamespaceComplete','Matched','Closed',
                'SampledPeakWorkingSetBytes','WorkingSetSamples','Phases','NativeSupplementCalls')
            $round=[int][Math]::Floor($n/2)+1
            Integer $s.Round $round $round
            if ($s.Algorithm -isnot [string] -or $s.Algorithm -cne $order[$n]) { throw 'VSS_SCALE_EVIDENCE_INVALID' }
            Time $s.Milliseconds $true;Time $s.CpuMilliseconds
            Integer $s.ManagedAllocatedBytes 0 ([long]::MaxValue)
            Integer $s.SampledPeakWorkingSetBytes 1 8589934592
            Integer $s.WorkingSetSamples 1 45002
            Boolean $s.Matched $true;Boolean $s.Closed $true
            Fields $s.Phases $phaseFields
            $phases=[ordered]@{}
            foreach ($field in $phaseFields) { Time $s.Phases.$field;$phases[$field]=[double]$s.Phases.$field }
            if ($phases.OwnerMilliseconds+$phases.BackendMilliseconds+$phases.CleanupMilliseconds -gt [double]$s.Milliseconds+1) { throw 'VSS_SCALE_EVIDENCE_INVALID' }
            foreach ($field in @('NamespaceMilliseconds','RawReadMilliseconds','ParseMilliseconds','IndexMilliseconds','NativeMilliseconds','ProjectionMilliseconds','DirectoryMilliseconds')) {
                if ($phases[$field] -gt $phases.BackendMilliseconds+1) { throw 'VSS_SCALE_EVIDENCE_INVALID' }
            }
            if ($s.Algorithm -ceq 'RawMft') {
                Integer $s.RawReadCalls 1 8192
                Integer $s.RawRequestedBytes 1024 268500992
                if ($s.RawRequestedBytes%512 -ne 0 -or $s.RawRequestedBytes -lt $s.RawReadCalls*512 -or
                    $s.RawRequestedBytes -gt [long]$s.RawReadCalls*65536) { throw 'VSS_SCALE_EVIDENCE_INVALID' }
                Integer $s.NativeFallbackObjects 0 ([Math]::Min(32768,$ExpectedFiles+101))
                Integer $s.NativeSupplementCalls $s.NativeFallbackObjects $s.NativeFallbackObjects
                Integer $s.ScopeDecodedRecords 0 ($ExpectedFiles+101)
                Integer $s.NamespaceEntries ($ExpectedFiles+111) ($ExpectedFiles+111)
                Boolean $s.NamespaceComplete $true
                if ($phases.DirectoryMilliseconds -ne 0) { throw 'VSS_SCALE_EVIDENCE_INVALID' }
                $totalCalls+=$s.RawReadCalls;$totalBytes+=$s.RawRequestedBytes
            } else {
                foreach ($field in @('RawReadCalls','RawRequestedBytes','NativeFallbackObjects','ScopeDecodedRecords','NamespaceEntries','NativeSupplementCalls')) { Integer $s.$field 0 0 }
                Boolean $s.NamespaceComplete $false
                foreach ($field in @('NamespaceMilliseconds','RawReadMilliseconds','ParseMilliseconds','IndexMilliseconds','NativeMilliseconds','ProjectionMilliseconds')) {
                    if ($phases[$field] -ne 0) { throw 'VSS_SCALE_EVIDENCE_INVALID' }
                }
            }
            $samples.Add([ordered]@{Round=$round;Algorithm=$order[$n];Milliseconds=[double]$s.Milliseconds;
                CpuMilliseconds=[double]$s.CpuMilliseconds;ManagedAllocatedBytes=[long]$s.ManagedAllocatedBytes;
                RawReadCalls=[int]$s.RawReadCalls;RawRequestedBytes=[long]$s.RawRequestedBytes;
                NativeFallbackObjects=[int]$s.NativeFallbackObjects;ScopeDecodedRecords=[int]$s.ScopeDecodedRecords;
                NamespaceEntries=[int]$s.NamespaceEntries;NamespaceComplete=[bool]$s.NamespaceComplete;Matched=$true;Closed=$true;
                SampledPeakWorkingSetBytes=[long]$s.SampledPeakWorkingSetBytes;WorkingSetSamples=[int]$s.WorkingSetSamples;
                Phases=$phases;NativeSupplementCalls=[int]$s.NativeSupplementCalls})
        }
        Integer $Value.TotalRawReadCalls $totalCalls $totalCalls
        Integer $Value.TotalRawRequestedBytes $totalBytes $totalBytes
        return [ordered]@{Revision='vss-scale-benchmark-v1';Workload=[ordered]@{Files=$ExpectedFiles;Directories=100;HardlinkAliases=10;
            ResidentFiles=[int]($ExpectedFiles/10);NonresidentFiles=[int](8*$ExpectedFiles/10);EmptyFiles=[int]($ExpectedFiles/10)};
            PreparationMilliseconds=[double]$Value.PreparationMilliseconds;Samples=$samples.ToArray();Matched=$true;
            WorkingSetSampleIntervalMilliseconds=20;ResultCounts=[ordered]@{FileObjects=$ExpectedFiles;FileEntries=$ExpectedFiles+10;
                Directories=100;LogicalBytes=$logical;AllocatedBytes=[long]$c.AllocatedBytes;UnknownAllocationFiles=0};
            SnapshotSessions=6;RawSessions=3;TotalRawReadCalls=$totalCalls;TotalRawRequestedBytes=$totalBytes}
    } catch { throw 'VSS_SCALE_EVIDENCE_INVALID' }
}

# Complete-envelope validation is the cleanup gate. A failed, confirmed-clean
# envelope can permit fixture cleanup but can never publish benchmark success.
function ConvertTo-NtfsVssScaleEnvelope($Value, [int]$ExpectedFiles) {
    try {
        if ($Value -isnot [pscustomobject]) { throw 'VSS_SCALE_ENVELOPE_INVALID' }
        $expected=@('schema','status','stage','checks','failures','code','native_error','cleanup_confirmed','vss_scale')
        $actual=@($Value.PSObject.Properties)
        if ($actual.Count -ne $expected.Count -or (Compare-Object $expected @($actual.Name) -CaseSensitive) -or
            @($actual|Where-Object MemberType -ne NoteProperty).Count) { throw 'VSS_SCALE_ENVELOPE_INVALID' }
        if ($ExpectedFiles -notin @(10000,100000)) { throw 'VSS_SCALE_ENVELOPE_INVALID' }
        foreach ($field in @('schema','checks','failures','native_error')) {
            if ($Value.$field -isnot [int] -and $Value.$field -isnot [long]) { throw 'VSS_SCALE_ENVELOPE_INVALID' }
        }
        if ($Value.schema -ne 1 -or $Value.checks -lt 0 -or $Value.checks -gt 6 -or
            $Value.native_error -lt [int]::MinValue -or $Value.native_error -gt [int]::MaxValue -or
            $Value.status -isnot [string] -or $Value.stage -isnot [string] -or $Value.code -isnot [string] -or
            $Value.cleanup_confirmed -isnot [bool]) { throw 'VSS_SCALE_ENVELOPE_INVALID' }
        $report=$null
        if ($Value.status -ceq 'vss_scale_benchmarked') {
            if ($Value.stage -cne 'Complete' -or $Value.code -cne 'None' -or $Value.native_error -ne 0 -or
                $Value.checks -ne 6 -or $Value.failures -ne 0 -or -not $Value.cleanup_confirmed) { throw 'VSS_SCALE_ENVELOPE_INVALID' }
            $report=ConvertTo-NtfsVssScaleEvidence $Value.vss_scale $ExpectedFiles
        } elseif ($Value.status -ceq 'failed') {
            $codes=@('VssScalePreparationFailed','VssScaleSourceFailed','VssScaleComparisonFailed','VssScaleCancelled','VssScaleCleanupFailed','VssScaleFailed')
            $stages=@('Preparation','SnapshotCreate','Namespace','RawRead','Parse','Index','Native','Projection','Directory','Cleanup','Comparison','Complete')
            if ($Value.code -cnotin $codes -or $Value.stage -cnotin $stages -or $Value.failures -ne 1 -or $null -ne $Value.vss_scale) {
                throw 'VSS_SCALE_ENVELOPE_INVALID'
            }
        } else { throw 'VSS_SCALE_ENVELOPE_INVALID' }
        return [ordered]@{schema=1;status=[string]$Value.status;stage=[string]$Value.stage;checks=[int]$Value.checks;
            failures=[int]$Value.failures;code=[string]$Value.code;native_error=[int]$Value.native_error;
            cleanup_confirmed=[bool]$Value.cleanup_confirmed;vss_scale=$report}
    } catch { throw 'VSS_SCALE_ENVELOPE_INVALID' }
}
