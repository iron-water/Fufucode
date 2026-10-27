param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $root 'src/Fufucode.Plugins.LayerFs/Fufucode.Plugins.LayerFs.csproj'
$out = Join-Path $root 'out/plugins-dist/workspace.layerfs'
& dotnet build $project -c $Configuration -p:EnableEnhancedPreview=false -p:EnableDevTooling=false --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'LayerFS build failed.' }
New-Item -ItemType Directory -Force (Join-Path $out 'Bridge') | Out-Null
$built = Join-Path $root "src/Fufucode.Plugins.LayerFs/bin/$Configuration/net10.0"
Copy-Item (Join-Path $built 'Fufucode.Plugins.LayerFs.dll') $out -Force
Copy-Item (Join-Path $built 'manifest.json') $out -Force
Copy-Item (Join-Path $built 'Bridge/layerfs-bridge.mjs') (Join-Path $out 'Bridge') -Force
Copy-Item (Join-Path $root 'src/Fufucode.Plugins.LayerFs/README.md') $out -Force
Copy-Item (Join-Path $root 'src/Fufucode.Plugins.LayerFs/Runtime.Dockerfile') $out -Force
Copy-Item (Join-Path $root 'src/Fufucode.Plugins.LayerFs/install-wsl.sh') $out -Force
$zip = Join-Path $root 'out/plugins-dist/workspace.layerfs-1.0.0.zip'
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
Write-Host "LayerFS package: $zip"
Get-FileHash -Algorithm SHA256 $zip
