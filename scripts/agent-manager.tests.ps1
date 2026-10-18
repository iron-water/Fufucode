$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-manager-tests-'+[guid]::NewGuid().ToString('N'))
$outbox=Join-Path $root 'tmp/agent-gate'
[IO.Directory]::CreateDirectory($outbox)|Out-Null
. (Join-Path $PSScriptRoot 'agent-events.ps1')
. (Join-Path $PSScriptRoot 'agent-manager.ps1')
function Reject($Action,$Label){$rejected=$false;try{& $Action}catch{$rejected=$true};if(-not $rejected){throw "Accepted unsafe manager: $Label"}}
$path=Join-Path $outbox 'manager.json'
$binding=@{schemaVersion=1;repository=$root;model='devin/swe-2';threadId='00000000-0000-0000-0000-000000000001';status='ready'}
Save-AgentJson $path $binding
[void](Read-AgentManager $root 'devin/swe-2')
Reject {Read-AgentManager $root 'gpt-6.1-sol'} 'model drift'
$binding.status='inflight'; Save-AgentJson $path $binding
Reject {Read-AgentManager $root 'devin/swe-2'} 'inflight'
$binding.status='needs_attention'; Save-AgentJson $path $binding
Reject {Read-AgentManager $root 'devin/swe-2'} 'failed or ambiguous'
$before=(Get-FileHash $path).Hash
Reject {Invoke-AgentManagerTurn -Repository $root -Model 'devin/swe-2' -CodexExecutable (Get-Command pwsh.exe).Source -RunId 'init' -Prompt 'no model' -Initialize} 'duplicate initialize'
if((Get-FileHash $path).Hash -ne $before){throw 'Duplicate initialize overwrote binding'}
$binding.status='ready';$binding.repository=$repo;Save-AgentJson $path $binding
Reject {Read-AgentManager $root 'devin/swe-2'} 'repository drift'
$binding.repository=$root;$binding.threadId=$env:CODEX_THREAD_ID;Save-AgentJson $path $binding
Reject {Read-AgentManager $root 'devin/swe-2'} 'current interactive thread'
# A new directory tests native launch failure; existing binding is never deleted.
$failedRoot=Join-Path $root 'native-failure'
[IO.Directory]::CreateDirectory((Join-Path $failedRoot 'tmp/agent-gate'))|Out-Null
Reject {Invoke-AgentManagerTurn -Repository $failedRoot -Model 'devin/swe-2' -CodexExecutable (Get-Command pwsh.exe).Source -RunId 'init' -Prompt 'no model' -Initialize} 'missing turn identity'
$failure=Get-Content (Join-Path $failedRoot 'tmp/agent-gate/manager.json') -Raw|ConvertFrom-Json
if($failure.status -ne 'needs_attention'){throw 'Native failure did not persist unresolved state'}
Write-Output "PASS: model/repository/current-thread guards, inflight/failed freeze, duplicate initialize unchanged, native failure persisted. Evidence: $root"
