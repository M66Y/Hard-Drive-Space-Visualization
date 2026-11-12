#requires -Version 7.4
# Pure whitelist conversion. Dot-sourcing only defines this function.
function ConvertTo-SpecialEvidence($Value) {
    function Assert-SpecialFields($Object, [string[]]$Expected) {
        if ($null -eq $Object -or $Object -isnot [pscustomobject]) { throw 'SPECIAL_EVIDENCE_INVALID' }
        $actual = @($Object.PSObject.Properties.Name)
        if ($actual.Count -ne $Expected.Count -or (Compare-Object $Expected $actual -CaseSensitive)) { throw 'SPECIAL_EVIDENCE_INVALID' }
    }
    function Assert-SpecialInteger($Number, [long]$Minimum, [long]$Maximum) {
        if (($Number -isnot [int] -and $Number -isnot [long]) -or $Number -lt $Minimum -or $Number -gt $Maximum) { throw 'SPECIAL_EVIDENCE_INVALID' }
    }
    Assert-SpecialFields $Value @('Objects','CompressedObjects','SparseObjects','UnsupportedObjects','ResolvedObjects','FailedObjects','LeaseCalls','HandlesOpened','SnapshotReads','Cases')
    Assert-SpecialInteger $Value.Objects 1 16
    foreach ($field in @('CompressedObjects','SparseObjects','UnsupportedObjects','ResolvedObjects','FailedObjects','LeaseCalls','HandlesOpened')) {
        Assert-SpecialInteger $Value.$field 0 16
    }
    Assert-SpecialInteger $Value.SnapshotReads 0 32
    if ($Value.Objects -ne $Value.CompressedObjects + $Value.SparseObjects + $Value.UnsupportedObjects -or
        $Value.ResolvedObjects -ne $Value.CompressedObjects + $Value.SparseObjects -or $Value.FailedObjects -ne 0 -or
        $Value.LeaseCalls -ne $Value.ResolvedObjects -or $Value.HandlesOpened -ne $Value.LeaseCalls -or
        $Value.SnapshotReads -ne 2 * $Value.LeaseCalls) { throw 'SPECIAL_EVIDENCE_INVALID' }
    if ($Value.Cases -isnot [array] -or $Value.Cases.Count -ne $Value.ResolvedObjects -or $Value.Cases.Count -gt 4) { throw 'SPECIAL_EVIDENCE_INVALID' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $cases = [Collections.Generic.List[object]]::new()
    $compressed = 0; $sparse = 0
    foreach ($case in $Value.Cases) {
        Assert-SpecialFields $case @('Kind','LogicalBytes','AllocatedBytes','Source')
        if ($case.Kind -isnot [string] -or $case.Kind -cnotin @('CompressedZero','CompressedRandom','SparseHole','SparseWritten') -or
            -not $seen.Add($case.Kind) -or $case.Source -isnot [string] -or $case.Source -cne 'HandleLookup') { throw 'SPECIAL_EVIDENCE_INVALID' }
        Assert-SpecialInteger $case.LogicalBytes 0 ([long]::MaxValue)
        Assert-SpecialInteger $case.AllocatedBytes 0 ([long]::MaxValue)
        if ($case.Kind.StartsWith('Compressed', [StringComparison]::Ordinal)) { $compressed++ } else { $sparse++ }
        $cases.Add([ordered]@{Kind=[string]$case.Kind;LogicalBytes=[long]$case.LogicalBytes;AllocatedBytes=[long]$case.AllocatedBytes;Source='HandleLookup'})
    }
    if ($compressed -ne $Value.CompressedObjects -or $sparse -ne $Value.SparseObjects) { throw 'SPECIAL_EVIDENCE_INVALID' }
    return [ordered]@{Objects=[int]$Value.Objects;CompressedObjects=[int]$Value.CompressedObjects;SparseObjects=[int]$Value.SparseObjects;
        UnsupportedObjects=[int]$Value.UnsupportedObjects;ResolvedObjects=[int]$Value.ResolvedObjects;FailedObjects=0;
        LeaseCalls=[int]$Value.LeaseCalls;HandlesOpened=[int]$Value.HandlesOpened;SnapshotReads=[int]$Value.SnapshotReads;Cases=$cases.ToArray()}
}
