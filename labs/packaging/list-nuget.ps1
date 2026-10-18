$ErrorActionPreference = 'Stop'
$root = 'C:\Users\21894\.codex\worktrees\track-f-packaging\Fufucode'
$assets = Join-Path $root 'src\Fufucode.App\obj\project.assets.json'
$a = Get-Content -Raw $assets | ConvertFrom-Json
$a.libraries.psobject.properties | ForEach-Object {
    '{0} :: {1}' -f $_.Name, $_.Value.version
} | Sort-Object