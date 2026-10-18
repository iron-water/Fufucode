param(
    [Parameter(Mandatory)][string]$CliPublishRoot,
    [string]$AppPublishRoot,
    [string]$PackageRoot
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliSource = (Resolve-Path -LiteralPath $CliPublishRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $cliSource 'Fufucode.Cli.exe'))) {
    throw 'CliPublishRoot must contain an already built Fufucode.Cli.exe. This script never builds source.'
}
if (-not $PackageRoot) {
    $PackageRoot = Join-Path $root ('tmp/cli-package-' + [Guid]::NewGuid().ToString('N') + ' space!')
}
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
if (Test-Path -LiteralPath $PackageRoot) { throw 'Use a new verification directory; existing output is never overwritten.' }
New-Item -ItemType Directory -Path (Join-Path $PackageRoot 'cli') -Force | Out-Null
Get-ChildItem -LiteralPath $cliSource | Copy-Item -Destination (Join-Path $PackageRoot 'cli') -Recurse
Copy-Item -LiteralPath (Join-Path $PackageRoot 'cli/Fufucode.Cli.exe') -Destination (Join-Path $PackageRoot 'cli/fufucode.exe')
if ($AppPublishRoot) {
    New-Item -ItemType Directory -Path (Join-Path $PackageRoot 'app') | Out-Null
    Get-ChildItem -LiteralPath $AppPublishRoot | Copy-Item -Destination (Join-Path $PackageRoot 'app') -Recurse
}

# Extract only the real package's three launcher declarations, without invoking
# publish, removing out/build, copying runtime, or touching the user's PATH.
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $root 'scripts/packaging/package.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$launchers = $ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -eq 'Set-Content' -and
    $node.Extent.Text -match "'Fufucode.cmd'|'cli/fufucode.cmd'|'Fufucode.ps1'"
}, $true)
if ($launchers.Count -ne 3) { throw 'Expected exactly three launcher declarations.' }
foreach ($launcher in $launchers) { & ([scriptblock]::Create($launcher.Extent.Text)) }

$dataRoot = Join-Path $PackageRoot 'data space!'
$project = Join-Path $PackageRoot 'project space!'
New-Item -ItemType Directory -Path $project | Out-Null
$previousLocalAppData = $env:LocalAppData
$env:LocalAppData = Join-Path $PackageRoot 'local-app-data'
$records = [Collections.Generic.List[object]]::new()
try {
    foreach ($entry in @('Fufucode.cmd', 'cli/fufucode.cmd', 'Fufucode.ps1', 'cli/fufucode.exe')) {
        $entryPath = Join-Path $PackageRoot $entry
        foreach ($check in @(
            @{ Name = 'settings'; Arguments = @('settings', 'get', 'theme', '--app-data', $dataRoot, '--json'); Code = 0 },
            @{ Name = 'sessions'; Arguments = @('sessions', 'list', '--project', $project, '--app-data', $dataRoot, '--json'); Code = 0 },
            @{ Name = 'usage-error'; Arguments = @('unknown-command', '--app-data', $dataRoot); Code = 2 },
            @{ Name = 'missing-runtime'; Arguments = @('models', 'list', '--project', $project, '--app-data', $dataRoot,
                '--pi-node', (Join-Path $PackageRoot 'missing-node.exe'), '--pi-cli', (Join-Path $PackageRoot 'missing-cli.js')); Code = 3 }
        )) {
            $stderrPath = Join-Path $PackageRoot ('stderr-' + $records.Count + '.txt')
            $arguments = $check.Arguments
            if ($entry.EndsWith('.ps1')) {
                $stdout = & (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -File $entryPath @arguments 2> $stderrPath
            } else {
                # PowerShell runs .cmd through cmd; DisableDelayedExpansion in
                # the shipped launchers preserves the exclamation marks above.
                $stdout = & $entryPath @arguments 2> $stderrPath
            }
            $code = $LASTEXITCODE
            $stderr = Get-Content -Raw -LiteralPath $stderrPath
            $record = [ordered]@{ entry = $entry; check = $check.Name; exitCode = $code; stdout = ($stdout -join "`n"); stderr = $stderr }
            $records.Add($record)
            if ($code -ne $check.Code) { throw ("Unexpected exit code: " + ($record | ConvertTo-Json -Compress)) }
            if ($check.Code -eq 0) { $null = $record.stdout | ConvertFrom-Json }
            if ($check.Code -ne 0 -and [string]::IsNullOrWhiteSpace($stderr)) { throw 'Expected stderr for failed command.' }
        }
    }
    if ($AppPublishRoot) {
        $stderrPath = Join-Path $PackageRoot 'stderr-gui-selftest.txt'
        $stdout = & (Join-Path $PackageRoot 'Fufucode.cmd') --selftest 2> $stderrPath
        $code = $LASTEXITCODE
        $records.Add([ordered]@{ entry = 'Fufucode.cmd'; check = 'gui-selftest'; exitCode = $code; stdout = ($stdout -join "`n"); stderr = (Get-Content -Raw -LiteralPath $stderrPath) })
        if ($code -ne 0) { throw "GUI selftest returned $code" }
    }
} finally {
    $env:LocalAppData = $previousLocalAppData
    $records | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PackageRoot 'launcher-report.json') -Encoding utf8
}
Write-Output "Launcher checks passed: $($records.Count). Artifact: $PackageRoot"
Write-Output 'Only existing binaries and launchers were checked; no publish, runtime dialogue, installer, or user PATH verification.'
