# Disposable native worker exit and startup recovery. No harness/model is launched.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/agent-events.ps1"
$root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-crashflow-'+[guid]::NewGuid().ToString('N'))
$box="$root/tmp/agent-flow";$job="$root/tmp/agent-queue/crash-A-1"
[IO.Directory]::CreateDirectory($box)|Out-Null;[IO.Directory]::CreateDirectory($job)|Out-Null
Set-Content "$box/prompt.md" 'No model';Set-Content "$box/integrate.md" 'No model';Set-Content "$box/verify.ps1" 'exit 0'
Set-Content "$root/dummy.ps1" 'Start-Sleep -Seconds 30; exit 0'
$psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=(Get-Command pwsh.exe).Source;$psi.WorkingDirectory=$root;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
foreach($arg in @('-NoProfile','-File',"$root/dummy.ps1")){$psi.ArgumentList.Add($arg)}
$p=[Diagnostics.Process]::Start($psi)
$m=@{runId=[guid]::NewGuid().ToString('N');workflowId='crash';task='A';worktree="$root/never-created";model='devin/swe-2';harness='codex';attempt=1}
Save-AgentJson "$job/manifest.json" $m
Save-AgentJson "$job/state.json" @{status='running';workerPid=$p.Id;agentPid=$null;task='A';runId=$m.runId;started=[DateTime]::UtcNow.ToString('o')}
$task=@{id='A';model='devin/swe-2';harness='codex';worktree=$m.worktree;allowedPaths=@('module.txt');dependsOn=@();integrationDependsOn=@();integrationPaths=@();gates=@('G0');promptFile="$box/prompt.md";integrationPromptFile="$box/integrate.md";verificationFile="$box/verify.ps1"}
Save-AgentJson "$box/plan.json" @{schemaVersion=1;id='crash';repository=$root;mechanismTest=$true;managerModel='devin/swe-2';codexExecutable='NO-MODEL-MUST-BE-CALLED.exe';gates=@{G0='passed'};tasks=@($task)}
$control="$PSScriptRoot/agent-workflow.ps1"
& pwsh.exe -NoProfile -File $control -Mode Approve -Repository $root;if($LASTEXITCODE){throw 'Approval failed'}
& pwsh.exe -NoProfile -File $control -Mode Register -Repository $root;if($LASTEXITCODE){throw 'Register failed'}
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do{if(Test-Path "$box/runtime.json"){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
    if(-not (Test-Path "$box/runtime.json")){throw 'Workflow absent'}
    Stop-Process -Id $p.Id
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do{$state=Read-AgentJson "$job/state.json";if($state.status -eq 'orphaned_worker' -and (Test-Path "$job/events/07.json")){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
    if($state.status -ne 'orphaned_worker' -or (Read-AgentJson "$job/events/07.json").type -ne 'supervisor.failed'){throw 'Worker crash not locally persisted'}
    if(Test-Path "$job/events/04.json"){throw 'Crash incorrectly received as delivery'}
    if(@(Get-ChildItem $box -Filter 'launch-*.json').Count -or (Test-Path $m.worktree)){throw 'Crash automatically resent/recreated'}
    Save-AgentJson "$root/validation.json" @{passed=$true;modelCalls=0;workerPid=$p.Id;status=$state.status;failureEvent="$job/events/07.json";originalState="$job/orphaned-state.json";sourceSha256=(Get-FileHash "$PSScriptRoot/agent-workflow.ps1").Hash}
    Write-Output "PASS actual native worker crash -> Exited event -> persisted failure, no receipt/relaunch/worktree. Evidence: $root"
}finally{& pwsh.exe -NoProfile -File $control -Mode Stop -Repository $root;$p.Dispose()}
