param([string]$Build = 'tmp/production-a-native-build')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$fixture = Get-ChildItem (Join-Path $root 'tmp/project-fork') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'acceptance.json') } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $fixture) { throw 'Run Core.Tests --worktree-only to prepare private acceptance data.' }
$data = Get-Content (Join-Path $fixture.FullName 'acceptance.json') -Raw | ConvertFrom-Json
$app = Join-Path $root "$Build/Fufucode.App.exe"
$arguments = @('--project', "`"$($data.project)`"", '--session', $data.sessionId,
    '--data-root', "`"$($data.dataRoot)`"")
$process = Start-Process -FilePath $app -ArgumentList $arguments -WindowStyle Hidden -PassThru
[pscustomobject]@{pid=$process.Id;project=$data.project;dataRoot=$data.dataRoot;sessionId=$data.sessionId} | ConvertTo-Json
