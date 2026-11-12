#requires -Version 7.4
# Transport validation only. A valid report does not establish VSS provenance.
# The caller must reject duplicate JSON keys before converting a parsed object.
function ConvertTo-VssFixtureEvidence($Value) {
    try {
        if ($null -eq $Value -or $Value -isnot [pscustomobject]) { throw 'VSS_FIXTURE_EVIDENCE_INVALID' }
        $expected=@('Revision','Stage','CreatedSnapshots','DeletedSnapshots','FilesCompared','RawRecordsCompared','RawReadCalls',
            'RawRequestedBytes','PropertiesBound','SnapshotFilesStable','LiveChangesObserved','GeometryVerified','RawRecordsMatched','CleanupConfirmed')
        $properties=@($Value.PSObject.Properties)
        if ($properties.Count -ne $expected.Count -or (Compare-Object $expected @($properties.Name) -CaseSensitive) -or
            @($properties|Where-Object MemberType -ne NoteProperty).Count -ne 0) { throw 'VSS_FIXTURE_EVIDENCE_INVALID' }
        if ($Value.Revision -isnot [string] -or $Value.Revision -cne 'fixture-vss-v1' -or
            $Value.Stage -isnot [string] -or $Value.Stage -cne 'SnapshotReadVerified') { throw 'VSS_FIXTURE_EVIDENCE_INVALID' }
        foreach($field in @('CreatedSnapshots','DeletedSnapshots','FilesCompared','RawRecordsCompared','RawReadCalls','RawRequestedBytes')) {
            if ($Value.$field -isnot [int] -and $Value.$field -isnot [long]) { throw 'VSS_FIXTURE_EVIDENCE_INVALID' }
        }
        if ($Value.CreatedSnapshots -ne 1 -or $Value.DeletedSnapshots -ne 1 -or $Value.FilesCompared -ne 3 -or
            $Value.RawRecordsCompared -ne 4 -or $Value.RawReadCalls -lt 4 -or $Value.RawReadCalls -gt 16 -or
            $Value.RawRequestedBytes -lt 4096 -or $Value.RawRequestedBytes -gt 16384 -or
            $Value.RawRequestedBytes % 512 -ne 0 -or $Value.RawRequestedBytes -lt $Value.RawReadCalls * 512) {
            throw 'VSS_FIXTURE_EVIDENCE_INVALID'
        }
        foreach($field in @('PropertiesBound','SnapshotFilesStable','LiveChangesObserved','GeometryVerified','RawRecordsMatched','CleanupConfirmed')) {
            if ($Value.$field -isnot [bool] -or -not $Value.$field) { throw 'VSS_FIXTURE_EVIDENCE_INVALID' }
        }
        return [ordered]@{Revision='fixture-vss-v1';Stage='SnapshotReadVerified';CreatedSnapshots=1;DeletedSnapshots=1;FilesCompared=3;
            RawRecordsCompared=4;RawReadCalls=[int]$Value.RawReadCalls;RawRequestedBytes=[long]$Value.RawRequestedBytes;PropertiesBound=$true;
            SnapshotFilesStable=$true;LiveChangesObserved=$true;GeometryVerified=$true;RawRecordsMatched=$true;CleanupConfirmed=$true}
    } catch { throw 'VSS_FIXTURE_EVIDENCE_INVALID' }
}

# This complete envelope is also the resource-cleanup gate. A failed but
# confirmed-clean envelope may permit fixture cleanup, never verified success.
function ConvertTo-NtfsVssFixtureEnvelope($Value) {
    try {
        if ($null -eq $Value -or $Value -isnot [pscustomobject]) { throw 'VSS_FIXTURE_ENVELOPE_INVALID' }
        $expected=@('schema','status','stage','checks','failures','code','native_error','cleanup_confirmed','vss_evidence')
        $properties=@($Value.PSObject.Properties)
        if ($properties.Count -ne $expected.Count -or (Compare-Object $expected @($properties.Name) -CaseSensitive) -or
            @($properties|Where-Object MemberType -ne NoteProperty).Count -ne 0) { throw 'VSS_FIXTURE_ENVELOPE_INVALID' }
        foreach($field in @('schema','checks','failures','native_error')) {
            if ($Value.$field -isnot [int] -and $Value.$field -isnot [long]) { throw 'VSS_FIXTURE_ENVELOPE_INVALID' }
        }
        if ($Value.schema -ne 1 -or $Value.checks -lt 0 -or $Value.checks -gt 1000 -or
            $Value.native_error -lt [int]::MinValue -or $Value.native_error -gt [int]::MaxValue -or
            $Value.status -isnot [string] -or $Value.stage -isnot [string] -or $Value.code -isnot [string] -or
            $Value.cleanup_confirmed -isnot [bool]) { throw 'VSS_FIXTURE_ENVELOPE_INVALID' }
        $evidence=$null
        if ($Value.status -ceq 'vss_verified') {
            if ($Value.stage -cne 'SnapshotReadVerified' -or $Value.code -cne 'None' -or $Value.native_error -ne 0 -or
                $Value.failures -ne 0 -or $Value.checks -lt 1 -or -not $Value.cleanup_confirmed) { throw 'VSS_FIXTURE_ENVELOPE_INVALID' }
            $evidence=ConvertTo-VssFixtureEvidence $Value.vss_evidence
        } elseif ($Value.status -ceq 'failed') {
            $codes=@('VssFixtureGuard','VssFixtureCreate','VssComInitialize','VssComSecurity','VssCreateBackup','VssInitializeBackup',
                'VssContext','VssVolumeUnsupported','VssStartSet','VssAddVolume','VssCreateSnapshot','VssAsyncWait','VssAsyncStatus',
                'VssAsyncTimeout','VssCancelled','VssProperties','VssSnapshotBinding','VssSnapshotNative','VssLiveMutation',
                'VssRawOpen','VssRawGeometry','VssRawRead','VssRawMap','VssRawCompare','VssCleanup','VssUnexpected')
            if ($Value.code -cnotin $codes -or $Value.stage -cne $Value.code -or $Value.failures -ne 1 -or
                $null -ne $Value.vss_evidence) { throw 'VSS_FIXTURE_ENVELOPE_INVALID' }
        } else { throw 'VSS_FIXTURE_ENVELOPE_INVALID' }
        return [ordered]@{schema=1;status=[string]$Value.status;stage=[string]$Value.stage;checks=[int]$Value.checks;
            failures=[int]$Value.failures;code=[string]$Value.code;native_error=[int]$Value.native_error;
            cleanup_confirmed=[bool]$Value.cleanup_confirmed;vss_evidence=$evidence}
    } catch { throw 'VSS_FIXTURE_ENVELOPE_INVALID' }
}
