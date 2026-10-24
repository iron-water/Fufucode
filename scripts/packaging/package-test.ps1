# Fufucode 实验性测试渠道安装包（TEST-1）。
#
# 与正式渠道（package-native.ps1 → Fufucode-Native-Setup.exe）的区别只在"安装身份"，
# 载体内容相同：同一套 App + CLI + 随包插件，同样的自包含裁剪运行时。
#
# 身份隔离（四个安装可与正式版并存，互不覆盖）：
#   产物       Fufucode-Test-Setup.exe / FufucodeTest.zip
#   载荷目录   out/build-test
#   AppId      独立 GUID，独立卸载项
#   安装目录   {localappdata}\Programs\Fufucode Test
#   数据根     {localappdata}\FufucodeTest（经安装根目录的 data-root.txt 由启动器注入）
#   缺省协议   fufucode-test: / fufucode-test-approval:
#   单实例     随数据根派生，两个渠道可同时运行
#   版本号     形如 0.1.0-test.3：程序集、安装器、feed.json 三处同源
#   更新渠道   channel=test；GitHub 上只认 prerelease 的 Release
#
# 用法：
#   pwsh scripts/packaging/package-test.ps1 -AppVersion 0.1.0-test.1
#   pwsh scripts/packaging/package-test.ps1 -AppVersion 0.1.0-test.1 -FrameworkDependent
param(
    [Parameter(Mandatory = $true)][string]$AppVersion,
    [string]$OutName,
    # 打包目标平台。默认 Windows x64。
    [string]$RuntimeIdentifier = 'win-x64',
    # 框架依赖瘦身版：不携带 .NET 运行时，要求目标机已安装 .NET 10 Runtime。
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$packageName = if ($FrameworkDependent) { 'build-test-slim' } else { 'build-test' }
$packageRoot = Join-Path $root "out\$packageName"
$zipName = if ($FrameworkDependent) { 'FufucodeTest-Slim.zip' } else { 'FufucodeTest.zip' }
$zipPath = Join-Path $root "out\$zipName"
$manifestName = 'integrity-test.sha256.json'
$dataRootName = 'FufucodeTest'
if ([string]::IsNullOrWhiteSpace($OutName)) {
    $OutName = if ($FrameworkDependent) { 'Fufucode-Test-Slim-Setup' } else { 'Fufucode-Test-Setup' }
}
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

# 测试版本必须是"数字段 + 预发布后缀"：更新比较按 SemVer 排序，且正式渠道永远不会看到它。
if ($AppVersion -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "AppVersion '$AppVersion' 不是合法版本号（期望 0.1.0 或 0.1.0-test.1）。"
}

if (-not [IO.Path]::GetFullPath($packageRoot).StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test package output must stay inside the repository.'
}
if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
New-Item -ItemType Directory -Force $packageRoot | Out-Null

# --- 1. 自包含发布（App 与 CLI 共享一份裁剪运行时；与正式渠道同样的布局） ---
$appDir = Join-Path $packageRoot 'app'
$cliDir = Join-Path $packageRoot 'cli'
New-Item -ItemType Directory -Force $appDir | Out-Null
New-Item -ItemType Directory -Force $cliDir | Out-Null

# 版本号显式下传，保证程序集元数据、build-info.json、安装器与 feed.json 完全一致。
$versionProps = @("--property:Version=$AppVersion")

# 测试渠道不裁剪。实测（2026-10-24）：裁剪后的共享运行时会让随包插件在激活期失效
# ——performance.monitor 在未裁剪构建里正常加载，裁剪构建里则静默不激活，且 CLI 会因
# DefaultInterpolatedStringHandler 的泛型重载被裁掉而 MethodNotFound。
# 实验性构建的目标是"行为正确、可验证"，不是体积；代价是载荷从约 25 MB 涨到约 130 MB。
$trimProps = @('--property:PublishTrimmed=false', '--property:PublishReadyToRun=true')

$cliStaging = Join-Path $packageRoot '.cli-staging'
New-Item -ItemType Directory -Force $cliStaging | Out-Null
Write-Host "Publishing Fufucode.Cli $RuntimeIdentifier (self-contained=$selfContained, version=$AppVersion, trimmed=false)..."
dotnet publish (Join-Path $root 'src/Fufucode.Cli/Fufucode.Cli.csproj') -c Release -r $RuntimeIdentifier `
    --self-contained $selfContained @versionProps @trimProps -o $cliStaging
if ($LASTEXITCODE -ne 0) { throw 'Fufucode.Cli publish failed.' }

Write-Host "Publishing Fufucode.App into shared app layout..."
dotnet publish (Join-Path $root 'src/Fufucode.App/Fufucode.App.csproj') -c Release -r $RuntimeIdentifier `
    --self-contained $selfContained @versionProps @trimProps -o $appDir
if ($LASTEXITCODE -ne 0) { throw 'Fufucode.App publish failed.' }

# 默认随包插件：chat.interactive-ui 落在 app/plugins（宿主首次启动播种）。
$interactiveUiDir = Join-Path $appDir 'plugins/chat.interactive-ui'
dotnet publish (Join-Path $root 'src/Fufucode.Plugins.InteractiveUI/Fufucode.Plugins.InteractiveUI.csproj') `
    -c Release @versionProps -o $interactiveUiDir
if ($LASTEXITCODE -ne 0) { throw 'Interactive UI plugin publish failed.' }
Get-ChildItem -LiteralPath $interactiveUiDir -Filter 'Fufucode.*.dll' |
    Where-Object { $_.Name -ne 'Fufucode.Plugins.InteractiveUI.dll' } |
    Remove-Item -Force
Get-ChildItem -LiteralPath $interactiveUiDir -Filter 'Avalonia*.dll' -ErrorAction SilentlyContinue | Remove-Item -Force
if (Test-Path (Join-Path $interactiveUiDir 'runtimes')) { Remove-Item -LiteralPath (Join-Path $interactiveUiDir 'runtimes') -Recurse -Force }

# 默认随包插件：performance.monitor 必须落在 app/plugins-dist（PluginBootstrapper 从
# <程序目录>/plugins-dist/<id> 播种）。正式渠道只在 out/plugins-dist 出可选 zip，
# 从不把载荷放进包内，导致"默认安装"的插件在安装后永远缺载荷 —— 这里一并修掉。
$performanceDir = Join-Path $appDir 'plugins-dist/performance.monitor'
New-Item -ItemType Directory -Force (Split-Path $performanceDir) | Out-Null
dotnet publish (Join-Path $root 'src/Fufucode.Plugins.PerformanceMonitor/Fufucode.Plugins.PerformanceMonitor.csproj') `
    -c Release @versionProps -o $performanceDir
if ($LASTEXITCODE -ne 0) { throw 'Performance monitor plugin publish failed.' }
Get-ChildItem -LiteralPath $performanceDir -Filter 'Fufucode.*.dll' |
    Where-Object { $_.Name -ne 'Fufucode.Plugins.PerformanceMonitor.dll' } |
    Remove-Item -Force
foreach ($pattern in @('Avalonia*.dll', 'CommunityToolkit*.dll', '*.pdb')) {
    Get-ChildItem -LiteralPath $performanceDir -Filter $pattern -ErrorAction SilentlyContinue | Remove-Item -Force
}
if (Test-Path (Join-Path $performanceDir 'runtimes')) { Remove-Item -LiteralPath (Join-Path $performanceDir 'runtimes') -Recurse -Force }

# 只并入 CLI 入口相关文件；共享框架程序集以 App 的裁剪结果为准。
foreach ($name in @('Fufucode.Cli.exe', 'Fufucode.Cli.dll', 'Fufucode.Cli.deps.json',
        'Fufucode.Cli.runtimeconfig.json')) {
    $source = Join-Path $cliStaging $name
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $appDir $name) -Force
    }
}
Remove-Item -LiteralPath $cliStaging -Recurse -Force

# 共享裁剪运行时冒烟：CLI 必须仍能在这个布局里启动。
$cliProbeData = Join-Path ([IO.Path]::GetTempPath()) ('fufucode-test-cli-probe-' + [Guid]::NewGuid().ToString('N'))
try {
    $previousData = $env:FUFUCODE_DATA_ROOT
    $env:FUFUCODE_DATA_ROOT = $cliProbeData
    $probe = & (Join-Path $appDir 'Fufucode.Cli.exe') --help 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Packaged CLI failed to start on the shared trimmed runtime: $($probe -join [Environment]::NewLine)"
    }
} finally {
    $env:FUFUCODE_DATA_ROOT = $previousData
    if (Test-Path -LiteralPath $cliProbeData) { Remove-Item -LiteralPath $cliProbeData -Recurse -Force -ErrorAction SilentlyContinue }
}

# 平台守卫：测试渠道不裁剪，非 Windows 的 Avalonia 后端会原样出现在载荷里，按 RID 手工移除，
# 免得 Windows 包带上 X11/FreeDesktop/macOS 后端。
$nonWindowsBackends = @('Avalonia.X11.dll', 'Avalonia.FreeDesktop.dll', 'Avalonia.FreeDesktop.AtSpi.dll',
    'Avalonia.Native.dll', 'Tmds.DBus.Protocol.dll')
if ($RuntimeIdentifier -like 'win-*') {
    foreach ($unused in $nonWindowsBackends) {
        $target = Join-Path $appDir $unused
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
    }
}

# --- 2. 入口：原生转发器 + 渠道专属 GUI 启动器 ---
if ($RuntimeIdentifier -like 'win-*') {
    $cliForwarderSrc = Join-Path $root 'tools/fufucode-cli-forwarder/fufucode.exe'
    if (-not (Test-Path -LiteralPath $cliForwarderSrc) -and (Get-Command gcc -ErrorAction SilentlyContinue)) {
        & gcc -O2 -s (Join-Path $root 'tools/fufucode-cli-forwarder/fufucode.c') -o $cliForwarderSrc
    }
    if (Test-Path -LiteralPath $cliForwarderSrc) {
        Copy-Item -LiteralPath $cliForwarderSrc -Destination (Join-Path $cliDir 'fufucode.exe') -Force
        Copy-Item -LiteralPath $cliForwarderSrc -Destination (Join-Path $cliDir 'Fufucode.Cli.exe') -Force
    }

    # 启动器按渠道重编：产品名与版本号写进资源，桌面快捷方式和"应用"列表都显示 Fufucode Test。
    & (Join-Path $PSScriptRoot 'build-launcher.ps1') -Destination (Join-Path $packageRoot 'Fufucode.exe') `
        -AppVersion $AppVersion -ProductName 'Fufucode Test' -InternalName 'FufucodeTest.exe'
    if ($LASTEXITCODE -ne 0) { throw 'Fufucode Test launcher build failed.' }

    # 数据根隔离：启动器读取安装根目录的 data-root.txt 并注入 FUFUCODE_DATA_ROOT。
    Set-Content -LiteralPath (Join-Path $packageRoot 'data-root.txt') -Value $dataRootName -Encoding Ascii

    # 批处理/PowerShell 入口同样先设置数据根，再转交 app，免得绕过启动器启动时串用正式数据。
    Set-Content -Path (Join-Path $packageRoot 'Fufucode.cmd') -Value @"
@echo off
setlocal DisableDelayedExpansion
if not defined FUFUCODE_DATA_ROOT set "FUFUCODE_DATA_ROOT=%LocalAppData%\$dataRootName"
set "APP_DIR=%~dp0app"

if "%~1"=="" goto gui
if /i "%~1"=="--selftest" goto gui
"%APP_DIR%\Fufucode.Cli.exe" %*
exit /b %ERRORLEVEL%
:gui
"%APP_DIR%\Fufucode.App.exe" %*
exit /b %ERRORLEVEL%
"@ -Encoding Ascii

    Set-Content -Path (Join-Path $packageRoot 'cli/fufucode.cmd') -Value @"
@echo off
setlocal DisableDelayedExpansion
if not defined FUFUCODE_DATA_ROOT set "FUFUCODE_DATA_ROOT=%LocalAppData%\$dataRootName"
"%~dp0..\app\Fufucode.Cli.exe" %*
exit /b %ERRORLEVEL%
"@ -Encoding Ascii

    Set-Content -Path (Join-Path $packageRoot 'Fufucode.ps1') -Value @"
`$ErrorActionPreference = 'Stop'
if (-not `$Env:FUFUCODE_DATA_ROOT) { `$Env:FUFUCODE_DATA_ROOT = Join-Path `$Env:LocalAppData '$dataRootName' }
`$null = New-Item -ItemType Directory -Force `$Env:FUFUCODE_DATA_ROOT
`$component = if (`$args.Count -eq 0 -or `$args[0] -eq '--selftest') { 'app\Fufucode.App.exe' } else { 'app\Fufucode.Cli.exe' }
`$exe = Join-Path `$PSScriptRoot `$component
& `$exe @args
exit `$LASTEXITCODE
"@ -Encoding Utf8
}

# --- 3. 剥离符号与开发文档（与正式渠道一致，符号另发） ---
$symbolsDir = Join-Path $root 'out\symbols-test'
if (Test-Path -LiteralPath $symbolsDir) { Remove-Item -LiteralPath $symbolsDir -Recurse -Force }
New-Item -ItemType Directory -Force $symbolsDir | Out-Null
$symbolsZip = Join-Path $root 'out\FufucodeTest-Symbols.zip'
if (Test-Path -LiteralPath $symbolsZip) { Remove-Item -LiteralPath $symbolsZip -Force }

$pdbFiles = Get-ChildItem -Path $packageRoot -Filter *.pdb -Recurse
Write-Host ("Found {0} PDB files ({1:N2} MB), separating to symbols archive..." -f $pdbFiles.Count, (($pdbFiles | Measure-Object -Property Length -Sum).Sum / 1MB))
foreach ($pdb in $pdbFiles) {
    $rel = $pdb.FullName.Substring($packageRoot.Length).TrimStart('\', '/')
    $dest = Join-Path $symbolsDir $rel
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Move-Item -LiteralPath $pdb.FullName -Destination $dest -Force
}
Compress-Archive -Path (Join-Path $symbolsDir '*') -DestinationPath $symbolsZip -Force
Write-Host "Symbols archive created: $symbolsZip ($([Math]::Round((Get-Item $symbolsZip).Length / 1MB, 2)) MB)"

foreach ($xml in @('Microsoft.Web.WebView2.Core.xml')) {
    $targetXml = Join-Path $appDir $xml
    if (Test-Path -LiteralPath $targetXml) { Remove-Item -LiteralPath $targetXml -Force }
}
foreach ($name in @('Microsoft.DiaSymReader.Native.amd64.dll', 'Avalonia.DesignerSupport.dll')) {
    $target = Join-Path $appDir $name
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
}
$rootLoader = Join-Path $appDir 'WebView2Loader.dll'
$ridLoader = Join-Path $appDir "runtimes/$RuntimeIdentifier/native/WebView2Loader.dll"
if ((Test-Path -LiteralPath $rootLoader) -and (Test-Path -LiteralPath $ridLoader)) {
    Remove-Item -LiteralPath $rootLoader -Force
}

# --- 4. 载荷边界：测试包同样不得携带 Node/Pi 运行时或未安装插件 ---
$forbidden = @('runtime', 'extensions', 'notices')
foreach ($name in $forbidden) {
    if (Test-Path -LiteralPath (Join-Path $packageRoot $name)) {
        throw "Test package must not contain '$name' (managed runtime/extension payload)."
    }
}
$forbiddenPluginDlls = @(
    'Fufucode.Pi.dll',
    'Fufucode.Plugins.Pi.dll',
    'Fufucode.Plugins.BrowserUse.dll',
    'Fufucode.Plugins.Subagents.dll',
    'Fufucode.Plugins.ComputerUse.dll',
    'Fufucode.Plugins.Speech.dll',
    'Fufucode.Plugins.RemoteControl.dll',
    'Fufucode.Plugins.CronSchedules.dll',
    'Fufucode.Plugins.AeroSkin.dll',
    'Fufucode.Plugins.SkinMetro.dll',
    'Fufucode.Plugins.SkinMaterial3.dll',
    'Fufucode.Plugins.SkinLuna.dll',
    'Fufucode.Plugins.SkinLiquidGlass.dll',
    'Fufucode.Plugins.AutoDev.dll',
    'Fufucode.Plugins.Terminal.dll',
    'Fufucode.Plugins.Browser.dll',
    'Fufucode.Plugins.Library.dll',
    'Fufucode.Plugins.LocalReview.dll'
)
foreach ($dll in $forbiddenPluginDlls) {
    if (Test-Path -LiteralPath (Join-Path $packageRoot "app/$dll")) {
        throw "Test app package must not contain uninstalled plugin assembly '$dll'."
    }
}
foreach ($required in @('app/plugins/chat.interactive-ui/manifest.json',
        'app/plugins-dist/performance.monitor/manifest.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $required))) {
        throw "Test package is missing the bundled plugin payload '$required'."
    }
}

# --- 5. 构建标记：channel=test 驱动更新渠道与协议名 ---
$buildCommit = $null
try { $buildCommit = (& git -C $root rev-parse --short=12 HEAD 2>$null); if ($LASTEXITCODE -ne 0) { $buildCommit = $null } } catch { }
$githubRepo = if ($env:FUFUCODE_GITHUB_REPOSITORY) { $env:FUFUCODE_GITHUB_REPOSITORY } else { 'iron-water/Fufucode' }
$buildInfo = [ordered]@{
    version = $AppVersion
    builtAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    channel = 'test'
    githubRepository = $githubRepo
}
if ($buildCommit) { $buildInfo.commit = $buildCommit.Trim() }
$buildInfo | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path $appDir 'build-info.json') -Encoding Utf8
$buildInfo | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path $cliDir 'build-info.json') -Encoding Utf8
Write-Host "Build marker: channel=test version=$AppVersion repository=$githubRepo"

# 运行期自检：程序集版本必须与 build-info 一致，否则用户看到的"当前构建"是假的。
$appExe = Join-Path $appDir 'Fufucode.App.exe'
if (Test-Path -LiteralPath $appExe) {
    $fileVersion = (Get-Item -LiteralPath $appExe).VersionInfo.FileVersion
    $expectedPrefix = ($AppVersion.Split('-')[0])
    if ($fileVersion -and -not $fileVersion.StartsWith($expectedPrefix)) {
        throw "Fufucode.App.exe FileVersion '$fileVersion' does not match AppVersion '$AppVersion'."
    }
}

# --- 6. README ---
Set-Content -Path (Join-Path $packageRoot 'README.txt') -Value @"
Fufucode Test package (experimental channel)

Run:
  Fufucode.exe              start Fufucode Test (GUI, no console)
  Fufucode.exe --selftest   headless self-test (no desktop session required)
  Fufucode.exe <command>    run the CLI
  Fufucode.cmd              legacy batch launcher (kept for scripting)
  Fufucode.ps1              PowerShell launcher, same arguments as Fufucode.cmd
  cli\fufucode.cmd          CLI entry

Version: $AppVersion  (experimental test channel)

This is NOT the release build. It installs side by side with Fufucode:
  - install directory   %LocalAppData%\Programs\Fufucode Test
  - user data           %LocalAppData%\FufucodeTest
  - URL schemes         fufucode-test: and fufucode-test-approval:
  - its own single-instance lock, so both builds can run at the same time

Updates for this build come from GitHub Releases marked as pre-release in
$githubRepo. The release build never sees them, and this build never offers
the release build as an update.

Layout:
  app/              self-contained .NET 10.0 $RuntimeIdentifier publish (Avalonia 12.1.3)
  app/plugins/      bundled plugins seeded into the data root on first start
  cli/              CLI entry
  data-root.txt     data root name the launcher injects as FUFUCODE_DATA_ROOT
  $manifestName    SHA256 manifest of every shipped file

No Node runtime, no Pi runtime and no optional plugins are bundled. The
default FUMO native backend (pure C#) starts with zero external processes.

Unverified: code signing and automatic plugin upgrade.
The embedded browser requires Microsoft Edge WebView2 Runtime on the target machine.
"@

# --- 7. 完整性清单 + 归档 ---
& (Join-Path $PSScriptRoot 'integrity.ps1') -Mode Write -PackageRoot $packageRoot -ManifestName $manifestName

Write-Host "Archiving test package..."
New-Item -ItemType Directory -Force (Split-Path $zipPath) | Out-Null
& (Join-Path $PSScriptRoot 'archive-package.ps1') -PackageRoot $packageRoot -Destination $zipPath -ManifestName $manifestName
Write-Host "Package: $zipPath"
$zipSize = (Get-Item $zipPath).Length
Write-Host ("{0:N0} bytes ({1:N2} MB)" -f $zipSize, ($zipSize / 1MB))
Write-Host "Component sizes:"
Get-ChildItem -Directory $packageRoot | ForEach-Object {
    $size = (Get-ChildItem -Recurse -LiteralPath $_.FullName | Measure-Object -Property Length -Sum).Sum
    Write-Host ('  {0}: {1:N1} MB' -f $_.Name, ($size / 1MB))
}

# --- 8. Inno Setup 安装器（测试渠道独立 AppId / 安装目录 / 协议） ---
if ($RuntimeIdentifier -like 'win-*') {
    . (Join-Path $PSScriptRoot 'resolve-iscc.ps1')
    $iscc = Resolve-InnoCompiler
    $defines = @(
        "/DAppVersion=$AppVersion",
        "/DOutName=$OutName",
        "/DPayloadRoot=$packageName",
        "/DIntegrityManifest=$manifestName",
        "/DDataRootName=$dataRootName"
    )
    & $iscc (Join-Path $PSScriptRoot 'Fufucode-Test.iss') @defines
    if ($LASTEXITCODE -ne 0) { throw 'Test installer build failed.' }
} else {
    Write-Host "Skipping Inno Setup installer for $RuntimeIdentifier (zip only)."
}

# --- 9. 更新清单（测试渠道专用 feed.json） ---
$setupPath = Join-Path $root "out\$OutName.exe"
if (Test-Path -LiteralPath $setupPath) {
    $setupBytes = (Get-Item $setupPath).Length
    $setupSha256 = (Get-FileHash -Path $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $updateManifest = [ordered]@{
        version = $AppVersion
        releaseDate = (Get-Date).ToUniversalTime().ToString("o")
        releaseNotes = "Fufucode 实验性测试构建 v$AppVersion：仅包含 Windows 桌面端。"
        packageUrl = "$OutName.exe"
        sha256 = $setupSha256
        packageSizeBytes = $setupBytes
        minCompatibleVersion = "0.1.0"
        signaturePolicy = "Unsigned"
    }
    $feedJson = ConvertTo-Json $updateManifest -Depth 5
    Set-Content -Path (Join-Path $root 'out\feed-test.json') -Value $feedJson -Encoding Utf8
    Write-Host "Generated update feed manifest: out\feed-test.json (v$AppVersion, SHA-256: $setupSha256)"
}

Write-Host "TEST-1 packaging complete."
