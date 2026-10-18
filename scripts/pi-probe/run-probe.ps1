# Pi backend plugin diagnostic — not part of the default native package.
param(
    [Parameter(Mandatory = $true)][string]$PiRoot,
    [string]$Project,
    [string]$Output,
    [string]$ComparePiRoot,
    [string]$BaselineReport,
    [string]$NodeExe,
    [string]$FakePi,
    [switch]$GenerateFixture,
    [switch]$SelfTest,
    [switch]$Features
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$NodeExe) { $NodeExe = Join-Path $repo 'runtime/node/node.exe' }
$probeArgs = @((Join-Path $PSScriptRoot 'probe.mjs'), '--pi-root', $PiRoot)
foreach ($pair in @(@('--project', $Project), @('--output', $Output), @('--compare-pi-root', $ComparePiRoot), @('--baseline-report', $BaselineReport), @('--fake-pi', $FakePi))) {
    if ($pair[1]) { $probeArgs += $pair }
}
if ($GenerateFixture) { $probeArgs += '--generate-fixture' }
if ($SelfTest) { $probeArgs += '--self-test' }
if ($Features) { $probeArgs += '--features' }
& $NodeExe @probeArgs
exit $LASTEXITCODE
