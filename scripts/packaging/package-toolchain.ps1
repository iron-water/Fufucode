<#
可选工具链打包器：与后端插件同级，为 node / bun / python / git-bash / pi 生成
可下载的纯净运行时分发包。主安装包不包含任何工具链。

规则：
  - 用户系统已有该工具链时，应用直接使用，不需要下载；
  - 只有缺失时，用户才安装到 %LOCALAPPDATA%\Fufucode\runtime\<id>；
  - 每个包落地后必须能独立启动，脚本会做启动冒烟。

产物：
  out/runtimes/<id>-<version>.zip
  out/runtimes/<id>-<version>.json   （sha256 / size / 启动冒烟结果）

用法：
  pwsh scripts/packaging/package-toolchain.ps1 -Id pi
  pwsh scripts/packaging/package-toolchain.ps1 -Id node -Source "C:\Program Files\nodejs"
  pwsh scripts/packaging/package-toolchain.ps1 -Id python -Source C:\Python315
#>
param(
    [Parameter(Mandatory)][ValidateSet('pi', 'node', 'bun', 'python', 'git-bash')][string]$Id,
    [string]$Source,
    [string]$Version = '1.0.0',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $root 'out\runtimes' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null

$staging = Join-Path $root "out\.toolchain-$Id"
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null

# 每个工具链的“系统安装位置”与“包内相对路径”；与 ToolchainCatalog 保持一致。
$layouts = @{
    'node'     = @{ Root = (Join-Path $env:ProgramFiles 'nodejs'); Exe = 'node.exe'; Smoke = @('--version') }
    'bun'      = @{ Root = (Join-Path $env:USERPROFILE '.bun\bin'); Exe = 'bun.exe'; Smoke = @('--version') }
    'python'   = @{ Root = $null; Exe = 'python.exe'; Smoke = @('--version') }
    'git-bash' = @{ Root = (Join-Path $env:ProgramFiles 'Git'); Exe = 'bin\bash.exe'; Smoke = @('--version') }
    'pi'       = @{ Root = (Join-Path $root 'runtime\pi'); Exe = 'node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js'; Smoke = @('--version') }
}
$layout = $layouts[$Id]
if ([string]::IsNullOrWhiteSpace($Source)) {
    # 未显式给出时，从 PATH 中的可执行文件反推安装根目录。
    $commandName = switch ($Id) {
        'node' { 'node' } 'bun' { 'bun' } 'python' { 'python' } 'git-bash' { 'bash' } default { $null }
    }
    $command = if ($commandName) { Get-Command $commandName -ErrorAction SilentlyContinue } else { $null }
    if ($command) {
        $Source = switch ($Id) {
            'node' { Split-Path -Parent $command.Source }
            'bun' { Split-Path -Parent $command.Source }
            'python' { Split-Path -Parent $command.Source }
            # bash.exe 可能在 <Git>\bin 或 <Git>\usr\bin；向上找到同时含 bin 与 usr 的 Git 根。
            'git-bash' {
                $cursor = Split-Path -Parent $command.Source
                while ($cursor -and -not ((Test-Path (Join-Path $cursor 'usr')) -and (Test-Path (Join-Path $cursor 'bin')))) {
                    $cursor = Split-Path -Parent $cursor
                }
                if ($cursor) { $cursor } else { Split-Path -Parent $command.Source }
            }
            default { $layout.Root }
        }
    }
    if ([string]::IsNullOrWhiteSpace($Source)) { $Source = $layout.Root }
}
if ([string]::IsNullOrWhiteSpace($Source) -or -not (Test-Path -LiteralPath $Source)) {
    throw "Source for '$Id' not found; pass -Source <directory>."
}

Write-Host "Staging $Id from $Source ..."
if ($Id -eq 'pi') {
    # Pi 与主包时的处理一致：复制依赖树后裁剪运行时不读取的文件。
    Copy-Item -Path (Join-Path $Source '*') -Destination $staging -Recurse -Force
    $removed = 0
    Get-ChildItem -LiteralPath $staging -Recurse -File -Force | Where-Object {
        $_.Extension -in @('.map', '.d.ts', '.ts') -or
        ($_.Extension -in @('.md', '.markdown') -and $_.Name -notlike 'LICENSE*' -and $_.Name -notlike 'NOTICE*') -or
        $_.FullName -match '[\\/](test|tests|__tests__|docs?|examples?)[\\/]'
    } | ForEach-Object { $removed++; Remove-Item -LiteralPath $_.FullName -Force }
    Get-ChildItem -LiteralPath $staging -Recurse -Directory -Force |
        Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object {
            if (-not (Get-ChildItem -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath $_.FullName -Force
            }
        }
    Write-Host "  pruned $removed files"
}
elseif ($Id -eq 'node') {
    # 只发 node.exe 与许可证；npm/npx 不是运行 Pi 所必需。
    Copy-Item -LiteralPath (Join-Path $Source 'node.exe') -Destination (Join-Path $staging 'node.exe')
    foreach ($extra in @('LICENSE', 'LICENSE.md', 'LICENSE.txt')) {
        if (Test-Path -LiteralPath (Join-Path $Source $extra)) {
            Copy-Item -LiteralPath (Join-Path $Source $extra) -Destination $staging
        }
    }
}
elseif ($Id -eq 'bun') {
    Copy-Item -LiteralPath (Join-Path $Source 'bun.exe') -Destination (Join-Path $staging 'bun.exe')
}
elseif ($Id -eq 'python') {
    # 纯净 Python：完整标准库 + 解释器；不含 pip 缓存与测试。
    Copy-Item -Path (Join-Path $Source '*') -Destination $staging -Recurse -Force
    foreach ($relative in @('Lib\test', 'Lib\idlelib', 'Lib\tkinter', 'Lib\turtledemo', 'Lib\__pycache__')) {
        $target = Join-Path $staging $relative
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    }
}
elseif ($Id -eq 'git-bash') {
    # Git Bash：Git for Windows 的 bash.exe 依赖 usr/bin 下的 msys DLL，并且运行时
    # 需要 Git 根布局（usr/bin 会被解析成 /bin）。只发 usr 目录，根目录补 bin 转发脚本。
    $usr = Join-Path $Source 'usr'
    if (-not (Test-Path -LiteralPath $usr)) { throw "Git Bash source must contain 'usr': $Source" }
    Copy-Item -LiteralPath $usr -Destination (Join-Path $staging 'usr') -Recurse -Force
    # usr/share 主要是 man/doc/locale（约 69 MB），bash 运行不需要；保留 terminfo。
    foreach ($relative in @('share\doc', 'share\man', 'share\locale', 'share\info',
            'share\vim', 'share\perl5', 'share\misc', 'share\gtk-doc', 'share\gnupg')) {
        $target = Join-Path $staging "usr\$relative"
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    }
    New-Item -ItemType Directory -Force (Join-Path $staging 'bin') | Out-Null
    Copy-Item -LiteralPath (Join-Path $usr 'bin\bash.exe') -Destination (Join-Path $staging 'bin\bash.exe') -Force
    foreach ($relative in @('usr\share\doc', 'usr\share\man', 'mingw64\share\doc', 'mingw64\share\man')) {
        $target = Join-Path $staging $relative
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    }
}

$exePath = Join-Path $staging $layout.Exe
if (-not (Test-Path -LiteralPath $exePath)) { throw "Staged payload is missing $($layout.Exe)." }

# 启动冒烟：包内可执行文件必须能独立运行。
$smokeOutput = $null
$smokeExit = 1
if ($Id -eq 'pi') {
    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($node) { $smokeOutput = & $node.Source $exePath @($layout.Smoke) 2>&1; $smokeExit = $LASTEXITCODE }
    else { Write-Warning 'Node not found on this machine; Pi smoke test skipped.'; $smokeExit = 0 }
}
elseif ($Id -eq 'git-bash') {
    $smokeOutput = & $exePath -lc 'echo ok' 2>&1; $smokeExit = $LASTEXITCODE
}
else {
    $smokeOutput = & $exePath @($layout.Smoke) 2>&1; $smokeExit = $LASTEXITCODE
}
if ($smokeExit -ne 0) { throw "Smoke test failed for $Id (exit $smokeExit): $smokeOutput" }

$files = Get-ChildItem -LiteralPath $staging -Recurse -File -Force
$payloadBytes = ($files | Measure-Object -Sum Length).Sum
$manifest = [ordered]@{
    id = $Id
    version = $Version
    exe = $layout.Exe
    builtAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    fileCount = $files.Count
    payloadSizeBytes = [long]$payloadBytes
    smoke = [ordered]@{ exit = $smokeExit; output = ($smokeOutput -join ' ').Trim() }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $staging 'runtime.json') -Encoding Utf8

$zip = Join-Path $OutputDirectory "$Id-$Version.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $zip,
    [IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item -LiteralPath $staging -Recurse -Force

$sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$zipLength = (Get-Item -LiteralPath $zip).Length
$summary = [ordered]@{
    id = $Id
    version = $Version
    fileName = (Split-Path -Leaf $zip)
    sha256 = $sha
    sizeBytes = $zipLength
    unpackedBytes = $payloadBytes
    fileCount = $files.Count
    smoke = $manifest.smoke
    builtAtUtc = $manifest.builtAtUtc
}
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath ([IO.Path]::ChangeExtension($zip, '.json')) -Encoding Utf8

Write-Host ""
Write-Host ("Toolchain package: {0}" -f $zip)
Write-Host ("  payload : {0:N1} MB ({1} files)" -f ($payloadBytes / 1MB), $files.Count)
Write-Host ("  zip     : {0:N1} MB" -f ($zipLength / 1MB))
Write-Host ("  sha256  : {0}" -f $sha)
Write-Host ("  smoke   : exit {0} -> {1}" -f $smokeExit, $manifest.smoke.output)
