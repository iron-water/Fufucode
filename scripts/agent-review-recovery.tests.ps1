# Actual cheap fixed-M Review transport, followed by zero-model completed-turn reconciliation.
param([string]$VerifyExisting)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/agent-events.ps1"
$root=if($VerifyExisting){(Resolve-Path $VerifyExisting).Path}else{Join-Path ([IO.Path]::GetTempPath()) ('fufucode-review-recovery-'+[guid]::NewGuid().ToString('N'))}
if(-not $root.StartsWith((Join-Path ([IO.Path]::GetTempPath()) 'fufucode-review-recovery-'),[StringComparison]::OrdinalIgnoreCase)){throw 'Only owned temporary review fixture'}
$job="$root/tmp/agent-queue/one";$gate="$root/tmp/agent-gate"
$exe='C:/Users/mio/AppData/Roaming/npm/node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe'
if(-not $VerifyExisting) {
foreach($dir in @($job,$gate,"$root/scripts","$root/docs/tasks")){[IO.Directory]::CreateDirectory($dir)|Out-Null}
foreach($name in @('agent-events.ps1','agent-queue.ps1')){Copy-Item "$PSScriptRoot/$name" "$root/scripts/$name"}
Set-Content "$root/AGENTS.md" 'Isolated mechanism fixture. All models devin/swe-2. Review only the specified one delivery. No source changes, no subagents, no push. Use the local agent-queue Review entry.'
foreach($name in @('unattended-development.md','agent-event-protocol.md','execution-contract.md')){Set-Content "$root/docs/tasks/$name" 'A committed marker 42 is a valid module_ready delivery. Verify actual marker, HEAD and stopped writers. Accept with pending_wiring and no mainSha. Persist decision through Review; never edit review.json directly.'}
Set-Content "$root/.gitignore" "tmp/`n"
Set-Content "$root/marker.txt" '42'
& git.exe -C $root init -b main|Out-Null
& git.exe -C $root config user.name Fixture
& git.exe -C $root config user.email fixture@localhost
& git.exe -C $root add -- .gitignore AGENTS.md marker.txt docs scripts
& git.exe -C $root commit -m 'meaningless review fixture'|Out-Null
if($LASTEXITCODE){throw 'Fixture Git failed'}
$head=(& git.exe -C $root rev-parse HEAD).Trim()
$manifest=@{schemaVersion=1;runId='permission-review';task='TEST';worktree=$root;base=$head;model='devin/swe-2';allowedPaths=@('marker.txt');protocol='codex-json-v1'}
$state=@{schemaVersion=1;runId=$manifest.runId;task='TEST';status='completed_pending_review';head=$head;finished=[DateTime]::UtcNow.ToString('o');workerPid=0;agentPid=0;exitCode=0;dirty=''}
$card=@{schemaVersion=1;type='delivery.card';taskId='TEST';status='module_ready';summary='Meaningless committed marker';commits=@();changedFiles=@();verification=@{passed=@();failed=@();notRun=@()};evidence=@('fixture marker only');integrationNeeds=@('M owns wiring');blockers=@();writerStopped=$true}
Save-AgentJson "$job/manifest.json" $manifest
Save-AgentJson "$job/state.json" $state
Save-AgentJson "$job/delivery.json" @{schemaVersion=1;runId=$manifest.runId;taskId='TEST';head=$head;base=$head;card=$card;status='unreviewed'}
Write-AgentEvent $job $manifest 'delivery.received' @{head=$head;cardStatus='module_ready';receipt='delivery.json';legacy=$false}
$exe='C:/Users/mio/AppData/Roaming/npm/node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe'
& pwsh.exe -NoProfile -File "$PSScriptRoot/agent-manager-control.ps1" -Mode Initialize -Repository $root -Model devin/swe-2 -CodexExecutable $exe -TimeoutSeconds 900
if($LASTEXITCODE){throw 'Actual cheap initialization failed; preserve evidence, no retry'}
& pwsh.exe -NoProfile -File "$PSScriptRoot/agent-gate.ps1" -Once -Repository $root -Dispatch -FixedManager -Model devin/swe-2 -CodexExecutable $exe -ReviewTimeoutSeconds 900
if($LASTEXITCODE){throw 'Actual review scan failed'}
}
$manifest=Read-AgentJson "$job/manifest.json";$state=Read-AgentJson "$job/state.json";$head=(& git.exe -C $root rev-parse HEAD).Trim()
if($manifest.model -cne 'devin/swe-2' -or $manifest.worktree -ne $root){throw 'Unknown/expensive fixture refused'}
$claimPath="$gate/$($manifest.runId).dispatch.json";$claim=Read-AgentJson $claimPath
$review=Read-AgentJson "$job/review.json";$binding=Read-AgentJson "$gate/manager.json"
if($claim.status -cne 'decided' -or $review.decision -cne 'accept' -or $review.reviewedHead -cne $head -or $binding.status -cne 'ready'){throw 'Actual model did not persist the valid Review'}
$session=@(Get-ChildItem "$env:USERPROFILE/.codex/sessions" -Recurse -Filter "*$($binding.threadId)*.jsonl")
if($session.Count -ne 1){throw 'No unique original saved session'}
$contexts=@([IO.File]::ReadLines($session[0].FullName)|ForEach-Object {$e=$_|ConvertFrom-Json -AsHashtable;if($e.type -eq 'turn_context'){$e.payload}})
if($contexts[-1].model -cne 'devin/swe-2' -or $contexts[-1].sandbox_policy.type -cne 'danger-full-access'){throw 'Actual resumed model/permissions differ from explicit transport'}
$logHash=(Get-FileHash "$gate/$($manifest.runId).stdout.jsonl").Hash
$backup="$gate/accepted-input.json";Save-AgentJson $backup $review
# Fixture only: completed model without durable decision is never auto-authorized to rerun.
Remove-Item -LiteralPath "$job/events/05.json","$job/review.json"
$claim.status='needs_attention';Save-AgentJson $claimPath $claim
& pwsh.exe -NoProfile -File "$PSScriptRoot/agent-manager-control.ps1" -Mode Reconcile -Repository $root -SessionLog $session[0].FullName
if($LASTEXITCODE -eq 0 -or (Read-AgentJson $claimPath).status -cne 'needs_attention'){throw 'Completed turn without decision was made retryable'}
# Local explicitly validated decision repairs persistence, not the model transport.
& pwsh.exe -NoProfile -File "$PSScriptRoot/agent-queue.ps1" -Mode Review -JobDirectory $job -ReviewFile $backup
if($LASTEXITCODE){throw 'Review entry refused original decision'}
$decisionEvent=Read-AgentJson "$job/events/05.json"
$badEvent=$decisionEvent.Clone();$badEvent.taskId='WRONG-TASK';Save-AgentJson "$job/events/05.json" $badEvent
& pwsh.exe -NoProfile -File "$PSScriptRoot/agent-manager-control.ps1" -Mode Reconcile -Repository $root -SessionLog $session[0].FullName
if($LASTEXITCODE -eq 0 -or (Read-AgentJson $claimPath).status -cne 'needs_attention'){throw 'Conflicting task decision reconciled'}
Save-AgentJson "$job/events/05.json" $decisionEvent
$badState=$state.Clone();$badState.status='running';Save-AgentJson "$job/state.json" $badState
& pwsh.exe -NoProfile -File "$PSScriptRoot/agent-manager-control.ps1" -Mode Reconcile -Repository $root -SessionLog $session[0].FullName
if($LASTEXITCODE -eq 0 -or (Read-AgentJson $claimPath).status -cne 'needs_attention'){throw 'Nonterminal worker state reconciled'}
Save-AgentJson "$job/state.json" $state
& pwsh.exe -NoProfile -File "$PSScriptRoot/agent-manager-control.ps1" -Mode Reconcile -Repository $root -SessionLog $session[0].FullName
if($LASTEXITCODE -or (Read-AgentJson $claimPath).status -cne 'decided'){throw 'Known completed turn reconciliation failed'}
& pwsh.exe -NoProfile -File "$PSScriptRoot/agent-gate.ps1" -Once -Repository $root -Dispatch -FixedManager -Model devin/swe-2 -CodexExecutable $exe
if($LASTEXITCODE -or (Get-FileHash "$gate/$($manifest.runId).stdout.jsonl").Hash -cne $logHash){throw 'Reconciliation/replay issued a duplicate model request'}
$hashes=@{};foreach($name in @('agent-manager.ps1','agent-manager-control.ps1','agent-gate.ps1')){$hashes[$name]=(Get-FileHash "$PSScriptRoot/$name").Hash}
Save-AgentJson "$gate/repair-validated.json" @{status='passed';scope='review-permission-and-completed-turn-recovery';model='devin/swe-2';root=$root;threadId=$binding.threadId;originalSession=$session[0].FullName;actualSandbox=$contexts[-1].sandbox_policy.type;actualDecision=$review.decision;noDecisionRefused=$true;conflictingTaskRefused=$true;nonterminalStateRefused=$true;recoveryAndReplayModelCallsAdded=0;scripts=$hashes;verifiedAt=[DateTime]::UtcNow.ToString('o')}
Write-Output "PASS actual cheap fixed-M Review, completed-without-decision refusal, Review-entry reconciliation and zero-call replay: $gate/repair-validated.json"
