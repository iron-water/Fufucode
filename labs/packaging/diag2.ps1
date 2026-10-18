$ErrorActionPreference = 'Stop'
$wf = 'C:\Users\21894\.codex\worktrees\track-f-packaging\Fufucode'
$pkg = Join-Path $wf 'out\build'
$node = Join-Path $pkg 'runtime/node/node.exe'
$cli = Join-Path $pkg 'runtime/pi/dist/bundle/cli.js'
$tmp = Join-Path $wf 'tmp/diag2'
New-Item -ItemType Directory -Force $tmp | Out-Null

$out = Join-Path $tmp 'out.txt'
$err = Join-Path $tmp 'err.txt'
Remove-Item -LiteralPath $out,$err -ErrorAction SilentlyContinue

$si = New-Object System.Diagnostics.ProcessStartInfo
$si.FileName = $node
$si.Arguments = "$cli --mode rpc --no-session"
$si.UseShellExecute = $false
$si.RedirectStandardInput = $true
$si.RedirectStandardOutput = $true
$si.RedirectStandardError = $true
$si.CreateNoWindow = $true
$si.WorkingDirectory = Join-Path $wf 'TestApp'
$si.EnvironmentVariables['PI_CODING_AGENT_DIR'] = Join-Path $tmp 'agent'
$si.EnvironmentVariables['HOME'] = Join-Path $tmp 'home'

$p = [System.Diagnostics.Process]::Start($si)
Write-Host "started pid=$($p.Id)"
$p.WaitForExit()
Write-Host "EXIT_CODE=$($p.ExitCode)"
Write-Host "out.txt exists=$(Test-Path $out) ($(if (Test-Path $out) { [System.IO.File]::ReadAllText($out).Length } else { 0 }) chars)"
Write-Host "err.txt exists=$(Test-Path $err) ($(if (Test-Path $err) { [System.IO.File]::ReadAllText($err).Length } else { 0 }) chars)"
if (Test-Path $err) { [System.IO.File]::ReadAllText($err) | Write-Host }
if (Test-Path $out) { [System.IO.File]::ReadAllText($out) | Write-Host }
Write-Host done.