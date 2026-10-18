$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stage = Join-Path $root ('tmp/cli-gui-lock-' + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $stage 'project'
$null = New-Item -ItemType Directory -Path $project -Force
$app = Join-Path $root 'src/Fufucode.App/bin/Debug/net10.0/Fufucode.App.exe'
$cli = Join-Path $root 'src/Fufucode.Cli/bin/Debug/net10.0/Fufucode.Cli.exe'
$stdout = Join-Path $stage 'gui.stdout.txt'
$stderr = Join-Path $stage 'gui.stderr.txt'
# Product GUI does not expose an app-data override. Use an isolated project in
# the normal metadata store, then delete only its test session after closing it.
$gui = Start-Process -FilePath $app -ArgumentList @('--project', ('"' + $project + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
$sessionId = $null
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        Start-Sleep -Milliseconds 500
        $listing = & $cli --project $project --json sessions list
        if ($LASTEXITCODE -ne 0) { throw 'Session listing failed.' }
        $rows = @($listing | ConvertFrom-Json)
        if ($rows.Count -gt 0) { $sessionId = $rows[0].id }
        $gui.Refresh()
        if ($gui.HasExited) { throw ('GUI exited before session opened: ' + (Get-Content -LiteralPath $stderr -Raw)) }
    } until ($sessionId -or [DateTime]::UtcNow -gt $deadline)
    if (-not $sessionId) { throw 'GUI did not register a test session.' }
    $conflictError = Join-Path $stage 'cli.stderr.txt'
    $result = & $cli --project $project --session $sessionId --json sessions show $sessionId 2> $conflictError
    $code = $LASTEXITCODE
    $message = Get-Content -LiteralPath $conflictError -Raw
    $gui.Refresh()
    $record = [ordered]@{
        guiCommand = @($app, '--project', $project); guiPid = $gui.Id; guiRunning = -not $gui.HasExited
        guiWindowHandle = $gui.MainWindowHandle.ToInt64(); sessionId = $sessionId
        cliCommand = @($cli, '--project', $project, '--session', $sessionId, '--json', 'sessions', 'show', $sessionId)
        exitCode = $code; stdout = ($result -join "`n"); stderr = $message.Trim()
    }
    $record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'report.json')
    $record | ConvertTo-Json -Depth 5
    # CLI reports GetBaseException(): the Windows sharing-violation text, not
    # SessionLease's outer explanatory exception. Require the actual lock path.
    if ($code -ne 3 -or $message -notlike '*.locks*' -or $message -notlike '*being used by another process*') { throw 'Expected the GUI session lease to reject CLI resume.' }
} finally {
    $gui.Refresh()
    if (-not $gui.HasExited) {
        $null = $gui.CloseMainWindow()
        if (-not $gui.WaitForExit(5000)) { Stop-Process -Id $gui.Id }
    }
    if ($sessionId) {
        & $cli --project $project --json sessions delete $sessionId | Set-Content -LiteralPath (Join-Path $stage 'cleanup.json')
        if ($LASTEXITCODE -ne 0) { Write-Warning 'Test session cleanup failed; see the report.' }
    }
}
