# Sends Ctrl+C only to a disposable private console, never the user's current console.
[CmdletBinding()]
param([string]$Repository,[switch]$Launcher,[string]$Evidence)
$ErrorActionPreference='Stop'
if($Launcher) {
    Add-Type 'using System; using System.Runtime.InteropServices; public static class DisposableConsole { [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GenerateConsoleCtrlEvent(uint code,uint group); [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow(); }'
    $window=[DisposableConsole]::GetConsoleWindow();if($window -eq [IntPtr]::Zero){throw 'Disposable launcher has no private console'}
    Set-Content -LiteralPath $Evidence -Value "pid=$PID; private console=$window; sending Ctrl+C"
    if(-not [DisposableConsole]::GenerateConsoleCtrlEvent(0,0)){throw 'Ctrl+C signal rejected'}
    Start-Sleep -Seconds 2
    Set-Content ($Evidence+'.not-interrupted') 'Signal did not interrupt launcher'
    exit 13
}
if(-not $Repository -or -not (Test-Path "$Repository/tmp/flow/config.json")){throw 'Only isolated flow fixture is supported'}
$Repository=(Resolve-Path $Repository).Path
$gate=Get-Content "$Repository/tmp/agent-gate/runtime.json" -Raw|ConvertFrom-Json
$workflow=Get-Content "$Repository/tmp/agent-flow/runtime.json" -Raw|ConvertFrom-Json
$processIds=@($gate.pid,$workflow.pid)
foreach($directory in Get-ChildItem "$Repository/tmp/agent-queue" -Directory){$state=Get-Content "$($directory.FullName)/state.json" -Raw|ConvertFrom-Json;if($state.status -eq 'running'){$processIds+=@($state.workerPid,$state.agentPid)}}
$processIds=@($processIds|Where-Object {$_}|Select-Object -Unique)
foreach($processId in $processIds){if(-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)){throw 'Fixture service absent before console test'}}
$caseId=[guid]::NewGuid().ToString('N')
$Evidence="$Repository/tmp/flow/console-launcher-$caseId.txt"
$launchCommand='"'+(Get-Command pwsh.exe).Source+'" -NoProfile -File "'+$PSCommandPath+'" -Launcher -Evidence "'+$Evidence+'"'
$hostFile="$Repository/tmp/flow/console-test-$caseId.js"
$resultFile="$Repository/tmp/flow/console-exit-$caseId.txt"
$js='var s=new ActiveXObject("WScript.Shell");var c=s.Run('+($launchCommand|ConvertTo-Json -Compress)+',0,true);var f=new ActiveXObject("Scripting.FileSystemObject").CreateTextFile('+($resultFile|ConvertTo-Json -Compress)+',true);f.WriteLine(c);f.Close();'
Set-Content $hostFile $js -Encoding ascii
& wscript.exe //B //Nologo //E:JScript $hostFile
$deadline=[DateTime]::UtcNow.AddSeconds(15)
do{if(Test-Path $resultFile){break};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $deadline)
if(-not (Test-Path $resultFile)){throw 'Private console launcher did not exit'}
$code=(Get-Content $resultFile -Raw).Trim()
if($code -notin @('-1073741510','3221225786','0') -or -not (Test-Path $Evidence) -or (Test-Path ($Evidence+'.not-interrupted'))){throw "Expected actual Ctrl+C interruption, got $code; simulation not proven"}
foreach($processId in $processIds){if(-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)){throw 'Fixture service died when disposable console closed'}}
@{passed=$true;launcherExit=$code;gatePid=$gate.pid;workflowPid=$workflow.pid;survivingPids=$processIds;verifiedAt=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content "$Repository/tmp/flow/console-isolation.json"
Write-Output "PASS: disposable console Ctrl+C exit=$code; gate/workflow survived with original PIDs."
