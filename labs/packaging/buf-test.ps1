$ErrorActionPreference = 'Stop'
$wf = 'C:\Users\21894\.codex\worktrees\track-f-packaging\Fufucode'
$node = Join-Path $wf 'runtime/node/node.exe'
$tmp = Join-Path $wf 'tmp\buf'
New-Item -ItemType Directory -Force $tmp | Out-Null

function SafeLen {
    param([string]$path)
    if (Test-Path -LiteralPath $path) { [System.IO.File]::ReadAllText($path).Length } else { 0 }
}

# Does Node block-buffer stdout when redirected to a file?
$out = Join-Path $tmp 'buf.txt'
Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue
$si = New-Object System.Diagnostics.ProcessStartInfo
$si.FileName = $node
$si.Arguments = '-e "setTimeout(function(){console.log(\"BUF_MARKER\");},1200)"'
$si.UseShellExecute = $false
$si.RedirectStandardOutput = $true
$si.RedirectStandardError = $true
$si.CreateNoWindow = $true
$p = [System.Diagnostics.Process]::Start($si)
Start-Sleep -Seconds 2
Write-Host ("at 2s: file exists={0} size={1}" -f (Test-Path $out), (SafeLen $out))
$p.WaitForExit()
Write-Host ("after exit: {0}" -f (Get-Content $out -Raw))

# Now with a pipe-based drain (should see it immediately)
$out2 = Join-Path $tmp 'buf2.txt'
$si2 = New-Object System.Diagnostics.ProcessStartInfo
$si2.FileName = $node
$si2.Arguments = '-e "setTimeout(function(){console.log(\"PIPE_MARKER\");},1200)"'
$si2.UseShellExecute = $false
$si2.RedirectStandardOutput = $true
$si2.RedirectStandardError = $true
$si2.CreateNoWindow = $true
$p2 = [System.Diagnostics.Process]::Start($si2)
$drain = {
    param($proc, $path)
    $line = $proc.StandardOutput.ReadLine()
    while ($null -ne $line) { Add-Content -Path $path -Value $line; $line = $proc.StandardOutput.ReadLine() }
}
$job = Start-Job -ScriptBlock $drain -ArgumentList $p2, $out2
Start-Sleep -Seconds 2
Write-Host ("pipe at 2s: file exists={0} size={1}" -f (Test-Path $out2), (SafeLen $out2))
$p2.WaitForExit()
$job | Wait-Job | Out-Null
Write-Host ("after exit: {0}" -f (Get-Content $out2 -Raw))
Write-Host done.