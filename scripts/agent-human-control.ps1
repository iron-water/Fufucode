[CmdletBinding()]
param([ValidateSet('Submit','Acknowledge','Dispatch')][string]$Mode,
 [string]$Repository=(Split-Path $PSScriptRoot -Parent),[string]$RequestFile,[string]$CommandId,
 [ValidateSet('applied','blocked')][string]$Decision,[string]$Evidence)
$ErrorActionPreference='Stop'
$Repository=(Resolve-Path -LiteralPath $Repository).Path
. "$PSScriptRoot/agent-events.ps1"
. "$PSScriptRoot/agent-human.ps1"
$box="$Repository/tmp/agent-flow/human";[IO.Directory]::CreateDirectory($box)|Out-Null
if($Mode -in @('Submit','Acknowledge')){
 $lease=Open-HumanLease $Repository
 try{
  if($Mode -eq 'Submit'){
   $r=Read-AgentJson $RequestFile;$CommandId=$r.commandId
   if($CommandId -notmatch '^[A-Za-z0-9-]{8,80}$' -or $r.taskId -notmatch '^[A-Za-z0-9-]+$'){throw 'Invalid identity'}
   if($r.schemaVersion -ne 1 -or $r.type -cne 'human.change.request'){throw 'Invalid human change schema'}
   if($r.action -cne 'append_acceptance'){throw 'Only append_acceptance supported; no original-session prompt injection'}
   if([string]::IsNullOrWhiteSpace($r.requirement) -or $r.acceptance -isnot [array] -or -not $r.acceptance.Count){throw 'Requirement and acceptance array required'}
   if($r.requirement.Length -gt 30000 -or @($r.acceptance|Where-Object {$_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_)}).Count){throw 'Invalid requirement size/acceptance item'}
   $plan=Read-AgentJson "$Repository/tmp/agent-flow/plan.json"
   if($plan.id -cne $r.projectId -or -not @($plan.tasks|Where-Object {$_.id -ceq $r.taskId -and $_.enabled -ne $false}).Count){throw 'Unapproved task/project'}
   if($r.runId){
    $matches=@(Get-ChildItem "$Repository/tmp/agent-queue" -Directory|Where-Object {(Test-Path "$($_.FullName)/manifest.json") -and (Read-AgentJson "$($_.FullName)/manifest.json").runId -ceq $r.runId})
    if($matches.Count -ne 1 -or (Read-AgentJson "$($matches[0].FullName)/manifest.json").task -cne $r.taskId){throw 'Run/task identity mismatch'}
    if($r.baseHead -and (Read-AgentJson "$($matches[0].FullName)/state.json").head -cne $r.baseHead){throw 'Run HEAD changed; refresh before attaching request'}
   }
   $path="$box/request-$CommandId.json"
   $digest=ConvertTo-AgentOrdered $r|ConvertTo-Json -Depth 30 -Compress
   if(Test-Path $path){$prior=Read-AgentJson $path;if($prior.digest -cne $digest){throw 'Conflicting command replay'};$prior|ConvertTo-Json -Depth 30;exit}
   if(Test-Path "$Repository/tmp/agent-flow/done-$($r.taskId).json"){throw 'Already integrated; use a new task revision instead of changing past acceptance'}
   $current=Get-HumanRequirements $Repository $r.taskId
   foreach($superseded in @($r.supersedes|Where-Object {$_})){if($superseded -notin @($current.requests|ForEach-Object {$_.commandId})){throw 'Superseded requirement must belong to same task history'}}
   $replayed=@($current.requests|Where-Object commandId -ceq $CommandId)
   if($replayed.Count){if($replayed[0].record.digest -cne $digest){throw 'Conflicting recovered replay'};Save-AgentJson $path $replayed[0].record;$replayed[0].record|ConvertTo-Json -Depth 30;exit}
   if([int]$r.baseRevision -ne $current.revision){throw 'Stale requirement base; refresh'}
   $record=@{request=$r;digest=$digest;revision=($current.revision+1);status='queued';at=[DateTime]::UtcNow.ToString('o')}
   # One authoritative task index contains the complete immutable request, so a crash
   # between index and convenience request file cannot lose or duplicate a requirement.
   $current.requests+=@{commandId=$CommandId;record=$record}
   $current.revision++;$current.ready=$false;$current.taskId=$r.taskId
   Save-AgentJson "$box/task-$($r.taskId).json" $current
   Save-AgentJson $path $record
   $record|ConvertTo-Json -Depth 30
  }else{
   if($CommandId -notmatch '^[A-Za-z0-9-]{8,80}$' -or -not $Decision -or [string]::IsNullOrWhiteSpace($Evidence)){throw 'Acknowledgment identity/decision/evidence required'}
   $record=Read-AgentJson "$box/request-$CommandId.json";$task=$record.request.taskId
   $ackPath="$box/ack-$CommandId.json";$ack=@{commandId=$CommandId;taskId=$task;revision=$record.revision;decision=$Decision;evidence=$Evidence}
   if(Test-Path $ackPath){$old=Read-AgentJson $ackPath;if((ConvertTo-AgentOrdered $old|ConvertTo-Json -Compress) -cne (ConvertTo-AgentOrdered $ack|ConvertTo-Json -Compress)){throw 'Conflicting acknowledgment'}}
   Save-AgentJson $ackPath $ack
   $current=Get-HumanRequirements $Repository $task
   $current.ready=@($current.effectiveRequests|Where-Object { $p="$box/ack-$($_.commandId).json"; -not (Test-Path $p) -or (Read-AgentJson $p).decision -ne 'applied'}).Count -eq 0
   Save-AgentJson "$box/task-$task.json" $current
  }
 }finally{$lease.Dispose()};exit
}
. "$PSScriptRoot/agent-manager.ps1"
$plan=Read-AgentJson "$Repository/tmp/agent-flow/plan.json"
foreach($index in Get-ChildItem $box -Filter 'task-*.json'){
 $current=Read-AgentJson $index.FullName
 $effective=Get-HumanRequirements $Repository $current.taskId
 foreach($entry in $effective.effectiveRequests){
  $id=$entry.commandId;$path="$box/request-$id.json"
  if(-not (Test-Path $path)){Save-AgentJson $path $entry.record}
  if(Test-Path "$box/ack-$id.json"){continue}
  $claim="$box/dispatch-$id.json"
  if(Test-Path $claim){continue} # Ambiguous model transport is never automatically resent.
  if((Read-AgentJson "$Repository/tmp/agent-gate/manager.json").status -ne 'ready'){exit}
  $main=$null;try{$main=[IO.File]::Open("$Repository/tmp/agent-gate/main-writer.lock",'OpenOrCreate','ReadWrite','None')}catch [IO.IOException]{exit}
  try{
   Save-AgentJson $claim @{status='dispatching';commandId=$id;at=[DateTime]::UtcNow.ToString('o')}
   $prompt="你是原固定总控M。人类变更卡已落盘：$path。读取用户原文、任务契约和实际运行，确认新增验收要求的适用范围，不擅自改门禁/模型/授权。stepId/partId只是要求定位，当前v1仍整项审核，不假称局部协议已实现。本次不停止执行者、不发原会话提示词、不集成、不派新任务。通过 scripts/agent-human-control.ps1 -Mode Acknowledge -Repository '$Repository' -CommandId '$id' -Decision applied或blocked -Evidence '具体确认或阻塞理由' 落盘。applied表示M确认后续审核必须使用这个要求，不表示产品已通过。模型测试必须显式devin/swe-2。所有命令显式workdir。"
   $turn=Invoke-AgentManagerTurn -Repository $Repository -Model $plan.managerModel -CodexExecutable $plan.codexExecutable -RunId "human-$id" -Prompt $prompt
   Save-AgentJson $claim @{status=if($turn.completed -and (Test-Path "$box/ack-$id.json")){'acknowledged'}else{'needs_attention'};commandId=$id;logPrefix=$turn.logPrefix;finished=[DateTime]::UtcNow.ToString('o')}
  }finally{$main.Dispose()}
 }
}
