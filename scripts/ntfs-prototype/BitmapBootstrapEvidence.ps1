#requires -Version 7.4
# Pure transport whitelist only. The caller must reject duplicate JSON keys
# before conversion and establish the fixture source; this does not verify it.
function ConvertTo-BitmapBootstrapEvidence($Value) {
    try {
        function Assert-BitmapBootstrapFields($Object, [string[]]$Expected) {
            if ($null -eq $Object -or $Object -isnot [pscustomobject]) { throw 'BITMAP_BOOTSTRAP_EVIDENCE_INVALID' }
            $properties = @($Object.PSObject.Properties)
            $actual = @($properties.Name)
            if ($actual.Count -ne $Expected.Count -or (Compare-Object $Expected $actual -CaseSensitive) -or
                @($properties | Where-Object MemberType -ne NoteProperty).Count -ne 0) { throw 'BITMAP_BOOTSTRAP_EVIDENCE_INVALID' }
        }
        function Assert-BitmapBootstrapInteger($Number, [long]$Minimum, [long]$Maximum) {
            if (($Number -isnot [int] -and $Number -isnot [long]) -or $Number -lt $Minimum -or $Number -gt $Maximum) {
                throw 'BITMAP_BOOTSTRAP_EVIDENCE_INVALID'
            }
        }
        Assert-BitmapBootstrapFields $Value @('Revision','Stage','Range','Bitmap')
        if ($Value.Revision -isnot [string] -or $Value.Revision -cne 'bitmap-bootstrap-source-v1' -or
            $Value.Stage -isnot [string] -or $Value.Stage -cne 'ObservedShapeOnly') { throw 'BITMAP_BOOTSTRAP_EVIDENCE_INVALID' }
        # Keep the old v2 contract intact. In particular, simulated source data
        # cannot acquire NativeOwnedFixture evidence by adding a bitmap label.
        $range = ConvertTo-MftEnumerationEvidence $Value.Range
        $bitmap = $Value.Bitmap
        Assert-BitmapBootstrapFields $bitmap @('Status','Code','ExpectedSlots','CoveredSlots','PayloadBytes','NonresidentMetadata')
        if ($bitmap.Status -isnot [string] -or $bitmap.Code -isnot [string]) { throw 'BITMAP_BOOTSTRAP_EVIDENCE_INVALID' }
        $deferredCodes = @('BitmapSeedUnsupported','BitmapExtent','BitmapMultiple','BitmapNamed','BitmapFlags',
            'BitmapResidentPayloadBudget','BitmapNonresidentPayloadBudget','BitmapNonresident')
        $known = switch -CaseSensitive ($bitmap.Status) {
            'Missing' {$bitmap.Code -ceq 'BitmapMissing'}
            'ResidentPartial' {$bitmap.Code -ceq 'BitmapResidentPartial'}
            'ResidentCapacityOnly' {$bitmap.Code -ceq 'BitmapResidentComplete'}
            'Deferred' {$bitmap.Code -cin $deferredCodes}
            default {$false}
        }
        if (-not $known) { throw 'BITMAP_BOOTSTRAP_EVIDENCE_INVALID' }
        Assert-BitmapBootstrapInteger $bitmap.ExpectedSlots 22 4096
        Assert-BitmapBootstrapInteger $bitmap.CoveredSlots 0 4096
        Assert-BitmapBootstrapInteger $bitmap.PayloadBytes 0 512
        if ($bitmap.ExpectedSlots -ne $range.Enumeration.ExpectedSlots) { throw 'BITMAP_BOOTSTRAP_EVIDENCE_INVALID' }
        $resident = $bitmap.Status -cin @('ResidentPartial','ResidentCapacityOnly')
        if ($resident) {
            $capacity = [Math]::Min([int]$bitmap.ExpectedSlots, [int]$bitmap.PayloadBytes * 8)
            if ($bitmap.CoveredSlots -ne $capacity -or
                ($bitmap.Status -ceq 'ResidentCapacityOnly' -and $capacity -ne $bitmap.ExpectedSlots) -or
                ($bitmap.Status -ceq 'ResidentPartial' -and $capacity -ge $bitmap.ExpectedSlots)) {
                throw 'BITMAP_BOOTSTRAP_EVIDENCE_INVALID'
            }
        } elseif ($bitmap.CoveredSlots -ne 0 -or $bitmap.PayloadBytes -ne 0) { throw 'BITMAP_BOOTSTRAP_EVIDENCE_INVALID' }
        $metadata = $null
        if ($bitmap.Status -ceq 'Deferred' -and $bitmap.Code -ceq 'BitmapNonresident') {
            $item = $bitmap.NonresidentMetadata
            Assert-BitmapBootstrapFields $item @('LogicalBytes','InitializedBytes','AllocatedBytes')
            Assert-BitmapBootstrapInteger $item.LogicalBytes 0 512
            Assert-BitmapBootstrapInteger $item.InitializedBytes 0 $item.LogicalBytes
            Assert-BitmapBootstrapInteger $item.AllocatedBytes $item.LogicalBytes 268435456
            if ($item.AllocatedBytes % 4096 -ne 0) { throw 'BITMAP_BOOTSTRAP_EVIDENCE_INVALID' }
            $metadata = [ordered]@{LogicalBytes=[long]$item.LogicalBytes;InitializedBytes=[long]$item.InitializedBytes;
                AllocatedBytes=[long]$item.AllocatedBytes}
        } elseif ($null -ne $bitmap.NonresidentMetadata) { throw 'BITMAP_BOOTSTRAP_EVIDENCE_INVALID' }
        $safeBitmap = [ordered]@{Status=[string]$bitmap.Status;Code=[string]$bitmap.Code;ExpectedSlots=[int]$bitmap.ExpectedSlots;
            CoveredSlots=[int]$bitmap.CoveredSlots;PayloadBytes=[int]$bitmap.PayloadBytes;NonresidentMetadata=$metadata}
        # Capacity is not a bit interpretation or a count of allocated records.
        # No payload, readable address, token or per-slot identity is published.
        return [ordered]@{Revision='bitmap-bootstrap-source-v1';Stage='ObservedShapeOnly';Range=$range;Bitmap=$safeBitmap}
    }
    catch { throw 'BITMAP_BOOTSTRAP_EVIDENCE_INVALID' }
}
