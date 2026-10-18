# Local event-driven control reconciler; idle never invokes a model.
[CmdletBinding()]
param([ValidateSet('Register','Run','Status','Stop')][string]$Mode='Status',[string]$Repository=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$Repository=(Resolve-Path -LiteralPath $Repository).Path
. "$PSScriptRoot/agent-events.ps1"
$box="$Repository/tmp/agent-flow/controls"
[IO.Directory]::CreateDirectory($box)|Out-Null
$key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Repository.ToLowerInvariant()))).Substring(0,12)
$name="Fufucode-TaskControl-$key"
if($Mode -eq 'Status'){if(Test-Path "$box/runtime.json"){Get-Content "$box/runtime.json" -Raw};exit}
if($Mode -eq 'Stop'){Save-AgentJson "$box/host-stop.json" @{stop=$true};exit}
if($Mode -eq 'Register'){
 if(Test-Path "$box/host-stop.json"){Remove-Item -LiteralPath "$box/host-stop.json"}
 $scheduler=New-Object -ComObject Schedule.Service;$scheduler.Connect();$folder=$scheduler.GetFolder('\')
 $existing=$null;try{$existing=$folder.GetTask($name)}catch{}
 if($existing){if($existing.State -ne 4){[void]$existing.Run($null)};Write-Output $name;exit}
 $command='"'+(Get-Command pwsh.exe).Source+'" -NoProfile -NonInteractive -File "'+$PSCommandPath+'" -Mode Run -Repository "'+$Repository+'"'
 Set-Content "$box/host.js" ('var s=new ActiveXObject("WScript.Shell");s.CurrentDirectory='+($Repository|ConvertTo-Json -Compress)+';WScript.Quit(s.Run('+($command|ConvertTo-Json -Compress)+',0,true));') -Encoding ascii
 $d=$scheduler.NewTask(0);$d.Principal.UserId=[Security.Principal.WindowsIdentity]::GetCurrent().Name;$d.Principal.LogonType=3
 $d.Settings.ExecutionTimeLimit='PT0S';$d.Settings.MultipleInstances=2;$d.Settings.RestartCount=3;$d.Settings.RestartInterval='PT1M'
 $d.Settings.DisallowStartIfOnBatteries=$false;$d.Settings.StopIfGoingOnBatteries=$false
 $t=$d.Triggers.Create(9);$t.UserId=$d.Principal.UserId
 $a=$d.Actions.Create(0);$a.Path=(Get-Command wscript.exe).Source;$a.Arguments='//B //Nologo //E:JScript "'+"$box/host.js"+'"';$a.WorkingDirectory=$Repository
 $registered=$folder.RegisterTaskDefinition($name,$d,2,$d.Principal.UserId,$null,3);[void]$registered.Run($null);Write-Output $name;exit
}
$lease=[IO.File]::Open("$box/host.lock",'OpenOrCreate','ReadWrite','None')
$watch=New-AgentFileWatch "$Repository/tmp" "TaskControl-$PID"
try {
 Save-AgentJson "$box/runtime.json" @{pid=$PID;status='listening';at=[DateTime]::UtcNow.ToString('o')}
 do{
  if(Test-Path "$box/host-stop.json"){break}
  $bindingPath="$Repository/tmp/agent-gate/manager.json"
  if((Test-Path $bindingPath) -and (Read-AgentJson $bindingPath).status -eq 'ready'){
   & pwsh.exe -NoProfile -File "$PSScriptRoot/agent-human-control.ps1" -Mode Dispatch -Repository $Repository
   & pwsh.exe -NoProfile -File "$PSScriptRoot/agent-core-manager.ps1" -Mode Dispatch -Repository $Repository
   foreach($proposalDecision in Get-ChildItem "$Repository/tmp/agent-core" -Filter 'manager-*.json' -ErrorAction SilentlyContinue){
    if($proposalDecision.Name -like 'manager-dispatch-*'){continue}
    $record=Read-AgentJson $proposalDecision.FullName
    if($record.decision -eq 'accept' -and -not $record.applied -and $record.proposalFile -and -not (Test-Path "$Repository/tmp/agent-core/apply-$($record.key).json")){
     & pwsh.exe -NoProfile -File "$PSScriptRoot/agent-core-manager.ps1" -Mode Apply -Repository $Repository -Key $record.key
    }
   }
   foreach($file in Get-ChildItem $box -Filter 'command-*.json'){
    $c=Read-AgentJson $file.FullName
    if($c.status -ne 'queued'){continue}
    $args=@('-NoProfile','-File',"$PSScriptRoot/agent-task-control.ps1",'-Mode',$c.mode,'-Repository',$Repository,'-TaskId',$c.taskId,'-CommandId',$c.commandId)
    if($c.requestFile){$args+=@('-RequestFile',$c.requestFile)}
    $output=& pwsh.exe @args 2>&1
    if($LASTEXITCODE){$c.status='needs_attention';$c.error="$output";Save-AgentJson $file.FullName $c}
   }
   $draftPath="$Repository/tmp/automation-console/draft.json"
   if((Test-Path $draftPath) -and (Read-AgentJson $draftPath).status -eq 'queued_apply'){
    $active=$false
    foreach($job in Get-ChildItem "$Repository/tmp/agent-queue" -Directory -ErrorAction SilentlyContinue){$statePath=Join-Path $job.FullName 'state.json';if((Test-Path $statePath) -and (Read-AgentJson $statePath).status -in @('starting','running')){$active=$true}}
    if(-not $active){
     $out=& python.exe -B "$PSScriptRoot/automation-console.py" --repository $Repository --apply-draft 2>&1
     if($LASTEXITCODE){$draft=Read-AgentJson $draftPath;$draft.status='needs_attention';$draft.error="$out";Save-AgentJson $draftPath $draft}
     elseif((Read-AgentJson "$Repository/tmp/automation-console/policy.json").mode -eq 'auto'){
      & pwsh.exe -NoProfile -File "$PSScriptRoot/agent-gate-control.ps1" -Mode Resume -Repository $Repository
      & pwsh.exe -NoProfile -File "$PSScriptRoot/agent-workflow.ps1" -Mode Resume -Repository $Repository
     }
    }
   }
  }
  [void](Wait-AgentFileWatch $watch)
 }while($true)
}finally{Save-AgentJson "$box/runtime.json" @{pid=$PID;status='stopped';at=[DateTime]::UtcNow.ToString('o')};Close-AgentFileWatch $watch;$lease.Dispose()}
