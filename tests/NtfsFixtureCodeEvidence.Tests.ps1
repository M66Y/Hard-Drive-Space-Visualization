#requires -Version 7.4
# Pure result-code/summary checks. Never invokes the fixture script or storage commands.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:checks++; Write-Output ('PASS NTFS fixture code evidence ' + $Name)
}
$source = Join-Path $PSScriptRoot '../scripts/ntfs-prototype/FixtureCodeEvidence.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $source).Path, [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'semantic code converter syntax'
$functions = @($ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.FunctionDefinitionAst]})
Check ($functions.Count -eq 1 -and $ast.EndBlock.Statements.Count -eq 1) 'dot source defines only a pure converter'
. ([scriptblock]::Create($functions[0].Extent.Text))
function RejectSuccess($Code, $Status) {
    $message = $null
    try { ConvertTo-FixtureCodeEvidence $Code $Status | Out-Null } catch { $message = $_.Exception.Message }
    Check ($message -ceq 'FIXTURE_CODE_EVIDENCE_INVALID') 'success rejects a missing or non-None result code'
}
foreach ($status in @('passed','collected','verified')) {
    Check ((ConvertTo-FixtureCodeEvidence 'None' $status) -ceq 'None') 'successful fixture mode has exactly None code'
    foreach ($bad in @($null,'','none','OperationFailed','RawUnlockSucceeded','PrivateMarker123',$true,1,@('None'))) { RejectSuccess $bad $status }
}
foreach ($status in @('running','cleanup_failed','PASSED',$null,1,$true,@('passed'))) { RejectSuccess 'None' $status }
foreach ($code in @('FixtureGuard','FixtureIO','AccessDenied','ManifestJson','RawMetadataVolumeBinding',
    'RawUnlockSucceeded','RawPostNativeObjectStable','RawPostNativeAliasStable','RawPostNamedStreamContent',
    'RawLookupReasonsMatchFixture','RAW_EVIDENCE_MISSING','RAW_COMPARISON_EVIDENCE_INVALID','MATRIX_JSON_INVALID',
    'COMPARISON_FAILED','FIXTURE_CODE_EVIDENCE_INVALID','OperationFailed')) {
    Check ((ConvertTo-FixtureCodeEvidence $code 'failed') -ceq $code) 'known semantic failure code preserved'
}
foreach ($bad in @('PrivateMarker123','SECRET_IDENTIFIER','None','fixtureIO','FixtureIOPrivate','RawNative-Private-5',
    'LookupBatchComplete','LeaseCallBudget','SpecialEvidenceSource','SpecialCostNativeLeaseValues','MatrixFixedBudget',
    'RawStrict-Private','RawMftMap-Private','RawMftMap-MftRecord-Private','RawMetadataVolumeOpen-Private',
    'RawNative-LockFailed-5-Private','RawStrict-Decoded-Private',"FixtureIO`nPrivate",'C:\Synthetic\private.bin',
    ('x' * 80),('x' * 81),'',$null,$true,1,@('FixtureIO'))) {
    Check ((ConvertTo-FixtureCodeEvidence $bad 'failed') -ceq 'OperationFailed') 'unrecognized failure text is replaced without being forwarded'
}
foreach ($operation in @('LockFailed','AllocationFailed','SeekFailed','ReadFailed','ShortRead','BufferReleaseFailed')) {
    foreach ($errorCode in @(0,5,87,[int]::MaxValue)) {
        $code = 'RawNative-' + $operation + '-' + $errorCode
        Check ((ConvertTo-FixtureCodeEvidence $code 'failed') -ceq $code) 'native operation and nonnegative Int32 error are bounded'
    }
}
foreach ($number in @('-1','2147483648','4294967295','99999999999999999999','01','+1','1.0','1e3',' 5','5 ')) {
    Check ((ConvertTo-FixtureCodeEvidence ('RawNative-LockFailed-' + $number) 'failed') -ceq 'OperationFailed') 'invalid native numeric suffix is not forwarded'
}
foreach ($code in @('RawFreshVolumeData-F1-E0-B0','RawFreshVolumeData-F2-E87-B128','RawFreshVolumeData-F2-E2147483647-B104')) {
    Check ((ConvertTo-FixtureCodeEvidence $code 'failed') -ceq $code) 'raw metadata observation counters have bounded semantics'
}
foreach ($code in @('RawFreshVolumeData-F0-E0-B0','RawFreshVolumeData-F3-E0-B0','RawFreshVolumeData-F1-E2147483648-B0',
    'RawFreshVolumeData-F1-E0-B129','RawFreshVolumeData-F01-E0-B0','RawFreshVolumeData-F1-E0-B0-Private')) {
    Check ((ConvertTo-FixtureCodeEvidence $code 'failed') -ceq 'OperationFailed') 'raw metadata malformed counter rejected'
}
foreach ($phase in @('A','B','C')) {
    $lengths = if ($phase -ceq 'A') {@(8,24,64)} else {@(8,64)}
    foreach ($kind in @('Ordinary','Compressed','Sparse')) {
        foreach ($length in $lengths) {
            $code = "MatrixNativeAfterStable-$phase-$kind-$length-M126"
            Check ((ConvertTo-FixtureCodeEvidence $code 'failed') -ceq $code) 'fixed matrix identity and bounded change mask preserved'
        }
    }
}
Check ((ConvertTo-FixtureCodeEvidence 'MatrixNativeAfterStable-UnknownCase-M1' 'failed') -ceq 'MatrixNativeAfterStable-UnknownCase-M1') 'fixed unknown-case sentinel is controlled'
foreach ($code in @('MatrixNativeAfterStable-Private-M1','MatrixNativeAfterStable-B-Ordinary-24-M1',
    'MatrixNativeAfterStable-A-ordinary-8-M1','MatrixNativeAfterStable-A-Ordinary-8-M0',
    'MatrixNativeAfterStable-A-Ordinary-8-M128','MatrixNativeAfterStable-A-Ordinary-8-M01')) {
    Check ((ConvertTo-FixtureCodeEvidence $code 'failed') -ceq 'OperationFailed') 'unknown case or invalid matrix mask sanitized'
}
foreach ($code in @('DecodeKernelRecord-FixupMismatch','DecodeKernelRecord-FixupMismatch-Case6-P0-R1-S2',
    'DecodeKernelRecord-FixupMismatch-Case64-P128-R0-S128-U65536','RawStrict-DuplicateAttributeId',
    'RawMftMap-MftRecord-FixupMismatch','RawMftMap-MftRunCoverage','RawMftMap-MftArithmeticOverflow')) {
    Check ((ConvertTo-FixtureCodeEvidence $code 'failed') -ceq $code) 'old comparison and new raw strict controlled codes preserved'
}
foreach ($code in @('DecodeKernelRecord-FixupMismatch-Case0-P0-R1-S2-U512','DecodeKernelRecord-FixupMismatch-Case65-P0-R1-S2-U512',
    'DecodeKernelRecord-FixupMismatch-Case6-P3-R1-S2-U512','DecodeKernelRecord-FixupMismatch-Case6-P0-R3-S2-U512',
    'DecodeKernelRecord-FixupMismatch-Case6-P0-R1-S1-U512','DecodeKernelRecord-FixupMismatch-Case6-P0-R1-S2-U1025',
    'DecodeKernelRecord-FixupMismatch-Case6-P0-R1-S2-U000','DecodeKernelRecord-FixupMismatch-Case6-P0-R1-S2-Private')) {
    Check ((ConvertTo-FixtureCodeEvidence $code 'failed') -ceq 'OperationFailed') 'comparison counters reject injected or impossible values'
}

# Execute only the actual pure summary-code/gating/catch/export statements from
# the wrapper AST. The image creation/locking/cleanup commands are never loaded.
$wrapper = Join-Path $PSScriptRoot '../scripts/ntfs-prototype/Invoke-NtfsFixture.ps1'
$wrapperAst = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $wrapper).Path, [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'wrapper syntax after semantic-code integration'
$main = @($wrapperAst.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.TryStatementAst] -and $_.Body.Extent.Text.Contains("'AdministratorCheck'")})
Check ($main.Count -eq 1) 'identify fixture outer guarded operation'
$codeStatements = @($main[0].Body.Statements | Where-Object {$_.Extent.Text.StartsWith('$summaryCode =') -or $_.Extent.Text.StartsWith('$failureCode = ConvertTo-FixtureCodeEvidence')})
$gate = @($main[0].Body.Statements | Where-Object {$_ -is [Management.Automation.Language.IfStatementAst] -and $_.Extent.Text.Contains('$runnerExit -ne 0 -or $summary.status -cne $expectedStatus')})
Check ($codeStatements.Count -eq 2 -and $gate.Count -eq 1) 'actual summary code conversion precedes mode and exit gating'
$codeBlock = [scriptblock]::Create(($codeStatements.Extent.Text -join "`n"))
$gateBlock = [scriptblock]::Create($gate[0].Extent.Text)
$catchBlock = [scriptblock]::Create($main[0].CatchClauses[0].Body.Extent.Text.TrimStart('{').TrimEnd('}'))
$exports = @($main[0].Finally.Statements | Where-Object {$_.Extent.Text.StartsWith('$result.')})
Check ($exports.Count -ge 10) 'extract only final mode evidence export assignments'
$exportBlock = [scriptblock]::Create(($exports.Extent.Text -join "`n"))
function SummaryResult($Code, [string]$SummaryStatus, [string]$Expected, [int]$ExitCode, [bool]$Missing = $false, [bool]$CleanupFailure = $false) {
    $summary = [pscustomobject]@{status=$SummaryStatus;code=$Code}
    if ($Missing) {$summary.PSObject.Properties.Remove('code')}
    $status = 'failed'; $failureCode = 'None'; $runnerExit = $ExitCode; $failureCount = 0; $expectedStatus = $Expected
    $RawRecords = $Expected -ceq 'verified'; $KernelDiagnostics = $Expected -ceq 'collected'
    $rawEvidence = 'synthetic-raw'; $matrixEvidence = [pscustomobject]@{DecodeFailures=0}; $captureCleanup='EmptyRemoved'
    $lookupEvidence='synthetic-lookup'; $leaseEvidence='synthetic-lease'; $specialEvidence='synthetic-special'; $specialCostEvidence='synthetic-cost'
    try { . $codeBlock; . $gateBlock; $status=$Expected } catch { . $catchBlock }
    if ($CleanupFailure) {$status='cleanup_failed'}
    $result = [ordered]@{status=$status;code=$failureCode}
    . $exportBlock
    return $result
}
foreach ($mode in @('passed','collected','verified')) {
    $result = SummaryResult 'None' $mode $mode 0
    Check ($result.status -ceq $mode -and $result.code -ceq 'None') 'valid result keeps its own success mode'
    $result = SummaryResult 'PrivateMarker123' $mode $mode 0
    Check ($result.status -ceq 'failed' -and $result.code -ceq 'FIXTURE_CODE_EVIDENCE_INVALID' -and -not $result.raw_verified -and
        $null -eq $result.raw_comparison -and $null -eq $result.kernel_matrix -and $null -eq $result.lookup_experiment) 'invalid success code fails closed with no success evidence'
    $result = SummaryResult $null $mode $mode 0 -Missing $true
    Check ($result.status -ceq 'failed' -and $result.code -ceq 'FIXTURE_CODE_EVIDENCE_INVALID') 'missing success code fails closed'
    $result = SummaryResult 'None' $mode $mode 0 -CleanupFailure $true
    Check ($result.status -ceq 'cleanup_failed' -and -not $result.raw_verified -and $null -eq $result.raw_comparison -and
        $null -eq $result.kernel_matrix -and $null -eq $result.lookup_experiment) 'cleanup failure clears all successful mode summaries'
}
$result = SummaryResult 'PrivateMarker123' 'failed' 'verified' 1
Check ($result.status -ceq 'failed' -and $result.code -ceq 'OperationFailed' -and -not $result.raw_verified -and $null -eq $result.raw_comparison) 'unknown failure identifier is redacted and remains failed'
$result = SummaryResult 'RawNative-LockFailed-5' 'failed' 'verified' 1
Check ($result.status -ceq 'failed' -and $result.code -ceq 'RawNative-LockFailed-5') 'known raw failure remains a failure'
$result = SummaryResult 'None' 'passed' 'verified' 0
Check ($result.status -ceq 'failed' -and $result.code -ceq 'COMPARISON_FAILED' -and -not $result.raw_verified) 'comparison success cannot satisfy raw mode'
$result = SummaryResult 'None' 'verified' 'passed' 0
Check ($result.status -ceq 'failed' -and $null -eq $result.raw_comparison -and $null -eq $result.lookup_experiment) 'raw success cannot satisfy old comparison mode'
Write-Output ('PASS NTFS fixture code evidence checks: ' + $checks)
