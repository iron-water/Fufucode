# Fufucode 本地开发更新循环
# 用法：./scripts/dev/update-local.ps1 [-Version 1.4.6] [-SkipBuild]
#
# 流程：dotnet publish → 打 zip → 生成 feed.json + sha256 → 拷贝到 %LOCALAPPDATA%\Fufucode\updates\
# 然后打开应用 → 设置 → 软件更新 → 检查更新 → 下载（本地复制）→ 立即安装并重启
#
# 用户每次改完代码只需跑这个脚本，然后在已安装应用里点"检查更新"。

param(
    [string]$Version = "",
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$proj = Join-Path $root 'src\Fufucode.App\Fufucode.App.csproj'

# --- 版本号：从 csproj 读，未给则 auto-bump ---
if (-not $Version) {
    $xml = [xml](Get-Content $proj)
    $verNode = $xml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
    $Version = if ($verNode) { $verNode.Version } else { "1.4.6-dev" }
    if ($Version -match '^\d+\.\d+\.(\d+)$') {
        $Version = $Version -replace '\.(\d+)$', { '.' + ([int]$Matches[1] + 1) }
    } else {
        $Version = "1.4.6-dev"
    }
}
Write-Host "==> Version: $Version"

# --- 1. 发布 ---
$publishDir = Join-Path $root "out\dev-update\$Version"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force $publishDir | Out-Null

if (-not $SkipBuild) {
    Write-Host "==> Publishing Fufucode.App (win-x64 self-contained)..."
    dotnet publish $proj -c Release -r win-x64 --self-contained true -o $publishDir /p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
}

# --- 2. 打包成 zip（manifest 描述的 packageUrl 指向 zip）---
$zipPath = Join-Path $publishDir "Fufucode-$Version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipSize = (Get-Item $zipPath).Length
$sha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash

# --- 3. 生成 feed.json（本地 manifest）---
$feedDir = Join-Path $env:LOCALAPPDATA 'Fufucode\updates'
New-Item -ItemType Directory -Force $feedDir | Out-Null
$feedPath = Join-Path $feedDir 'feed.json'

$feed = @{
    version = $Version
    releaseDate = (Get-Date).ToUniversalTime().ToString("o")
    releaseNotes = "本地开发更新 v$Version（dev loop）"
    packageUrl = $zipPath
    sha256 = $sha256
    packageSizeBytes = $zipSize
    minCompatibleVersion = "1.0.0"
    signaturePolicy = "Unsigned"
}
$feed | ConvertTo-Json | Set-Content -Path $feedPath -Encoding UTF8

Write-Host ""
Write-Host "==> Done."
Write-Host "   Package: $zipPath ($([math]::Round($zipSize/1MB,1)) MB)"
Write-Host "   Feed:    $feedPath"
Write-Host ""
Write-Host "==> 应用内：设置 → 软件更新 → 检查更新 → 下载 → 立即安装并重启"
