# Harness-neutral managed worker. Models and sessions are always explicit.
[CmdletBinding()]
param([ValidateSet('Launch','Worker')][string]$Mode='Launch',[string]$Repository,[string]$JobDirectory,
    [string]$Worktree,[string]$PromptFile,[string]$TaskId,[string]$Model,
    [ValidateSet('omp','codex')][string]$Harness='omp',[string]$ResumeSession,
    [string[]]$AllowedPaths,[int]$TimeoutSeconds=7200,[switch]$MechanismTest,[switch]$InheritTaskChanges,
    [string]$WorkflowId,[int]$Attempt=1,[switch]$FormatOnly,
    [string]$CodexExecutable='C:/Users/mio/AppData/Roaming/npm/node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe',
    [string]$OmpExecutable='C:/Users/mio/.bun/bin/omp.exe')
$ErrorActionPreference='Stop'
. "$PSScriptRoot/agent-events.ps1"
function Executor-Git($Directory,$Arguments) {
    $out=& git.exe -c core.quotepath=false -C $Directory @Arguments;if($LASTEXITCODE){throw "Git failed: $($Arguments -join ' ')"};return ($out -join "`n")
}
function Read-CodexFinal($Path) {
    $thread=$null;$text=$null;$terminal=$null
    foreach($line in [IO.File]::ReadLines($Path)) {
        $e=$line|ConvertFrom-Json -AsHashtable
        if($e.type -eq 'thread.started'){$thread=$e.thread_id}
        if($e.type -eq 'item.completed' -and $e.item.type -eq 'agent_message'){$text=$e.item.text}
        if($e.type -in @('turn.completed','turn.failed')){$terminal=$e.type}
    }
    if(-not $thread -or -not $text -or $terminal -ne 'turn.completed'){throw 'Missing actual terminal Codex reply'}
    return @{sessionId=$thread;text=$text}
}
if($Mode -eq 'Launch') {
    if(-not $Repository -or -not $Worktree -or -not $PromptFile -or -not $TaskId -or -not $Model -or -not $AllowedPaths){throw 'Explicit repository/task/model/prompt/scope required'}
    if($MechanismTest -and $Model -cne 'devin/swe-2'){throw 'Mechanism test model must be devin/swe-2'}
    $Repository=(Resolve-Path -LiteralPath $Repository).Path;$Worktree=(Resolve-Path -LiteralPath $Worktree).Path
    if((Executor-Git $Repository @('rev-parse','--git-common-dir')) -ne (Executor-Git $Worktree @('rev-parse','--git-common-dir'))) {
        # Main's relative .git and worktree's absolute common dir need canonical comparison.
        $mainCommon=(Resolve-Path (Join-Path $Repository (Executor-Git $Repository @('rev-parse','--git-common-dir')))).Path
        $workerCommon=(Resolve-Path (Executor-Git $Worktree @('rev-parse','--git-common-dir'))).Path
        if($mainCommon -ne $workerCommon){throw 'Worker belongs to another repository'}
    }
    $inherited=Executor-Git $Worktree @('status','--porcelain')
    if($inherited) {
        if(-not $ResumeSession -or -not $InheritTaskChanges){throw 'Worker requires clean worktree; never inherit unclassified changes'}
        foreach($line in $inherited -split "`n") {
            $file=$line.Substring(3).Replace('\','/')
            $owned=@($AllowedPaths|Where-Object {$file -like $_ -or ($file.EndsWith('/') -and $_.StartsWith($file))})
            if(-not $owned.Count){throw "Inherited file outside task: $file"}
        }
    }
    if(Test-Path -LiteralPath $JobDirectory){throw 'Never overwrite an existing run'}
    if($ResumeSession -and $Harness -eq 'omp'){$ResumeSession=(Resolve-Path -LiteralPath $ResumeSession).Path}
    [IO.Directory]::CreateDirectory("$JobDirectory/sessions")|Out-Null
    Copy-Item -LiteralPath $PromptFile -Destination "$JobDirectory/prompt.md"
    Add-Content "$JobDirectory/prompt.md" '最终必须只输出单个纯JSON delivery.card，不能前言/后记。字段：schemaVersion=1,type=delivery.card,taskId,status=module_ready/submitted/blocked/incomplete,summary,commits（只列本轮base之后完整SHA）,changedFiles,verification{passed,failed,notRun},evidence,integrationNeeds,blockers,writerStopped=true。只写授权文件，不集成main、不push，不自动升级模型。'
    Set-Content "$JobDirectory/omp.yml" "modelRoles:`n  default: $Model`n  task: $Model`n  smol: $Model`n  slow: $Model`n  plan: $Model`n"
    $m=@{schemaVersion=1;runId=[guid]::NewGuid().ToString('N');repository=$Repository;task=$TaskId;worktree=$Worktree;base=(Executor-Git $Worktree @('rev-parse','HEAD'));model=$Model;harness=$Harness;protocol=if($Harness -eq 'omp'){'omp-json-v1'}else{'codex-json-v1'};resumeSession=$ResumeSession;inheritedDirty=$inherited;allowedPaths=$AllowedPaths;timeoutSeconds=$TimeoutSeconds;mechanismTest=[bool]$MechanismTest;codex=$CodexExecutable;omp=$OmpExecutable}
    $m.workflowId=$WorkflowId;$m.attempt=$Attempt;$m.formatOnly=[bool]$FormatOnly
    Save-AgentJson "$JobDirectory/manifest.json" $m
    Write-AgentEvent $JobDirectory $m 'job.created' @{worktree=$Worktree;model=$Model;base=$m.base;harness=$Harness}
    Save-AgentJson "$JobDirectory/state.json" @{status='starting';task=$TaskId;runId=$m.runId;model=$Model;worktree=$Worktree}
    $psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=(Get-Command pwsh.exe).Source;$psi.WorkingDirectory=$Repository;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
    foreach($arg in @('-NoProfile','-File',$PSCommandPath,'-Mode','Worker','-JobDirectory',[IO.Path]::GetFullPath($JobDirectory))){$psi.ArgumentList.Add($arg)}
    $p=[Diagnostics.Process]::Start($psi);$id=$p.Id
    Save-AgentJson "$JobDirectory/worker-binding.json" @{pid=$id;started=$p.StartTime.ToUniversalTime().ToString('o')}
    $p.Dispose()
    @{workerPid=$id;runId=$m.runId;job=$JobDirectory}|ConvertTo-Json;exit
}
$m=Get-Content "$JobDirectory/manifest.json" -Raw|ConvertFrom-Json -AsHashtable
$key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($m.worktree.ToLowerInvariant())))
$mutex=[Threading.Mutex]::new($false,"Local\FufucodeAgent-$key");$owns=$mutex.WaitOne(0)
if(-not $owns){throw 'Existing managed writer; do not start another worker'}
$state=@{schemaVersion=1;runId=$m.runId;task=$m.task;model=$m.model;worktree=$m.worktree;workerPid=$PID;workerStarted=(Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o');status='running';started=[DateTime]::UtcNow.ToString('o')}
$p=$null
try {
    Save-AgentJson "$JobDirectory/state.json" $state
    $psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=if($m.harness -eq 'omp'){$m.omp}else{$m.codex}
    $psi.WorkingDirectory=$m.worktree;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
    $psi.RedirectStandardInput=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
    if($m.harness -eq 'omp') {
        $args=@('--print','--mode','json','--model',$m.model,'--smol',$m.model,'--slow',$m.model,'--plan',$m.model,'--config',"$JobDirectory/omp.yml",'--session-dir',"$JobDirectory/sessions",'--no-title','--max-time',"$($m.timeoutSeconds)s",'--no-extensions','--auto-approve')
        if($m.mechanismTest){$args+=@('--thinking','minimal')}
        if($m.resumeSession){$args+=@('--resume',$m.resumeSession)};$args+=('@'+"$JobDirectory/prompt.md")
    }else{
        $args=@('--no-daemon','exec','-m',$m.model,'-C',$m.worktree,'-s','danger-full-access','-c','approval_policy="never"','--json')
        if($m.resumeSession){$args+=@('resume',$m.resumeSession)};$args+='-'
    }
    foreach($arg in $args){$psi.ArgumentList.Add([string]$arg)}
    $p=[Diagnostics.Process]::Start($psi);$state.agentPid=$p.Id;$state.agentStarted=$p.StartTime.ToUniversalTime().ToString('o');Save-AgentJson "$JobDirectory/state.json" $state
    Write-AgentEvent $JobDirectory $m 'agent.started' @{agentPid=$p.Id;workerPid=$PID;started=$state.started}
    $out=[IO.File]::Open("$JobDirectory/stdout.log",'CreateNew','Write','Read');$err=[IO.File]::Open("$JobDirectory/stderr.log",'CreateNew','Write','Read')
    try {
        $a=$p.StandardOutput.BaseStream.CopyToAsync($out);$b=$p.StandardError.BaseStream.CopyToAsync($err)
        if($m.harness -eq 'codex'){$p.StandardInput.Write((Get-Content "$JobDirectory/prompt.md" -Raw))};$p.StandardInput.Close()
        if(-not $p.WaitForExit($m.timeoutSeconds*1000)){$p.Kill($true);$p.WaitForExit();$state.watchdogTimeout=$true}
        [void]$a.GetAwaiter().GetResult();[void]$b.GetAwaiter().GetResult()
    }finally{$out.Dispose();$err.Dispose()}
    $state.exitCode=$p.ExitCode;$state.finished=[DateTime]::UtcNow.ToString('o');$state.status='process_exited'
    $state.head=Executor-Git $m.worktree @('rev-parse','HEAD');$state.dirty=Executor-Git $m.worktree @('status','--porcelain');$state.commits=Executor-Git $m.worktree @('log','--format=%H %s',"$($m.base)..HEAD")
    Write-AgentEvent $JobDirectory $m 'agent.exited' @{exitCode=$state.exitCode;head=$state.head;finished=$state.finished;watchdogTimeout=[bool]$state.watchdogTimeout}
    try {
        if($state.exitCode -ne 0 -or $state.watchdogTimeout){throw 'Harness failed or timed out'}
        $changed=@((Executor-Git $m.worktree @('diff','--name-only',$m.base,$state.head)) -split "`n"|Where-Object {$_})
        foreach($file in $changed){if(-not @($m.allowedPaths|Where-Object {$file -like $_}).Count){throw "Worker exceeded scope: $file"}}
        $reply=if($m.harness -eq 'omp'){Get-OmpFinalReply "$JobDirectory/stdout.log"}else{Read-CodexFinal "$JobDirectory/stdout.log"}
        if($m.harness -eq 'codex' -and $m.resumeSession -and $reply.sessionId -cne $m.resumeSession){throw 'Codex repair changed thread'}
        $card=Read-AgentCard $reply.text $m.task
        $commits=@($state.commits -split "`n"|Where-Object {$_}|ForEach-Object {($_ -split ' ')[0]})
        foreach($sha in $card.commits){if($sha -notin $commits){throw 'Commit outside run'}}
        Save-AgentJson "$JobDirectory/delivery.json" @{schemaVersion=1;runId=$m.runId;taskId=$m.task;sessionId=$reply.sessionId;base=$m.base;head=$state.head;commits=$commits;dirty=$state.dirty;card=$card;status='unreviewed';finished=$state.finished}
        Write-AgentEvent $JobDirectory $m 'delivery.received' @{head=$state.head;sessionId=$reply.sessionId;cardStatus=$card.status;receipt='delivery.json';legacy=$false}
        $state.status='completed_pending_review'
    }catch{Write-AgentEvent $JobDirectory $m 'delivery.invalid' @{reason=$_.Exception.Message;exitCode=$state.exitCode;head=$state.head};$state.status='failed_or_missing_card'}
    Save-AgentJson "$JobDirectory/state.json" $state
}catch{
    $failure=@{status='supervisor_error';error=$_.Exception.Message;agentPid=$state.agentPid;workerPid=$PID;runId=$m.runId;task=$m.task;agentStillRunning=[bool]($p -and -not $p.HasExited)}
    Save-AgentJson "$JobDirectory/state.json" $failure;Write-AgentEvent $JobDirectory $m 'supervisor.failed' $failure;throw
}finally{if($p){$p.Dispose()};$mutex.ReleaseMutex();$mutex.Dispose()}
