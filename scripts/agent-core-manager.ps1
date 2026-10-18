# Expert reports go to the original coordinator; experts never directly edit plans.
[CmdletBinding()]
param([ValidateSet('Dispatch','Decide','Apply')][string]$Mode='Dispatch',[string]$Repository=(Split-Path $PSScriptRoot -Parent),
 [string]$Key,[ValidateSet('accept','rework','blocked','defer')][string]$Decision,[string]$Evidence,[string]$ProposalFile)
$ErrorActionPreference='Stop'
$Repository=(Resolve-Path -LiteralPath $Repository).Path
. "$PSScriptRoot/agent-events.ps1"
. "$PSScriptRoot/agent-manager.ps1"
$box="$Repository/tmp/agent-core"
if($Mode -eq 'Apply'){
 $applyLease=[IO.File]::Open("$box/apply-writer.lock",'OpenOrCreate','ReadWrite','None')
 try{
 if($Key -notmatch '^[a-f0-9]{64}$'){throw 'Explicit proposal identity required'}
 $approvedDecision=Read-AgentJson "$box/manager-$Key.json"
 if($approvedDecision.decision -cne 'accept' -or -not $approvedDecision.proposalFile){throw 'No accepted concrete amendment'}
 if($approvedDecision.applied){exit}
 $proposal=Read-AgentJson $approvedDecision.proposalFile
 if($proposal.type -cne 'core.plan.amendment' -or $proposal.basePlanHash -cne $approvedDecision.taskPlanHash -or $proposal.operations -isnot [array] -or -not $proposal.operations.Count){throw 'Invalid versioned amendment'}
 $claim="$box/apply-$Key.json"
 if(Test-Path $claim){throw 'Prior apply outcome unresolved; no automatic resend'}
 if((Get-FileHash "$Repository/tmp/agent-flow/plan.json").Hash -cne $proposal.basePlanHash){throw 'Proposal is stale; no plan rewrite'}
 $ids=@($proposal.operations|ForEach-Object {$_.taskId})
 if(@($ids|Select-Object -Unique).Count -ne $ids.Count){throw 'Duplicate amendment target'}
 foreach($operation in $proposal.operations){
  if($operation.taskId -notmatch '^[A-Za-z0-9-]+$' -or $operation.changes -isnot [Collections.IDictionary]){throw 'Invalid amendment operation'}
  foreach($field in $operation.changes.Keys){if($field -notin @('model','harness','dependsOn','integrationDependsOn','allowedPaths','integrationPaths','timeoutSeconds','promptFile','verificationFile','integrationPromptFile')){throw 'Amendment cannot edit gates/runtime identities'}}
 }
 Save-AgentJson $claim @{status='applying';key=$Key;operations=$proposal.operations;automaticRetry=$false}
 $index=0;$replacements=@{}
 foreach($operation in $proposal.operations){
  $command="arch-$($Key.Substring(0,16))-$index";$inputPath="$box/amendment-$command.json"
  $changes=$operation.changes|ConvertTo-Json -Depth 30|ConvertFrom-Json -AsHashtable
  foreach($field in @('dependsOn','integrationDependsOn')){if($changes.ContainsKey($field)){$changes[$field]=@($changes[$field]|ForEach-Object {if($replacements.ContainsKey($_)){$replacements[$_]}else{$_}})}}
  Save-AgentJson $inputPath $changes
  & pwsh.exe -NoProfile -File "$PSScriptRoot/agent-task-control.ps1" -Mode Update -Repository $Repository -TaskId $operation.taskId -CommandId $command -RequestFile $inputPath
  if($LASTEXITCODE){throw 'Amendment stopped; prior operations recorded, do not resend batch'}
  $control=Read-AgentJson "$Repository/tmp/agent-flow/controls/command-$command.json"
  if($control.status -ne 'done'){throw 'Amendment queued; batch stays unresolved for explicit reconciliation'}
  $replacements[$operation.taskId]=$control.replacementTask
  $index++
 }
 Save-AgentJson $claim @{status='done';key=$Key;operations=$proposal.operations;finished=[DateTime]::UtcNow.ToString('o')}
 $approvedDecision.applied=$true;$approvedDecision.appliedPlanHash=(Get-FileHash "$Repository/tmp/agent-flow/plan.json").Hash;Save-AgentJson "$box/manager-$Key.json" $approvedDecision;exit
 }finally{$applyLease.Dispose()}
}
if($Mode -eq 'Decide'){
 if($Key -notmatch '^[a-f0-9]{64}$' -or -not $Decision -or [string]::IsNullOrWhiteSpace($Evidence)){throw 'Explicit expert review identity/decision/evidence required'}
 $result=Read-AgentJson "$box/results/$Key.json"
 if($result.role -cne 'architect' -or $result.members.Count -ne 3){throw 'Three independent expert reports required'}
 $envelope=Read-AgentJson "$box/events/$Key.json"
 $expected=$envelope.event.basePlanHash
 if($expected -and (Get-FileHash "$Repository/tmp/agent-flow/plan.json").Hash -cne $expected){throw 'Expert proposal stale against current task plan'}
 $record=@{key=$Key;decision=$Decision;evidence=$Evidence;proposalFile=$ProposalFile;taskPlanHash=(Get-FileHash "$Repository/tmp/agent-flow/plan.json").Hash;applied=$false}
 $path="$box/manager-$Key.json"
 if(Test-Path $path){$prior=Read-AgentJson $path;if((ConvertTo-AgentOrdered $prior|ConvertTo-Json -Compress) -cne (ConvertTo-AgentOrdered $record|ConvertTo-Json -Compress)){throw 'Conflicting expert review replay'};exit}
 Save-AgentJson $path $record;exit
}
$plan=Read-AgentJson "$Repository/tmp/agent-flow/plan.json"
if(-not (Test-Path "$box/results")){exit}
foreach($file in Get-ChildItem "$box/results" -Filter '*.json'){
 $result=Read-AgentJson $file.FullName;$Key=$result.key
 if($result.role -cne 'architect' -or (Test-Path "$box/manager-$Key.json")){continue}
 if((Read-AgentJson "$Repository/tmp/agent-gate/manager.json").status -ne 'ready'){exit}
 $claim="$box/manager-dispatch-$Key.json"
 if(Test-Path $claim){continue}
 $lease=$null;try{$lease=[IO.File]::Open("$Repository/tmp/agent-gate/main-writer.lock",'OpenOrCreate','ReadWrite','None')}catch [IO.IOException]{exit}
 try{
  Save-AgentJson $claim @{status='dispatching';key=$Key;at=[DateTime]::UtcNow.ToString('o')}
  $prompt="你是固定总控M的路线协调职责。三专家的独立报告已落盘：$($file.FullName)，原触发事件$box/events/$Key.json。按当前项目需要评估建议与异议，核对计划版本和实际门禁。专家不能直接accept产品或覆盖任务书。本次只形成路线决定，通过scripts/agent-core-manager.ps1 -Mode Decide -Repository '$Repository' -Key '$Key' -Decision accept/rework/blocked/defer -Evidence '具体评估理由' -ProposalFile '必要时你生成的版本化变更提案JSON路径' 落盘。具体提案格式type=core.plan.amendment,basePlanHash=当前plan.json的SHA256,operations数组每项taskId及changes，只含agent-task-control允许的版本修改字段。修正任务书时写入新的不可变promptFile，不覆盖旧契约。accept仅表示建议被采纳，applied仍false；本地Apply会通过版本控制入口执行，不绕过门禁。本轮不要修改源码/计划、不要集成/启动执行者。没有具体路线变更内容时defer而不是虚构方案。模型试跑显式devin/swe-2，所有命令显式workdir。"
  $turn=Invoke-AgentManagerTurn -Repository $Repository -Model $plan.managerModel -CodexExecutable $plan.codexExecutable -RunId "architecture-$Key" -Prompt $prompt
  Save-AgentJson $claim @{status=if($turn.completed -and (Test-Path "$box/manager-$Key.json")){'decided'}else{'needs_attention'};key=$Key;logPrefix=$turn.logPrefix;finished=[DateTime]::UtcNow.ToString('o')}
 }finally{$lease.Dispose()}
}
