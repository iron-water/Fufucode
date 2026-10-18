# Real lowest-cost worker/reviewer test. All mutations are in a new temporary repository.
[CmdletBinding()]
param([string]$Root, [string]$CodexExecutable, [switch]$FixedManager, [string]$BadDeliveryJob='bad-01',
    [ValidateSet('Setup','InitializeManager','LaunchBad','LaunchGood','Dispatch','Resume','Assert')][string]$Mode='Setup')
$ErrorActionPreference='Stop'
$source=Split-Path $PSScriptRoot -Parent
if (-not $Root) { $Root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-gate-e2e-'+[guid]::NewGuid().ToString('N')) }
$Root=[IO.Path]::GetFullPath($Root)
function Fixture-Git($Directory,$Arguments) { & git.exe -C $Directory @Arguments; if($LASTEXITCODE){throw 'Fixture git failed'} }
if($Mode -eq 'Setup') {
    if(Test-Path -LiteralPath $Root){throw 'Fixture root already exists'}
    New-Item -ItemType Directory -Path "$Root/scripts","$Root/docs/tasks","$Root/tmp/agent-queue" -Force | Out-Null
    Copy-Item -LiteralPath "$PSScriptRoot/agent-queue.ps1","$PSScriptRoot/agent-events.ps1","$PSScriptRoot/agent-gate.ps1","$PSScriptRoot/agent-manager.ps1","$PSScriptRoot/agent-manager-control.ps1" -Destination "$Root/scripts"
    Copy-Item -LiteralPath "$source/docs/tasks/agent-event-protocol.md","$source/docs/tasks/unattended-development.md","$source/docs/tasks/execution-contract.md" -Destination "$Root/docs/tasks"
    Set-Content -LiteralPath "$Root/AGENTS.md" -Encoding utf8 -Value '隔离机制夹具。仅本目录及其worktree。审核不得改源码。审核按 acceptance.md 独立执行验证，使用 Review 入口。测试模型显式 devin/swe-2，不派子代理。module_ready 是有效交付，不集成，不 push。'
    Set-Content -LiteralPath "$Root/.gitignore" -Value "tmp/`n"
    Set-Content -LiteralPath "$Root/acceptance.md" -Encoding utf8 -Value 'BAD 和 GOOD 任务验收相同：answer.txt 内容必须精确为42。独立用 Get-Content 核对文件及 git diff 起点..HEAD 范围，仅 answer.txt 可改变。内容41是明确缺陷，必须 rework；内容42且验证通过必须 accept，module_ready 不能因待接线打回。审核可在 tmp/agent-gate 写验证日志和决定输入，执行 scripts/agent-queue.ps1 -Mode Review 落盘。'
    Fixture-Git $Root @('init','-b','main')
    Fixture-Git $Root @('config','user.name','Gate Fixture'); Fixture-Git $Root @('config','user.email','gate-fixture@localhost')
    Fixture-Git $Root @('add','.'); Fixture-Git $Root @('commit','-m','isolated event gate fixture')
    Fixture-Git $Root @('worktree','add','-b','fixture/bad',"$Root/bad")
    Fixture-Git $Root @('worktree','add','-b','fixture/good',"$Root/good")
    # Worktree directories are untracked in the fixture main; no production files touched.
    Add-Content -LiteralPath "$Root/.git/info/exclude" -Value "bad/`ngood/"
    Write-Output "Fixture: $Root"
    exit
}
if($Mode -eq 'InitializeManager') {
    & pwsh.exe -NoProfile -File "$Root/scripts/agent-manager-control.ps1" -Mode Initialize -Repository $Root -Model 'devin/swe-2' -CodexExecutable $CodexExecutable
    if($LASTEXITCODE){throw 'Fixed manager initialization failed'}
    exit
}
if($Mode -in @('LaunchBad','LaunchGood','Resume')) {
    $task=if($Mode -eq 'LaunchGood'){'GOOD'}else{'BAD'}
    $job=if($Mode -eq 'Resume'){'bad-02'}elseif($task -eq 'BAD'){'bad-01'}else{'good-01'}
    $value=if($Mode -eq 'LaunchBad'){'41'}else{'42'}
    $prompt=Join-Path $Root "tmp/$job.md"
    $text="任务 $task，机制测试模型 devin/swe-2。你在隔离worktree内，不切主区。不读其他任务。用工具将 answer.txt 写为 $value，使用 git add answer.txt 和 git commit 提交，只改变answer.txt。不能push。此为有意测试夹具，不要纠正本轮指定数值。独立读回并记录事实，最终用真实提交SHA生成delivery.card，status=module_ready，integrationNeeds写由M负责共享接线。"
    $params=@{Mode='Launch';JobDirectory="$Root/tmp/agent-queue/$job";Worktree="$Root/$($task.ToLowerInvariant())";PromptFile=$prompt;Model='devin/swe-2';TaskId=$task;MechanismTest=$true;MaxTime='3m'}
    if($Mode -eq 'Resume') {
        $prior="$Root/tmp/agent-queue/$BadDeliveryJob"
        $review=Get-Content "$prior/review.json" -Raw | ConvertFrom-Json
        if($review.decision -ne 'rework'){throw 'Original job has no explicit rework'}
        $sessions=@(Get-ChildItem "$prior/sessions" -Filter '*.jsonl')
        if($sessions.Count -eq 0) {
            $priorManifest=Get-Content "$prior/manifest.json" -Raw|ConvertFrom-Json
            if($priorManifest.resumeSession){$sessions=@(Get-Item -LiteralPath $priorManifest.resumeSession)}
        }
        if($sessions.Count -ne 1){throw 'Ambiguous original saved session'}
        $params.ResumeSession=$sessions[0].FullName
        $text="原任务BAD修复，原模型devin/swe-2，原工作树原保存会话。先读审核 $prior/review.json 和 acceptance.md。将answer.txt改为42，独立读回，仅提交answer.txt。最终delivery.card用真实本轮SHA，module_ready，待M接线；不得重建worktree，不夹带其他修改。"
    }
    Set-Content -LiteralPath $prompt -Value $text -Encoding utf8
    & "$Root/scripts/agent-queue.ps1" @params
    exit
}
if($Mode -eq 'Dispatch') {
    if(-not $CodexExecutable){throw 'Explicit native executable required'}
    & "$Root/scripts/agent-gate.ps1" -Repository $Root -Once -Dispatch -Model 'devin/swe-2' -CodexExecutable $CodexExecutable -ReviewTimeoutSeconds 300 -FixedManager:$FixedManager
    exit
}
if($Mode -eq 'Assert') {
    foreach($pair in @(@($BadDeliveryJob,'rework'),@('good-01','accept'),@('bad-02','accept'))) {
        $job="$Root/tmp/agent-queue/$($pair[0])"
        $review=Get-Content "$job/review.json" -Raw | ConvertFrom-Json
        if($review.decision -cne $pair[1]){throw "Unexpected decision for $($pair[0])"}
        $claim=Get-Content "$Root/tmp/agent-gate/$($review.runId).dispatch.json" -Raw|ConvertFrom-Json
        if($claim.status -ne 'decided' -or $claim.reviewedHead -cne $review.reviewedHead){throw 'Review was not completed by event dispatch'}
    }
    $a=Get-Content "$Root/tmp/agent-queue/$BadDeliveryJob/delivery.json" -Raw | ConvertFrom-Json
    $b=Get-Content "$Root/tmp/agent-queue/bad-02/delivery.json" -Raw | ConvertFrom-Json
    if($a.sessionId -ne $b.sessionId){throw 'Repair did not preserve saved session identity'}
    if(-not $CodexExecutable){throw 'Assert requires explicit native CodexExecutable to test replay'}
    $outbox=Join-Path $Root 'tmp/agent-gate'
    $transcripts=@(Get-ChildItem $outbox -Filter '*.stdout.jsonl')
    if($transcripts.Count -ne 3){throw 'Expected exactly three actual reviewer invocations'}
    $before=@($transcripts | ForEach-Object {@{path=$_.FullName;sha256=(Get-FileHash $_.FullName).Hash}})
    $replayArgs=@('-NoProfile','-File',"$Root/scripts/agent-gate.ps1",'-Repository',$Root,'-Once','-Dispatch','-Model','devin/swe-2','-CodexExecutable',$CodexExecutable)
    if($FixedManager){$replayArgs+='-FixedManager'}
    & pwsh.exe @replayArgs
    if($LASTEXITCODE){throw 'Replay gate failed'}
    foreach($file in $before){if((Get-FileHash $file.path).Hash -ne $file.sha256){throw 'Replay changed model transcript'}}
    if(@(Get-ChildItem $outbox -Filter '*.dispatch.json').Count -ne 3){throw 'Replay invoked a second reviewer'}
    $fingerprints=@{}
    foreach($name in @('agent-gate.ps1','agent-queue.ps1','agent-events.ps1','agent-manager.ps1','agent-manager-control.ps1')) {
        $fixtureHash=(Get-FileHash "$Root/scripts/$name").Hash
        if($fixtureHash -ne (Get-FileHash "$PSScriptRoot/$name").Hash){throw "Fixture code drift: $name"}
        $fingerprints[$name]=$fixtureHash
    }
    $fixedThreadId=$null
    if($FixedManager) {
        $manager=Get-Content "$Root/tmp/agent-gate/manager.json" -Raw | ConvertFrom-Json
        if($manager.status -ne 'ready'){throw 'Fixed manager did not settle ready'}
        $fixedThreadId=$manager.threadId
        foreach($transcript in $transcripts) {
            $ids=@([IO.File]::ReadLines($transcript.FullName) | ForEach-Object {$j=$_|ConvertFrom-Json; if($j.type -eq 'thread.started'){$j.thread_id}})
            if($ids.Count -ne 1 -or $ids[0] -cne $fixedThreadId){throw 'A delivery was sent to another thread'}
        }
    }
    @{schemaVersion=1;scope='event-review-only';model='devin/swe-2';reviewLoopVerified=$true;replayAndEmptyQueueVerified=$true;fixedManagerVerified=[bool]$FixedManager;fixedManagerThreadId=$fixedThreadId;originalSessionId=$a.sessionId;evidenceRoot=$Root;scripts=$fingerprints;verifiedAt=[DateTime]::UtcNow.ToString('o');transcripts=$before} |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$Root/tmp/validation.json" -Encoding utf8
    Write-Output 'PASS: real worker commits, structured delivery, independent rework/accept, original saved session repair and redelivery.'
}
