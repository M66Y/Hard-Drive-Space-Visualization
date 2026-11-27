#requires -Version 7.4
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../scripts/ntfs-prototype/ProductTimingFixturePipe.ps1')
Initialize-ProductTimingFixturePipe
$checks = 0
function Check([bool]$Ok, [string]$Name) {
    if (-not $Ok) { throw $Name }
    $script:checks++
}
function Assert-Rejected([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Check $rejected $Name
}

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$server = $null
$client = $null
try {
    $acl = [IO.Pipes.PipeSecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($id.User)
    $acl.AddAccessRule([IO.Pipes.PipeAccessRule]::new($id.User, [IO.Pipes.PipeAccessRights]::FullControl,
        [Security.AccessControl.AccessControlType]::Allow))
    $name = 'AsukaLens.Timing.' + [guid]::NewGuid().ToString('N')
    $server = [IO.Pipes.NamedPipeServerStreamAcl]::Create($name, [IO.Pipes.PipeDirection]::InOut, 1,
        [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous, 65536, 65536,
        $acl, [IO.HandleInheritability]::None, [IO.Pipes.PipeAccessRights]0)
    $client = [IO.Pipes.NamedPipeClientStream]::new('.', $name, [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous, [Security.Principal.TokenImpersonationLevel]::Identification)
    $client.Connect(3000)
    $server.WaitForConnection()
    [uint32]$left = 0; [uint32]$right = 0
    Check ([AsukaLensProductTimingPipe]::GetNamedPipeClientProcessId($server.SafePipeHandle, [ref]$left) -and $left -eq $PID) 'bind client PID'
    Check ([AsukaLensProductTimingPipe]::GetNamedPipeServerProcessId($client.SafePipeHandle, [ref]$right) -and $right -eq $PID) 'bind server PID'
    [AsukaLensProductTimingPipe]::Write($client, '模拟夹具', 3)
    Check ([AsukaLensProductTimingPipe]::Read($server, 3) -ceq '模拟夹具') 'read async pipe frame'
    [AsukaLensProductTimingPipe]::Write($server, 'done', 3)
    Check ([AsukaLensProductTimingPipe]::Read($client, 3) -ceq 'done') 'write reply to pipe'
} finally {
    if ($null -ne $client) { $client.Dispose() }
    if ($null -ne $server) { $server.Dispose() }
    $id.Dispose()
}
foreach ($size in @(0, -1, (1MB+1), 100)) {
    $stream = [IO.MemoryStream]::new([BitConverter]::GetBytes([int]$size))
    try { Assert-Rejected { [AsukaLensProductTimingPipe]::Read($stream, 2) } "reject invalid or incomplete frame $size" }
    finally { $stream.Dispose() }
}
$stream = [IO.MemoryStream]::new()
try { Assert-Rejected { [AsukaLensProductTimingPipe]::Write($stream, ('a' * (1MB+1)), 2) } 'reject overlong write' }
finally { $stream.Dispose() }
Assert-Rejected { Assert-ProductTimingScope 'C:\Windows' 'bad' } 'reject unowned scope shape'
Write-Output "PASS product timing fixture pipe checks: $checks; no virtual disk operations"
