$ErrorActionPreference='Stop'
$root=Resolve-Path (Join-Path $PSScriptRoot '..\..')
. (Join-Path $PSScriptRoot 'resolve-iscc.ps1')
$iscc = Resolve-InnoCompiler
& $iscc (Join-Path $PSScriptRoot 'Fufucode.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}


