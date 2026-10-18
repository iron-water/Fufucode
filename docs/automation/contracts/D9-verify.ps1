param([string]$Repository)
$ErrorActionPreference="Stop"
$project=Join-Path $Repository 'tests/Fufucode.D9.Tests/Fufucode.D9.Tests.csproj'
if(-not (Test-Path $project)){throw "Missing independent behavior tests"}
& dotnet.exe run --project $project -c Release
if($LASTEXITCODE){throw "Behavior tests failed"}
& dotnet.exe build (Join-Path $Repository "src/Fufucode.App/Fufucode.App.csproj") -c Release --nologo
if($LASTEXITCODE){throw "Product build failed"}
Write-Output "PASS module only; native gates remain unchanged"
