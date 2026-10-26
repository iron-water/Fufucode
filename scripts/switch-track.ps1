#Requires -Version 7.0
<#
.SYNOPSIS
    在「完整特性」与「最小构建」两条轨道间切换。

.DESCRIPTION
    默认行为由仓库根 Directory.Build.props 自动判定：
        本机存在 LocalPreviewFeatures=true  -> 完整特性
        否则                                -> 最小构建

    本脚本用于手动覆盖这个默认值，写入仓库根的 local.dev.props
    （已被 .gitignore 忽略，不会推送到远端）。

.PARAMETER Track
    full  = 启用扩展特性（本地开发）
    min   = 关闭全部扩展特性（最小构建）
    auto  = 删除 local.dev.props，回到自动判定

.PARAMETER Status
    只打印当前轨道，不做修改。

.EXAMPLE
    pwsh -File scripts\switch-track.ps1 -Status
    pwsh -File scripts\switch-track.ps1 -Track min
    pwsh -File scripts\switch-track.ps1 -Track full
#>
[CmdletBinding()]
param(
    [ValidateSet('full','min','auto')]
    [string]$Track,
    [switch]$Status
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path (Join-Path $repo 'Fufucode.sln'))) { $repo = $PWD.Path }
$propsFile = Join-Path $repo 'local.dev.props'
$proj = Join-Path $repo 'src\Fufucode.App\Fufucode.App.csproj'

function Get-SwitchValue([string]$name) {
    if (-not (Test-Path $proj)) { return '(无项目)' }
    $v = dotnet msbuild $proj -getProperty:$name 2>&1 | Select-Object -Last 1
    if (-not $v) { return '' }
    return $v.ToString().Trim()
}

function Show-Status {
    $enhanced = Get-SwitchValue 'EnableEnhancedPreview'
    $tooling  = Get-SwitchValue 'EnableDevTooling'
    $hasLocal = Test-Path $propsFile

    Write-Host "================================================================"
    Write-Host " 构建轨道"
    Write-Host "================================================================"
    Write-Host "  覆盖文件 local.dev.props : $(if ($hasLocal) { '存在（手动覆盖中）' } else { '不存在（自动判定）' })"
    Write-Host "  EnableEnhancedPreview   : $(if ($enhanced -eq 'true') { 'true  ← 扩展特性启用' } else { 'false ← 最小构建' })"
    Write-Host "  EnableDevTooling        : $(if ($tooling -eq 'true') { 'true  ← 开发工具（仅 Debug）' } else { 'false' })"
    Write-Host ""
    if ($enhanced -eq 'true') {
        Write-Host "  当前轨道: 完整特性" -ForegroundColor Cyan
    } else {
        Write-Host "  当前轨道: 最小构建" -ForegroundColor Yellow
    }
    Write-Host "================================================================"
}

if ($Status -or -not $Track) { Show-Status; return }

switch ($Track) {
    'auto' {
        if (Test-Path $propsFile) { Remove-Item $propsFile -Force; Write-Host "已删除 local.dev.props，回到自动判定" -ForegroundColor Green }
        else { Write-Host "local.dev.props 本就不存在，已是自动判定" -ForegroundColor DarkGray }
    }
    'full' {
        @'
<Project>
  <!-- 由 scripts\switch-track.ps1 写入；已被 .gitignore 忽略 -->
  <PropertyGroup>
    <EnableEnhancedPreview>true</EnableEnhancedPreview>
    <EnableDevTooling>true</EnableDevTooling>
  </PropertyGroup>
</Project>
'@ | Set-Content -Path $propsFile -Encoding UTF8
        Write-Host "已切换到：完整特性" -ForegroundColor Green
    }
    'min' {
        @'
<Project>
  <!-- 由 scripts\switch-track.ps1 写入；已被 .gitignore 忽略 -->
  <PropertyGroup>
    <EnableEnhancedPreview>false</EnableEnhancedPreview>
    <EnableDevTooling>false</EnableDevTooling>
  </PropertyGroup>
</Project>
'@ | Set-Content -Path $propsFile -Encoding UTF8
        Write-Host "已切换到：最小构建" -ForegroundColor Green
    }
}

Write-Host ""
Show-Status
Write-Host ""
Write-Host "提示：切换后需重新 restore：" -ForegroundColor DarkGray
Write-Host "  dotnet restore" -ForegroundColor DarkGray
