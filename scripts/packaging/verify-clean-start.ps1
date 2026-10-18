param([string]$ExtractedPackage)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$zip = Join-Path $root 'out\Fufucode.zip'
$testRoot = Join-Path $Env:TEMP ('fff-pkg-test-' + [Guid]::NewGuid().ToString('N'))
$report = Join-Path $root 'labs\packaging\clean-start-report.jsonl'
$logDir = Join-Path $testRoot 'logs'

function Out-Log {
    param([string]$msg)
    $stamp = Get-Date -Format 'HH:mm:ss.fff'
    Add-Content -Path (Join-Path $logDir 'run.log') -Value "$stamp $msg"
    Write-Host $msg
}

function Record {
    param([hashtable]$rec)
    $rec.time = Get-Date -Format 'o'
    $rec | ConvertTo-Json -Compress | Add-Content -Path $report
}

function New-Rec {
    param([string]$phase)
    return @{ phase = $phase }
}

function Start-PiProcess {
    param([string]$node, [string]$cli, [hashtable]$envVars)
    $si = New-Object System.Diagnostics.ProcessStartInfo
    $si.FileName = $node
    $si.Arguments = "`"$cli`" --mode rpc --no-session --no-extensions"
    $si.UseShellExecute = $false
    $si.RedirectStandardInput = $true
    [Console]::InputEncoding = New-Object Text.UTF8Encoding($false)
    $si.RedirectStandardOutput = $true
    $si.RedirectStandardError = $true
    $si.CreateNoWindow = $true
    $si.WorkingDirectory = Join-Path $testRoot 'project'
    foreach ($k in $envVars.Keys) { $si.EnvironmentVariables[$k] = $envVars[$k] }
    $proc = [System.Diagnostics.Process]::Start($si)

    # Node block-buffers stdout when redirected to a file, so drain the pipe continuously.
    $lines = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    $reader = $proc.StandardOutput
    $tss = New-Object System.Threading.Tasks.TaskCompletionSource[bool]
    $code = {
        param($reader, $lines, $tss)
        try {
            while ($true) {
                $task = $reader.ReadLineAsync()
                $task.Wait()
                if ($task.IsCompletedSuccessfully -and $null -ne $task.Result) {
                    $lines.Enqueue($task.Result)
                } else { break }
            }
        } catch {}
        finally { [void]$tss.TrySetResult($true) }
    }
    $rs = [runspacefactory]::CreateRunspace(); $rs.Open()
    $ps = [powershell]::Create(); $ps.Runspace = $rs
    $ps.AddScript($code).AddArgument($reader).AddArgument($lines).AddArgument($tss) | Out-Null
    $ar = $ps.BeginInvoke()
    return @{ proc = $proc; ps = $ps; rs = $rs; ar = $ar; lines = $lines; tss = $tss; stderrTask = $stderrTask }
}

function Find-PiResponse {
    param([hashtable]$state, [string]$id)
    foreach ($ln in $state.lines.ToArray()) {
        try { $response = $ln | ConvertFrom-Json } catch { continue }
        if ($response.id -eq $id -and $response.type -eq 'response') { return $response }
    }
    return $null
}

function Stop-Pi {
    param([hashtable]$state)
    $graceful = $false
    try {
        $state.proc.StandardInput.Close()
        $graceful = $state.proc.WaitForExit(5000)
        if (-not $graceful) { $state.proc.Kill($true); $state.proc.WaitForExit() }
    } catch {
        if (-not $state.proc.HasExited) { $state.proc.Kill($true); $state.proc.WaitForExit() }
    }
    try { $state.ps.EndInvoke($state.ar) | Out-Null } catch {}
    $state.rs.Close(); $state.ps.Dispose(); $state.rs.Dispose()
    $exitCode = $state.proc.ExitCode
    $state.proc.Dispose()
    return @{ exitCode = $exitCode; graceful = $graceful }
}

function Test-PiStateResponse {
    param($response)
    return $null -ne $response -and $response.type -eq 'response' -and
        $response.success -eq $true -and $response.command -eq 'get_state'
}

$pi = $null
$stInfo = $null
try {
New-Item -ItemType Directory -Force $logDir | Out-Null
New-Item -ItemType Directory -Force $testRoot | Out-Null
New-Item -ItemType Directory -Force (Join-Path $testRoot 'project') | Out-Null
New-Item -ItemType Directory -Force $logDir | Out-Null
Remove-Item -LiteralPath $report -ErrorAction SilentlyContinue

$cleanPath = ($Env:Path -split ';' | Where-Object {
    $directory = $_.Trim('"')
    $containsGlobalRuntime = $false
    foreach ($name in @('node.exe', 'node.cmd', 'npm.cmd', 'pi.exe', 'pi.cmd', 'fufucode.exe', 'fufucode.cmd')) {
        if ([IO.File]::Exists([IO.Path]::Combine($directory, $name))) { $containsGlobalRuntime = $true; break }
    }
    -not $containsGlobalRuntime -and $_ -notmatch '\\nodejs|\\npm|\\node_modules|\\pi-coding-agent|AppData\\Roaming\\npm'
}) -join ';'

# --- 1. unpack into a non-dev path ---
$packageRoot = if ($ExtractedPackage) { (Resolve-Path -LiteralPath $ExtractedPackage).Path } else { Join-Path $testRoot 'package' }
$alreadyExtracted = (Test-Path (Join-Path $packageRoot 'app\Fufucode.App.exe')) -and (Test-Path (Join-Path $packageRoot 'runtime\pi\node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js'))
if ($ExtractedPackage -and -not $alreadyExtracted) { throw 'The supplied extracted package is incomplete.' }
if (-not $alreadyExtracted) {
    Out-Log "Unpacking $zip -> $packageRoot"
    $unpacked = Measure-Command { Expand-Archive -LiteralPath $zip -DestinationPath $packageRoot -Force }
    Out-Log "Unpacked in $([math]::Round($unpacked.TotalSeconds, 2)) s"
} else {
    Out-Log "Reusing extracted package at $packageRoot"
}
$appDir = Join-Path $packageRoot 'app'
$nodeExe = Join-Path $packageRoot 'runtime\node\node.exe'
$cli = Join-Path $packageRoot 'runtime\pi\node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js'
$ok = $true

# --- 2. selftest with the dev PATH stripped ---
Out-Log "Running selftest (clean PATH)..."
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$selftestStart = [Diagnostics.ProcessStartInfo]::new()
$selftestStart.FileName = Join-Path $appDir 'Fufucode.App.exe'
$selftestStart.Arguments = '--selftest'
$selftestStart.WorkingDirectory = Join-Path $testRoot 'project'
$selftestStart.UseShellExecute = $false
$selftestStart.CreateNoWindow = $true
$selftestStart.RedirectStandardOutput = $true
$selftestStart.RedirectStandardError = $true
$selftestStart.Environment['PATH'] = $cleanPath
$selftestStart.Environment['FUFUCODE_DATA'] = Join-Path $testRoot 'data'
$stInfo = [Diagnostics.Process]::Start($selftestStart)
$selftestOut = $stInfo.StandardOutput.ReadToEndAsync()
$selftestError = $stInfo.StandardError.ReadToEndAsync()
if (-not $stInfo.WaitForExit(600000)) { $stInfo.Kill($true); $stInfo.WaitForExit(); throw 'Packaged selftest timed out.' }
[IO.File]::WriteAllText((Join-Path $logDir 'selftest.out.txt'), $selftestOut.GetAwaiter().GetResult())
[IO.File]::WriteAllText((Join-Path $logDir 'selftest.err.txt'), $selftestError.GetAwaiter().GetResult())
$sw.Stop()
$stText = Get-Content (Join-Path $logDir 'selftest.out.txt') -Raw
$stPass = ($stInfo.ExitCode -eq 0) -and ($stText -match '0 项失败')
$selftestExitCode = $stInfo.ExitCode
$r = New-Rec -phase 'selftest'
$r.exitCode = $stInfo.ExitCode
$r.elapsedMs = $sw.ElapsedMilliseconds
$r.pass = $stPass
Record -rec $r
$stInfo.Dispose()
$stInfo = $null
if (-not $stPass) { Out-Log "  FAIL selftest exit=$selftestExitCode"; $ok = $false }
else { Out-Log "  ok selftest passed" }

# --- 3. Pi startup from managed runtime, clean PATH, no model query ---
# Per plan §12, "Pi ready" = process spawn until the state query succeeds.
Out-Log "Starting Pi from managed runtime (clean PATH, no model query)..."
$envVars = @{
    PATH = $cleanPath
    PI_CODING_AGENT_DIR = Join-Path $testRoot 'data\pi-agent'
    HOME = Join-Path $testRoot 'home'
    USERPROFILE = Join-Path $testRoot 'home'
    TEMP = Join-Path $testRoot 'tmp'
    TMP = Join-Path $testRoot 'tmp'
}
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$pi = Start-PiProcess -node $nodeExe -cli $cli -envVars $envVars
$spawnMs = $sw.ElapsedMilliseconds

# Wait up to 45 s for the first response to a real query.
$id = [Guid]::NewGuid().ToString('N')
$req = @{ id = $id; type = 'get_state' } | ConvertTo-Json -Compress
$pi.proc.StandardInput.WriteLine($req)
$deadline = [DateTime]::Now.AddSeconds(45)
$stateOk = $false
$stateJson = ''
$readyMs = $null
while ([DateTime]::Now -lt $deadline) {
    $hit = Find-PiResponse -state $pi -id $id
    if ($null -ne $hit) {
        $stateJson = $hit | ConvertTo-Json -Compress -Depth 32
        $stateOk = Test-PiStateResponse $hit
        $readyMs = $sw.ElapsedMilliseconds
        break
    }
    if ($pi.proc.HasExited) { break }
    Start-Sleep -Milliseconds 50
}
$readyMs = if ($null -eq $readyMs) { $sw.ElapsedMilliseconds } else { $readyMs }
$piStillAlive = -not $pi.proc.HasExited
$r = New-Rec -phase 'pi-start'
$r.startedInMs = $spawnMs
$r.readyInMs = $readyMs
$r.gotResponse = $stateOk
$r.alive = $piStillAlive
$r.exitCode = if ($pi.proc.HasExited) { $pi.proc.ExitCode } else { $null }
$r.pass = ($stateOk -and $piStillAlive)
Record -rec $r
if (-not $stateOk -or -not $piStillAlive) { Out-Log "  FAIL Pi did not successfully answer get_state while alive"; $ok = $false }
else { Out-Log "  ok Pi ready in $readyMs ms (spawn $spawnMs ms)" }

# --- 4. a second state query confirms repeatability (no model RPC, no API key needed) ---
Out-Log "Querying get_state a second time from package-managed Pi..."
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$id2 = [Guid]::NewGuid().ToString('N')
$req2 = @{ id = $id2; type = 'get_state' } | ConvertTo-Json -Compress
$pi.proc.StandardInput.WriteLine($req2)
$stateDeadline = [DateTime]::Now.AddSeconds(15)
$stateOk2 = $false
$stateJson2 = ''
while ([DateTime]::Now -lt $stateDeadline) {
    $hit = Find-PiResponse -state $pi -id $id2
    if ($null -ne $hit) {
        $stateJson2 = $hit | ConvertTo-Json -Compress -Depth 32
        $stateOk2 = Test-PiStateResponse $hit
        break
    }
    if ($pi.proc.HasExited) { break }
    Start-Sleep -Milliseconds 50
}
$sw.Stop()
$r = New-Rec -phase 'get-state'
$r.elapsedMs = $sw.ElapsedMilliseconds
$r.success = $stateOk2
$r.responseBytes = $stateJson2.Length
Record -rec $r
if (-not $stateOk2) { Out-Log "  FAIL second get_state timed out"; $ok = $false }
else { Out-Log "  ok second get_state returned $($stateJson2.Length) bytes" }

# --- 5. clean shutdown ---
Out-Log "Stopping Pi (close stdin)..."
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$stopped = Stop-Pi -state $pi
$pi = $null
$exitCode = $stopped.exitCode
$sw.Stop()
$r = New-Rec -phase 'pi-stop'
$r.exitCode = $exitCode
$r.stoppedInMs = $sw.ElapsedMilliseconds
$r.graceful = $stopped.graceful
$r.clean = ($stopped.graceful -and $exitCode -eq 0)
Record -rec $r
Out-Log "  Pi stopped in $($sw.ElapsedMilliseconds) ms (exit $exitCode)"
if (-not $r.clean) { $ok = $false }

# --- 6. residual process check (only processes launched from the package tree) ---
Out-Log "Checking for residual processes..."
$residual = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -match 'node|Fufucode.App|Fufucode' -and $_.Path -and $_.Path.StartsWith($packageRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
} | Select-Object Id, ProcessName, Path
$r = New-Rec -phase 'residual'
$r.count = $residual.Count
$r.processes = $residual
Record -rec $r
if ($residual.Count -gt 0) { Out-Log "  WARN residual processes: $($residual.Count)"; $ok = $false }
else { Out-Log "  ok no residual processes" }

# --- 7. final ---
$r = New-Rec -phase 'overall'
$r.pass = $ok
$r.packageRoot = $packageRoot
$r.pathEntryCount = ($cleanPath -split ';').Count
$r.selftestWorkingDirectory = Join-Path $testRoot 'project'
Record -rec $r
$final = if ($ok) { 'PASS' } else { 'FAIL' }
Out-Log "=== clean-start verification: $final ==="
Out-Log "Report: $report"
if ($ok) { exit 0 } else { exit 1 }
} finally {
    if ($pi) { [void](Stop-Pi -state $pi) }
    if ($stInfo) {
        if (-not $stInfo.HasExited) { $stInfo.Kill($true); $stInfo.WaitForExit() }
        $stInfo.Dispose()
    }
}
