#requires -Version 7.4
# Pure whitelist conversion. Dot-sourcing only defines these functions.
function Assert-UniqueJsonKeys([string]$Json) {
    $document = $null
    try {
        if ([string]::IsNullOrEmpty($Json) -or [Text.Encoding]::UTF8.GetByteCount($Json) -gt 65536) { throw 'MATRIX_JSON_INVALID' }
        $options = [Text.Json.JsonDocumentOptions]::new()
        $options.MaxDepth = 16
        $document = [Text.Json.JsonDocument]::Parse($Json, $options)
        function Assert-MatrixJsonElement([Text.Json.JsonElement]$Element) {
            if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
                $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($property in $Element.EnumerateObject()) {
                    if (-not $names.Add($property.Name)) { throw 'MATRIX_JSON_INVALID' }
                    Assert-MatrixJsonElement $property.Value
                }
            }
            elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
                foreach ($item in $Element.EnumerateArray()) { Assert-MatrixJsonElement $item }
            }
        }
        Assert-MatrixJsonElement $document.RootElement
    }
    catch { throw 'MATRIX_JSON_INVALID' }
    finally { if ($null -ne $document) { $document.Dispose() } }
}

function ConvertTo-KernelMatrixEvidence($Value) {
    try {
        function Assert-KernelMatrixFields($Object, [string[]]$Expected) {
            if ($null -eq $Object -or $Object -isnot [pscustomobject]) { throw 'KERNEL_MATRIX_EVIDENCE_INVALID' }
            $properties = @($Object.PSObject.Properties)
            $actual = @($properties.Name)
            if ($actual.Count -ne $Expected.Count -or (Compare-Object $Expected $actual -CaseSensitive) -or
                @($properties | Where-Object MemberType -ne NoteProperty).Count -ne 0) { throw 'KERNEL_MATRIX_EVIDENCE_INVALID' }
        }
        function Assert-KernelMatrixInteger($Number, [long]$Minimum, [long]$Maximum) {
            if (($Number -isnot [int] -and $Number -isnot [long]) -or $Number -lt $Minimum -or $Number -gt $Maximum) { throw 'KERNEL_MATRIX_EVIDENCE_INVALID' }
        }
        Assert-KernelMatrixFields $Value @('Revision','Objects','Requests','Decoded','DecodeFailures','Captured','CaptureFailures','CaptureLimited','Rows')
        if ($Value.Revision -isnot [string] -or $Value.Revision -cne 'kernel-matrix-v1') { throw 'KERNEL_MATRIX_EVIDENCE_INVALID' }
        Assert-KernelMatrixInteger $Value.Objects 21 21
        Assert-KernelMatrixInteger $Value.Requests 63 63
        foreach ($field in @('Decoded','DecodeFailures','CaptureFailures','CaptureLimited')) { Assert-KernelMatrixInteger $Value.$field 0 63 }
        Assert-KernelMatrixInteger $Value.Captured 0 6
        if ($Value.Decoded + $Value.DecodeFailures -ne $Value.Requests -or
            $Value.Captured + $Value.CaptureFailures + $Value.CaptureLimited -ne $Value.DecodeFailures -or
            $Value.Rows -isnot [array] -or $Value.Rows.Count -ne 63) { throw 'KERNEL_MATRIX_EVIDENCE_INVALID' }

        # This literal table mirrors the fixed decoder result codes, never arbitrary text.
        $invalid = @('RecordNumber','RecordLength','Signature','RecordHeader','FixupMismatch','Sequence','AttributeHeader',
            'AttributeLength','DuplicateAttributeId','AttributeName','ResidentBounds','MappingBounds','MappingPairs',
            'SparseMappingFlags','StandardInformation','FileName','DataSize','MissingEndMarker','AllocationGeometry')
        $unsupported = @('NtfsVersion','FixupStride','ClusterGeometry','RecordFlags','AttributeLimit','AttributeForm',
            'AttributeFlags','MetadataFlags','NameLimit','MultipleUnnamedData','AttributeType')
        $expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach ($phase in @('A','B','C')) {
            $lengths = if ($phase -ceq 'A') { @(8,24,64) } else { @(8,64) }
            foreach ($kind in @('Ordinary','Compressed','Sparse')) {
                foreach ($length in $lengths) {
                    foreach ($stage in @('Initial','Reopened','Flushed')) {
                        $expected.Add("$phase-$kind-$length-$stage", @($stage,$kind,$length))
                    }
                }
            }
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $rows = [Collections.Generic.List[object]]::new()
        $decoded = 0
        foreach ($row in $Value.Rows) {
            Assert-KernelMatrixFields $row @('CaseId','Stage','Kind','NameLength','Status','Code','ProtectedTails','RestoredTails','Sectors','UsedBytes','VisibleNames')
            if ($row.CaseId -isnot [string] -or $row.CaseId.Length -gt 40 -or -not $expected.ContainsKey($row.CaseId) -or -not $seen.Add($row.CaseId)) { throw 'KERNEL_MATRIX_EVIDENCE_INVALID' }
            $case = $expected[$row.CaseId]
            if ($row.Stage -isnot [string] -or $row.Stage -cne $case[0] -or $row.Kind -isnot [string] -or $row.Kind -cne $case[1]) { throw 'KERNEL_MATRIX_EVIDENCE_INVALID' }
            Assert-KernelMatrixInteger $row.NameLength ([long]$case[2]) ([long]$case[2])
            if ($row.Status -isnot [string] -or $row.Code -isnot [string]) { throw 'KERNEL_MATRIX_EVIDENCE_INVALID' }
            $validCode = switch -CaseSensitive ($row.Status) {
                'Decoded' { $row.Code -ceq 'Decoded' }
                'Free' { $row.Code -ceq 'FreeRecord' }
                'Invalid' { $row.Code -cin $invalid }
                'Unsupported' { $row.Code -cin $unsupported }
                default { $false }
            }
            if (-not $validCode) { throw 'KERNEL_MATRIX_EVIDENCE_INVALID' }
            Assert-KernelMatrixInteger $row.Sectors 2 128
            Assert-KernelMatrixInteger $row.ProtectedTails 0 $row.Sectors
            Assert-KernelMatrixInteger $row.RestoredTails 0 $row.Sectors
            Assert-KernelMatrixInteger $row.UsedBytes -1 65536
            if ($row.UsedBytes -gt $row.Sectors * 512) { throw 'KERNEL_MATRIX_EVIDENCE_INVALID' }
            if ($row.Status -ceq 'Decoded') { Assert-KernelMatrixInteger $row.VisibleNames 0 256; $decoded++ }
            else { Assert-KernelMatrixInteger $row.VisibleNames -1 -1 }
            $rows.Add([ordered]@{CaseId=[string]$row.CaseId;Stage=[string]$row.Stage;Kind=[string]$row.Kind;NameLength=[int]$row.NameLength;
                Status=[string]$row.Status;Code=[string]$row.Code;ProtectedTails=[int]$row.ProtectedTails;RestoredTails=[int]$row.RestoredTails;
                Sectors=[int]$row.Sectors;UsedBytes=[int]$row.UsedBytes;VisibleNames=[int]$row.VisibleNames})
        }
        if ($decoded -ne $Value.Decoded) { throw 'KERNEL_MATRIX_EVIDENCE_INVALID' }
        return [ordered]@{Revision='kernel-matrix-v1';Objects=21;Requests=63;Decoded=[int]$Value.Decoded;DecodeFailures=[int]$Value.DecodeFailures;
            Captured=[int]$Value.Captured;CaptureFailures=[int]$Value.CaptureFailures;CaptureLimited=[int]$Value.CaptureLimited;Rows=$rows.ToArray()}
    }
    catch { throw 'KERNEL_MATRIX_EVIDENCE_INVALID' }
}
