# Explicit approved task graph, driven by delivery/review/plan filesystem events.
[CmdletBinding()]
param([ValidateSet('Approve','Register','Run','Status','Pause','Resume','Stop','Reconcile')][string]$Mode='Status',
    [string]$Repository=(Split-Path $PSScriptRoot -Parent),[string]$TaskId)
$ErrorActionPreference='Stop'
$Repository=(Resolve-Path -LiteralPath $Repository).Path
$box=Join-Path $Repository 'tmp/agent-flow'
[IO.Directory]::CreateDirectory($box)|Out-Null
. "$PSScriptRoot/agent-events.ps1"
. "$PSScriptRoot/agent-manager.ps1"
. "$PSScriptRoot/agent-human.ps1"
function Workflow-Read($Path){Read-AgentJson $Path}
function Workflow-Git($Directory,$Arguments){$out=& git.exe -c core.quotepath=false -C $Directory @Arguments;if($LASTEXITCODE){throw "Git failed: $($Arguments -join ' ')"};return ($out -join "`n")}
function Workflow-Log($Type,$Data){@{at=[DateTime]::UtcNow.ToString('o');type=$Type;data=$Data}|ConvertTo-Json -Depth 20 -Compress|Add-Content "$box/events.jsonl"}
function Workflow-Revoked($Task){$path="$box/controls/task-$($Task.id).json";return ($Task.enabled -eq $false -or ((Test-Path $path) -and (Workflow-Read $path).state -eq 'revoked'))}
function Workflow-ScopeOverlap($Left,$Right){
 foreach($a in $Left){foreach($b in $Right){
  $pa=($a -split '[*?\[]',2)[0];$pb=($b -split '[*?\[]',2)[0]
  if($a -like $b -or $b -like $a -or $pa.StartsWith($pb,[StringComparison]::OrdinalIgnoreCase) -or $pb.StartsWith($pa,[StringComparison]::OrdinalIgnoreCase)){return $true}
 }};return $false
}
function Workflow-CanLaunch($Plan,$Task){
 foreach($other in $Plan.tasks){
  if($other.id -ceq $Task.id -or (Test-Path "$box/done-$($other.id).json")){continue}
  $prior=Workflow-Latest $Plan $other
  if(-not $prior){continue}
  $state=Workflow-Read "$($prior.directory)/state.json"
  if((Workflow-Revoked $other) -and -not (Test-AgentProcessIdentity $state 'agent') -and -not (Test-AgentProcessIdentity $state 'worker')){continue}
  if($state.status -eq 'failed_or_missing_card' -and $state.exitCode -ne 0 -and $state.finished -and
     -not (Test-AgentProcessIdentity $state 'agent') -and -not (Test-AgentProcessIdentity $state 'worker')){continue}
  if(Workflow-ScopeOverlap (@($Task.allowedPaths)+@($Task.integrationPaths)) (@($other.allowedPaths)+@($other.integrationPaths))){return $false}
 };return $true
}
function Workflow-IndexHash {return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((Workflow-Git $Repository @('diff','--cached','--binary')))))}
function Workflow-AllowedDirty($Allowed) {
    return ((Workflow-Git $Repository (@('diff','--name-only','--')+$Allowed))+"`n"+(Workflow-Git $Repository (@('diff','--cached','--name-only','--')+$Allowed))+"`n"+(Workflow-Git $Repository (@('ls-files','--others','--exclude-standard','--')+$Allowed))).Trim()
}
function Workflow-Plan {
    $plan=Workflow-Read "$box/plan.json";$approval=Workflow-Read "$box/approval.json"
    if($approval.sha256 -cne (Get-FileHash "$box/plan.json").Hash -or $plan.schemaVersion -ne 1 -or $plan.repository -ne $Repository){throw 'Task plan not explicitly approved or repository mismatch'}
    return $plan
}
function Validate-WorkflowPlan($Plan) {
    if($Plan.repository -ne $Repository -or $Plan.schemaVersion -ne 1 -or $Plan.id -notmatch '^[a-zA-Z0-9-]+$' -or -not $Plan.managerModel -or -not $Plan.codexExecutable){throw 'Invalid explicit workflow plan'}
    if($Plan.mechanismTest -and $Plan.managerModel -cne 'devin/swe-2'){throw 'Test manager must be devin/swe-2'}
    $ids=@($Plan.tasks|ForEach-Object {$_.id});if(@($ids|Select-Object -Unique).Count -ne $ids.Count){throw 'Duplicate task identity'}
    foreach($task in $Plan.tasks) {
        if($task.id -notmatch '^[A-Za-z0-9-]+$' -or -not $task.model -or $task.harness -notin @('omp','codex') -or
            -not $task.allowedPaths -or -not $task.promptFile -or -not $task.integrationPromptFile -or -not $task.verificationFile){throw 'Task identity/model/scope/worker and integration contract required'}
        if($Plan.mechanismTest -and $task.model -cne 'devin/swe-2'){throw 'Test worker must be devin/swe-2'}
        foreach($path in @($task.allowedPaths)+@($task.integrationPaths)) {
            if($path -match '(^[*/]|\.\.|[:\\])' -or $path -match '^(reference|\.git|tmp)(/|$)' -or $path -eq '*'){throw "Invalid task scope: $path"}
        }
        foreach($dependency in ((@($task.dependsOn)+@($task.integrationDependsOn))|Where-Object {$_})){if($dependency -notin $ids -or $dependency -eq $task.id){throw 'Unknown or self dependency'}}
        foreach($file in @($task.promptFile,$task.integrationPromptFile,$task.verificationFile)){if(-not (Test-Path -LiteralPath $file)){throw "Missing approved contract: $file"}}
    }
    # Detect cycles before doing any work.
    $resolved=@();do{$prior=$resolved.Count;foreach($task in $Plan.tasks){$dependencies=(@($task.dependsOn)+@($task.integrationDependsOn))|Where-Object {$_};if($task.id -notin $resolved -and @($dependencies|Where-Object {$_ -notin $resolved}).Count -eq 0){$resolved+=$task.id}}}while($resolved.Count -gt $prior)
    if($resolved.Count -ne $ids.Count){throw 'Cyclic task dependencies'}
    $backlogIds=@($Plan.projectBacklog|Where-Object {$_}|ForEach-Object {$_.id})
    if(@($backlogIds|Select-Object -Unique).Count -ne $backlogIds.Count){throw 'Duplicate project backlog identity'}
    foreach($entry in $Plan.projectBacklog|Where-Object {$_}){
        if($entry.status -notin @('scheduled','completed','blocked','module_integrated','unplanned')){throw 'Unknown project disposition'}
        if($entry.status -eq 'scheduled' -and $entry.id -notin $ids){throw 'Scheduled project task lacks executable contract'}
        if($entry.status -in @('blocked','module_integrated') -and -not $entry.reason){throw 'Project blockage must have persistent reason'}
    }
}
if($Mode -eq 'Approve') {
    $p=Workflow-Read "$box/plan.json";Validate-WorkflowPlan $p
    $contracts=@{};foreach($task in $p.tasks){foreach($file in @($task.promptFile,$task.integrationPromptFile,$task.verificationFile)){$contracts[$file]=(Get-FileHash -LiteralPath $file).Hash}}
    Save-AgentJson "$box/approval.json" @{schemaVersion=1;sha256=(Get-FileHash "$box/plan.json").Hash;contracts=$contracts;approved=[DateTime]::UtcNow.ToString('o')}
    Workflow-Log 'plan.approved' @{id=$p.id;tasks=@($p.tasks|ForEach-Object {$_.id})};exit
}
$key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Repository.ToLowerInvariant()))).Substring(0,12)
$taskName="Fufucode-Workflow-$key"
if($Mode -eq 'Pause'){Set-Content "$box/paused" 'Explicitly paused; in-flight action is allowed to finish';Save-AgentJson "$box/control.json" @{changed=[DateTime]::UtcNow.ToString('o')};exit}
if($Mode -in @('Register','Resume','Stop')) {
    $scheduler=New-Object -ComObject Schedule.Service;$scheduler.Connect();$folder=$scheduler.GetFolder('\')
    $existing=$null;try{$existing=$folder.GetTask($taskName)}catch{}
    if($Mode -eq 'Stop'){Save-AgentJson "$box/stop.json" @{stop=$true};if($existing){$existing.Enabled=$false};exit}
    if($Mode -eq 'Resume'){if(-not $existing){throw 'Workflow not registered'};if(Test-Path "$box/paused"){Remove-Item "$box/paused"};if(Test-Path "$box/stop.json"){Remove-Item "$box/stop.json"};Save-AgentJson "$box/control.json" @{changed=[DateTime]::UtcNow.ToString('o')};$existing.Enabled=$true;if($existing.State -ne 4){[void]$existing.Run($null)};exit}
    $plan=Workflow-Plan;Validate-WorkflowPlan $plan
    if(-not $plan.mechanismTest) {
        $proof=Workflow-Read "$box/workflow-validated.json"
        if($proof.scope -cne 'isolated-full-flow' -or $proof.status -cne 'passed' -or $proof.model -cne 'devin/swe-2' -or -not $proof.sourceConsistencyVerified){throw 'Production workflow requires real lowest-cost complete-flow proof'}
        foreach($file in $proof.scripts.Keys){if((Get-FileHash (Join-Path $PSScriptRoot $file)).Hash -cne $proof.scripts[$file]){throw "Workflow code changed since proof: $file"}}
    }
    if($existing){throw 'Existing workflow owner; inspect instead of replacing'}
    # A GUI script host owns a private hidden PowerShell console, separate from CLI console teardown.
    $command='"'+(Get-Command pwsh.exe).Source+'" -NoLogo -NoProfile -NonInteractive -File "'+$PSCommandPath+'" -Mode Run -Repository "'+$Repository+'"'
    Set-Content "$box/host.js" ('var s = new ActiveXObject("WScript.Shell");s.CurrentDirectory='+($Repository|ConvertTo-Json -Compress)+';WScript.Quit(s.Run('+($command|ConvertTo-Json -Compress)+',0,true));') -Encoding ascii
    $d=$scheduler.NewTask(0);$d.Principal.UserId=[Security.Principal.WindowsIdentity]::GetCurrent().Name;$d.Principal.LogonType=3;$d.Principal.RunLevel=0
    $d.Settings.ExecutionTimeLimit='PT0S';$d.Settings.MultipleInstances=2;$d.Settings.DisallowStartIfOnBatteries=$false;$d.Settings.StopIfGoingOnBatteries=$false
    $d.Settings.RestartCount=3;$d.Settings.RestartInterval='PT1M';$trigger=$d.Triggers.Create(9);$trigger.UserId=$d.Principal.UserId
    $a=$d.Actions.Create(0);$a.Path=(Get-Command wscript.exe).Source;$a.WorkingDirectory=$Repository;$a.Arguments='//B //Nologo //E:JScript "'+(Join-Path $box 'host.js')+'"'
    $registered=$folder.RegisterTaskDefinition($taskName,$d,2,$d.Principal.UserId,$null,3);[void]$registered.Run($null);Write-Output $taskName;exit
}
if($Mode -eq 'Status') {
    $runtime=if(Test-Path "$box/runtime.json"){Workflow-Read "$box/runtime.json"}else{$null}
    $alive=if($runtime){[bool](Get-CimInstance Win32_Process -Filter "ProcessId=$($runtime.pid)"|Where-Object {$_.CommandLine -match 'agent-workflow.ps1' -and $_.CommandLine.Contains($Repository)})}else{$false}
    @{taskName=$taskName;alive=$alive;paused=(Test-Path "$box/paused");runtime=$runtime;integrations=@(Get-ChildItem $box -Filter 'integration-*.json'|ForEach-Object {Workflow-Read $_.FullName});log="$box/events.jsonl"}|ConvertTo-Json -Depth 20;exit
}
function Workflow-Latest($Plan,$Task) {
    $runs=@(Get-ChildItem "$Repository/tmp/agent-queue" -Directory|ForEach-Object {
        $path=Join-Path $_.FullName 'manifest.json';if(Test-Path $path){$m=Workflow-Read $path;if($m.workflowId -ceq $Plan.id -and $m.task -ceq $Task.id){@{directory=$_.FullName;manifest=$m;created=$_.CreationTimeUtc}}}
    }|Sort-Object created)
    if($runs.Count){return $runs[-1]};return $null
}
function Workflow-Launch($Plan,$Task,$Attempt=1,$Resume=$null,[bool]$Format=$false,[bool]$Inherit=$false) {
    $scopeState="$box/task-$($Task.id).json"
    if(-not (Test-Path $scopeState)) {
        if(Test-Path -LiteralPath $Task.worktree){throw 'Never take over an unknown existing worktree'}
        $base=Workflow-Git $Repository @('rev-parse','HEAD')
        Save-AgentJson $scopeState @{status='creating';worktree=$Task.worktree;base=$base}
        [void](Workflow-Git $Repository @('worktree','add','-b',"agent/$($Plan.id)-$($Task.id)",$Task.worktree))
        Save-AgentJson $scopeState @{status='ready';worktree=$Task.worktree;base=$base}
    }
    $ownership=Workflow-Read $scopeState;if($ownership.status -ne 'ready' -or $ownership.worktree -ne $Task.worktree){throw 'Ambiguous worktree creation; do not recreate'}
    $suffix=if($Format){'-card'}else{''};$job="$Repository/tmp/agent-queue/$($Plan.id)-$($Task.id)-$Attempt$suffix"
    if(Test-Path $job){throw 'Existing dispatch path; never launch twice'}
    $prompt=$Task.promptFile
    if($Resume) {
        $prompt="$box/repair-$($Task.id)-$Attempt$suffix.md"
        $latest=Workflow-Latest $Plan $Task
        $reason=if($Format){'上轮正常退出，但delivery.invalid明确拒收格式。只补schema，不擅自改验收条件；有本任务未提交改动则仅核对并提交允许文件。不能伪造子代理证据。'}else{'先读原运行review.json中的明确rework findings，按原验收条件修复。'}
        $schema=@{schemaVersion=1;type='delivery.card';taskId=$Task.id;status='module_ready';summary='真实事实';commits=@();changedFiles=@();verification=@{passed=@();failed=@();notRun=@()};evidence=@();integrationNeeds=@();blockers=@();writerStopped=$true}|ConvertTo-Json -Depth 10 -Compress
        Set-Content $prompt "原任务$($Task.id)恢复｜原模型$($Task.model)｜原worktree/原保存会话，不换模型。原运行$($latest.directory)。$reason 不扩大允许路径$($Task.allowedPaths -join ',')，不接管main，不push，不登录，不删除。任务原契约文件$($Task.promptFile)。本轮commits只列新提交，之前SHA及原子代理结果放evidence。最终仅纯JSON，schema为$schema，所有数组字段必须数组。"
    }
    $claim="$box/launch-$($Task.id)-$Attempt$suffix.json"
    if(Test-Path $claim){throw 'Ambiguous prior launch; no automatic resend'}
    Save-AgentJson $claim @{status='dispatching';job=$job;taskId=$Task.id;model=$Task.model;resumeSession=$Resume;started=[DateTime]::UtcNow.ToString('o')}
    $arguments=@{Mode='Launch';Repository=$Repository;JobDirectory=$job;Worktree=$Task.worktree;PromptFile=$prompt;TaskId=$Task.id;Model=$Task.model;Harness=$Task.harness;AllowedPaths=[string[]]$Task.allowedPaths;WorkflowId=$Plan.id;Attempt=$Attempt;FormatOnly=$Format;MechanismTest=[bool]$Plan.mechanismTest;CodexExecutable=$Plan.codexExecutable;OmpExecutable=$Plan.ompExecutable;TimeoutSeconds=$Task.timeoutSeconds;InheritTaskChanges=$Inherit}
    if($Resume){$arguments.ResumeSession=$Resume}
    & "$PSScriptRoot/agent-executor.ps1" @arguments
    # Calling a script that uses exit is isolated by its own script scope, not another harness.
    Save-AgentJson $claim @{status='launched';job=$job;taskId=$Task.id;model=$Task.model;resumeSession=$Resume;finished=[DateTime]::UtcNow.ToString('o')}
    Workflow-Log 'task.launched' @{task=$Task.id;attempt=$Attempt;format=$Format;job=$job;harness=$Task.harness;model=$Task.model}
}
function Workflow-ResumeIdentity($Run) {
    $m=$Run.manifest
    if($m.harness -eq 'omp') {
        $sessions=@(Get-ChildItem "$($Run.directory)/sessions" -Filter '*.jsonl')
        if($sessions.Count -eq 0 -and $m.resumeSession){return $m.resumeSession}
        if($sessions.Count -ne 1){throw 'Ambiguous saved OMP lead session'};return $sessions[0].FullName
    }
    $thread=$null;$terminal=$null
    foreach($line in [IO.File]::ReadLines("$($Run.directory)/stdout.log")){$e=$line|ConvertFrom-Json;if($e.type -eq 'thread.started'){$thread=$e.thread_id};if($e.type -in @('turn.completed','turn.failed')){$terminal=$e.type}}
    if(-not $thread -or $terminal -ne 'turn.completed'){throw 'Codex delivery transport uncertain; never resend'};return $thread
}
function Workflow-Protected($Allowed) {
    $paths=@(((Workflow-Git $Repository @('diff','--name-only'))+"`n"+(Workflow-Git $Repository @('diff','--cached','--name-only'))+"`n"+(Workflow-Git $Repository @('ls-files','--others','--exclude-standard'))) -split "`n"|Where-Object {$_}|Select-Object -Unique)
    $snapshot=@{}
    foreach($path in $paths){if(-not @($Allowed|Where-Object {$path -like $_}).Count){$absolute=Join-Path $Repository $path;$snapshot[$path]=if(Test-Path -LiteralPath $absolute){(Get-FileHash -LiteralPath $absolute).Hash}else{'missing'}}}
    return $snapshot
}
function Workflow-Integrate($Plan,$Task,$Run,[switch]$Reconcile) {
    if(Workflow-Revoked $Task){return $false}
    $m=$Run.manifest;$path="$box/integration-$($Task.id)-$($m.runId).json"
    $prior=$null
    if(Test-Path $path){$prior=Workflow-Read $path;if($prior.status -eq 'done'){return $true};if(-not $Reconcile){throw 'Integration outcome unresolved; no retry'}}
    if($Reconcile -and (-not $prior -or $prior.status -ne 'inflight')){throw 'No ambiguous integration to reconcile'}
    $lease=$null;try{$lease=[IO.File]::Open("$Repository/tmp/agent-gate/main-writer.lock",'OpenOrCreate','ReadWrite','None')}catch [IO.IOException]{return $false}
    $humanLease=$null
    try {
        $humanLease=Open-HumanLease $Repository
        $r=Workflow-Read "$($Run.directory)/review.json";$state=Workflow-Read "$($Run.directory)/state.json"
        $human=Get-HumanRequirements $Repository $Task.id
        if($human.revision -gt 0 -and (-not $human.ready -or $r.requirementRevision -ne $human.revision)){return $false}
        $binding=Read-AgentManager $Repository $Plan.managerModel
        if($r.decision -cne 'accept' -or $r.reviewedHead -cne $state.head -or (Workflow-Git $m.worktree @('rev-parse','HEAD')) -cne $state.head -or $state.dirty){throw 'Integration preflight identity/dirty mismatch'}
        foreach($role in @('worker','agent')){if(Test-AgentProcessIdentity $state $role){throw 'Writer still active'}}
        $initial=Workflow-Read "$box/task-$($Task.id).json"
        $changed=@((Workflow-Git $m.worktree @('diff','--name-only',$initial.base,$state.head)) -split "`n"|Where-Object {$_})
        foreach($file in $changed){if(-not @($Task.allowedPaths|Where-Object {$file -like $_}).Count){throw "Source exceeds task scope: $file"}}
        $allowed=@($Task.allowedPaths)+@($Task.integrationPaths)
        if(-not $Reconcile -and (Workflow-AllowedDirty $allowed)){throw 'Main already has task-scope user edits; refuse to overwrite or inherit'}
        $protected=if($Reconcile){$prior.protected}else{Workflow-Protected $allowed}
        $indexHash=if($Reconcile){$prior.indexHash}else{Workflow-IndexHash}
        $base=if($Reconcile){$prior.mainBase}else{Workflow-Git $Repository @('rev-parse','HEAD')}
        if(-not $Reconcile){Save-AgentJson $path @{status='inflight';task=$Task.id;runId=$m.runId;reviewedHead=$state.head;mainBase=$base;protected=$protected;indexHash=$indexHash;started=[DateTime]::UtcNow.ToString('o')}}
        $contract=Get-Content -LiteralPath $Task.integrationPromptFile -Raw
        $prompt="固定总控M收到真实integration.request事件，任务$($Task.id)，明确授权共享接线及main整合。先读AGENTS.md与执行协议，严格检查运行$($Run.directory)的accept/真实HEAD/无活跃writer与范围。源码源HEAD=$($state.head)，原始起点=$($initial.base)。主区开始HEAD=$base。只允许提交$($allowed -join ', ')。保护全部继承未提交内容，禁止git add .、reset/clean、push、登录、删除worktree。使用git merge --no-ff真实来源HEAD，随后实现必要公共接线，只git add明确本任务文件并小步提交。外部管理器已经持有main-writer lease，本模型不再获取同锁。必须运行批准的验收脚本$($Task.verificationFile)，不放宽断言。接受≠产品整项验收，不伪造G1/G2门。$contract 最终给真实mainSHA与证据，失败直说，不偷偷标完成。"
        if($Reconcile) {
            $thread=$null;$terminal=$null
            foreach($line in [IO.File]::ReadLines("$Repository/tmp/agent-gate/integration-$($m.runId).stdout.jsonl")){$entry=$line|ConvertFrom-Json;if($entry.type -eq 'thread.started'){$thread=$entry.thread_id};if($entry.type -in @('turn.completed','turn.failed')){$terminal=$entry.type}}
            if($thread -cne $binding.threadId -or $terminal -ne 'turn.completed'){throw 'No matching completed integration transport; keep frozen'}
            $turn=@{threadId=$thread;completed=$true}
        }else {
            $turn=Invoke-AgentManagerTurn -Repository $Repository -Model $Plan.managerModel -CodexExecutable $Plan.codexExecutable -RunId "integration-$($m.runId)" -Prompt $prompt -TimeoutSeconds 1200 -Integration
            if(-not $turn.completed){throw 'Integration model failed; retain uncertain claim'}
        }
        [void](Workflow-Git $Repository @('merge-base','--is-ancestor',$state.head,'HEAD'))
        foreach($file in ((Workflow-Git $Repository @('diff','--name-only',$base,'HEAD')) -split "`n"|Where-Object {$_})){if(-not @($allowed|Where-Object {$file -like $_}).Count){throw "Integration exceeded scope: $file"}}
        $after=Workflow-Protected $allowed
        foreach($file in $protected.Keys){if($after[$file] -cne $protected[$file]){throw "Inherited file changed: $file"}}
        foreach($file in $after.Keys){if(-not $protected.ContainsKey($file)){throw "Unexpected unrelated dirty file: $file"}}
        if(-not $indexHash -or (Workflow-IndexHash) -cne $indexHash){throw 'Inherited index staging changed; keep integration unresolved'}
        if(Workflow-AllowedDirty $allowed){throw 'Task-scope edits remain uncommitted; accept is not integration'}
        $verifyOut=& pwsh.exe -NoProfile -File $Task.verificationFile -Repository $Repository
        $code=$LASTEXITCODE;Set-Content "$box/verify-$($Task.id)-$($m.runId).log" ($verifyOut -join "`n")
        if($code){throw 'Independent integration verification failed'}
        $sha=Workflow-Git $Repository @('rev-parse','HEAD')
        if($Reconcile){Save-AgentJson "$box/recovery-integration-$($m.runId).json" @{prior=$prior;reconciledAt=[DateTime]::UtcNow.ToString('o');threadId=$turn.threadId;modelCalls=0}}
        Save-AgentJson $path @{status='done';task=$Task.id;runId=$m.runId;reviewedHead=$state.head;mainBase=$base;mainSha=$sha;threadId=$turn.threadId;reconciled=[bool]$Reconcile;finished=[DateTime]::UtcNow.ToString('o');evidence="$box/verify-$($Task.id)-$($m.runId).log"}
        Save-AgentJson "$box/done-$($Task.id).json" @{task=$Task.id;mainSha=$sha;integration=$path;runId=$m.runId}
        Workflow-Log 'integration.completed' @{task=$Task.id;mainSha=$sha;runId=$m.runId}
        return $true
    }finally{if($humanLease){$humanLease.Dispose()};$lease.Dispose()}
}
if($Mode -eq 'Reconcile') {
    $plan=Workflow-Plan;$tasks=@($plan.tasks|Where-Object id -ceq $TaskId)
    if($tasks.Count -ne 1){throw 'Explicit unique approved TaskId required'}
    $run=Workflow-Latest $plan $tasks[0]
    if(-not (Workflow-Integrate $plan $tasks[0] $run -Reconcile)){throw 'Writer busy; do not reconcile'}
    Write-Output 'Reconciled original completed integration with independent scope/protection/verification; zero model calls.';exit
}
$lock=[IO.File]::Open("$box/workflow.lock",'OpenOrCreate','ReadWrite','None')
$watcher=New-AgentFileWatch (Join-Path $Repository 'tmp') "FufucodeFlow-$PID"
$runtime=@{pid=$PID;status='running';started=[DateTime]::UtcNow.ToString('o');repository=$Repository}
Save-AgentJson "$box/runtime.json" $runtime
try {
    do {
        if(Test-Path "$box/stop.json"){$runtime.status='stopped';Save-AgentJson "$box/runtime.json" $runtime;break}
        $plan=Workflow-Plan;Validate-WorkflowPlan $plan
        $approval=Workflow-Read "$box/approval.json"
        foreach($contract in $approval.contracts.Keys){if((Get-FileHash -LiteralPath $contract).Hash -cne $approval.contracts[$contract]){throw 'Approved contract changed; stop instead of guessing'}}
        if(-not (Test-Path "$box/paused")) {
            $active=0
            foreach($task in $plan.tasks){$run=Workflow-Latest $plan $task;if($run){$s=Workflow-Read "$($run.directory)/state.json";if($s.status -in @('starting','running')){
                $workerId=$s.workerPid
                if(-not $workerId -and (Test-Path "$($run.directory)/worker-binding.json")){$workerId=(Workflow-Read "$($run.directory)/worker-binding.json").pid}
                if($workerId -and (Test-AgentProcessIdentity @{workerPid=$workerId;workerStarted=$s.workerStarted;finished=$s.finished} 'worker') -and (Watch-AgentProcessExit $watcher $workerId)){$active++}
                else {
                    # Re-read after Exited: normal final state is written before worker exit.
                    $s=Workflow-Read "$($run.directory)/state.json"
                    if($s.status -in @('starting','running')){
                        Save-AgentJson "$($run.directory)/orphaned-state.json" $s
                        $s.status='orphaned_worker';$s.error='Managed worker stopped or launch identity unknown; no automatic resend';$s.detected=[DateTime]::UtcNow.ToString('o')
                        Save-AgentJson "$($run.directory)/state.json" $s
                        Write-AgentEvent $run.directory $run.manifest 'supervisor.failed' @{message=$s.error;automaticRetry=$false}
                        Workflow-Log 'worker.orphaned' @{task=$task.id;runId=$run.manifest.runId;workerPid=$workerId}
                    }
                }
            }}}
            foreach($task in $plan.tasks) {
              try {
                if(Workflow-Revoked $task){continue}
                if(Test-Path "$box/blocked-$($task.id).json"){continue}
                if(Test-Path "$box/done-$($task.id).json"){continue}
                if(@($task.gates|Where-Object {$plan.gates[$_] -cne 'passed'}).Count){continue}
                if(@($task.dependsOn|Where-Object {$_ -and -not (Test-Path "$box/done-$_.json")}).Count){continue}
                $run=Workflow-Latest $plan $task
                if(-not $run){if($active -lt 2 -and (Workflow-CanLaunch $plan $task)){Workflow-Launch $plan $task;$active++};continue}
                $m=$run.manifest;$s=Workflow-Read "$($run.directory)/state.json"
                if($s.status -in @('starting','running')){continue}
                if($s.status -eq 'orphaned_worker'){continue}
                if($s.status -eq 'supervisor_error'){continue} # Isolated task freezes; unrelated tasks can continue.
                if($s.status -eq 'failed_or_missing_card') {
                    $invalid=Workflow-Read "$($run.directory)/events/04.json"
                    if($m.formatOnly -or $s.exitCode -ne 0 -or -not $s.finished -or $invalid.type -ne 'delivery.invalid' -or (Workflow-Git $m.worktree @('rev-parse','HEAD')) -cne $s.head){
                        $park="$box/blocked-$($task.id).json"
                        if(-not (Test-Path $park)){Save-AgentJson $park @{task=$task.id;runId=$m.runId;status='worker_failure';automaticRetry=$false;reason='Not a safely repairable card format event';at=[DateTime]::UtcNow.ToString('o')};Workflow-Log 'task.parked' @{task=$task.id;runId=$m.runId}}
                        continue
                    }
                    $liveWriter=$false;foreach($role in @('worker','agent')){if(Test-AgentProcessIdentity $s $role){$liveWriter=$true}};if($liveWriter){continue}
                    $resume=Workflow-ResumeIdentity $run
                    if($active -ge 2 -or -not (Workflow-CanLaunch $plan $task)){continue}
                    Workflow-Launch $plan $task $m.attempt $resume $true ([bool]$s.dirty);$active++;continue
                }
                if(-not (Test-Path "$($run.directory)/events/05.json")){continue}
                $r=Workflow-Read "$($run.directory)/review.json";$claim="$Repository/tmp/agent-gate/$($m.runId).dispatch.json"
                if(-not (Test-Path $claim) -or (Workflow-Read $claim).status -ne 'decided'){continue}
                $binding=Workflow-Read "$Repository/tmp/agent-gate/manager.json";if($binding.status -ne 'ready'){continue}
                if($r.decision -eq 'rework') {
                    if($m.attempt -ge 3){throw 'Bounded repair attempts exhausted; do not pretend acceptance'}
                    if($active -ge 2 -or -not (Workflow-CanLaunch $plan $task)){continue}
                    Workflow-Launch $plan $task ($m.attempt+1) (Workflow-ResumeIdentity $run);$active++;continue
                }
                if($r.decision -eq 'accept' -and @($task.integrationDependsOn|Where-Object {$_ -and -not (Test-Path "$box/done-$_.json")}).Count -eq 0){[void](Workflow-Integrate $plan $task $run)}
                # blocked/defer remain parked; independent tasks continue.
              }catch{
                Save-AgentJson "$box/blocked-$($task.id).json" @{task=$task.id;status='needs_attention';automaticRetry=$false;error=$_.Exception.Message;at=[DateTime]::UtcNow.ToString('o')}
                Workflow-Log 'task.failed' @{task=$task.id;error=$_.Exception.Message}
              }
            }
        }
        $complete=@($plan.tasks|Where-Object {-not (Workflow-Revoked $_) -and -not (Test-Path "$box/done-$($_.id).json")}).Count -eq 0
        $unplanned=@($plan.projectBacklog|Where-Object {$_.status -eq 'unplanned'}).Count
        $projectOpen=@($plan.projectBacklog|Where-Object {$_ -and $_.status -notin @('completed','scheduled')}).Count
        $nextStatus=if(Test-Path "$box/paused"){'paused'}elseif($unplanned){'planning_required'}elseif($complete -and $projectOpen){'waiting_project_gates'}elseif($complete){'idle_complete'}else{'waiting_events'}
        $runtime.unplanned=$unplanned
        $runtime.projectOpen=$projectOpen
        if($runtime.status -ne $nextStatus -or $runtime.plan -ne $plan.id){$runtime.status=$nextStatus;$runtime.plan=$plan.id;$runtime.updated=[DateTime]::UtcNow.ToString('o');Save-AgentJson "$box/runtime.json" $runtime}
        [void](Wait-AgentFileWatch $watcher)
    }while($true)
}catch{$runtime.status='needs_attention';$runtime.error=$_.Exception.Message;Save-AgentJson "$box/runtime.json" $runtime;Workflow-Log 'workflow.failed' $runtime}
finally{Close-AgentFileWatch $watcher;$lock.Dispose()}
