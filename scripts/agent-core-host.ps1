# Separate local event service: semantic/expert work cannot block control requests.
[CmdletBinding()]
param([ValidateSet('Register','Run','Stop','Status')][string]$Mode='Status',[string]$Repository=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$Repository=(Resolve-Path -LiteralPath $Repository).Path
. "$PSScriptRoot/agent-events.ps1"
$box="$Repository/tmp/agent-core";[IO.Directory]::CreateDirectory($box)|Out-Null
if($Mode -eq 'Stop'){Save-AgentJson "$box/stop.json" @{stop=$true};exit}
if($Mode -eq 'Status'){if(Test-Path "$box/runtime.json"){Get-Content "$box/runtime.json" -Raw};exit}
$key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Repository.ToLowerInvariant()))).Substring(0,12)
$name="Fufucode-Core-$key"
if($Mode -eq 'Register'){
 if(Test-Path "$box/stop.json"){Remove-Item -LiteralPath "$box/stop.json"}
 $scheduler=New-Object -ComObject Schedule.Service;$scheduler.Connect();$folder=$scheduler.GetFolder('\')
 $existing=$null;try{$existing=$folder.GetTask($name)}catch{}
 if($existing){throw 'Existing core owner; inspect rather than replace'}
 $command='"'+(Get-Command pwsh.exe).Source+'" -NoProfile -NonInteractive -File "'+$PSCommandPath+'" -Mode Run -Repository "'+$Repository+'"'
 Set-Content "$box/host.js" ('var s=new ActiveXObject("WScript.Shell");s.CurrentDirectory='+($Repository|ConvertTo-Json -Compress)+';WScript.Quit(s.Run('+($command|ConvertTo-Json -Compress)+',0,true));') -Encoding ascii
 $d=$scheduler.NewTask(0);$d.Principal.UserId=[Security.Principal.WindowsIdentity]::GetCurrent().Name;$d.Principal.LogonType=3
 $d.Settings.ExecutionTimeLimit='PT0S';$d.Settings.MultipleInstances=2;$d.Settings.RestartCount=3;$d.Settings.RestartInterval='PT1M';$d.Settings.DisallowStartIfOnBatteries=$false;$d.Settings.StopIfGoingOnBatteries=$false
 $t=$d.Triggers.Create(9);$t.UserId=$d.Principal.UserId
 $a=$d.Actions.Create(0);$a.Path=(Get-Command wscript.exe).Source;$a.WorkingDirectory=$Repository;$a.Arguments='//B //Nologo //E:JScript "'+"$box/host.js"+'"'
 $task=$folder.RegisterTaskDefinition($name,$d,2,$d.Principal.UserId,$null,3);[void]$task.Run($null);Write-Output $name;exit
}
$lease=[IO.File]::Open("$box/host.lock",'OpenOrCreate','ReadWrite','None')
$watch=New-AgentFileWatch "$Repository/tmp" "Core-$PID"
try{
 Save-AgentJson "$box/runtime.json" @{pid=$PID;status='listening';started=[DateTime]::UtcNow.ToString('o')}
 do{
  if(Test-Path "$box/stop.json"){break}
  if((Test-Path "$box/policy.json") -and (Read-AgentJson "$box/policy.json").enabled){
   $plan=Read-AgentJson "$Repository/tmp/agent-flow/plan.json"
   $roles=@('architect')
   if(-not (Test-Path "$Repository/tmp/agent-gate/paused")){$roles+='validator'}
   & python.exe -B "$PSScriptRoot/agent-core.py" --repository $Repository --dispatch --executable $plan.codexExecutable --roles $roles
  }
  [void](Wait-AgentFileWatch $watch)
 }while($true)
}finally{Save-AgentJson "$box/runtime.json" @{pid=$PID;status='stopped';at=[DateTime]::UtcNow.ToString('o')};Close-AgentFileWatch $watch;$lease.Dispose()}
