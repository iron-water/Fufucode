# Requires PowerShell 7. Queue receipts are emitted only after OMP exits.
[CmdletBinding()]
param(
    [ValidateSet('Launch','Worker','Status','Events','Review','ConvertLegacy')][string]$Mode = 'Status',
    [string]$JobDirectory,
    [string]$Worktree,
    [string]$PromptFile,
    [string]$Model,
    [string]$TaskId,
    [string]$ReviewFile,
    [string]$OmpExecutable = (Join-Path $env:USERPROFILE '.bun/bin/omp.exe'),
    [switch]$Probe,
    [switch]$TransportTest,
    [switch]$MechanismTest,
    [string]$ResumeSession,
    [switch]$InheritTaskChanges,
    [string]$MaxTime = '2h'
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required' }
. (Join-Path $PSScriptRoot 'agent-events.ps1')
. (Join-Path $PSScriptRoot 'agent-human.ps1')
function Save-Json($Path, $Value) {
    Save-AgentJson $Path $Value
}
function Git-Text([string]$Directory, [string[]]$Arguments) {
    $result = & git -C $Directory @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $($Arguments -join ' ')" }
    return ($result -join "`n")
}
if (-not $JobDirectory) { throw 'Specify JobDirectory' }
$JobDirectory = [IO.Path]::GetFullPath($JobDirectory)
if ($Mode -eq 'Events') {
    Get-ChildItem -LiteralPath $JobDirectory -Filter '*.json' -Recurse | Where-Object { $_.Directory.Name -eq 'events' } |
        Sort-Object FullName | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
    exit 0
}
if ($Mode -in @('Review','ConvertLegacy')) {
    if($Mode -eq 'Review' -and (Test-Path (Join-Path $JobDirectory 'revoked.json'))){throw 'Run revoked; no review decision or acceptance'}
    $manifest = Get-Content -LiteralPath (Join-Path $JobDirectory 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
    $state = Get-Content -LiteralPath (Join-Path $JobDirectory 'state.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($state.status -in @('starting','running') -or -not $state.finished) { throw 'Agent must finish before review or conversion' }
    foreach ($role in @('worker','agent')) { if (Test-AgentProcessIdentity $state $role) { throw 'Recorded writer process is still present; inspect process identity before proceeding' } }
    $worktreeKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($manifest.worktree.ToLowerInvariant())))
    $reviewMutex = [Threading.Mutex]::new($false, "Local\FufucodeAgent-$worktreeKey")
    $ownsReview = $reviewMutex.WaitOne(0)
    try {
        if (-not $ownsReview) { throw 'Worktree has an active managed writer' }
        if ((Git-Text $manifest.worktree @('rev-parse','HEAD')) -ne $state.head) { throw 'Worktree HEAD changed since delivery' }
        if ($Mode -eq 'ConvertLegacy') {
            if ($manifest.protocol -eq 'omp-json-v1') { throw 'Already uses structured protocol' }
            if (-not $manifest.runId) { $manifest.runId = 'legacy-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($JobDirectory.ToLowerInvariant()))).Substring(0,24) }
            $manifest.protocol = 'legacy-text'
            Save-Json (Join-Path $JobDirectory 'manifest.json') $manifest
            Write-AgentEvent $JobDirectory $manifest 'agent.exited' @{exitCode=$state.exitCode;head=$state.head;finished=$state.finished;legacy=$true}
            if ($state.status -eq 'completed_pending_review') {
                $old = Get-Content -LiteralPath (Join-Path $JobDirectory 'delivery.json') -Raw | ConvertFrom-Json -AsHashtable
                $receipt = [ordered]@{schemaVersion=1;runId=$manifest.runId;taskId=$manifest.task;base=$manifest.base;head=$state.head;status='unreviewed';card=$null;legacy=$true;requiresManualCard=$true;cardLog=$old.cardLog;finished=$state.finished}
                Save-AgentJson (Join-Path $JobDirectory 'delivery.json') $receipt
                Write-AgentEvent $JobDirectory $manifest 'delivery.received' @{head=$state.head;cardStatus='legacy_unparsed';receipt='delivery.json';legacy=$true}
            } else { Write-AgentEvent $JobDirectory $manifest 'delivery.invalid' @{reason='Legacy process failed or card missing';exitCode=$state.exitCode;head=$state.head} }
        } else {
            if (-not $ReviewFile) { throw 'ReviewFile required' }
            $review = Get-Content -LiteralPath $ReviewFile -Raw | ConvertFrom-Json -AsHashtable
            $root=Split-Path (Split-Path (Split-Path $JobDirectory -Parent) -Parent) -Parent
            $humanLease=Open-HumanLease $root
            try{Assert-HumanReview $root $manifest.task $review;Publish-AgentReview $JobDirectory $manifest $state $review}finally{$humanLease.Dispose()}
        }
    } finally { if ($ownsReview) { $reviewMutex.ReleaseMutex() }; $reviewMutex.Dispose() }
    exit 0
}
if ($Mode -eq 'Status') {
    Get-ChildItem -LiteralPath $JobDirectory -Filter state.json -Recurse -ErrorAction SilentlyContinue |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
    exit 0
}
if ($Mode -eq 'Launch') {
    if (-not $TaskId -or -not $Model -or -not $Worktree -or -not $PromptFile) { throw 'Missing launch arguments' }
    if ($MechanismTest -and $Model -cne 'devin/swe-2') { throw 'MechanismTest requires explicit devin/swe-2' }
    if (-not $Probe -and -not $TransportTest -and -not $MechanismTest -and $Model -notmatch '^(magpie/)?group/') { throw 'Implementation model must be a group ID' }
    if (-not (Test-Path -LiteralPath $OmpExecutable)) { throw 'OMP executable missing' }
    $Worktree = (Resolve-Path -LiteralPath $Worktree).Path
    $PromptFile = (Resolve-Path -LiteralPath $PromptFile).Path
    $base = Git-Text $Worktree @('rev-parse','HEAD')
    $dirty = Git-Text $Worktree @('status','--porcelain')
    if ($dirty -and -not ($ResumeSession -and $InheritTaskChanges)) { throw 'Use a clean isolated worktree; repair may explicitly inherit this managed task changes' }
    if ($ResumeSession) { $ResumeSession = (Resolve-Path -LiteralPath $ResumeSession).Path }
    if (Test-Path -LiteralPath $JobDirectory) { throw 'Job directory already exists; never overwrite a prior run' }
    New-Item -ItemType Directory -Path $JobDirectory | Out-Null
    Copy-Item -LiteralPath $PromptFile -Destination (Join-Path $JobDirectory 'prompt.md')
    if (-not $Probe) {
        $example = @{schemaVersion=1;type='delivery.card';taskId=$TaskId;status='incomplete';summary='填写真实交付摘要';commits=@();changedFiles=@();verification=@{passed=@();failed=@();notRun=@()};evidence=@();integrationNeeds=@();blockers=@();writerStopped=$true} | ConvertTo-Json -Depth 10 -Compress
        $instructions = "`n受管交付协议v1：任务结束后最终回复只能输出一个JSON对象（可用单个json代码围栏），不能在最终回复加其他正文。仍在任务记录写人类交付卡。对象格式如下：`n$example`nstatus只允许module_ready/submitted/blocked/incomplete；模块待M接线用module_ready，不自称验收。所有数组填写真实事实，禁止复制示例冒称完成。commits只列本次运行起点之后的完整40位SHA；之前轮次证据放evidence。verification分别列真实通过/失败/未运行项。被阻塞须填blockers。停止写入并输出最终JSON后退出。"
        Add-Content -LiteralPath (Join-Path $JobDirectory 'prompt.md') -Value $instructions -Encoding utf8
    }
    $manifest = @{schemaVersion=1;runId=[guid]::NewGuid().ToString('N');protocol='omp-json-v1';task=$TaskId;worktree=$Worktree;model=$Model;base=$base;inheritedDirty=$dirty;resumeSession=$ResumeSession;omp=$OmpExecutable;probe=[bool]$Probe;transportTest=[bool]$TransportTest;mechanismTest=[bool]$MechanismTest;maxTime=$MaxTime}
    Save-Json (Join-Path $JobDirectory 'manifest.json') $manifest
    Write-AgentEvent $JobDirectory $manifest 'job.created' @{worktree=$Worktree;model=$Model;base=$base;probe=[bool]$Probe;transportTest=[bool]$TransportTest}
    Save-Json (Join-Path $JobDirectory 'state.json') @{schemaVersion=1;runId=$manifest.runId;task=$TaskId;status='starting';worktree=$Worktree;model=$Model}
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command pwsh).Source
    $start.WorkingDirectory = $Worktree
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in @('-NoLogo','-NoProfile','-NonInteractive','-File',$PSCommandPath,'-Mode','Worker','-JobDirectory',$JobDirectory)) { $start.ArgumentList.Add($argument) }
    $worker = [Diagnostics.Process]::Start($start)
    @{task=$TaskId;workerPid=$worker.Id;job=$JobDirectory} | ConvertTo-Json
    exit 0
}
$lock = [IO.File]::Open((Join-Path $JobDirectory 'worker.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    $manifest = Get-Content -LiteralPath (Join-Path $JobDirectory 'manifest.json') -Raw | ConvertFrom-Json
    $worktreeKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($manifest.worktree.ToLowerInvariant())))
    $writerMutex = [Threading.Mutex]::new($false, "Local\FufucodeAgent-$worktreeKey")
    $ownsWriter = $writerMutex.WaitOne(0)
    if (-not $ownsWriter) { throw 'Another managed worker owns this worktree' }
    $state = @{schemaVersion=1;runId=$manifest.runId;task=$manifest.task;status='running';worktree=$manifest.worktree;model=$manifest.model;workerPid=$PID;started=[DateTime]::UtcNow.ToString('o')}
    Save-Json (Join-Path $JobDirectory 'state.json') $state
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $manifest.omp
    $start.WorkingDirectory = $manifest.worktree
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $arguments = @('--print','--mode','json','--model',$manifest.model,'--session-dir',(Join-Path $JobDirectory 'sessions'),'--no-title','--max-time',$manifest.maxTime,'--no-extensions')
    if ($manifest.resumeSession) { $arguments += @('--resume',$manifest.resumeSession) }
    if ($manifest.probe -or $manifest.transportTest) { $arguments += @('--no-tools','--no-skills','--no-rules') }
    else { $arguments += '--auto-approve' }
    $arguments += ('@' + (Join-Path $JobDirectory 'prompt.md'))
    foreach ($argument in $arguments) { $start.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::Start($start)
    $state.agentPid = $process.Id
    Save-Json (Join-Path $JobDirectory 'state.json') $state
    Write-AgentEvent $JobDirectory $manifest 'agent.started' @{agentPid=$process.Id;workerPid=$PID;started=$state.started}
    # Concurrent stream copies avoid pipe deadlock and retain logs across supervisor loss.
    $stdout = [IO.File]::Open((Join-Path $JobDirectory 'stdout.log'), 'Create', 'Write', 'Read')
    $stderr = [IO.File]::Open((Join-Path $JobDirectory 'stderr.log'), 'Create', 'Write', 'Read')
    try {
        $outCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $errCopy = $process.StandardError.BaseStream.CopyToAsync($stderr)
        $watchdogMs = if ($manifest.probe -or $manifest.transportTest) { 180000 } else { 10800000 }
        if (-not $process.WaitForExit($watchdogMs)) {
            $process.Kill($true)
            $process.WaitForExit()
            $state.watchdogTimeout = $true
        }
        $outCopy.GetAwaiter().GetResult()
        $errCopy.GetAwaiter().GetResult()
    } finally { $stdout.Dispose(); $stderr.Dispose() }
    $state.exitCode = $process.ExitCode
    $state.finished = [DateTime]::UtcNow.ToString('o')
    $state.status = 'process_exited'
    $state.head = Git-Text $manifest.worktree @('rev-parse','HEAD')
    $state.dirty = Git-Text $manifest.worktree @('status','--porcelain')
    $state.commits = Git-Text $manifest.worktree @('log','--format=%H %s',"$($manifest.base)..HEAD")
    Write-AgentEvent $JobDirectory $manifest 'agent.exited' @{exitCode=$state.exitCode;head=$state.head;finished=$state.finished;watchdogTimeout=[bool]$state.watchdogTimeout}
    if ($manifest.probe) {
        $reply = Get-OmpFinalReply (Join-Path $JobDirectory 'stdout.log')
        $state.status = if ($process.ExitCode -eq 0 -and $reply.text -ceq 'QUEUE_PROBE_OK') {'probe_passed'} else {'failed'}
    } else { $state.status = Publish-AgentDelivery $JobDirectory $manifest $state (Join-Path $JobDirectory 'stdout.log') }
    Save-Json (Join-Path $JobDirectory 'state.json') $state
} catch {
    $supervisorError = $_.Exception.Message
    $agentStillRunning = $process -and -not $process.HasExited
    $failure = @{schemaVersion=1;runId=$manifest.runId;task=$manifest.task;status='supervisor_error';error=$supervisorError;workerPid=$PID;agentPid=$state.agentPid;agentStillRunning=[bool]$agentStillRunning}
    Save-Json (Join-Path $JobDirectory 'state.json') $failure
    if ($manifest.runId) { Write-AgentEvent $JobDirectory $manifest 'supervisor.failed' $failure }
    throw
} finally {
    if ($ownsWriter) { $writerMutex.ReleaseMutex() }
    if ($writerMutex) { $writerMutex.Dispose() }
    $lock.Dispose()
}
