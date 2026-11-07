#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Compiler)
. (Join-Path $PSScriptRoot '../scripts/release/Release.Common.ps1')
$Compiler=(Resolve-Path -LiteralPath $Compiler).Path
$tools=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../scripts/release/toolchain.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $Compiler).Hash.ToLowerInvariant() -ne $tools.iscc_sha256) { throw 'Compiler does not match the pinned toolchain.' }
$parent=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
$root=Join-Path $parent ('installer-identity-'+[guid]::NewGuid().ToString('N'))
Assert-NoReparse $root
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $payload=Join-Path $root 'payload'
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    [IO.File]::WriteAllText((Join-Path $payload 'AsukaLens.exe'),'synthetic never-executed payload')
    foreach($name in @('LICENSE','INSTALL-NOTICE.txt')) { [IO.File]::WriteAllText((Join-Path $payload $name),'synthetic test notice') }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../assets/app.ico') -Destination $payload
    $passed=0
    foreach($case in @(
        @{name='default';value=$null;expected='AsukaLens'},
        @{name='formal';value='0';expected='AsukaLens'},
        @{name='local';value='1';expected='AsukaLens (LOCAL TEST)'},
        @{name='out-of-range';value='2';expected=$null},
        @{name='negative';value='-1';expected=$null},
        @{name='invalid';value='false';expected=$null}
    )) {
        $output=Join-Path $root $case.name
        [IO.Directory]::CreateDirectory($output) | Out-Null
        $arguments=@('--quiet','--no-ide-signtools','--define=AppVersion=1.5.0',('--define=PayloadDir='+$payload),('--define=OutputDir='+$output))
        if ($null -ne $case.value) { $arguments+=('--define=LocalTest='+$case.value) }
        $arguments+=(Join-Path $PSScriptRoot '../scripts/release/AsukaLens.iss')
        & $Compiler @arguments *> (Join-Path $output 'compiler.txt')
        $exit=$LASTEXITCODE
        $exe=Join-Path $output 'AsukaLens v1.5.0.exe'
        if ($null -eq $case.expected) {
            if ($exit -eq 0 -or (Test-Path -LiteralPath $exe)) { throw ('Invalid identity was compiled: '+$case.name) }
        } else {
            if ($exit -ne 0) { throw ('Compilation failed: '+$case.name) }
            if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductName.Trim() -cne $case.expected) { throw ('Wrong installer identity: '+$case.name) }
        }
        $passed++; Write-Host ('PASS installer identity: '+$case.name)
    }
    Write-Host "$passed real compiler identity checks passed; no installer executed."
} finally { Remove-OwnedDirectory $root $parent }
