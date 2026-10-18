$ErrorActionPreference = 'Stop'
$wf = 'C:\Users\21894\.codex\worktrees\track-f-packaging\Fufucode'
$pkg = Join-Path $wf 'out\build'
$node = Join-Path $pkg 'runtime/node/node.exe'
$cli = Join-Path $pkg 'runtime/pi/dist/bundle/cli.js'
$tmp = Join-Path $wf 'tmp/dbg-pi'
New-Item -ItemType Directory -Force $tmp | Out-Null
$log = Join-Path $tmp 'lines.log'

$si = New-Object System.Diagnostics.ProcessStartInfo
$si.FileName = $node; $si.Arguments = "$cli --mode rpc --no-session"
$si.UseShellExecute = $false
$si.RedirectStandardInput = $true; $si.RedirectStandardOutput = $true; $si.RedirectStandardError = $true
$si.CreateNoWindow = $true
$si.WorkingDirectory = Join-Path $wf 'TestApp'
$si.EnvironmentVariables['PI_CODING_AGENT_DIR'] = Join-Path $tmp 'agent'
$si.EnvironmentVariables['HOME'] = Join-Path $tmp 'home'

$proc = [System.Diagnostics.Process]::Start($si)
$lines = [System.Collections.Generic.List[string]]::new()
$code = {
    param($proc, $lines, $log)
    $line = $proc.StandardOutput.ReadLine()
    while ($null -ne $line) {
        $lines.Add($line)
        [System.IO.File]::AppendAllText($log, "$(Get-Date -Format HH:mm:ss.fff) $([System.Environment]::NewLine)$line")
        $line = $proc.StandardOutput.ReadLine() }
}
$rs = [runspacefactory]::CreateRunspace(); $rs.Open()
$ps = [powershell]::Create(); $ps.Runspace = $rs
$ps.AddScript($code).AddArgument($proc).AddArgument($lines).AddArgument($log) | Out-Null
$ar = $ps.BeginInvoke()

Write-HOST "started pid=$($proc.Id)"
$id1 = [Guid]::NewGuid().ToString('N'); $id2 = [Guid]::NewGuid().ToString('N')
Write-HOST "sending q1 ($id1)"; $proc.StandardInput.WriteLine("{`"id`":`"$id1`",`"type`":`"get_state`"}")
Start-Sleep -Seconds 12
Write-HOST "sending q2 ($id2)"; $proc.StandardInput.WriteLine("{`"id`":`"$id2`",`"type`":`"get_state`"}")
Start-Sleep -Seconds 20
Write-HOST "alive=$($proc.HasExited) exit=$($proc.ExitCode)"
Write-HOST "lines drained=$($lines.Count)"
if (Test-Path $log) { Write-HOST "log bytes=$([System.IO.File]::ReadAllBytes($log).Length)"; Get-Content $log | Select-Object -First 30 | Write-HOST }
$proc.StandardInput.Close()
try { $proc.WaitForExit(5000) } catch {}
if (-not $proc.HasExited) { $proc.Kill($true) }
try { $ps.EndInvoke($ar) } catch {}
$rs.Close(); $ps.Dispose(); $rs.Dispose()
Write-HOST done.