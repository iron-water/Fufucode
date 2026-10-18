$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $projectRoot
$node = Join-Path $projectRoot 'runtime/node/node.exe'
$cli = Join-Path $projectRoot 'runtime/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js'
if (!(Test-Path -LiteralPath $node) -or !(Test-Path -LiteralPath $cli)) {
    throw 'Pi runtime is missing. Run scripts/setup-runtime.ps1 with Node v24.15.0 installed.'
}
dotnet build TestApp/TestApp.csproj
if ($LASTEXITCODE -ne 0) { throw 'Avalonia build failed.' }
dotnet build tests/Fufucode.FakePi/Fufucode.FakePi.csproj
if ($LASTEXITCODE -ne 0) { throw 'FakePi build failed.' }
dotnet run --project tests/Fufucode.Pi.Tests/Fufucode.Pi.Tests.csproj
if ($LASTEXITCODE -ne 0) { throw 'RPC tests failed.' }
& $node scripts/pi-baseline.mjs
if ($LASTEXITCODE -ne 0) { throw 'Pi baseline failed.' }
