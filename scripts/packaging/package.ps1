param([string]$AppVersion = '1.4.0')
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$packageRoot = Join-Path $root 'out\build'
$zipPath = Join-Path $root 'out\Fufucode.zip'

# A publish must not inherit files from an older release.
if (-not [IO.Path]::GetFullPath($packageRoot).StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output must stay inside the repository.'
}
if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
New-Item -ItemType Directory -Force $packageRoot | Out-Null

$nodeExe = Join-Path $root 'runtime/node/node.exe'
$piRoot = Join-Path $root 'runtime/pi'
$piNodeModules = Join-Path $piRoot 'node_modules'
$piVersion = (Get-Content -LiteralPath (Join-Path $piNodeModules '@earendil-works/pi-coding-agent/package.json') -Raw | ConvertFrom-Json).version
$cli = Join-Path $piNodeModules '@earendil-works/pi-coding-agent/dist/bundle/cli.js'

if (-not (Test-Path -LiteralPath $nodeExe)) {
    throw "Managed node.exe is missing at $nodeExe. Run this script after populating runtime/node."
}
if (-not (Test-Path -LiteralPath $cli)) {
    throw "Pi CLI is missing at $cli. Run 'npm ci --prefix runtime/pi --ignore-scripts' first."
}

# --- 1. .NET self-contained publish (win-x64) ---
foreach ($component in @('App', 'Cli')) {
    $project = Join-Path $root "src/Fufucode.$component/Fufucode.$component.csproj"
    $destination = Join-Path $packageRoot $component.ToLowerInvariant()
    Write-Host "Publishing Fufucode.$component self-contained win-x64..."
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Fufucode.$component publish failed." }
}
# Keep the original apphost for root launchers, and expose a native PATH entry
# so Ctrl+C reaches the CLI directly instead of cmd's batch-job prompt.
Copy-Item -LiteralPath (Join-Path $packageRoot 'cli/Fufucode.Cli.exe') -Destination (Join-Path $packageRoot 'cli/fufucode.exe')

# --- 2. Managed node ---
Write-Host "Copying managed Node..."
$nodeDest = Join-Path $packageRoot 'runtime/node/node.exe'
New-Item -ItemType Directory -Force (Split-Path $nodeDest) | Out-Null
Copy-Item -LiteralPath $nodeExe -Destination $nodeDest -Force
$nodeLicense = Join-Path $root 'runtime/node/LICENSE'
if (-not (Test-Path -LiteralPath $nodeLicense)) { throw 'Managed Node license is missing.' }
Copy-Item -LiteralPath $nodeLicense -Destination (Join-Path (Split-Path $nodeDest) 'LICENSE')

# --- 3. Pi + lockfile (content fixed by A's package-lock.json) ---
Write-Host "Copying managed Pi runtime..."
New-Item -ItemType Directory -Force (Join-Path $packageRoot 'runtime/pi') | Out-Null
Copy-Item -LiteralPath (Join-Path $piRoot 'package.json') -Destination (Join-Path $packageRoot 'runtime/pi/package.json') -Force
Copy-Item -LiteralPath (Join-Path $piRoot 'package-lock.json') -Destination (Join-Path $packageRoot 'runtime/pi/package-lock.json') -Force
Copy-Item -Recurse -Force -LiteralPath $piNodeModules -Destination (Join-Path $packageRoot 'runtime/pi/node_modules')

# --- 3b. Bundled Pi extensions shipped with the app (D Wave2 finding: fufucode-bridge
# is required for approvals/messaging; no external deps). Core discovers the bridge
# beside runtime/. Ships only this production extension — dev fixtures stay out.
$bridgeSource = Join-Path $root 'extensions/fufucode-bridge'
if (-not (Test-Path -LiteralPath (Join-Path $bridgeSource 'index.ts'))) { throw 'Approval bridge is missing.' }
$bridgeDestination = Join-Path $packageRoot 'extensions/fufucode-bridge'
New-Item -ItemType Directory -Force $bridgeDestination | Out-Null
Get-ChildItem -LiteralPath $bridgeSource | Copy-Item -Recurse -Force -Destination $bridgeDestination

& (Join-Path $PSScriptRoot 'collect-notices.ps1') -Destination (Join-Path $packageRoot 'notices')
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $packageRoot 'notices/README.md')

# --- 4. Launcher ---
Write-Host "Writing launcher..."
Set-Content -Path (Join-Path $packageRoot 'Fufucode.cmd') -Value '@echo off
setlocal DisableDelayedExpansion
set "FUFUCODE_DATA=%LocalAppData%\Fufucode"
if not exist "%FUFUCODE_DATA%" mkdir "%FUFUCODE_DATA%"
set "APP_DIR=%~dp0app"
set "NODE=%~dp0runtime\node\node.exe"
set "PI_CLI=%~dp0runtime\pi\node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js"
set "PI_AGENT_DIR=%FUFUCODE_DATA%\pi-agent"
set "SESSION_DIR=%FUFUCODE_DATA%\sessions"
set "PI_CODING_AGENT_DIR=%PI_AGENT_DIR%"

if "%~1"=="" goto gui
if /i "%~1"=="--selftest" goto gui
"%~dp0cli\Fufucode.Cli.exe" %*
exit /b %ERRORLEVEL%
:gui
"%APP_DIR%\Fufucode.App.exe" %*
exit /b %ERRORLEVEL%
' -Encoding Ascii

# The PATH entry is cli/, so bare `fufucode` starts the native CLI REPL; shortcuts
# keep using the root Fufucode.cmd and therefore still open the GUI.
Set-Content -Path (Join-Path $packageRoot 'cli/fufucode.cmd') -Value '@echo off
setlocal DisableDelayedExpansion
"%~dp0Fufucode.Cli.exe" %*
exit /b %ERRORLEVEL%
' -Encoding Ascii

Set-Content -Path (Join-Path $packageRoot 'Fufucode.ps1') -Value '$ErrorActionPreference = ''Stop''
$Env:FUFUCODE_DATA = Join-Path $Env:LocalAppData ''Fufucode''
$Env:PI_CODING_AGENT_DIR = Join-Path $Env:FUFUCODE_DATA ''pi-agent''
$null = New-Item -ItemType Directory -Force $Env:FUFUCODE_DATA
$component = if ($args.Count -eq 0 -or $args[0] -eq ''--selftest'') { ''app\Fufucode.App.exe'' } else { ''cli\Fufucode.Cli.exe'' }
$exe = Join-Path $PSScriptRoot $component
& $exe @args
exit $LASTEXITCODE
' -Encoding Utf8

# --- 5. Readme + manifest of shipped components ---
Set-Content -Path (Join-Path $packageRoot 'README.txt') -Value @"
Fufucode release layout (M7 packaging spike)

Run:
  Fufucode.cmd              start the app
  Fufucode.cmd --selftest   headless self-test (no desktop session required)
  Fufucode.cmd <command>    run the CLI (for example: settings get --json)
  cli\fufucode.cmd          CLI REPL; installed cli/ is added to the user PATH
  cli\fufucode.exe          native CLI entry (preferred by PATH, direct Ctrl+C)
  fufucode <command>        installed CLI (open a new terminal after installation)
  Fufucode.ps1              PowerShell launcher, same arguments as Fufucode.cmd

Runtime layout:
  app/              self-contained .NET 10.0 win-x64 publish (Avalonia 12.1.3)
  cli/              self-contained .NET 10.0 win-x64 CLI publish
  extensions/       required Fufucode approval bridge
  runtime/node/     project-managed Node v24.15.0 (exact binary copied from dev machine)
  notices/          dependency license texts and remaining notice audit gaps
  runtime/pi/       @earendil-works/pi-coding-agent@$piVersion + lockfile, started from runtime/node
  Fufucode.cmd/.ps1 set PI_CODING_AGENT_DIR and --session-dir to %LocalAppData%\Fufucode

Data is written to %LocalAppData%\Fufucode (pi-agent, sessions, desktop.db, logs, exports).
No global node, pi, or npm on PATH is required.

Unverified: code signing and automatic Pi upgrade.
The embedded browser requires Microsoft Edge WebView2 Runtime on the target machine.
If unavailable, the app reports the missing runtime and offers an external-browser fallback.
Installer validation evidence is maintained outside this repository.
"@

# --- 6. publish file integrity manifest ---
& (Join-Path $PSScriptRoot 'integrity.ps1') -Mode Write -PackageRoot $packageRoot

# --- 7. zip it ---
Write-Host "Archiving..."
New-Item -ItemType Directory -Force (Split-Path $zipPath) | Out-Null
& (Join-Path $PSScriptRoot 'archive-package.ps1') -PackageRoot $packageRoot -Destination $zipPath
Write-Host "Package: $zipPath"
$zipSize = (Get-Item $zipPath).Length
Write-Host ("{0:N0} bytes ({1:N2} MB)" -f $zipSize, ($zipSize / 1MB))
Write-Host "Component sizes:"
Get-ChildItem -Directory $packageRoot | ForEach-Object {
    $size = (Get-ChildItem -Recurse -LiteralPath $_.FullName | Measure-Object -Property Length -Sum).Sum
    Write-Host ('  {0}: {1:N1} MB' -f $_.Name, ($size / 1MB))
}

& (Join-Path $PSScriptRoot 'build-installer-version.ps1') -AppVersion $AppVersion -OutName "Fufucode-$AppVersion-setup"
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
