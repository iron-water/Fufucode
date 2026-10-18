# Prove an actual complete local Dispatch scan cannot repeat decided deliveries.
param([Parameter(Mandatory)][string]$Root)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/agent-events.ps1"
if($Root -notlike '*fufucode-flow-*' -or (Read-AgentJson "$Root/tmp/flow/config.json").model -cne 'devin/swe-2'){throw 'Only cheap isolated fixture'}
$watch=New-AgentFileWatch "$Root/tmp" "ReplayTest-$PID"
try {
    do{$runtime=Read-AgentJson "$Root/tmp/agent-flow/runtime.json";if($runtime.status -eq 'idle_complete'){break};if($runtime.status -in @('needs_attention','stopped','paused')){throw "Workflow cannot pass while $($runtime.status): $($runtime.error)"};[void](Wait-AgentFileWatch $watch)}while($true)
    & pwsh.exe -NoProfile -File "$PSScriptRoot/agent-flowtest.ps1" -Mode AssertWorkflow -Root $Root
    if($LASTEXITCODE){throw 'Full workflow assertion failed'}
    & pwsh.exe -NoProfile -File "$PSScriptRoot/agent-gate-control.ps1" -Mode Stop -Repository $Root
    if($LASTEXITCODE){throw 'Stop failed'}
    $managerHash=(Get-FileHash "$Root/tmp/agent-gate/manager.json").Hash
    $logs=@{};foreach($f in Get-ChildItem "$Root/tmp/agent-gate" -Recurse -Filter '*.stdout.jsonl'){$logs[$f.FullName]=(Get-FileHash $f.FullName).Hash}
    foreach($directory in Get-ChildItem "$Root/tmp/agent-queue" -Directory){$event="$($directory.FullName)/events/04.json";if(Test-Path $event){Save-AgentJson $event (Read-AgentJson $event)}}
    $codex=(Read-AgentJson "$Root/tmp/flow/config.json").codex
    & pwsh.exe -NoProfile -File "$PSScriptRoot/agent-gate.ps1" -Once -Repository $Root -Dispatch -FixedManager -Model devin/swe-2 -CodexExecutable $codex
    if($LASTEXITCODE){throw 'Actual replay scan failed'}
    if((Get-FileHash "$Root/tmp/agent-gate/manager.json").Hash -cne $managerHash){throw 'Replay invoked manager'}
    $after=@(Get-ChildItem "$Root/tmp/agent-gate" -Recurse -Filter '*.stdout.jsonl')
    if($after.Count -ne $logs.Count){throw 'Replay created another model transport'}
    foreach($file in $after){if((Get-FileHash $file.FullName).Hash -cne $logs[$file.FullName]){throw 'Replay changed an actual model log'}}
    & pwsh.exe -NoProfile -File "$PSScriptRoot/agent-gate-control.ps1" -Mode Resume -Repository $Root
    if($LASTEXITCODE){throw 'Resume failed'}
    $proof=Read-AgentJson "$Root/tmp/agent-flow/validation.json"
    $proof.replayAndEmptyQueueVerified=$true;$proof.replayEvidence=@{actualDispatchScan=$true;managerSha256=$managerHash;modelLogSha256=$logs;modelCallsAdded=0;at=[DateTime]::UtcNow.ToString('o')}
    if(-not $proof.sourceConsistencyVerified){throw 'Fixture code changed during run; no production proof'}
    Save-AgentJson "$Root/tmp/agent-flow/validation.json" $proof
    $gateProof=$proof.Clone();$gateProof.scope='event-review-only'
    Save-AgentJson "$Root/tmp/agent-gate/dispatch-validated.json" $gateProof
    Write-Output "PASS actual Dispatch replay + zero eligible events: 0 extra model calls; full-flow proof $Root/tmp/agent-flow/validation.json"
}finally{Close-AgentFileWatch $watch}
