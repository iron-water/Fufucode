# Versioned control requests. Run manifests, sessions and worktrees remain intact.
[CmdletBinding()]
param([ValidateSet('Status','Revoke','Rebuild','Update')][string]$Mode='Status',
 [string]$Repository=(Split-Path $PSScriptRoot -Parent),[string]$TaskId,[string]$RequestFile,[string]$CommandId)
$ErrorActionPreference='Stop'
$Repository=(Resolve-Path -LiteralPath $Repository).Path
. "$PSScriptRoot/agent-events.ps1"
. "$PSScriptRoot/agent-human.ps1"
$box=Join-Path $Repository 'tmp/agent-flow'
$controls=Join-Path $box 'controls'
[IO.Directory]::CreateDirectory($controls)|Out-Null
if($Mode -eq 'Status'){@{controls=@(Get-ChildItem $controls -Filter '*.json'|ForEach-Object {Read-AgentJson $_.FullName})}|ConvertTo-Json -Depth 30;exit}
if($TaskId -notmatch '^[A-Za-z0-9-]+$' -or $CommandId -notmatch '^[A-Za-z0-9-]{8,80}$'){throw 'Explicit task and unique command identity required'}
$controlLock=[IO.File]::Open("$box/control-writer.lock",'OpenOrCreate','ReadWrite','None')
try {
 $request=if($RequestFile){Read-AgentJson $RequestFile}else{@{}}
 $requestDigest=(ConvertTo-AgentOrdered $request|ConvertTo-Json -Depth 30 -Compress)
 $claimPath="$controls/command-$CommandId.json"
 if(Test-Path $claimPath){$prior=Read-AgentJson $claimPath;if($prior.taskId -cne $TaskId -or $prior.mode -cne $Mode -or $prior.requestDigest -cne $requestDigest){throw 'Conflicting command replay'};if($prior.status -eq 'done'){$prior|ConvertTo-Json -Depth 20;exit};if($prior.status -ne 'queued'){throw 'Prior control outcome unknown; do not resend'}}
 $plan=Read-AgentJson "$box/plan.json";$approval=Read-AgentJson "$box/approval.json"
 if($approval.sha256 -cne (Get-FileHash "$box/plan.json").Hash){throw 'Plan not approved'}
 $source=@($plan.tasks|Where-Object id -ceq $TaskId)
 if($source.Count -ne 1){throw 'Unknown task'}
 $task=$source[0]
 if($Mode -ne 'Revoke' -and @($plan.tasks|Where-Object {$_.supersedes -ceq $TaskId -and $_.enabled -ne $false}).Count){throw 'Task already replaced; select its latest enabled revision'}
 foreach($key in $request.Keys){if($key -notin @('model','harness','dependsOn','integrationDependsOn','promptFile','verificationFile','integrationPromptFile','allowedPaths','integrationPaths','timeoutSeconds')){throw "Unsupported task change: $key"}}
 $claim=@{commandId=$CommandId;taskId=$TaskId;mode=$Mode;requestFile=$RequestFile;requestDigest=$requestDigest;status='inflight';at=[DateTime]::UtcNow.ToString('o')}
 Save-AgentJson $claimPath $claim
 # Revoke first: even a crash cannot turn this command into continued integration.
 Save-AgentJson "$controls/task-$TaskId.json" @{taskId=$TaskId;state='revoked';commandId=$CommandId;at=$claim.at;reason=$Mode}
 & "$PSScriptRoot/agent-workflow.ps1" -Mode Pause -Repository $Repository
 & "$PSScriptRoot/agent-gate-control.ps1" -Mode Pause -Repository $Repository
 foreach($dir in @(Get-ChildItem "$Repository/tmp/agent-queue" -Directory -ErrorAction SilentlyContinue)){
  if(-not (Test-Path "$($dir.FullName)/manifest.json")){continue}
  $m=Read-AgentJson "$($dir.FullName)/manifest.json"
  if($m.workflowId -cne $plan.id -or $m.task -cne $TaskId){continue}
  if(-not (Test-Path "$($dir.FullName)/state.json")){continue}
  $s=Read-AgentJson "$($dir.FullName)/state.json"
  Save-AgentJson "$($dir.FullName)/revoked.json" @{commandId=$CommandId;taskId=$TaskId;runId=$m.runId;at=$claim.at}
  if($s.status -in @('starting','running') -and $s.agentPid){
   if(-not $s.agentStarted){throw 'Missing exact agent identity; keep fence, no kill'}
   $p=Get-Process -Id $s.agentPid -ErrorAction SilentlyContinue
   $expected=if($s.agentStarted -is [DateTime]){$s.agentStarted}else{[DateTime]::Parse($s.agentStarted)}
   if($p){if($p.StartTime.ToUniversalTime() -ne $expected.ToUniversalTime()){throw 'PID birth mismatch; no kill and no replacement'};$p.Kill($true);$p.WaitForExit();$p.Dispose()}
  }
 }
 if((Read-AgentJson "$Repository/tmp/agent-gate/manager.json").status -ne 'ready'){$claim.status='queued';$claim.reason='waiting_for_manager_settle';Save-AgentJson $claimPath $claim;$claim|ConvertTo-Json -Depth 20;exit}
 $mainLock=[IO.File]::Open("$Repository/tmp/agent-gate/main-writer.lock",'OpenOrCreate','ReadWrite','None')
 try {
  & "$PSScriptRoot/agent-workflow.ps1" -Mode Stop -Repository $Repository
  $deadline=[DateTime]::UtcNow.AddSeconds(25)
  while((Read-AgentJson "$box/runtime.json").status -ne 'stopped'){
   if([DateTime]::UtcNow -ge $deadline){throw 'Workflow did not stop; control unresolved'}
   Start-Sleep -Milliseconds 100
  }
  [IO.Directory]::CreateDirectory("$box/revisions")|Out-Null
  Copy-Item -LiteralPath "$box/plan.json" -Destination "$box/revisions/$CommandId.plan.json"
  Copy-Item -LiteralPath "$box/approval.json" -Destination "$box/revisions/$CommandId.approval.json"
  $task.enabled=$false
  if($Mode -ne 'Revoke'){
   $newTask=($task|ConvertTo-Json -Depth 30|ConvertFrom-Json -AsHashtable)
   $revisionKey=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($CommandId))).Substring(0,16).ToLowerInvariant()
   $newId="$TaskId-r$revisionKey"
   $newTask.id=$newId;$newTask.enabled=$true;$newTask.supersedes=$TaskId
   $newTask.worktree=Join-Path (Split-Path $task.worktree -Parent) $newId
   if(Test-Path -LiteralPath $newTask.worktree){throw 'Revision worktree exists; never overwrite or recreate'}
   foreach($key in $request.Keys){$newTask[$key]=$request[$key]}
   # No gates in the editable keys: a graph revision is not acceptance.
   $plan.tasks+= $newTask
   foreach($downstream in $plan.tasks|Where-Object {$_.id -ne $newId -and $_.id -ne $TaskId -and $_.enabled -ne $false}){
    foreach($key in @('dependsOn','integrationDependsOn')){$downstream[$key]=@($downstream[$key]|ForEach-Object {if($_ -ceq $TaskId){$newId}else{$_}})}
   }
   $claim.replacementTask=$newId
  }
  $plan.revision=if($plan.revision){[int]$plan.revision+1}else{2}
  Save-AgentJson "$box/plan.json" $plan
  & pwsh.exe -NoProfile -File "$PSScriptRoot/agent-workflow.ps1" -Mode Approve -Repository $Repository
  if($LASTEXITCODE){
   Copy-Item -LiteralPath "$box/revisions/$CommandId.plan.json" -Destination "$box/plan.json" -Force
   Copy-Item -LiteralPath "$box/revisions/$CommandId.approval.json" -Destination "$box/approval.json" -Force
   throw 'Invalid revision; original bytes restored, revocation stays fenced'
  }
  if($Mode -ne 'Revoke'){
   $human=Get-HumanRequirements $Repository $TaskId
   if($human.revision -gt 0){$human.taskId=$newId;$human.inheritedFromTask=$TaskId;Save-AgentJson "$box/human/task-$newId.json" $human}
  }
  $claim.status='done';$claim.revision=$plan.revision;$claim.finished=[DateTime]::UtcNow.ToString('o');Save-AgentJson $claimPath $claim
 }finally{$mainLock.Dispose()}
 $policyPath="$Repository/tmp/automation-console/policy.json"
 if((Test-Path $policyPath) -and (Read-AgentJson $policyPath).mode -eq 'auto'){
  & "$PSScriptRoot/agent-gate-control.ps1" -Mode Resume -Repository $Repository
  & "$PSScriptRoot/agent-workflow.ps1" -Mode Resume -Repository $Repository
 }
 $claim|ConvertTo-Json -Depth 20
}finally{$controlLock.Dispose()}
