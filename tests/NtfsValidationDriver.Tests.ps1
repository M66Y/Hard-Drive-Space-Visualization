#requires -Version 7.4
# Executes the actual driver preflight and orchestration AST with a harmless
# PowerShell child. No elevation, disk image, storage cmdlet, or VSS is invoked.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$checks=0
function Check([bool]$Condition,[string]$Name){if(-not $Condition){throw ('FAIL '+$Name)};$script:checks++;Write-Output ('PASS NTFS validation driver '+$Name)}
$source=Join-Path $PSScriptRoot '../scripts/ntfs-prototype/Invoke-NtfsValidation.ps1'
$sourceText=Get-Content -LiteralPath $source -Raw
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($sourceText,[ref]$tokens,[ref]$errors)
Check ($errors.Count -eq 0) 'driver syntax'
$functions=@($ast.EndBlock.Statements|Where-Object {$_ -is [Management.Automation.Language.FunctionDefinitionAst]})
foreach($name in @('SafePath','State','Completed','MetadataEvidence','ReadMetadata')){
    $definition=@($functions|Where-Object Name -CEQ $name)
    Check ($definition.Count -eq 1) ('actual driver function '+$name)
    . ([scriptblock]::Create($definition[0].Extent.Text))
}
$main=@($ast.EndBlock.Statements|Where-Object {$_ -is [Management.Automation.Language.TryStatementAst]})
Check ($main.Count -eq 1) 'one actual orchestration try statement'
$orchestrate=[scriptblock]::Create($main[0].Extent.Text)
$start=[regex]::Match($sourceText,'(?m)^\$StatePath\s*=\s*SafePath').Index
$principal=[regex]::Match($sourceText,'(?m)^\$principal\s*=').Index
Check ($start -gt 0 -and $principal -gt $start) 'preflight slice excludes privilege and native execution'
$preflight=[scriptblock]::Create($sourceText.Substring($start,$principal-$start))
$guardSource=Join-Path $PSScriptRoot '../scripts/ntfs-prototype/KernelMatrixEvidence.ps1'
$guardAst=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $guardSource).Path,[ref]$tokens,[ref]$errors)
$guard=@($guardAst.EndBlock.Statements|Where-Object {$_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -ceq 'Assert-UniqueJsonKeys'})
Check ($errors.Count -eq 0 -and $guard.Count -eq 1) 'actual duplicate JSON guard'
. ([scriptblock]::Create($guard[0].Extent.Text))
$nonce=[guid]::NewGuid().ToString('N')
$taskRoot=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('AsukaLensValidationDriver-'+$nonce)))
$artifacts=Join-Path $taskRoot 'artifacts'
[void][IO.Directory]::CreateDirectory($artifacts)
$pwsh=(Get-Command pwsh.exe -CommandType Application -ErrorAction Stop).Source
$runner=Join-Path $taskRoot 'child.ps1'
$configPath=Join-Path $taskRoot 'config.json'
$invocations=Join-Path $taskRoot 'calls.jsonl'
$metadataChild=Join-Path $taskRoot 'metadata.ps1'
function Paths {
    return [pscustomobject]@{State=(Join-Path $artifacts ('ntfs-validation-result-'+[guid]::NewGuid().ToString('N')+'.json'));
        Small=(Join-Path $artifacts ('ntfs-live-result-'+[guid]::NewGuid().ToString('N')+'.json'));
        Large=(Join-Path $artifacts ('ntfs-live-result-'+[guid]::NewGuid().ToString('N')+'.json'));
        Snapshot=(Join-Path $artifacts ('ntfs-live-result-'+[guid]::NewGuid().ToString('N')+'.json'));
        Metadata=(Join-Path $artifacts ('ntfs-personal-metadata-'+[guid]::NewGuid().ToString('N')+'.json'))}
}
function RunPreflight($paths,[string]$SelectedRoot='', [string]$PrivateResult=''){
    $StatePath=$paths.State;$SmallResult=$paths.Small;$LargeResult=$paths.Large;$SnapshotResult=$paths.Snapshot
    $MetadataRoot=$SelectedRoot;$MetadataResult=$PrivateResult
    & $preflight
}
function Rejected([scriptblock]$Action,[string]$Code,[string]$Name){$message=$null;try{& $Action|Out-Null}catch{$message=$_.Exception.Message};Check ($message -ceq $Code) $Name}
function DeleteOwned([string]$Path){
    $full=[IO.Path]::GetFullPath($Path)
    if($full -cne $taskRoot -and -not $full.StartsWith($taskRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::Ordinal)){throw 'TEST_CLEANUP_SCOPE'}
    $item=Get-Item -LiteralPath $full -Force
    if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){
        if($full -ceq $taskRoot){throw 'TEST_ROOT_REPARSE'}
        if($item.PSIsContainer){[IO.Directory]::Delete($full)}else{[IO.File]::Delete($full)}
        return
    }
    if($item.PSIsContainer){foreach($child in Get-ChildItem -LiteralPath $full -Force){DeleteOwned $child.FullName};[IO.Directory]::Delete($full)}else{[IO.File]::Delete($full)}
}
try {
    [IO.File]::WriteAllText($runner,@'
param([string]$ResultPath,[int]$FileCount,[switch]$Snapshot,[int]$SnapshotFileCount)
$ErrorActionPreference='Stop'
$config=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Raw|ConvertFrom-Json
$phase=if($Snapshot){'snapshot'}elseif($FileCount -eq 10000 -or $SnapshotFileCount -eq 10000){'small'}elseif($FileCount -eq 100000 -or $SnapshotFileCount -eq 100000){'large'}else{throw 'STUB_UNEXPECTED_ARGUMENT'}
[IO.File]::AppendAllText((Join-Path $PSScriptRoot 'calls.jsonl'),($phase+[Environment]::NewLine))
$status=if($Snapshot){'vss_verified'}elseif($SnapshotFileCount){'vss_scale_benchmarked'}else{'scale_benchmarked'}
$value=[ordered]@{schema=1;mode=$(if($Snapshot){'fixture-vss'}elseif($SnapshotFileCount){'fixture-vss-scale'}else{'fixture-scale'});status=$status;stage='SyntheticStub';code='None';exit_code=0;detached=$true;cleanup_completed=$true}
if($Snapshot -or $SnapshotFileCount){$value.snapshot_cleanup_confirmed=$true}
if($Snapshot){$value.vss_evidence=$null}
if($config.StopAt -ceq $phase){
    switch($config.Kind){
        'exit' {exit 1}
        'missing' {exit 0}
        'cleanup' {$value.cleanup_completed=$false}
        'detach' {$value.detached=$false}
        'status' {$value.status='failed'}
        'schema' {$value.schema=2}
        'code' {$value.code='SyntheticFailure'}
        'snapshot' {$value.snapshot_cleanup_confirmed=$false}
        'mode' {$value.mode='fixture-scale'}
        'duplicate' {[IO.File]::WriteAllText($ResultPath,'{"schema":1,"status":"failed","status":"scale_benchmarked","code":"None","exit_code":0,"detached":true,"cleanup_completed":true,"mode":"fixture-scale"}');exit 0}
    }
}
[IO.File]::WriteAllText($ResultPath,($value|ConvertTo-Json -Depth 5))
exit 0
'@,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($metadataChild,@'
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
$ErrorActionPreference='Stop'
if($Arguments.Count -ne 3 -or $Arguments[0] -cne '--ntfs-volume-preflight' -or $Arguments[1] -cne 'S:\' -or $Arguments[2] -cne '--read-metadata-handle'){exit 11}
$config=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Raw|ConvertFrom-Json
[IO.File]::AppendAllText((Join-Path $PSScriptRoot 'calls.jsonl'),('metadata'+[Environment]::NewLine))
[Console]::Error.WriteLine('SyntheticPrivateStderr')
switch($config.MetadataKind){
    'oversize' {[Console]::Write(('x'*65537));Start-Sleep -Seconds 1;exit 0}
    'text' {[Console]::Write('SyntheticPrivateException');exit 1}
    'duplicate' {[Console]::Write('{"Status":"Unavailable","status":"MetadataAvailable"}');exit 2}
}
$value=[ordered]@{Revision='volume-preflight-v1';Status='MetadataAvailable';Code='None';NativeError=0;FileSystem='NTFS';OpenedWithZeroAccess=$false;
    SectorBytes=512;ClusterBytes=4096;RecordBytes=1024;VolumeBytes=[long]1073741824;MftValidDataBytes=[long]1048576;MftSlots=[long]1024}
if($config.MetadataKind -eq 'unavailable'){$value.Status='Unavailable';$value.Code='PreflightQuery';$value.NativeError=5;foreach($f in @('SectorBytes','ClusterBytes','RecordBytes','VolumeBytes','MftValidDataBytes','MftSlots')){$value[$f]=$null}}
[Console]::Write(($value|ConvertTo-Json -Compress))
if($config.MetadataKind -eq 'unavailable'){exit 2};exit 0
'@,[Text.UTF8Encoding]::new($false))
    $paths=Paths;RunPreflight $paths
    Check (-not(Test-Path -LiteralPath $paths.State)) 'fresh argument validation does not create result artifacts'
    Check ((SafePath $paths.Small '^ntfs-live-result-[0-9a-f]{32}\.json$') -ceq $paths.Small) 'fresh immediate artifact path accepted'
    foreach($bad in @((Join-Path $taskRoot ('ntfs-live-result-'+[guid]::NewGuid().ToString('N')+'.json')),
        (Join-Path $artifacts 'wrong.json'),(Join-Path $artifacts ('ntfs-live-result-'+[guid]::NewGuid().ToString('N')+'.json.tmp')))){
        Rejected {SafePath $bad '^ntfs-live-result-[0-9a-f]{32}\.json$'} 'VALIDATION_PATH' 'outside or malformed result path rejected'
    }
    $paths=Paths;$paths.Large=$paths.Small
    Rejected {RunPreflight $paths} 'VALIDATION_DUPLICATE_PATH' 'all four result paths must be distinct'
    foreach($field in @('State','Small','Large','Snapshot')){
        $paths=Paths;[IO.File]::WriteAllText($paths.$field,'synthetic')
        Rejected {RunPreflight $paths} 'VALIDATION_RESULT_EXISTS' 'existing result is never reused or overwritten by preflight'
    }
    $paths=Paths
    Rejected {RunPreflight $paths 'S:\' ''} 'VALIDATION_METADATA_ARGUMENTS' 'metadata root requires its private result destination'
    Rejected {RunPreflight $paths '' $paths.Metadata} 'VALIDATION_METADATA_ARGUMENTS' 'metadata destination never triggers an implicit root query'
    foreach($bad in @('S:','S:\folder','\\server\share\','\\.\S:','S:/','S:\\')){
        Rejected {RunPreflight $paths $bad $paths.Metadata} 'VALIDATION_METADATA_ROOT' 'metadata accepts only an explicit local drive root'
    }
    RunPreflight $paths 'S:\' $paths.Metadata
    Check (-not(Test-Path -LiteralPath $paths.Metadata)) 'metadata argument validation does not read a drive or create a report'
    [IO.File]::WriteAllText($paths.Metadata,'synthetic')
    Rejected {RunPreflight $paths 'S:\' $paths.Metadata} 'VALIDATION_RESULT_EXISTS' 'existing private metadata report cannot be overwritten'
    $paths=Paths
    Rejected {RunPreflight $paths 'S:\' (Join-Path $taskRoot ('ntfs-personal-metadata-'+[guid]::NewGuid().ToString('N')+'.json'))} 'VALIDATION_PATH' 'metadata report must remain inside local ignored artifacts'
    Rejected {RunPreflight $paths 'S:\' $paths.Small} 'VALIDATION_PATH' 'metadata cannot reuse a public result filename'
    $target=Join-Path $taskRoot 'junction-target';[void][IO.Directory]::CreateDirectory($target)
    $junction=Join-Path $taskRoot 'junction'
    [void](New-Item -ItemType Junction -Path $junction -Target $target)
    $savedArtifacts=$artifacts;$artifacts=$junction
    $linkResult=Join-Path $junction ('ntfs-live-result-'+[guid]::NewGuid().ToString('N')+'.json')
    Rejected {SafePath $linkResult '^ntfs-live-result-[0-9a-f]{32}\.json$'} 'VALIDATION_REPARSE' 'reparse ancestor rejected before any child access'
    $artifacts=$savedArtifacts
    [IO.Directory]::Delete($junction)

    function RunCase([string]$StopAt,[string]$Kind,[string[]]$Expected,[string]$ExpectedStatus,[bool]$SnapshotScale=$false,[string]$MetadataKind=''){
        $paths=Paths
        $StatePath=$paths.State;$SmallResult=$paths.Small;$LargeResult=$paths.Large;$SnapshotResult=$paths.Snapshot
        $UseSnapshotScale=$SnapshotScale;$MetadataRoot='';$MetadataResult='';$dotnet=$pwsh;$testsDll=$metadataChild
        if($MetadataKind){$MetadataRoot='S:\';$MetadataResult=$paths.Metadata}
        & $preflight
        if(Test-Path -LiteralPath $invocations){Remove-Item -LiteralPath $invocations -Force}
        [IO.File]::WriteAllText($configPath,([pscustomobject]@{StopAt=$StopAt;Kind=$Kind;MetadataKind=$MetadataKind}|ConvertTo-Json))
        $phase='Scale10000';$status='running';$code='None'
        # Execute the actual try/catch/finally, not a second model of its loop.
        . $orchestrate
        $calls=@(Get-Content -LiteralPath $invocations)
        Check (($calls -join ',') -ceq ($Expected -join ',')) 'actual child order stops at the first unaccepted step'
        $state=Get-Content -LiteralPath $StatePath -Raw|ConvertFrom-Json
        Check ($state.status -ceq $ExpectedStatus) 'actual final state reports passed versus failed accurately'
        Check ($state.code -ceq $(if($ExpectedStatus -ceq 'passed'){'None'}else{'ControlledValidationFailed'})) 'driver state contains only fixed outcome codes'
        Check ($state.phase -ceq $(if($ExpectedStatus -ceq 'passed'){'Complete'}elseif($StopAt -ceq 'metadata'){'Metadata'}elseif($StopAt -ceq 'small'){$(if($SnapshotScale){'SnapshotScale10000'}else{'Scale10000'})}elseif($StopAt -ceq 'large'){$(if($SnapshotScale){'SnapshotScale100000'}else{'Scale100000'})}else{'Snapshot'})) 'failed phase is not advanced to later work'
        $stateText=Get-Content -LiteralPath $StatePath -Raw
        Check ($stateText -cnotmatch 'S:|MetadataAvailable|PreflightQuery|SyntheticPrivate|MftSlots|VolumeBytes') 'public state excludes private root metadata and stderr'
        if($MetadataKind -cin @('available','unavailable')){
            $private=Get-Content -LiteralPath $MetadataResult -Raw|ConvertFrom-Json
            Check ($private.Status -ceq $(if($MetadataKind -ceq 'available'){'MetadataAvailable'}else{'Unavailable'}) -and
                @($private.PSObject.Properties).Count -eq 12) 'only sanitized private metadata is stored separately'
        }else{Check (-not(Test-Path -LiteralPath $paths.Metadata)) 'default and rejected metadata never create private output'}
    }
    RunCase '' '' @('small','large','snapshot') 'passed'
    foreach($kind in @('exit','missing','cleanup','detach','status','schema','code','duplicate')){
        RunCase 'small' $kind @('small') 'failed'
    }
    foreach($kind in @('exit','cleanup','detach')){RunCase 'large' $kind @('small','large') 'failed'}
    foreach($kind in @('exit','cleanup','detach','snapshot')){RunCase 'snapshot' $kind @('small','large','snapshot') 'failed'}
    RunCase '' '' @('small','large','snapshot') 'passed' $true
    foreach($kind in @('exit','missing','cleanup','detach','snapshot','mode')){RunCase 'small' $kind @('small') 'failed' $true}
    RunCase 'large' 'snapshot' @('small','large') 'failed' $true
    RunCase '' '' @('metadata','small','large','snapshot') 'passed' $true 'available'
    RunCase '' '' @('metadata','small','large','snapshot') 'passed' $true 'unavailable'
    foreach($kind in @('text','duplicate','oversize')){RunCase 'metadata' '' @('metadata') 'failed' $true $kind}
    function MetadataValue {
        return [pscustomobject]@{Revision='volume-preflight-v1';Status='MetadataAvailable';Code='None';NativeError=0;FileSystem='NTFS';OpenedWithZeroAccess=$false;
            SectorBytes=512;ClusterBytes=4096;RecordBytes=1024;VolumeBytes=1073741824L;MftValidDataBytes=1048576L;MftSlots=1024L}
    }
    function BadMetadata([scriptblock]$Mutation){$v=MetadataValue;& $Mutation $v;Rejected {MetadataEvidence $v} 'VALIDATION_METADATA_EVIDENCE' 'metadata fields and relationships reject untrusted content'}
    foreach($field in @((MetadataValue).PSObject.Properties.Name)){BadMetadata {param($v)$v.PSObject.Properties.Remove($field)}}
    foreach($field in @('SectorBytes','ClusterBytes','RecordBytes','VolumeBytes','MftValidDataBytes','MftSlots','NativeError')){
        foreach($bad in @('1',$true,[double]1,$null)){BadMetadata {param($v)$v.$field=$bad}}
    }
    foreach($field in @('Path','VolumeSerial','MftStartLcn','Name','Raw','StackTrace')){BadMetadata {param($v)$v|Add-Member -NotePropertyName $field -NotePropertyValue 'SyntheticPrivateMarker'}}
    foreach($pair in @(@('Revision','fixture-vss-v1'),@('Status','passed'),@('Code','SyntheticPrivateMarker'),@('FileSystem','NTFS private'),
        @('OpenedWithZeroAccess',$true),@('SectorBytes',513),@('ClusterBytes',512),@('RecordBytes',768),@('MftSlots',1025),@('MftValidDataBytes',1048577L))){
        if($pair[0] -ceq 'ClusterBytes'){$pair[1]=256}
        BadMetadata {param($v)$v.($pair[0])=$pair[1]}
    }
    # Exact integer division remains reliable beyond the floating-point safe-integer range.
    $v=MetadataValue;$v.VolumeBytes=9223372036854775296L;$v.MftValidDataBytes=9223372036854774784L;$v.MftSlots=9007199254740991L
    Check ((MetadataEvidence $v).MftSlots -eq 9007199254740991L) 'large metadata slot count uses exact integer division'
    $v.MftSlots=9007199254740992L
    Rejected {MetadataEvidence $v} 'VALIDATION_METADATA_EVIDENCE' 'large slot count cannot be rounded into a false match'
    Check (-not(Test-Path -LiteralPath $junction)) 'test junction is removed without traversing its target'
} finally {
    if(Test-Path -LiteralPath $taskRoot){DeleteOwned $taskRoot}
}
Check (-not(Test-Path -LiteralPath $taskRoot)) 'only this random owned test workspace is cleaned'
Write-Output ('RESULT: PASS ('+$checks+' checks)')
