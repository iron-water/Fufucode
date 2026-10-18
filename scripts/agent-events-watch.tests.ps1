$ErrorActionPreference='Stop'
. "$PSScriptRoot/agent-events.ps1"
$root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-watch-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$watch=New-AgentFileWatch $root "WatchTest-$PID"
try {
 $clock=[Diagnostics.Stopwatch]::StartNew()
 $signal=Wait-AgentFileWatch $watch -TimeoutSeconds 1
 if($signal -ne 'local.reconcile' -or $clock.Elapsed.TotalSeconds -gt 3){throw 'Idle missed-event fallback failed'}
 Save-AgentJson "$root/event.json" @{type='fixture.local_event'}
 $signal=Wait-AgentFileWatch $watch -TimeoutSeconds 2
 if($signal -eq 'local.reconcile' -or -not $signal.StartsWith("WatchTest-$PID")){throw 'File event wake failed'}
 Write-Output "PASS local periodic reconciliation and real file-event wake; zero model calls. Evidence: $root"
}finally{Close-AgentFileWatch $watch}
