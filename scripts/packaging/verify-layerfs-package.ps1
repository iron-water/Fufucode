param([string]$Package)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Package))
try {
    $names = @($archive.Entries | ForEach-Object FullName)
    foreach ($required in @('manifest.json','Fufucode.Plugins.LayerFs.dll','Bridge/layerfs-bridge.mjs')) {
        if ($required -notin $names) { throw "Missing package entry: $required" }
    }
    if (@($names | Where-Object { $_ -match '(^|/)(Fufucode.App|Fufucode.Core|Avalonia).*\.dll$' }).Count) {
        throw 'Plugin package contains host assemblies.'
    }
    Write-Host 'PASS physical manifest, bridge, plugin DLL and no host/runtime payload'
} finally { $archive.Dispose() }
