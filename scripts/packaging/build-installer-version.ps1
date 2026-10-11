param(
    [Parameter(Mandatory = $true)][string]$AppVersion,
    [Parameter(Mandatory = $true)][string]$OutName
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'resolve-iscc.ps1')
$iscc = Resolve-InnoCompiler
$iss = Join-Path $root 'scripts/packaging/Fufucode.iss'
$integrity = Join-Path $root 'scripts/packaging/integrity.ps1'

# Integrity manifest is part of the shipped payload and is installed by the installer.
& $integrity -Mode Write -PackageRoot (Join-Path $root 'out/build')
if ($LASTEXITCODE -ne 0) { throw 'integrity manifest generation failed' }

& $iscc $iss "/DAppVersion=$AppVersion" "/DOutName=$OutName"
if ($LASTEXITCODE -ne 0) { throw "installer compilation failed for $AppVersion" }

Write-Host "built $OutName.exe"
