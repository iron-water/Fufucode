# 编译 GUI 启动器（tools/fufucode-launcher），把版本号与产品名写进资源。
#
# 为什么不用 windres -D：windres 的参数解析会吃掉带引号字符串里的内层引号，
# `-DVER_PRODUCT_NAME="\"Fufucode Test\""` 会直接变成 rc 语法错误。改为在临时目录
# 生成 version-overrides.h 再用 -I 传入，rc 里用 __has_include 可选包含它。
param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [Parameter(Mandatory = $true)][string]$AppVersion,
    # 写入 FileDescription / ProductName 的产品名（正式 Fufucode，测试 Fufucode Test）。
    [string]$ProductName = 'Fufucode',
    # OriginalFilename / InternalName。
    [string]$InternalName = 'Fufucode.exe'
)
$ErrorActionPreference = 'Stop'

if (-not (Get-Command gcc -ErrorAction SilentlyContinue)) {
    throw 'gcc is required to build the Fufucode launcher (Strawberry Perl / MSYS2 / mingw-w64).'
}
if (-not (Get-Command windres -ErrorAction SilentlyContinue)) {
    throw 'windres is required to embed the launcher icon and version resource.'
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$launcherDir = Join-Path $root 'tools\fufucode-launcher'

# 数字段：0.1.0-test.3 → 0,1,0,0。非数字段回落为 0。
$numericParts = @($AppVersion.Split('-')[0].Split('.')) | ForEach-Object {
    $value = 0
    if ([int]::TryParse($_, [ref]$value)) { $value } else { 0 }
}
$numeric = ($numericParts + @(0, 0, 0, 0))[0..3] -join ','
$numericString = ($numericParts + @(0, 0, 0, 0))[0..3] -join '.'

$overrideDir = Join-Path ([IO.Path]::GetTempPath()) ('fufucode-launcher-res-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $overrideDir | Out-Null
try {
    # \0 必须写成文件里的字面反斜杠 + 0（RC 字符串以 NUL 结尾），不能被 PowerShell 转成 NUL 字符。
    $header = @(
        "#define VER_FILEVERSION_CSV $numeric"
        "#define VER_FILEVERSION_STR `"$numericString\0`""
        "#define VER_PRODUCTVERSION_CSV $numeric"
        "#define VER_PRODUCTVERSION_STR `"$numericString\0`""
        "#define VER_PRODUCT_NAME `"$ProductName\0`""
        "#define VER_INTERNAL_NAME `"$InternalName\0`""
    )
    Set-Content -LiteralPath (Join-Path $overrideDir 'version-overrides.h') -Value $header -Encoding Ascii

    $objectPath = Join-Path $overrideDir 'launcher_res.o'
    Push-Location $launcherDir
    try {
        & windres -I $overrideDir 'fufucode.rc' -o $objectPath
        if ($LASTEXITCODE -ne 0) { throw 'windres failed for the Fufucode launcher.' }
        & gcc -O2 -s -mwindows 'fufucode.c' $objectPath -o $Destination
        if ($LASTEXITCODE -ne 0) { throw 'gcc failed for the Fufucode launcher.' }
        if (-not (Test-Path -LiteralPath $Destination)) { throw 'Fufucode launcher was not produced.' }
    } finally {
        Pop-Location
    }
} finally {
    Remove-Item -LiteralPath $overrideDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Launcher: $Destination (v$numericString, $ProductName)"
