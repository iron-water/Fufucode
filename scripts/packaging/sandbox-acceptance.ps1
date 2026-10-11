param([string]$Payload = 'C:\Payload', [string]$Evidence = 'C:\Evidence')
$ErrorActionPreference = 'Stop'
$results = [Collections.Generic.List[object]]::new()
$install = Join-Path $env:LOCALAPPDATA 'Programs\Fufucode'
$app = $null
$pi = $null
function Record([string]$name, [bool]$passed, [string]$detail) {
    $results.Add([pscustomobject]@{name=$name;passed=$passed;detail=$detail})
    $results | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Evidence 'results.json') -Encoding UTF8
    if (-not $passed) { throw "$name failed: $detail" }
}
try {
    $model = (Get-CimInstance Win32_ComputerSystem).Model
    [pscustomobject]@{user=$env:USERNAME;model=$model;os=[Environment]::OSVersion.VersionString} |
        ConvertTo-Json | Set-Content (Join-Path $Evidence 'sandbox-info.json') -Encoding UTF8
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($env:USERNAME -ne 'WDAGUtilityAccount' -and
        -not ($identity.User.Value -eq 'S-1-5-18' -and $model -eq 'Virtual Machine' -and $Payload -eq 'C:\Payload')) {
        throw 'Run this destructive installer drill only inside a disposable Windows Sandbox VM.'
    }
    Record 'clean-image' (-not (Test-Path $install)) 'No previous install; disposable Windows Sandbox account.'
    $setup = Get-ChildItem $Payload -Filter '*-setup.exe' | Select-Object -First 1
    if (-not $setup) { throw 'Installer missing.' }
    $process = Start-Process $setup.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$Evidence\install.log`"" -WindowStyle Hidden -PassThru -Wait
    Record 'install' ($process.ExitCode -eq 0 -and (Test-Path "$install\app\Fufucode.App.exe")) "exit=$($process.ExitCode)"
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Fufucode.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    Record 'silent-start-menu-shortcut' ((Test-Path $shortcutPath) -and $shortcut.TargetPath -eq "$install\Fufucode.cmd" -and $shortcut.WorkingDirectory -eq [Environment]::GetFolderPath('MyDocuments')) 'Installed link points to bundled launcher and user Documents.'
    $protocolKey = 'HKCU:\Software\Classes\fufucode\shell\open\command'
    $protocol = (Get-Item $protocolKey).GetValue('')
    Record 'session-protocol-registration' ($protocol -eq "`"$install\app\Fufucode.App.exe`" `"%1`"") 'User protocol points to installed App without runtime override arguments.'
    $manifest = Get-Content "$install\integrity.sha256.json" -Raw | ConvertFrom-Json
    $changed = @($manifest.files | Where-Object {
        $file = Join-Path $install $_.path
        -not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash -ne $_.sha256
    })
    Record 'integrity' ($changed.Count -eq 0) "checked=$($manifest.files.Count);changed=$($changed.Count)"
    $app = Start-Process "$install\app\Fufucode.App.exe" -ArgumentList '--mock' -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do { Start-Sleep -Milliseconds 250; $app.Refresh() } while (-not $app.HasExited -and $app.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    Record 'gui-start' (-not $app.HasExited -and $app.MainWindowHandle -ne 0) 'Native top-level window created from installed self-contained executable.'
    [void]$app.CloseMainWindow()
    Record 'gui-exit' ($app.WaitForExit(15000)) 'Closed installed window.'
    $data = Join-Path $env:LOCALAPPDATA 'Fufucode'
    New-Item -ItemType Directory -Force "$data\pi-agent", "$data\project" | Out-Null
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = "$install\runtime\node\node.exe"
    $info.Arguments = "`"$install\runtime\pi\node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js`" --mode rpc --no-session --no-extensions"
    $info.WorkingDirectory = "$data\project"
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    [Console]::InputEncoding = New-Object Text.UTF8Encoding($false)
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables['PI_CODING_AGENT_DIR'] = "$data\pi-agent"
    $pi = [Diagnostics.Process]::Start($info)
    $errors = $pi.StandardError.ReadToEndAsync()
    $pi.StandardInput.WriteLine('{"id":"sandbox-state","type":"get_state"}')
    $pi.StandardInput.Flush()
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $response = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = $pi.StandardOutput.ReadLineAsync()
        if (-not $line.Wait(60000)) { throw 'Pi response timeout.' }
        if ($null -eq $line.Result) { break }
        $message = $line.Result | ConvertFrom-Json
        if ($message.id -eq 'sandbox-state') { $response = $message; break }
    }
    Record 'bundled-pi-state' ($null -ne $response -and $response.type -eq 'response' -and $response.command -eq 'get_state' -and $response.success -eq $true) 'Official bundled Pi get_state response; no global Node/Pi or development checkout.'
    $pi.StandardInput.Close()
    $graceful = $pi.WaitForExit(5000)
    if (-not $graceful) { $pi.Kill(); [void]$pi.WaitForExit(5000) }
    Record 'bundled-pi-stop' ($graceful -and $pi.ExitCode -eq 0) "graceful=$graceful; exit=$($pi.ExitCode)"
    $marker = Join-Path $data 'acceptance-user-data.txt'
    Set-Content $marker 'preserve-user-data' -Encoding UTF8
    $uninstall = Start-Process "$install\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$Evidence\uninstall.log`"" -WindowStyle Hidden -PassThru -Wait
    Record 'uninstall' ($uninstall.ExitCode -eq 0 -and -not (Test-Path "$install\app\Fufucode.App.exe")) "exit=$($uninstall.ExitCode)"
    Record 'uninstall-shortcut-and-protocol' (-not (Test-Path $shortcutPath) -and -not (Test-Path $protocolKey)) 'Installer-owned shortcut and protocol removed.'
    Record 'preserve-user-data' ((Get-Content $marker -Raw).Trim() -eq 'preserve-user-data') 'User data remains outside installed payload.'
    Set-Content (Join-Path $Evidence 'complete.txt') 'PASS' -Encoding ASCII
} catch {
    if ($pi) {
        if (-not $pi.HasExited) { $pi.Kill() }
        if ($errors -and $errors.Wait(5000)) { $errors.Result | Set-Content (Join-Path $Evidence 'pi-stderr.txt') -Encoding UTF8 }
    }
    $_ | Out-String | Set-Content (Join-Path $Evidence 'error.txt') -Encoding UTF8
    Set-Content (Join-Path $Evidence 'complete.txt') 'FAIL' -Encoding ASCII
} finally {
    foreach ($process in @($app, $pi)) { if ($process -and -not $process.HasExited) { $process.Kill() } }
}
