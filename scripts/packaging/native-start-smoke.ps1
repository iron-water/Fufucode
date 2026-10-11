param([Parameter(Mandatory)][string]$PackageRoot)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$root = (Resolve-Path -LiteralPath $PackageRoot).Path
$evidence = Join-Path $repo ('tmp/native-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$watch = [Diagnostics.Stopwatch]::StartNew()
$app = $null
try {
    $app = Start-Process -FilePath (Join-Path $root 'app/Fufucode.App.exe') `
        -ArgumentList '--mock', '--data-root', "`"$evidence\data`"" -WorkingDirectory $evidence `
        -WindowStyle Hidden -PassThru
    do {
        Start-Sleep -Milliseconds 100
        $app.Refresh()
    } while (-not $app.HasExited -and $app.MainWindowHandle -eq 0 -and $watch.ElapsedMilliseconds -lt 30000)
    if ($app.HasExited -or $app.MainWindowHandle -eq 0) { throw 'Packaged native window did not appear.' }
    $createdMs = $watch.ElapsedMilliseconds
    $responding = $app.Responding
    $requestedClose = $app.CloseMainWindow()
    $graceful = $app.WaitForExit(15000)
    if (-not $requestedClose -or -not $graceful -or $app.ExitCode -ne 0) { throw 'Packaged native window did not close normally.' }
    $result = [pscustomobject]@{
        passed=$true; windowCreatedMs=$createdMs; responding=$responding; exitCode=$app.ExitCode
        scope='Local packaged mock native window lifecycle; not clean-machine or pointer/visual acceptance.'
    }
    $result | ConvertTo-Json | Set-Content (Join-Path $evidence 'result.json') -Encoding UTF8
    $result | ConvertTo-Json
    Write-Output "Evidence: $evidence"
} finally {
    if ($app) {
        if (-not $app.HasExited) { $app.Kill(); [void]$app.WaitForExit(5000) }
        $app.Dispose()
    }
}
