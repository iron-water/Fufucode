$ErrorActionPreference = 'Stop'
$root = 'C:\Users\21894\.codex\worktrees\track-f-packaging\Fufucode'
$tmp = Join-Path $root 'tmp'
New-Item -ItemType Directory -Force $tmp | Out-Null

function Run-Pi {
    param([string]$node, [string]$cli, [string]$tag)
    $out = Join-Path $tmp "pi-$tag.out.txt"
    $err = Join-Path $tmp "pi-$tag.err.txt"
    $proc = Start-Process -FilePath $node -ArgumentList $cli, '--mode', 'rpc', '--no-session' `
        -NoNewWindow -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError $err `
        -Environment @{ PI_CODING_AGENT_DIR = (Join-Path $tmp "pi-$tag-agent"); HOME = (Join-Path $tmp "home-$tag") }
    Write-HOST "[$tag] pid=$($proc.Id)"
    Start-Sleep -Seconds 20
    Write-HOST "[$tag] alive=$($proc.HasExited)"
    Write-HOST "[$tag] stdout lines=$(Get-Content $out -ErrorAction SilentlyContinue | Measure-Object).Count"
    if (Test-Path $out) {
        $c = Get-Content $out -Raw -ErrorAction SilentlyContinue
        if ($c) { Write-HOST "[$tag] OUT: $([System.Math]::Min(400, $c.Length)) chars"; Write-HOST $c.Substring(0, [System.Math]::Min(400, $c.Length)) }
    }
    if (Test-Path $err) { Write-HOST "[$tag] ERR: $(Get-Content $err -Raw | Select-Object -Last 3)" }
    $proc.StandardInput.Close()
    try { $proc.WaitForExit(5000) } catch {}
    if (-not $proc.HasExited) { $proc.Kill($true) }
}

Write-HOST '### 1. managed runtime (packaged) ###'
Run-Pi -node (Join-Path $root 'runtime/node/node.exe') -cli (Join-Path $root 'runtime/pi/bundle/cli.js') -tag managed

Write-HOST '### 2. global node + global pi (known baseline) ###'
$globalCli = (Get-Command pi -ErrorAction SilentlyContinue).Source
if ($globalCli) {
    Run-Pi -node node -cli $globalCli -tag global
} else {
    Write-HOST 'global pi not found'
}
Write-HOST done.