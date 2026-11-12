#requires -Version 7.4
# Semantic result-code whitelist. Unknown text is never forwarded to retained results.
function ConvertTo-FixtureCodeEvidence($Value, $Status) {
    if ($Status -isnot [string] -or $Status -cnotin @('passed','collected','verified','failed')) {
        throw 'FIXTURE_CODE_EVIDENCE_INVALID'
    }
    if ($Status -cne 'failed') {
        if ($Value -isnot [string] -or $Value -cne 'None') { throw 'FIXTURE_CODE_EVIDENCE_INVALID' }
        return 'None'
    }
    if ($Value -isnot [string] -or $Value.Length -lt 1 -or $Value.Length -gt 80) { return 'OperationFailed' }
    # Preserve only diagnostic categories that are useful outside the private log.
    # Ordinary assertion names need not be public: unlisted names become OperationFailed.
    $fixed = @(
        'OperationFailed','FixtureGuard','FixtureFailure','FixtureIO','AccessDenied','ManifestJson','ManifestField',
        'RawMetadataVolumeOpen','RawMetadataVolumeBinding','RawFreshGeometry','RawPhysicalAlignmentDescriptor',
        'RawPhysicalSectorBound','RawFixedTargetBudget','RawFixtureGeometry','RawUniqueTargets','RawKnownTarget',
        'RawTargetNativeIdentity','RawBootstrapReverseBinding','RawSliceCoverage','RawCompleteRecordRead',
        'RawRecordMatchesNativeContext','RawLookupReasonsMatchFixture','RawDirectAllocationMatches',
        'RawSpecialAllocationRemainsUnknown','RawUnlockSucceeded','RawPostNativeObjectStable','RawPostNativeParentStable',
        'RawPostNativeAliasStable','RawPostNamedStreamLength','RawPostNamedStreamContent','RawVolumeFactsStableAfterUnlock',
        'RawResultCoverage','ADMINISTRATOR_REQUIRED','COMPARISON_FAILED','CONFLICTING_FIXTURE_MODES',
        'RUNNER_LIMIT','RUNNER_NOT_BUILT','RUNNER_STILL_ACTIVE','SUMMARY_INVALID','SUMMARY_MISSING',
        'RAW_EVIDENCE_MISSING','RAW_COMPARISON_EVIDENCE_INVALID','MATRIX_JSON_INVALID',
        'KERNEL_MATRIX_EVIDENCE_INVALID','CAPTURE_CLEANUP_INVALID','FIXTURE_CODE_EVIDENCE_INVALID'
    )
    if ($Value -cin $fixed) { return $Value }
    $decoder = @('Decoded','FreeRecord','RecordNumber','RecordLength','Signature','RecordHeader','FixupMismatch','Sequence',
        'AttributeHeader','AttributeLength','DuplicateAttributeId','AttributeName','ResidentBounds','MappingBounds','MappingPairs',
        'SparseMappingFlags','StandardInformation','FileName','DataSize','MissingEndMarker','AllocationGeometry','NtfsVersion',
        'FixupStride','ClusterGeometry','RecordFlags','AttributeLimit','AttributeForm','AttributeFlags','MetadataFlags','NameLimit',
        'MultipleUnnamedData','AttributeType')
    foreach ($prefix in @('DecodeKernelRecord-','RawStrict-','RawMftMap-MftRecord-')) {
        if ($Value.StartsWith($prefix, [StringComparison]::Ordinal) -and $Value.Substring($prefix.Length) -cin $decoder) { return $Value }
    }
    $mft = @('Mapped','MftGeometry','MftFixupStride','MftRecordLength','MftBootstrapBounds','MftBaseRecord','MftLookupRequired',
        'MftName','MftAttributeBounds','MftAttributeList','MftNamedData','MftMultipleData','MftDataForm','MftMissingData',
        'MftExtentContinuation','MftVcnBounds','MftDataLengths','MftMappingBounds','MftRunLimit','MftRunEncoding','MftRunLength',
        'MftRunVolumeBounds','MftRunCoverage','MftRunPhysicalOverlap','MftBootstrapLcn','MftBootstrapFragmented','MftArithmeticOverflow')
    if ($Value.StartsWith('RawMftMap-', [StringComparison]::Ordinal) -and $Value.Substring(10) -cin $mft) { return $Value }
    function Test-FixtureCodeNumber([string]$Text, [long]$Minimum, [long]$Maximum) {
        [long]$number = 0
        return $Text -cmatch '\A(?:0|[1-9][0-9]{0,9})\z' -and [long]::TryParse($Text, [ref]$number) -and
            $number -ge $Minimum -and $number -le $Maximum
    }
    $match = [regex]::Match($Value, '\ARawNative-(LockFailed|AllocationFailed|SeekFailed|ReadFailed|ShortRead|BufferReleaseFailed)-([0-9]+)\z')
    if ($match.Success -and (Test-FixtureCodeNumber $match.Groups[2].Value 0 ([int]::MaxValue))) { return $Value }
    $match = [regex]::Match($Value, '\ARawFreshVolumeData-F([0-9]+)-E([0-9]+)-B([0-9]+)\z')
    if ($match.Success -and (Test-FixtureCodeNumber $match.Groups[1].Value 1 2) -and
        (Test-FixtureCodeNumber $match.Groups[2].Value 0 ([int]::MaxValue)) -and
        (Test-FixtureCodeNumber $match.Groups[3].Value 0 128)) { return $Value }
    $match = [regex]::Match($Value, '\AMatrixNativeAfterStable-(.+)-M([0-9]+)\z')
    if ($match.Success -and (Test-FixtureCodeNumber $match.Groups[2].Value 1 127)) {
        $case = $match.Groups[1].Value
        if ($case -ceq 'UnknownCase') { return $Value }
        foreach ($phase in @('A','B','C')) {
            $lengths = if ($phase -ceq 'A') { @(8,24,64) } else { @(8,64) }
            foreach ($kind in @('Ordinary','Compressed','Sparse')) {
                foreach ($length in $lengths) { if ($case -ceq "$phase-$kind-$length") { return $Value } }
            }
        }
    }
    $match = [regex]::Match($Value, '\ADecodeKernelRecord-FixupMismatch-Case([0-9]+)-P([0-9]+)-R([0-9]+)-S([0-9]+)(?:-U([0-9]+))?\z')
    if ($match.Success -and (Test-FixtureCodeNumber $match.Groups[1].Value 1 64) -and
        (Test-FixtureCodeNumber $match.Groups[4].Value 2 128)) {
        [int]$sectors = $match.Groups[4].Value
        if ((Test-FixtureCodeNumber $match.Groups[2].Value 0 $sectors) -and
            (Test-FixtureCodeNumber $match.Groups[3].Value 0 $sectors) -and
            (-not $match.Groups[5].Success -or (Test-FixtureCodeNumber $match.Groups[5].Value 0 ($sectors * 512)))) { return $Value }
    }
    return 'OperationFailed'
}
