#requires -Version 7.4
# Pure whitelist conversion. Load RawComparisonEvidence.ps1 before calling.
# A validated JSON report is evidence transport, not an independent source verifier.
function ConvertTo-SourceAdapterEvidence($Value) {
    try {
        if ($null -eq $Value -or $Value -isnot [pscustomobject]) { throw 'SOURCE_ADAPTER_EVIDENCE_INVALID' }
        $expected = @('Revision','EvidenceKind','GuardOwned','TokenBound','MapBound','Closed','RejectedReads','Comparison')
        $properties = @($Value.PSObject.Properties)
        $actual = @($properties.Name)
        if ($actual.Count -ne $expected.Count -or (Compare-Object $expected $actual -CaseSensitive) -or
            @($properties | Where-Object MemberType -ne NoteProperty).Count -ne 0) { throw 'SOURCE_ADAPTER_EVIDENCE_INVALID' }
        if ($Value.Revision -isnot [string] -or $Value.Revision -cne 'fixture-source-v1' -or
            $Value.EvidenceKind -isnot [string] -or $Value.EvidenceKind -cne 'NativeOwnedFixture') { throw 'SOURCE_ADAPTER_EVIDENCE_INVALID' }
        foreach ($field in @('GuardOwned','TokenBound','MapBound','Closed')) {
            if ($Value.$field -isnot [bool] -or -not $Value.$field) { throw 'SOURCE_ADAPTER_EVIDENCE_INVALID' }
        }
        if (($Value.RejectedReads -isnot [int] -and $Value.RejectedReads -isnot [long]) -or
            $Value.RejectedReads -lt 0 -or $Value.RejectedReads -gt 128) { throw 'SOURCE_ADAPTER_EVIDENCE_INVALID' }
        # Rebuild every nested field through the existing strict 22-record/21-object
        # contract. In particular 14 special allocations remain Unknown, never zero.
        $comparison = ConvertTo-RawComparisonEvidence $Value.Comparison
        return [ordered]@{Revision='fixture-source-v1';EvidenceKind='NativeOwnedFixture';GuardOwned=$true;
            TokenBound=$true;MapBound=$true;Closed=$true;RejectedReads=[int]$Value.RejectedReads;Comparison=$comparison}
    }
    catch { throw 'SOURCE_ADAPTER_EVIDENCE_INVALID' }
}
