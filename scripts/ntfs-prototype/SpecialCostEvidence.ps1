#requires -Version 7.4
# Pure whitelist conversion. Dot-sourcing only defines the converter.
function ConvertTo-SpecialCostEvidence($Value) {
    function Assert-SpecialCostFields($Object, [string[]]$Expected) {
        if ($null -eq $Object -or $Object -isnot [pscustomobject]) { throw 'SPECIAL_COST_EVIDENCE_INVALID' }
        $actual = @($Object.PSObject.Properties.Name)
        if ($actual.Count -ne $Expected.Count -or (Compare-Object $Expected $actual -CaseSensitive)) { throw 'SPECIAL_COST_EVIDENCE_INVALID' }
    }
    function Assert-SpecialCostInteger($Number, [int]$Expected) {
        if (($Number -isnot [int] -and $Number -isnot [long]) -or $Number -ne $Expected) { throw 'SPECIAL_COST_EVIDENCE_INVALID' }
    }
    function Assert-SpecialCostTime($Number) {
        if (($Number -isnot [int] -and $Number -isnot [long] -and $Number -isnot [double]) -or
            -not [double]::IsFinite([double]$Number) -or $Number -lt 0) { throw 'SPECIAL_COST_EVIDENCE_INVALID' }
    }
    Assert-SpecialCostFields $Value @('Objects','ReferenceCalls','LeaseCalls','HandlesOpened','SnapshotReads','Samples')
    foreach ($field in @('Objects','ReferenceCalls','LeaseCalls','HandlesOpened')) { Assert-SpecialCostInteger $Value.$field 4 }
    Assert-SpecialCostInteger $Value.SnapshotReads 8
    if ($Value.Samples -isnot [array] -or $Value.Samples.Count -ne 6) { throw 'SPECIAL_COST_EVIDENCE_INVALID' }
    $samples = [Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt 6; $i++) {
        $sample = $Value.Samples[$i]
        Assert-SpecialCostFields $sample @('Round','Order','ReferenceMilliseconds','LeaseMilliseconds')
        Assert-SpecialCostInteger $sample.Round ($i + 1)
        $order = if ($i % 2 -eq 0) { 'reference-first' } else { 'lease-first' }
        if ($sample.Order -isnot [string] -or $sample.Order -cne $order) { throw 'SPECIAL_COST_EVIDENCE_INVALID' }
        Assert-SpecialCostTime $sample.ReferenceMilliseconds
        Assert-SpecialCostTime $sample.LeaseMilliseconds
        $samples.Add([ordered]@{Round=$i + 1;Order=$order;ReferenceMilliseconds=[double]$sample.ReferenceMilliseconds;LeaseMilliseconds=[double]$sample.LeaseMilliseconds})
    }
    return [ordered]@{Objects=4;ReferenceCalls=4;LeaseCalls=4;HandlesOpened=4;SnapshotReads=8;Samples=$samples.ToArray()}
}
