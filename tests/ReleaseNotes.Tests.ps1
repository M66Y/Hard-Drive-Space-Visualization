#requires -Version 7.4
[CmdletBinding()]
param([string]$Summary)
. (Join-Path $PSScriptRoot '../scripts/release/Release.Common.ps1')
$manifest=[pscustomobject]@{version='v1.4.2';runtime='10.0.14';commit=('b'*40)}
$template=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../docs/release-notes.template.md'))
$valid=$template.Replace('{{VERSION}}',$manifest.version).Replace('{{RUNTIME}}',$manifest.runtime).Replace('{{COMMIT}}',$manifest.commit).
    Replace('{{CHANGES_ZH}}','合成测试内容。').Replace('{{FIXES_ZH}}','合成修复内容。').Replace('{{CHANGES_EN}}','Synthetic changes.').Replace('{{FIXES_EN}}','Synthetic fixes.')
$checks=[Collections.Generic.List[string]]::new()
function Check([bool]$Condition,[string]$Name) {
    if (-not $Condition) { throw ('FAIL '+$Name) }
    $checks.Add($Name); Write-Host ('PASS '+$Name)
}
function Reject([string]$Text,[string]$Name) {
    $failed=$false
    try { Assert-ReleaseNotes $Text $manifest } catch { $failed=$true }
    Check $failed $Name
}
Assert-ReleaseNotes $valid $manifest; Check $true 'completed production template accepted'
Reject ($valid.Replace('AsukaLens.v1.4.2','AsukaLens%20v1.4.2')) 'legacy download URLs rejected for a new release'
Assert-ReleaseNotes ($valid.Replace("`r`n","`n").Replace("`n","`r`n")) $manifest; Check $true 'CRLF accepted'
Assert-ReleaseNotes ($valid+"`n``````powershell`nGet-FileHash -Algorithm SHA256 'synthetic.zip'`n```````n") $manifest
Check $true 'additional fenced usage example accepted'
Reject $template 'unresolved production template rejected'
Reject "# AsukaLens v1.4.2`n## 简体中文`n示例`n## English`nExample" 'old minimum-only notes rejected'
foreach ($bad in @('v1.4.20','v1.4.2-preview','v1.4.2.0','V1.4.2')) { Reject ($valid.Replace('# AsukaLens v1.4.2',"# AsukaLens $bad")) ('wrong title '+$bad) }
foreach ($heading in @('更新内容','修复','已知限制','适用环境与依赖','安装与便携使用','下载与校验','源码与许可',
    'Changes','Fixes','Known limitations','Requirements','Installation and portable use','Downloads and checksums','Source and licensing')) {
    Reject ($valid.Replace('### '+$heading,'### Missing section')) ('missing subsection '+$heading)
}
Reject ($valid.Replace('合成测试内容。','')) 'empty changes rejected'
Reject ($valid.Replace('Synthetic fixes.','<!-- No actual content -->')) 'comment-only fixes rejected'
Reject ($valid.Replace('Synthetic changes.',"``````text`nOnly in a code example`n``````")) 'code-only changes rejected'
Reject ($valid+"`n``````text`nunterminated") 'unterminated code fence rejected'
Reject ($valid+"`n<!-- unterminated") 'unterminated comment rejected'
Reject ($valid.Replace('## English','## ENGLISH')) 'explicit English section required'
Reject ($valid+"`n## English`nDuplicate") 'duplicate language rejected'
Reject ($valid+"`n### Changes`nDuplicate") 'duplicate subsection rejected'
Reject ($valid.Replace('Windows 11 x64','Windows 10 x64')) 'missing Windows 11 rejected'
Reject ($valid.Replace('Windows 10 22H2 x64','Windows 10 x64')) 'missing Windows 10 baseline rejected'
Reject ($valid.Replace('.NET 10.0.14','.NET 10.0.140')) 'runtime substring mismatch rejected'
Reject ($valid.Replace('未代码签名','未确认发行者')) 'Chinese signing disclosure required'
Reject ($valid.Replace('unsigned','unverified')) 'English signing disclosure required'
$base='https://github.com/M66Y/Hard-Drive-Space-Visualization/releases/download/v1.4.2/'
foreach ($file in @('AsukaLens.v1.4.2.zip','AsukaLens.v1.4.2.exe','AsukaLens.v1.4.2-source.zip','AsukaLens.v1.4.2-source.tar.gz','SHA256SUMS.txt','release-manifest.json')) {
    $link=$base+[Uri]::EscapeDataString($file)
    Reject ($valid.Replace($link,'https://example.invalid/missing')) ('missing asset link '+$file)
}
Reject ($valid.Replace('/releases/download/v1.4.2/','/releases/download/v1.4.20/')) 'wrong version asset URLs rejected'
Reject ($valid.Replace('/M66Y/Hard-Drive-Space-Visualization/releases/','/fixture/other/releases/')) 'wrong repository asset URLs rejected'
$extra='[unexpected]('+ $base +'unexpected.zip)'
Reject ($valid.Replace('### 源码与许可',$extra+"`n`n### 源码与许可")) 'unexpected download rejected'
Reject ($valid.Replace('/tree/'+$manifest.commit,'/tree/'+('c'*40))) 'wrong source commit rejected'
$chineseEnd=$valid.IndexOf('## English')
Reject ($valid.Substring(0,$chineseEnd)+$valid.Substring($chineseEnd).Replace('/releases/download/v1.4.2/','/releases/download/v1.4.20/')) 'each language needs matching links'
if ($Summary) {
    if (Test-Path -LiteralPath $Summary) { throw 'Summary exists; refusing overwrite.' }
    Write-ReleaseJson ([ordered]@{schema=1;passed=$checks.Count;checks=$checks.ToArray();network_used=$false;software_packages_generated=$false}) ([IO.Path]::GetFullPath($Summary))
}
Write-Host ('PASS release notes checks: '+$checks.Count)
