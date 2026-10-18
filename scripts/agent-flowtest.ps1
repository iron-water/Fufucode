# Real cross-harness simulation. Nothing here is a production task dispatcher.
[CmdletBinding()]
param([ValidateSet('Setup','SetupWorkflow','Start','Driver','Worker','Status','Assert','AssertWorkflow','Stop')][string]$Mode='Status',
    [string]$Root,[string]$JobDirectory,
    [string]$CodexExecutable='C:/Users/mio/AppData/Roaming/npm/node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe',
    [string]$OmpExecutable='C:/Users/mio/.bun/bin/omp.exe')
$ErrorActionPreference='Stop'
$source=Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/agent-events.ps1"
. "$PSScriptRoot/agent-manager.ps1"
function Flow-Git($Directory,$Arguments) {
    $output=& git.exe -C $Directory @Arguments
    if($LASTEXITCODE){throw "Fixture git failed: $($Arguments -join ' ')"}
    return ($output -join "`n")
}
function Flow-Log($Type,$Data) {
    @{at=[DateTime]::UtcNow.ToString('o');type=$Type;data=$Data}|ConvertTo-Json -Depth 15 -Compress|Add-Content "$Root/tmp/flow/events.jsonl"
}
function Read-FlowJson($Path) {Read-AgentJson $Path}
function Flow-Native($Executable,$Arguments,$Directory,$Prefix,$Prompt,$Timeout=600) {
    $psi=[Diagnostics.ProcessStartInfo]::new()
    $psi.FileName=$Executable;$psi.WorkingDirectory=$Directory;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
    $psi.RedirectStandardInput=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
    foreach($argument in $Arguments){$psi.ArgumentList.Add([string]$argument)}
    $p=[Diagnostics.Process]::Start($psi)
    $stdout=[IO.File]::Open(($Prefix+'.stdout.jsonl'),'CreateNew','Write','Read')
    $stderr=[IO.File]::Open(($Prefix+'.stderr.log'),'CreateNew','Write','Read')
    try {
        $a=$p.StandardOutput.BaseStream.CopyToAsync($stdout);$b=$p.StandardError.BaseStream.CopyToAsync($stderr)
        $p.StandardInput.Write($Prompt);$p.StandardInput.Close()
        if(-not $p.WaitForExit($Timeout*1000)){$p.Kill($true);$p.WaitForExit();throw 'Fixture model timeout; no automatic retry'}
        [void]$a.GetAwaiter().GetResult();[void]$b.GetAwaiter().GetResult()
        return $p.ExitCode
    } finally {$stdout.Dispose();$stderr.Dispose();$p.Dispose()}
}
function Get-FlowCodexReply($Path) {
    $thread=$null;$text=$null;$terminal=$null
    foreach($line in [IO.File]::ReadLines($Path)) {
        $entry=$line|ConvertFrom-Json -AsHashtable
        if($entry.type -eq 'thread.started'){$thread=$entry.thread_id}
        if($entry.type -eq 'item.completed' -and $entry.item.type -eq 'agent_message'){$text=$entry.item.text}
        if($entry.type -in @('turn.completed','turn.failed')){$terminal=$entry.type}
    }
    if(-not $thread -or -not $text -or $terminal -ne 'turn.completed'){throw 'Missing actual Codex terminal reply'}
    return @{sessionId=$thread;text=$text}
}
if($Mode -eq 'Setup') {
    if(-not $Root){$Root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-flow-'+[guid]::NewGuid().ToString('N'))}
    if(Test-Path -LiteralPath $Root){throw 'Never overwrite a fixture'}
    foreach($dir in @('scripts','docs/tasks','.omp/agents','tmp/flow','tmp/agent-gate','tmp/agent-queue')){[IO.Directory]::CreateDirectory("$Root/$dir")|Out-Null}
    Copy-Item "$PSScriptRoot/agent-events.ps1","$PSScriptRoot/agent-queue.ps1","$PSScriptRoot/agent-gate.ps1","$PSScriptRoot/agent-manager.ps1","$PSScriptRoot/agent-manager-control.ps1","$PSScriptRoot/agent-human.ps1","$PSScriptRoot/agent-human-control.ps1" "$Root/scripts"
    Copy-Item "$source/docs/tasks/agent-event-protocol.md","$source/docs/tasks/execution-contract.md","$source/docs/tasks/unattended-development.md" "$Root/docs/tasks"
    Set-Content "$Root/AGENTS.md" '隔离无意义流程夹具。所有模型及子代理必须显式devin/swe-2。不访问真实项目、不push、不登录、不删除数据。任务A/B/C只写各自module标记，共享wire.ps1与main集成只由M。子代理仅做无意义标记；执行者收回子代理后输出完整纯JSON交付卡。M审核严格按acceptance.md，不将module_ready待接线当缺陷。'
    Set-Content "$Root/acceptance.md" 'A/B模块交付各自modules/a.txt或b.txt必须精确42；A初轮41必须rework。仅允许本任务对应文件，tmp子代理记录不提交。C依赖A和B已经main整合且wire.ps1实际运行输出84；C只允许modules/c.txt精确84。共享接线仅M写wire.ps1，脚本必须读取真实modules/a.txt与b.txt求和，不能硬编码84。审核只能请求指定run，不扫描/处理其他任务。accept不等于已集成，集成必须真实main提交SHA及独立wire执行通过。所有交付必须有真实子代理调用证据，A用OMP task工具，B/C用Codex spawn_agent，每个子代理显式devin/swe-2，结果需收回。'
    Set-Content "$Root/.gitignore" "tmp/`nworktrees/`n"
    Set-Content "$Root/.omp/agents/fixture-helper.md" "---`nname: fixture-helper`ndescription: 无意义夹具子代理，仅回复任务要求标记`nmodel: devin/swe-2`nspawns: []`n---`n仅执行父任务给出的无意义标记。不访问真实仓库，不改源码，不调用其他模型，不再派子代理。"
    Set-Content "$Root/tmp/flow/omp.yml" "modelRoles:`n  default: devin/swe-2`n  task: devin/swe-2`n  smol: devin/swe-2`n  slow: devin/swe-2`n  plan: devin/swe-2`n  taskPlan: devin/swe-2`n"
    [void](Flow-Git $Root @('init','-b','main'))
    [void](Flow-Git $Root @('config','user.name','Flow Fixture'));[void](Flow-Git $Root @('config','user.email','flow@localhost'))
    [void](Flow-Git $Root @('add','.'));[void](Flow-Git $Root @('commit','-m','isolated cross harness fixture'))
    foreach($task in @('a','b')){[void](Flow-Git $Root @('worktree','add','-b',"fixture/$task","$Root/worktrees/$task"))}
    Save-AgentJson "$Root/tmp/flow/config.json" @{schemaVersion=1;root=[IO.Path]::GetFullPath($Root);model='devin/swe-2';source=$source;codex=$CodexExecutable;omp=$OmpExecutable;base=(Flow-Git $Root @('rev-parse','HEAD'))}
    Write-Output $Root;exit
}
if(-not $Root){throw 'Explicit fixture Root required'}
$Root=(Resolve-Path -LiteralPath $Root).Path
$config=Read-FlowJson "$Root/tmp/flow/config.json"
if($config.root -ne $Root -or $config.model -cne 'devin/swe-2' -or $Root -notlike '*fufucode-flow-*'){throw 'Refuse a non-fixture repository'}
$CodexExecutable=$config.codex;$OmpExecutable=$config.omp
if($Mode -eq 'SetupWorkflow') {
    $box="$Root/tmp/agent-flow";[IO.Directory]::CreateDirectory($box)|Out-Null
    $fingerprints=@{};foreach($name in @('agent-gate.ps1','agent-gate-control.ps1','agent-events.ps1','agent-queue.ps1','agent-manager.ps1','agent-manager-control.ps1','agent-executor.ps1','agent-workflow.ps1','agent-human.ps1','agent-human-control.ps1','agent-core.py')){$fingerprints[$name]=(Get-FileHash "$PSScriptRoot/$name").Hash}
    Save-AgentJson "$Root/tmp/flow/source-scripts.json" $fingerprints
    $tasks=@()
    foreach($task in @('A','B','C')) {
        $lower=$task.ToLowerInvariant();$harness=if($task -eq 'A'){'omp'}else{'codex'};$value=if($task -eq 'A'){41}elseif($task -eq 'B'){42}else{84}
        $child=if($harness -eq 'omp'){'用原生task工具，fixture-helper子代理定义显式devin/swe-2，两份并行仅返回CHILD-A-1/CHILD-A-2，等待结果。'}else{"用原生spawn_agent两份独立只读任务，fork_turns=none、model=devin/swe-2，各只回复CHILD-$task-1/CHILD-$task-2，等待两份结果。不许shell进程冒充原生子代理。"}
        $prompt="$box/prompt-$task.md";$integration="$box/integrate-$task.md";$verify="$box/verify-$task.ps1"
        $schema=@{schemaVersion=1;type='delivery.card';taskId=$task;status='module_ready';summary='真实事实';commits=@();changedFiles=@();verification=@{passed=@();failed=@();notRun=@()};evidence=@();integrationNeeds=@();blockers=@();writerStopped=$true}|ConvertTo-Json -Depth 10 -Compress
        Set-Content $prompt "任务$task｜L2机制测试｜模型devin/swe-2。$child 只写modules/$lower.txt=$value、git add此文件、git commit。不改其他文件。不push。A第一轮41是明确指定的故意缺陷，不擅纠正42。C须先实际跑wire.ps1确认84。最终只输出纯JSON：$schema 。evidence/integrationNeeds等必须字符串数组；commits填本轮真实完整SHA，evidence填真实子代理ID和结果。先写tmp记录也可，不提交tmp。"
        $wiring=if($task -eq 'B'){'A已真实集成，通过后才轮到B集成。创建wire.ps1读取真实modules/a.txt和b.txt，整数求和并输出，不能硬编码84；单独提交wire.ps1。'}else{'只集成本任务模块，没有共享文件需额外改动。'}
        Set-Content $integration $wiring
        $content="param([string]`$Repository)`n`$ErrorActionPreference='Stop'`nif((Get-Content (Join-Path `$Repository 'modules/$lower.txt') -Raw).Trim() -cne '$(if($task -eq 'C'){84}else{42})'){throw 'Wrong module marker'}`n"
        if($task -in @('B','C')){$content+='$value=& pwsh.exe -NoProfile -File (Join-Path $Repository ''wire.ps1'');if($LASTEXITCODE -or "$value".Trim() -cne ''84''){throw ''Wiring failed''}'+"`n"}
        $content+="Write-Output 'PASS $task'`n";Set-Content $verify $content
        $tasks+=@{id=$task;model='devin/swe-2';harness=$harness;worktree="$Root/worktrees/generic-$lower";allowedPaths=@("modules/$lower.txt");integrationPaths=if($task -eq 'B'){@('wire.ps1')}else{@()};dependsOn=if($task -eq 'C'){@('A','B')}else{@()};integrationDependsOn=if($task -eq 'B'){@('A')}else{@()};gates=@('G0');promptFile=$prompt;integrationPromptFile=$integration;verificationFile=$verify;timeoutSeconds=600}
    }
    Save-AgentJson "$box/plan.json" @{schemaVersion=1;id='cross-harness';repository=$Root;mechanismTest=$true;managerModel='devin/swe-2';codexExecutable=$CodexExecutable;ompExecutable=$OmpExecutable;gates=@{G0='passed';G1='open';G2='open'};tasks=$tasks}
    & "$PSScriptRoot/agent-workflow.ps1" -Mode Approve -Repository $Root
    Write-Output "Approved generic workflow at $Root";exit
}
if($Mode -eq 'AssertWorkflow') {
    $box="$Root/tmp/agent-flow";$runtime=Read-FlowJson "$box/runtime.json"
    if($runtime.status -ne 'idle_complete'){throw 'Generic flow not complete'}
    $binding=Read-AgentManager $Root 'devin/swe-2';$childEvidence=@()
    $pairs=@(@('A',1,'rework'),@('A',2,'accept'));$acceptedRuns=@{A=2};$reviewEvidence=@()
    foreach($task in @('B','C')) {
        $attempts=@(Get-ChildItem "$Root/tmp/agent-queue" -Directory -Filter "cross-harness-$task-*"|Where-Object {Test-Path "$($_.FullName)/events/05.json"}|Sort-Object CreationTimeUtc)
        if(-not $attempts.Count -or $attempts.Count -gt 3){throw 'Missing or unbounded fixture review attempts'}
        $original=Read-FlowJson "$($attempts[0].FullName)/delivery.json"
        foreach($job in $attempts) {
            $manifest=Read-FlowJson "$($job.FullName)/manifest.json";$review=Read-FlowJson "$($job.FullName)/review.json";$delivery=Read-FlowJson "$($job.FullName)/delivery.json"
            $expected=if($job -eq $attempts[-1]){'accept'}else{'rework'}
            if($review.decision -cne $expected -or $delivery.sessionId -cne $original.sessionId){throw 'Fixture changed original session or bypassed explicit rework/accept'}
            if($expected -eq 'rework' -and -not $review.findings.Count){throw 'Rework missing actual findings'}
            $pairs+=,@($task,$manifest.attempt,$expected)
            if($expected -eq 'accept'){$acceptedRuns[$task]=$manifest.attempt;if((Read-FlowJson "$box/done-$task.json").runId -cne $manifest.runId){throw 'Integrated a different delivery than accepted'}}
        }
    }
    foreach($pair in $pairs) {
        $job="$Root/tmp/agent-queue/cross-harness-$($pair[0])-$($pair[1])"
        $r=Read-FlowJson "$job/review.json";if($r.decision -cne $pair[2]){throw 'Review path differs from required fixture'}
        $m=Read-FlowJson "$job/manifest.json";$claim=Read-FlowJson "$Root/tmp/agent-gate/$($m.runId).dispatch.json"
        if($claim.status -ne 'decided' -or $claim.reviewerThreadId -cne $binding.threadId -or $m.model -cne 'devin/swe-2'){throw 'Review not actual fixed-thread event transport'}
        $reviewEvidence+=@{task=$pair[0];attempt=$pair[1];decision=$r.decision;runId=$m.runId;head=$r.reviewedHead;findings=$r.findings}
        if(($pair[0] -eq 'A' -and $pair[1] -eq 1) -or ($pair[0] -ne 'A' -and $pair[1] -eq $acceptedRuns[$pair[0]])) {
            if($m.harness -eq 'codex') {
                $spawn=@();$completed=@{}
                foreach($line in [IO.File]::ReadLines("$job/stdout.log")) {
                    $j=$line|ConvertFrom-Json -AsHashtable
                    if($j.item.type -eq 'collab_tool_call' -and $j.item.status -eq 'completed') {
                        if($j.item.tool -eq 'spawn_agent'){$spawn+=@($j.item.receiver_thread_ids)}
                        foreach($id in $j.item.agents_states.Keys){if($j.item.agents_states[$id].status -eq 'completed'){$completed[$id]=$j.item.agents_states[$id].message}}
                    }
                }
                if(@($spawn|Select-Object -Unique).Count -lt 2){throw 'Codex did not actually spawn two children'}
                foreach($id in ($spawn|Select-Object -Unique)) {
                    $files=@(Get-ChildItem (Join-Path $env:USERPROFILE '.codex/sessions/2026/10/10') -Filter "*$id*.jsonl")
                    if($files.Count -ne 1){throw 'No unique saved child thread evidence'}
                    $models=@();$lastText=$null
                    foreach($line in [IO.File]::ReadLines($files[0].FullName)) {
                        $entry=$line|ConvertFrom-Json -AsHashtable
                        if($entry.type -eq 'turn_context'){$models+= $entry.payload.model}
                        if($entry.type -eq 'response_item' -and $entry.payload.role -eq 'assistant' -and $entry.payload.type -eq 'message'){$lastText=(@($entry.payload.content|ForEach-Object {$_.text}) -join '')}
                    }
                    if(-not $models.Count -or @($models|Where-Object {$_ -cne 'devin/swe-2'}).Count -or $lastText -notmatch "^CHILD-$($pair[0])-[12]$"){throw 'Child model/result not actually cheap or completed'}
                    if(-not $completed.ContainsKey($id) -or $completed[$id] -cne $lastText){throw 'Lead did not actually collect the completed native child result'}
                    $childEvidence+=@{harness='codex';threadId=$id;model='devin/swe-2';result=$lastText;session=$files[0].FullName}
                }
            }else{
                $collected=@{}
                foreach($parentFile in Get-ChildItem "$job/sessions" -File -Filter '*.jsonl') {
                    foreach($line in [IO.File]::ReadLines($parentFile.FullName)) {
                        $entry=$line|ConvertFrom-Json -AsHashtable
                        if($entry.type -eq 'custom_message' -and $entry.customType -eq 'async-result') {
                            $notice=[xml]$entry.content
                            foreach($resultNode in $notice.SelectNodes('//task-result')) {
                                if($resultNode.status -ceq 'completed' -and $resultNode.id -in @($entry.details.jobs.jobId)) {
                                    $data=$resultNode.SelectSingleNode('output').InnerText|ConvertFrom-Json -AsHashtable
                                    $collected[$resultNode.id]=if($data.ContainsKey('result')){$data.result}else{$data.marker}
                                }
                            }
                        }
                    }
                }
                $children=@(Get-ChildItem "$job/sessions" -Recurse -Filter '*.jsonl'|Where-Object {$_.DirectoryName -ne (Resolve-Path "$job/sessions").Path})
                if($children.Count -lt 2){throw 'OMP native task children not saved'}
                foreach($file in $children) {
                    $model=$null;$provider=$null;$result=$null;$settled=$false
                    foreach($line in [IO.File]::ReadLines($file.FullName)) {
                        $entry=$line|ConvertFrom-Json -AsHashtable
                        if($entry.message.role -eq 'assistant'){$model=$entry.message.model;$provider=$entry.message.provider}
                        if($entry.message.role -eq 'toolResult' -and $entry.message.toolName -eq 'yield' -and $entry.message.details.status -eq 'success'){$data=$entry.message.details.data;$result=if($data.ContainsKey('result')){$data.result}else{$data.marker}}
                        if($entry.type -eq 'custom' -and $entry.customType -eq 'session_exit' -and $entry.data.kind -eq 'normal'){$settled=$true}
                    }
                    if($model -cne 'swe-2' -or $provider -cne 'devin' -or -not $settled -or $result -notmatch '^CHILD-A-[12]$'){throw 'OMP child model/result missing'}
                    if(-not $collected.ContainsKey($file.BaseName) -or $collected[$file.BaseName] -cne $result){throw 'OMP lead never received the actual settled child result'}
                    $childEvidence+=@{harness='omp';session=$file.FullName;model='devin/swe-2';result=$result}
                }
            }
        }
    }
    $a=Read-FlowJson "$Root/tmp/agent-queue/cross-harness-A-1/delivery.json";$repair=Read-FlowJson "$Root/tmp/agent-queue/cross-harness-A-2/delivery.json"
    if($a.sessionId -cne $repair.sessionId){throw 'Repair changed OMP saved session'}
    foreach($task in @('A','B','C')) {
        $done=Read-FlowJson "$box/done-$task.json";$integration=Read-FlowJson $done.integration
        if($integration.status -ne 'done' -or $integration.threadId -cne $binding.threadId){throw 'Integration not actual fixed manager'}
        [void](Flow-Git $Root @('merge-base','--is-ancestor',$integration.reviewedHead,'HEAD'))
    }
    $c=Read-FlowJson "$Root/tmp/agent-queue/cross-harness-C-1/manifest.json";$bDone=Read-FlowJson "$box/done-B.json"
    if($c.base -cne $bDone.mainSha){throw 'C dispatched before approved dependency integration'}
    $wire=& pwsh.exe -NoProfile -File "$Root/wire.ps1";if($LASTEXITCODE -or "$wire".Trim() -cne '84'){throw 'Actual shared wiring failed'}
    $hashes=@{};foreach($name in @('agent-gate.ps1','agent-gate-control.ps1','agent-events.ps1','agent-queue.ps1','agent-manager.ps1','agent-manager-control.ps1','agent-executor.ps1','agent-workflow.ps1','agent-human.ps1','agent-human-control.ps1','agent-core.py')){$hashes[$name]=(Get-FileHash "$PSScriptRoot/$name").Hash}
    $consistent=$false
    if(Test-Path "$Root/tmp/flow/source-scripts.json"){$before=Read-FlowJson "$Root/tmp/flow/source-scripts.json";$consistent=@($hashes.Keys|Where-Object {$hashes[$_] -cne $before[$_]}).Count -eq 0}
    $protected=@{}
    if(Test-Path "$Root/tmp/flow/inherited.json"){$protected=Read-FlowJson "$Root/tmp/flow/inherited.json";foreach($path in $protected.Keys){if((Get-FileHash (Join-Path $Root $path)).Hash -cne $protected[$path]){throw 'Inherited fixture content changed'}}}
    Save-AgentJson "$box/validation.json" @{schemaVersion=1;scope='isolated-full-flow';status='passed';model='devin/swe-2';root=$Root;reviewLoopVerified=$true;fixedManagerVerified=$true;sourceConsistencyVerified=$consistent;protectedFiles=$protected;originalSessionId=$a.sessionId;managerThreadId=$binding.threadId;mainSha=(Flow-Git $Root @('rev-parse','HEAD'));reviews=$reviewEvidence;acceptedAttempts=$acceptedRuns;children=$childEvidence;scripts=$hashes;verifiedAt=[DateTime]::UtcNow.ToString('o')}
    Write-Output "PASS: generic framework cross-harness A/B concurrent leads, $($childEvidence.Count) actual cheap children, rework/original session, fixed-M review/integration/shared wiring/dependent C."
    exit
}
$taskName='Fufucode-FlowTest-'+(Split-Path $Root -Leaf).Substring(14)
if($Mode -in @('Start','Stop')) {
    $scheduler=New-Object -ComObject Schedule.Service;$scheduler.Connect();$folder=$scheduler.GetFolder('\')
    if($Mode -eq 'Stop') {
        $task=$folder.GetTask($taskName);$task.Enabled=$false;$task.Stop(0)
        & "$PSScriptRoot/agent-gate-control.ps1" -Mode Stop -Repository $Root
        exit
    }
    $existing=$null;try{$existing=$folder.GetTask($taskName)}catch{}
    if($existing -and $existing.State -eq 4){throw 'Fixture driver already active'}
    if(-not $existing) {
        $d=$scheduler.NewTask(0);$d.Principal.UserId=[Security.Principal.WindowsIdentity]::GetCurrent().Name;$d.Principal.LogonType=3
        $d.Settings.ExecutionTimeLimit='PT0S';$d.Settings.MultipleInstances=2;$d.Settings.DisallowStartIfOnBatteries=$false;$d.Settings.StopIfGoingOnBatteries=$false
        $command='"'+(Get-Command pwsh.exe).Source+'" -NoLogo -NoProfile -NonInteractive -File "'+$PSCommandPath+'" -Mode Driver -Root "'+$Root+'"'
        $hostScript="$Root/tmp/flow/launch.js"
        $js='var s = new ActiveXObject("WScript.Shell"); s.CurrentDirectory = '+($source|ConvertTo-Json -Compress)+'; var c = s.Run('+($command|ConvertTo-Json -Compress)+', 0, true); WScript.Quit(c);'
        Set-Content -LiteralPath $hostScript -Value $js -Encoding ascii
        $a=$d.Actions.Create(0);$a.Path=(Get-Command wscript.exe).Source;$a.WorkingDirectory=$source
        $a.Arguments='//B //Nologo //E:JScript "'+$hostScript+'"'
        $existing=$folder.RegisterTaskDefinition($taskName,$d,2,$d.Principal.UserId,$null,3)
    }
    $existing.Enabled=$true;[void]$existing.Run($null);Write-Output "Started $taskName";exit
}
if($Mode -eq 'Status'){Get-Content "$Root/tmp/flow/runtime.json" -Raw -ErrorAction SilentlyContinue;Get-Content "$Root/tmp/flow/events.jsonl" -Tail 8 -ErrorAction SilentlyContinue;exit}
function Launch-FlowJob($Task,$Attempt,$Harness,$Value,$Resume=$null,[bool]$FormatOnly=$false) {
    $suffix=if($FormatOnly){'-card'}else{''}
    $directory="$Root/tmp/agent-queue/$($Task.ToLowerInvariant())-$Attempt$suffix"
    if(Test-Path -LiteralPath $directory){return}
    $worktree="$Root/worktrees/$($Task.ToLowerInvariant())"
    $dirty=Flow-Git $worktree @('status','--porcelain')
    if($dirty -and -not ($Resume -and $FormatOnly)){throw 'Fixture worker starts dirty'}
    if($dirty -and $dirty -cne '?? modules/'){throw 'Unexpected inherited fixture change'}
    [IO.Directory]::CreateDirectory("$directory/sessions")|Out-Null
    $manifest=@{schemaVersion=1;runId=[guid]::NewGuid().ToString('N');task=$Task;worktree=$worktree;base=(Flow-Git $worktree @('rev-parse','HEAD'));model='devin/swe-2';protocol=if($Harness -eq 'omp'){'omp-json-v1'}else{'codex-json-v1'};harness=$Harness;resumeSession=$Resume;attempt=$Attempt;value=$Value;formatOnly=$FormatOnly;mechanismTest=$true}
    $child=if($Harness -eq 'omp'){'必须调用原生task工具，agent=fixture-helper，显式model=devin/swe-2（若工具不支持model字段，由fixture-helper定义固定模型）；派两个并行无意义子任务，各返回CHILD-A-1/CHILD-A-2并等待结果。'}else{'必须原生spawn_agent两份独立只读任务，fork_turns=none、model=devin/swe-2，各只回复CHILD-'+$Task+'-1/CHILD-'+$Task+'-2；等待并收回两个结果。禁止用shell子进程冒充原生子代理。'}
    $repair=if($Resume){'此为原会话修复：先读上一轮review，保持原模型/worktree/session。'}else{'首次无意义模拟任务。'}
    $prompt="任务$Task｜L2机制试跑｜模型devin/swe-2。$repair $child 然后只将modules/$($Task.ToLowerInvariant()).txt写为$Value并git add指定文件、git commit提交。不改其他版本化文件，不push。A第一轮指定41是故意缺陷，禁止纠正为42；将按真实验收打回。独立读回。最终只能一个纯JSON对象delivery.card，字段schemaVersion=1,type,taskId=$Task,status=module_ready,summary,commits（本轮起点之后完整40位SHA）,changedFiles,verification{passed,failed,notRun},evidence（包含真实子代理ID/结果）,integrationNeeds,blockers,writerStopped=true。不能任何前言/后记。临时子代理证据仅tmp，不提交。共享接线由M。所有命令显式workdir。"
    if($FormatOnly){$prompt="原任务$Task原模型devin/swe-2原保存会话补交格式。上一轮源码已提交，但交付卡schema不合法。本轮只读验证，不改文件、不新提交、不再次派子代理。用之前真实子代理ID和完成结果作evidence，之前SHA放evidence，本轮commits=[]、changedFiles=[]。保持modules标记现值$Value，不擅修缺陷。最终纯JSON且所有数组字段必须数组。"}
    if($FormatOnly -and $dirty){$prompt="原任务$Task原模型devin/swe-2原保存会话恢复。上轮被Git沙箱挡住且卡格式无效。此执行入口已授权提交。已核实唯一继承改动是你原任务modules/$($Task.ToLowerInvariant()).txt，保持值$Value，只git add该文件并commit，禁止夹带其他改动。不再派子代理，用之前真实ID/结果作evidence。最终纯JSON，commits填写本轮真实完整SHA，所有数组字段必须数组。"}
    $example=@{schemaVersion=1;type='delivery.card';taskId=$Task;status='module_ready';summary='真实事实';commits=@();changedFiles=@();verification=@{passed=@();failed=@();notRun=@()};evidence=@();integrationNeeds=@();blockers=@();writerStopped=$true}|ConvertTo-Json -Depth 10 -Compress
    $prompt+=" 完整schema示例：$example 。evidence与integrationNeeds只能字符串数组，禁止对象或单字符串；填写真实事实。"
    Set-Content "$directory/prompt.md" $prompt -Encoding utf8
    Save-AgentJson "$directory/manifest.json" $manifest
    Write-AgentEvent $directory $manifest 'job.created' @{worktree=$worktree;model=$manifest.model;base=$manifest.base;harness=$Harness}
    Save-AgentJson "$directory/state.json" @{status='starting';runId=$manifest.runId;task=$Task;model=$manifest.model;worktree=$worktree}
    $psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=(Get-Command pwsh.exe).Source;$psi.WorkingDirectory=$source;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
    foreach($arg in @('-NoProfile','-File',$PSCommandPath,'-Mode','Worker','-Root',$Root,'-JobDirectory',$directory)){$psi.ArgumentList.Add($arg)}
    $p=[Diagnostics.Process]::Start($psi);$workerPid=$p.Id;$p.Dispose()
    Flow-Log 'worker.launched' @{task=$Task;job=$directory;pid=$workerPid;harness=$Harness;resume=$Resume}
}
if($Mode -eq 'Worker') {
    $m=Read-FlowJson "$JobDirectory/manifest.json"
    $key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($m.worktree.ToLowerInvariant())))
    $mutex=[Threading.Mutex]::new($false,"Local\FufucodeAgent-$key");$owns=$mutex.WaitOne(0)
    if(-not $owns){throw 'Worker writer already active'}
    $state=@{schemaVersion=1;runId=$m.runId;task=$m.task;model=$m.model;worktree=$m.worktree;workerPid=$PID;status='running';started=[DateTime]::UtcNow.ToString('o')}
    try {
        Save-AgentJson "$JobDirectory/state.json" $state
        # Worker owns task mutex throughout native lead and its children.
        $psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=if($m.harness -eq 'omp'){$OmpExecutable}else{$CodexExecutable}
        $psi.WorkingDirectory=$m.worktree;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
        $psi.RedirectStandardInput=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
        if($m.harness -eq 'omp') {
            $arguments=@('--print','--mode','json','--model','devin/swe-2','--smol','devin/swe-2','--slow','devin/swe-2','--plan','devin/swe-2','--config',"$Root/tmp/flow/omp.yml",'--session-dir',"$JobDirectory/sessions",'--no-title','--max-time','8m','--no-extensions','--auto-approve')
            if($m.resumeSession){$arguments+=@('--resume',$m.resumeSession)}
            $arguments+=('@'+"$JobDirectory/prompt.md")
        } else {
            $arguments=@('--no-daemon','exec','-m','devin/swe-2','-C',$m.worktree,'-s','danger-full-access','-c','approval_policy="never"','--json')
            if($m.resumeSession){$arguments+=@('resume',$m.resumeSession)}
            $arguments+='-'
        }
        foreach($arg in $arguments){$psi.ArgumentList.Add([string]$arg)}
        $p=[Diagnostics.Process]::Start($psi);$state.agentPid=$p.Id;Save-AgentJson "$JobDirectory/state.json" $state
        Write-AgentEvent $JobDirectory $m 'agent.started' @{agentPid=$p.Id;workerPid=$PID;started=$state.started}
        $out=[IO.File]::Open("$JobDirectory/stdout.log",'CreateNew','Write','Read');$err=[IO.File]::Open("$JobDirectory/stderr.log",'CreateNew','Write','Read')
        try {
            $a=$p.StandardOutput.BaseStream.CopyToAsync($out);$b=$p.StandardError.BaseStream.CopyToAsync($err)
            if($m.harness -eq 'codex'){$p.StandardInput.Write((Get-Content "$JobDirectory/prompt.md" -Raw))};$p.StandardInput.Close()
            if(-not $p.WaitForExit(600000)){$p.Kill($true);$p.WaitForExit();$state.watchdogTimeout=$true}
            [void]$a.GetAwaiter().GetResult();[void]$b.GetAwaiter().GetResult()
        } finally {$out.Dispose();$err.Dispose()}
        $state.exitCode=$p.ExitCode;$p.Dispose();$state.finished=[DateTime]::UtcNow.ToString('o');$state.status='process_exited'
        $state.head=Flow-Git $m.worktree @('rev-parse','HEAD');$state.dirty=Flow-Git $m.worktree @('status','--porcelain');$state.commits=Flow-Git $m.worktree @('log','--format=%H %s',"$($m.base)..HEAD")
        Write-AgentEvent $JobDirectory $m 'agent.exited' @{exitCode=$state.exitCode;head=$state.head;finished=$state.finished;watchdogTimeout=[bool]$state.watchdogTimeout}
        if($m.harness -eq 'omp'){$state.status=Publish-AgentDelivery $JobDirectory $m $state "$JobDirectory/stdout.log"}else{
            try {
                if($state.exitCode -ne 0 -or $state.watchdogTimeout){throw 'Codex lead failed'}
                $reply=Get-FlowCodexReply "$JobDirectory/stdout.log";$card=Read-AgentCard $reply.text $m.task
                $commits=@(($state.commits -split "`n")|Where-Object {$_}|ForEach-Object {($_ -split ' ')[0]})
                foreach($sha in $card.commits){if($sha -notin $commits){throw 'Codex card commit outside run'}}
                Save-AgentJson "$JobDirectory/delivery.json" @{schemaVersion=1;runId=$m.runId;taskId=$m.task;sessionId=$reply.sessionId;base=$m.base;head=$state.head;commits=$commits;dirty=$state.dirty;card=$card;status='unreviewed';finished=$state.finished}
                Write-AgentEvent $JobDirectory $m 'delivery.received' @{head=$state.head;sessionId=$reply.sessionId;cardStatus=$card.status;receipt='delivery.json';legacy=$false}
                $state.status='completed_pending_review'
            }catch{Write-AgentEvent $JobDirectory $m 'delivery.invalid' @{reason=$_.Exception.Message;exitCode=$state.exitCode;head=$state.head};$state.status='failed_or_missing_card'}
        }
        Save-AgentJson "$JobDirectory/state.json" $state
    }catch{Save-AgentJson "$JobDirectory/state.json" @{status='supervisor_error';error=$_.Exception.Message;agentPid=$state.agentPid;workerPid=$PID;runId=$m.runId};throw}
    finally{$mutex.ReleaseMutex();$mutex.Dispose()}
    exit
}
function Integrate-Flow($Name,$Jobs) {
    $path="$Root/tmp/flow/$Name.json"
    if(Test-Path $path){$prior=Read-FlowJson $path;if($prior.status -eq 'done'){return};throw 'Ambiguous integration; no automatic rerun'}
    $lease=[IO.File]::Open("$Root/tmp/agent-gate/main-writer.lock",'OpenOrCreate','ReadWrite','None')
    try {
        $heads=@();foreach($job in $Jobs){$d=Read-FlowJson "$Root/tmp/agent-queue/$job/delivery.json";$r=Read-FlowJson "$Root/tmp/agent-queue/$job/review.json";if($r.decision -ne 'accept' -or $r.reviewedHead -cne $d.head){throw 'Cannot integrate unaccepted head'};$heads+=$d.head}
        Save-AgentJson $path @{status='inflight';heads=$heads;started=[DateTime]::UtcNow.ToString('o')}
        $spec=if($Name -eq 'wire-ab'){'用git merge --no-ff真实A/B来源HEAD收编，仅modules/a.txt,b.txt。A有首轮缺陷提交及修复，需完整收编对应源范围。M创建wire.ps1读取modules/a.txt,b.txt，转换整数相加并输出；必须实际执行输出84。git add仅wire.ps1并提交共享接线。'}else{'用git merge --no-ff仅收编C来源HEAD的modules/c.txt；独立确认84且wire.ps1输出84，不能重复收编A/B或改接线。'}
        $prompt="固定M本轮是隔离全流程夹具的集成事件，明确授权执行集成，不是review请求。难度L3试跑，模型devin/swe-2。先读acceptance.md；只在本隔离仓库main写入。来源Job目录：$($Jobs -join ', ')，真实HEAD：$($heads -join ', ')。先核对review.decision=accept和HEAD/停止worker及允许范围。$spec 禁止push、删除worktree、改任何真实项目。由外部控制器持有main-writer lease，不再自行取得同锁。最终报告真实mainSHA和验证事实。所有命令显式workdir，禁止子代理接管main。"
        $turn=Invoke-AgentManagerTurn -Repository $Root -Model 'devin/swe-2' -CodexExecutable $CodexExecutable -RunId "flow-$Name" -Prompt $prompt -TimeoutSeconds 600 -Integration
        if(-not $turn.completed){throw 'Integration model failed; retain claim'}
        foreach($head in $heads){[void](Flow-Git $Root @('merge-base','--is-ancestor',$head,'HEAD'))}
        if((Flow-Git $Root @('status','--porcelain'))){throw 'Integration left versioned dirty files'}
        $wire=& pwsh.exe -NoProfile -File "$Root/wire.ps1";if($LASTEXITCODE -or "$wire".Trim() -cne '84'){throw 'Shared wire independent verification failed'}
        if($Name -eq 'integrate-c' -and (Get-Content "$Root/modules/c.txt" -Raw).Trim() -cne '84'){throw 'C verification failed'}
        $sha=Flow-Git $Root @('rev-parse','HEAD')
        Save-AgentJson $path @{status='done';heads=$heads;mainSha=$sha;threadId=$turn.threadId;finished=[DateTime]::UtcNow.ToString('o');wire=$wire}
        Flow-Log 'integration.done' @{name=$Name;mainSha=$sha;heads=$heads;threadId=$turn.threadId}
    }finally{$lease.Dispose()}
}
if($Mode -eq 'Driver') {
    $lock=[IO.File]::Open("$Root/tmp/flow/driver.lock",'OpenOrCreate','ReadWrite','None')
    $watcher=New-AgentFileWatch "$Root/tmp" "FufucodeFixture-$PID"
    $runtime=@{status='running';pid=$PID;started=[DateTime]::UtcNow.ToString('o');model='devin/swe-2'}
    try {
        Save-AgentJson "$Root/tmp/flow/runtime.json" $runtime
        Flow-Log 'driver.started' @{pid=$PID}
        if(-not (Test-Path "$Root/tmp/agent-gate/manager.json")) {
            & pwsh.exe -NoProfile -File "$Root/scripts/agent-manager-control.ps1" -Mode Initialize -Repository $Root -Model 'devin/swe-2' -CodexExecutable $CodexExecutable -TimeoutSeconds 600
            if($LASTEXITCODE){throw 'Fixed fixture manager initialization failed'}
        }
        [void](Read-AgentManager $Root 'devin/swe-2')
        if(-not (Test-Path "$Root/tmp/flow/gate-started.json")) {
            Copy-Item "$source/tmp/agent-gate/dispatch-validated.json" "$Root/tmp/agent-gate/dispatch-validated.json"
            & pwsh.exe -NoProfile -File "$PSScriptRoot/agent-gate-control.ps1" -Mode Start -Repository $Root -Dispatch -FixedManager -Model 'devin/swe-2' -CodexExecutable $CodexExecutable
            if($LASTEXITCODE){throw 'Fixture gate scheduler start failed'}
            Save-AgentJson "$Root/tmp/flow/gate-started.json" @{started=[DateTime]::UtcNow.ToString('o')}
        }
        Launch-FlowJob 'A' 1 'omp' 41
        Launch-FlowJob 'B' 1 'codex' 42
        $deadline=[DateTime]::UtcNow.AddMinutes(25)
        do {
            $accepted=@{}
            foreach($task in @('A','B','C')) {
                $jobs=@(Get-ChildItem "$Root/tmp/agent-queue" -Directory | Where-Object {$_.Name -match ('^'+$task.ToLowerInvariant()+'-\d+(-card)?$')} | Sort-Object Name)
                if(-not $jobs){continue}
                $job=$jobs[-1];$m=Read-FlowJson "$($job.FullName)/manifest.json";$s=Read-FlowJson "$($job.FullName)/state.json"
                if($s.status -eq 'failed_or_missing_card') {
                    if($m.formatOnly -or $s.exitCode -ne 0 -or -not $s.finished -or (Flow-Git $m.worktree @('rev-parse','HEAD')) -cne $s.head){throw "Worker $($job.Name) failed; no blind retry"}
                    foreach($id in @($s.workerPid,$s.agentPid)){if(Get-Process -Id $id -ErrorAction SilentlyContinue){throw 'Failed-card writer still active'}}
                    $invalid=Read-FlowJson "$($job.FullName)/events/04.json"
                    if($invalid.type -ne 'delivery.invalid'){throw 'No explicit invalid delivery event'}
                    if($m.harness -eq 'omp'){$reply=Get-OmpFinalReply "$($job.FullName)/stdout.log";$sessions=@(Get-ChildItem "$($job.FullName)/sessions" -Filter '*.jsonl');if($sessions.Count -ne 1){throw 'Ambiguous original OMP lead session'};$resume=$sessions[0].FullName}else{$reply=Get-FlowCodexReply "$($job.FullName)/stdout.log";$resume=$reply.sessionId}
                    Launch-FlowJob $task $m.attempt $m.harness $m.value $resume $true
                    continue
                }
                if($s.status -eq 'supervisor_error'){throw "Worker $($job.Name) supervisor uncertain; no retry"}
                if(-not (Test-Path "$($job.FullName)/review.json")){continue}
                $r=Read-FlowJson "$($job.FullName)/review.json"
                $manager=Read-FlowJson "$Root/tmp/agent-gate/manager.json"
                if($manager.status -ne 'ready'){continue}
                $claimPath="$Root/tmp/agent-gate/$($m.runId).dispatch.json"
                if(-not (Test-Path $claimPath) -or (Read-FlowJson $claimPath).status -ne 'decided'){continue}
                if($r.decision -eq 'rework') {
                    if($m.attempt -ge 2){throw 'Second rework exceeds fixture bound; inspect'}
                    if($m.harness -eq 'omp'){$sessions=@(Get-ChildItem "$($job.FullName)/sessions" -Filter '*.jsonl');if($sessions.Count -eq 0 -and $m.resumeSession){$sessions=@(Get-Item $m.resumeSession)};if($sessions.Count -ne 1){throw 'Ambiguous OMP original session'};$resume=$sessions[0].FullName}else{$resume=(Read-FlowJson "$($job.FullName)/delivery.json").sessionId}
                    Launch-FlowJob $task 2 $m.harness $(if($task -eq 'C'){84}else{42}) $resume
                }elseif($r.decision -eq 'accept'){$accepted[$task]=$job.Name}else{throw "Fixture blocked by explicit decision: $($r.decision)"}
            }
            if($accepted.ContainsKey('A') -and $accepted.ContainsKey('B') -and -not (Test-Path "$Root/tmp/flow/wire-ab.json")) {
                $binding=Read-FlowJson "$Root/tmp/agent-gate/manager.json"
                if($binding.status -eq 'ready'){Integrate-Flow 'wire-ab' @($accepted.A,$accepted.B)}
            }
            if(Test-Path "$Root/tmp/flow/wire-ab.json") {
                $wireState=Read-FlowJson "$Root/tmp/flow/wire-ab.json"
                if($wireState.status -ne 'done'){throw 'Unresolved shared integration'}
                if(-not (Test-Path "$Root/worktrees/c")){[void](Flow-Git $Root @('worktree','add','-b','fixture/c',"$Root/worktrees/c"))}
                Launch-FlowJob 'C' 1 'codex' 84
            }
            if($accepted.ContainsKey('C')){Integrate-Flow 'integrate-c' @($accepted.C);$runtime.status='complete';break}
            if((Read-FlowJson "$Root/tmp/agent-gate/manager.json").status -eq 'needs_attention'){throw 'Manager failed/ambiguous; no automatic retransmission'}
            [void](Wait-AgentFileWatch $watcher)
        }while([DateTime]::UtcNow -lt $deadline)
        if($runtime.status -ne 'complete'){throw 'Fixture overall timeout'}
        $runtime.finished=[DateTime]::UtcNow.ToString('o');Save-AgentJson "$Root/tmp/flow/runtime.json" $runtime
        Flow-Log 'flow.complete' $runtime
    }catch{$runtime.status='needs_attention';$runtime.error=$_.Exception.Message;Save-AgentJson "$Root/tmp/flow/runtime.json" $runtime;Flow-Log 'flow.failed' $runtime}
    finally{Close-AgentFileWatch $watcher;$lock.Dispose()}
    exit
}
if($Mode -eq 'Assert') {
    $runtime=Read-FlowJson "$Root/tmp/flow/runtime.json";if($runtime.status -ne 'complete'){throw 'Flow not complete'}
    foreach($pair in @(@('a-1','rework'),@('a-2','accept'),@('b-1','accept'),@('c-1','accept'))) {
        $job="$Root/tmp/agent-queue/$($pair[0])";$r=Read-FlowJson "$job/review.json";if($r.decision -cne $pair[1]){throw 'Unexpected review path'}
        $m=Read-FlowJson "$job/manifest.json";if($m.model -cne 'devin/swe-2'){throw 'Expensive model drift'}
        $claim=Read-FlowJson "$Root/tmp/agent-gate/$($m.runId).dispatch.json";if($claim.status -ne 'decided'){throw 'Review transport unresolved'}
    }
    $a=Read-FlowJson "$Root/tmp/agent-queue/a-1/delivery.json";$b=Read-FlowJson "$Root/tmp/agent-queue/a-2/delivery.json"
    if($a.sessionId -cne $b.sessionId){throw 'A repair created another session'}
    $binding=Read-AgentManager $Root 'devin/swe-2'
    foreach($claimFile in Get-ChildItem "$Root/tmp/agent-gate" -Filter '*.dispatch.json') {if((Read-FlowJson $claimFile.FullName).reviewerThreadId -cne $binding.threadId){throw 'Manager changed thread'}}
    foreach($name in @('wire-ab','integrate-c')){if((Read-FlowJson "$Root/tmp/flow/$name.json").threadId -cne $binding.threadId){throw 'Integration used another manager'}}
    $c=Read-FlowJson "$Root/tmp/agent-queue/c-1/manifest.json";$wire=Read-FlowJson "$Root/tmp/flow/wire-ab.json"
    if($c.base -cne $wire.mainSha){throw 'Dependent task started before integration'}
    Save-AgentJson "$Root/tmp/flow/validation.json" @{status='passed';scope='isolated-full-flow';model='devin/swe-2';managerThreadId=$binding.threadId;mainSha=(Flow-Git $Root @('rev-parse','HEAD'));originalSessionId=$a.sessionId;verifiedAt=[DateTime]::UtcNow.ToString('o');root=$Root}
    Write-Output 'PASS: isolated cross-harness dispatch, event review, original-session repair, shared wiring and dependent dispatch.'
}
