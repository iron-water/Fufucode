# Execute the actual production guard functions against disposable Git data; zero models.
$ErrorActionPreference='Stop'
$Repository=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-protection-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($Repository)|Out-Null
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile("$PSScriptRoot/agent-workflow.ps1",[ref]$tokens,[ref]$errors)
if($errors){throw $errors}
foreach($name in @('Workflow-Git','Workflow-IndexHash','Workflow-AllowedDirty','Workflow-Protected')) {
    $definition=$ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)|Where-Object Name -eq $name
    . ([scriptblock]::Create($definition.Extent.Text))
}
[void](Workflow-Git $Repository @('init','-b','main'))
[void](Workflow-Git $Repository @('config','user.name','Protection Fixture'))
[void](Workflow-Git $Repository @('config','user.email','fixture@localhost'))
Set-Content "$Repository/用户 已跟踪.txt" 'original';Set-Content "$Repository/module.txt" 'original'
[void](Workflow-Git $Repository @('add','--','用户 已跟踪.txt','module.txt'))
[void](Workflow-Git $Repository @('commit','-m','fixture baseline'))
if(Workflow-AllowedDirty @('module.txt')){throw 'Clean scope reported dirty'}
Set-Content "$Repository/用户 已跟踪.txt" 'inherited edit'
Set-Content "$Repository/用户 未跟踪.txt" 'untracked inherited'
$protected=Workflow-Protected @('module.txt')
foreach($path in @('用户 已跟踪.txt','用户 未跟踪.txt')){if($protected[$path] -cne (Get-FileHash (Join-Path $Repository $path)).Hash){throw 'Chinese/space inherited path was not actually hashed'}}
$before=Workflow-IndexHash
[void](Workflow-Git $Repository @('add','--','用户 已跟踪.txt'))
if((Workflow-IndexHash) -ceq $before){throw 'Unwanted staging not detected'}
$same=Workflow-Protected @('module.txt')
foreach($path in $protected.Keys){if($same[$path] -cne $protected[$path]){throw 'Staging fixture unexpectedly changed user bytes'}}
Set-Content "$Repository/module.txt" 'uncommitted wiring'
if(-not (Workflow-AllowedDirty @('module.txt'))){throw 'Uncommitted wiring not detected'}
Set-Content "$Repository/validation.json" (@{passed=$true;modelCalls=0;guards=@('real unicode/space protected hashes','unchanged user bytes across staging','index staging drift detected','uncommitted task wiring detected','clean task scope accepted');sourceSha256=(Get-FileHash "$PSScriptRoot/agent-workflow.ps1").Hash}|ConvertTo-Json)
Write-Output "PASS actual guard functions: Unicode/space inherited files, staging drift, task dirty scope. Evidence: $Repository"
