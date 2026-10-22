<#
把已验收的 feature 分支安全合入 main。

前提（任一不满足就拒绝，不在 main 上留下半合并状态）：
  - feature 工作树存在、干净、已提交；
  - main 工作树干净；
  - main 是 feature 的祖先（feature 已同步 main，合入是 fast-forward）。

用法：
    pwsh fucode/feature-merge.ps1 -Name review-ui
    pwsh fucode/feature-merge.ps1 -Name review-ui -DryRun

合入后不会自动删除工作树；确认 main 构建正常后再运行 feature-worktree.ps1 -Remove。
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Name,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$scriptRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Stop-WithError {
    param([string]$Message)
    Write-Host "错误：$Message" -ForegroundColor Red
    exit 1
}

$commonDir = @(& git -C $scriptRoot rev-parse --path-format=absolute --git-common-dir 2>$null)
if ($LASTEXITCODE -ne 0 -or $commonDir.Count -eq 0) {
    Stop-WithError "不是 Git 仓库：$scriptRoot"
}
$mainRoot = Split-Path -Parent $commonDir[0].Trim()
$slug = ($Name.Trim() -replace '[^A-Za-z0-9._-]+', '-').Trim('-', '.', '_')
if ([string]::IsNullOrWhiteSpace($slug)) { Stop-WithError "Feature 名不能为空。" }
$branch = "feature/$slug"
$worktree = Join-Path $mainRoot ".claude/worktrees/$slug"

if (-not (Test-Path -LiteralPath $worktree)) {
    Stop-WithError "工作树不存在：$worktree"
}
$top = @(& git -C $worktree rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or $top.Count -eq 0 -or
    [IO.Path]::GetFullPath($top[0].Trim()) -ne [IO.Path]::GetFullPath($worktree)) {
    Stop-WithError "不是独立的 feature 工作树：$worktree"
}
$featureDirty = @(& git -C $worktree status --porcelain 2>$null)
if ($featureDirty.Count -gt 0) {
    Stop-WithError "feature 工作树有未提交改动，先提交：`n$($featureDirty -join "`n")"
}
$mainDirty = @(& git -C $mainRoot status --porcelain 2>$null)
if ($mainDirty.Count -gt 0) {
    Stop-WithError "main 工作树有未提交改动，先提交或清空：`n$($mainDirty -join "`n")"
}
& git -C $mainRoot show-ref --verify --quiet "refs/heads/$branch"
if ($LASTEXITCODE -ne 0) { Stop-WithError "分支不存在：$branch" }

& git -C $mainRoot merge-base --is-ancestor main $branch 2>$null
if ($LASTEXITCODE -ne 0) {
    Stop-WithError "feature 没有同步最新的 main。先在 feature 工作树运行 git merge main，重新构建并验收，再合入。"
}
$count = [int]((& git -C $mainRoot rev-list --count "main..$branch" 2>$null) | Select-Object -First 1)
if ($count -eq 0) {
    Write-Host "没有需要合入的提交：$branch 已在 main 上。"
    exit 0
}

$head = (& git -C $worktree rev-parse --short=8 HEAD).Trim()
Write-Host "准备合入：$branch ($head) -> main"
if ($DryRun) {
    Write-Host "DryRun：检查通过，未改动 main。"
    exit 0
}

& git -C $mainRoot merge --ff-only $branch
if ($LASTEXITCODE -ne 0) {
    Stop-WithError "fast-forward 合并失败，main 未完成合并；检查 main 是否在检查后又有新提交。"
}

$merged = (& git -C $mainRoot rev-parse --short=8 HEAD).Trim()
Write-Host ""
Write-Host "已合入 main" -ForegroundColor Green
Write-Host "  branch : $branch"
Write-Host "  commit : $merged"
Write-Host "  next   : pwsh fucode/feature-publish.ps1 -Main"
Write-Host "  清理   : pwsh fucode/feature-worktree.ps1 -Name $slug -Remove"
exit 0
