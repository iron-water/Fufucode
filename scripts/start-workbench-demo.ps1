param([switch]$Mock)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw 'Build first: dotnet build src/Fufucode.App/Fufucode.App.csproj'
}
$options = @{ FilePath = $exe; WorkingDirectory = $root; WindowStyle = 'Hidden' }
if ($Mock) { $options.ArgumentList = @('--mock') }
Start-Process @options
