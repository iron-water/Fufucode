<#
Runs the solution's executable regression suites without live model probes.
Requires Windows, .NET 10 and Git. Logs include explicit skipped probe projects.
Examples: pwsh scripts/test-offline.ps1
          pwsh scripts/test-offline.ps1 -NoBuild -Suite Fufucode.Security.Tests
#>
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string[]]$Suite = @(),
    [switch]$NoBuild,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 300,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (!$IsWindows) { throw 'The process fixtures currently require Windows apphosts.' }
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $root ('tmp/offline-tests-' + [guid]::NewGuid().ToString('N'))
}
$output = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)).FullName
$fixtures = [IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ('fufu-' + [guid]::NewGuid().ToString('N').Substring(0, 8)))).FullName
$projects = [regex]::Matches((Get-Content (Join-Path $root 'Fufucode.sln') -Raw),
    '"(tests\\[^"\r\n]+\.csproj)"') | ForEach-Object { Join-Path $root $_.Groups[1].Value }
$excluded = @{
    'Fufucode.FakePi' = 'Child-process fixture, not a test entrypoint.'
    'Fufucode.FakeClaude' = 'Child-process fixture, not a test entrypoint.'
    'Fufucode.App.Graph.Integration.Tests' = 'Live model/project integration requires explicit opt-in.'
}
$known = @($projects | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) })
foreach ($name in $Suite) {
    if ($name -notin $known -or $excluded.ContainsKey($name)) { throw "Not an offline suite: $name" }
}
if (!$NoBuild) {
    & dotnet build (Join-Path $root 'Fufucode.sln') -c $Configuration --nologo -m:1 *> (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Build failed; see $output/build.log" }
}

$results = [System.Collections.Generic.List[object]]::new()
foreach ($project in $projects) {
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    if ($Suite.Count -gt 0 -and $name -notin $Suite) { continue }
    if ($excluded.ContainsKey($name)) {
        $results.Add([pscustomobject]@{ Suite = $name; Status = 'Skipped'; Reason = $excluded[$name] })
        continue
    }
    [xml]$definition = Get-Content -LiteralPath $project -Raw
    $assembly = @($definition.Project.PropertyGroup.AssemblyName | Where-Object { $_ }) | Select-Object -First 1
    if (!$assembly) { $assembly = $name }
    $exe = Join-Path (Split-Path $project) "bin/$Configuration/net10.0/$assembly.exe"
    $log = Join-Path $output "$name.log"
    $status = 'Failed'
    $exitCode = $null
    $process = $null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (!(Test-Path -LiteralPath $exe)) { throw "Missing apphost: $exe; build before using -NoBuild." }
        $start = [Diagnostics.ProcessStartInfo]::new($exe)
        $start.WorkingDirectory = $root
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.Environment['FUFUCODE_DATA_ROOT'] = Join-Path $output "data/$name"
        $start.Environment['FUFUCODE_TEST_ROOT'] = $fixtures
        $start.Environment['FUFUCODE_GIT_TEST_ROOT'] = $fixtures
        $start.Environment['FUFUCODE_SKIP_UI_GROUP'] = '1'
        foreach ($key in @('FUFUCODE_REAL_MODEL_TEST', 'FUFUCODE_REAL_PERSISTENCE_MODEL_TEST', 'FUFUCODE_TEST_SHUSHENG_API_KEY')) {
            $start.Environment.Remove($key) | Out-Null
        }
        if ($name -in @('Fufucode.Core.Tests', 'Fufucode.Pi.Tests')) { $start.ArgumentList.Add('--offline') }
        if ($name -eq 'Fufucode.NativeCore.Tests') { $start.ArgumentList.Add('--all') }
        if ($name -eq 'Fufucode.ManagedWorkspace.Tests') { $start.ArgumentList.Add($fixtures) }
        $process = [Diagnostics.Process]::Start($start)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $finished = $process.WaitForExit($TimeoutSeconds * 1000)
        if (!$finished) { $process.Kill($true); $process.WaitForExit(); $status = 'TimedOut' }
        $exitCode = $process.ExitCode
        [IO.File]::WriteAllText($log, $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
        if ($finished -and $exitCode -eq 0) { $status = 'Passed' }
    } catch {
        $_ | Out-String | Add-Content -LiteralPath $log
    } finally {
        if ($process) { $process.Dispose() }
    }
    $results.Add([pscustomobject]@{
        Suite = $name; Status = $status; ExitCode = $exitCode
        Seconds = [math]::Round($timer.Elapsed.TotalSeconds, 2); Log = $log; FixtureRoot = $fixtures
        SkippedChecks = @(Select-String -LiteralPath $log -Pattern '^\s*SKIP' | ForEach-Object { $_.Line })
    })
    Write-Host "$name : $status (exit $exitCode)"
    ConvertTo-Json -InputObject @($results.ToArray()) -Depth 5 | Set-Content (Join-Path $output 'results.json')
}
ConvertTo-Json -InputObject @($results.ToArray()) -Depth 5 | Set-Content (Join-Path $output 'results.json')
$results | Group-Object Status | Format-Table Name, Count
Write-Host "Evidence: $output"
if (@($results | Where-Object { $_.Status -in @('Failed', 'TimedOut') }).Count -gt 0) { exit 1 }
