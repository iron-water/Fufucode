<#
Dev-only: produces runtime/node + runtime/pi used only by the Pi backend plugin.

警告：这是开发期工具，用于在本机拉取受管 Node 与 Pi 依赖（供 pi-probe 探针、
Pi 后端插件调试等场景）。生产默认包（scripts/packaging/package.ps1 与
package-native.ps1）均不依赖、也不打包 runtime/pi 或 runtime/node。
#>
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$nodeCommand = Get-Command node -ErrorAction Stop
$nodeVersion = (& $nodeCommand.Source --version).Trim()
if ($nodeVersion -ne 'v24.15.0') {
    throw "Expected Node v24.15.0; found $nodeVersion at $($nodeCommand.Source)."
}
$nodeDirectory = Join-Path $projectRoot 'runtime/node'
New-Item -ItemType Directory -Force $nodeDirectory | Out-Null
New-Item -ItemType Directory -Force (Join-Path $projectRoot 'logs'), (Join-Path $projectRoot 'tmp') | Out-Null
Copy-Item -LiteralPath $nodeCommand.Source -Destination (Join-Path $nodeDirectory 'node.exe') -Force
npm ci --prefix (Join-Path $projectRoot 'runtime/pi') --ignore-scripts
if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
$taskPiPackage = Get-Content -LiteralPath (Join-Path $projectRoot 'runtime/pi/node_modules/@earendil-works/pi-coding-agent/package.json') -Raw | ConvertFrom-Json
Write-Host "Pi runtime ready: Node $nodeVersion; Pi $($taskPiPackage.version)"
