$ErrorActionPreference='Stop'
. "$PSScriptRoot/agent-events.ps1"
$root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-core-apply-'+[guid]::NewGuid().ToString('N'))
foreach($dir in @("$root/tmp/agent-core/events","$root/tmp/agent-core/results","$root/tmp/agent-flow","$root/tmp/agent-gate","$root/tmp/agent-queue","$root/tmp/automation-console")){[IO.Directory]::CreateDirectory($dir)|Out-Null}
Set-Content "$root/prompt.md" 'fixture';Set-Content "$root/integrate.md" 'fixture';Set-Content "$root/verify.ps1" 'exit 0'
$a=@{id='A';enabled=$true;model='devin/swe-2';harness='codex';worktree="$root/worktrees/A";allowedPaths=@('modules/a.txt');integrationPaths=@();dependsOn=@();integrationDependsOn=@();gates=@('G1');promptFile="$root/prompt.md";integrationPromptFile="$root/integrate.md";verificationFile="$root/verify.ps1"}
$b=$a.Clone();$b.id='B';$b.worktree="$root/worktrees/B";$b.allowedPaths=@('modules/b.txt');$b.dependsOn=@('A')
Save-AgentJson "$root/tmp/agent-flow/plan.json" @{id='test';repository=$root;schemaVersion=1;managerModel='devin/swe-2';codexExecutable='NEVER-CALLED.exe';mechanismTest=$true;tasks=@($a,$b);gates=@{G1='open'}}
Save-AgentJson "$root/tmp/agent-flow/runtime.json" @{status='stopped'}
Save-AgentJson "$root/tmp/agent-gate/manager.json" @{status='ready'}
Save-AgentJson "$root/tmp/automation-console/policy.json" @{mode='manual'}
& pwsh -NoProfile -File "$PSScriptRoot/agent-workflow.ps1" -Mode Approve -Repository $root
if($LASTEXITCODE){throw 'Fixture approval failed'}
$key='a'*64;$hash=(Get-FileHash "$root/tmp/agent-flow/plan.json").Hash
Save-AgentJson "$root/tmp/agent-core/results/$key.json" @{role='architect';members=@(@{},@{},@{})}
Save-AgentJson "$root/tmp/agent-core/events/$key.json" @{event=@{basePlanHash=$hash}}
Save-AgentJson "$root/proposal.json" @{type='core.plan.amendment';basePlanHash=$hash;operations=@(@{taskId='A';changes=@{timeoutSeconds=90}},@{taskId='B';changes=@{dependsOn=@('A');timeoutSeconds=100}})}
& pwsh -NoProfile -File "$PSScriptRoot/agent-core-manager.ps1" -Mode Decide -Repository $root -Key $key -Decision accept -Evidence 'Fixture explicit approval' -ProposalFile "$root/proposal.json"
if($LASTEXITCODE){throw 'Decision failed'}
& pwsh -NoProfile -File "$PSScriptRoot/agent-core-manager.ps1" -Mode Apply -Repository $root -Key $key
if($LASTEXITCODE){throw 'Apply failed'}
$plan=Read-AgentJson "$root/tmp/agent-flow/plan.json";$newA=@($plan.tasks|Where-Object {$_.supersedes -ceq 'A'})[0];$newB=@($plan.tasks|Where-Object {$_.supersedes -ceq 'B'})[0]
if($newB.dependsOn[0] -cne $newA.id -or $plan.gates.G1 -cne 'open'){throw 'Dependency remap/gate preservation failed'}
$revision=$plan.revision
& pwsh -NoProfile -File "$PSScriptRoot/agent-core-manager.ps1" -Mode Apply -Repository $root -Key $key
if($LASTEXITCODE -or (Read-AgentJson "$root/tmp/agent-flow/plan.json").revision -ne $revision){throw 'Replay reapplied proposal'}
Save-AgentJson "$root/validation.json" @{status='passed';modelCalls=0;checks=@('explicit approval','versioned operations','dependency remap','gates preserved','replay')}
Write-Output "PASS native approved architecture amendment: $root"
