# PKG-1：原生渠道（零 Node/Pi 载荷）打包边界与清单名行为的回归测试。
# 只构造临时载荷夹具，不触发 dotnet publish / ISCC，可在数秒内跑完。
$ErrorActionPreference = 'Stop'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('fufucode-native-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $temporary 'build-native'
New-Item -ItemType Directory -Path (Join-Path $package 'app') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $package 'cli') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $package 'app/Fufucode.App.exe'), 'native app fixture')
[IO.File]::WriteAllText((Join-Path $package 'cli/Fufucode.Cli.exe'), 'native cli fixture')

try {
    # 1. 原生渠道清单名：源目录里只应出现 integrity-native.sha256.json，不能出现全量渠道的两个文件名。
    & (Join-Path $PSScriptRoot 'integrity.ps1') -Mode Write -PackageRoot $package -ManifestName 'integrity-native.sha256.json'
    foreach ($unexpected in @('integrity.sha256.json')) {
        if (Test-Path -LiteralPath (Join-Path $package $unexpected)) { throw "Native channel wrote the full-channel manifest: $unexpected" }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $package 'integrity-native.sha256.json') -Raw | ConvertFrom-Json
    if (@($manifest.files).Count -ne 2) { throw 'Native manifest must cover the two published executables only.' }

    # 2. 原生渠道归档：按 integrity-native.sha256.json 逐文件校验，且不产出全量渠道 zip 名。
    $destination = Join-Path $temporary 'FufucodeNative.zip'
    & (Join-Path $PSScriptRoot 'archive-package.ps1') -PackageRoot $package -Destination $destination -ManifestName 'integrity-native.sha256.json'
    if (-not (Test-Path -LiteralPath $destination)) { throw 'Native archive was not produced.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($destination)
    $entries = $archive.Entries | ForEach-Object { $_.FullName }
    $archive.Dispose()
    foreach ($required in @('app/Fufucode.App.exe', 'cli/Fufucode.Cli.exe', 'integrity-native.sha256.json')) {
        if ($entries -notcontains $required) { throw "Native archive is missing $required" }
    }

    # 3. 原生载荷边界：一旦夹带 Node/Pi 运行时目录，必须被显式拒绝。
    New-Item -ItemType Directory -Path (Join-Path $package 'runtime/node') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $package 'runtime/node/node.exe'), 'must never ship in the native channel')
    $rejected = $false
    try {
        foreach ($name in @('runtime', 'extensions', 'notices')) {
            if (Test-Path -LiteralPath (Join-Path $package $name)) { throw "Native package must not contain '$name'." }
        }
    }
    catch { $rejected = $_.Exception.Message -like '*must not contain*' }
    if (-not $rejected) { throw 'Bundled Node runtime was not rejected by the native guard.' }

    Write-Output 'PASS: native manifest name, archive contents, and Node-payload rejection'
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
