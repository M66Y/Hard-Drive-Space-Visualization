#requires -Version 7.4
# Pure transport whitelist for a bounded, owned-fixture MFT classification.
# Callers must first reject duplicate JSON keys. This does not verify a source.
function ConvertTo-MftEnumerationEvidence($Value) {
    try {
        function Assert-MftEnumerationFields($Object, [string[]]$Expected) {
            if ($null -eq $Object -or $Object -isnot [pscustomobject]) { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
            $properties = @($Object.PSObject.Properties)
            $actual = @($properties.Name)
            if ($actual.Count -ne $Expected.Count -or (Compare-Object $Expected $actual -CaseSensitive) -or
                @($properties | Where-Object MemberType -ne NoteProperty).Count -ne 0) { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
        }
        function Assert-MftEnumerationInteger($Number, [long]$Minimum, [long]$Maximum) {
            if (($Number -isnot [int] -and $Number -isnot [long]) -or $Number -lt $Minimum -or $Number -gt $Maximum) {
                throw 'MFT_ENUMERATION_EVIDENCE_INVALID'
            }
        }
        function Assert-MftEnumerationTrue($Object, [string[]]$Names) {
            foreach ($name in $Names) {
                if ($Object.$name -isnot [bool] -or -not $Object.$name) { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
            }
        }
        Assert-MftEnumerationFields $Value @('Revision','EvidenceKind','GuardOwned','TokenBound','MapBound','Closed','RejectedReads','Enumeration')
        if ($Value.Revision -isnot [string] -or $Value.Revision -cne 'bounded-mft-source-v2' -or
            $Value.EvidenceKind -isnot [string] -or $Value.EvidenceKind -cne 'NativeOwnedFixture') { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
        Assert-MftEnumerationTrue $Value @('GuardOwned','TokenBound','MapBound','Closed')
        Assert-MftEnumerationInteger $Value.RejectedReads 0 128
        $range = $Value.Enumeration
        Assert-MftEnumerationFields $range @('ValidBytes','ExpectedSlots','VisitedSlots','Decoded','Free','Unsupported','Invalid',
            'LookupRequired','UnknownLogical','UnknownAllocated','Pages','CompletedCursor','MatchedObjects','UnknownAllocationObjects',
            'MftRuns','ReadCalls','RequestedBytes','PhysicalSectorBytes','LockAcquired','UnlockSucceeded','PostNativeStable','Cases',
            'AllZeroInvalid','CodeCounts')
        Assert-MftEnumerationInteger $range.ValidBytes 22528 4194304
        Assert-MftEnumerationInteger $range.ExpectedSlots 22 4096
        Assert-MftEnumerationInteger $range.VisitedSlots 22 4096
        if ($range.ValidBytes % 1024 -ne 0 -or $range.ValidBytes -ne $range.ExpectedSlots * 1024 -or
            $range.VisitedSlots -ne $range.ExpectedSlots) { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
        Assert-MftEnumerationInteger $range.Decoded 22 $range.VisitedSlots
        foreach ($field in @('Free','Unsupported','Invalid')) { Assert-MftEnumerationInteger $range.$field 0 $range.VisitedSlots }
        if ($range.Decoded + $range.Free + $range.Unsupported + $range.Invalid -ne $range.VisitedSlots) { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
        Assert-MftEnumerationInteger $range.LookupRequired 16 $range.Decoded
        Assert-MftEnumerationInteger $range.UnknownLogical 0 $range.LookupRequired
        Assert-MftEnumerationInteger $range.UnknownAllocated 14 $range.LookupRequired
        Assert-MftEnumerationInteger $range.Pages 1 64
        if ($range.Pages -ne [Math]::Ceiling($range.ValidBytes / 65536)) { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
        Assert-MftEnumerationInteger $range.MatchedObjects 21 21
        Assert-MftEnumerationInteger $range.UnknownAllocationObjects 14 14
        Assert-MftEnumerationInteger $range.MftRuns 1 128
        Assert-MftEnumerationInteger $range.ReadCalls 2 256
        Assert-MftEnumerationInteger $range.RequestedBytes 23552 4195328
        Assert-MftEnumerationInteger $range.PhysicalSectorBytes 512 65536
        if ($range.ReadCalls -lt $range.Pages + 1 -or $range.RequestedBytes -ne $range.ValidBytes + 1024 -or
            ($range.PhysicalSectorBytes -band ($range.PhysicalSectorBytes - 1)) -ne 0) { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
        Assert-MftEnumerationTrue $range @('CompletedCursor','LockAcquired','UnlockSucceeded','PostNativeStable')
        if ($range.Cases -isnot [array] -or $range.Cases.Count -ne 21) { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }

        Assert-MftEnumerationInteger $range.AllZeroInvalid 0 $range.Invalid
        if ($range.CodeCounts -isnot [array] -or $range.CodeCounts.Count -lt 1 -or $range.CodeCounts.Count -gt 128) {
            throw 'MFT_ENUMERATION_EVIDENCE_INVALID'
        }
        # Exact results of the unchanged strict decoder. A code belongs to one
        # status only; generic strings and exception messages never become codes.
        $invalidCodes = @('AllocationGeometry','AttributeHeader','AttributeLength','AttributeName','DataSize','DuplicateAttributeId',
            'FileName','FixupMismatch','MappingBounds','MappingPairs','MissingEndMarker','RecordHeader','RecordLength','RecordNumber',
            'ResidentBounds','Sequence','Signature','SparseMappingFlags','StandardInformation')
        $unsupportedCodes = @('AttributeFlags','AttributeForm','AttributeLimit','AttributeType','ClusterGeometry','FixupStride',
            'MetadataFlags','MultipleUnnamedData','NameLimit','NtfsVersion','RecordFlags')
        $rank = [Collections.Generic.Dictionary[string,int]]::new([StringComparer]::Ordinal)
        $rank.Add('Decoded',0); $rank.Add('Free',1); $rank.Add('Invalid',2); $rank.Add('Unsupported',3)
        $pairs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $codes = [Collections.Generic.List[object]]::new()
        [long[]]$totals = @(0,0,0,0)
        [long]$total = 0; [long]$signatureInvalid = 0
        $previousRank = -1; $previousCode = ''
        foreach ($row in $range.CodeCounts) {
            Assert-MftEnumerationFields $row @('Status','Code','Count')
            if ($row.Status -isnot [string] -or -not $rank.ContainsKey($row.Status) -or $row.Code -isnot [string]) {
                throw 'MFT_ENUMERATION_EVIDENCE_INVALID'
            }
            $validCode = switch -CaseSensitive ($row.Status) {
                'Decoded' {$row.Code -ceq 'Decoded'}
                'Free' {$row.Code -ceq 'FreeRecord'}
                'Invalid' {$row.Code -cin $invalidCodes}
                'Unsupported' {$row.Code -cin $unsupportedCodes}
                default {$false}
            }
            if (-not $validCode -or -not $pairs.Add($row.Status + '/' + $row.Code)) { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
            Assert-MftEnumerationInteger $row.Count 1 $range.VisitedSlots
            $currentRank = $rank[$row.Status]
            if ($currentRank -lt $previousRank -or ($currentRank -eq $previousRank -and
                [string]::CompareOrdinal($row.Code,$previousCode) -le 0)) { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
            $previousRank = $currentRank; $previousCode = $row.Code
            $totals[$currentRank] += [long]$row.Count; $total += [long]$row.Count
            if ($row.Status -ceq 'Invalid' -and $row.Code -ceq 'Signature') {$signatureInvalid = [long]$row.Count}
            $codes.Add([ordered]@{Status=[string]$row.Status;Code=[string]$row.Code;Count=[int]$row.Count})
        }
        if ($total -ne $range.VisitedSlots -or $totals[0] -ne $range.Decoded -or $totals[1] -ne $range.Free -or
            $totals[2] -ne $range.Invalid -or $totals[3] -ne $range.Unsupported -or $range.AllZeroInvalid -gt $signatureInvalid) {
            throw 'MFT_ENUMERATION_EVIDENCE_INVALID'
        }

        $expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach ($phase in @('A','B','C')) {
            $lengths = if ($phase -ceq 'A') {@(8,24,64)} else {@(8,64)}
            foreach ($kind in @('Ordinary','Compressed','Sparse')) {
                foreach ($length in $lengths) { $expected.Add("$phase-$kind-$length", @($kind,$length)) }
            }
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $cases = [Collections.Generic.List[object]]::new()
        $unknown = 0
        foreach ($row in $range.Cases) {
            Assert-MftEnumerationFields $row @('CaseId','Kind','NameLength','RawCode','IdentityMatched','LogicalMatched','LinksMatched','NamesMatched','AllocationCheck')
            if ($row.CaseId -isnot [string] -or -not $expected.ContainsKey($row.CaseId) -or -not $seen.Add($row.CaseId)) { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
            $descriptor = $expected[$row.CaseId]
            if ($row.Kind -isnot [string] -or $row.Kind -cne $descriptor[0] -or
                $row.RawCode -isnot [string] -or $row.RawCode -cne 'Decoded') { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
            Assert-MftEnumerationInteger $row.NameLength ([long]$descriptor[1]) ([long]$descriptor[1])
            Assert-MftEnumerationTrue $row @('IdentityMatched','LogicalMatched','LinksMatched','NamesMatched')
            $allocation = if ($row.Kind -ceq 'Ordinary') {'Matched'} else {'Unknown'}
            if ($row.AllocationCheck -isnot [string] -or $row.AllocationCheck -cne $allocation) { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
            if ($allocation -ceq 'Unknown') { $unknown++ }
            $cases.Add([ordered]@{CaseId=[string]$row.CaseId;Kind=[string]$row.Kind;NameLength=[int]$row.NameLength;
                RawCode='Decoded';IdentityMatched=$true;LogicalMatched=$true;LinksMatched=$true;NamesMatched=$true;AllocationCheck=$allocation})
        }
        if ($unknown -ne $range.UnknownAllocationObjects) { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
        # Four strict classifications describe coverage, not occupied-byte totals
        # or a claim that every metadata format is supported or the disk is healthy.
        $safe = [ordered]@{ValidBytes=[long]$range.ValidBytes;ExpectedSlots=[int]$range.ExpectedSlots;VisitedSlots=[int]$range.VisitedSlots;
            Decoded=[int]$range.Decoded;Free=[int]$range.Free;Unsupported=[int]$range.Unsupported;Invalid=[int]$range.Invalid;
            LookupRequired=[int]$range.LookupRequired;UnknownLogical=[int]$range.UnknownLogical;UnknownAllocated=[int]$range.UnknownAllocated;
            Pages=[int]$range.Pages;CompletedCursor=$true;MatchedObjects=21;UnknownAllocationObjects=14;MftRuns=[int]$range.MftRuns;
            ReadCalls=[int]$range.ReadCalls;RequestedBytes=[long]$range.RequestedBytes;PhysicalSectorBytes=[int]$range.PhysicalSectorBytes;
            LockAcquired=$true;UnlockSucceeded=$true;PostNativeStable=$true;Cases=$cases.ToArray();
            AllZeroInvalid=[int]$range.AllZeroInvalid;CodeCounts=$codes.ToArray()}
        return [ordered]@{Revision='bounded-mft-source-v2';EvidenceKind='NativeOwnedFixture';GuardOwned=$true;
            TokenBound=$true;MapBound=$true;Closed=$true;RejectedReads=[int]$Value.RejectedReads;Enumeration=$safe}
    }
    catch { throw 'MFT_ENUMERATION_EVIDENCE_INVALID' }
}
