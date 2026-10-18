$bytes = [System.IO.File]::ReadAllBytes('scripts/packaging/verify-clean-start.ps1')
$off = 1680
$end = [Math]::Min(2130, $bytes.Length)
Write-Host ('--- bytes {0}..{1} ---' -f $off, $end)
for ($i = $off; $i -lt $end; $i++) {
    $b = $bytes[$i]
    if ($b -eq 10) { Write-Host '<LF>' }
    elseif ($b -eq 13) { Write-Host -NoNewline '<CR>' }
    elseif ($b -ge 32 -and $b -le 126) { Write-Host -NoNewline ([char]$b) }
    else { Write-Host -NoNewline ('<{0:X2}>' -f $b) }
}
Write-Host