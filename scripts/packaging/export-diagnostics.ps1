param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][string]$OutputZip,
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'Fufucode')
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageRoot).Path
$out = [IO.Path]::GetFullPath($OutputZip)
$stage = Join-Path ([IO.Path]::GetTempPath()) ('fufucode-diagnostics-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $integrity = & (Join-Path $PSScriptRoot 'integrity.ps1') -Mode Verify -PackageRoot $package 2>&1 | Out-String
    $integrityExit = $LASTEXITCODE
    if ($integrityExit -ne 0) {
        # Malformed or absent manifests are recorded without copying exception text or paths.
        $integrity = '{"ok":false,"error":"integrity check failed"}'
    }
    $integrity | Set-Content -LiteralPath (Join-Path $stage 'integrity.json') -Encoding utf8

    $manifest = Join-Path $package 'runtime/pi/package.json'
    $piVersion = $null
    if (Test-Path -LiteralPath $manifest) {
        $piVersion = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).dependencies.'@earendil-works/pi-coding-agent'
    }
    $appExe = Join-Path $package 'app/Fufucode.App.exe'
    $appVersion = if (Test-Path -LiteralPath $appExe) { (Get-Item -LiteralPath $appExe).VersionInfo.ProductVersion } else { $null }
    $nodeExe = Join-Path $package 'runtime/node/node.exe'
    $nodeVersion = if (Test-Path -LiteralPath $nodeExe) { (Get-Item -LiteralPath $nodeExe).VersionInfo.ProductVersion } else { $null }
    [pscustomobject]@{
        format = 1; createdUtc = (Get-Date).ToUniversalTime().ToString('o')
        appVersion = $appVersion; nodeVersion = $nodeVersion; piPackageVersion = $piVersion
        integrityPassed = ($integrityExit -eq 0)
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'versions.json') -Encoding utf8

    # Only fixed event names and severity are copied. Free text, paths, prompts,
    # environment variables and credentials are never placed in the archive.
    $logRoot = Join-Path $DataRoot 'logs'
    $events = [collections.generic.list[object]]::new()
    if (Test-Path -LiteralPath $logRoot -PathType Container) {
        Get-ChildItem -LiteralPath $logRoot -File -Filter '*.log' | Select-Object -First 8 | ForEach-Object {
            Get-Content -LiteralPath $_.FullName -Tail 200 | ForEach-Object {
                if ($_ -match '(?i)\b(error|warning|info)\b') {
                    $severity = $Matches[1].ToLowerInvariant()
                    if ($_ -match '(?i)\b(start|stop|exit|timeout|rpc|selftest|integrity)\b') {
                        $events.Add([pscustomobject]@{severity=$severity; event=$Matches[1].ToLowerInvariant()})
                    }
                }
            }
        }
    }
    $events | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $stage 'log-events.json') -Encoding utf8
    if (Test-Path -LiteralPath $out) { throw "Output already exists: $out" }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $out
    Write-Output $out
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
