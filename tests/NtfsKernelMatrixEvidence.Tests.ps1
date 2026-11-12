#requires -Version 7.4
# Pure-memory conversion checks: no disk fixture, volume, or native calls.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:checks++; Write-Output ('PASS NTFS kernel matrix evidence ' + $Name)
}
$source = Join-Path $PSScriptRoot '../scripts/ntfs-prototype/KernelMatrixEvidence.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $source).Path, [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'converter script syntax'
$functions = @($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] })
Check ($functions.Count -eq 2 -and $ast.EndBlock.Statements.Count -eq 2) 'dot source defines only pure evidence functions'
. ([scriptblock]::Create(($functions.Extent.Text -join "`n")))
Assert-UniqueJsonKeys '{"Objects":21,"Rows":[{"CaseId":"A"}]}'
Check $true 'raw JSON unique keys accepted'
foreach($json in @('{"Objects":21,"Objects":22}', '{"Objects":21,"objects":21}', '{"Rows":[{"CaseId":"A","CaseId":"B"}]}',
    '{"Rows":[{"CaseId":"A","caseid":"B"}]}','{"Rows":[{"Nested":{"X":1,"x":2}}]}','{','',(' ' * 65537))) {
    $message=$null;try { Assert-UniqueJsonKeys $json } catch { $message=$_.Exception.Message }
    Check ($message -ceq 'MATRIX_JSON_INVALID') 'raw duplicate malformed or oversized JSON rejected'
}
function Fixture {
    $rows = @(
        foreach ($phase in @('A','B','C')) {
            $lengths = if ($phase -ceq 'A') { @(8,24,64) } else { @(8,64) }
            foreach ($kind in @('Ordinary','Compressed','Sparse')) {
                foreach ($length in $lengths) {
                    foreach ($stage in @('Initial','Reopened','Flushed')) {
                        [pscustomobject]@{CaseId="$phase-$kind-$length-$stage";Stage=$stage;Kind=$kind;NameLength=$length;
                            Status='Decoded';Code='Decoded';ProtectedTails=2;RestoredTails=0;Sectors=2;UsedBytes=512;VisibleNames=1}
                    }
                }
            }
        }
    )
    [pscustomobject]@{Revision='kernel-matrix-v1';Objects=21;Requests=63;Decoded=63;DecodeFailures=0;Captured=0;CaptureFailures=0;CaptureLimited=0;Rows=$rows}
}
function Reject([scriptblock]$Mutation, [string]$Name) {
    $value=Fixture; & $Mutation $value
    $message=$null
    try { ConvertTo-KernelMatrixEvidence $value | Out-Null } catch { $message=$_.Exception.Message }
    Check ($message -ceq 'KERNEL_MATRIX_EVIDENCE_INVALID') $Name
}
$value=Fixture; $safe=ConvertTo-KernelMatrixEvidence $value
Check ($safe.Rows.Count -eq 63 -and $safe.Decoded -eq 63) 'complete fixed matrix preserved'
Check (($safe.Keys -join ',') -ceq 'Revision,Objects,Requests,Decoded,DecodeFailures,Captured,CaptureFailures,CaptureLimited,Rows') 'canonical root shape'
Check (($safe.Rows[0].Keys -join ',') -ceq 'CaseId,Stage,Kind,NameLength,Status,Code,ProtectedTails,RestoredTails,Sectors,UsedBytes,VisibleNames') 'canonical row shape'
$value.Rows[0].CaseId='private-marker';$value.Decoded=0
Check ($safe.Rows[0].CaseId -ceq 'A-Ordinary-8-Initial' -and $safe.Decoded -eq 63) 'independent deep whitelist copy'
Check ((ConvertTo-KernelMatrixEvidence (($safe|ConvertTo-Json -Depth 5)|ConvertFrom-Json)).Rows.Count -eq 63) 'JSON roundtrip'
$failure=Fixture
for($i=0;$i -lt 8;$i++){$failure.Rows[$i].Status='Invalid';$failure.Rows[$i].Code='FixupMismatch';$failure.Rows[$i].VisibleNames=-1}
$failure.Decoded=55;$failure.DecodeFailures=8;$failure.Captured=6;$failure.CaptureFailures=1;$failure.CaptureLimited=1
Check ((ConvertTo-KernelMatrixEvidence $failure).DecodeFailures -eq 8) 'decode failures remain valid collection evidence'
$unsupported=Fixture;$unsupported.Rows[0].Status='Unsupported';$unsupported.Rows[0].Code='AttributeList';$unsupported.Rows[0].VisibleNames=-1
$unsupported.Decoded=62;$unsupported.DecodeFailures=1;$unsupported.CaptureLimited=1
$unsupported.Rows[0].Code='AttributeType'
Check ((ConvertTo-KernelMatrixEvidence $unsupported).Rows[0].Status -ceq 'Unsupported') 'controlled unsupported outcome preserved'
$free=Fixture;$free.Rows[0].Status='Free';$free.Rows[0].Code='FreeRecord';$free.Rows[0].VisibleNames=-1
$free.Decoded=62;$free.DecodeFailures=1;$free.CaptureFailures=1
Check ((ConvertTo-KernelMatrixEvidence $free).Rows[0].Status -ceq 'Free') 'free outcome is a decode failure partition'
$bounds=Fixture;$bounds.Rows[0].Sectors=128;$bounds.Rows[0].UsedBytes=65536;$bounds.Rows[0].ProtectedTails=128;$bounds.Rows[0].RestoredTails=128;$bounds.Rows[0].VisibleNames=256
Check ((ConvertTo-KernelMatrixEvidence $bounds).Rows[0].UsedBytes -eq 65536) 'maximum diagnostics retained'
$bounds.Rows[0].UsedBytes=-1;$bounds.Rows[0].VisibleNames=0
Check ((ConvertTo-KernelMatrixEvidence $bounds).Rows[0].VisibleNames -eq 0) 'unknown used bytes and decoded missing names retained'
foreach($field in @('Path','Identity','Exception','Skipped')) { Reject {param($v) $v|Add-Member -NotePropertyName $field -NotePropertyValue 'private-marker'} ('root injection '+$field) }
foreach($field in @('Path','Identity','Exception','Name','Reference')) { Reject {param($v) $v.Rows[0]|Add-Member -NotePropertyName $field -NotePropertyValue 'private-marker'} ('row injection '+$field) }
foreach($field in @('Revision','Objects','Requests','Decoded','DecodeFailures','Captured','CaptureFailures','CaptureLimited','Rows')) {
    Reject {param($v) $v.PSObject.Properties.Remove($field)} ('missing root '+$field)
}
foreach($field in @('CaseId','Stage','Kind','NameLength','Status','Code','ProtectedTails','RestoredTails','Sectors','UsedBytes','VisibleNames')) {
    Reject {param($v) $v.Rows[0].PSObject.Properties.Remove($field)} ('missing row '+$field)
}
Reject {param($v) $v.PSObject.Properties.Remove('Objects');$v|Add-Member -NotePropertyName objects -NotePropertyValue 21} 'exact root casing'
Reject {param($v) $v.Rows[0].PSObject.Properties.Remove('CaseId');$v.Rows[0]|Add-Member -NotePropertyName caseId -NotePropertyValue 'A-Ordinary-8-Initial'} 'exact row casing'
Reject {param($v) $v.Revision='kernel-matrix-v2'} 'unknown revision'
Reject {param($v) $v.Revision=$true} 'revision type'
Reject {param($v) $v.Objects=20} 'object count fixed'
Reject {param($v) $v.Requests=62} 'request count fixed'
Reject {param($v) $v.Decoded=62} 'decoded partition relationship'
Reject {param($v) $v.CaptureFailures=1} 'capture partition relationship'
Reject {param($v) $v.Captured=7} 'capture upper bound'
foreach($field in @('Objects','Requests','Decoded','DecodeFailures','Captured','CaptureFailures','CaptureLimited')) {
    foreach($bad in @(-1,[long]::MaxValue,'1',$true,[double]1,[decimal]1)) {
        Reject {param($v) $v.$field=$bad} ('numeric root boundary '+$field+' '+$bad.GetType().Name)
    }
}
Reject {param($v) $v.Rows=$null} 'null rows'
Reject {param($v) $v.Rows=$v.Rows[0]} 'scalar rows'
Reject {param($v) $v.Rows=$v.Rows[0..61]} 'lost row'
Reject {param($v) $v.Rows=@($v.Rows)+@($v.Rows[0])} 'extra row'
Reject {param($v) $v.Rows[0]=$null} 'null row'
Reject {param($v) $v.Rows[0]=@{CaseId='A-Ordinary-8-Initial'}} 'hashtable row'
Reject {param($v) $v.Rows[1].CaseId=$v.Rows[0].CaseId} 'duplicate case id'
Reject {param($v) $v.Rows[0].CaseId='A-Ordinary-8-initial'} 'case id exact casing'
Reject {param($v) $v.Rows[0].CaseId='A-Ordinary-24-Reopened'} 'case identity metadata disagreement'
Reject {param($v) $v.Rows[0].CaseId='B-Ordinary-24-Initial'} 'unknown fixed object'
Reject {param($v) $v.Rows[0].CaseId='../private-marker'} 'injected case id'
Reject {param($v) $v.Rows[0].Stage='Reopened'} 'stage must match case'
Reject {param($v) $v.Rows[0].Kind='Sparse'} 'kind must match case'
Reject {param($v) $v.Rows[0].NameLength=64} 'length must match case'
Reject {param($v) $v.Rows[0].Status='decoded'} 'status exact casing'
Reject {param($v) $v.Rows[0].Code='Signature'} 'code must match status'
Reject {param($v) $v.Rows[0].Code='Decoded\nprivate-marker'} 'unknown code text'
Reject {param($v) $v.Rows[0].ProtectedTails=3} 'protected tails bounded by sector count'
Reject {param($v) $v.Rows[0].RestoredTails=3} 'restored tails bounded by sector count'
Reject {param($v) $v.Rows[0].Sectors=1} 'sector minimum'
Reject {param($v) $v.Rows[0].Sectors=129} 'sector maximum'
Reject {param($v) $v.Rows[0].UsedBytes=-2} 'used bytes sentinel bound'
Reject {param($v) $v.Rows[0].UsedBytes=1025} 'used bytes bounded by record bytes'
Reject {param($v) $v.Rows[0].VisibleNames=-1} 'decoded visible names nonnegative'
Reject {param($v) $v.Rows[0].VisibleNames=257} 'visible names maximum'
foreach($field in @('NameLength','ProtectedTails','RestoredTails','Sectors','UsedBytes','VisibleNames')) {
    foreach($bad in @('2',$true,[double]2,[decimal]2)) { Reject {param($v) $v.Rows[0].$field=$bad} ('numeric row type '+$field+' '+$bad.GetType().Name) }
}
Write-Output ('PASS NTFS kernel matrix evidence checks: '+$checks)
