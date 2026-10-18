# Receipt precedes worker exit: only native Exited wakes the gate. Zero model calls.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/agent-events.ps1"
$root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-gate-exit-'+[guid]::NewGuid().ToString('N'))
$job="$root/tmp/agent-queue/exit-race"
[IO.Directory]::CreateDirectory("$job/events")|Out-Null
& git.exe -C $root init -b main|Out-Null
& git.exe -C $root config user.name Fixture
& git.exe -C $root config user.email fixture@localhost
Set-Content "$root/seed.txt" 'fixture'
& git.exe -C $root add -- seed.txt
& git.exe -C $root commit -m fixture|Out-Null
if($LASTEXITCODE){throw 'Fixture Git failed'}
$head=(& git.exe -C $root rev-parse HEAD).Trim()
Set-Content "$root/dummy.ps1" 'Start-Sleep -Seconds 30; exit 0'
$psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=(Get-Command pwsh.exe).Source;$psi.WorkingDirectory=$root;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
foreach($arg in @('-NoProfile','-File',"$root/dummy.ps1")){$psi.ArgumentList.Add($arg)}
$p=[Diagnostics.Process]::Start($psi)
Save-AgentJson "$job/manifest.json" @{runId='exit-race';task='TEST';worktree=$root;model='devin/swe-2'}
Save-AgentJson "$job/state.json" @{status='completed_pending_review';finished=[DateTime]::UtcNow.ToString('o');head=$head;workerPid=$p.Id;workerStarted=$p.StartTime.ToUniversalTime().ToString('o');agentPid=0}
Save-AgentJson "$job/events/04.json" @{type='delivery.received';runId='exit-race';taskId='TEST';payload=@{head=$head}}
Save-AgentJson "$job/delivery.json" @{runId='exit-race';taskId='TEST';head=$head;legacy=$true}
$control="$PSScriptRoot/agent-gate-control.ps1"
try {
    & pwsh.exe -NoProfile -File $control -Mode Start -Repository $root -Model devin/swe-2;if($LASTEXITCODE){throw 'Gate launch failed'}
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do{$s=(& pwsh.exe -NoProfile -File $control -Mode Status -Repository $root|Out-String)|ConvertFrom-Json;if($s.gateAlive){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
    if(-not $s.gateAlive -or (Test-Path "$root/tmp/agent-gate/exit-race.request.json")){throw 'Gate absent or read live writer'}
    # No JSON/event is changed after killing the disposable process.
    Stop-Process -Id $p.Id;$p.WaitForExit();$exit=$p.ExitTime.ToUniversalTime()
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do{if(Test-Path "$root/tmp/agent-gate/exit-race.request.json"){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
    $request=Get-Item "$root/tmp/agent-gate/exit-race.request.json" -ErrorAction Stop
    if($request.CreationTimeUtc -lt $exit -or @(Get-ChildItem "$root/tmp/agent-gate" -Filter '*.dispatch.json').Count){throw 'Gate accepted before exit or called model'}
    Save-AgentJson "$root/validation.json" @{passed=$true;modelCalls=0;workerPid=$p.Id;workerExited=$exit;requestCreated=$request.CreationTimeUtc;event='native Process.Exited';sourceSha256=(Get-FileHash "$PSScriptRoot/agent-gate.ps1").Hash}
    Write-Output "PASS receipt-before-worker-exit: native Exited wakes gate, no JSON trigger/model. Evidence: $root"
}finally{& pwsh.exe -NoProfile -File $control -Mode Stop -Repository $root;$p.Dispose()}
