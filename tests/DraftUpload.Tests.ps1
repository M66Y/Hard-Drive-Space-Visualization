#requires -Version 7.4
# Exercise the unmodified upload script through a native gh shim, without credentials/network.
# The package validator is a fixture double: actual ZIP/EXE acceptance belongs to PackageAcceptance.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../scripts/release/Release.Common.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$parent = Join-Path $repository 'artifacts'
$root = Join-Path $parent ('draft-flow-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$oldPath=$env:PATH; $oldRoot=$env:ASUKALENS_DRAFT_TEST_ROOT; $oldShell=$env:ASUKALENS_DRAFT_TEST_PWSH
$oldTelemetry=[Environment]::GetEnvironmentVariable('GH_TELEMETRY','Process')
$script:passed=[Collections.Generic.List[string]]::new()
function Check([bool]$Condition,[string]$Name) { if (-not $Condition) { throw ('FAIL '+$Name) }; $script:passed.Add($Name); Write-Host ('PASS '+$Name) }
function State { Get-Content -LiteralPath (Join-Path $script:case 'state.json') -Raw | ConvertFrom-Json -AsHashtable }
function Save($Value) { Write-ReleaseJson $Value (Join-Path $script:case 'state.json') }
function New-Case([string]$Scenario='normal') {
    $script:case=Join-Path $root ([guid]::NewGuid().ToString('N'))
    $script:assets=Join-Path $script:case 'assets'
    [IO.Directory]::CreateDirectory($script:assets) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $script:case 'server')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $script:case 'offline-fixture'),'offline synthetic content; never a distributable package')
    $files=@('AsukaLens.v1.4.2.zip','AsukaLens.v1.4.2.exe','AsukaLens.v1.4.2-source.zip','AsukaLens.v1.4.2-source.tar.gz')
    foreach($name in $files){ [IO.File]::WriteAllText((Join-Path $script:assets $name),'NONEXECUTABLE SYNTHETIC TEST FIXTURE '+$name) }
    $manifest=[ordered]@{version='v1.4.2';commit=('b'*40);runtime='10.0.14';local_test=$false;assets=@(Get-FileInventory $script:assets)}
    Write-ReleaseJson $manifest (Join-Path $script:assets 'release-manifest.json')
    [IO.File]::WriteAllText((Join-Path $script:assets 'SHA256SUMS.txt'),'synthetic checksums; validator double used')
    $script:notes=Join-Path $script:case 'notes.md'
    $template=[IO.File]::ReadAllText((Join-Path $repository 'docs/release-notes.template.md'))
    $noteFixture=$template.Replace('{{VERSION}}',$manifest.version).Replace('{{RUNTIME}}',$manifest.runtime).Replace('{{COMMIT}}',$manifest.commit).
        Replace('{{CHANGES_ZH}}','合成测试内容。').Replace('{{FIXES_ZH}}','合成修复内容。').Replace('{{CHANGES_EN}}','Synthetic changes.').Replace('{{FIXES_EN}}','Synthetic fixes.')
    [IO.File]::WriteAllText($script:notes,$noteFixture)
    $script:acceptance=Join-Path $script:case 'acceptance.json'
    Write-ReleaseJson @{commit=$manifest.commit;manifest_sha256=(Get-FileHash -LiteralPath (Join-Path $script:assets 'release-manifest.json')).Hash.ToLowerInvariant();checks=@{clean_windows_10_no_dotnet='passed';clean_windows_11_no_dotnet='passed';portable_workflow='passed';install_upgrade_uninstall='passed';failure_recovery='passed';source_builds='passed';privacy_and_licenses='passed'}} $script:acceptance
    Save @{scenario=$Scenario;commit=$manifest.commit;version=$manifest.version;release=$null;calls=@();created=0;uploaded=@();downloaded=0;interrupted=$false}
    $env:ASUKALENS_DRAFT_TEST_ROOT=$script:case
}
function Existing-Draft {
    $state=State
    $state.release=@{id=42;draft=$true;tag_name=$state.version;name=('AsukaLens '+$state.version);body=[IO.File]::ReadAllText($script:notes);assets=@()}
    Save $state
}
function Upload([switch]$Resume,[switch]$WhatIf,[switch]$AllowCleanEnvironmentWaiver) {
    try { & (Join-Path $root 'scripts/Upload-Draft.ps1') -Directory $script:assets -Notes $script:notes -Acceptance $script:acceptance -Resume:$Resume -WhatIf:$WhatIf -AllowCleanEnvironmentWaiver:$AllowCleanEnvironmentWaiver -Confirm:$false | Out-Null }
    finally { if ([Environment]::GetEnvironmentVariable('GH_TELEMETRY','Process') -cne 'log') { throw 'Caller telemetry environment was not restored.' } }
}
function Reject([scriptblock]$Action,[string]$Name) {
    $rejected=$false
    try { & $Action } catch { $rejected=$true }
    Check $rejected $Name
    Check (@(Get-ChildItem -LiteralPath $script:assets -Force -Directory).Count -eq 0) ($Name+' cleans download staging')
}
function No-Writes([string]$Name) { $state=State; Check ($state.created -eq 0 -and $state.uploaded.Count -eq 0) $Name }
try {
    $scripts=Join-Path $root 'scripts'; $shim=Join-Path $root 'shim'
    [IO.Directory]::CreateDirectory($scripts) | Out-Null; [IO.Directory]::CreateDirectory($shim) | Out-Null
    foreach($name in @('Upload-Draft.ps1','Release.Common.ps1')) { Copy-Item -LiteralPath (Join-Path $repository "scripts/release/$name") -Destination $scripts }
    Check ((Get-FileHash -LiteralPath (Join-Path $scripts 'Upload-Draft.ps1')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $repository 'scripts/release/Upload-Draft.ps1')).Hash) 'actual upload script copied without modifications'
    [IO.File]::WriteAllText((Join-Path $scripts 'Test-Release.ps1'), @'
param([string]$Directory)
$m=Get-Content -LiteralPath (Join-Path $Directory 'release-manifest.json') -Raw | ConvertFrom-Json
if(@(Get-ChildItem -LiteralPath $Directory -Force).Count -ne 6){ throw 'Synthetic fixture must have six files.' }
foreach($f in $m.assets){ if((Get-FileHash -LiteralPath (Join-Path $Directory $f.path)).Hash.ToLowerInvariant() -ne $f.sha256){ throw 'Synthetic asset checksum mismatch.' } }
'@)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures/release/FakeGitHub.ps1') -Destination $shim
    [IO.File]::WriteAllText((Join-Path $shim 'gh.cmd'), "@echo off`r`n`"%ASUKALENS_DRAFT_TEST_PWSH%`" -NoProfile -File `"%~dp0FakeGitHub.ps1`" %*`r`nexit /b %errorlevel%`r`n", [Text.Encoding]::ASCII)
    $env:ASUKALENS_DRAFT_TEST_PWSH=Join-Path $PSHOME 'pwsh.exe'; $env:PATH=$shim+';'+$oldPath
    $env:GH_TELEMETRY='log'
    Check ((Get-Command gh -CommandType Application).Source -eq (Join-Path $shim 'gh.cmd')) 'native gh resolves only to the offline shim'
    New-Case; Upload
    $state=State
    Check ($state.created -eq 1 -and $state.uploaded.Count -eq 6 -and $state.downloaded -eq 1 -and $state.release.draft) 'create upload download verify; annotated tag and paginated draft'
    Check (@($state.release.assets | Where-Object { $_.name.Contains(' ') }).Count -eq 0) 'GitHub-normalized filenames match local assets and download verification'
    $state.release.body=$state.release.body.Replace("`n","`r`n"); Save $state
    Upload -Resume
    $state=State
    Check ($state.uploaded.Count -eq 6 -and $state.downloaded -eq 2) 'complete resume skips matching assets and accepts normalized newlines'
    New-Case
    $legacyManifest=Get-Content (Join-Path $script:assets 'release-manifest.json') -Raw | ConvertFrom-Json
    foreach($asset in $legacyManifest.assets) {
        $renamed=$asset.path.Replace('AsukaLens.v','AsukaLens v')
        Move-Item -LiteralPath (Join-Path $script:assets $asset.path) -Destination (Join-Path $script:assets $renamed)
        $asset.path=$renamed
    }
    Write-ReleaseJson $legacyManifest (Join-Path $script:assets 'release-manifest.json')
    Reject { Upload } 'legacy names cannot reach GitHub'
    Check ((State).calls.Count -eq 0) 'legacy name rejection performs no GitHub calls'
    New-Case; Upload -WhatIf
    Check ((State).calls.Count -eq 0) 'WhatIf performs no GitHub calls'
    New-Case
    $accepted=Get-Content $script:acceptance -Raw | ConvertFrom-Json -AsHashtable; $accepted.checks.clean_windows_10_no_dotnet='pending'; Write-ReleaseJson $accepted $script:acceptance
    Reject { Upload } 'incomplete acceptance rejected'; Check ((State).calls.Count -eq 0) 'acceptance failure is offline'
    New-Case
    $accepted=Get-Content $script:acceptance -Raw | ConvertFrom-Json -AsHashtable; $accepted.checks.clean_windows_11_no_dotnet='pending'; Write-ReleaseJson $accepted $script:acceptance
    Reject { Upload } 'Windows 11 acceptance is independently required'; Check ((State).calls.Count -eq 0) 'Windows 11 gate is offline'
    New-Case
    $accepted=Get-Content $script:acceptance -Raw | ConvertFrom-Json -AsHashtable; $accepted.checks.Remove('clean_windows_10_no_dotnet'); $accepted.checks.Remove('clean_windows_11_no_dotnet'); $accepted.checks.clean_windows_no_dotnet='passed'; Write-ReleaseJson $accepted $script:acceptance
    Reject { Upload } 'legacy single OS acceptance rejected'; Check ((State).calls.Count -eq 0) 'legacy gate is offline'
    New-Case
    $accepted=Get-Content $script:acceptance -Raw | ConvertFrom-Json -AsHashtable
    $accepted.checks.clean_windows_10_no_dotnet='waived'; $accepted.checks.clean_windows_11_no_dotnet='waived'
    $accepted.clean_environment_waiver=@{commit=$accepted.commit;manifest_sha256=$accepted.manifest_sha256;reason_code='environment_unavailable';checks=@('clean_windows_10_no_dotnet','clean_windows_11_no_dotnet')}
    Write-ReleaseJson $accepted $script:acceptance
    Reject { Upload -WhatIf } 'waiver without opt-in rejected'; Check ((State).calls.Count -eq 0) 'waiver opt-in rejection offline'
    Upload -WhatIf -AllowCleanEnvironmentWaiver; Check ((State).calls.Count -eq 0) 'bound clean-environment waiver accepts disclosed notes offline'
    Upload -AllowCleanEnvironmentWaiver
    $state=State; Check ($state.created -eq 1 -and $state.uploaded.Count -eq 6 -and $state.downloaded -eq 1 -and $state.release.draft) 'waiver permits offline draft upload and download verification'
    $accepted.clean_environment_waiver.commit=('c'*40); Write-ReleaseJson $accepted $script:acceptance
    Reject { Upload -WhatIf -AllowCleanEnvironmentWaiver } 'waiver wrong commit rejected'
    $accepted.clean_environment_waiver.commit=$accepted.commit; $accepted.clean_environment_waiver.manifest_sha256=('c'*64); Write-ReleaseJson $accepted $script:acceptance
    Reject { Upload -WhatIf -AllowCleanEnvironmentWaiver } 'waiver wrong manifest rejected'
    $accepted.clean_environment_waiver.manifest_sha256=$accepted.manifest_sha256; $accepted.clean_environment_waiver.checks=@('clean_windows_10_no_dotnet'); Write-ReleaseJson $accepted $script:acceptance
    Reject { Upload -WhatIf -AllowCleanEnvironmentWaiver } 'waiver scope mismatch rejected'
    $accepted.clean_environment_waiver.checks=@('clean_windows_10_no_dotnet','clean_windows_11_no_dotnet','clean_windows_11_no_dotnet'); Write-ReleaseJson $accepted $script:acceptance
    Reject { Upload -WhatIf -AllowCleanEnvironmentWaiver } 'duplicate waiver scope rejected'
    $accepted.clean_environment_waiver.checks=@('clean_windows_10_no_dotnet'); $accepted.checks.clean_windows_11_no_dotnet='passed'; Write-ReleaseJson $accepted $script:acceptance
    Reject { Upload -WhatIf -AllowCleanEnvironmentWaiver } 'single clean-system waiver rejected'
    $accepted.checks.clean_windows_11_no_dotnet='waived'
    $accepted.clean_environment_waiver.checks=@('clean_windows_10_no_dotnet','clean_windows_11_no_dotnet'); $accepted.clean_environment_waiver.reason_code='other'; Write-ReleaseJson $accepted $script:acceptance
    Reject { Upload -WhatIf -AllowCleanEnvironmentWaiver } 'waiver unsupported reason rejected'
    $accepted.clean_environment_waiver.reason_code='environment_unavailable'; $accepted.checks.failure_recovery='waived'; Write-ReleaseJson $accepted $script:acceptance
    Reject { Upload -WhatIf -AllowCleanEnvironmentWaiver } 'non-environment waiver rejected'
    $accepted.checks.failure_recovery='passed'; Write-ReleaseJson $accepted $script:acceptance
    $originalNotes=[IO.File]::ReadAllText($script:notes)
    [IO.File]::WriteAllText($script:notes,$originalNotes.Replace('干净 Windows 10 和 Windows 11 环境尚未验收。',''))
    Reject { Upload -WhatIf -AllowCleanEnvironmentWaiver } 'missing Chinese disclosure rejected'
    [IO.File]::WriteAllText($script:notes,$originalNotes.Replace('Clean Windows 10 and Windows 11 environments have not been validated.',''))
    Reject { Upload -WhatIf -AllowCleanEnvironmentWaiver } 'missing English disclosure rejected'
    [IO.File]::WriteAllText($script:notes,$originalNotes.Replace('干净 Windows 10 和 Windows 11 环境尚未验收。','<!-- 干净 Windows 10 和 Windows 11 环境尚未验收。 -->'))
    Reject { Upload -WhatIf -AllowCleanEnvironmentWaiver } 'commented disclosure rejected'
    New-Case
    $testManifest=Get-Content (Join-Path $script:assets 'release-manifest.json') -Raw | ConvertFrom-Json -AsHashtable
    $testManifest.local_test=$true; Write-ReleaseJson $testManifest (Join-Path $script:assets 'release-manifest.json')
    Reject { Upload } 'local test identity never reaches GitHub'; Check ((State).calls.Count -eq 0) 'local test rejection is offline'
    foreach($wrongVersion in @('v1.4.20','v1.4.2-preview','v1.4.2.0','V1.4.2')) {
        New-Case
        [IO.File]::WriteAllText($script:notes,"# AsukaLens $wrongVersion`n## 简体中文`n测试`n## English`nTest`n")
        Reject { Upload } ('wrong notes version '+$wrongVersion); Check ((State).calls.Count -eq 0) 'wrong notes version rejected before credentials'
    }
    New-Case
    [IO.File]::WriteAllText($script:notes,"# AsukaLens v1.4.2`n## 简体中文`n短说明`n## English`nShort notes`n")
    Reject { Upload } 'incomplete bilingual notes rejected'; Check ((State).calls.Count -eq 0) 'missing content rejected before GitHub'
    New-Case
    [IO.File]::WriteAllText($script:notes,([IO.File]::ReadAllText($script:notes).Replace('/releases/download/v1.4.2/','/releases/download/v1.4.20/')))
    Reject { Upload } 'wrong asset version rejected'; Check ((State).calls.Count -eq 0) 'wrong links rejected before GitHub'
    foreach($scenario in @('wrong-commit','missing-tag','wrong-tag','list-error')) {
        New-Case $scenario; Reject { Upload } $scenario; No-Writes ($scenario+' makes no remote changes')
    }
    New-Case; Existing-Draft; Reject { Upload } 'existing draft requires explicit resume'; No-Writes 'implicit resume has no writes'
    New-Case; Existing-Draft; $state=State; $state.release.draft=$false; Save $state
    Reject { Upload -Resume } 'published release rejected'; No-Writes 'published release unchanged'
    foreach($field in @('name','body')) {
        New-Case; Existing-Draft; $state=State; $state.release[$field]='different release content'; Save $state
        Reject { Upload -Resume } ('mismatched draft '+$field); No-Writes ('mismatched '+$field+' has no writes')
    }
    foreach($asset in @(@{name='AsukaLens.v1.4.2.zip';digest='sha256:different'},@{name='unrelated.zip';digest='sha256:different'})) {
        New-Case; Existing-Draft; $state=State; $state.release.assets=@($asset); Save $state
        Reject { Upload -Resume } ('conflicting asset '+$asset.name); No-Writes 'conflicting assets not overwritten'
    }
    New-Case 'interrupt'; Reject { Upload } 'upload interruption propagates'
    $state=State; Check ($state.release.draft -and $state.uploaded.Count -eq 1) 'interrupted draft preserves completed asset'
    Upload -Resume; $state=State
    Check ($state.created -eq 1 -and $state.uploaded.Count -eq 6 -and $state.downloaded -eq 1) 'partial resume only uploads missing assets'
    foreach($scenario in @('corrupt-download','missing-download','extra-download','publish-mid-upload','publish-after-download')) {
        New-Case $scenario; Reject { Upload } $scenario
        if($scenario -eq 'publish-mid-upload') { Check ((State).uploaded.Count -eq 1) 'published transition stops remaining uploads' }
    }
    Write-Host ('PASS draft upload flow checks: '+$script:passed.Count)
} finally {
    [Environment]::SetEnvironmentVariable('PATH',$oldPath,'Process')
    [Environment]::SetEnvironmentVariable('ASUKALENS_DRAFT_TEST_ROOT',$oldRoot,'Process')
    [Environment]::SetEnvironmentVariable('ASUKALENS_DRAFT_TEST_PWSH',$oldShell,'Process')
    [Environment]::SetEnvironmentVariable('GH_TELEMETRY',$oldTelemetry,'Process')
    Remove-OwnedDirectory $root $parent
}
