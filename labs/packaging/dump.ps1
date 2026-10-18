$bytes = [System.IO.File]::ReadAllBytes('scripts/packaging/verify-clean-start.ps1')
$off = 2100
$len = 500
Write-Host ('--- bytes {0}..{1} ---' -f $off, ($off + $len))
for ($i = $off; $i -lt [Math]::Min($off + $len, $bytes.Length); $i++) {
    $b = $bytes[$i]
    if ($b -eq 10) { Write-Host '<LF>' }
    elseif ($b -eq 13) { Write-Host '<CR>' }
    elseif ($b -ge 32 -and $b -le 126) { Write-Host -NoNewline ([char]$b) }
    else { Write-Host ('<{0:X2}>' -f $b) }
}
Write-Host