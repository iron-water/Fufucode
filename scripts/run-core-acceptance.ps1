$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$previousKey = $env:FUFUCODE_ATRIA_API_KEY
$previousFlag = $env:FUFUCODE_ATRIA_REAL_MODEL_TEST
try {
    if (-not $previousKey) {
        $env:FUFUCODE_ATRIA_API_KEY = [Environment]::GetEnvironmentVariable('FUFUCODE_ATRIA_API_KEY', 'User')
    }
    if ($env:FUFUCODE_ATRIA_API_KEY) { $env:FUFUCODE_ATRIA_REAL_MODEL_TEST = '1' }
    & dotnet run --project (Join-Path $root 'tests/Fufucode.Core.Tests') --no-build
    $result = $LASTEXITCODE
} finally {
    $env:FUFUCODE_ATRIA_API_KEY = $previousKey
    $env:FUFUCODE_ATRIA_REAL_MODEL_TEST = $previousFlag
}
exit $result
