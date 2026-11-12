#requires -Version 7.4
# Pure deep whitelist conversion; callers validate raw JSON uniqueness before conversion.
function ConvertTo-RawComparisonEvidence($Value) {
    try {
        function Assert-RawComparisonFields($Object, [string[]]$Expected) {
            if ($null -eq $Object -or $Object -isnot [pscustomobject]) { throw 'RAW_COMPARISON_EVIDENCE_INVALID' }
            $properties = @($Object.PSObject.Properties)
            $actual = @($properties.Name)
            if ($actual.Count -ne $Expected.Count -or (Compare-Object $Expected $actual -CaseSensitive) -or
                @($properties | Where-Object MemberType -ne NoteProperty).Count -ne 0) { throw 'RAW_COMPARISON_EVIDENCE_INVALID' }
        }
        function Assert-RawComparisonInteger($Number, [long]$Minimum, [long]$Maximum) {
            if (($Number -isnot [int] -and $Number -isnot [long]) -or $Number -lt $Minimum -or $Number -gt $Maximum) {
                throw 'RAW_COMPARISON_EVIDENCE_INVALID'
            }
        }
        Assert-RawComparisonFields $Value @('Revision','Objects','KernelRequests','KernelDecoded','KernelRejected',
            'RawRecords','RawDecoded','MatchedObjects','UnknownAllocationObjects','MftRuns','ReadCalls','RequestedBytes',
            'PhysicalSectorBytes','LockAcquired','UnlockSucceeded','PostNativeStable','Cases')
        if ($Value.Revision -isnot [string] -or $Value.Revision -cne 'raw-records-v1') { throw 'RAW_COMPARISON_EVIDENCE_INVALID' }
        Assert-RawComparisonInteger $Value.Objects 21 21
        Assert-RawComparisonInteger $Value.KernelRequests 63 63
        Assert-RawComparisonInteger $Value.KernelDecoded 0 63
        Assert-RawComparisonInteger $Value.KernelRejected 0 63
        if ($Value.KernelDecoded + $Value.KernelRejected -ne 63) { throw 'RAW_COMPARISON_EVIDENCE_INVALID' }
        Assert-RawComparisonInteger $Value.RawRecords 22 22
        Assert-RawComparisonInteger $Value.RawDecoded 22 22
        Assert-RawComparisonInteger $Value.MatchedObjects 21 21
        Assert-RawComparisonInteger $Value.UnknownAllocationObjects 14 14
        Assert-RawComparisonInteger $Value.MftRuns 1 128
        Assert-RawComparisonInteger $Value.ReadCalls 22 128
        Assert-RawComparisonInteger $Value.RequestedBytes 22528 2097152
        Assert-RawComparisonInteger $Value.PhysicalSectorBytes 512 65536
        if ($Value.RequestedBytes % 512 -ne 0 -or
            ($Value.PhysicalSectorBytes -band ($Value.PhysicalSectorBytes - 1)) -ne 0) { throw 'RAW_COMPARISON_EVIDENCE_INVALID' }
        foreach ($field in @('LockAcquired','UnlockSucceeded','PostNativeStable')) {
            if ($Value.$field -isnot [bool] -or -not $Value.$field) { throw 'RAW_COMPARISON_EVIDENCE_INVALID' }
        }
        if ($Value.Cases -isnot [array] -or $Value.Cases.Count -ne 21) { throw 'RAW_COMPARISON_EVIDENCE_INVALID' }

        # Fixed results of the current strict decoder, not arbitrary exception text.
        $kernelCodes = @('Decoded','FreeRecord','RecordNumber','RecordLength','Signature','RecordHeader','FixupMismatch',
            'Sequence','AttributeHeader','AttributeLength','DuplicateAttributeId','AttributeName','ResidentBounds',
            'MappingBounds','MappingPairs','SparseMappingFlags','StandardInformation','FileName','DataSize',
            'MissingEndMarker','AllocationGeometry','NtfsVersion','FixupStride','ClusterGeometry','RecordFlags',
            'AttributeLimit','AttributeForm','AttributeFlags','MetadataFlags','NameLimit','MultipleUnnamedData','AttributeType')
        $expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach ($phase in @('A','B','C')) {
            $lengths = if ($phase -ceq 'A') { @(8,24,64) } else { @(8,64) }
            foreach ($kind in @('Ordinary','Compressed','Sparse')) {
                foreach ($length in $lengths) { $expected.Add("$phase-$kind-$length", @($kind,$length)) }
            }
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $cases = [Collections.Generic.List[object]]::new()
        $unknown = 0
        foreach ($row in $Value.Cases) {
            Assert-RawComparisonFields $row @('CaseId','Kind','NameLength','KernelCode','RawCode','IdentityMatched',
                'LogicalMatched','LinksMatched','NamesMatched','AllocationCheck')
            if ($row.CaseId -isnot [string] -or $row.CaseId.Length -gt 40 -or
                -not $expected.ContainsKey($row.CaseId) -or -not $seen.Add($row.CaseId)) { throw 'RAW_COMPARISON_EVIDENCE_INVALID' }
            $case = $expected[$row.CaseId]
            if ($row.Kind -isnot [string] -or $row.Kind -cne $case[0]) { throw 'RAW_COMPARISON_EVIDENCE_INVALID' }
            Assert-RawComparisonInteger $row.NameLength ([long]$case[1]) ([long]$case[1])
            if ($row.KernelCode -isnot [string] -or $row.KernelCode -cnotin $kernelCodes -or
                $row.RawCode -isnot [string] -or $row.RawCode -cne 'Decoded') { throw 'RAW_COMPARISON_EVIDENCE_INVALID' }
            foreach ($field in @('IdentityMatched','LogicalMatched','LinksMatched','NamesMatched')) {
                if ($row.$field -isnot [bool] -or -not $row.$field) { throw 'RAW_COMPARISON_EVIDENCE_INVALID' }
            }
            $allocation = if ($row.Kind -ceq 'Ordinary') { 'Matched' } else { 'Unknown' }
            if ($row.AllocationCheck -isnot [string] -or $row.AllocationCheck -cne $allocation) { throw 'RAW_COMPARISON_EVIDENCE_INVALID' }
            if ($allocation -ceq 'Unknown') { $unknown++ }
            $cases.Add([ordered]@{CaseId=[string]$row.CaseId;Kind=[string]$row.Kind;NameLength=[int]$row.NameLength;
                KernelCode=[string]$row.KernelCode;RawCode='Decoded';IdentityMatched=$true;LogicalMatched=$true;
                LinksMatched=$true;NamesMatched=$true;AllocationCheck=$allocation})
        }
        if ($seen.Count -ne 21 -or $unknown -ne $Value.UnknownAllocationObjects) { throw 'RAW_COMPARISON_EVIDENCE_INVALID' }
        # Cases describe Flushed only. They must not be mistaken for all three observations.
        return [ordered]@{Revision='raw-records-v1';Objects=21;KernelRequests=63;
            KernelDecoded=[int]$Value.KernelDecoded;KernelRejected=[int]$Value.KernelRejected;RawRecords=22;RawDecoded=22;
            MatchedObjects=21;UnknownAllocationObjects=14;MftRuns=[int]$Value.MftRuns;ReadCalls=[int]$Value.ReadCalls;
            RequestedBytes=[long]$Value.RequestedBytes;PhysicalSectorBytes=[int]$Value.PhysicalSectorBytes;
            LockAcquired=$true;UnlockSucceeded=$true;PostNativeStable=$true;Cases=$cases.ToArray()}
    }
    catch { throw 'RAW_COMPARISON_EVIDENCE_INVALID' }
}
