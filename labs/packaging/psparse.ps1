$path = 'scripts\packaging\verify-clean-start.ps1'
$source = [System.IO.File]::ReadAllText($path)
$tokens = @()
$errors = @()
try {
    $parsed = [System.Management.Automation.PSParser]::Tokenize($source, [ref]$tokens)
    Write-Host ('tokenized OK, {0} tokens' -f $parsed.Count)
    $unterminated = $tokens | Where-Object { $_.Type -eq 'String' -and $_.Content -notmatch '^\s*$' } | Select-Object -Last 5
    Write-Host 'last string tokens:'
    $unterminated | ForEach-Object { Write-Host ('  type={0} start={1} content={2}' -f $_.Type, $_.Start, $_.Content.Substring(0, [Math]::Min(40, $_.Content.Length))) }
} catch {
    Write-Host ('ERROR: {0}' -f $_.Exception.Message)
}