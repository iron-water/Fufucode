<#
Pi pipe helper — start `pi --mode rpc --no-session`, drain stdout through a
separate runspace (Node block-buffers stdout when redirected), match responses
by request id, and shut down by closing stdin.

Shared by verify-clean-start.ps1 and the installer upgrade drill.
#>

function Start-PiProcess {
    param(
        [string]$node,
        [string]$cli,
        [hashtable]$envVars,
        [string]$workingDirectory
    )
    $si = New-Object System.Diagnostics.ProcessStartInfo
    $si.FileName = $node
    $si.Arguments = "$cli --mode rpc --no-session"
    $si.UseShellExecute = $false
    $si.RedirectStandardInput = $true
    $si.RedirectStandardOutput = $true
    $si.RedirectStandardError = $true
    $si.CreateNoWindow = $true
    if ($workingDirectory) { $si.WorkingDirectory = $workingDirectory }
    foreach ($k in $envVars.Keys) { $si.EnvironmentVariables[$k] = $envVars[$k] }
    $proc = [System.Diagnostics.Process]::Start($si)

    $lines = [System.Collections.Generic.List[string]]::new()
    $reader = $proc.StandardOutput
    $tss = New-Object System.Threading.Tasks.TaskCompletionSource[bool]
    $code = {
        param($reader, $lines, $tss)
        try {
            while ($true) {
                $task = $reader.ReadLineAsync()
                $task.Wait()
                if ($task.IsCompletedSuccessfully -and $null -ne $task.Result) {
                    $lines.Add($task.Result)
                } else { break }
            }
        } catch {}
        finally { $tss.TrySetResult($true) | Out-Null }
    }
    $rs = [runspacefactory]::CreateRunspace()
    $rs.Open()
    $ps = [powershell]::Create()
    $ps.Runspace = $rs
    $ps.AddScript($code).AddArgument($reader).AddArgument($lines).AddArgument($tss) | Out-Null
    $ar = $ps.BeginInvoke()
    return @{ proc = $proc; ps = $ps; rs = $rs; ar = $ar; lines = $lines; tss = $tss }
}

function Find-PiResponse {
    param([hashtable]$state, [string]$id)
    foreach ($ln in $state.lines) {
        if ($ln -match '"id"\s*:\s*"([^"]+)"' -and $Matches[1] -eq $id) { return $ln }
    }
    return $null
}

function Stop-Pi {
    param([hashtable]$state)
    $state.proc.StandardInput.Close()
    try {
        if (-not $state.proc.WaitForExit(5000)) {
            $state.proc.Kill($true)
            $state.proc.WaitForExit()
        }
    } catch {}
    try { $state.ps.EndInvoke($state.ar) } catch {}
    try { $state.rs.Close() } catch {}
    try { $state.ps.Dispose() } catch {}
    try { $state.rs.Dispose() } catch {}
    return $state.proc.ExitCode
}