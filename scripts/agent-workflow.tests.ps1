# Local-only concurrency/lifecycle guards. No model is called.
[CmdletBinding()]
param([ValidateSet('Run','Writer')][string]$Mode='Run',[string]$Root)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/agent-events.ps1"
if($Mode -eq 'Writer') {
    foreach($n in 1..3000){Save-AgentJson "$Root/state.json" @{schemaVersion=1;sequence=$n;payload=('x'*100)}}
    exit
}
$Root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-local-flow-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory("$Root/tmp/agent-flow")|Out-Null
Save-AgentJson "$Root/state.json" @{schemaVersion=1;sequence=0;payload=('x'*100)}
$psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName=(Get-Command pwsh.exe).Source;$psi.WorkingDirectory=$Root;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
$psi.RedirectStandardError=$true
foreach($arg in @('-NoProfile','-File',$PSCommandPath,'-Mode','Writer','-Root',$Root)){$psi.ArgumentList.Add($arg)}
$p=[Diagnostics.Process]::Start($psi);$reads=0
do {
    $state=Read-AgentJson "$Root/state.json"
    if($state.schemaVersion -ne 1 -or $state.payload.Length -ne 100){throw 'Partial JSON visible to concurrent reader'}
    $reads++
}while(-not $p.HasExited)
if($p.ExitCode){throw "Writer failed $($p.ExitCode): $($p.StandardError.ReadToEnd())"};$p.Dispose()
Set-Content "$Root/prompt.md" 'no model';Set-Content "$Root/integrate.md" 'no model';Set-Content "$Root/verify.ps1" 'exit 0'
$baseTask=@{id='A';model='devin/swe-2';harness='omp';allowedPaths=@('modules/a.txt');integrationPaths=@();dependsOn=@();promptFile="$Root/prompt.md";integrationPromptFile="$Root/integrate.md";verificationFile="$Root/verify.ps1"}
$plan=@{schemaVersion=1;id='local';repository=$Root;mechanismTest=$true;managerModel='devin/swe-2';codexExecutable='not-called.exe';tasks=@($baseTask)}
$control="$PSScriptRoot/agent-workflow.ps1"
function Approve($Expected) {
    Save-AgentJson "$Root/tmp/agent-flow/plan.json" $plan
    $result=& pwsh.exe -NoProfile -File $control -Mode Approve -Repository $Root 2>&1
    if(($LASTEXITCODE -eq 0) -ne $Expected){throw "Plan approval guard failed: $result"}
}
Approve $true
$plan.managerModel='gpt-6.1-sol';Approve $false;$plan.managerModel='devin/swe-2'
$baseTask.allowedPaths=@('*');Approve $false;$baseTask.allowedPaths=@('modules/a.txt')
$baseTask.dependsOn=@('A');Approve $false;$baseTask.dependsOn=@()
$baseTask.dependsOn=@('UNKNOWN');Approve $false;$baseTask.dependsOn=@()
$plan.tasks=@($baseTask,$baseTask);Approve $false;$plan.tasks=@($baseTask)
$plan.repository='C:/Projects/Fucode/Fufucode';Approve $false
Save-AgentJson "$Root/validation.json" @{passed=$true;reads=$reads;atomicWrites=3000;guards=@('explicit cheap test model','bounded ownership paths','self dependency','unknown dependency','duplicate identity','repository mismatch');modelCalls=0}
Write-Output "PASS: $reads concurrent reads / 3000 atomic writes, no missing/partial JSON; plan guards; zero models. Evidence: $Root"
