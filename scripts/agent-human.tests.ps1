# Actual immutable storage, replay, acknowledgment and native Review gates. No models.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/agent-events.ps1"
. "$PSScriptRoot/agent-human.ps1"
$root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-human-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory("$root/tmp/agent-flow")|Out-Null
Save-AgentJson "$root/tmp/agent-flow/plan.json" @{id='test';tasks=@(@{id='A';enabled=$true})}
$r=@{schemaVersion=1;type='human.change.request';commandId='human-00000001';projectId='test';taskId='A';baseRevision=0;action='append_acceptance';requirement='必须为42';acceptance=@('value equals 42')}
Save-AgentJson "$root/input.json" $r
function Native($Arguments,[bool]$Expected=$true){$out=& pwsh -NoProfile -File "$PSScriptRoot/agent-human-control.ps1" -Repository $root @Arguments 2>&1;if(($LASTEXITCODE -eq 0) -ne $Expected){throw "Unexpected result: $out"}}
Native @('-Mode','Submit','-RequestFile',"$root/input.json")
Native @('-Mode','Submit','-RequestFile',"$root/input.json")
if((Get-HumanRequirements $root 'A').revision -ne 1){throw 'Replay duplicated revision'}
$r.requirement='changed';Save-AgentJson "$root/input.json" $r
Native @('-Mode','Submit','-RequestFile',"$root/input.json") $false
try{Assert-HumanReview $root 'A' @{requirementRevision=1};throw 'Pending accepted'}catch{if($_.Exception.Message -eq 'Pending accepted'){throw}}
Native @('-Mode','Acknowledge','-CommandId','human-00000001','-Decision','applied','-Evidence','M checked applicability')
Assert-HumanReview $root 'A' @{requirementRevision=1}
try{Assert-HumanReview $root 'A' @{requirementRevision=0};throw 'Stale accepted'}catch{if($_.Exception.Message -eq 'Stale accepted'){throw}}
# Reconstruct convenience record after a crash; authoritative index prevents duplicate.
Move-Item "$root/tmp/agent-flow/human/request-human-00000001.json" "$root/saved-request.json"
$r.requirement='必须为42';Save-AgentJson "$root/input.json" $r
Native @('-Mode','Submit','-RequestFile',"$root/input.json")
if((Get-HumanRequirements $root 'A').revision -ne 1){throw 'Crash replay duplicated revision'}
$r.commandId='human-00000002';$r.baseRevision=1;Save-AgentJson "$root/input.json" $r
Native @('-Mode','Submit','-RequestFile',"$root/input.json")
Native @('-Mode','Acknowledge','-CommandId','human-00000002','-Decision','blocked','-Evidence','Needs scope coordination')
if((Get-HumanRequirements $root 'A').ready){throw 'Blocked requirement became accepted'}
$r.commandId='human-00000003';$r.baseRevision=2;$r.supersedes=@('human-00000002');Save-AgentJson "$root/input.json" $r
Native @('-Mode','Submit','-RequestFile',"$root/input.json")
Native @('-Mode','Acknowledge','-CommandId','human-00000003','-Decision','applied','-Evidence','Corrected blocked requirement')
if(-not (Get-HumanRequirements $root 'A').ready){throw 'Explicit supersession did not unblock corrected requirements'}
& git -C $root init -q
& git -C $root config user.name Fixture
& git -C $root config user.email fixture@localhost
Set-Content "$root/value.txt" '42'
& git -C $root add -- value.txt
& git -C $root commit -qm fixture
$head=(& git -C $root rev-parse HEAD).Trim()
$nativeJob="$root/tmp/agent-queue/native-A";[IO.Directory]::CreateDirectory("$nativeJob/events")|Out-Null
Save-AgentJson "$nativeJob/manifest.json" @{runId='native-a';task='A';worktree=$root}
Save-AgentJson "$nativeJob/state.json" @{status='completed_pending_review';head=$head;finished=[DateTime]::UtcNow.ToString('o');workerPid=0;agentPid=0}
Save-AgentJson "$nativeJob/delivery.json" @{runId='native-a';taskId='A';head=$head;card=@{status='module_ready'}}
Save-AgentJson "$nativeJob/events/04.json" @{type='delivery.received';runId='native-a';taskId='A';payload=@{head=$head}}
$review=@{schemaVersion=1;type='review.decision';runId='native-a';taskId='A';reviewedHead=$head;decision='accept';requirementRevision=1;evidence=@('fixture');findings=@();nextAction='module ready';integrationStatus='not_integrated'}
Save-AgentJson "$root/review-input.json" $review
$out=& pwsh -NoProfile -File "$PSScriptRoot/agent-queue.ps1" -Mode Review -JobDirectory $nativeJob -ReviewFile "$root/review-input.json" 2>&1
if($LASTEXITCODE -eq 0 -or (Test-Path "$nativeJob/review.json")){throw 'Native Review accepted stale requirements'}
$review.requirementRevision=3;Save-AgentJson "$root/review-input.json" $review
& pwsh -NoProfile -File "$PSScriptRoot/agent-queue.ps1" -Mode Review -JobDirectory $nativeJob -ReviewFile "$root/review-input.json"
if($LASTEXITCODE -or -not (Test-Path "$nativeJob/events/05-r3.json")){throw 'Native current-version Review failed'}
$manifest=@{runId='r1';task='A'}
Write-AgentEvent "$root/tmp/agent-queue/A" $manifest 'review.decided' @{decision='accept'}
Write-AgentEvent "$root/tmp/agent-queue/A" $manifest 'review.decided' @{decision='rework';requirementRevision=1}
if(-not (Test-Path "$root/tmp/agent-queue/A/events/05.json") -or -not (Test-Path "$root/tmp/agent-queue/A/events/05-r1.json")){throw 'Review history lost'}
Save-AgentJson "$root/validation.json" @{status='passed';modelCalls=0;checks=@('replay','conflicting replay','pending blocked','stale blocked','acknowledgment','crash recovery','blocked applicability','immutable review history')}
Write-Output "PASS human requirement storage/replay/ack/stale review/history. Evidence: $root"
