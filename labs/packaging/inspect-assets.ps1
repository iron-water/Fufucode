$ErrorActionPreference = 'Stop'
$root = 'C:\Users\21894\.codex\worktrees\track-f-packaging\Fufucode'
$assets = Join-Path $root 'src\Fufucode.App\obj\project.assets.json'
$a = Get-Content -Raw $assets | ConvertFrom-Json
$a.psobject.properties | ForEach-Object { Write-Host "top: $($_.Name) ($($_.Value.GetType().Name))" }
$p = $a.packages
if ($null -eq $p) { Write-Host 'packages is null, trying libraries'; $p = $a.libraries }
if ($null -ne $p) {
    Write-Host ('type={0} count={1}' -f $p.GetType().Name, $p.Count)
    $first = $p.GetEnumerator() | Select-Object -First 1
    Write-Host ('first key={0} valtype={1}' -f $first.Key, $first.Value.GetType().Name)
    $first.Value.psobject.properties.Name | Select-Object -First 25 | ForEach-Object { Write-Host "  $_" }
}