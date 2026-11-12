#requires -Version 7.4
# Pure whitelist conversion only. Dot-sourcing this file performs no disk operation.
function ConvertTo-LeaseEvidence($Value) {
    function Assert-LeaseFields($Object, [string[]]$Expected) {
        if ($null -eq $Object -or $Object -isnot [pscustomobject]) { throw 'LEASE_EVIDENCE_INVALID' }
        $actual = @($Object.PSObject.Properties.Name)
        if ($actual.Count -ne $Expected.Count -or (Compare-Object $Expected $actual -CaseSensitive)) { throw 'LEASE_EVIDENCE_INVALID' }
    }
    function Assert-LeaseInteger($Number, [long]$Minimum, [long]$Maximum) {
        if (($Number -isnot [int] -and $Number -isnot [long]) -or $Number -lt $Minimum -or $Number -gt $Maximum) { throw 'LEASE_EVIDENCE_INVALID' }
    }
    function Assert-LeaseTime($Number) {
        if (($Number -isnot [int] -and $Number -isnot [long] -and $Number -isnot [double]) -or
            -not [double]::IsFinite([double]$Number) -or $Number -lt 0) { throw 'LEASE_EVIDENCE_INVALID' }
    }
    Assert-LeaseFields $Value @('Objects','ResidentObjects','DirectObjects','UnsupportedExcluded','DoubleReadCalls','LeaseCalls','LeaseHandles','LeaseSnapshots','Samples')
    Assert-LeaseInteger $Value.Objects 1 4096
    Assert-LeaseInteger $Value.ResidentObjects 0 $Value.Objects
    Assert-LeaseInteger $Value.DirectObjects 0 $Value.Objects
    Assert-LeaseInteger $Value.UnsupportedExcluded 0 4096
    Assert-LeaseInteger $Value.DoubleReadCalls 0 8192
    Assert-LeaseInteger $Value.LeaseCalls 0 4096
    Assert-LeaseInteger $Value.LeaseHandles 0 4096
    Assert-LeaseInteger $Value.LeaseSnapshots 0 8192
    if ($Value.Objects -ne $Value.ResidentObjects + $Value.DirectObjects -or
        $Value.Objects + $Value.UnsupportedExcluded -gt 4096 -or
        $Value.DoubleReadCalls -ne 2 * $Value.ResidentObjects -or $Value.LeaseCalls -ne $Value.ResidentObjects -or
        $Value.LeaseHandles -ne $Value.LeaseCalls -or $Value.LeaseSnapshots -ne 2 * $Value.LeaseCalls) { throw 'LEASE_EVIDENCE_INVALID' }
    if ($Value.Samples -isnot [array] -or $Value.Samples.Count -ne 6) { throw 'LEASE_EVIDENCE_INVALID' }
    $samples = [Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt 6; $i++) {
        $sample = $Value.Samples[$i]
        Assert-LeaseFields $sample @('Round','Order','DoubleMilliseconds','LeaseMilliseconds','DoubleNativeMilliseconds','LeaseNativeMilliseconds')
        Assert-LeaseInteger $sample.Round 1 6
        $expectedOrder = if ($i % 2 -eq 0) { 'double-first' } else { 'lease-first' }
        if ($sample.Round -ne $i + 1 -or $sample.Order -isnot [string] -or $sample.Order -cne $expectedOrder) { throw 'LEASE_EVIDENCE_INVALID' }
        foreach ($field in @('DoubleMilliseconds','LeaseMilliseconds','DoubleNativeMilliseconds','LeaseNativeMilliseconds')) { Assert-LeaseTime $sample.$field }
        if ($sample.DoubleNativeMilliseconds -gt $sample.DoubleMilliseconds -or
            $sample.LeaseNativeMilliseconds -gt $sample.LeaseMilliseconds) { throw 'LEASE_EVIDENCE_INVALID' }
        $samples.Add([ordered]@{Round=[int]$sample.Round;Order=$expectedOrder;
            DoubleMilliseconds=[double]$sample.DoubleMilliseconds;LeaseMilliseconds=[double]$sample.LeaseMilliseconds;
            DoubleNativeMilliseconds=[double]$sample.DoubleNativeMilliseconds;LeaseNativeMilliseconds=[double]$sample.LeaseNativeMilliseconds})
    }
    return [ordered]@{Objects=[int]$Value.Objects;ResidentObjects=[int]$Value.ResidentObjects;DirectObjects=[int]$Value.DirectObjects;
        UnsupportedExcluded=[int]$Value.UnsupportedExcluded;DoubleReadCalls=[int]$Value.DoubleReadCalls;
        LeaseCalls=[int]$Value.LeaseCalls;LeaseHandles=[int]$Value.LeaseHandles;LeaseSnapshots=[int]$Value.LeaseSnapshots;Samples=$samples.ToArray()}
}
