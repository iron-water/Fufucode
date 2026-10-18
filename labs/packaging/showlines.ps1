$lines = Get-Content 'scripts/packaging/verify-clean-start.ps1'
for ($i = 54; $i -lt [Math]::Min(70, $lines.Length); $i++) {
    Write-Host ('{0,-4} {1}' -f ($i + 1), $lines[$i])
}