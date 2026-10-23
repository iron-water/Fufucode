<#
本地开发构建 + 部署：一条命令完成"构建 → 停旧进程 → 覆盖 → 重启"。

用法：
    pwsh fucode/publish-local.ps1                 # 构建并自动部署重启（默认）
    pwsh fucode/publish-local.ps1 -NoDeploy       # 只构建，不动运行中的进程
    pwsh fucode/publish-local.ps1 -NoRun          # 构建并部署，但不自动启动
    pwsh fucode/publish-local.ps1 -Configuration Release

行为：
    1. 按 RID 做框架依赖发布到 out/app 与 out/cli（只复制本平台原生库，
       不再生成 runtimes/ 下全部 26 个平台的载荷），删除第三方原生 PDB 与
       非当前平台的 Avalonia 后端；写入带 commit 的 build-info.json；
       发布后删除目标目录里本次构建已不存在的旧文件，避免解耦后的 DLL 永久残留；
    2. 若 out/app 里的程序正在运行，结束它（先礼后兵：CloseMainWindow → Wait → Kill）；
    3. 把构建产物同步到运行目录（默认就是 out/app 本身，无需复制）；
    4. 写部署指针（供安装在其它位置的应用发现本机构建）；
    5. 重新启动应用。

不打包、不生成安装器、不联网。
#>
param(
    [string]$Configuration = 'Debug',
    # 本地开发默认只发布当前平台，避免把其它 RID 的原生库带进 out/app。
    [string]$RuntimeIdentifier = 'win-x64',
    [switch]$NoDeploy,
    [switch]$NoRun,
    [switch]$NoRestore,
    # ReadyToRun：预编译 IL 为原生代码，冷启动可省约 1 秒 JIT；发布体积增加约 6 MB。
    [switch]$ReadyToRun,
    # 并行审阅：构建到独立目录，不停止/启动现有应用，不写本地更新指针。
    [string]$OutputRoot = '',
    # 审阅标签（默认 branch@commit），会写入 build-info.json 并显示在窗口标题。
    # 只给 label 不给 OutputRoot 时也按审阅构建处理，输出到 out/review/<label>，
    # 避免误把并行构建部署进 out/app。
    [string]$ReviewLabel = '',
    # 只构建并复制到 out/app，但不写“本地更新”指针。
    [switch]$NoPointer
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# 审阅目录按分支名固定（/ 换成 -），不按 commit 变化；审阅板用同一规则，
# 模型漏写 -OutputRoot 时也能落到审阅板预期的位置。
$branchHint = $null
try {
    $branchHint = (& git -C $root rev-parse --abbrev-ref HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $branchHint -eq 'HEAD') { $branchHint = $null } else { $branchHint = $branchHint.Trim() }
} catch { }

# 审阅产物统一放在主工作树的 out/review 下。这样模型在自己的 worktree 里发布，
# 主工作树的审阅板也能直接找到，不需要知道主仓库绝对路径。
$sharedRoot = $root
try {
    $commonDir = (& git -C $root rev-parse --path-format=absolute --git-common-dir 2>$null)
    if ($LASTEXITCODE -eq 0 -and $commonDir) {
        $candidate = Split-Path -Parent ($commonDir.Trim())
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { $sharedRoot = $candidate }
    }
} catch { }

$hasOutputRoot = -not [string]::IsNullOrWhiteSpace($OutputRoot)
$isReview = $hasOutputRoot -or (-not [string]::IsNullOrWhiteSpace($ReviewLabel))
$outRoot = if ($hasOutputRoot) {
    if ([IO.Path]::IsPathRooted($OutputRoot)) { [IO.Path]::GetFullPath($OutputRoot) }
    else { [IO.Path]::GetFullPath((Join-Path $sharedRoot $OutputRoot)) }
} elseif ($isReview) {
    $slugSource = if ($branchHint) { $branchHint } else { $ReviewLabel }
    $reviewSlug = ($slugSource.Trim() -replace '[^A-Za-z0-9._-]+', '-').Trim('-', '.', '_')
    if ([string]::IsNullOrWhiteSpace($reviewSlug)) { $reviewSlug = 'review' }
    Join-Path $sharedRoot "out/review/$reviewSlug"
} else { Join-Path $root 'out' }
$appOut = Join-Path $outRoot 'app'
$cliOut = Join-Path $outRoot 'cli'
$appExe = Join-Path $appOut 'Fufucode.App.exe'

function Get-Commit {
    try {
        $hash = & git -C $root rev-parse --short=12 HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $hash) { return $hash.Trim() }
    } catch { }
    return $null
}

<#
停止所有正在运行的应用实例。只结束确实来自本仓库构建的进程：
按可执行文件路径匹配，避免误杀其它位置安装的 Fufucode。
返回被停止的进程数。
#>
function Stop-RunningApp {
    param([string]$ExpectedExe)

    $expected = [IO.Path]::GetFullPath($ExpectedExe)
    $targets = @()
    foreach ($name in @('Fufucode.App', 'Fufucode')) {
        foreach ($proc in Get-Process -Name $name -ErrorAction SilentlyContinue) {
            try {
                $path = $proc.Path
                if ($path -and ([IO.Path]::GetFullPath($path) -eq $expected -or
                        [IO.Path]::GetDirectoryName($path) -eq [IO.Path]::GetDirectoryName($expected))) {
                    $targets += $proc
                }
            } catch {
                # 无权读取路径的进程不动，交给后续复制失败提示。
            }
        }
    }

    foreach ($proc in $targets) {
        Write-Host "  停止正在运行的 $($proc.ProcessName) (PID $($proc.Id)) ..."
        try {
            if (-not $proc.HasExited) {
                $null = $proc.CloseMainWindow()
                if (-not $proc.WaitForExit(3000)) { $proc.Kill($true) }
                $proc.WaitForExit(5000) | Out-Null
            }
        } catch {
            Write-Warning "  无法结束 PID $($proc.Id)：$($_.Exception.Message)"
        }
    }

    return $targets.Count
}

$commit = Get-Commit
# 版本：显式环境变量优先；否则沿用最近一次 out/app 里的版本，保持 dev 序列稳定。
$version = $env:FUFUCODE_BUILD_VERSION
if ([string]::IsNullOrWhiteSpace($version)) {
    $previousInfo = Join-Path $appOut 'build-info.json'
    if (Test-Path -LiteralPath $previousInfo) {
        try { $version = (Get-Content -LiteralPath $previousInfo -Raw | ConvertFrom-Json).version } catch { }
    }
}
if ([string]::IsNullOrWhiteSpace($version)) { $version = '1.4.0' }

# 先发布到暂存目录，避免目标目录被占用时构建本身失败。
$stagingRoot = Join-Path $outRoot ('.staging-' + $PID)
$stagingApp = Join-Path $stagingRoot 'app'
$stagingCli = Join-Path $stagingRoot 'cli'
if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $stagingApp, $stagingCli | Out-Null

$dotnetArgs = @('publish', (Join-Path $root 'src/Fufucode.App/Fufucode.App.csproj'),
    '-c', $Configuration, '-r', $RuntimeIdentifier, '--self-contained', 'false',
    '-o', $stagingApp, '--nologo', '-v', 'q')
if ($NoRestore) { $dotnetArgs += '--no-restore' }
if ($ReadyToRun) { $dotnetArgs += @('-p:PublishReadyToRun=true') }
Write-Host "Publishing Fufucode.App ($Configuration) -> staging ..."
& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { throw 'Fufucode.App publish failed.' }

Write-Host "Publishing Fufucode.Cli ($Configuration) -> staging ..."
$cliArgs = @('publish', (Join-Path $root 'src/Fufucode.Cli/Fufucode.Cli.csproj'),
    '-c', $Configuration, '-r', $RuntimeIdentifier, '--self-contained', 'false',
    '-o', $stagingCli, '--nologo', '-v', 'q')
if ($NoRestore) { $cliArgs += '--no-restore' }
if ($ReadyToRun) { $cliArgs += @('-p:PublishReadyToRun=true') }
& dotnet @cliArgs
if ($LASTEXITCODE -ne 0) { throw 'Fufucode.Cli publish failed.' }

# 默认随包分发的对话增强插件：发布到 app/plugins/<id>，由宿主首次启动播种到数据目录。
Write-Host "Publishing chat.interactive-ui plugin -> staging ..."
$pluginOut = Join-Path $stagingApp 'plugins/chat.interactive-ui'
$pluginArgs = @('publish', (Join-Path $root 'src/Fufucode.Plugins.InteractiveUI/Fufucode.Plugins.InteractiveUI.csproj'),
    '-c', $Configuration, '-o', $pluginOut, '--nologo', '-v', 'q')
if ($NoRestore) { $pluginArgs += '--no-restore' }
& dotnet @pluginArgs
if ($LASTEXITCODE -ne 0) { throw 'Interactive UI plugin publish failed.' }
# 插件包只带自己的程序集：宿主已加载的 Core/App/Avalonia 一律剥离。
Get-ChildItem -LiteralPath $pluginOut -Filter 'Fufucode.*.dll' |
    Where-Object { $_.Name -ne 'Fufucode.Plugins.InteractiveUI.dll' } |
    Remove-Item -Force
Get-ChildItem -LiteralPath $pluginOut -Filter 'Avalonia*.dll' -ErrorAction SilentlyContinue | Remove-Item -Force
if (Test-Path (Join-Path $pluginOut 'runtimes')) { Remove-Item -LiteralPath (Join-Path $pluginOut 'runtimes') -Recurse -Force }

# 默认启用的性能监控插件：发布到 app/plugins-dist/<id>，启动时由 PluginBootstrapper 幂等播种。
Write-Host "Publishing performance.monitor plugin -> staging ..."
$perfOut = Join-Path $stagingApp 'plugins-dist/performance.monitor'
$perfArgs = @('publish', (Join-Path $root 'src/Fufucode.Plugins.PerformanceMonitor/Fufucode.Plugins.PerformanceMonitor.csproj'),
    '-c', $Configuration, '-o', $perfOut, '--nologo', '-v', 'q')
if ($NoRestore) { $perfArgs += '--no-restore' }
& dotnet @perfArgs
if ($LASTEXITCODE -ne 0) { throw 'Performance monitor plugin publish failed.' }
Get-ChildItem -LiteralPath $perfOut -Filter 'Fufucode.*.dll' |
    Where-Object { $_.Name -ne 'Fufucode.Plugins.PerformanceMonitor.dll' } |
    Remove-Item -Force
Get-ChildItem -LiteralPath $perfOut -Filter 'Avalonia*.dll' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem -LiteralPath $perfOut -Filter 'CommunityToolkit*.dll' -ErrorAction SilentlyContinue | Remove-Item -Force
if (Test-Path (Join-Path $perfOut 'runtimes')) { Remove-Item -LiteralPath (Join-Path $perfOut 'runtimes') -Recurse -Force }

# 可选内置浏览器插件：放进 plugins-dist，供插件市场安装；默认不播种、不激活。
Write-Host "Publishing workspace.browser plugin -> staging ..."
$browserOut = Join-Path $stagingApp 'plugins-dist/workspace.browser'
$browserArgs = @('publish', (Join-Path $root 'src/Fufucode.Plugins.Browser/Fufucode.Plugins.Browser.csproj'),
    '-c', $Configuration, '-o', $browserOut, '--nologo', '-v', 'q')
if ($NoRestore) { $browserArgs += '--no-restore' }
& dotnet @browserArgs
if ($LASTEXITCODE -ne 0) { throw 'Browser plugin publish failed.' }
Get-ChildItem -LiteralPath $browserOut -Filter 'Fufucode.*.dll' |
    Where-Object { $_.Name -ne 'Fufucode.Plugins.Browser.dll' } |
    Remove-Item -Force
Get-ChildItem -LiteralPath $browserOut -Filter 'Avalonia*.dll' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem -LiteralPath $browserOut -Filter 'CommunityToolkit*.dll' -ErrorAction SilentlyContinue | Remove-Item -Force
if (Test-Path (Join-Path $browserOut 'runtimes')) { Remove-Item -LiteralPath (Join-Path $browserOut 'runtimes') -Recurse -Force }

# 可选资料库 / 本地审查插件：放进 plugins-dist，供插件市场安装；默认不播种、不激活。
foreach ($plugin in @(
    @{ Id='workspace.library'; Project='src/Fufucode.Plugins.Library/Fufucode.Plugins.Library.csproj'; Dll='Fufucode.Plugins.Library.dll' },
    @{ Id='workspace.local-review'; Project='src/Fufucode.Plugins.LocalReview/Fufucode.Plugins.LocalReview.csproj'; Dll='Fufucode.Plugins.LocalReview.dll' },
    @{ Id='tool.sketch'; Project='src/Fufucode.Plugins.Sketch/Fufucode.Plugins.Sketch.csproj'; Dll='Fufucode.Plugins.Sketch.dll' }
)) {
    Write-Host "Publishing $($plugin.Id) plugin -> staging ..."
    $pluginOut = Join-Path $stagingApp "plugins-dist/$($plugin.Id)"
    $pluginArgs = @('publish', (Join-Path $root $plugin.Project), '-c', $Configuration, '-o', $pluginOut, '--nologo', '-v', 'q')
    if ($NoRestore) { $pluginArgs += '--no-restore' }
    & dotnet @pluginArgs
    if ($LASTEXITCODE -ne 0) { throw "$($plugin.Id) plugin publish failed." }
    Get-ChildItem -LiteralPath $pluginOut -Filter 'Fufucode.*.dll' |
        Where-Object { $_.Name -ne $plugin.Dll } | Remove-Item -Force
    Get-ChildItem -LiteralPath $pluginOut -Filter 'Avalonia*.dll' -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem -LiteralPath $pluginOut -Filter 'CommunityToolkit*.dll' -ErrorAction SilentlyContinue | Remove-Item -Force
    if (Test-Path (Join-Path $pluginOut 'runtimes')) { Remove-Item -LiteralPath (Join-Path $pluginOut 'runtimes') -Recurse -Force }
}

# 本地调试只需要自有程序集的 PDB；Skia/HarfBuzz 的原生 PDB 合计约 100 MB，删掉。
Get-ChildItem -LiteralPath $stagingApp -Recurse -File -Filter *.pdb |
    Where-Object { $_.Name -notlike 'Fufucode.*.pdb' } |
    Remove-Item -Force -ErrorAction SilentlyContinue
# SDK 说明 XML 非运行必需。
Remove-Item -LiteralPath (Join-Path $stagingApp 'Microsoft.Web.WebView2.Core.xml') -Force -ErrorAction SilentlyContinue

# RID 特定发布不携带其它平台的 Avalonia 后端；UsePlatformDetect 只探测当前平台。
if ($RuntimeIdentifier -like 'win-*') {
    foreach ($unused in @('Avalonia.X11.dll', 'Avalonia.FreeDesktop.dll', 'Avalonia.FreeDesktop.AtSpi.dll', 'Avalonia.Native.dll', 'Tmds.DBus.Protocol.dll')) {
        Remove-Item -LiteralPath (Join-Path $stagingApp $unused) -Force -ErrorAction SilentlyContinue
    }
}

# 构建标记：channel 固定 local，写 commit 与构建时间，构成唯一构建身份。
$branch = $branchHint
$label = if (-not [string]::IsNullOrWhiteSpace($ReviewLabel)) { $ReviewLabel } else { $null }
if ([string]::IsNullOrWhiteSpace($label) -and $isReview) {
    $label = if ($branch) { "$branch@$commit" } else { "review@$commit" }
}
$buildInfo = [ordered]@{
    version = $version
    builtAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    channel = 'local'
}
if ($commit) { $buildInfo.commit = $commit }
if ($branch) { $buildInfo.branch = $branch }
if ($label) { $buildInfo.label = $label }
if ($isReview) {
    $buildInfo.worktree = $root
    $buildInfo.review = $true
}
$json = $buildInfo | ConvertTo-Json -Depth 3
Set-Content -Path (Join-Path $stagingApp 'build-info.json') -Value $json -Encoding Utf8
Set-Content -Path (Join-Path $stagingCli 'build-info.json') -Value $json -Encoding Utf8

$buildId = if ($commit) { "$commit-$(Get-Date -Format 'yyyyMMddHHmmss')" } else { "nocommit-$(Get-Date -Format 'yyyyMMddHHmmss')" }

# 部署：结束占用进程后把暂存产物同步到 out/app 与 out/cli。
function Merge-Build {
    param([string]$From, [string]$To)
    $failed = @()
    foreach ($file in Get-ChildItem -LiteralPath $From -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($From, $file.FullName)
        $target = Join-Path $To $relative
        $targetDir = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $targetDir)) { New-Item -ItemType Directory -Force -Path $targetDir | Out-Null }
        try { Copy-Item -LiteralPath $file.FullName -Destination $target -Force }
        catch { $failed += $relative }
    }
    return $failed
}

<#
删除目标目录中本次构建不再产出的文件。out/app 与 out/cli 是纯构建目录，
不做这一步会让解耦前的 DLL（例如 Fufucode.Pi.dll）和其它 RID 的原生库永久残留。
restart-pending.json 是运行时部署状态，必须保留。
#>
function Remove-StaleBuild {
    param([string]$From, [string]$To)
    $keep = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in Get-ChildItem -LiteralPath $From -Recurse -File) {
        [void]$keep.Add([IO.Path]::GetRelativePath($From, $file.FullName))
    }
    $removed = 0
    foreach ($file in Get-ChildItem -LiteralPath $To -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($To, $file.FullName)
        if ($keep.Contains($relative) -or $relative -eq 'restart-pending.json') { continue }
        try { Remove-Item -LiteralPath $file.FullName -Force; $removed++ } catch { }
    }
    Get-ChildItem -LiteralPath $To -Recurse -Directory -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object {
            if (-not (Get-ChildItem -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
            }
        }
    return $removed
}

$shouldDeploy = (-not $NoDeploy) -and (-not $isReview)
$stopped = 0
if ($shouldDeploy) {
    $stopped = Stop-RunningApp -ExpectedExe $appExe
}

New-Item -ItemType Directory -Force -Path $appOut, $cliOut | Out-Null
$failedApp = @(Merge-Build -From $stagingApp -To $appOut)
$failedCli = @(Merge-Build -From $stagingCli -To $cliOut)
$staleApp = Remove-StaleBuild -From $stagingApp -To $appOut
$staleCli = Remove-StaleBuild -From $stagingCli -To $cliOut
Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue

# 指针：安装在其它位置（%LOCALAPPDATA%\Programs）的应用无法向上找到仓库 out/app，
# 因此在用户的本地更新目录写一个指向真实构建的 build-info.json。
# 并行审阅构建绝不写这个指针，否则会覆盖用户正在使用的本地构建。
if (-not $isReview -and -not $NoPointer) {
    $localPointerDir = Join-Path $Env:LOCALAPPDATA 'Fufucode/updates/local'
    New-Item -ItemType Directory -Force -Path $localPointerDir | Out-Null
    $pointer = [ordered]@{
        version = $version
        builtAtUtc = $buildInfo.builtAtUtc
        channel = 'local'
        sourceDirectory = $appOut
    }
    if ($commit) { $pointer.commit = $commit }
    $pointer | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path $localPointerDir 'build-info.json') -Encoding Utf8
}

if ($isReview) {
    $review = [ordered]@{
        label = $label
        branch = $branch
        commit = $commit
        worktree = $root
        builtAtUtc = $buildInfo.builtAtUtc
        appExe = $appExe
        dataRoot = (Join-Path $outRoot 'data')
        launchArgs = @('--project', $root, '--data-root', (Join-Path $outRoot 'data'),
            '--parallel-review', '--review-label', $label)
    }
    $review | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $outRoot 'review.json') -Encoding Utf8
}

Write-Host ""
Write-Host $(if ($isReview) { "Review build ready: $appOut" } else { "Local build ready: $appOut" })
Write-Host "  version : $version"
Write-Host "  buildId : $buildId"
if ($label) { Write-Host "  label   : $label" }
if ($stopped -gt 0) { Write-Host "  stopped : $stopped 个运行中的实例" }
if ($failedApp.Count -gt 0) {
    Write-Warning "  $($failedApp.Count) 个文件未能覆盖（可能仍被占用）：$($failedApp -join ', ')"
}
if ($staleApp -gt 0 -or $staleCli -gt 0) {
    Write-Host "  cleaned : app $staleApp 个旧文件, cli $staleCli 个旧文件"
}

$shouldLaunch = $shouldDeploy -and (-not $NoRun) -and (Test-Path -LiteralPath $appExe)
if ($shouldLaunch) {
    Write-Host "  启动 : $appExe"
    Start-Process -FilePath $appExe -WorkingDirectory $appOut
} elseif ($isReview) {
    Write-Host "  审阅 : 在 review 面板逐个启动（独立数据根，不抢运行实例）"
} else {
    Write-Host "  应用内：设置 → 构建与更新 → 部署最新构建"
}
Write-Host ""
