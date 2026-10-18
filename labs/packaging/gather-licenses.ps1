$ErrorActionPreference = 'Stop'
$root = 'C:\Users\21894\.codex\worktrees\track-f-packaging\Fufucode'
$nu = Join-Path $Env:USERPROFILE '.nuget\packages'
$pkgs = @('avalonia', 'avalonia.desktop', 'avalonia.themes.fluent', 'avalonia.fonts.inter', 'avalonia.headless', 'communitytoolkit.mvvm', 'avaloniaui.diagnosticssupport', 'avalonia.skia', 'harfbuzzsharp', 'sixlabors.imagesharp', 'sqlitepclraw.bundle_green', 'serilog', 'system.text.json', 'runtime.win-x64.microsoft.netcore.app.runtime')
foreach ($pkg in $pkgs) {
    $pd = Join-Path $nu $pkg
    if (-not (Test-Path $pd)) { "$pkg :: not in nuget cache"; continue }
    $vers = Get-ChildItem $pd -Directory | Select-Object -ExpandProperty Name
    $v = $vers | Select-Object -First 1
    $nuspec = Join-Path $pd $v "$pkg.$v.nuspec"
    if (-not (Test-Path $nuspec)) { "$pkg :: nuspec not found ($vers - $nuspec)"; continue }
    try {
        [xml]$x = Get-Content -LiteralPath $nuspec
        $md = $x.package.metadata
        $lic = $md.license
        if (-not $lic -and $x.package.metadata.licenses) { $lic = $x.package.metadata.licenses }
        "$pkg :: version=$v title=$($md.title) license=$lic"
    } catch {
        "$pkg :: parse error: $_"
    }
}
Write-Host '--- .NET SDK runtime license (shipped by dotnet publish --self-contained) ---'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) { "dotnet path: $($dotnet.Source)" } else { 'dotnet not found' }
Write-Host '--- node license ---'
$nodePkg = Join-Path $root 'out\build\runtime\node'
if (Test-Path (Join-Path $root 'runtime\node\LICENSE')) { 'runtime/node/LICENSE present' } else { 'runtime/node/LICENSE absent (node binary copied from dev machine; license not shipped)' }