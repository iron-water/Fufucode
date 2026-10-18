$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-manager-recovery-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
function Put($path,$data){[IO.Directory]::CreateDirectory((Split-Path $path))|Out-Null;$data|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $path -Encoding utf8}
foreach($scenario in @('safe','effects','unknown')){
 $repo=Join-Path $root $scenario
 [IO.Directory]::CreateDirectory("$repo/scripts")|Out-Null
 foreach($file in @('agent-events.ps1','agent-manager.ps1','agent-manager-control.ps1')){Copy-Item -LiteralPath "$PSScriptRoot/$file" -Destination "$repo/scripts/$file"}
 & git.exe -C $repo init --quiet
 & git.exe -C $repo -c user.name=fixture -c user.email=fixture@example.invalid commit --quiet --allow-empty -m fixture
 $head=(& git.exe -C $repo rev-parse HEAD).Trim()
 $box="$repo/tmp/agent-gate";$job="$repo/tmp/agent-queue/job";$thread=[guid]::NewGuid().ToString()
 Put "$box/manager.json" @{schemaVersion=1;repository=$repo;model='devin/swe-2';threadId=$thread;status='needs_attention';processId=0;lastRunId='fixture-run';lastStarted='2026-01-01T00:00:00Z';exitCode=-1073740791}
 Put "$box/fixture-run.dispatch.json" @{runId='fixture-run';status='needs_attention';reviewedHead=$head;reviewerThreadId=$thread;reviewAttempt=0;exitCode=-1073740791}
 Put "$box/fixture-run.request.json" @{runId='fixture-run';taskId='A';jobDirectory=$job;reviewedHead=$head}
 Put "$job/manifest.json" @{runId='fixture-run';task='A';worktree=$repo}
 Put "$job/state.json" @{status='completed_pending_review';finished='2026-01-01T00:00:01Z';head=$head}
 $entries=@(
 @{type='session_meta';timestamp='2025-12-31T23:59:59Z';payload=@{id=$thread;cwd=$repo}},
 @{type='event_msg';timestamp='2026-01-01T00:00:02Z';payload=@{type='task_started';turn_id='turn'}},
 @{type='response_item';timestamp='2026-01-01T00:00:03Z';payload=@{type='message';role='user';content=@(@{text='review fixture-run'})}}
 )
 if($scenario -eq 'effects'){$entries+=@{type='response_item';timestamp='2026-01-01T00:00:04Z';payload=@{type='function_call'}}}
 $session="$repo/session.jsonl";$entries|ForEach-Object {$_|ConvertTo-Json -Depth 20 -Compress}|Set-Content $session
 @{type='turn.started'}|ConvertTo-Json -Compress|Set-Content "$box/fixture-run.stdout.jsonl"
 $errorText=if($scenario -eq 'unknown'){'connection lost'}else{'memory allocation of 1073741824 bytes failed'}
 Set-Content "$box/fixture-run.stderr.log" $errorText
 $result=& pwsh -NoProfile -File "$repo/scripts/agent-manager-control.ps1" -Mode RecoverFailed -Repository $repo -SessionLog $session 2>&1
 $exitCode=$LASTEXITCODE
 if($scenario -eq 'safe'){
  if($exitCode -ne 0){throw "Safe recovery failed: $result"}
  $claim=Get-Content "$box/fixture-run.dispatch.json" -Raw|ConvertFrom-Json
  if($claim.status -ne 'resume_authorized' -or $claim.reviewAttempt -ne 1){throw 'Recovery identity lost'}
  & pwsh -NoProfile -File "$repo/scripts/agent-manager-control.ps1" -Mode RecoverFailed -Repository $repo -SessionLog $session 2>&1|Out-Null
  if($LASTEXITCODE -eq 0){throw 'Duplicate recovery authorized'}
  Put "$job/review.json" @{type='review.decision';runId='fixture-run';taskId='A';reviewedHead=$head;decision='rework';requirementRevision=0}
  $event=@{type='review.decided';eventId='fixture-run/review.decided';runId='fixture-run';taskId='A';payload=@{reviewedHead=$head;decision='rework';requirementRevision=0}}
  . "$repo/scripts/agent-events.ps1"
  . "$repo/scripts/agent-manager.ps1"
  $hash=(Get-FileHash "$job/review.json").Hash
  if(-not (Restore-AgentDecidedClaim $repo $job $event 'fixture-run')){throw 'Persisted decision did not settle recovery claim'}
  if(Restore-AgentDecidedClaim $repo $job $event 'fixture-run'){throw 'Decision claim replay was not a no-op'}
  if((Get-FileHash "$job/review.json").Hash -ne $hash){throw 'Decision rewritten during reconciliation'}
 }elseif($exitCode -eq 0){throw "Unsafe recovery allowed: $scenario"}
}
Write-Output "PASS explicit safe recovery, replay rejection, effects/unknown freeze; zero model calls. Evidence: $root"
