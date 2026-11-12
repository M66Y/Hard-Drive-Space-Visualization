#requires -Version 7.4
# Transport validation only; source provenance must come from the private owner.
function ConvertTo-FixtureFastScanEvidence($Value) {
    try {
        function Assert-FastFields($Object, [string[]]$Expected) {
            if ($null -eq $Object -or $Object -isnot [pscustomobject]) { throw 'FIXTURE_FAST_SCAN_EVIDENCE_INVALID' }
            $properties=@($Object.PSObject.Properties)
            if ($properties.Count -ne $Expected.Count -or
                (Compare-Object $Expected @($properties.Name) -CaseSensitive) -or
                @($properties|Where-Object MemberType -ne NoteProperty).Count -ne 0) { throw 'FIXTURE_FAST_SCAN_EVIDENCE_INVALID' }
        }
        function Assert-FastInteger($Number, [long]$Minimum, [long]$Maximum) {
            if (($Number -isnot [int] -and $Number -isnot [long]) -or $Number -lt $Minimum -or $Number -gt $Maximum) {
                throw 'FIXTURE_FAST_SCAN_EVIDENCE_INVALID'
            }
        }
        function Assert-FastTrue($Object, [string[]]$Names) {
            foreach($name in $Names) { if($Object.$name -isnot [bool] -or -not $Object.$name) { throw 'FIXTURE_FAST_SCAN_EVIDENCE_INVALID' } }
        }
        Assert-FastFields $Value @('Revision','Stage','Range','Bitmap','Index','Comparison','Resources')
        if ($Value.Revision -isnot [string] -or $Value.Revision -cne 'fixture-fast-scan-v1' -or
            $Value.Stage -isnot [string] -or $Value.Stage -cne 'ControlledFixtureCompared') { throw 'FIXTURE_FAST_SCAN_EVIDENCE_INVALID' }
        $range=ConvertTo-MftEnumerationEvidence $Value.Range
        $r=$range.Enumeration; $b=$Value.Bitmap; $i=$Value.Index; $c=$Value.Comparison; $resources=$Value.Resources
        Assert-FastFields $b @('Form','PrefixBytes','CoveredSlots','ReadCalls','RequestedBytes','TargetsAllocated','ReleasedUnallocated','ReleasedFreeHeaders','Corroborated')
        if($b.Form -isnot [string] -or $b.Form -cnotin @('Resident','Nonresident')) { throw 'FIXTURE_FAST_SCAN_EVIDENCE_INVALID' }
        Assert-FastInteger $b.PrefixBytes 1 512
        Assert-FastInteger $b.CoveredSlots 22 4096
        Assert-FastInteger $b.ReadCalls 0 1
        Assert-FastInteger $b.RequestedBytes 0 512
        Assert-FastInteger $b.TargetsAllocated 21 21
        Assert-FastInteger $b.ReleasedUnallocated 2 2
        Assert-FastInteger $b.ReleasedFreeHeaders 2 2
        Assert-FastTrue $b @('Corroborated')
        if($b.PrefixBytes -ne [Math]::Ceiling($r.ExpectedSlots/8) -or $b.CoveredSlots -ne $r.ExpectedSlots -or $r.Free -lt 2 -or
            ($b.Form -ceq 'Resident' -and ($b.ReadCalls -ne 0 -or $b.RequestedBytes -ne 0)) -or
            ($b.Form -ceq 'Nonresident' -and ($b.ReadCalls -ne 1 -or $b.RequestedBytes -ne 512))) { throw 'FIXTURE_FAST_SCAN_EVIDENCE_INVALID' }
        Assert-FastFields $i @('Revision','Scope','DecodedRecords','FileObjects','DirectoryObjects','FileEntries','DosAliasesIgnored',
            'KnownLogicalBytes','KnownAllocatedBytes','UnknownLogicalObjects','UnknownAllocatedObjects','RecordKnownAllocatedObjects',
            'NativeFallbackAllocatedObjects','LookupObjects','CompleteWithinScope')
        if($i.Revision -isnot [string] -or $i.Revision -cne 'fixture-directory-index-v1' -or
            $i.Scope -isnot [string] -or $i.Scope -cne 'FixedFixtureDataRoot') { throw 'FIXTURE_FAST_SCAN_EVIDENCE_INVALID' }
        Assert-FastInteger $i.DecodedRecords 43 4096
        Assert-FastInteger $i.FileObjects 21 21
        Assert-FastInteger $i.DirectoryObjects 21 21
        Assert-FastInteger $i.FileEntries 27 27
        Assert-FastInteger $i.DosAliasesIgnored 0 16384
        Assert-FastInteger $i.KnownLogicalBytes 0 7247757312
        Assert-FastInteger $i.KnownAllocatedBytes 0 268435456
        Assert-FastInteger $i.UnknownLogicalObjects 0 0
        Assert-FastInteger $i.UnknownAllocatedObjects 0 0
        Assert-FastInteger $i.RecordKnownAllocatedObjects 7 7
        Assert-FastInteger $i.NativeFallbackAllocatedObjects 14 14
        Assert-FastInteger $i.LookupObjects 0 21
        Assert-FastTrue $i @('CompleteWithinScope')
        if($i.DecodedRecords -ne $r.Decoded -or $i.FileObjects+$i.DirectoryObjects+1 -gt $i.DecodedRecords -or
            $i.RecordKnownAllocatedObjects+$i.NativeFallbackAllocatedObjects+$i.UnknownAllocatedObjects -ne $i.FileObjects) {
            throw 'FIXTURE_FAST_SCAN_EVIDENCE_INVALID'
        }
        Assert-FastFields $c @('Revision','ScannerComplete','EntriesMatched','CountsMatched','LogicalMatched','AllocatedMatched','Matched',
            'ScannerFileEntries','ScannerDirectories','ScannerLogicalBytes','ScannerAllocatedBytes')
        if($c.Revision -isnot [string] -or $c.Revision -cne 'fixture-scanner-comparison-v1') { throw 'FIXTURE_FAST_SCAN_EVIDENCE_INVALID' }
        Assert-FastTrue $c @('ScannerComplete','EntriesMatched','CountsMatched','LogicalMatched','AllocatedMatched','Matched')
        Assert-FastInteger $c.ScannerFileEntries 27 27
        Assert-FastInteger $c.ScannerDirectories 21 21
        Assert-FastInteger $c.ScannerLogicalBytes 0 7247757312
        Assert-FastInteger $c.ScannerAllocatedBytes 0 268435456
        if($c.ScannerFileEntries -ne $i.FileEntries -or $c.ScannerDirectories -ne $i.DirectoryObjects -or
            $c.ScannerLogicalBytes -ne $i.KnownLogicalBytes -or $c.ScannerAllocatedBytes -ne $i.KnownAllocatedBytes) {
            throw 'FIXTURE_FAST_SCAN_EVIDENCE_INVALID'
        }
        Assert-FastFields $resources @('ReadCalls','RequestedBytes','Closed')
        Assert-FastTrue $resources @('Closed')
        Assert-FastInteger $resources.ReadCalls 2 256
        Assert-FastInteger $resources.RequestedBytes 23552 4195840
        if($resources.ReadCalls -ne $r.ReadCalls+$b.ReadCalls -or $resources.RequestedBytes -ne $r.RequestedBytes+$b.RequestedBytes) {
            throw 'FIXTURE_FAST_SCAN_EVIDENCE_INVALID'
        }
        # Deeply rebuild every accepted field. No tree, names, references,
        # payload, token, readable address, or arbitrary error text may escape.
        $safeBitmap=[ordered]@{Form=[string]$b.Form;PrefixBytes=[int]$b.PrefixBytes;CoveredSlots=[int]$b.CoveredSlots;
            ReadCalls=[int]$b.ReadCalls;RequestedBytes=[long]$b.RequestedBytes;TargetsAllocated=21;ReleasedUnallocated=2;ReleasedFreeHeaders=2;Corroborated=$true}
        $safeIndex=[ordered]@{Revision='fixture-directory-index-v1';Scope='FixedFixtureDataRoot';DecodedRecords=[int]$i.DecodedRecords;
            FileObjects=21;DirectoryObjects=21;FileEntries=27;DosAliasesIgnored=[int]$i.DosAliasesIgnored;
            KnownLogicalBytes=[long]$i.KnownLogicalBytes;KnownAllocatedBytes=[long]$i.KnownAllocatedBytes;UnknownLogicalObjects=0;
            UnknownAllocatedObjects=0;RecordKnownAllocatedObjects=[int]$i.RecordKnownAllocatedObjects;
            NativeFallbackAllocatedObjects=[int]$i.NativeFallbackAllocatedObjects;LookupObjects=[int]$i.LookupObjects;CompleteWithinScope=$true}
        $safeComparison=[ordered]@{Revision='fixture-scanner-comparison-v1';ScannerComplete=$true;EntriesMatched=$true;CountsMatched=$true;
            LogicalMatched=$true;AllocatedMatched=$true;Matched=$true;ScannerFileEntries=27;ScannerDirectories=21;
            ScannerLogicalBytes=[long]$c.ScannerLogicalBytes;ScannerAllocatedBytes=[long]$c.ScannerAllocatedBytes}
        return [ordered]@{Revision='fixture-fast-scan-v1';Stage='ControlledFixtureCompared';Range=$range;Bitmap=$safeBitmap;Index=$safeIndex;
            Comparison=$safeComparison;Resources=[ordered]@{ReadCalls=[int]$resources.ReadCalls;RequestedBytes=[long]$resources.RequestedBytes;Closed=$true}}
    } catch { throw 'FIXTURE_FAST_SCAN_EVIDENCE_INVALID' }
}
