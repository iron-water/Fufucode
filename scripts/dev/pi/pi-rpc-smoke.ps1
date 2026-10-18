# Pi backend plugin diagnostic — not part of the default native package.
param([Parameter(Mandatory)][string]$NodeExe, [Parameter(Mandatory)][string]$PiCli,
    [Parameter(Mandatory)][string]$Project, [Parameter(Mandatory)][string]$Agent)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $Project, $Agent -Force | Out-Null
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $NodeExe
$start.Arguments = "`"$PiCli`" --mode rpc --no-session --no-extensions"
$start.WorkingDirectory = $Project
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
[Console]::InputEncoding = New-Object Text.UTF8Encoding($false)
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.EnvironmentVariables['PI_CODING_AGENT_DIR'] = $Agent
$process = [Diagnostics.Process]::Start($start)
$errorOutput = $process.StandardError.ReadToEndAsync()
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
    $request = [Text.Encoding]::UTF8.GetBytes('{"id":"packaging-state","type":"get_state"}' + "`n")
    $process.StandardInput.BaseStream.Write($request, 0, $request.Length)
    $process.StandardInput.BaseStream.Flush()
    $line = $process.StandardOutput.ReadLineAsync()
    if (-not $line.Wait(60000)) { throw 'Pi state response timeout.' }
    if ($null -eq $line.Result) { throw 'Pi exited without a state response.' }
    $response = $line.Result | ConvertFrom-Json
    if ($response.id -ne 'packaging-state' -or $response.type -ne 'response' -or $response.command -ne 'get_state' -or $response.success -ne $true) { throw "Pi state mismatch: id=$($response.id);type=$($response.type);success=$($response.success);error=$($response.error)" }
    [pscustomobject]@{passed=$true;elapsedMs=$watch.ElapsedMilliseconds;command='get_state'} | ConvertTo-Json
} catch {
    if (-not $process.HasExited) { $process.Kill() }
    [void]$process.WaitForExit(5000)
    $stderr = if ($errorOutput.Wait(5000)) { $errorOutput.Result } else { 'stderr did not close' }
    throw ($_ | Out-String) + "`nPi stderr: $stderr"
} finally {
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(5000)) { $process.Kill() }
    $process.Dispose()
}
