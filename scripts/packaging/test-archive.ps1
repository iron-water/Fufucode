$ErrorActionPreference = 'Stop'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('fufucode-archive-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $temporary 'package'
New-Item -ItemType Directory -Path $package -Force | Out-Null
$file = Join-Path $package 'symbols.pdb'
[IO.File]::WriteAllText($file, 'fixture symbols')
& (Join-Path $PSScriptRoot 'integrity.ps1') -Mode Write -PackageRoot $package
$destination = Join-Path $temporary 'package.zip'
$writer = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::Read)
try { & (Join-Path $PSScriptRoot 'archive-package.ps1') -PackageRoot $package -Destination $destination }
finally { $writer.Dispose() }
$before = (Get-FileHash -LiteralPath $destination).Hash
[IO.File]::AppendAllText($file, 'changed')
$rejected = $false
try { & (Join-Path $PSScriptRoot 'archive-package.ps1') -PackageRoot $package -Destination $destination }
catch { $rejected = $_.Exception.Message -like '*hash mismatch*' }
if (-not $rejected -or (Get-FileHash -LiteralPath $destination).Hash -ne $before) {
    throw 'Hash mismatch must reject output and preserve the previous archive.'
}
if (Get-ChildItem -LiteralPath $temporary -Filter '*.partial') { throw 'Partial archive was not cleaned.' }
Write-Output 'PASS: shared-reader archive; changed source rejected; previous archive preserved; partial cleaned'
