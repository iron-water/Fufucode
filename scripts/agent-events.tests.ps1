param([string]$RealProtocolLog, [string]$IntegrationWorktree)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'agent-events.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('fufucode-event-tests-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$script:passed = 0
function Assert-True($Condition, $Message) { if (-not $Condition) { throw $Message }; $script:passed++ }
function Assert-Rejected([scriptblock]$Action, $Message) { $caught=$false; try { & $Action | Out-Null } catch { $caught=$true }; Assert-True $caught $Message }
function New-Card { return @{schemaVersion=1;type='delivery.card';taskId='T1';status='module_ready';summary='模块待M接线，尚未整合';commits=@();changedFiles=@();verification=@{passed=@();failed=@();notRun=@('native')};evidence=@();integrationNeeds=@('M wiring');blockers=@();writerStopped=$true} }
function New-Protocol($Directory, $Card) {
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    $message = @{role='assistant';stopReason='stop';content=@(@{type='text';text=($Card | ConvertTo-Json -Depth 20 -Compress)})}
    $path = Join-Path $Directory 'stdout.log'
    @(@{type='session';id='session-test'},@{type='agent_end';isTerminal=$true;messages=@($message)}) |
        ForEach-Object { $_ | ConvertTo-Json -Depth 25 -Compress } | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}
$card = New-Card
$json = $card | ConvertTo-Json -Depth 20 -Compress
Assert-True ((Read-AgentCard $json 'T1').status -eq 'module_ready') 'Module-ready must remain a valid delivery'
Assert-True ((Read-AgentCard ('```json' + "`n" + $json + "`n" + '```') 'T1').status -eq 'module_ready') 'Fenced JSON rejected'
Assert-Rejected { Read-AgentCard '状态：尚未整合，不能标为已整合' 'T1' } 'Prose accepted as JSON'
Assert-Rejected { Read-AgentCard $json 'OTHER' } 'Wrong task accepted'
$bad=New-Card; $bad.status='accepted'; Assert-Rejected { Read-AgentCard ($bad | ConvertTo-Json -Depth 20) 'T1' } 'Agent self-acceptance allowed'
$bad=New-Card; $bad.writerStopped='true'; Assert-Rejected { Read-AgentCard ($bad | ConvertTo-Json -Depth 20) 'T1' } 'String stopped flag accepted'
$bad=New-Card; $bad.Remove('verification'); Assert-Rejected { Read-AgentCard ($bad | ConvertTo-Json -Depth 20) 'T1' } 'Missing verification accepted'
$bad=New-Card; $bad.status='blocked'; Assert-Rejected { Read-AgentCard ($bad | ConvertTo-Json -Depth 20) 'T1' } 'Blocked without reasons accepted'
$bad=New-Card; $bad.commits=@('123abc'); Assert-Rejected { Read-AgentCard ($bad | ConvertTo-Json -Depth 20) 'T1' } 'Short SHA accepted'
$manifest=@{runId='run-1';task='T1';base=('a'*40)}
$state=@{status='running';finished=$null;exitCode=0;head=('a'*40);dirty='';commits=''}
$one=Join-Path $testRoot 'one'; $log=New-Protocol $one $card
Assert-Rejected { Publish-AgentDelivery $one $manifest $state $log } 'Running task published'
Assert-True (-not (Test-Path (Join-Path $one 'delivery.json'))) 'Premature receipt exists'
$state.status='process_exited'; $state.finished=[DateTime]::UtcNow.ToString('o')
Assert-True ((Publish-AgentDelivery $one $manifest $state $log) -eq 'completed_pending_review') 'Completed valid card not queued'
$receipt=Get-Content (Join-Path $one 'delivery.json') -Raw | ConvertFrom-Json -AsHashtable
Assert-True ($receipt.sessionId -eq 'session-test' -and $receipt.card.status -eq 'module_ready') 'Identity/status lost'
Assert-True ($receipt.status -eq 'unreviewed') 'Receipt promoted to acceptance'
$two=Join-Path $testRoot 'two'; $log2=New-Protocol $two $card
$other=@{runId='run-2';task='T1';base=('a'*40)}
Assert-True ((Publish-AgentDelivery $two $other $state $log2) -eq 'completed_pending_review') 'Second delivery cannot queue'
Assert-True ((Test-Path (Join-Path $one 'delivery.json')) -and (Test-Path (Join-Path $two 'delivery.json'))) 'Accumulation overwrote delivery'
$failed=Join-Path $testRoot 'failed'; $failedLog=New-Protocol $failed $card
$state.exitCode=1
Assert-True ((Publish-AgentDelivery $failed $manifest $state $failedLog) -eq 'failed_or_missing_card') 'Failed process accepted despite JSON card'
Assert-True (-not (Test-Path (Join-Path $failed 'delivery.json'))) 'Failed process has receipt'
$state.exitCode=0
$broken=Join-Path $testRoot 'broken'; [IO.Directory]::CreateDirectory($broken) | Out-Null
$brokenLog=Join-Path $broken 'stdout.log'; '{"type":"session","id":"x"}' | Set-Content $brokenLog
Assert-True ((Publish-AgentDelivery $broken $manifest $state $brokenLog) -eq 'failed_or_missing_card') 'No terminal message accepted'
$truncated=Join-Path $testRoot 'truncated.log'; '{"type":' | Set-Content $truncated
Assert-Rejected { Get-OmpFinalReply $truncated } 'Truncated JSON stream accepted'
$toolLog=Join-Path $testRoot 'tool.log'
@(@{type='session';id='s'},@{type='agent_end';isTerminal=$true;messages=@(@{role='assistant';stopReason='stop';content=@(@{type='toolCall';name='write'})})}) | ForEach-Object { $_ | ConvertTo-Json -Depth 20 -Compress } | Set-Content $toolLog
Assert-Rejected { Get-OmpFinalReply $toolLog } 'Outstanding tool call accepted'
$promptOnly=Join-Path $testRoot 'prompt-only'; [IO.Directory]::CreateDirectory($promptOnly) | Out-Null
$promptLog=Join-Path $promptOnly 'stdout.log'
@(@{type='session';id='s'},@{type='agent_end';isTerminal=$true;messages=@(@{role='user';content=@(@{type='text';text=$json})},@{role='assistant';stopReason='stop';content=@(@{type='text';text='没有交付卡，也不能标为已整合'})})}) | ForEach-Object { $_ | ConvertTo-Json -Depth 20 -Compress } | Set-Content $promptLog
Assert-True ((Publish-AgentDelivery $promptOnly $manifest $state $promptLog) -eq 'failed_or_missing_card') 'Prompt card or keyword treated as final delivery'
$review=@{schemaVersion=1;type='review.decision';runId='run-1';taskId='T1';reviewedHead=('a'*40);decision='accept';integrationStatus='pending_wiring';mainSha=$null;evidence=@('fixture boundary verified');findings=@();nextAction='尚未整合，由M接线'}
$state.status='completed_pending_review'
Publish-AgentReview $one $manifest $state $review
Assert-True ((Get-Content (Join-Path $one 'review.json') -Raw | ConvertFrom-Json).decision -eq 'accept') 'Structured decision affected by prose'
Publish-AgentReview $one $manifest $state $review
Assert-True (@(Get-ChildItem (Join-Path $one 'events') -Filter '05.json').Count -eq 1) 'Duplicate review event'
$wrong=$review.Clone(); $wrong.reviewedHead='b'*40; Assert-Rejected { Publish-AgentReview $one $manifest $state $wrong } 'Stale HEAD review accepted'
$wrong=$review.Clone(); $wrong.decision='已整合'; Assert-Rejected { Publish-AgentReview $one $manifest $state $wrong } 'Free-form decision accepted'
$wrong=$review.Clone(); $wrong.decision='rework'; Assert-Rejected { Publish-AgentReview $one $manifest $state $wrong } 'Rework without findings accepted'
$wrong=$review.Clone(); $wrong.integrationStatus='integrated'; Assert-Rejected { Publish-AgentReview $one $manifest $state $wrong } 'Integrated without main SHA accepted'
$wrong=$review.Clone(); $wrong.nextAction='different'; Assert-Rejected { Publish-AgentReview $one $manifest $state $wrong } 'Conflicting review overwrote history'
$payloadOne=[ordered]@{head='a';status='pending'}; $payloadTwo=[ordered]@{status='pending';head='a'}
$replay=Join-Path $testRoot 'replay'
Write-AgentEvent $replay $manifest 'agent.exited' $payloadOne
Write-AgentEvent $replay $manifest 'agent.exited' $payloadTwo
Assert-True (@(Get-ChildItem (Join-Path $replay 'events')).Count -eq 1) 'Property order broke idempotence'
if ($RealProtocolLog) {
    $actual=Get-OmpFinalReply $RealProtocolLog
    Assert-True ((Read-AgentCard $actual.text 'EVENT-PROBE').status -eq 'incomplete') 'Real OMP terminal stream not parsed'
}
if ($IntegrationWorktree) {
    $head=(& git -C $IntegrationWorktree rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Fixture git read failed' }
    $legacy=Join-Path $testRoot 'legacy'; [IO.Directory]::CreateDirectory($legacy) | Out-Null
    Save-AgentJson (Join-Path $legacy 'manifest.json') @{task='LEGACY-FIXTURE';worktree=$IntegrationWorktree;base=$head;transportTest=$true}
    Save-AgentJson (Join-Path $legacy 'state.json') @{status='completed_pending_review';finished=[DateTime]::UtcNow.ToString('o');head=$head;exitCode=0}
    Save-AgentJson (Join-Path $legacy 'delivery.json') @{cardLog='stdout.log';status='unreviewed'}
    & pwsh -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'agent-queue.ps1') -Mode ConvertLegacy -JobDirectory $legacy
    Assert-True ($LASTEXITCODE -eq 0) 'Legacy conversion failed'
    $converted=Get-Content (Join-Path $legacy 'delivery.json') -Raw | ConvertFrom-Json
    Assert-True ($converted.legacy -and $converted.requiresManualCard -and $null -eq $converted.card) 'Legacy text fabricated into verified card'
    & pwsh -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'agent-queue.ps1') -Mode ConvertLegacy -JobDirectory $legacy
    Assert-True ($LASTEXITCODE -eq 0 -and @(Get-ChildItem (Join-Path $legacy 'events')).Count -eq 2) 'Legacy replay duplicated events'
    $running=Join-Path $testRoot 'running-legacy'; [IO.Directory]::CreateDirectory($running) | Out-Null
    Save-AgentJson (Join-Path $running 'manifest.json') @{task='LEGACY-RUNNING';worktree=$IntegrationWorktree}
    Save-AgentJson (Join-Path $running 'state.json') @{status='running'}
    & pwsh -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'agent-queue.ps1') -Mode ConvertLegacy -JobDirectory $running 2>$null
    Assert-True ($LASTEXITCODE -ne 0 -and -not (Test-Path (Join-Path $running 'events'))) 'Running legacy task converted'
}
"PASS: $script:passed assertions; fixtures: $testRoot"
