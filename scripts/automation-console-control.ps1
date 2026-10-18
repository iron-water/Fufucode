[CmdletBinding()]
param([ValidateSet('Start','Status','Stop')][string]$Mode='Status',
 [string]$Repository=(Split-Path $PSScriptRoot -Parent),[int]$Port=18765)
$ErrorActionPreference='Stop'
$Repository=(Resolve-Path -LiteralPath $Repository).Path
$box=Join-Path $Repository 'tmp/automation-console'
[IO.Directory]::CreateDirectory($box)|Out-Null
$key=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Repository.ToLowerInvariant()))).Substring(0,12)
$name="Fufucode-Console-$key"
$scheduler=New-Object -ComObject Schedule.Service
$scheduler.Connect();$folder=$scheduler.GetFolder('\')
$existing=$null;try{$existing=$folder.GetTask($name)}catch{}
if($Mode -eq 'Status'){
 $runtime=if(Test-Path "$box/server.json"){Get-Content "$box/server.json" -Raw|ConvertFrom-Json}else{$null}
 $live=if($runtime){Get-CimInstance Win32_Process -Filter "ProcessId=$($runtime.pid)"|Where-Object {$_.CommandLine.Contains('automation-console.py') -and $_.CommandLine.Contains($Repository)}}else{$null}
 @{task=$name;alive=[bool]$live;runtime=$runtime;log="$box/server.stderr.log"}|ConvertTo-Json -Depth 6;exit
}
if($Mode -eq 'Stop'){
 if($existing){$existing.Enabled=$false}
 $url="http://127.0.0.1:$Port"
 try {
  $session=Invoke-RestMethod "$url/api/session"
  Invoke-RestMethod "$url/api/command" -Method Post -Headers @{'Origin'=$url;'X-Console-Token'=$session.token} -ContentType 'application/json' -Body '{"action":"shutdown"}' | Out-Null
 }catch{Write-Output 'Console API not reachable; inspect its PID before any restart.'}
 Write-Output 'Console task disabled/stopped; production gate and workflow unchanged.';exit
}
if($existing){$existing.Enabled=$true;if($existing.State -ne 4){[void]$existing.Run($null)};Write-Output "http://127.0.0.1:$Port";exit}
$python=(Get-Command python.exe).Source
$command='"'+$python+'" "'+(Join-Path $PSScriptRoot 'automation-console.py')+'" --repository "'+$Repository+'" --port '+$Port
$hostScript='var s=new ActiveXObject("WScript.Shell");s.CurrentDirectory='+($Repository|ConvertTo-Json -Compress)+';WScript.Quit(s.Run('+($command|ConvertTo-Json -Compress)+',0,true));'
Set-Content -LiteralPath "$box/host.js" -Value $hostScript -Encoding ascii
$definition=$scheduler.NewTask(0);$definition.Principal.UserId=[Security.Principal.WindowsIdentity]::GetCurrent().Name
$definition.Principal.LogonType=3;$definition.Principal.RunLevel=0
$definition.Settings.ExecutionTimeLimit='PT0S';$definition.Settings.MultipleInstances=2
$definition.Settings.DisallowStartIfOnBatteries=$false;$definition.Settings.StopIfGoingOnBatteries=$false
$definition.Settings.RestartCount=3;$definition.Settings.RestartInterval='PT1M'
$trigger=$definition.Triggers.Create(9);$trigger.UserId=$definition.Principal.UserId
$action=$definition.Actions.Create(0);$action.Path=(Get-Command wscript.exe).Source;$action.WorkingDirectory=$Repository
$action.Arguments='//B //Nologo //E:JScript "'+(Join-Path $box 'host.js')+'"'
$registered=$folder.RegisterTaskDefinition($name,$definition,2,$definition.Principal.UserId,$null,3)
[void]$registered.Run($null)
Write-Output "http://127.0.0.1:$Port"
