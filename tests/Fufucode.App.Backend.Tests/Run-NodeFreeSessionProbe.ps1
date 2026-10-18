param(
    [switch] $Live,
    [string] $BaseUrl = 'http://127.0.0.1:3425/v1',
    [string] $Model = 'devin/swe-2',
    [string] $ApiKey = 'local-probe-key',
    [string] $OutputPrefix
)

# Run the built managed harness directly: no dotnet-run/MSBuild/CLI subprocess is required.
$ErrorActionPreference = 'Stop'
$taskRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source
$taskDll = Join-Path $PSScriptRoot 'bin/Debug/net10.0/Fufucode.App.Backend.Tests.dll'
if (-not (Test-Path -LiteralPath $taskDll)) {
    throw 'Build Fufucode.App.Backend.Tests before running this probe.'
}
if ([string]::IsNullOrWhiteSpace($OutputPrefix)) {
    $OutputPrefix = Join-Path $taskRepoRoot ('tmp/cs3-nodefree-' + $(if ($Live) { 'live' } else { 'fixture' }))
}
$OutputPrefix = [IO.Path]::GetFullPath($OutputPrefix)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPrefix)) | Out-Null

$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $taskDotnet
$startInfo.WorkingDirectory = $taskRepoRoot
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.Environment['PATH'] = ''
$startInfo.Environment.Remove('NODE_PATH') | Out-Null
$startInfo.Environment.Remove('NODE_OPTIONS') | Out-Null
$startInfo.ArgumentList.Add($taskDll)
if ($Live) {
    $startInfo.ArgumentList.Add('--live-session')
    $startInfo.ArgumentList.Add($BaseUrl)
    $startInfo.ArgumentList.Add($Model)
    $startInfo.ArgumentList.Add($ApiKey)
}
else {
    $startInfo.ArgumentList.Add('--session-only')
}

$process = [Diagnostics.Process]::Start($startInfo)
$outputTask = $process.StandardOutput.ReadToEndAsync()
$errorTask = $process.StandardError.ReadToEndAsync()
$observed = [Collections.Generic.Dictionary[int, object]]::new()
$samples = 0
$timer = [Diagnostics.Stopwatch]::StartNew()
try {
    while (-not $process.HasExited) {
        # Sample this owned harness's descendants only. Do not stop unrelated processes.
        $parents = [Collections.Generic.Queue[int]]::new()
        $parents.Enqueue($process.Id)
        while ($parents.Count -gt 0) {
            $parentId = $parents.Dequeue()
            foreach ($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $parentId")) {
                $observed[[int]$child.ProcessId] = [pscustomobject]@{
                    processId = $child.ProcessId
                    parentId = $child.ParentProcessId
                    name = $child.Name
                    executablePath = $child.ExecutablePath
                }
                $parents.Enqueue([int]$child.ProcessId)
            }
        }
        $samples++
        if ($timer.Elapsed.TotalMinutes -gt 4) {
            $process.Kill($true)
            throw 'Owned probe exceeded its four-minute timeout.'
        }
        Start-Sleep -Milliseconds 150
    }
    $process.WaitForExit()
    $probeExitCode = $process.ExitCode
    $stdout = $outputTask.GetAwaiter().GetResult()
    $stderr = $errorTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText($OutputPrefix + '.stdout.log', $stdout, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($OutputPrefix + '.stderr.log', $stderr, [Text.UTF8Encoding]::new($false))
    $runtimeChildren = @($observed.Values | Where-Object { $_.name -match '^(node|python|python3|pi|omp|codex)(\.exe)?$' })
    $evidence = [ordered]@{
        mode = $(if ($Live) { 'live-session' } else { 'session-only' })
        model = $(if ($Live) { $Model } else { $null })
        executable = $taskDotnet
        harnessPid = $process.Id
        childPath = ''
        bundledNodeExists = (Test-Path -LiteralPath (Join-Path $taskRepoRoot 'runtime/node/node.exe'))
        sampledDescendants = @($observed.Values)
        sampleCount = $samples
        sampledAgentRuntimeCount = $runtimeChildren.Count
        exitCode = $probeExitCode
        elapsedMilliseconds = $timer.ElapsedMilliseconds
        boundary = 'PATH-empty managed session harness with sampled descendants; not an OS sandbox or a clean-installed full desktop acceptance.'
    }
    $json = $evidence | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($OutputPrefix + '.json', $json, [Text.UTF8Encoding]::new($false))
    Write-Output $stdout.TrimEnd()
    if ($stderr.Length -gt 0) { Write-Output $stderr.TrimEnd() }
    Write-Output $json
    if ($runtimeChildren.Count -gt 0) { throw 'Observed an unexpected agent-runtime child in the owned probe.' }
    exit $probeExitCode
}
finally {
    if (-not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    $process.Dispose()
}
