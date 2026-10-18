$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-gate-'+[guid]::NewGuid().ToString('N'))
$queue=Join-Path $root 'tmp/agent-queue'
New-Item -ItemType Directory -Path $queue -Force | Out-Null
$repo=Split-Path $PSScriptRoot -Parent
$head=(& git -C $repo rev-parse HEAD).Trim()
function Fixture($id,$probe=$false,$running=$false,$reviewed=$false) {
    $job=Join-Path $queue $id
    New-Item -ItemType Directory -Path (Join-Path $job 'events') -Force | Out-Null
    @{runId=$id;task='TEST';worktree=$repo;probe=$probe}|ConvertTo-Json|Set-Content (Join-Path $job 'manifest.json')
    @{status='completed_pending_review';finished=[DateTime]::UtcNow.ToString('o');head=$head;workerPid=if($running){$PID}else{0};workerStarted=if($running){(Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o')}else{$null};agentPid=0}|ConvertTo-Json|Set-Content (Join-Path $job 'state.json')
    @{type='delivery.received';runId=$id;taskId='TEST';payload=@{head=$head}}|ConvertTo-Json|Set-Content (Join-Path $job 'events/04.json')
    @{runId=$id;taskId='TEST';head=$head;legacy=$true}|ConvertTo-Json|Set-Content (Join-Path $job 'delivery.json')
    if($reviewed){@{type='review.decided';runId=$id;taskId='TEST';payload=@{reviewedHead=$head;decision='accept'}}|ConvertTo-Json|Set-Content (Join-Path $job 'events/05.json')}
}
Fixture 'ready'; Fixture 'probe' $true; Fixture 'running' $false $true; Fixture 'reviewed' $false $false $true
& (Join-Path $PSScriptRoot 'agent-gate.ps1') -Repository $root -Once
$files=@(Get-ChildItem (Join-Path $root 'tmp/agent-gate') -Filter '*.request.json')
if($files.Count -ne 1 -or $files[0].BaseName -ne 'ready.request'){throw 'Eligibility failed'}
$hash=(Get-FileHash $files[0].FullName).Hash
& (Join-Path $PSScriptRoot 'agent-gate.ps1') -Repository $root -Once
if((Get-FileHash $files[0].FullName).Hash -ne $hash){throw 'Replay changed request'}
if(Get-ChildItem (Join-Path $root 'tmp/agent-gate') -Filter '*.dispatch.json'){throw 'Collection invoked model'}
Write-Output "PASS: eligibility, running guard, probe/review skip, replay, zero dispatch. Fixture retained: $root"
Fixture 'bad-review' $false $false $true
'{}' | Set-Content (Join-Path $queue 'bad-review/events/05.json')
Fixture 'changed-head'
$s=Get-Content (Join-Path $queue 'changed-head/state.json') -Raw | ConvertFrom-Json
$s.head='0000000000000000000000000000000000000000'
$s | ConvertTo-Json | Set-Content (Join-Path $queue 'changed-head/state.json')
Fixture 'bad-receipt'
'{}' | Set-Content (Join-Path $queue 'bad-receipt/delivery.json')
& (Join-Path $PSScriptRoot 'agent-gate.ps1') -Repository $root -Once
if(@(Get-ChildItem (Join-Path $root 'tmp/agent-gate') -Filter '*.request.json').Count -ne 1){throw 'Invalid input was queued'}
# Holding the managed worktree mutex must prevent even a model dispatch attempt.
$key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($repo.ToLowerInvariant())))
$mutex=[Threading.Mutex]::new($false,"Local\FufucodeAgent-$key")
$held=$mutex.WaitOne(0)
try {
    $psi=[Diagnostics.ProcessStartInfo]::new()
    $psi.FileName=(Get-Command pwsh.exe).Source; $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true; $psi.WorkingDirectory=$repo
    foreach($a in @('-NoProfile','-File',(Join-Path $PSScriptRoot 'agent-gate.ps1'),'-Repository',$root,'-Once','-Dispatch','-Model','devin/swe-2','-CodexExecutable',(Get-Command pwsh.exe).Source)){$psi.ArgumentList.Add($a)}
    $p=[Diagnostics.Process]::Start($psi); $p.WaitForExit(); if($p.ExitCode){throw 'Writer guard subprocess failed'}
    if(Get-ChildItem (Join-Path $root 'tmp/agent-gate') -Filter '*.dispatch.json'){throw 'Active writer dispatched'}
} finally {if($held){$mutex.ReleaseMutex()};$mutex.Dispose()}
# pwsh is a deliberate failing native transport, not a model. The resulting claim is durable.
& (Join-Path $PSScriptRoot 'agent-gate.ps1') -Repository $root -Once -Dispatch -Model 'devin/swe-2' -CodexExecutable (Get-Command pwsh.exe).Source
$claimPath=Join-Path $root 'tmp/agent-gate/ready.dispatch.json'
$claim=Get-Content $claimPath -Raw | ConvertFrom-Json
if($claim.status -ne 'needs_attention'){throw 'Failed reviewer was accepted'}
$hash=(Get-FileHash $claimPath).Hash
& (Join-Path $PSScriptRoot 'agent-gate.ps1') -Repository $root -Once -Dispatch -Model 'devin/swe-2' -CodexExecutable (Get-Command pwsh.exe).Source
if((Get-FileHash $claimPath).Hash -ne $hash){throw 'Failed dispatch was retried'}
# Simulate a crashed supervisor after persisting a claim. Restart must freeze all dispatches.
Fixture 'backlog'
@{status='dispatching';runId='unknown';model='devin/swe-2'}|ConvertTo-Json|Set-Content (Join-Path $root 'tmp/agent-gate/unknown.dispatch.json')
& (Join-Path $PSScriptRoot 'agent-gate.ps1') -Repository $root -Once -Dispatch -Model 'devin/swe-2' -CodexExecutable (Get-Command pwsh.exe).Source
if(Test-Path (Join-Path $root 'tmp/agent-gate/backlog.dispatch.json')){throw 'Unknown dispatch was bypassed on restart'}
Write-Output "PASS: malformed review/receipt, changed HEAD, managed writer, failed reviewer replay, ambiguous restart freeze. Fixture retained: $root"

# Validator failure is evidence for an independent review, not a global scheduling dead end.
$root=Join-Path $root 'validator-failure'
$queue=Join-Path $root 'tmp/agent-queue'
$outbox=Join-Path $root 'tmp/agent-gate'
New-Item -ItemType Directory -Path $queue,$outbox,"$root/tmp/agent-core/claims" -Force|Out-Null
Fixture 'failed-validation'
@{enabled=$true}|ConvertTo-Json|Set-Content "$root/tmp/agent-core/policy.json"
@{eventId='failed-validation/verification.local_passed'}|ConvertTo-Json|Set-Content "$outbox/failed-validation.verification.json"
$coreId=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('failed-validation/verification.local_passed'))).ToLowerInvariant()
@{key=$coreId;status='needs_attention';error='Invalid advisory JSON';automaticRetry=$false}|ConvertTo-Json|Set-Content "$root/tmp/agent-core/claims/$coreId.json"
# pwsh rejects Codex arguments locally; this records an attempted review transport without any model.
& (Join-Path $PSScriptRoot 'agent-gate.ps1') -Repository $root -Once -Dispatch -Model 'devin/swe-2' -CodexExecutable (Get-Command pwsh.exe).Source
if(-not (Test-Path "$outbox/failed-validation.dispatch.json")){throw 'Validator failure stranded the review'}
if(Test-Path "$root/tmp/agent-core/results/$coreId.json"){throw 'Failed validation was promoted to success'}
Write-Output 'PASS: failed validator reaches independent review, no fabricated successful report; zero model calls.'
