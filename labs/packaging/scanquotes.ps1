$i = 0
Get-Content 'scripts/packaging\verify-clean-start.ps1' | ForEach-Object {
    $i++
    $q = ($_ -replace '\\\\\"', '').Count('\"')
    if ($q % 2 -eq 1) {
        Write-Host ('line {0}: odd quotes ({1}) : {2}' -f $i, $q, $_.Substring(0, [Math]::Min(120, $_.Length)))
    }
}
Write-Host 'scan done'