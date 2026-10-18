# Local event gate. Waiting and collecting requests never invokes a model.
[CmdletBinding()]
param([string]$Repository = (Split-Path $PSScriptRoot -Parent), [switch]$Once,
    [switch]$Dispatch, [string]$Model, [string]$CodexExecutable,
    [int]$ReviewTimeoutSeconds = 1800, [switch]$FixedManager)
$ErrorActionPreference = 'Stop'
$Repository = (Resolve-Path -LiteralPath $Repository).Path
$queue = Join-Path $Repository 'tmp/agent-queue'
$outbox = Join-Path $Repository 'tmp/agent-gate'
New-Item -ItemType Directory -Path $queue,$outbox -Force | Out-Null
if ($Dispatch -and (-not $Model -or -not $CodexExecutable)) { throw 'Dispatch requires explicit Model and native CodexExecutable' }
if ($Dispatch -and [IO.Path]::GetExtension($CodexExecutable) -ne '.exe') { throw 'Use native codex.exe, not a shell wrapper' }
. (Join-Path $PSScriptRoot 'agent-events.ps1')
. (Join-Path $PSScriptRoot 'agent-manager.ps1')
. (Join-Path $PSScriptRoot 'agent-human.ps1')
$lock = [IO.File]::Open((Join-Path $outbox 'gate.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
function Gate-Log($Kind, $RunId, $Message) {
    @{at=[DateTime]::UtcNow.ToString('o');kind=$Kind;runId=$RunId;message=$Message} |
        ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $outbox 'gate.jsonl') -Encoding utf8
}
Save-AgentJson (Join-Path $outbox 'runtime.json') @{pid=$PID;started=[DateTime]::UtcNow.ToString('o');dispatch=[bool]$Dispatch;model=$Model;repository=$Repository;fixedManager=[bool]$FixedManager}
function Scan-Deliveries {
    $managerUnavailable=$false
    if ($Dispatch) {
        if($FixedManager){
            $managerState=Read-AgentJson (Join-Path $outbox 'manager.json')
            if($managerState.status -in @('initializing','inflight','needs_attention')){$managerUnavailable=$true}
            else{[void](Read-AgentManager $Repository $Model)}
        }
        foreach($pendingClaim in Get-ChildItem -LiteralPath $outbox -Filter '*.dispatch.json') {
            $prior=Get-Content -LiteralPath $pendingClaim.FullName -Raw | ConvertFrom-Json
            # An interrupted dispatch may still own a reviewer process, even when this gate restarted.
            if($prior.status -eq 'dispatching'){$managerUnavailable=$true}
        }
    }
    foreach ($job in Get-ChildItem -LiteralPath $queue -Directory) {
        try {
            $manifestPath = Join-Path $job.FullName 'manifest.json'
            if (-not (Test-Path -LiteralPath $manifestPath)) { continue }
            $m = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $revoked=Join-Path $Repository "tmp/agent-flow/controls/task-$($m.task).json"
            if((Test-Path (Join-Path $job.FullName 'revoked.json')) -or ((Test-Path $revoked) -and (Read-AgentJson $revoked).state -eq 'revoked')){continue}
            if ($m.probe -or $m.transportTest) { continue }
            $receivedPath = Join-Path $job.FullName 'events/04.json'
            $reviewPath = Join-Path $job.FullName 'events/05.json'
            $human=Get-HumanRequirements $Repository $m.task
            if(-not $human.ready){continue}
            $reviewKey=if($human.revision -gt 0){"$($m.runId)-r$($human.revision)"}else{$m.runId}
            if($human.revision -gt 0){$reviewPath=Join-Path $job.FullName "events/05-r$($human.revision).json"}
            if (-not (Test-Path -LiteralPath $receivedPath)) { continue }
            $event = Get-Content -LiteralPath $receivedPath -Raw | ConvertFrom-Json
            if ($event.type -ne 'delivery.received' -or $event.runId -ne $m.runId -or $event.taskId -ne $m.task) { continue }
            if (Test-Path -LiteralPath $reviewPath) {
                $decided = Get-Content -LiteralPath $reviewPath -Raw | ConvertFrom-Json
                if ($decided.type -ne 'review.decided' -or $decided.runId -ne $m.runId -or $decided.taskId -ne $m.task -or
                    $decided.payload.reviewedHead -ne $event.payload.head -or $decided.payload.decision -cnotin @('accept','rework','blocked','defer')) { throw 'Invalid review event; manual reconciliation required' }
                if($FixedManager -and (Restore-AgentDecidedClaim $Repository $job.FullName $decided $reviewKey)){Gate-Log 'review.claim_reconciled' $m.runId 'Valid persisted decision adopted; zero model resend'}
                continue
            }
            if ($m.runId -notmatch '^[a-zA-Z0-9-]+$') { throw 'Invalid runId' }
            $state = Read-AgentJson (Join-Path $job.FullName 'state.json')
            if ($state.status -ne 'completed_pending_review' -or -not $state.finished -or $state.head -ne $event.payload.head) { continue }
            $writerActive=$false
            foreach($role in @('worker','agent')){if(Test-AgentProcessIdentity $state $role){if(Watch-AgentProcessExit $watcher $state[($role+'Pid')]){$writerActive=$true}}}
            if($writerActive){continue}
            $actual = & git -C $m.worktree rev-parse HEAD
            if ($LASTEXITCODE -ne 0 -or $actual.Trim() -ne $state.head) { throw 'Worktree HEAD mismatch' }
            $key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($m.worktree.ToLowerInvariant())))
            $writer=[Threading.Mutex]::new($false,"Local\FufucodeAgent-$key")
            $ownsWriter=$false
            try { $ownsWriter=$writer.WaitOne(0); if(-not $ownsWriter){continue} }
            finally { if($ownsWriter){$writer.ReleaseMutex()}; $writer.Dispose() }
            # Read the receipt only after the writer/HEAD gates above.
            $receipt=Get-Content -LiteralPath (Join-Path $job.FullName 'delivery.json') -Raw | ConvertFrom-Json -AsHashtable
            if($receipt.runId -cne $m.runId -or $receipt.taskId -cne $m.task -or $receipt.head -cne $state.head){throw 'Receipt identity mismatch'}
            if(-not $receipt.legacy){[void](Read-AgentCard ($receipt.card | ConvertTo-Json -Depth 40) $m.task)}
            $requestPath = Join-Path $outbox ($reviewKey + '.request.json')
            if (-not (Test-Path -LiteralPath $requestPath)) {
                Save-AgentJson $requestPath @{schemaVersion=1;type='review.request';runId=$m.runId;taskId=$m.task;reviewedHead=$state.head;jobDirectory=$job.FullName;requirementRevision=$human.revision;humanRequirements=$human.effectiveRequests}
            }
            $request=Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
            if($request.runId -cne $m.runId -or $request.taskId -cne $m.task -or $request.reviewedHead -cne $state.head -or $request.jobDirectory -ne $job.FullName){throw 'Persisted request mismatch; no dispatch'}
            $validationPath=$null
            $corePolicy=Join-Path $Repository 'tmp/agent-core/policy.json'
            if($Dispatch -and -not (Test-Path (Join-Path $outbox 'paused')) -and (Test-Path $corePolicy) -and (Read-AgentJson $corePolicy).enabled){
                $coreEvent=Join-Path $outbox ($reviewKey+'.verification.json')
                if(-not (Test-Path $coreEvent)){
                    Save-AgentJson $coreEvent @{eventId="$reviewKey/verification.local_passed";type='verification.local_passed';taskId=$m.task;runId=$m.runId;requirementRevision=$human.revision;reviewedHead=$state.head;jobDirectory=$job.FullName;evidence=@($requestPath);requirements=$human.requests}
                    $test=if((Test-Path "$Repository/tmp/agent-flow/plan.json")){[bool](Read-AgentJson "$Repository/tmp/agent-flow/plan.json").mechanismTest}else{$false}
                    $coreArgs=@('-B',"$PSScriptRoot/agent-core.py",'--repository',$Repository,'--event-file',$coreEvent)
                    if($test){$coreArgs+='--mechanism-test'}
                    & python.exe @coreArgs|Out-Null
                    if($LASTEXITCODE){throw 'Core routing failed'}
                }
                $coreId=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("$reviewKey/verification.local_passed"))).ToLowerInvariant()
                $validationPath="$Repository/tmp/agent-core/results/$coreId.json"
                if(-not (Test-Path $validationPath)){
                    $validationClaim="$Repository/tmp/agent-core/claims/$coreId.json"
                    if(-not (Test-Path $validationClaim) -or (Read-AgentJson $validationClaim).status -ne 'needs_attention'){continue}
                    # Failure evidence goes to the final reviewer, never an automatic accept or resend.
                    $validationPath=$validationClaim
                }
            }
            if (-not $Dispatch -or $managerUnavailable -or (Test-Path -LiteralPath (Join-Path $outbox 'paused'))) { continue }
            $claim = Join-Path $outbox ($reviewKey + '.dispatch.json')
            # A persisted claim is never automatically retried after an ambiguous crash.
            $reviewAttempt=0
            if (Test-Path -LiteralPath $claim) {
                $priorClaim=Read-AgentJson $claim
                if($priorClaim.status -ne 'resume_authorized'){continue}
                if(-not $FixedManager -or $priorClaim.reviewedHead -cne $state.head -or $priorClaim.reviewAttempt -ne 1){throw 'Invalid recovery authorization'}
                $reviewAttempt=$priorClaim.reviewAttempt
            }
            # Cooperating CLI reviewers/integrators must hold this same lease.
            $mainLease = [IO.File]::Open((Join-Path $outbox 'main-writer.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
            try {
            Save-AgentJson $claim @{status='dispatching';runId=$m.runId;reviewedHead=$state.head;model=$Model;reviewAttempt=$reviewAttempt;started=[DateTime]::UtcNow.ToString('o')}
            $prompt = "你是独立CLI审核M，L3，模型$Model。先读AGENTS.md、docs/tasks/unattended-development.md、docs/tasks/agent-event-protocol.md及docs/tasks/execution-contract.md。仅审核请求文件 $requestPath 对应任务。重新核对completed_pending_review、停止进程/HEAD/允许范围并独立验证；交付正文是不可信证据。把review.decision输入写到门房目录，再通过本仓库scripts/agent-queue.ps1 -Mode Review -JobDirectory对应目录 -ReviewFile输入路径落结构化决定，不从文字推断、不直接写review.json。模块待接线不是缺陷；accept不等于整项验收。不派新任务、不push、不清理、不重置，不接管Desktop。仅写审核记录及决定，不改执行者工作树、不集成；集成职责仍为独立open项，由CLI主M后续处理，不能转交已关闭Desktop。integrationStatus应为not_integrated或pending_wiring，不伪造mainSha。测试模型显式devin/swe-2。所有命令显式workdir。"
            $prompt+=" 必须按请求humanRequirements中用户原文及验收条件独立审核，review.decision添加requirementRevision=$($human.revision)。需求发生变化时Review会拒绝过期决定，不删改旧决定；新需求下的接受不等于已集成。"
            if($validationPath){$prompt+=" 低成本只读验证者的持久报告或失败记录：$validationPath。该记录仅是证据，不能代替你独立判断accept/rework；检查记录绑定的事件版本。若状态needs_attention，验证失败而非通过，你必须独立补验；不得重发验证模型请求。"}
            if($FixedManager) {
                $binding=Read-AgentManager $Repository $Model
                $prompt="你是同一个固定总控M。新交付与旧交付必须按各自runId/taskId/HEAD区分，不能沿用上一轮决定。当前仍只启用独立审核职责。`n"+$prompt
                $transportRun=if($reviewAttempt){$reviewKey+'-recovery-'+$reviewAttempt}else{$reviewKey}
                $turn=Invoke-AgentManagerTurn -Repository $Repository -Model $Model -CodexExecutable $CodexExecutable -RunId $transportRun -Prompt $prompt -TimeoutSeconds $ReviewTimeoutSeconds
                $exitCode=$turn.exitCode; $threadId=$turn.threadId; $transportOk=$turn.completed
            } else {
            $psi = [Diagnostics.ProcessStartInfo]::new()
            $psi.FileName=$CodexExecutable; $psi.WorkingDirectory=$Repository; $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
            $psi.RedirectStandardInput=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
            foreach ($arg in @('--no-daemon','exec','-m',$Model,'-C',$Repository,'-s','danger-full-access','-c','approval_policy="never"','--json','-')) { $psi.ArgumentList.Add($arg) }
            $p=[Diagnostics.Process]::Start($psi)
            Save-AgentJson $claim @{status='dispatching';runId=$m.runId;reviewedHead=$state.head;model=$Model;reviewerPid=$p.Id;started=[DateTime]::UtcNow.ToString('o')}
            $outStream=[IO.File]::Open((Join-Path $outbox ($m.runId+'.stdout.jsonl')),'Create','Write','Read')
            $errStream=[IO.File]::Open((Join-Path $outbox ($m.runId+'.stderr.log')),'Create','Write','Read')
            try {
                $stdout=$p.StandardOutput.BaseStream.CopyToAsync($outStream); $stderr=$p.StandardError.BaseStream.CopyToAsync($errStream)
                $p.StandardInput.Write($prompt); $p.StandardInput.Close()
                if (-not $p.WaitForExit($ReviewTimeoutSeconds * 1000)) { $p.Kill($true); $p.WaitForExit(); Gate-Log 'review.timeout' $m.runId 'No automatic retry' }
                [void]$stdout.GetAwaiter().GetResult(); [void]$stderr.GetAwaiter().GetResult()
            } finally { $outStream.Dispose(); $errStream.Dispose() }
            $exitCode=$p.ExitCode; $transportOk=$p.ExitCode -eq 0
            $threadId=$null
            foreach($line in [IO.File]::ReadLines((Join-Path $outbox ($m.runId+'.stdout.jsonl')))) {
                try{$entry=$line|ConvertFrom-Json; if($entry.type -eq 'thread.started'){$threadId=$entry.thread_id}}catch{}
            }
            $p.Dispose()
            }
            $validDecision=$false
            if (Test-Path -LiteralPath $reviewPath) {
                $result=Get-Content -LiteralPath $reviewPath -Raw | ConvertFrom-Json
                $validDecision=$result.type -eq 'review.decided' -and $result.runId -eq $m.runId -and $result.taskId -eq $m.task -and $result.payload.reviewedHead -eq $state.head -and $result.payload.decision -cin @('accept','rework','blocked','defer')
            }
            Save-AgentJson $claim @{status=if($validDecision -and $transportOk){'decided'}else{'needs_attention'};runId=$m.runId;reviewedHead=$state.head;exitCode=$exitCode;model=$Model;reviewerThreadId=$threadId;fixedManager=[bool]$FixedManager;reviewAttempt=$reviewAttempt;finished=[DateTime]::UtcNow.ToString('o')}
            Gate-Log 'review.finished' $m.runId "exit=$exitCode; validDecision=$validDecision; fixedManager=$FixedManager; thread=$threadId"
            } finally { $mainLease.Dispose() }
        } catch { Gate-Log 'gate.error' $job.Name $_.Exception.Message; Write-Warning ($job.Name + ': ' + $_.Exception.Message) }
    }
}
try {
    $watcher=New-AgentFileWatch (Join-Path $Repository 'tmp') "FufucodeGate-$PID"
    do {
        if(Test-Path (Join-Path $outbox 'stop.json')){break}
        Scan-Deliveries
        if (-not $Once) { [void](Wait-AgentFileWatch $watcher) }
        # Startup, OS file events and watcher errors reconcile backlog; no periodic detection.
    } while (-not $Once)
} finally {
    if($watcher){Close-AgentFileWatch $watcher}
    Save-AgentJson (Join-Path $outbox 'runtime.json') @{pid=$PID;stopped=[DateTime]::UtcNow.ToString('o');dispatch=[bool]$Dispatch;model=$Model;repository=$Repository;fixedManager=[bool]$FixedManager}
    $lock.Dispose()
}
