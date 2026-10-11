$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$root = Join-Path $repo ('tmp/notices-test-' + [Guid]::NewGuid().ToString('N'))
$cache = Join-Path $root 'cache'
$npm = Join-Path $root 'node_modules/sample'
New-Item -ItemType Directory -Force -Path (Join-Path $cache 'runtime/1.0'), $npm | Out-Null
Set-Content -LiteralPath (Join-Path $cache 'runtime/1.0/LICENSE.txt') -Value 'fixture license, not product licensing' -Encoding UTF8
Set-Content -LiteralPath (Join-Path $npm 'package.json') -Value '{"name":"sample","version":"1.0","license":"MIT"}' -Encoding UTF8
$assets = @{
    targets=@{'net10.0/win-x64'=@{
        'Runtime/1.0'=@{type='package';runtime=@{'lib/runtime.dll'=@{}}}
        'Missing/1.0'=@{type='package';runtime=@{'lib/missing.dll'=@{}}}
        'BuildOnly/1.0'=@{type='package';build=@{'build/only.targets'=@{}}}
        'OtherPlatform/1.0'=@{type='package';runtimeTargets=@{'runtimes/linux-x64/native/other.so'=@{rid='linux-x64'}}}
    }}
    libraries=@{
        'Runtime/1.0'=@{type='package';path='runtime/1.0';files=@('LICENSE.txt','lib/runtime.dll')}
        'Missing/1.0'=@{type='package';path='missing/1.0';files=@('lib/missing.dll')}
        'BuildOnly/1.0'=@{type='package';path='buildonly/1.0';files=@('LICENSE.txt')}
        'OtherPlatform/1.0'=@{type='package';path='otherplatform/1.0';files=@('LICENSE.txt')}
    }
    packageFolders=@{$cache=@{}}
}
$assetsFile = Join-Path $root 'assets.json'
$assets | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $assetsFile -Encoding UTF8
$destination = Join-Path $root 'collected'
& (Join-Path $PSScriptRoot 'collect-notices.ps1') -Destination $destination -AssetsFiles @($assetsFile,$assetsFile) -PiNodeModules (Split-Path $npm -Parent)
$report = Get-Content -LiteralPath (Join-Path $destination 'report.json') -Raw | ConvertFrom-Json
if ($report.nugetPackages -ne 2 -or $report.copiedTexts -ne 1 -or $report.npmPackages -ne 1 -or $report.missingTextPackages -ne 2) { throw 'Dependency selection or missing-text accounting failed.' }
$sourceHash = (Get-FileHash -LiteralPath (Join-Path $cache 'runtime/1.0/LICENSE.txt')).Hash
$targetHash = (Get-FileHash -LiteralPath (Join-Path $destination 'nuget/Runtime/1.0/LICENSE.txt')).Hash
if ($sourceHash -ne $targetHash) { throw 'License bytes changed.' }
if (Test-Path -LiteralPath (Join-Path $destination 'nuget/Missing/1.0')) { throw 'Missing license was fabricated.' }
if (Test-Path -LiteralPath (Join-Path $destination 'npm')) { throw 'Already bundled npm tree copied twice.' }
$assets.libraries['Runtime/1.0'].files = @('../LICENSE.txt')
$assets | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $assetsFile -Encoding UTF8
$rejected = $false
try { & (Join-Path $PSScriptRoot 'collect-notices.ps1') -Destination (Join-Path $root 'rejected') -AssetsFiles @($assetsFile) -PiNodeModules (Split-Path $npm -Parent) } catch { $rejected = $_.Exception.Message -match 'escapes' }
if (-not $rejected) { throw 'Escaping package path accepted.' }
$cacheWriteRejected = $false
try { & (Join-Path $PSScriptRoot 'collect-notices.ps1') -Destination (Join-Path $cache 'forbidden-output') -AssetsFiles @($assetsFile) -PiNodeModules (Split-Path $npm -Parent) } catch { $cacheWriteRejected = $_.Exception.Message -match 'read-only' }
if (-not $cacheWriteRejected -or (Test-Path -LiteralPath (Join-Path $cache 'forbidden-output'))) { throw 'Cache output was not rejected before writing.' }
Write-Output 'PASS: runtime-only selection, deduplication, exact bytes, missing notices, npm no-copy, escaping-path and source-write rejection'
Write-Output "Evidence: $root"
