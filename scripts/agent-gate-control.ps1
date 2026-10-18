# Task Scheduler owns the gate; it is not a child of the interactive Codex shell.
[CmdletBinding()]
param([ValidateSet('Start','Configure','Status','Pause','Resume','Stop')][string]$Mode='Status',
    [string]$Repository=(Split-Path $PSScriptRoot -Parent), [switch]$Dispatch,
    [string]$Model, [string]$CodexExecutable, [switch]$FixedManager, [switch]$MechanismTest)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'agent-events.ps1')
$Repository=(Resolve-Path -LiteralPath $Repository).Path
$outbox=Join-Path $Repository 'tmp/agent-gate'
[IO.Directory]::CreateDirectory($outbox)|Out-Null
$key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Repository.ToLowerInvariant()))).Substring(0,12)
$name="Fufucode-EventGate-$key"
$paused=Join-Path $outbox 'paused'
if($Mode -eq 'Pause'){Set-Content -LiteralPath $paused -Value 'Paused explicitly; in-flight review is allowed to finish.'; @{changed=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content (Join-Path $outbox 'control.json'); exit}
$scheduler=New-Object -ComObject 'Schedule.Service'
$scheduler.Connect()
$folder=$scheduler.GetFolder('\')
if($Mode -in @('Start','Configure')) {
    if($Dispatch -and (-not $Model -or -not $CodexExecutable)){throw 'Explicit Model and CodexExecutable required'}
    if($MechanismTest) {
        $fixture=Join-Path $Repository 'tmp/flow/config.json'
        if($Model -cne 'devin/swe-2' -or -not (Test-Path $fixture) -or
            -not $Repository.StartsWith([IO.Path]::GetTempPath().TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)){throw 'MechanismTest is limited to explicit devin/swe-2 isolated temporary fixtures'}
        $fixtureConfig=Get-Content $fixture -Raw|ConvertFrom-Json
        if($fixtureConfig.root -ne $Repository -or $fixtureConfig.model -cne 'devin/swe-2'){throw 'Fixture identity mismatch'}
        if($FixedManager){. (Join-Path $PSScriptRoot 'agent-manager.ps1');[void](Read-AgentManager $Repository $Model)}
    }
    if($Dispatch -and -not $MechanismTest) {
        $proofPath=Join-Path $outbox 'dispatch-validated.json'
        if(-not (Test-Path -LiteralPath $proofPath)){throw 'Production Dispatch is gated by recorded real closed-loop validation'}
        $proof=Get-Content $proofPath -Raw|ConvertFrom-Json -AsHashtable
        if($proof.scope -cne 'event-review-only' -or $proof.model -cne 'devin/swe-2' -or $proof.reviewLoopVerified -ne $true -or $proof.replayAndEmptyQueueVerified -ne $true){throw 'Invalid validation proof'}
        $scripts=@('agent-gate.ps1','agent-queue.ps1','agent-events.ps1')
        if($FixedManager){
            if($proof.fixedManagerVerified -ne $true){throw 'Fixed manager transport has not passed real validation'}
            $scripts+=@('agent-manager.ps1','agent-manager-control.ps1')
            . (Join-Path $PSScriptRoot 'agent-manager.ps1')
            [void](Read-AgentManager $Repository $Model)
        }
        foreach($script in $scripts) {
            if($proof.scripts[$script] -cne (Get-FileHash (Join-Path $PSScriptRoot $script)).Hash){throw "Code changed since validation: $script"}
        }
    }
    # Do not silently replace an existing owner/configuration.
    $existing=$null
    try{$existing=$folder.GetTask($name)}catch{if($_.Exception.HResult -ne -2147024894){throw}}
    if($existing -and $Mode -eq 'Start'){throw "Task already exists: $name. Inspect Status; do not replace an unknown owner."}
    if($Mode -eq 'Configure') {
        if(-not $existing -or -not (Test-Path -LiteralPath $paused)){throw 'Configure requires an existing explicitly paused managed task'}
        $owner=$existing.Definition.Principal.UserId
        $ownerSid=if($owner -match '^S-1-'){[Security.Principal.SecurityIdentifier]::new($owner)}else{[Security.Principal.NTAccount]::new($owner).Translate([Security.Principal.SecurityIdentifier])}
        $ownedAction=$existing.Definition.Actions.Item(1).Arguments.Contains((Join-Path $PSScriptRoot 'agent-gate.ps1'))
        $hostPath=Join-Path $outbox 'gate-host.js'
        if($existing.Definition.Actions.Item(1).Path -eq (Get-Command wscript.exe).Source -and $existing.Definition.Actions.Item(1).Arguments.Contains($hostPath) -and (Test-Path (Join-Path $outbox 'task-config.json'))) {
            $ownedConfig=Get-Content (Join-Path $outbox 'task-config.json') -Raw|ConvertFrom-Json
            $ownedAction=$ownedConfig.repository -eq $Repository -and $ownedConfig.hostHash -ceq (Get-FileHash $hostPath).Hash
        }
        if($existing.Definition.Actions.Item(1).WorkingDirectory -ne $Repository -or -not $ownedAction -or
            $ownerSid.Value -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'Existing task identity mismatch; do not take over'}
        foreach($claimFile in Get-ChildItem $outbox -Filter '*.dispatch.json') {
            $claim=Get-Content $claimFile.FullName -Raw|ConvertFrom-Json
            if($claim.status -eq 'dispatching'){throw 'Cannot configure with unresolved dispatch'}
        }
        [IO.File]::WriteAllText((Join-Path $outbox ('task-backup-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff')+'.xml')),$existing.Xml)
        $existing.Enabled=$false
        @{stop=$true}|ConvertTo-Json|Set-Content (Join-Path $outbox 'stop.json')
        $deadline=[DateTime]::UtcNow.AddSeconds(35)
        do {
            $statePath=Join-Path $outbox 'runtime.json'
            $state=if(Test-Path $statePath){Get-Content $statePath -Raw|ConvertFrom-Json}else{$null}
            $live=if($state -and -not $state.stopped){Get-CimInstance Win32_Process -Filter "ProcessId=$($state.pid)"|Where-Object {$_.CommandLine -match 'agent-gate.ps1' -and $_.CommandLine.Contains($Repository)}}else{$null}
            if(-not $live){break};Start-Sleep -Milliseconds 200
        }while([DateTime]::UtcNow -lt $deadline)
        if($live){throw 'Existing gate has not exited; do not replace its live owner'}
        $existing.Stop(0)
    }
    if(Test-Path (Join-Path $outbox 'stop.json')){Remove-Item (Join-Path $outbox 'stop.json')}
    $definition=$scheduler.NewTask(0)
    $definition.RegistrationInfo.Description='Fufucode local delivery-event gate. No idle model polling.'
    $definition.Principal.UserId=[Security.Principal.WindowsIdentity]::GetCurrent().Name
    $definition.Principal.LogonType=3 # InteractiveToken: never stores a password or logs the user in.
    $definition.Principal.RunLevel=0
    $definition.Settings.Enabled=$true
    $definition.Settings.ExecutionTimeLimit='PT0S'
    $definition.Settings.DisallowStartIfOnBatteries=$false
    $definition.Settings.StopIfGoingOnBatteries=$false
    $definition.Settings.MultipleInstances=2 # IgnoreNew
    $definition.Settings.RestartCount=3
    $definition.Settings.RestartInterval='PT1M'
    $trigger=$definition.Triggers.Create(9) # At logon, not a model timer.
    $trigger.UserId=$definition.Principal.UserId
    $action=$definition.Actions.Create(0)
    $action.WorkingDirectory=$Repository
    foreach($value in @($Repository,$PSScriptRoot,$Model,$CodexExecutable)){if($value -match '["\r\n]'){throw 'Invalid task argument'}}
    $gateArguments='-NoLogo -NoProfile -NonInteractive -File "'+(Join-Path $PSScriptRoot 'agent-gate.ps1')+'" -Repository "'+$Repository+'"'
    if($Dispatch){$gateArguments+=' -Dispatch -Model "'+$Model+'" -CodexExecutable "'+$CodexExecutable+'"'}
    if($FixedManager){$gateArguments+=' -FixedManager'}
    $gateCommand='"'+(Get-Command pwsh.exe).Source+'" '+$gateArguments
    $hostPath=Join-Path $outbox 'gate-host.js'
    Set-Content -LiteralPath $hostPath -Value ('var s=new ActiveXObject("WScript.Shell");s.CurrentDirectory='+($Repository|ConvertTo-Json -Compress)+';WScript.Quit(s.Run('+($gateCommand|ConvertTo-Json -Compress)+',0,true));') -Encoding ascii
    @{repository=$Repository;gateCommand=$gateCommand;hostHash=(Get-FileHash $hostPath).Hash}|ConvertTo-Json|Set-Content (Join-Path $outbox 'task-config.json')
    $action.Path=(Get-Command wscript.exe).Source
    $action.Arguments='//B //Nologo //E:JScript "'+$hostPath+'"'
    $registrationFlag=if($Mode -eq 'Configure'){4}else{2}
    $registered=$folder.RegisterTaskDefinition($name,$definition,$registrationFlag,$definition.Principal.UserId,$null,3)
    [void]$registered.Run($null)
    Write-Output "Started scheduled task: $name"
    exit
}
$task=$null
try{$task=$folder.GetTask($name)}catch{if($_.Exception.HResult -ne -2147024894){throw}}
if($Mode -eq 'Resume') {
    if(-not $task){throw 'No registered gate; use Start with verified configuration first'}
    if(Test-Path -LiteralPath $paused){Remove-Item -LiteralPath $paused}
    @{changed=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content (Join-Path $outbox 'control.json')
    if(Test-Path (Join-Path $outbox 'stop.json')){Remove-Item (Join-Path $outbox 'stop.json')}
    $task.Enabled=$true
    if($task.State -ne 4){[void]$task.Run($null)}
    Write-Output 'Task enabled and dispatch unpaused; ambiguous claims are still not retried.'
    exit
}
if($Mode -eq 'Stop') {
    # Disable future logon/restart first; do not delete the task or claims.
    if($task){$task.Enabled=$false}
    @{stop=$true}|ConvertTo-Json|Set-Content (Join-Path $outbox 'stop.json')
    # A wscript-owned console can outlive Task.Stop; request a graceful local event exit instead.
    $deadline=[DateTime]::UtcNow.AddSeconds(35)
    do {
        $statePath=Join-Path $outbox 'runtime.json'
        $state=if(Test-Path $statePath){Get-Content $statePath -Raw|ConvertFrom-Json}else{$null}
        $live=if($state -and -not $state.stopped){Get-CimInstance Win32_Process -Filter "ProcessId=$($state.pid)"|Where-Object {$_.CommandLine -match 'agent-gate.ps1' -and $_.CommandLine.Contains($Repository)}}else{$null}
        if(-not $live){break};Start-Sleep -Milliseconds 200
    }while([DateTime]::UtcNow -lt $deadline)
    if($live){Write-Output 'Task disabled; graceful stop requested. In-flight review may still finish; no automatic resend.'}else{Write-Output 'Task disabled; gate exited. Claims and logs retained.'}
    exit
}
$runtime=$null
if(Test-Path -LiteralPath (Join-Path $outbox 'runtime.json')){$runtime=Get-Content (Join-Path $outbox 'runtime.json') -Raw | ConvertFrom-Json}
$live=$null
if($runtime -and -not $runtime.stopped){$live=Get-CimInstance Win32_Process -Filter "ProcessId=$($runtime.pid)" | Where-Object {$_.CommandLine -match 'agent-gate.ps1' -and $_.CommandLine.Contains($Repository)}}
$manager=$null
if(Test-Path -LiteralPath (Join-Path $outbox 'manager.json')){$manager=Get-Content (Join-Path $outbox 'manager.json') -Raw|ConvertFrom-Json}
@{taskName=$name;taskExists=[bool]$task;taskState=if($task){$task.State}else{$null};taskEnabled=if($task){$task.Enabled}else{$false};taskCommand=if($task){$task.Definition.Actions.Item(1).Arguments}else{$null};paused=(Test-Path -LiteralPath $paused);gateAlive=[bool]$live;gateParentPid=if($live){$live.ParentProcessId}else{$null};runtime=$runtime;manager=$manager;claims=@(Get-ChildItem $outbox -Filter '*.dispatch.json' | ForEach-Object {Get-Content $_.FullName -Raw | ConvertFrom-Json});log=(Join-Path $outbox 'gate.jsonl')} | ConvertTo-Json -Depth 15
