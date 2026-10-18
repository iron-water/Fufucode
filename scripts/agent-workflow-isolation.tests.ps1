# Real dispatcher/worker with a local failing CLI stub. No model/network is called.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/agent-events.ps1"
$root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-isolation-'+[guid]::NewGuid().ToString('N'))
$box="$root/tmp/agent-flow";$job="$root/tmp/agent-queue/isolation-A-1";$client="$root/tmp/local-client"
foreach($dir in @($box,$job,$client)){[IO.Directory]::CreateDirectory($dir)|Out-Null}
& git.exe -C $root init -b main|Out-Null
& git.exe -C $root config user.name Fixture
& git.exe -C $root config user.email fixture@localhost
Set-Content "$root/.gitignore" "tmp/`nworktrees/`n"
Set-Content "$root/seed.txt" fixture
& git.exe -C $root add -- .gitignore seed.txt
& git.exe -C $root commit -m fixture|Out-Null
if($LASTEXITCODE){throw 'Fixture Git failed'}
$head=(& git.exe -C $root rev-parse HEAD).Trim()
Set-Content "$client/LocalClient.csproj" '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
Set-Content "$client/Program.cs" @'
System.Console.In.ReadToEnd();
System.Console.WriteLine("{\"type\":\"thread.started\",\"thread_id\":\"00000000-0000-4000-8000-000000000001\"}");
System.Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Local stub failure; no model was called\"}}");
System.Console.WriteLine("{\"type\":\"turn.completed\"}");
return 2;
'@
& dotnet.exe build "$client/LocalClient.csproj" -c Release --nologo|Out-Null
if($LASTEXITCODE){throw 'Local stub build failed'}
Set-Content "$box/prompt.md" 'Local stub only';Set-Content "$box/integrate.md" 'Never integrate a failed local stub';Set-Content "$box/verify.ps1" 'exit 0'
$tasks=@()
foreach($id in @('A','B')){$tasks+=@{id=$id;model='devin/swe-2';harness='codex';worktree="$root/worktrees/$id";allowedPaths=@('module.txt');dependsOn=@();integrationDependsOn=@();integrationPaths=@();gates=@('G0');promptFile="$box/prompt.md";integrationPromptFile="$box/integrate.md";verificationFile="$box/verify.ps1";timeoutSeconds=60}}
Save-AgentJson "$box/plan.json" @{schemaVersion=1;id='isolation';repository=$root;mechanismTest=$true;managerModel='devin/swe-2';codexExecutable="$client/bin/Release/net10.0/LocalClient.exe";gates=@{G0='passed'};tasks=$tasks}
$manifest=@{runId=[guid]::NewGuid().ToString('N');workflowId='isolation';task='A';worktree=$root;model='devin/swe-2';harness='codex';attempt=1}
Save-AgentJson "$job/manifest.json" $manifest
Save-AgentJson "$job/state.json" @{status='failed_or_missing_card';exitCode=2;finished=[DateTime]::UtcNow.ToString('o');head=$head;workerPid=0;agentPid=0}
Write-AgentEvent $job $manifest 'delivery.invalid' @{reason='deliberate local failure'}
$control="$PSScriptRoot/agent-workflow.ps1"
& pwsh.exe -NoProfile -File $control -Mode Approve -Repository $root;if($LASTEXITCODE){throw 'Approval failed'}
& pwsh.exe -NoProfile -File $control -Mode Register -Repository $root;if($LASTEXITCODE){throw 'Register failed'}
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    do{if((Test-Path "$box/blocked-A.json") -and (Test-Path "$box/launch-B-1.json") -and (Read-AgentJson "$box/launch-B-1.json").status -eq 'launched'){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
    if(-not (Test-Path "$box/blocked-A.json") -or (Read-AgentJson "$box/launch-B-1.json").status -ne 'launched'){throw 'Failed A stopped independent B dispatcher'}
    if((Read-AgentJson "$box/runtime.json").status -eq 'needs_attention'){throw 'Failure still terminated whole workflow'}
    if(Test-Path "$box/launch-A-2.json"){throw 'Failed A auto resent'}
    if(Test-Path "$job/events/04.json") {if((Read-AgentJson "$job/events/04.json").type -ne 'delivery.invalid'){throw 'Failed A received'}}
    Save-AgentJson "$root/validation.json" @{passed=$true;modelCalls=0;A='persisted blocked/no retry';B='actual independent dispatch to local failing client';evidence="$box/events.jsonl";sourceSha256=(Get-FileHash "$PSScriptRoot/agent-workflow.ps1").Hash}
    Write-Output "PASS actual dispatcher: failed A parked, independent B launched, no A resend/no models. Evidence: $root"
}finally{& pwsh.exe -NoProfile -File $control -Mode Stop -Repository $root}
