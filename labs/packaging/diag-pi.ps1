$ErrorActionPreference = 'Stop'
$wf = 'C:\Users\21894\.codex\worktrees\track-f-packaging\Fufucode'
$pkg = Join-Path $wf 'out\build'
$node = Join-Path $pkg 'runtime/node/node.exe'
$cli = Join-Path $pkg 'runtime/pi/dist/bundle/cli.js'
$tmp = Join-Path $wf 'tmp/diag-pi'
New-Item -ItemType Directory -Force $tmp | Out-Null

$workDir = Join-Path $wf 'TestApp'
$out = Join-Path $tmp 'out.txt'
$err = Join-Path $tmp 'err.txt'

$si = New-Object System.Diagnostics.ProcessStartInfo
$si.FileName = $node
$si.Arguments = "$cli --mode rpc --no-session"
$si.UseShellExecute = $false
$si.RedirectStandardInput = $true
$si.RedirectStandardOutput = $true
$si.RedirectStandardError = $true
$si.CreateNoWindow = $true
$si.WorkingDirectory = $workDir
$si.EnvironmentVariables['PI_CODING_AGENT_DIR'] = Join-Path $tmp 'agent'
$si.EnvironmentVariables['HOME'] = Join-Path $tmp 'home'

$p = [System.Diagnostics.Process]::Start($si)
Write-Host "started pid=$($p.Id)"
Start-Sleep -Seconds 30
Write-Host "alive=$($p.HasExited) exit=$($p.ExitCode)"
if (Test-Path $out) {
    $c = [System.IO.File]::ReadAllText($out)
    Write-Host "stdout chars=$($c.Length)"
    Write-Host $c
} else {
    Write-Host 'out.txt not created'
}
if (Test-Path $err) {
    Write-Host 'stderr tail:'
    [System.IO.File]::ReadAllText($err) | Select-Object -Last 12 | Write-Host
}
$p.StandardInput.Close()
try { $p.WaitForExit(5000) } catch {}
if (-not $p.HasExited) { $p.Kill($true) }
Write-Host done.