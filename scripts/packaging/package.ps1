<#
全量渠道打包脚本：默认零 Pi/Node 载荷。

历史版本会把 runtime/node、runtime/pi（约 149MB node_modules）与 extensions/fufucode-bridge
打进安装包。按"FUMO 原生内核（纯 C# 实现）为默认后端、其它后端以插件身份下发"的方向，
本渠道与 package-native.ps1 等价：只发布 Fufucode.App + Fufucode.Cli。

Pi/OMP/Codex/DSH/Claude 是可选后端插件，由插件市场按需安装并携带各自运行时。
#>
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

# --- 2. 默认包不得包含任何 Node/Pi/扩展载荷 ---
# 默认包只发 FUMO 原生内核（纯 C# 实现）；Pi 后端（含审批桥扩展 fufucode-bridge）改由后端插件包下发。
$forbidden = @('runtime', 'extensions', 'notices')
foreach ($name in $forbidden) {
    if (Test-Path -LiteralPath (Join-Path $packageRoot $name)) {
        throw "Default package must not contain '$name' (managed runtime/extension payload)."
    }
}

# --- 4. Launcher ---
Write-Host "Writing launcher..."
Set-Content -Path (Join-Path $packageRoot 'Fufucode.cmd') -Value '@echo off
setlocal DisableDelayedExpansion
set "FUFUCODE_DATA=%LocalAppData%\Fufucode"
if not exist "%FUFUCODE_DATA%" mkdir "%FUFUCODE_DATA%"
set "APP_DIR=%~dp0app"

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

Set-Content -Path (Join-Path $packageRoot 'cli/Fufucode.Cli.cmd') -Value '@echo off
setlocal DisableDelayedExpansion
"%~dp0Fufucode.Cli.exe" %*
exit /b %ERRORLEVEL%
' -Encoding Ascii

Set-Content -Path (Join-Path $packageRoot 'Fufucode.ps1') -Value '$ErrorActionPreference = ''Stop''
$Env:FUFUCODE_DATA = Join-Path $Env:LocalAppData ''Fufucode''
$null = New-Item -ItemType Directory -Force $Env:FUFUCODE_DATA
$component = if ($args.Count -eq 0 -or $args[0] -eq ''--selftest'') { ''app\Fufucode.App.exe'' } else { ''cli\Fufucode.Cli.exe'' }
$exe = Join-Path $PSScriptRoot $component
& $exe @args
exit $LASTEXITCODE
' -Encoding Utf8

# --- 5. Readme + manifest of shipped components ---
Set-Content -Path (Join-Path $packageRoot 'README.txt') -Value @"
Fufucode release layout

Run:
  Fufucode.cmd              start the app
  Fufucode.cmd --selftest   headless self-test (no desktop session required)
  Fufucode.cmd <command>    run the CLI (for example: settings get --json)
  cli\fufucode.cmd          CLI REPL; installed cli/ is added to the user PATH
  cli\fufucode.exe          native CLI entry (preferred by PATH, direct Ctrl+C)
  fufucode <command>        installed CLI (open a new terminal after installation)
  Fufucode.ps1              PowerShell launcher, same arguments as Fufucode.cmd

Layout:
  app/              self-contained .NET 10.0 win-x64 publish (Avalonia 12.1.3)
  cli/              self-contained .NET 10.0 win-x64 CLI publish + PATH launchers
  Fufucode.cmd/.ps1 set FUFUCODE_DATA to %LocalAppData%\Fufucode

This package ships ONLY the native core (Fufucode.App + Fufucode.Cli). It contains no
Node runtime, no Pi runtime and no bundled extensions, so the default FUMO native
backend (pure C#) starts and runs with zero Node/external-agent processes.

Pi, OMP, Codex, DSH and Claude are optional backend plugins: install them from the
marketplace, which provisions each plugin's own runtime. Never copy runtime/node or
runtime/pi into this layout by hand.

Data is written to %LocalAppData%\Fufucode (agent, sessions, desktop.db, logs, exports;
the legacy pi-agent directory is still read for backward compatibility).
No global node, pi, or npm on PATH is required.

Unverified: code signing and automatic backend plugin upgrade.
The embedded browser requires Microsoft Edge WebView2 Runtime on the target machine.
If unavailable, the app reports the missing runtime and offers an external-browser fallback.
Installer validation evidence is maintained separately in docs/records/.
"@

# --- Build marker: local channel discovery; release builds write the github channel ---
$buildChannel = if ($env:FUFUCODE_BUILD_CHANNEL) { $env:FUFUCODE_BUILD_CHANNEL } else { 'local' }
$githubRepo = $env:FUFUCODE_GITHUB_REPOSITORY
$buildCommit = $null
try { $buildCommit = (& git -C $root rev-parse --short=12 HEAD 2>$null); if ($LASTEXITCODE -ne 0) { $buildCommit = $null } } catch { }
$buildInfo = [ordered]@{
    version = $AppVersion
    builtAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    channel = $buildChannel
}
if ($buildCommit) { $buildInfo.commit = $buildCommit.Trim() }
if (-not [string]::IsNullOrWhiteSpace($githubRepo)) { $buildInfo.githubRepository = $githubRepo }
$buildInfo | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path $packageRoot 'app/build-info.json') -Encoding Utf8
$buildInfo | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path $packageRoot 'cli/build-info.json') -Encoding Utf8
Write-Host "Build marker: channel=$buildChannel version=$AppVersion"

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

# --- 8. 独立发布可选后端插件（与 native 渠道同构：Pi 后端按需安装） ---
$distRoot = Join-Path $root 'out\plugins-dist'
$backendPlugins = @(
    @{ Id = 'backend.pi'; Project = 'src/Fufucode.Plugins.Pi/Fufucode.Plugins.Pi.csproj' },
    @{ Id = 'backend.omp'; Project = 'src/Fufucode.Plugins.Omp/Fufucode.Plugins.Omp.csproj' },
    @{ Id = 'backend.claude'; Project = 'src/Fufucode.Plugins.Claude/Fufucode.Plugins.Claude.csproj' },
    @{ Id = 'backend.codex'; Project = 'src/Fufucode.Plugins.Codex/Fufucode.Plugins.Codex.csproj' },
    @{ Id = 'backend.dsh'; Project = 'src/Fufucode.Plugins.Dsh/Fufucode.Plugins.Dsh.csproj' }
)
foreach ($p in $backendPlugins) {
    $proj = Join-Path $root $p.Project
    if (-not (Test-Path -LiteralPath $proj)) {
        Write-Warning "Backend plugin project missing, skipped: $proj"
        continue
    }
    $targetDir = Join-Path $distRoot $p.Id
    if (Test-Path -LiteralPath $targetDir) { Remove-Item -LiteralPath $targetDir -Recurse -Force }
    Write-Host "Publishing backend plugin '$($p.Id)'..."
    dotnet publish $proj -c Release -o $targetDir
    if ($LASTEXITCODE -ne 0) { throw "Backend plugin $($p.Id) publish failed." }
    foreach ($pattern in @('Fufucode.Core.*', 'Fufucode.App.*', 'Fufucode.App.Abstractions.*', 'Avalonia.*',
            'Microsoft.Data.Sqlite.*', 'SQLitePCLRaw.*', 'CommunityToolkit.Mvvm.*')) {
        Get-ChildItem -Path $targetDir -Filter $pattern | Remove-Item -Force -Recurse
    }
    $runtimesDir = Join-Path $targetDir 'runtimes'
    if (Test-Path -LiteralPath $runtimesDir) { Remove-Item -LiteralPath $runtimesDir -Recurse -Force }
    $manifestSrc = Join-Path (Split-Path $proj) 'manifest.json'
    if (Test-Path -LiteralPath $manifestSrc) { Copy-Item -LiteralPath $manifestSrc -Destination $targetDir -Force }
    Compress-Archive -Path (Join-Path $targetDir '*') -DestinationPath (Join-Path $distRoot "$($p.Id)-1.0.0.zip") -Force
    Write-Host "  -> $($p.Id)-1.0.0.zip"
}
