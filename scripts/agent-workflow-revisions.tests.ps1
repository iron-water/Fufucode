# Real rule functions with temporary data. No models or real project writes.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/agent-events.ps1"
$root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-revision-rules-'+[guid]::NewGuid().ToString('N'))
$box="$root/tmp/agent-flow";[IO.Directory]::CreateDirectory("$box/controls")|Out-Null
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile("$PSScriptRoot/agent-workflow.ps1",[ref]$tokens,[ref]$errors)
if($errors){throw $errors}
foreach($name in @('Workflow-Revoked','Workflow-ScopeOverlap')){ $function=$ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$false)|Where-Object Name -eq $name;. ([scriptblock]::Create($function.Extent.Text)) }
function Workflow-Read($Path){Read-AgentJson $Path}
if(-not (Workflow-ScopeOverlap @('src/Core/*.cs') @('src/Core/Service.cs'))){throw 'Glob ownership overlap missed'}
if(Workflow-ScopeOverlap @('src/Automation/*') @('src/Scheduling/*')){throw 'Independent modules blocked'}
if(-not (Workflow-ScopeOverlap @('src/Core/Host*') @('src/Core/Host/Process.cs'))){throw 'Ancestor scope overlap missed'}
if(Workflow-Revoked @{id='A';enabled=$true}){throw 'Fresh task incorrectly revoked'}
Save-AgentJson "$box/controls/task-A.json" @{state='revoked'}
if(-not (Workflow-Revoked @{id='A';enabled=$true})){throw 'Durable fence ignored'}
if(-not (Workflow-Revoked @{id='B';enabled=$false})){throw 'Disabled revision dispatched'}
$source=Get-Content "$PSScriptRoot/agent-workflow.ps1" -Raw
if($source -notmatch "planning_required" -or $source -notmatch "waiting_project_gates" -or $source -notmatch "task.failed"){throw 'Project completion/failure isolation rules absent'}
Save-AgentJson "$box/rules-validation.json" @{status='passed';modelCalls=0;checks=@('overlapping scopes serialize','independent modules continue','revocation fence','disabled old revision')}
Write-Output "PASS actual ownership/revocation rules; zero models. Evidence: $root"
