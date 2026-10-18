param([Parameter(Mandatory)][string]$PackageRoot, [Parameter(Mandatory)][string]$Destination,
  # 与 integrity.ps1 对齐的双渠道清单名；默认仍是全量渠道清单。
  [string]$ManifestName = 'integrity.sha256.json')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ManifestName) -or $ManifestName -match '[\\/:*?"<>|]') { throw "Unsupported manifest name: $ManifestName" }
Add-Type -AssemblyName System.IO.Compression
$root = (Resolve-Path -LiteralPath $PackageRoot).Path
$destinationPath = [IO.Path]::GetFullPath($Destination)
if ($destinationPath.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Archive destination must be outside the package tree.'
}
$manifestPath = Join-Path $root $ManifestName
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifest.format -ne 1) { throw 'Unsupported integrity manifest.' }
$expected = @{}
foreach ($item in $manifest.files) {
    $relative = [string]$item.path
    if ($relative -match '(^/|^[A-Za-z]:|(^|/)\.\.(/|$)|\\|:)' -or [string]::IsNullOrWhiteSpace($relative) -or $expected.ContainsKey($relative)) {
        throw "Unsafe or duplicate manifest entry: $relative"
    }
    $expected[$relative] = $item.sha256
}
$temporary = $destinationPath + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
$output = $null; $archive = $null
$copied = 0
try {
    $output = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $archive = New-Object IO.Compression.ZipArchive($output, [IO.Compression.ZipArchiveMode]::Create, $true)
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Package link rejected: $($file.FullName)" }
        $relative = $file.FullName.Substring($root.TrimEnd('\').Length + 1).Replace('\', '/')
        if ($relative -ne $ManifestName -and -not $expected.ContainsKey($relative)) { throw "Unmanifested file: $relative" }
        $input = $null; $entryStream = $null; $hasher = $null
        try {
            # Readers such as symbol indexing need not prevent a verified archive.
            $input = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $entryStream = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal).Open()
            $hasher = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            $buffer = New-Object byte[] 65536
            while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $entryStream.Write($buffer, 0, $read)
                $hasher.AppendData($buffer, 0, $read)
            }
            $hash = [BitConverter]::ToString($hasher.GetHashAndReset()).Replace('-', '').ToLowerInvariant()
            if ($relative -eq $ManifestName -and $hash -ne $manifestHash) { throw 'Integrity manifest changed during archiving.' }
            if ($relative -ne $ManifestName) {
                if ($hash -ne $expected[$relative]) { throw "Source changed or hash mismatch: $relative" }
                $copied++
            }
        } finally {
            if ($hasher) { $hasher.Dispose() }
            if ($entryStream) { $entryStream.Dispose() }
            if ($input) { $input.Dispose() }
        }
    }
    if ($copied -ne $expected.Count) { throw 'Missing manifested files.' }
    $archive.Dispose(); $archive = $null
    $output.Dispose(); $output = $null
    Move-Item -LiteralPath $temporary -Destination $destinationPath -Force
    Write-Output "PASS archive: $copied file hashes verified; $destinationPath"
} finally {
    if ($archive) { $archive.Dispose() }
    if ($output) { $output.Dispose() }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
