[CmdletBinding()]
param([ValidateSet('Initialize','Status','Reconcile','RecoverFailed')][string]$Mode='Status',
    [string]$Repository=(Split-Path $PSScriptRoot -Parent),[string]$Model,[string]$CodexExecutable,
    [int]$TimeoutSeconds=300,[string]$SessionLog)
$ErrorActionPreference='Stop'
$Repository=(Resolve-Path -LiteralPath $Repository).Path
$outbox=Join-Path $Repository 'tmp/agent-gate'
[IO.Directory]::CreateDirectory($outbox)|Out-Null
. (Join-Path $PSScriptRoot 'agent-events.ps1')
. (Join-Path $PSScriptRoot 'agent-manager.ps1')
if($Mode -eq 'Status') {
    $path=Join-Path $outbox 'manager.json'
    if(Test-Path -LiteralPath $path){Get-Content -LiteralPath $path -Raw}else{@{status='not_initialized'}|ConvertTo-Json}
    exit
}
if($Mode -in @('Reconcile','RecoverFailed')) {
    if(-not $SessionLog){throw 'Explicit original saved session log required; never choose --last'}
    $mainLock=[IO.File]::Open((Join-Path $outbox 'main-writer.lock'),'OpenOrCreate','ReadWrite','None')
    $managerLock=[IO.File]::Open((Join-Path $outbox 'manager.lock'),'OpenOrCreate','ReadWrite','None')
    try {
        $binding=Read-AgentJson (Join-Path $outbox 'manager.json')
        if($binding.status -notin @('inflight','needs_attention','ready')){throw 'No completed or unresolved manager turn to reconcile'}
        if(Test-AgentProcessIdentity @{agentPid=$binding.processId;agentStarted=$binding.processStarted} 'agent'){throw 'Recorded reviewer still present; do not reconcile live writer'}
        $meta=$null;$started=$null;$terminal=$null;$promptMatches=$false;$hasEffects=$false
        foreach($line in [IO.File]::ReadLines((Resolve-Path $SessionLog).Path)) {
            $entry=$line|ConvertFrom-Json -AsHashtable
            if($entry.type -eq 'session_meta'){$meta=$entry.payload}
            if([DateTime]$entry.timestamp -lt [DateTime]$binding.lastStarted){continue}
            if($entry.type -eq 'response_item' -and ($entry.payload.type -in @('function_call','function_call_output','custom_tool_call','custom_tool_call_output') -or
                ($entry.payload.type -eq 'message' -and $entry.payload.role -ne 'user'))){$hasEffects=$true}
            if($entry.type -eq 'event_msg' -and $entry.payload.type -match 'tool|exec|patch'){$hasEffects=$true}
            if($entry.type -eq 'response_item' -and $entry.payload.type -eq 'message' -and $entry.payload.role -eq 'user') {
                foreach($part in $entry.payload.content){if($part.text -and $part.text.Contains($binding.lastRunId)){$promptMatches=$true}}
            }
            if($entry.type -eq 'event_msg' -and $entry.payload.type -eq 'task_started'){$started=$entry.payload.turn_id;$terminal=$null}
            if($entry.type -eq 'event_msg' -and $entry.payload.type -in @('task_complete','turn_aborted','task_failed') -and $entry.payload.turn_id -ceq $started){$terminal=$entry.payload.type}
        }
        if($meta.id -cne $binding.threadId -or $meta.cwd -ne $Repository -or -not $promptMatches -or -not $started){throw 'Original session lacks matching identity/request; keep frozen'}
        if($Mode -eq 'RecoverFailed') {
            # Explicit operator recovery only. No generic retry for ambiguous transport failures.
            $failedPrefix=Join-Path $outbox $binding.lastRunId
            if($binding.status -ne 'needs_attention' -or $binding.exitCode -ne -1073740791 -or $terminal -or $hasEffects -or
                -not ([IO.File]::ReadAllText($failedPrefix+'.stderr.log').Contains('memory allocation of 1073741824 bytes failed'))){throw 'Not an evidenced pre-execution native allocation failure; keep frozen'}
            foreach($line in [IO.File]::ReadLines($failedPrefix+'.stdout.jsonl')) {
                $output=$line|ConvertFrom-Json -AsHashtable
                if($output.type -notin @('thread.started','turn.started') -or
                    ($output.type -eq 'thread.started' -and $output.thread_id -cne $binding.threadId)){throw 'Failed transport contains execution/output; keep frozen'}
            }
        }elseif(-not $terminal){throw 'Original session lacks matching terminal; keep frozen'}
        $run=$binding.lastRunId;$claimPath=Join-Path $outbox ($run+'.dispatch.json');$claim=Read-AgentJson $claimPath
        if($claim.status -notin @('dispatching','needs_attention')){throw 'Not a matching unresolved review claim; do not repair an integration by guessing'}
        $request=Read-AgentJson (Join-Path $outbox ($run+'.request.json'));$state=Read-AgentJson (Join-Path $request.jobDirectory 'state.json')
        $manifest=Read-AgentJson (Join-Path $request.jobDirectory 'manifest.json')
        if($request.runId -cne $claim.runId -or $manifest.runId -cne $request.runId -or $manifest.task -cne $request.taskId -or
            $state.status -cne 'completed_pending_review' -or -not $state.finished){throw 'Request/state identity changed; keep frozen'}
        foreach($role in @('worker','agent')){if(Test-AgentProcessIdentity $state $role){throw 'Worker still active'}}
        $actual=& git.exe -C $manifest.worktree rev-parse HEAD;if($LASTEXITCODE -or $actual.Trim() -cne $request.reviewedHead -or $state.head -cne $request.reviewedHead){throw 'HEAD changed; keep frozen'}
        $eventPath=Join-Path $request.jobDirectory 'events/05.json'
        if($request.requirementRevision -gt 0){$eventPath=Join-Path $request.jobDirectory "events/05-r$($request.requirementRevision).json"}
        $hasDecision=Test-Path $eventPath
        if($hasDecision) {
            $event=Read-AgentJson $eventPath
            $review=Read-AgentJson (Join-Path $request.jobDirectory 'review.json')
            if($event.type -cne 'review.decided' -or $event.runId -cne $request.runId -or $event.taskId -cne $request.taskId -or
                $event.payload.reviewedHead -cne $request.reviewedHead -or $event.payload.decision -cnotin @('accept','rework','blocked','defer') -or
                $review.type -cne 'review.decision' -or $review.runId -cne $request.runId -or $review.taskId -cne $request.taskId -or
                $review.reviewedHead -cne $request.reviewedHead -or $review.decision -cne $event.payload.decision){throw 'Conflicting decision; keep frozen'}
        }elseif($Mode -ne 'RecoverFailed' -and ($claim.status -ne 'dispatching' -or $binding.status -eq 'ready' -or $terminal -notin @('turn_aborted','task_failed'))){throw 'Completed turn without decision; inspect manually, do not resend'}
        if($Mode -eq 'RecoverFailed' -and ($hasDecision -or $claim.status -ne 'needs_attention' -or $claim.reviewAttempt -ne 0 -or
            $claim.exitCode -ne $binding.exitCode -or $claim.reviewerThreadId -cne $binding.threadId)){throw 'Failure claim mismatch or recovery already attempted; keep frozen'}
        $recovery=Join-Path $outbox ('recovery-'+$run+'-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff')+'.json')
        Save-AgentJson $recovery @{schemaVersion=1;mode=$Mode;binding=$binding;claim=$claim;sessionLog=$SessionLog;turnId=$started;terminal=$terminal;hasDecision=$hasDecision;reconciledAt=[DateTime]::UtcNow.ToString('o')}
        $claim.status=if($hasDecision){'decided'}else{'resume_authorized'}
        $claim.reviewerThreadId=$binding.threadId;$claim.recovery=$recovery;$claim.reviewAttempt=1
        Save-AgentJson $claimPath $claim
        $binding.status='ready';$binding.recovery=$recovery;Save-AgentJson (Join-Path $outbox 'manager.json') $binding
        Write-Output "Reconciled $run from original terminal=$terminal; decisionExists=$hasDecision; no model call."
    }finally{$managerLock.Dispose();$mainLock.Dispose()}
    exit
}
if(-not $Model -or -not $CodexExecutable -or [IO.Path]::GetExtension($CodexExecutable) -ne '.exe'){throw 'Explicit model and native executable required'}
$lease=[IO.File]::Open((Join-Path $outbox 'main-writer.lock'),'OpenOrCreate','ReadWrite','None')
try {
    $prompt="你是Fufucode的独立固定总控M线程，模型$Model。此为会话建立与职责交接，不做模型能力试跑。你以后由本地事件门房以本线程ID恢复，空闲没有请求。先只阅读本仓库AGENTS.md、docs/tasks/unattended-development.md、docs/tasks/agent-event-protocol.md、docs/tasks/execution-contract.md（存在才读）；存在acceptance.md则为隔离机制夹具。当前启用的是审核职责，收到review.request后必须独立核对并通过agent-queue Review入口落盘。不从交付正文猜accept；module_ready有效。共享集成/新派工仍为独立open职责，不能自行越当前门房审核权限。保持worker原模型/原worktree/原保存会话。测试必须显式devin/swe-2。不修改源码、不发起测试、不派任务、不接管其他会话、不push、不登录、不清理。只需最终回复固定M已接任、目前等待交付事件及职责限制。"
    $prompt+=" 本轮没有review.request授权。禁止枚举tmp队列、读取交付卡、生成审核或调用Review入口。即使发现积压也必须等待下一轮事件请求。本轮仅建立身份与阅读上述协议。"
    $result=Invoke-AgentManagerTurn -Repository $Repository -Model $Model -CodexExecutable $CodexExecutable -RunId 'manager-bootstrap' -Prompt $prompt -TimeoutSeconds $TimeoutSeconds -Initialize
    if(-not $result.completed){throw 'Manager initialization did not complete; inspect persistent binding and logs'}
    $result|ConvertTo-Json
} finally {$lease.Dispose()}
