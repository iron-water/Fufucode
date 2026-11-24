<# Publish the standalone terminal host only. No GUI, CLI, installer or optional backend is built or updated. #>
[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = 'win-x64',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$ArtifactsPath,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root "out/fui/$RuntimeIdentifier" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$publishArgs = @('publish', (Join-Path $root 'src/Fufucode.Fui/Fufucode.Fui.csproj'),
    '-c', $Configuration, '-r', $RuntimeIdentifier, '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=false',
    '-o', $output, '--nologo', '-warnaserror')
if ($ArtifactsPath) { $publishArgs += @('--artifacts-path', $ArtifactsPath) }
if ($NoRestore) { $publishArgs += '--no-restore' }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'FUI publish failed.' }
$name = if ($RuntimeIdentifier -like 'win-*') { 'fui.exe' } else { 'fui' }
$exe = Get-Item -LiteralPath (Join-Path $output $name) -ErrorAction Stop
Write-Host "FUI: $($exe.FullName)"
