<#
并行 feature 的一键发布入口。给执行模型用，不需要人工拼路径或 label。

做五件事：
  1. 确认自己在 feature 工作树，不在 main；
  2. 确认工作树干净（除非显式 -AllowDirty）；
  3. 停掉该 feature 上一次启动的审阅窗口；
  4. 用 branch@commit 作为 label 发布审阅构建；
  5. 校验 review.json 后立即启动新窗口，用户直接看效果。

用法（在 feature 工作树内）：
    pwsh fucode/feature-publish.ps1
    pwsh fucode/feature-publish.ps1 -NoRestore
    pwsh fucode/feature-publish.ps1 -AllowDirty
    pwsh fucode/feature-publish.ps1 -NoLaunch   # 只构建，不启动

成功后把输出的 branch / commit / label / output / PID 写进交付卡。
#>
param(
    [string]$Configuration = 'Debug',
    [switch]$NoRestore,
    [switch]$AllowDirty,
    # 只构建不启动（CI/批量验证用）。默认构建成功后立即启动审阅窗口。
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Stop-WithError {
    param([string]$Message)
    Write-Host "错误：$Message" -ForegroundColor Red
    exit 1
}

$top = @(& git -C $root rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or $top.Count -eq 0) {
    Stop-WithError "不是 Git 工作树：$root"
}
$topPath = [IO.Path]::GetFullPath($top[0].Trim())
if (-not $topPath.Equals([IO.Path]::GetFullPath($root), [StringComparison]::OrdinalIgnoreCase)) {
    Stop-WithError "请在 feature 工作树根目录运行（当前解析到 $topPath）。"
}

$branch = @(& git -C $root rev-parse --abbrev-ref HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or $branch.Count -eq 0) {
    Stop-WithError "无法读取当前分支。"
}
$branch = $branch[0].Trim()
if ($branch -in @('main', 'master', 'HEAD')) {
    Stop-WithError "当前分支是 $branch。并行 feature 必须在独立工作树和 feature 分支上发布。"
}

$dirty = @(& git -C $root status --porcelain 2>$null)
if ($dirty.Count -gt 0 -and -not $AllowDirty) {
    Write-Host "工作树有未提交改动：" -ForegroundColor Yellow
    $dirty | ForEach-Object { Write-Host "  $_" }
    Stop-WithError "先提交，或确认这些改动确实要进入本次审阅构建后加 -AllowDirty。"
}

$sha = @(& git -C $root rev-parse --short=8 HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or $sha.Count -eq 0) {
    Stop-WithError "无法读取 HEAD。"
}
$sha = $sha[0].Trim()
$label = "$branch@$sha"

$commonDir = @(& git -C $root rev-parse --path-format=absolute --git-common-dir 2>$null)
if ($LASTEXITCODE -ne 0 -or $commonDir.Count -eq 0) {
    Stop-WithError "无法定位主工作树。"
}
$sharedRoot = Split-Path -Parent $commonDir[0].Trim()
$slug = ($branch -replace '[^A-Za-z0-9._-]+', '-').Trim('-', '.', '_')
$reviewOut = Join-Path $sharedRoot "out/review/$slug"
$reviewFile = Join-Path $reviewOut 'review.json'
$launchFile = Join-Path $reviewOut 'launch.json'
$exe = Join-Path $reviewOut 'app/Fufucode.App.exe'

# 同一 feature 重新发布时，先停掉上一次由本脚本启动的窗口，避免旧进程锁住 exe/dll
# 导致新构建复制失败，也避免用户同时看到两个同名窗口。
if (Test-Path -LiteralPath $launchFile) {
    try {
        $previous = Get-Content -LiteralPath $launchFile -Raw | ConvertFrom-Json
        $previousPid = [int]$previous.pid
        if ($previousPid -gt 0) {
            $process = Get-Process -Id $previousPid -ErrorAction SilentlyContinue
            if ($process) {
                $sameExe = $false
                try {
                    $processPath = $process.Path
                    $sameExe = $processPath -and
                        ([IO.Path]::GetFullPath($processPath).Equals([IO.Path]::GetFullPath($exe),
                            [StringComparison]::OrdinalIgnoreCase))
                } catch { }
                if ($sameExe) {
                    Write-Host "停止上一次审阅窗口：PID $previousPid"
                    $process.CloseMainWindow() | Out-Null
                    if (-not $process.WaitForExit(3000)) { Stop-Process -Id $previousPid -Force }
                }
            }
        }
    } catch {
        Write-Host "上一次启动记录无法读取，继续构建：$($_.Exception.Message)" -ForegroundColor Yellow
    }
}

$publish = Join-Path $root 'fucode/publish-local.ps1'
if (-not (Test-Path -LiteralPath $publish)) {
    Stop-WithError "找不到发布脚本：$publish"
}

$pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
if (-not $pwsh) {
    Stop-WithError "找不到 pwsh（PowerShell 7）。安装后再运行。"
}

Write-Host "发布审阅构建：$label"
$args = @('-NoProfile', '-File', $publish, '-Configuration', $Configuration,
    '-ReviewLabel', $label, '-NoRun')
if ($NoRestore) { $args += '-NoRestore' }
& $pwsh @args
if ($LASTEXITCODE -ne 0) {
    Stop-WithError "构建失败（退出码 $LASTEXITCODE）。修复后重新运行本脚本。"
}

if (-not (Test-Path -LiteralPath $reviewFile) -or -not (Test-Path -LiteralPath $exe)) {
    Stop-WithError "构建结束但审阅产物不完整：$reviewOut"
}
try {
    $review = Get-Content -LiteralPath $reviewFile -Raw | ConvertFrom-Json
} catch {
    Stop-WithError "review.json 无法读取：$reviewFile"
}
if ([string]$review.label -ne $label) {
    Stop-WithError "review.json label 不一致：期望 $label，实际 $($review.label)"
}
if (-not [string]$review.commit -or -not ([string]$review.commit).StartsWith($sha)) {
    Stop-WithError "review.json commit 与 HEAD 不一致：期望 $sha，实际 $($review.commit)"
}

$dataRoot = Join-Path $reviewOut 'data'
$started = $null
if (-not $NoLaunch) {
    $appArgs = @('--project', $root, '--data-root', $dataRoot,
        '--parallel-review', '--review-label', $label)
    $started = Start-Process -FilePath $exe -ArgumentList $appArgs `
        -WorkingDirectory (Split-Path $exe) -PassThru
    Start-Sleep -Milliseconds 1500
    $started.Refresh()
    if ($started.HasExited) {
        Stop-WithError "构建成功，但应用启动后立即退出（exit $($started.ExitCode)）。"
    }
    [ordered]@{
        pid = $started.Id
        label = $label
        branch = $branch
        commit = $sha
        worktree = $root
        appExe = $exe
        dataRoot = $dataRoot
        startedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $launchFile -Encoding Utf8
} else {
    Remove-Item -LiteralPath $launchFile -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "审阅构建已发布并启动" -ForegroundColor Green
Write-Host "  branch : $branch"
Write-Host "  commit : $sha"
Write-Host "  label  : $label"
Write-Host "  output : $reviewOut"
if ($started) {
    Write-Host "  window : PID $($started.Id)（标题应显示 $label）"
} else {
    Write-Host "  window : 未启动（-NoLaunch）"
}
if ($dirty.Count -gt 0) {
    Write-Host "  dirty  : 是（构建包含未提交改动）" -ForegroundColor Yellow
}
Write-Host ""
exit 0
