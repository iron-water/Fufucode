<#
FUFU-06 packaging: builds the independent FUFU desktop app and the Fufucode
Bridge plugin payload in one command.

Usage:
    pwsh fucode/publish-fufu.ps1                      # build everything into out/fufu-06
    pwsh fucode/publish-fufu.ps1 -OutputRoot out\x    # custom output root
    pwsh fucode/publish-fufu.ps1 -NoZip               # skip .zip archives

Outputs under <OutputRoot>:
    fufu\                          self-contained win-x64 fufu.exe (double-click runs standalone)
    service.fufu-bridge\           plugin payload: manifest.json + Fufucode.Plugins.FufuBridge.dll
    fufu-<label>.zip               zipped app (unless -NoZip)
    service.fufu-bridge-<label>.zip zipped plugin payload
    build-info.json                commit + timestamp label

Install plugin: Fufucode Plugin Marketplace -> service.fufu-bridge -> Install,
or copy service.fufu-bridge\ into %LOCALAPPDATA%\Fufucode\plugins\.
Uninstall keeps %LOCALAPPDATA%\FufuPet (pet settings + task-log.jsonl) and
%LOCALAPPDATA%\Fufucode\fufu-bridge.log — no user data is deleted.
#>
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputRoot = '',
    [switch]$NoRestore,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $root 'out\fufu-06' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

$commit = (& git -C $root rev-parse --short HEAD 2>$null)
if (-not $commit) { $commit = 'unknown' }
$label = "fufu-$commit"

$appOut    = Join-Path $OutputRoot 'fufu'
$pluginOut = Join-Path $OutputRoot 'service.fufu-bridge'

Write-Host "[publish-fufu] app -> $appOut"
dotnet publish (Join-Path $root 'src\Fufucode.Fufu\Fufucode.Fufu.csproj') `
    -c $Configuration -r $RuntimeIdentifier --self-contained true `
    -o $appOut $(if ($NoRestore) { '--no-restore' })
if ($LASTEXITCODE -ne 0) { throw "fufu publish failed ($LASTEXITCODE)" }

Write-Host "[publish-fufu] plugin -> $pluginOut"
dotnet publish (Join-Path $root 'src\Fufucode.Plugins.FufuBridge\Fufucode.Plugins.FufuBridge.csproj') `
    -c $Configuration -o (Join-Path $OutputRoot '.plugin-staging') `
    $(if ($NoRestore) { '--no-restore' })
if ($LASTEXITCODE -ne 0) { throw "plugin publish failed ($LASTEXITCODE)" }

# Payload = manifest.json + entry dll only: host resolves Core/App.Abstractions
# from its own ALC; extra copies in the payload would shadow and break type identity.
New-Item -ItemType Directory -Force -Path $pluginOut | Out-Null
Copy-Item (Join-Path $root 'src\Fufucode.Plugins.FufuBridge\manifest.json') $pluginOut
Copy-Item (Join-Path $OutputRoot '.plugin-staging\Fufucode.Plugins.FufuBridge.dll') $pluginOut
Remove-Item -Recurse -Force (Join-Path $OutputRoot '.plugin-staging')

@{ label = $label; commit = $commit; utc = (Get-Date).ToUniversalTime().ToString('o') } |
    ConvertTo-Json | Set-Content (Join-Path $OutputRoot 'build-info.json') -Encoding utf8

if (-not $NoZip) {
    Compress-Archive -Path (Join-Path $appOut '*')    -DestinationPath (Join-Path $OutputRoot "$label.zip") -Force
    Compress-Archive -Path (Join-Path $pluginOut '*') -DestinationPath (Join-Path $OutputRoot "service.fufu-bridge-$commit.zip") -Force
    Write-Host "[publish-fufu] zips written"
}
Write-Host "[publish-fufu] done: $OutputRoot"
