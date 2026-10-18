# Local-only lifecycle test: the short-lived launcher exits before checking scheduler-owned PID.
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-gate-scheduler-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$control=Join-Path $PSScriptRoot 'agent-gate-control.ps1'
function Control($Mode) {
    & pwsh.exe -NoProfile -File $control -Repository $root -Mode $Mode
    if($LASTEXITCODE){throw "Control $Mode failed"}
}
function Read-Status { (Control 'Status' | Out-String) | ConvertFrom-Json }
try {
    Control 'Start' # This launcher must finish; gate is owned by Task Scheduler.
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do {$s=Read-Status; if($s.gateAlive){break}; Start-Sleep -Milliseconds 300}while([DateTime]::UtcNow -lt $deadline)
    if(-not $s.gateAlive -or $s.taskState -ne 4){throw 'Scheduler-owned gate did not start'}
    $parent=Get-CimInstance Win32_Process -Filter "ProcessId=$($s.gateParentPid)"
    if($s.gateParentPid -eq $PID -or $parent.Name -match 'codex|pwsh|powershell'){throw 'Gate still depends on interactive launcher'}
    $s|ConvertTo-Json -Depth 15|Set-Content "$root/running.json"
    Control 'Pause'; if(-not (Read-Status).paused){throw 'Pause flag missing'}
    Control 'Configure'
    Control 'Resume'; if((Read-Status).paused){throw 'Resume flag remains'}
    Control 'Stop'
    Control 'Resume'
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do {$s=Read-Status; if($s.gateAlive -and $s.runtime.pid -ne (Get-Content "$root/running.json" -Raw|ConvertFrom-Json).runtime.pid){break}; Start-Sleep -Milliseconds 300}while([DateTime]::UtcNow -lt $deadline)
    if(-not $s.gateAlive){throw 'Stopped task did not resume'}
    if(@(Get-ChildItem "$root/tmp/agent-gate" -Filter '*.dispatch.json').Count){throw 'Idle gate dispatched a model'}
    $s|ConvertTo-Json -Depth 15|Set-Content "$root/resumed.json"
    Write-Output "PASS: launcher exited, scheduler parent=$($parent.Name), idle zero dispatch, pause/resume, stop/restart. Evidence: $root"
} finally {Control 'Stop'}
