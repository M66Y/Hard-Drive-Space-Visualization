#requires -Version 7.4
# Pure transport scenarios. Native labels below are invented data, not I/O proof.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$checks=0
function Check([bool]$Value,[string]$Name) {
    if(-not $Value){throw ('FAIL '+$Name)}
    $script:checks++; Write-Output ('PASS fixture fast evidence '+$Name)
}
function Load-Function([string]$Relative,[string]$Name) {
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path (Join-Path $PSScriptRoot $Relative)).Path,[ref]$tokens,[ref]$errors)
    Check ($errors.Count -eq 0) ('syntax '+$Name)
    $f=@($ast.EndBlock.Statements|Where-Object{$_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -ceq $Name})
    Check ($f.Count -eq 1) ('unique function '+$Name)
    return $f[0].Extent.Text
}
. ([scriptblock]::Create((Load-Function '../scripts/ntfs-prototype/FixtureFastScanEvidence.ps1' 'ConvertTo-FixtureFastScanEvidence'|Select-Object -Last 1)))
. ([scriptblock]::Create((Load-Function '../scripts/ntfs-prototype/MftEnumerationEvidence.ps1' 'ConvertTo-MftEnumerationEvidence'|Select-Object -Last 1)))
. ([scriptblock]::Create((Load-Function '../scripts/ntfs-prototype/KernelMatrixEvidence.ps1' 'Assert-UniqueJsonKeys'|Select-Object -Last 1)))
# Reuse only the pure fixture constructor; never execute its test suite or script imports.
. ([scriptblock]::Create((Load-Function 'NtfsBitmapBootstrapEvidence.Tests.ps1' 'Fixture'|Select-Object -Last 1)))
function FastFixture([string]$Form='Nonresident') {
    $range=(Fixture).Range
    $r=$range.Enumeration
    $r.Decoded=44;$r.Free=16;$r.Invalid=2;$r.Unsupported=2;$r.AllZeroInvalid=2
    $r.CodeCounts=@([pscustomobject]@{Status='Decoded';Code='Decoded';Count=44},
        [pscustomobject]@{Status='Free';Code='FreeRecord';Count=16},
        [pscustomobject]@{Status='Invalid';Code='Signature';Count=2},
        [pscustomobject]@{Status='Unsupported';Code='AttributeType';Count=2})
    $calls=if($Form -ceq 'Nonresident'){1}else{0};$bytes=if($calls -eq 1){512}else{0}
    [pscustomobject]@{Revision='fixture-fast-scan-v1';Stage='ControlledFixtureCompared';Range=$range;
        Bitmap=[pscustomobject]@{Form=$Form;PrefixBytes=8;CoveredSlots=64;ReadCalls=$calls;RequestedBytes=$bytes;
            TargetsAllocated=21;ReleasedUnallocated=2;ReleasedFreeHeaders=2;Corroborated=$true};
        Index=[pscustomobject]@{Revision='fixture-directory-index-v1';Scope='FixedFixtureDataRoot';DecodedRecords=44;
            FileObjects=21;DirectoryObjects=21;FileEntries=27;DosAliasesIgnored=4;KnownLogicalBytes=21233664L;
            KnownAllocatedBytes=1048576L;UnknownLogicalObjects=0;UnknownAllocatedObjects=0;RecordKnownAllocatedObjects=7;
            NativeFallbackAllocatedObjects=14;LookupObjects=16;CompleteWithinScope=$true};
        Comparison=[pscustomobject]@{Revision='fixture-scanner-comparison-v1';ScannerComplete=$true;EntriesMatched=$true;
            CountsMatched=$true;LogicalMatched=$true;AllocatedMatched=$true;Matched=$true;ScannerFileEntries=27;
            ScannerDirectories=21;ScannerLogicalBytes=21233664L;ScannerAllocatedBytes=1048576L};
        Resources=[pscustomobject]@{ReadCalls=(2+$calls);RequestedBytes=(66560L+$bytes);Closed=$true}}
}
function Rejected($Value,[string]$Name) {
    $message=$null
    try{ConvertTo-FixtureFastScanEvidence $Value|Out-Null}catch{$message=$_.Exception.Message}
    Check ($message -ceq 'FIXTURE_FAST_SCAN_EVIDENCE_INVALID') $Name
}
foreach($form in @('Resident','Nonresident')) {
    $v=FastFixture $form;$safe=ConvertTo-FixtureFastScanEvidence $v
    Check ($safe.Bitmap.Form -ceq $form -and $safe.Resources.Closed -and $safe.Comparison.Matched) 'form accepts only corroborated closed comparison'
    $round=$safe|ConvertTo-Json -Depth 20|ConvertFrom-Json
    Check ((ConvertTo-FixtureFastScanEvidence $round).Index.FileEntries -eq 27) 'deep JSON roundtrip'
    $v.Index.KnownLogicalBytes=1
    Check ($safe.Index.KnownLogicalBytes -eq 21233664) 'safe output does not alias mutable inputs'
}
foreach($path in @('', 'Bitmap','Index','Comparison','Resources','Range','Range.Enumeration')) {
    $v=FastFixture;$object=$v
    if($path){foreach($part in $path.Split('.')){$object=$object.$part}}
    foreach($property in @($object.PSObject.Properties.Name)) {
        $copy=FastFixture|ConvertTo-Json -Depth 20|ConvertFrom-Json;$target=$copy
        if($path){foreach($part in $path.Split('.')){$target=$target.$part}}
        $target.PSObject.Properties.Remove($property)
        Rejected $copy ('missing '+$path+'.'+$property)
    }
    $object|Add-Member -NotePropertyName PrivatePayload -NotePropertyValue 'SyntheticPrivateMarker'
    Rejected $v ('extra field '+$path)
}
foreach($path in @('Bitmap','Index','Comparison','Resources')) {
    $v=FastFixture;$object=$v.$path
    $first=@($object.PSObject.Properties.Name)[0]
    $object.PSObject.Properties.Remove($first)
    $object|Add-Member -MemberType ScriptProperty -Name $first -Value {throw 'SyntheticPrivateMarker'}
    Rejected $v ('accessor is rejected without invocation '+$path)
}
foreach($pair in @(@('Bitmap','PrefixBytes'),@('Bitmap','CoveredSlots'),@('Bitmap','ReadCalls'),@('Bitmap','RequestedBytes'),
    @('Bitmap','TargetsAllocated'),@('Bitmap','ReleasedUnallocated'),@('Bitmap','ReleasedFreeHeaders'),
    @('Index','DecodedRecords'),@('Index','FileObjects'),@('Index','DirectoryObjects'),@('Index','FileEntries'),
    @('Index','DosAliasesIgnored'),@('Index','KnownLogicalBytes'),@('Index','KnownAllocatedBytes'),
    @('Index','UnknownLogicalObjects'),@('Index','UnknownAllocatedObjects'),@('Index','RecordKnownAllocatedObjects'),
    @('Index','NativeFallbackAllocatedObjects'),@('Index','LookupObjects'),@('Comparison','ScannerFileEntries'),
    @('Comparison','ScannerDirectories'),@('Comparison','ScannerLogicalBytes'),@('Comparison','ScannerAllocatedBytes'),
    @('Resources','ReadCalls'),@('Resources','RequestedBytes'))) {
    foreach($bad in @($null,$true,'1',1.5,-1,[long]::MaxValue)) {
        $v=FastFixture;$v.($pair[0]).($pair[1])=$bad
        Rejected $v ('bounded integer '+$pair[0]+'.'+$pair[1])
    }
}
foreach($pair in @(@('Bitmap','Corroborated'),@('Index','CompleteWithinScope'),@('Comparison','ScannerComplete'),
    @('Comparison','EntriesMatched'),@('Comparison','CountsMatched'),@('Comparison','LogicalMatched'),
    @('Comparison','AllocatedMatched'),@('Comparison','Matched'),@('Resources','Closed'))) {
    foreach($bad in @($false,$null,1,'true')) {
        $v=FastFixture;$v.($pair[0]).($pair[1])=$bad
        Rejected $v ('required boolean '+$pair[0]+'.'+$pair[1])
    }
}
foreach($action in @(
    {$args[0].Revision='bitmap-bootstrap-source-v1'},{$args[0].Stage='SnapshotComplete'},
    {$args[0].Range.EvidenceKind='SyntheticFixture'},{$args[0].Range.Closed=$false},
    {$args[0].Bitmap.Form='Unknown'},{$args[0].Bitmap.PrefixBytes=7},{$args[0].Bitmap.CoveredSlots=63},
    {$args[0].Bitmap.ReadCalls=0},{$args[0].Bitmap.RequestedBytes=0},{$args[0].Bitmap.ReleasedFreeHeaders=1},
    {$args[0].Resources.ReadCalls=4},{$args[0].Resources.RequestedBytes=67073},
    {$args[0].Index.RecordKnownAllocatedObjects=21;$args[0].Index.NativeFallbackAllocatedObjects=0},
    {$args[0].Index.KnownLogicalBytes++},{$args[0].Index.KnownAllocatedBytes++},
    {$args[0].Comparison.ScannerLogicalBytes++},{$args[0].Comparison.ScannerAllocatedBytes++}
)) {$v=FastFixture;& $action $v;Rejected $v 'contradiction or foreign stage rejected'}
foreach($bad in @($null,1,'SyntheticPrivateMarker',@(),@{})) {Rejected $bad 'root shape rejected'}
$json=FastFixture|ConvertTo-Json -Depth 20 -Compress
foreach($bad in @($json.Replace('"Stage":','"Stage":"Injected","Stage":'),$json.Replace('"Form":','"form":"Injected","Form":'))) {
    $caught=$false;try{Assert-UniqueJsonKeys $bad}catch{$caught=$true};Check $caught 'duplicate and case-variant keys rejected before conversion'
}
Write-Output ('RESULT: PASS ('+$checks+' checks)')
