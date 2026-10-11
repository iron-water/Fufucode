#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'verify-clean-start.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Verifier parse failed.' }
foreach ($name in @('Find-PiResponse', 'Test-PiStateResponse')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    Invoke-Expression $definition.Extent.Text
}
$lines = [Collections.Concurrent.ConcurrentQueue[string]]::new()
$lines.Enqueue('not-json')
$lines.Enqueue('{"id":"other","type":"response","success":true,"command":"get_state"}')
$lines.Enqueue('{"id":"requested","type":"response","success":false,"command":"get_state"}')
$hit = Find-PiResponse @{lines=$lines} 'requested'
if ($null -eq $hit -or (Test-PiStateResponse $hit)) { throw 'Failed RPC was accepted or missed.' }
foreach ($case in @(
    '{"type":"response","success":false,"command":"get_state"}',
    '{"type":"event","success":true,"command":"get_state"}',
    '{"type":"response","success":true,"command":"get_entries"}',
    '{"type":"response","command":"get_state"}')) {
    if (Test-PiStateResponse ($case | ConvertFrom-Json)) { throw 'Invalid state response accepted.' }
}
if (-not (Test-PiStateResponse ('{"type":"response","success":true,"command":"get_state"}' | ConvertFrom-Json))) { throw 'Successful state rejected.' }
Write-Output 'PASS: failed/malformed/wrong-command responses rejected; valid get_state accepted'
