# Native control operations in a disposable repository; no model calls.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/agent-events.ps1"
$root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-control-'+[guid]::NewGuid().ToString('N'))
$box="$root/tmp/agent-flow"
foreach($dir in @($box,"$root/tmp/agent-gate","$root/tmp/agent-queue","$root/tmp/automation-console","$root/worktrees/A")){[IO.Directory]::CreateDirectory($dir)|Out-Null}
Set-Content "$root/prompt.md" 'No model';Set-Content "$root/integration.md" 'No model';Set-Content "$root/verify.ps1" 'exit 0';Set-Content "$root/worktrees/A/keep.txt" 'keep original worktree'
$a=@{id='A';model='devin/swe-2';harness='codex';worktree="$root/worktrees/A";allowedPaths=@('modules/a.txt');integrationPaths=@();dependsOn=@();integrationDependsOn=@();gates=@('G1');promptFile="$root/prompt.md";integrationPromptFile="$root/integration.md";verificationFile="$root/verify.ps1";timeoutSeconds=60}
$b=$a.Clone();$b.id='B';$b.worktree="$root/worktrees/B";$b.allowedPaths=@('modules/b.txt');$b.dependsOn=@('A')
Save-AgentJson "$box/plan.json" @{schemaVersion=1;id='control';repository=$root;mechanismTest=$true;managerModel='devin/swe-2';codexExecutable='NEVER-CALLED.exe';tasks=@($a,$b);gates=@{G1='open'}}
Save-AgentJson "$box/runtime.json" @{status='stopped'}
Save-AgentJson "$root/tmp/agent-gate/manager.json" @{status='ready'}
Save-AgentJson "$root/tmp/automation-console/policy.json" @{mode='manual'}
& pwsh.exe -NoProfile -File "$PSScriptRoot/agent-workflow.ps1" -Mode Approve -Repository $root
if($LASTEXITCODE){throw 'Fixture approve failed'}
function Control($Mode,$Command,$Expect=$true,$Request=$null,$Target='A'){
 $args=@('-NoProfile','-File',"$PSScriptRoot/agent-task-control.ps1",'-Mode',$Mode,'-Repository',$root,'-TaskId',$Target,'-CommandId',$Command)
 if($Request){$args+=@('-RequestFile',$Request)}
 $out=& pwsh.exe @args 2>&1
 if(($LASTEXITCODE -eq 0) -ne $Expect){throw "Control assertion failed: $out"}
}
Set-Content "$root/local-worker.ps1" 'Start-Sleep -Seconds 50'
$psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=(Get-Command pwsh.exe).Source;$psi.WorkingDirectory=$root;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
foreach($arg in @('-NoProfile','-File',"$root/local-worker.ps1")){$psi.ArgumentList.Add($arg)}
$p=[Diagnostics.Process]::Start($psi)
$job="$root/tmp/agent-queue/run-A";[IO.Directory]::CreateDirectory($job)|Out-Null
Save-AgentJson "$job/manifest.json" @{task='A';workflowId='control';runId='r1'}
Save-AgentJson "$job/state.json" @{status='running';agentPid=$p.Id;agentStarted=$p.StartTime.ToUniversalTime().ToString('o')}
Control 'Revoke' 'revoke-00000001'
if(-not $p.HasExited -or -not (Test-Path "$job/revoked.json")){throw 'Exact managed cancellation failed'}
$p.Dispose()
if((Read-AgentJson "$box/plan.json").tasks[0].enabled -ne $false){throw 'Revocation not applied'}
Control 'Revoke' 'revoke-00000001' # Replay must not create another revision.
$rev=(Read-AgentJson "$box/plan.json").revision
Control 'Rebuild' 'rebuild-0000001'
$plan=Read-AgentJson "$box/plan.json";$new=@($plan.tasks|Where-Object supersedes -eq 'A')
if($new.Count -ne 1 -or $new[0].model -cne 'devin/swe-2' -or $plan.tasks[1].dependsOn[0] -cne $new[0].id){throw 'Rebuild identity/dependency remap failed'}
if((Get-Content "$root/worktrees/A/keep.txt" -Raw).Trim() -cne 'keep original worktree'){throw 'Old worktree changed'}
if($plan.gates.G1 -cne 'open'){throw 'Control bypassed gate'}
Control 'Rebuild' 'rebuild-0000001'
if((Read-AgentJson "$box/plan.json").revision -ne $plan.revision){throw 'Replay rebuilt twice'}
Save-AgentJson "$root/change.json" @{model='devin/swe-2';dependsOn=@()}
Control 'Rebuild' 'duplicate-000001' $false
if((Read-AgentJson "$box/plan.json").revision -ne $plan.revision){throw 'Stale task created sibling revision'}
Control 'Update' 'update-00000001' $true "$root/change.json" $new[0].id
Save-AgentJson "$root/change.json" @{model='another'}
Control 'Update' 'update-00000001' $false "$root/change.json" $new[0].id
Save-AgentJson "$root/change.json" @{gates=@('G0')}
Control 'Update' 'badgates-000001' $false "$root/change.json"
Save-AgentJson "$box/controls/command-unknown-000001.json" @{taskId='A';mode='Rebuild';status='inflight';requestDigest='{}'}
Control 'Rebuild' 'unknown-000001' $false
Save-AgentJson "$root/tmp/agent-gate/manager.json" @{status='inflight'}
Control 'Revoke' 'queued-0000001'
if((Read-AgentJson "$box/controls/command-queued-0000001.json").status -cne 'queued'){throw 'Busy M control not queued'}
$hostInfo=[Diagnostics.ProcessStartInfo]::new();$hostInfo.FileName=(Get-Command pwsh.exe).Source;$hostInfo.WorkingDirectory=$root;$hostInfo.UseShellExecute=$false;$hostInfo.CreateNoWindow=$true
foreach($arg in @('-NoProfile','-File',"$PSScriptRoot/agent-task-control-host.ps1",'-Mode','Run','-Repository',$root)){$hostInfo.ArgumentList.Add($arg)}
$hostProcess=[Diagnostics.Process]::Start($hostInfo)
$deadline=[DateTime]::UtcNow.AddSeconds(10)
while(-not (Test-Path "$box/controls/runtime.json")){if([DateTime]::UtcNow -gt $deadline){throw 'Control host failed to start'};Start-Sleep -Milliseconds 50}
Save-AgentJson "$root/tmp/agent-gate/manager.json" @{status='ready'}
$deadline=[DateTime]::UtcNow.AddSeconds(20)
try {
 while((Read-AgentJson "$box/controls/command-queued-0000001.json").status -ne 'done'){
  if([DateTime]::UtcNow -gt $deadline){throw 'Queued control did not automatically apply on manager event'}
  Start-Sleep -Milliseconds 100
 }
}finally{Save-AgentJson "$box/controls/host-stop.json" @{stop=$true};if(-not $hostProcess.WaitForExit(5000)){throw 'Own fixture control host did not exit'};$hostProcess.Dispose()}
Save-AgentJson "$box/control-validation.json" @{status='passed';modelCalls=0;scope='native-revoke-rebuild-update';checks=@('exact cancellation','old worktree preserved','same default model','dependency remap','gates unchanged','replay no second revision','conflicting replay rejected','unknown claim frozen','busy M queued and applied')}
Write-Output "PASS native revoke/rebuild/update/queue/replay/unknown guards; zero models. Evidence: $root"
