param([string]$AppVersion = '1.4.0', [switch]$SoftwareRendering)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$setup = Join-Path $root "out/Fufucode-$AppVersion-setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw 'Build the versioned installer first.' }
$stage = Join-Path $root ('tmp/sandbox-' + [Guid]::NewGuid().ToString('N'))
$payload = New-Item -ItemType Directory -Path (Join-Path $stage 'payload') -Force
$evidence = New-Item -ItemType Directory -Path (Join-Path $stage 'evidence') -Force
Copy-Item -LiteralPath $setup -Destination $payload.FullName
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sandbox-acceptance.ps1') -Destination $payload.FullName
$xml = [xml]'<Configuration><Networking>Disable</Networking><ClipboardRedirection>Disable</ClipboardRedirection><MappedFolders/><LogonCommand><Command/></LogonCommand></Configuration>'
if ($SoftwareRendering) {
    $gpu = $xml.CreateElement('vGPU')
    $gpu.InnerText = 'Disable'
    [void]$xml.DocumentElement.AppendChild($gpu)
}
foreach ($mapping in @(@($payload.FullName, 'C:\Payload', 'true'), @($evidence.FullName, 'C:\Evidence', 'false'))) {
    $folder = $xml.CreateElement('MappedFolder')
    foreach ($item in @(@('HostFolder', $mapping[0]), @('SandboxFolder', $mapping[1]), @('ReadOnly', $mapping[2]))) {
        $element = $xml.CreateElement($item[0]); $element.InnerText = $item[1]; [void]$folder.AppendChild($element)
    }
    [void]$xml.SelectSingleNode('/Configuration/MappedFolders').AppendChild($folder)
}
$xml.SelectSingleNode('/Configuration/LogonCommand/Command').InnerText = ''
$configuration = Join-Path $stage 'acceptance.wsb'
$xml.Save($configuration)
Write-Host "Sandbox evidence: $($evidence.FullName)"
$cli = Get-Command wsb.exe -ErrorAction SilentlyContinue
try {
if ($cli) {
    $id = [Guid]::NewGuid().ToString()
    & $cli.Source start --id $id --config $xml.OuterXml --raw
    if ($LASTEXITCODE -ne 0) { throw 'Windows Sandbox CLI start failed.' }
    & $cli.Source connect --id $id --raw
    $loginDeadline = [DateTime]::UtcNow.AddSeconds(90)
    $loggedIn = $false
    do {
        # Native stderr is a terminating error with Stop in Windows PowerShell 5.1.
        # A guest that is still logging in is an expected transient probe failure.
        $previousErrorAction = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'SilentlyContinue'
            $login = & $cli.Source exec --id $id --run-as ExistingLogin --command 'whoami.exe' --raw 2>$null
            if ($LASTEXITCODE -eq 0) {
                try { $loggedIn = ($login | ConvertFrom-Json).ExitCode -eq 0 } catch { }
            }
        } finally { $ErrorActionPreference = $previousErrorAction }
        if (-not $loggedIn) { Start-Sleep -Seconds 3 }
    } while (-not $loggedIn -and [DateTime]::UtcNow -lt $loginDeadline)
    if (-not $loggedIn) { & $cli.Source stop --id $id --raw; throw 'Sandbox interactive login did not become ready.' }
    # The clean guest blocks unsigned scripts; bypass applies only to this trusted verifier process.
    $execution = & $cli.Source exec --id $id --run-as ExistingLogin --command 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Payload\sandbox-acceptance.ps1' --raw
    $commandExit = $LASTEXITCODE
    $execution | Write-Host
    if ($commandExit -ne 0 -or ($execution | ConvertFrom-Json).ExitCode -ne 0) {
        & $cli.Source stop --id $id --raw
        throw "Windows Sandbox CLI execute failed; evidence=$($evidence.FullName)"
    }
} else {
    $xml.SelectSingleNode('/Configuration/LogonCommand/Command').InnerText = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Payload\sandbox-acceptance.ps1'
    $xml.Save($configuration)
    $sandbox = Start-Process -FilePath (Join-Path $env:WINDIR 'System32/WindowsSandbox.exe') -ArgumentList "`"$configuration`"" -WindowStyle Hidden -PassThru
}
$deadline = [DateTime]::UtcNow.AddMinutes(8)
$complete = Join-Path $evidence.FullName 'complete.txt'
    while ([DateTime]::UtcNow -lt $deadline -and -not (Test-Path -LiteralPath $complete)) {
        Start-Sleep -Seconds 2
        # The launcher can exit after handing the VM to WindowsSandboxServer.
    }
    if (-not (Test-Path -LiteralPath $complete)) { throw "Sandbox acceptance timed out; evidence=$($evidence.FullName)" }
    $result = (Get-Content -LiteralPath $complete -Raw).Trim()
    Write-Host "Sandbox acceptance: $result"
    if ($result -ne 'PASS') { throw "Sandbox acceptance failed; evidence=$($evidence.FullName)" }
} finally {
    if ($cli -and $id) { & $cli.Source stop --id $id --raw }
}
