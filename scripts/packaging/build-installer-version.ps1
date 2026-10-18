param(
    [Parameter(Mandatory = $true)][string]$AppVersion,
    [Parameter(Mandatory = $true)][string]$OutName,
    # 双渠道：默认全量渠道（Fufucode.iss + out/build）；原生渠道传 Fufucode-Native.iss + out/build-native。
    [string]$IssName = 'Fufucode.iss',
    [string]$PackageRoot,
    # 原生渠道的清单文件名与全量渠道不同，且由 package-native.ps1 提前写好，因此跳过热渠道专用的清单重写。
    [switch]$NoManifest
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($PackageRoot)) { $PackageRoot = Join-Path $root 'out/build' }
. (Join-Path $PSScriptRoot 'resolve-iscc.ps1')
$iscc = Resolve-InnoCompiler
$iss = Join-Path $root "scripts/packaging/$IssName"
$integrity = Join-Path $root 'scripts/packaging/integrity.ps1'

# Integrity manifest is part of the shipped payload and is installed by the installer.
if (-not $NoManifest) {
    & $integrity -Mode Write -PackageRoot $PackageRoot
    if ($LASTEXITCODE -ne 0) { throw 'integrity manifest generation failed' }
}

& $iscc $iss "/DAppVersion=$AppVersion" "/DOutName=$OutName"
if ($LASTEXITCODE -ne 0) { throw "installer compilation failed for $AppVersion" }

Write-Host "built $OutName.exe"
