# Fixed Codex thread transport. Dot-source; does not call a model by itself.
function Restore-AgentDecidedClaim($Repository,$JobDirectory,$Event,$ReviewKey) {
    # An explicitly authorized recovery must not resend after a valid decision appeared.
    $box=Join-Path $Repository 'tmp/agent-gate'
    $path=Join-Path $box ($ReviewKey+'.dispatch.json')
    if(-not (Test-Path $path)){return $false}
    $lease=$null
    try{$lease=[IO.File]::Open((Join-Path $box 'main-writer.lock'),'OpenOrCreate','ReadWrite','None')}catch [IO.IOException]{return $false}
    try {
        $claim=Read-AgentJson $path;$binding=Read-AgentJson (Join-Path $box 'manager.json')
        if($claim.status -ne 'resume_authorized' -or $binding.status -ne 'ready'){return $false}
        $m=Read-AgentJson (Join-Path $JobDirectory 'manifest.json')
        $s=Read-AgentJson (Join-Path $JobDirectory 'state.json')
        $r=Read-AgentJson (Join-Path $JobDirectory 'review.json')
        $actual=& git.exe -C $m.worktree rev-parse HEAD
        if($LASTEXITCODE -or $s.status -ne 'completed_pending_review' -or -not $s.finished -or
           $actual.Trim() -cne $s.head -or $claim.runId -cne $m.runId -or $claim.reviewedHead -cne $s.head -or
           $Event.type -cne 'review.decided' -or $Event.runId -cne $m.runId -or $Event.taskId -cne $m.task -or
           $Event.payload.reviewedHead -cne $s.head -or $Event.payload.decision -cnotin @('accept','rework','blocked','defer') -or
           $r.type -cne 'review.decision' -or $r.runId -cne $m.runId -or $r.taskId -cne $m.task -or
           $r.reviewedHead -cne $s.head -or $r.decision -cne $Event.payload.decision -or
           [int]$r.requirementRevision -ne [int]$Event.payload.requirementRevision){throw 'Decision recovery identity mismatch'}
        foreach($role in @('worker','agent')){if(Test-AgentProcessIdentity $s $role){return $false}}
        if(Test-AgentProcessIdentity @{agentPid=$binding.processId;agentStarted=$binding.processStarted} 'agent'){return $false}
        $claim.status='decided';$claim.decisionRecoveredAt=[DateTime]::UtcNow.ToString('o')
        $claim.decisionEvent=$Event.eventId
        Save-AgentJson $path $claim
        return $true
    }finally{$lease.Dispose()}
}

function Read-AgentManager($Repository, $Model) {
    $path=Join-Path $Repository 'tmp/agent-gate/manager.json'
    $binding=Read-AgentJson $path
    if($binding.schemaVersion -ne 1 -or $binding.repository -ne $Repository -or $binding.model -cne $Model -or
        $binding.threadId -notmatch '^[0-9a-f-]{36}$' -or $binding.status -ne 'ready') { throw 'Fixed manager binding is invalid or unresolved; no automatic replacement' }
    if($binding.threadId -eq $env:CODEX_THREAD_ID){throw 'Never resume the currently active interactive thread as a second writer'}
    return $binding
}
function Invoke-AgentManagerTurn {
    param([string]$Repository,[string]$Model,[string]$CodexExecutable,[string]$RunId,[string]$Prompt,
        [int]$TimeoutSeconds=1800,[switch]$Initialize,[switch]$Integration)
    $outbox=Join-Path $Repository 'tmp/agent-gate'
    $bindingPath=Join-Path $outbox 'manager.json'
    $managerLock=[IO.File]::Open((Join-Path $outbox 'manager.lock'),'OpenOrCreate','ReadWrite','None')
    $process=$null
    $claimed=$false
    $outStream=$null; $errStream=$null
    try {
        if($Initialize) {
            if(Test-Path -LiteralPath $bindingPath){throw 'Manager binding already exists; inspect instead of replacing or reinitializing'}
            $binding=@{schemaVersion=1;repository=$Repository;model=$Model;threadId=$null;status='initializing';created=[DateTime]::UtcNow.ToString('o')}
            $logDirectory=Join-Path $outbox 'manager'
            [IO.Directory]::CreateDirectory($logDirectory)|Out-Null
            $prefix=Join-Path $logDirectory 'bootstrap'
        } else {
            $binding=Read-AgentManager $Repository $Model
            $binding.status='inflight'
            $prefix=Join-Path $outbox $RunId
        }
        $binding.lastRunId=$RunId
        $binding.lastStarted=[DateTime]::UtcNow.ToString('o')
        # This write occurs before process creation. A crash cannot justify creating another thread.
        Save-AgentJson $bindingPath $binding
        $claimed=$true
        $psi=[Diagnostics.ProcessStartInfo]::new()
        $psi.FileName=$CodexExecutable; $psi.WorkingDirectory=$Repository
        $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
        $psi.RedirectStandardInput=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
        if($Initialize -and $Integration){throw 'Initialization cannot integrate'}
        # The authorized managed M must persist its Review input and queue decision.
        # Windows workspace-write can deny this real repository despite allowing Temp fixtures.
        # Initialization stays read-only; review scope and the main-writer lease remain unchanged.
        $sandbox=if($Initialize){'read-only'}else{'danger-full-access'}
        $arguments=@('--no-daemon','exec','-m',$Model,'-C',$Repository,'-s',$sandbox,'-c','approval_policy="never"','--json')
        if(-not $Initialize){$arguments+=@('resume',$binding.threadId)}
        $arguments+='-'
        foreach($argument in $arguments){$psi.ArgumentList.Add($argument)}
        $process=[Diagnostics.Process]::Start($psi)
        $binding.processId=$process.Id
        $binding.processStarted=$process.StartTime.ToUniversalTime().ToString('o')
        Save-AgentJson $bindingPath $binding
        $outStream=[IO.File]::Open(($prefix+'.stdout.jsonl'),'CreateNew','Write','Read')
        $errStream=[IO.File]::Open(($prefix+'.stderr.log'),'CreateNew','Write','Read')
        $outCopy=$process.StandardOutput.BaseStream.CopyToAsync($outStream)
        $errCopy=$process.StandardError.BaseStream.CopyToAsync($errStream)
        $process.StandardInput.Write($Prompt); $process.StandardInput.Close()
        $timedOut=-not $process.WaitForExit($TimeoutSeconds * 1000)
        if($timedOut){$process.Kill($true); $process.WaitForExit()}
        [void]$outCopy.GetAwaiter().GetResult(); [void]$errCopy.GetAwaiter().GetResult()
        $outStream.Dispose(); $outStream=$null; $errStream.Dispose(); $errStream=$null
        $threadId=$null; $terminal=$null
        foreach($line in [IO.File]::ReadLines(($prefix+'.stdout.jsonl'))) {
            $entry=$line | ConvertFrom-Json
            if($entry.type -eq 'thread.started'){$threadId=$entry.thread_id}
            if($entry.type -in @('turn.completed','turn.failed')){$terminal=$entry.type}
        }
        if(-not $Initialize -and $threadId -cne $binding.threadId){throw 'Resumed manager returned a different thread identity'}
        if($threadId -notmatch '^[0-9a-f-]{36}$'){throw 'Manager thread identity missing; retain binding/logs, no automatic retry'}
        $binding.threadId=$threadId
        $ok=$process.ExitCode -eq 0 -and -not $timedOut -and $terminal -eq 'turn.completed'
        $binding.status=if($ok){'ready'}else{'needs_attention'}
        $binding.lastFinished=[DateTime]::UtcNow.ToString('o')
        $binding.exitCode=$process.ExitCode; $binding.timedOut=$timedOut
        Save-AgentJson $bindingPath $binding
        return @{exitCode=$process.ExitCode;threadId=$threadId;completed=$ok;logPrefix=$prefix}
    } catch {
        # Do not turn a possibly running process into a retryable binding.
        if($claimed -and $binding -and (Test-Path -LiteralPath $bindingPath)) {
            $binding.status='needs_attention'; $binding.error=$_.Exception.Message
            $binding.processMayBeRunning=[bool]($process -and -not $process.HasExited)
            Save-AgentJson $bindingPath $binding
        }
        throw
    } finally {
        if($outStream){$outStream.Dispose()}; if($errStream){$errStream.Dispose()}
        if($process){$process.Dispose()}; $managerLock.Dispose()
    }
}
