<#
并行 feature 的轻量工作树守卫。

规则很短：
  - main 只做集成，不直接承载并行 feature；
  - 一个 feature 名 = 一个分支 = 一个工作树 = 一个写入者；
  - 结束只允许删除“干净且已合并”的工作树，绝不强删未提交工作。

用法：
    pwsh fucode/feature-worktree.ps1 -Name review-ui
    pwsh fucode/feature-worktree.ps1 -Name review-ui -Remove

新建输出：
    branch  : feature/review-ui
    worktree: <repo>/.claude/worktrees/review-ui
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Name,
    [string]$Base = 'main',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$slug = ($Name.Trim() -replace '[^A-Za-z0-9._-]+', '-').Trim('-', '.', '_')
if ([string]::IsNullOrWhiteSpace($slug)) { throw 'Feature 名不能为空。' }
$branch = "feature/$slug"
$path = Join-Path $root ".claude/worktrees/$slug"

function Assert-Clean {
    param([string]$Worktree)
    $top = @(& git -C $Worktree rev-parse --show-toplevel 2>$null)
    $expected = [IO.Path]::GetFullPath($Worktree)
    $actual = if ($top.Count -gt 0 -and $top[0]) { [IO.Path]::GetFullPath($top[0].Trim()) } else { '' }
    if ($LASTEXITCODE -ne 0 -or $actual -ne $expected) {
        throw "不是独立的 Git 工作树（可能已损坏）：$Worktree"
    }
    $dirty = @(& git -C $Worktree status --porcelain 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "不是有效的 Git 工作树：$Worktree" }
    if ($dirty.Count -gt 0) {
        throw "工作树有未提交改动，拒绝自动删除：$Worktree`n$($dirty -join "`n")"
    }
}

if ($Remove) {
    if (-not (Test-Path -LiteralPath $path)) { throw "工作树不存在：$path" }
    $current = [IO.Path]::GetFullPath((Get-Location).Path)
    $inside = $current.Equals($path, [StringComparison]::OrdinalIgnoreCase) -or
        $current.StartsWith($path + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    if ($inside) {
        throw "不要在被删除的工作树内运行 -Remove。先切到主工作树：$root"
    }
    Assert-Clean $path
    & git -C $root merge-base --is-ancestor $branch $Base 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "分支 $branch 尚未合并到 $Base，拒绝删除；先审核/合并，或保留工作树。"
    }
    & git -C $root worktree remove $path
    if ($LASTEXITCODE -ne 0) { throw "git worktree remove 失败：$path" }
    & git -C $root branch -d $branch
    if ($LASTEXITCODE -ne 0) { throw "git branch -d 失败：$branch" }
    Write-Host "已移除：$branch / $path"
    exit 0
}

if (Test-Path -LiteralPath $path) { throw "工作树已存在：$path" }
& git -C $root show-ref --verify --quiet "refs/heads/$branch"
if ($LASTEXITCODE -eq 0) { throw "分支已存在：$branch" }
& git -C $root worktree add -b $branch $path $Base
if ($LASTEXITCODE -ne 0) { throw "git worktree add 失败：$branch" }

$branchSlug = ($branch -replace '[^A-Za-z0-9._-]+', '-').Trim('-', '.', '_')
Write-Host "已创建并行工作树："
Write-Host "  branch  : $branch"
Write-Host "  worktree: $path"
Write-Host "  review  : $(Join-Path $root "out/review/$branchSlug")"
Write-Host "  label   : $branch@<commit>"
Write-Host ""
Write-Host "下一步："
Write-Host "  cd `"$path`""
Write-Host "  pwsh fucode/publish-local.ps1 -ReviewLabel `"$branch@<commit>`" -NoRun"
