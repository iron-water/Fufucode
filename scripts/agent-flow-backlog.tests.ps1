# Controlled gate restart while the independent workflow finishes B and delivers C.
# All waiting is OS file/process events. No test model or synthetic receipt is created.
param([Parameter(Mandatory)][string]$Root)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/agent-events.ps1"
if($Root -notlike '*fufucode-flow-*' -or (Read-AgentJson "$Root/tmp/flow/config.json").model -cne 'devin/swe-2'){throw 'Only real cheap isolated fixture'}
$watch=New-AgentFileWatch "$Root/tmp" "BacklogTest-$PID"
$control="$PSScriptRoot/agent-gate-control.ps1"
try {
    do {
        $manager=Read-AgentJson "$Root/tmp/agent-gate/manager.json"
        $runtime=Read-AgentJson "$Root/tmp/agent-flow/runtime.json"
        if($manager.status -eq 'needs_attention' -or $runtime.status -in @('needs_attention','stopped','paused')){throw 'Upstream flow failed; cannot pretend backlog recovery passed'}
        $integratingB=$false
        foreach($job in Get-ChildItem "$Root/tmp/agent-queue" -Directory -Filter 'cross-harness-B-*'){$manifest=Read-AgentJson "$($job.FullName)/manifest.json";if($manager.status -eq 'inflight' -and $manager.lastRunId -ceq "integration-$($manifest.runId)"){$integratingB=$true}}
        if($integratingB){break}
        [void](Wait-AgentFileWatch $watch)
    }while($true)
    $before=Read-AgentJson "$Root/tmp/agent-gate/runtime.json"
    & pwsh.exe -NoProfile -File $control -Mode Stop -Repository $Root
    if($LASTEXITCODE){throw 'Gate stop failed'}
    $status=(& pwsh.exe -NoProfile -File $control -Mode Status -Repository $Root|Out-String)|ConvertFrom-Json
    if($status.gateAlive){throw 'Gate did not actually exit'}
    do {
        $c="$Root/tmp/agent-queue/cross-harness-C-1"
        if(Test-Path "$c/state.json") {
            $state=Read-AgentJson "$c/state.json"
            if($state.workerPid){[void](Watch-AgentProcessExit $watch $state.workerPid)}
            if($state.status -eq 'completed_pending_review' -and -not (Test-AgentProcessIdentity $state 'worker')){break}
            if($state.status -in @('supervisor_error','failed_or_missing_card','orphaned_worker')){throw "C failed instead of delivery: $($state.status)"}
        }
        [void](Wait-AgentFileWatch $watch)
    }while($true)
    $manifest=Read-AgentJson "$c/manifest.json"
    if(Test-Path "$c/events/05.json"){throw 'Stopped gate somehow already reviewed C'}
    if(Test-Path "$Root/tmp/agent-gate/$($manifest.runId).dispatch.json"){throw 'C was already dispatched; not a fresh backlog'}
    $evidence=@{passed=$false;model='devin/swe-2';oldGatePid=$before.pid;pendingRun=$manifest.runId;pendingHead=$state.head;backlogReceived=(Read-AgentJson "$c/events/04.json").occurredAt;gateConfirmedStopped=$true;resumed=[DateTime]::UtcNow.ToString('o')}
    Save-AgentJson "$Root/tmp/flow/backlog-restart.json" $evidence
    & pwsh.exe -NoProfile -File $control -Mode Resume -Repository $Root
    if($LASTEXITCODE){throw 'Gate resume failed'}
    do {
        $after=Read-AgentJson "$Root/tmp/agent-gate/runtime.json"
        $claim="$Root/tmp/agent-gate/$($manifest.runId).dispatch.json"
        if(Test-Path $claim){$dispatch=Read-AgentJson $claim;if($dispatch.status -eq 'needs_attention'){throw 'Restart dispatch failed'};if($dispatch.status -eq 'decided'){break}}
        [void](Wait-AgentFileWatch $watch)
    }while($true)
    if($after.pid -eq $before.pid -or $dispatch.reviewerThreadId -cne (Read-AgentJson "$Root/tmp/agent-gate/manager.json").threadId){throw 'No actual fresh gate/fixed thread recovery'}
    $evidence.passed=$true;$evidence.newGatePid=$after.pid;$evidence.managerThreadId=$dispatch.reviewerThreadId;$evidence.finished=[DateTime]::UtcNow.ToString('o')
    Save-AgentJson "$Root/tmp/flow/backlog-restart.json" $evidence
    Write-Output "PASS: gate truly stopped before C received, startup recovered backlog in original fixed M, old/new gate PIDs $($before.pid)/$($after.pid)."
}finally{Close-AgentFileWatch $watch}
