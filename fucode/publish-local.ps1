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
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$appOut = Join-Path $root 'out/app'
$cliOut = Join-Path $root 'out/cli'
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
$stagingRoot = Join-Path $root 'out/.staging'
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
Write-Host "Publishing Fufucode.App ($Configuration) -> staging ..."
& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { throw 'Fufucode.App publish failed.' }

Write-Host "Publishing Fufucode.Cli ($Configuration) -> staging ..."
$cliArgs = @('publish', (Join-Path $root 'src/Fufucode.Cli/Fufucode.Cli.csproj'),
    '-c', $Configuration, '-r', $RuntimeIdentifier, '--self-contained', 'false',
    '-o', $stagingCli, '--nologo', '-v', 'q')
if ($NoRestore) { $cliArgs += '--no-restore' }
& dotnet @cliArgs
if ($LASTEXITCODE -ne 0) { throw 'Fufucode.Cli publish failed.' }

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
$buildInfo = [ordered]@{
    version = $version
    builtAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    channel = 'local'
}
if ($commit) { $buildInfo.commit = $commit }
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

$stopped = 0
if (-not $NoDeploy) {
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

Write-Host ""
Write-Host "Local build ready: $appOut"
Write-Host "  version : $version"
Write-Host "  buildId : $buildId"
if ($stopped -gt 0) { Write-Host "  stopped : $stopped 个运行中的实例" }
if ($failedApp.Count -gt 0) {
    Write-Warning "  $($failedApp.Count) 个文件未能覆盖（可能仍被占用）：$($failedApp -join ', ')"
}
if ($staleApp -gt 0 -or $staleCli -gt 0) {
    Write-Host "  cleaned : app $staleApp 个旧文件, cli $staleCli 个旧文件"
}

$shouldLaunch = (-not $NoRun) -and (-not $NoDeploy) -and (Test-Path -LiteralPath $appExe)
if ($shouldLaunch) {
    Write-Host "  启动 : $appExe"
    Start-Process -FilePath $appExe -WorkingDirectory $appOut
} else {
    Write-Host "  应用内：设置 → 构建与更新 → 部署最新构建"
}
Write-Host ""
