<#
PKG-1 原生安装包渠道（零 Node / 零 Pi 运行时的极简 Setup 构建）。

范围与边界：
  - 只发布 Fufucode.App 与 Fufucode.Cli（win-x64 self-contained）；
  - 不打包 runtime/node、runtime/pi、extensions/fufucode-bridge、notices 与 Pi 启动脚本；
  - 产物：out/FufucodeNative.zip + out/Fufucode-Native-Setup.exe + integrity-native.sha256.json；
  - 全量渠道（scripts/packaging/package.ps1 + Fufucode.iss 的 Fufucode.zip / Fufucode-<版本>-setup.exe）
    保持独立，本脚本不删除也不依赖其产物。

设计理由（本机实测）：自有 FUMO 原生内核（纯 C# 实现）是默认后端；Pi/OMP/DSH/Codex 以后端插件形式
按需安装，Pi 插件的 Node/Pi 运行时由扩展包下发；原安装包在组合根把 runtime/pi 当硬依赖并在缺 Node/Pi 时
抛 FileNotFoundException，因此把两侧打进同一包会重新引入 Node 硬门槛。
#>
param(
    [string]$AppVersion = '1.4.0',
    [string]$OutName,
    # 打包目标平台。默认 Windows x64；同一源码可用 win-x64 / linux-x64 / osx-arm64 等
    # 分别产出独立安装包，不把其他平台的载荷塞进单一发布。
    [string]$RuntimeIdentifier = 'win-x64',
    # 框架依赖瘦身版：不携带 .NET 运行时，要求目标机已安装 .NET 10 Runtime。
    # 用户已有运行时（开发者/已装 .NET）时优先发这个版本；否则用默认自包含版。
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$packageName = if ($FrameworkDependent) { 'build-native-slim' } else { 'build-native' }
$packageRoot = Join-Path $root "out\$packageName"
$zipName = if ($FrameworkDependent) { 'FufucodeNative-Slim.zip' } else { 'FufucodeNative.zip' }
$zipPath = Join-Path $root "out\$zipName"
if ([string]::IsNullOrWhiteSpace($OutName)) {
    $OutName = if ($FrameworkDependent) { 'Fufucode-Native-Slim-Setup' } else { 'Fufucode-Native-Setup' }
}
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

# 发布输出必须落在仓库内，避免误删仓库外目录。
if (-not [IO.Path]::GetFullPath($packageRoot).StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Native package output must stay inside the repository.'
}
if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
New-Item -ItemType Directory -Force $packageRoot | Out-Null

# --- 1. .NET self-contained 发布（win-x64，App 与 Cli 共享单份托管运行库底座） ---
$appDir = Join-Path $packageRoot 'app'
$cliDir = Join-Path $packageRoot 'cli'
New-Item -ItemType Directory -Force $appDir | Out-Null
New-Item -ItemType Directory -Force $cliDir | Out-Null

# 两个入口共用一份裁剪运行时。不能把两次 publish 直接写进同一目录：
# 第二次 publish 的增量复制会按大小/时间跳过同名的框架程序集，导致 CLI 的
# 更小裁剪结果（缺少 App 需要的类型）覆盖 App。正确做法是各自发布到独立
# 目录，再只把 CLI 专属文件并入 App 的发布结果。
$cliStaging = Join-Path $packageRoot '.cli-staging'
New-Item -ItemType Directory -Force $cliStaging | Out-Null
Write-Host "Publishing Fufucode.Cli $RuntimeIdentifier (self-contained=$selfContained)..."
dotnet publish (Join-Path $root 'src/Fufucode.Cli/Fufucode.Cli.csproj') -c Release -r $RuntimeIdentifier --self-contained $selfContained -o $cliStaging
if ($LASTEXITCODE -ne 0) { throw "Fufucode.Cli publish failed." }

Write-Host "Publishing Fufucode.App into shared app layout (self-contained=$selfContained)..."
dotnet publish (Join-Path $root 'src/Fufucode.App/Fufucode.App.csproj') -c Release -r $RuntimeIdentifier --self-contained $selfContained -o $appDir
if ($LASTEXITCODE -ne 0) { throw "Fufucode.App publish failed." }

# 只并入 CLI 入口相关文件；共享框架程序集以 App 的裁剪结果为准。
foreach ($name in @('Fufucode.Cli.exe', 'Fufucode.Cli.dll', 'Fufucode.Cli.deps.json',
        'Fufucode.Cli.runtimeconfig.json', 'Fufucode.Cli.pdb')) {
    $source = Join-Path $cliStaging $name
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $appDir $name) -Force
    }
}
Remove-Item -LiteralPath $cliStaging -Recurse -Force

# 共享裁剪运行时冒烟：App 的裁剪结果覆盖后面发布的 CLI 时，CLI 必须仍能启动。
$cliProbeData = Join-Path ([IO.Path]::GetTempPath()) ('fufucode-cli-probe-' + [Guid]::NewGuid().ToString('N'))
try {
    $previousData = $env:FUFUCODE_DATA
    $env:FUFUCODE_DATA = $cliProbeData
    $cliExeName = if ($RuntimeIdentifier -like 'win-*') { 'Fufucode.Cli.exe' } else { 'Fufucode.Cli' }
    $cliProbe = & (Join-Path $appDir $cliExeName) --help 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Packaged CLI failed to start on the shared trimmed runtime: $($cliProbe -join [Environment]::NewLine)"
    }
} finally {
    $env:FUFUCODE_DATA = $previousData
    if (Test-Path -LiteralPath $cliProbeData) { Remove-Item -LiteralPath $cliProbeData -Recurse -Force -ErrorAction SilentlyContinue }
}

# 平台守卫：
# - 自包含版依赖裁剪器移除其它平台后端；失败则拒绝打包。
# - 框架依赖版不裁剪，按 RID 手工移除其它平台托管后端。
$nonWindowsBackends = @('Avalonia.X11.dll', 'Avalonia.FreeDesktop.dll', 'Avalonia.FreeDesktop.AtSpi.dll',
    'Avalonia.Native.dll', 'Tmds.DBus.Protocol.dll')
if ($RuntimeIdentifier -like 'win-*') {
    foreach ($unused in $nonWindowsBackends) {
        $target = Join-Path $appDir $unused
        if ($FrameworkDependent) {
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
        } elseif (Test-Path -LiteralPath $target) {
            throw "Trimming did not remove non-Windows backend '$unused'; refusing to package an untrimmed payload."
        }
    }
}

# Windows 专属入口（原生转发器与 GUI 启动器）只在 win-* 包中生成。
if ($RuntimeIdentifier -like 'win-*') {
    # 编译并部署 CLI 原生转发入口 fufucode.exe（仅 22KB，避免 cmd 批处理作业提示吞 Ctrl+C，并共享 app 运行库）
    $cliForwarderSrc = Join-Path $root 'tools/fufucode-cli-forwarder/fufucode.exe'
    if (-not (Test-Path -LiteralPath $cliForwarderSrc)) {
        $cSrc = Join-Path $root 'tools/fufucode-cli-forwarder/fufucode.c'
        if (Get-Command gcc -ErrorAction SilentlyContinue) {
            & gcc -O2 -s $cSrc -o $cliForwarderSrc
        }
    }
    if (Test-Path -LiteralPath $cliForwarderSrc) {
        Copy-Item -LiteralPath $cliForwarderSrc -Destination (Join-Path $cliDir 'fufucode.exe') -Force
        Copy-Item -LiteralPath $cliForwarderSrc -Destination (Join-Path $cliDir 'Fufucode.Cli.exe') -Force
    }

    # 部署根目录 GUI 启动器 Fufucode.exe：无参起 App、有参转发 Cli，带图标和版本信息。
    # 开始菜单/桌面快捷方式都指向它，替代 Fufucode.cmd 的黑框体验。
    $launcherDir = Join-Path $root 'tools/fufucode-launcher'
    $launcherExe = Join-Path $launcherDir 'fufucode.exe'
    $launcherSrc = Join-Path $launcherDir 'fufucode.c'
    $launcherRc  = Join-Path $launcherDir 'fufucode.rc'
    $launcherRes = Join-Path $launcherDir 'fufucode_res.o'
    if (-not (Test-Path -LiteralPath $launcherExe) -and (Get-Command gcc -ErrorAction SilentlyContinue) -and (Get-Command windres -ErrorAction SilentlyContinue)) {
        & windres $launcherRc -o $launcherRes
        & gcc -O2 -s -mwindows $launcherSrc $launcherRes -o $launcherExe
    }
    if (Test-Path -LiteralPath $launcherExe) {
        Copy-Item -LiteralPath $launcherExe -Destination (Join-Path $packageRoot 'Fufucode.exe') -Force
    } else {
        Write-Warning "Fufucode.exe launcher not available; shortcuts will keep using Fufucode.cmd."
    }
}

# --- 分离符号文件（PDB）与 SDK 开发文档（XML），不随用户默认安装包发布 ---
$symbolsSuffix = if ($FrameworkDependent) { '-Slim' } else { '' }
$symbolsDir = Join-Path $root "out\symbols$symbolsSuffix"
if (Test-Path -LiteralPath $symbolsDir) { Remove-Item -LiteralPath $symbolsDir -Recurse -Force }
New-Item -ItemType Directory -Force $symbolsDir | Out-Null
$symbolsZip = Join-Path $root "out\FufucodeNative-Symbols$symbolsSuffix.zip"
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

# 移除 SDK 开发说明 XML（如 WebView2.Core.xml 0.584MB，非运行必需）
$docXmls = @('Microsoft.Web.WebView2.Core.xml')
foreach ($xml in $docXmls) {
    $targetXml = Join-Path $appDir $xml
    if (Test-Path -LiteralPath $targetXml) {
        Remove-Item -LiteralPath $targetXml -Force
    }
}

# --- 2. 原生包不得包含任何 Node/Pi 载荷 ---
# 这些路径一旦出现，说明发布把托管运行时又带了回来，必须在打包期失败而不是留到安装后。
$forbidden = @('runtime', 'extensions', 'notices')
foreach ($name in $forbidden) {
    if (Test-Path -LiteralPath (Join-Path $packageRoot $name)) {
        throw "Native package must not contain '$name' (managed runtime/extension payload)."
    }
}

# 验证：默认核心 App 包中严禁静态包含未安装插件的实现程序集（真实物理剥离）
$forbiddenPluginDlls = @(
    # 后端插件：宿主不再静态依赖，插件载荷由 plugins-dist 下发。
    'Fufucode.Pi.dll',
    'Fufucode.Plugins.Pi.dll',
    # 可选功能插件。
    'Fufucode.Plugins.BrowserUse.dll',
    'Fufucode.Plugins.ComputerUse.dll',
    'Fufucode.Plugins.Speech.dll',
    'Fufucode.Plugins.RemoteControl.dll',
    'Fufucode.Plugins.CronSchedules.dll',
    'Fufucode.Plugins.AeroSkin.dll',
    'Fufucode.Plugins.AutoDev.dll'
)
foreach ($dll in $forbiddenPluginDlls) {
    if (Test-Path -LiteralPath (Join-Path $packageRoot "app/$dll")) {
        throw "Default app package must not contain uninstalled plugin assembly '$dll'."
    }
}

# --- 3. 原生启动器与说明（Windows 包才写 cmd/ps1 入口） ---
if ($RuntimeIdentifier -like 'win-*') {
Write-Host "Writing launchers..."
Set-Content -Path (Join-Path $packageRoot 'Fufucode.cmd') -Value '@echo off
setlocal DisableDelayedExpansion
set "FUFUCODE_DATA=%LocalAppData%\Fufucode"
if not exist "%FUFUCODE_DATA%" mkdir "%FUFUCODE_DATA%"
set "APP_DIR=%~dp0app"

if "%~1"=="" goto gui
if /i "%~1"=="--selftest" goto gui
"%APP_DIR%\Fufucode.Cli.exe" %*
exit /b %ERRORLEVEL%
:gui
"%APP_DIR%\Fufucode.App.exe" %*
exit /b %ERRORLEVEL%
' -Encoding Ascii

Set-Content -Path (Join-Path $packageRoot 'cli/fufucode.cmd') -Value '@echo off
setlocal DisableDelayedExpansion
"%~dp0..\app\Fufucode.Cli.exe" %*
exit /b %ERRORLEVEL%
' -Encoding Ascii

Set-Content -Path (Join-Path $packageRoot 'cli/Fufucode.Cli.cmd') -Value '@echo off
setlocal DisableDelayedExpansion
"%~dp0..\app\Fufucode.Cli.exe" %*
exit /b %ERRORLEVEL%
' -Encoding Ascii

Set-Content -Path (Join-Path $packageRoot 'Fufucode.ps1') -Value '$ErrorActionPreference = ''Stop''
$Env:FUFUCODE_DATA = Join-Path $Env:LocalAppData ''Fufucode''
$null = New-Item -ItemType Directory -Force $Env:FUFUCODE_DATA
$component = if ($args.Count -eq 0 -or $args[0] -eq ''--selftest'') { ''app\Fufucode.App.exe'' } else { ''app\Fufucode.Cli.exe'' }
$exe = Join-Path $PSScriptRoot $component
& $exe @args
exit $LASTEXITCODE
' -Encoding Utf8
}

Set-Content -Path (Join-Path $packageRoot 'README.txt') -Value @"
Fufucode native package (PKG-1)

Run:
  Fufucode.exe              start the native app (GUI, no console)
  Fufucode.exe --selftest   headless self-test (no desktop session required)
  Fufucode.exe <command>    run the CLI (for example: settings get --json)
  Fufucode.cmd              legacy batch launcher (kept for scripting)
  Fufucode.ps1              PowerShell launcher, same arguments as Fufucode.cmd
  cli\fufucode.cmd          CLI REPL; installed cli/ is added to the user PATH
  cli\fufucode.exe          native CLI entry (preferred by PATH, direct Ctrl+C)
  fufucode <command>        installed CLI (open a new terminal after installation)

Layout:
  app/              .NET 10.0 $RuntimeIdentifier publish (Avalonia 12.1.3, $(if ($FrameworkDependent) { 'framework-dependent slim' } else { 'self-contained trimmed' }))
  cli/              PATH launcher (fufucode.exe forwarder + fufucode.cmd)
  integrity-native.sha256.json   SHA256 manifest of every shipped file

This package ships ONLY the native core (Fufucode.App + Fufucode.Cli). It contains no Node
runtime, no Pi runtime and no bundled extensions, so the default FUMO native backend
(pure C#) starts and runs with zero Node/Python/external-agent processes.

Pi, OMP, DSH and Codex are optional backend plugins: install them from the marketplace,
which provisions each plugin's own runtime (Node, Pi bundle, OMP/Codex binary). FUMO
remains the default kernel. Never copy runtime/node or runtime/pi into this layout by hand.

Data is written to %LocalAppData%\Fufucode (agent, sessions, desktop.db, logs, exports;
the legacy pi-agent directory is still read for backward compatibility).

Unverified: code signing and automatic plugin upgrade.
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
$buildInfo | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path $appDir 'build-info.json') -Encoding Utf8
$buildInfo | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path $cliDir 'build-info.json') -Encoding Utf8
Write-Host "Build marker: channel=$buildChannel version=$AppVersion"

# --- 4. 发布文件完整性清单 ---
& (Join-Path $PSScriptRoot 'integrity.ps1') -Mode Write -PackageRoot $packageRoot -ManifestName 'integrity-native.sha256.json'

# --- 5. 归档（归档校验器同样按 integrity-native.sha256.json 逐文件校验） ---
Write-Host "Archiving native package..."
New-Item -ItemType Directory -Force (Split-Path $zipPath) | Out-Null
& (Join-Path $PSScriptRoot 'archive-package.ps1') -PackageRoot $packageRoot -Destination $zipPath -ManifestName 'integrity-native.sha256.json'
Write-Host "Package: $zipPath"
$zipSize = (Get-Item $zipPath).Length
Write-Host ("{0:N0} bytes ({1:N2} MB)" -f $zipSize, ($zipSize / 1MB))
Write-Host "Component sizes:"
Get-ChildItem -Directory $packageRoot | ForEach-Object {
    $size = (Get-ChildItem -Recurse -LiteralPath $_.FullName | Measure-Object -Property Length -Sum).Sum
    Write-Host ('  {0}: {1:N1} MB' -f $_.Name, ($size / 1MB))
}

# --- 6. Inno Setup 编译（仅 Windows；其它 RID 只产出 zip） ---
if ($RuntimeIdentifier -like 'win-*') {
    & (Join-Path $PSScriptRoot 'build-installer-version.ps1') -AppVersion $AppVersion -OutName $OutName `
        -IssName 'Fufucode-Native.iss' -PackageRoot $packageRoot -PayloadRoot $packageName -NoManifest
    if ($LASTEXITCODE -ne 0) { throw 'Native installer build failed.' }
} else {
    Write-Host "Skipping Inno Setup installer for $RuntimeIdentifier (zip only)."
}

# --- 7. 独立发布可选插件分发包（plugins-dist，按需瘦身与依赖池分离） ---
$distRoot = Join-Path $root 'out\plugins-dist'
if (Test-Path -LiteralPath $distRoot) { Remove-Item -LiteralPath $distRoot -Recurse -Force }
New-Item -ItemType Directory -Force $distRoot | Out-Null
$sharedDistPool = Join-Path $distRoot '.shared-packages'
New-Item -ItemType Directory -Force $sharedDistPool | Out-Null

$plugins = @(
    @{ Id = 'agent.browser-use'; Project = 'src/Fufucode.Plugins.BrowserUse/Fufucode.Plugins.BrowserUse.csproj' },
    @{ Id = 'agent.computer-use'; Project = 'src/Fufucode.Plugins.ComputerUse/Fufucode.Plugins.ComputerUse.csproj' },
    @{ Id = 'service.speech-synthesis'; Project = 'src/Fufucode.Plugins.Speech/Fufucode.Plugins.Speech.csproj' },
    @{ Id = 'service.remote-control'; Project = 'src/Fufucode.Plugins.RemoteControl/Fufucode.Plugins.RemoteControl.csproj' },
    @{ Id = 'automation.cron'; Project = 'src/Fufucode.Plugins.CronSchedules/Fufucode.Plugins.CronSchedules.csproj' },
    @{ Id = 'skin.aero'; Project = 'src/Fufucode.Plugins.AeroSkin/Fufucode.Plugins.AeroSkin.csproj' },
    @{ Id = 'automation.auto-dev'; Project = 'src/Fufucode.Plugins.AutoDev/Fufucode.Plugins.AutoDev.csproj' },
    # 五类后端插件结构完全同级；各自携带协议适配器，运行时由用户安装或按需下发。
    @{ Id = 'backend.pi'; Project = 'src/Fufucode.Plugins.Pi/Fufucode.Plugins.Pi.csproj' },
    @{ Id = 'backend.omp'; Project = 'src/Fufucode.Plugins.Omp/Fufucode.Plugins.Omp.csproj' },
    @{ Id = 'backend.claude'; Project = 'src/Fufucode.Plugins.Claude/Fufucode.Plugins.Claude.csproj' },
    @{ Id = 'backend.codex'; Project = 'src/Fufucode.Plugins.Codex/Fufucode.Plugins.Codex.csproj' },
    @{ Id = 'backend.dsh'; Project = 'src/Fufucode.Plugins.Dsh/Fufucode.Plugins.Dsh.csproj' }
)

# 宿主已提供的共享核心与基础运行时程序集（严禁在插件包中重复打包第二份）
$hostAssemblies = @(
    'Fufucode.Core.*',
    # Fufucode.Pi.* 已迁出为后端插件载荷，宿主不再提供，无需剥离。
    'Fufucode.App.*',
    'Fufucode.App.Abstractions.*',
    'Avalonia.*',
    'Microsoft.Data.Sqlite.*',
    'SQLitePCLRaw.*',
    'CommunityToolkit.Mvvm.*'
)

foreach ($p in $plugins) {
    $pluginId = $p.Id
    $proj = Join-Path $root $p.Project
    $targetDir = Join-Path $distRoot $pluginId
    Write-Host "Publishing independent plugin '$pluginId' to plugins-dist (lean package)..."
    dotnet publish $proj -c Release -o $targetDir
    if ($LASTEXITCODE -ne 0) { throw "Plugin $pluginId publish failed." }

    # 1. 物理剥离宿主已有的程序集
    foreach ($pattern in $hostAssemblies) {
        Get-ChildItem -Path $targetDir -Filter $pattern | Remove-Item -Force -Recurse
    }
    # 2. 剥离无用的跨平台非 win-x64 原生运行时
    $runtimesDir = Join-Path $targetDir 'runtimes'
    if (Test-Path -LiteralPath $runtimesDir) {
        Remove-Item -LiteralPath $runtimesDir -Recurse -Force
    }

    # 3. 复制 manifest.json
    $manifestSrc = Join-Path (Split-Path $proj) 'manifest.json'
    if (Test-Path -LiteralPath $manifestSrc) {
        Copy-Item -LiteralPath $manifestSrc -Destination $targetDir -Force
    }
    $pluginZip = Join-Path $distRoot "$pluginId-1.0.0.zip"
    Compress-Archive -Path (Join-Path $targetDir '*') -DestinationPath $pluginZip -Force
    $zipLen = (Get-Item $pluginZip).Length
    Write-Host ("  -> Slimmed package $pluginId-1.0.0.zip: {0:N0} bytes ({1:N2} KB)" -f $zipLen, ($zipLen / 1KB))
}

# --- 8. 生成官方主程序更新清单（仅默认自包含渠道） ---
$setupPath = Join-Path $root "out\$OutName.exe"
if (-not $FrameworkDependent -and (Test-Path -LiteralPath $setupPath)) {
    $setupBytes = (Get-Item $setupPath).Length
    $setupSha256 = (Get-FileHash -Path $setupPath -Algorithm SHA256).Hash
    $updateManifest = [ordered]@{
        version = $AppVersion
        releaseDate = (Get-Date).ToUniversalTime().ToString("o")
        releaseNotes = "Fufucode 原生生产包更新 (v$AppVersion)：FUMO 为默认内核（纯 C# 实现），Pi/OMP/DSH/Codex 为可选后端插件；包含物理插件市场、共享依赖池管理与完整自测链路。"
        packageUrl = "$OutName.exe"
        sha256 = $setupSha256
        packageSizeBytes = $setupBytes
        minCompatibleVersion = "1.0.0"
        signaturePolicy = "Unsigned"
    }
    $feedJson = ConvertTo-Json $updateManifest -Depth 5
    Set-Content -Path (Join-Path $root 'out\feed.json') -Value $feedJson -Encoding Utf8
    Write-Host "Generated update feed manifest: out\feed.json (v$AppVersion, SHA-256: $setupSha256)"
}

Write-Host "PKG-1 Native packaging complete with slimmed plugin packages and update feed manifest."
