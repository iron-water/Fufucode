<#
Build one optional plugin as a lean package that the host can install under
%LOCALAPPDATA%\Fufucode\plugins\<id>.

The host resolves Fufucode.Core/App/Avalonia/Microsoft.* and other shared
assemblies from its own load context. Keeping second copies in the plugin
package creates type-identity drift across upgrades, so those files and
unneeded native runtime folders are physically removed here.
#>
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$Version,
    [string]$ZipPath,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$projectPath = [IO.Path]::GetFullPath((Join-Path $root $Project))
if (!(Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw "插件项目不存在：$projectPath" }
$manifestPath = Join-Path (Split-Path $projectPath -Parent) 'manifest.json'
if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "插件 manifest 不存在：$manifestPath" }
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$id = [string]$manifest.id
if ([string]::IsNullOrWhiteSpace($id)) { throw "manifest 缺少 id：$manifestPath" }

$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
}
$staging = Join-Path $root ("tmp/publish-plugin-$([guid]::NewGuid().ToString('N'))")
$repoPrefix = $root.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
if (!([IO.Path]::GetFullPath($staging)).StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '插件临时目录越界。'
}
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    $args = @('publish', $projectPath, '-c', $Configuration, '-o', $staging, '--nologo', '-v', 'q')
    if ($NoRestore) { $args += '--no-restore' }
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "插件 publish 失败：$id" }

    $hostPatterns = @(
        'Fufucode.Core.*', 'Fufucode.App.*', 'Fufucode.App.Abstractions.*',
        'Fufucode.Cli.*', 'Avalonia.*', 'CommunityToolkit.Mvvm.*',
        'Microsoft.*', 'SQLitePCLRaw.*'
    )
    foreach ($pattern in $hostPatterns) {
        Get-ChildItem -LiteralPath $staging -Filter $pattern -File -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    }
    Get-ChildItem -LiteralPath $staging -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    $runtimes = Join-Path $staging 'runtimes'
    if (Test-Path -LiteralPath $runtimes) { Remove-Item -LiteralPath $runtimes -Recurse -Force }

    $manifestCopy = $manifest.PSObject.Copy()
    if (![string]::IsNullOrWhiteSpace($Version)) { $manifestCopy.version = $Version }
    $manifestCopy | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Encoding utf8

    $entry = [string]$manifest.entryAssembly
    if ([string]::IsNullOrWhiteSpace($entry) -or !(Test-Path -LiteralPath (Join-Path $staging $entry) -PathType Leaf)) {
        throw "插件发布结果缺少 entryAssembly：$entry"
    }

    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    Copy-Item -Path (Join-Path $staging '*') -Destination $output -Recurse -Force
    if (!(Test-Path -LiteralPath (Join-Path $output 'manifest.json') -PathType Leaf) -or
        !(Test-Path -LiteralPath (Join-Path $output $entry) -PathType Leaf)) {
        throw "插件安装目录不完整：$output"
    }
    if (![string]::IsNullOrWhiteSpace($ZipPath)) {
        $zip = if ([IO.Path]::IsPathRooted($ZipPath)) {
            [IO.Path]::GetFullPath($ZipPath)
        } else {
            [IO.Path]::GetFullPath((Join-Path $root $ZipPath))
        }
        $parent = Split-Path $zip -Parent
        if (!(Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
        Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -Force
        Write-Host "Plugin zip: $zip"
    }
    Write-Host "Plugin: $id"
    Write-Host "Output: $output"
}
finally {
    if (Test-Path -LiteralPath $staging) {
        if (!([IO.Path]::GetFullPath($staging)).StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw '拒绝清理越界临时目录。'
        }
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
