<#
并行 feature 审阅：把每个 Git 工作树构建到独立目录，生成一张审阅板，
并可逐个用独立数据根启动。不会停止或覆盖正在使用的 out/app。

用法：
    pwsh fucode/review-parallel.ps1                    # 盘点 + 构建所有工作树 + 生成审阅板
    pwsh fucode/review-parallel.ps1 -NoBuild           # 只盘点，不构建
    pwsh fucode/review-parallel.ps1 -Open              # 构建后打开 HTML 审阅板
    pwsh fucode/review-parallel.ps1 -Launch            # 启动所有已构建 feature
    pwsh fucode/review-parallel.ps1 -LaunchSlug fix-x  # 只启动一个
    pwsh fucode/review-parallel.ps1 -IncludeMain       # 把 main 也当作一个审阅项

输出：
    out/review/<slug>/app/Fufucode.App.exe
    out/review/<slug>/review.json
    out/review/<slug>/build.log
    out/review/index.html
    out/review/index.md

每个 review 构建带 label（默认 branch@commit），窗口标题会显示该 label；
启动时使用独立 --data-root，并带 --parallel-review 跳过单实例守卫。
#>
param(
    [string]$Filter = '',
    [string[]]$Branch = @(),
    [switch]$NoBuild,
    [switch]$NoRestore,
    [switch]$IncludeMain,
    [switch]$Launch,
    [string]$LaunchSlug = '',
    [switch]$Mock,
    [switch]$Open,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$reviewRoot = Join-Path $root 'out/review'
New-Item -ItemType Directory -Force -Path $reviewRoot | Out-Null

function Invoke-Git {
    param([string]$Worktree, [string[]]$Arguments)
    $output = & git -C $Worktree @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { return @() }
    return @($output)
}

function Get-Worktrees {
    $items = [Collections.Generic.List[object]]::new()
    $current = $null
    foreach ($line in (& git -C $root worktree list --porcelain)) {
        if ($line -like 'worktree *') {
            if ($null -ne $current) { $items.Add([pscustomobject]$current) }
            $current = [ordered]@{ Path = $line.Substring(9); Head = ''; Branch = '' }
        } elseif ($line -like 'HEAD *' -and $null -ne $current) {
            $current.Head = $line.Substring(5)
        } elseif ($line -like 'branch *' -and $null -ne $current) {
            $current.Branch = $line.Substring(7) -replace '^refs/heads/', ''
        } elseif ([string]::IsNullOrWhiteSpace($line) -and $null -ne $current) {
            $items.Add([pscustomobject]$current); $current = $null
        }
    }
    if ($null -ne $current) { $items.Add([pscustomobject]$current) }
    return $items
}

function Get-Slug {
    param([string]$Value)
    $slug = ($Value -replace '[^A-Za-z0-9._-]+', '-').Trim('-', '.', '_')
    if ([string]::IsNullOrWhiteSpace($slug)) { $slug = 'feature' }
    return $slug
}

$sharedPatterns = @(
    'Fufucode.sln', 'Directory.Build.props', 'Directory.Packages.props',
    'AGENTS.md', '*.csproj', 'fucode/*', 'scripts/packaging/*',
    'src/Fufucode.App/Views/MainWindow*', 'src/Fufucode.App/Views/SettingsLayout.cs',
    'src/Fufucode.App/ViewModels/SettingsPageViewModel*', 'src/Fufucode.Core/AppSettings.cs'
)

$mainBranch = 'main'
$worktrees = Get-Worktrees
$features = [Collections.Generic.List[object]]::new()
foreach ($item in $worktrees) {
    $isMain = ([IO.Path]::GetFullPath($item.Path) -eq [IO.Path]::GetFullPath($root))
    if ($isMain -and -not $IncludeMain) { continue }
    $branchName = if ($item.Branch) { $item.Branch } else { '(detached)' }
    if ($Filter -and $branchName -notlike $Filter) { continue }
    if ($Branch.Count -gt 0 -and $branchName -notin $Branch) { continue }

    $head = if ($item.Head) { $item.Head.Substring(0, [Math]::Min(8, $item.Head.Length)) } else { 'unknown' }
    $label = "$branchName@$head"
    if ($isMain) { $label = "main@$head" }
    $slug = Get-Slug $branchName
    $reviewOut = Join-Path $reviewRoot $slug
    $buildScript = Join-Path $item.Path 'fucode/publish-local.ps1'
    $dirty = @(& git -C $item.Path status --porcelain 2>$null).Count
    $ahead = 0; $behind = 0; $mergeBase = ''
    $base = (& git -C $root merge-base $mainBranch $item.Head 2>$null)
    if ($LASTEXITCODE -eq 0 -and $base) {
        $mergeBase = $base.Trim()
        $ahead = [int]((& git -C $root rev-list --count "$mainBranch..$($item.Head)" 2>$null) | Select-Object -First 1)
        $behind = [int]((& git -C $root rev-list --count "$($item.Head)..$mainBranch" 2>$null) | Select-Object -First 1)
    }
    $changed = @()
    if ($mergeBase) { $changed += @(& git -C $item.Path diff --name-only "$mergeBase..$($item.Head)" 2>$null) }
    $changed += @(& git -C $item.Path status --porcelain 2>$null | ForEach-Object { $_.Substring(3) })
    $changed = @($changed | Where-Object { $_ } | Sort-Object -Unique)
    $risk = @($changed | Where-Object {
        $path = $_
        $sharedPatterns | Where-Object { $path -like $_ } | Select-Object -First 1
    }).Count

    $features.Add([pscustomobject]@{
        Path = $item.Path
        Branch = $branchName
        Head = $head
        Label = $label
        Slug = $slug
        ReviewOut = $reviewOut
        BuildScript = $buildScript
        HasBuildScript = (Test-Path -LiteralPath $buildScript)
        Dirty = $dirty
        Ahead = $ahead
        Behind = $behind
        MergeBase = $mergeBase
        Changed = $changed
        Risk = $risk
        BuildExit = $null
        BuildError = ''
        BuildLog = ''
        DurationMs = 0
        Published = $false
        PublishedLabel = ''
        PublishedCommit = ''
        PublishedAtUtc = ''
        PublishedStale = $false
    })
}

if ($features.Count -eq 0) {
    Write-Host "没有发现可审阅的工作树。"
    exit 0
}

if (-not $NoBuild) {
    $pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
    if (-not $pwsh) { $pwsh = (Get-Command powershell -ErrorAction Stop).Source }
    Write-Host "构建 $($features.Count) 个审阅项（串行，避免多工作树同时争用 CPU/IO）..."

    foreach ($item in $features) {
        if (-not $item.HasBuildScript) {
            $item.BuildExit = 127
            $item.BuildError = '工作树缺少 fucode/publish-local.ps1'
            continue
        }
        New-Item -ItemType Directory -Force -Path $item.ReviewOut | Out-Null
        $log = Join-Path $item.ReviewOut 'build.log'
        $err = Join-Path $item.ReviewOut 'build.err.log'
        $started = [DateTimeOffset]::UtcNow
        $args = @('-NoProfile', '-File', $item.BuildScript, '-Configuration', $Configuration,
            '-OutputRoot', $item.ReviewOut, '-ReviewLabel', $item.Label, '-NoDeploy', '-NoRun')
        if ($NoRestore) { $args += '-NoRestore' }
        $p = Start-Process -FilePath $pwsh -ArgumentList $args -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $log -RedirectStandardError $err
        $item.DurationMs = [int]([DateTimeOffset]::UtcNow - $started).TotalMilliseconds
        $item.BuildExit = $p.ExitCode
        $item.BuildLog = $log
        $item.BuildError = if (Test-Path -LiteralPath $err) { (Get-Content -LiteralPath $err -Tail 30) -join "`n" } else { '' }
    }
} else {
    # 只盘点：模型可能已经各自发布过构建。读取 review.json，区分“没构建”和
    # “有构建但落后于当前 HEAD”，避免审阅板把已有产物误报成未构建。
    foreach ($item in $features) {
        $reviewFile = Join-Path $item.ReviewOut 'review.json'
        $exe = Join-Path $item.ReviewOut 'app/Fufucode.App.exe'
        if (-not (Test-Path -LiteralPath $reviewFile) -or -not (Test-Path -LiteralPath $exe)) { continue }
        try { $review = Get-Content -LiteralPath $reviewFile -Raw | ConvertFrom-Json } catch { continue }
        if (-not $review) { continue }
        $item.Published = $true
        $item.PublishedLabel = [string]$review.label
        $item.PublishedCommit = [string]$review.commit
        $item.PublishedAtUtc = [string]$review.builtAtUtc
        $publishedShort = if ($item.PublishedCommit) {
            $item.PublishedCommit.Substring(0, [Math]::Min(8, $item.PublishedCommit.Length))
        } else { '' }
        $item.PublishedStale = [bool]($publishedShort -and $item.Head -and
            $publishedShort -ne $item.Head)
    }
}

function Get-EffectiveLabel {
    param($item)
    if ($item.Published -and -not [string]::IsNullOrWhiteSpace($item.PublishedLabel)) {
        return $item.PublishedLabel
    }
    return $item.Label
}

function Get-LaunchCommand {
    param($item)
    $exe = Join-Path $item.ReviewOut 'app/Fufucode.App.exe'
    $args = @('--project', $root, '--data-root', (Join-Path $item.ReviewOut 'data'),
        '--parallel-review', '--review-label', (Get-EffectiveLabel $item))
    if ($Mock) { $args += '--mock' }
    $quoted = $args | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    return ('& "{0}" {1}' -f $exe, ($quoted -join ' '))
}

function Get-Status {
    param($item)
    if ($item.Published) {
        $published = if ($item.PublishedLabel) { $item.PublishedLabel } else { $item.PublishedCommit }
        if ($item.PublishedStale) {
            return "已发布旧构建 $published（当前 $($item.Head)）"
        }
        return "已发布 $published"
    }
    if ($null -eq $item.BuildExit) { return '未构建' }
    if ($item.BuildExit -eq 0) { return '构建通过' }
    return "构建失败 ($($item.BuildExit))"
}

function Get-StatusColor {
    param($item)
    if ($item.Published) {
        if ($item.PublishedStale) { return '#9a6700' }
        return '#1a7f37'
    }
    if ($null -eq $item.BuildExit) { return '#6b7280' }
    if ($item.BuildExit -eq 0) { return '#1a7f37' }
    return '#cf222e'
}

$htmlRows = [Text.StringBuilder]::new()
$mdRows = [Text.StringBuilder]::new()
$mdRows.AppendLine('| Feature | Build | Branch | Commit | Ahead/Behind | Dirty | Risk | Changed |') | Out-Null
$mdRows.AppendLine('|---|---|---|---|---|---:|---:|---:|') | Out-Null
foreach ($item in ($features | Sort-Object @{ Expression = {
            if ($_.Published) { if ($_.PublishedStale) { 1 } else { 2 } }
            elseif ($null -eq $_.BuildExit) { 4 }
            elseif ($_.BuildExit -ne 0) { 0 }
            else { 3 }
        } }, Label)) {
    $status = Get-Status $item
    $color = Get-StatusColor $item
    $logRel = "$($item.Slug)/build.log"
    $labelHtml = [System.Net.WebUtility]::HtmlEncode($item.Label)
    $statusHtml = [System.Net.WebUtility]::HtmlEncode($status)
    $launchText = Get-LaunchCommand $item
    $launchHtml = $launchText.Replace('&', '&amp;').Replace('"', '&quot;').Replace('<', '&lt;')
    $changedHtml = if ($item.Changed.Count -gt 0) {
        '<details><summary>' + $item.Changed.Count + ' files</summary><pre>' +
            [System.Net.WebUtility]::HtmlEncode(($item.Changed -join "`n")) + '</pre></details>'
    } else { '0' }
    $errorHtml = if ($item.BuildError) {
        '<details><summary>错误输出</summary><pre>' + [System.Net.WebUtility]::HtmlEncode($item.BuildError) + '</pre></details>'
    } else { '' }
    $htmlRows.AppendLine(@"
<article class="card">
  <header><strong>$labelHtml</strong><span class="status" style="color:$color">$statusHtml</span></header>
  <p class="meta">$($item.Branch) · $($item.Head) · ahead $($item.Ahead) / behind $($item.Behind) · dirty $($item.Dirty) · risk $($item.Risk)</p>
  <p class="launch"><code>$launchHtml</code></p>
  <p>$changedHtml $errorHtml <a href="$logRel">build.log</a></p>
</article>
"@) | Out-Null
    $mdRows.AppendLine("| $($item.Label) | $status | $($item.Branch) | $($item.Head) | $($item.Ahead)/$($item.Behind) | $($item.Dirty) | $($item.Risk) | $($item.Changed.Count) |") | Out-Null
}

$generated = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
$html = @"
<!doctype html>
<html lang="zh-CN"><meta charset="utf-8">
<title>Fufucode parallel feature review</title>
<style>
body{font:14px/1.5 "Segoe UI",sans-serif;background:#111;color:#ddd;margin:24px}
h1{font-size:22px} .meta{color:#999}.card{border:1px solid #333;border-radius:10px;padding:14px 18px;margin:12px 0;background:#181818}
header{display:flex;justify-content:space-between;gap:12px}.status{font-weight:600}
code,pre{font-family:Consolas,monospace;background:#0b0b0b;color:#9cdcfe;padding:6px 8px;border-radius:6px;display:block;overflow:auto}
a{color:#4cc2ff}
</style>
<h1>Fufucode parallel feature review</h1>
<p class="meta">生成于 $generated · 仓库 $([System.Net.WebUtility]::HtmlEncode($root)) · 构建配置 $Configuration</p>
<p class="meta">每个 feature 构建在 out/review/&lt;slug&gt;，启动使用独立 data-root；不会覆盖 out/app。</p>
$($htmlRows.ToString())
</html>
"@
Set-Content -LiteralPath (Join-Path $reviewRoot 'index.html') -Value $html -Encoding Utf8
$md = "# Fufucode parallel feature review`n`n生成于 $generated`n`n" + $mdRows.ToString()
Set-Content -LiteralPath (Join-Path $reviewRoot 'index.md') -Value $md -Encoding Utf8

$features | Select-Object Label, Branch, Head, BuildExit,
    @{ Name = 'Published'; Expression = { if ($_.Published) { $_.PublishedLabel } else { '' } } },
    @{ Name = 'Stale'; Expression = { $_.PublishedStale } },
    Dirty, Ahead, Behind, Risk, ReviewOut |
    Format-Table -AutoSize
Write-Host "审阅板: $(Join-Path $reviewRoot 'index.html')"

if ($Launch -or $LaunchSlug) {
    foreach ($item in $features) {
        if ($LaunchSlug -and $item.Slug -ne $LaunchSlug) { continue }
        $exe = Join-Path $item.ReviewOut 'app/Fufucode.App.exe'
        $launchLabel = Get-EffectiveLabel $item
        if (-not (Test-Path -LiteralPath $exe)) { Write-Warning "跳过 $launchLabel：尚未构建"; continue }
        if ($item.PublishedStale) {
            Write-Warning "$launchLabel 是旧构建（$($item.PublishedCommit)），当前 HEAD 为 $($item.Head)；请先重新构建。"
        }
        $args = @('--project', $root, '--data-root', (Join-Path $item.ReviewOut 'data'),
            '--parallel-review', '--review-label', $launchLabel)
        if ($Mock) { $args += '--mock' }
        Start-Process -FilePath $exe -ArgumentList $args -WorkingDirectory (Join-Path $item.ReviewOut 'app')
        Write-Host "已启动: $launchLabel"
    }
}

if ($Open) { Start-Process (Join-Path $reviewRoot 'index.html') }

$failed = @($features | Where-Object { $_.BuildExit -ne 0 -and $null -ne $_.BuildExit })
if ($failed.Count -gt 0) { exit 1 }
exit 0
