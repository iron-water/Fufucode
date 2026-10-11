<#
M7 installer upgrade/rollback drill.

Sequence:
  install <old> -> verify layout + selftest -> upgrade to <new> -> verify layout + integrity -> uninstall

Evidence: install logs (/LOG), installed directory manifests, selftest output, integrity result.
All written to labs/packaging/drill/.

Usage:
  pwsh run-drill.ps1 [-OldVersion 1.2.0] [-NewVersion 1.4.0]
#>
param(
    [string]$OldVersion = '1.2.0',
    [string]$NewVersion = '1.4.0'
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ini = Join-Path $root 'scripts/packaging/pi-pipe.ps1'
$drillDir = Join-Path $root 'labs/packaging/drill'
$oldSetup = Join-Path $root "out\Fufucode-$OldVersion-setup.exe"
$newSetup = Join-Path $root "out\Fufucode-$NewVersion-setup.exe"
$manifest = Join-Path $root 'out/build/integrity.sha256.json'
$report = Join-Path $drillDir 'drill-report.jsonl'

New-Item -ItemType Directory -Force $drillDir | Out-Null
Remove-Item -LiteralPath $report -ErrorAction SilentlyContinue

function Out-Log {
    param([string]$msg)
    $stamp = Get-Date -Format 'HH:mm:ss.fff'
    Add-Content -Path (Join-Path $drillDir 'run.log') -Value "$stamp $msg"
    Write-Host $msg
}

function Record {
    param([hashtable]$rec)
    $rec.time = Get-Date -Format 'o'
    $rec | ConvertTo-Json -Compress | Add-Content -Path $report
}

function Invoke-SilentInstall {
    param([string]$Installer, [string]$Log)
    # /VERYSILENT suppresses the "corrupted"/error message boxes so failures are
    # captured by exit code + /LOG rather than blocking on a GUI dialog.
    $proc = Start-Process -FilePath $Installer -ArgumentList '/VERYSILENT', "/LOG=`"$Log`"" -WindowStyle Hidden -PassThru -Wait
    return $proc.ExitCode
}

function Uninstall-Silent {
    param([string]$UninstallKey, [string]$Log)
    $uninst = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$UninstallKey" -ErrorAction SilentlyContinue).UninstallString
    if (-not $uninst) { $uninst = (Get-ItemProperty "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$UninstallKey" -ErrorAction SilentlyContinue).UninstallString }
    if (-not $uninst) { throw "uninstaller not found for $UninstallKey" }
    $uninst = $uninst -replace '^"([^"]+)"$', '$1'
    $proc = Start-Process -FilePath $uninst -ArgumentList '/SILENT', '/VERYSILENT', "/LOG=`"$Log`"" -WindowStyle Hidden -PassThru -Wait
    return $proc.ExitCode
}

function Get-InstalledManifest {
    param([string]$InstallDir)
    $sep = [IO.Path]::DirectorySeparatorChar
    $excluded = @('logs', 'pi-agent', 'sessions', 'desktop.db')
    $entries = Get-ChildItem -LiteralPath $InstallDir -Recurse -File | Where-Object {
        $rel = $_.FullName.Substring($InstallDir.Length).TrimStart($sep)
        $top = ($rel -split [regex]::Escape([string]$sep))[0]
        $excluded -notcontains $top
    }
    $entries | ForEach-Object {
        $rel = $_.FullName.Substring($InstallDir.Length).TrimStart($sep) -replace [regex]::Escape([string]$sep), '/'
        $raw = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([System.IO.File]::ReadAllBytes($_.FullName)))
        $hash = $raw.Replace('-', '').ToLowerInvariant()
        [PSCustomObject]@{ path = $rel; size = $_.Length; sha256 = $hash }
    }
}

$ok = $true
$defaultInstallDir = Join-Path $Env:LOCALAPPDATA 'Programs\Fufucode'
$uninstallKey = '{640956A0-9ACB-4E56-BDBA-49608B84942E}_is1'

function New-Rec {
    param([string]$phase)
    return @{ phase = $phase }
}

function Get-InstallDir {
    # UsePreviousAppDir can redirect the real install location if a stale AppId
    # uninstall entry exists; read InstallLocation from the registry after each
    # install instead of assuming DefaultDirName. Falls back to DefaultDirName.
    foreach ($scope in 'HKCU', 'HKLM') {
        $key = "${scope}:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{640956A0-9ACB-4E56-BDBA-49608B84942E}_is1"
        if (Test-Path $key) {
            $loc = (Get-ItemProperty $key).InstallLocation
            if ($loc -and (Test-Path -LiteralPath $loc.TrimEnd('\'))) { return $loc.TrimEnd('\') }
        }
    }
    return $defaultInstallDir
}

# Resolved after each install via Get-InstallDir (see UsePreviousAppDir note above).
$installDir = $defaultInstallDir

# --- 1. install old version ---
Out-Log "=== phase 1: install $OldVersion ==="
$log0 = Join-Path $drillDir "install-$OldVersion.log"
$exit0 = Invoke-SilentInstall -Installer $oldSetup -Log $log0
$r = New-Rec -phase "install-$OldVersion"; $r.exitCode = $exit0; $r.pass = ($exit0 -eq 0); Record -rec $r
if (-not $r.pass) { Out-Log "  FAIL install $OldVersion exit=$exit0"; $ok = $false }
else { Out-Log "  ok install $OldVersion exit=0" }

# Resolve the real install location (UsePreviousAppDir may redirect it).
$installDir = Get-InstallDir
$r = New-Rec -phase 'resolved-install-dir'; $r.dir = $installDir; $r.pass = ($installDir -eq $defaultInstallDir); Record -rec $r
Out-Log "  resolved install dir: $installDir$(if ($installDir -ne $defaultInstallDir) { ' (WARNING: redirected by UsePreviousAppDir)' })"

# --- 2. verify installed layout + selftest ---
Out-Log "=== phase 2: verify $OldVersion layout + selftest ==="
$layoutOk = (
    (Test-Path (Join-Path $installDir 'app\Fufucode.App.exe')) -and
    (Test-Path (Join-Path $installDir 'runtime\node\node.exe')) -and
    (Test-Path (Join-Path $installDir 'runtime\pi\dist\bundle\cli.js')) -and
    (Test-Path (Join-Path $installDir 'Fufucode.cmd')) -and
    (Test-Path (Join-Path $installDir 'extensions\fufucode-bridge\index.ts')) -and
    (Test-Path (Join-Path $installDir 'extensions\fufucode-bridge\package.json')) -and
    (Test-Path (Join-Path $installDir 'extensions\fufucode-bridge\policy.ts'))
)
$r = New-Rec -phase "layout-$OldVersion"; $r.pass = $layoutOk; $r.hasIntegrityManifest = Test-Path (Join-Path $installDir 'integrity.sha256.json'); Record -rec $r
if (-not $layoutOk) { Out-Log "  FAIL layout"; $ok = $false }
else { Out-Log "  ok layout; integrity manifest present=$($r.hasIntegrityManifest)" }

$stLog = Join-Path $drillDir "selftest-$OldVersion.log"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$st = Start-Process -FilePath (Join-Path $installDir 'app\Fufucode.App.exe') -ArgumentList '--selftest' -NoNewWindow -PassThru -Wait `
    -RedirectStandardOutput (Join-Path $drillDir "selftest-$OldVersion.out.txt") `
    -RedirectStandardError (Join-Path $drillDir "selftest-$OldVersion.err.txt") `
    -Environment @{ FUFUCODE_DATA = Join-Path $Env:LOCALAPPDATA 'Fufucode' }
$sw.Stop()
$stText = Get-Content (Join-Path $drillDir "selftest-$OldVersion.out.txt") -Raw
$r = New-Rec -phase "selftest-$OldVersion"; $r.exitCode = $st.ExitCode; $r.elapsedMs = $sw.ElapsedMilliseconds; $r.pass = ($st.ExitCode -eq 0) -and ($stText -match '0 项失败'); Record -rec $r
if (-not $r.pass) { Out-Log "  FAIL selftest $OldVersion exit=$($st.ExitCode)"; $ok = $false }
else { Out-Log "  ok selftest $OldVersion in $($sw.ElapsedMilliseconds) ms" }

# --- 3. install saved layout manifest for later diff ---
Out-Log "=== phase 2b: snapshot $OldVersion installed tree ==="
Get-InstalledManifest -InstallDir $installDir | ConvertTo-Json -Depth 4 | Out-File (Join-Path $drillDir "tree-$OldVersion.json") -Encoding Utf8
$r = New-Rec -phase "snapshot-$OldVersion"; $r.files = (Get-Content (Join-Path $drillDir "tree-$OldVersion.json") | ConvertFrom-Json).Count; Record -rec $r
Out-Log "  recorded $($r.files) entries"

# --- 4. upgrade install new version ---
Out-Log "=== phase 3: upgrade to $NewVersion ==="
$log1 = Join-Path $drillDir "install-$NewVersion.log"
$exit1 = Invoke-SilentInstall -Installer $newSetup -Log $log1
$r = New-Rec -phase "upgrade-$NewVersion"; $r.exitCode = $exit1; $r.pass = ($exit1 -eq 0); Record -rec $r
if (-not $r.pass) { Out-Log "  FAIL upgrade exit=$exit1"; $ok = $false }
else { Out-Log "  ok upgrade exit=0" }

# Re-resolve in case the upgrade redirected (should stay the same dir).
$installDir = Get-InstallDir
$r = New-Rec -phase "resolved-install-dir-$NewVersion"; $r.dir = $installDir; Record -rec $r
Out-Log "  upgrade install dir: $installDir"

# --- 5. verify upgraded layout + integrity ---
Out-Log "=== phase 4: verify $NewVersion layout + integrity ==="
$layout1Ok = (
    (Test-Path (Join-Path $installDir 'app\Fufucode.App.exe')) -and
    (Test-Path (Join-Path $installDir 'runtime\node\node.exe')) -and
    (Test-Path (Join-Path $installDir 'runtime\pi\node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js')) -and
    (Test-Path (Join-Path $installDir 'extensions\fufucode-bridge\index.ts'))
)
$r = New-Rec -phase "layout-$NewVersion"; $r.pass = $layout1Ok; Record -rec $r
if (-not $layout1Ok) { Out-Log "  FAIL layout $NewVersion"; $ok = $false }
else { Out-Log "  ok layout $NewVersion" }

$intLog = Join-Path $drillDir "integrity-$NewVersion.log"
& (Join-Path $root 'scripts/packaging/integrity.ps1') -Mode Verify -PackageRoot $installDir > $intLog 2>&1
$intExit = $LASTEXITCODE
$intResult = Get-Content $intLog -Raw | ConvertFrom-Json
# Only these root Inno files are allowed; selftest screenshots remain unexpected.
$expectedExtra = @('unins000.dat', 'unins000.exe')
$unexpectedExtra = @($intResult.extra | Where-Object { $_ -notin $expectedExtra })
$r = New-Rec -phase "integrity-$NewVersion"
$r.checked = $intResult.checked; $r.missing = $intResult.missing; $r.changed = $intResult.changed
$r.expectedExtra = $expectedExtra; $r.extra = $unexpectedExtra
$r.pass = ($intResult.missing.Count -eq 0) -and ($intResult.changed.Count -eq 0) -and ($unexpectedExtra.Count -eq 0)
Record -rec $r
if (-not $r.pass) { Out-Log "  FAIL integrity: missing=$($r.missing.Count) changed=$($r.changed.Count) extra=$($r.extra.Count)"; $ok = $false }
else { Out-Log "  ok integrity ($($r.checked) files)" }

Get-InstalledManifest -InstallDir $installDir | ConvertTo-Json -Depth 4 | Out-File (Join-Path $drillDir "tree-$NewVersion.json") -Encoding Utf8
$r = New-Rec -phase "snapshot-$NewVersion"; $r.files = (Get-Content (Join-Path $drillDir "tree-$NewVersion.json") | ConvertFrom-Json).Count; Record -rec $r
Out-Log "  recorded $($r.files) entries"

# --- 6. uninstall ---
Out-Log "=== phase 5: uninstall ==="
$unLog = Join-Path $drillDir 'uninstall.log'
$exitU = Uninstall-Silent -UninstallKey $uninstallKey -Log $unLog
$r = New-Rec -phase 'uninstall'; $r.exitCode = $exitU; $r.pass = ($exitU -eq 0); Record -rec $r
if (-not $r.pass) { Out-Log "  FAIL uninstall exit=$exitU"; $ok = $false }
else { Out-Log "  ok uninstall exit=0" }
Start-Sleep -Seconds 2
# Uninstallers only remove files they installed; runtime-written leftovers
# (app's --selftest screenshot, Inno's unins*.dat residue) legitimately remain.
# Assert no CODE/payload files survive, not that the dir is empty.
$codeLeftover = @()
if (Test-Path $installDir) {
    $codeLeftover = @(Get-ChildItem -LiteralPath $installDir -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
        $_.Extension -in @('.exe', '.dll', '.js', '.cmd', '.ps1', '.json') -and $_.Name -notmatch '^unins.*\.(exe|dat)$'
    })
}
$r = New-Rec -phase 'code-tree-removed'
$r.pass = ($codeLeftover.Count -eq 0)
$r.leftover = $codeLeftover | ForEach-Object { $_.FullName }
Record -rec $r
if (-not $r.pass) { Out-Log "  FAIL code payload still present: $($codeLeftover.Count) files"; $ok = $false }
else { Out-Log "  ok code tree removed (runtime leftovers only: $(if (Test-Path $installDir) { 'selftest png etc.' } else { 'none' }))" }

# --- 7. user data preserved after uninstall ---
Out-Log "=== phase 6: user data preservation ==="
$userData = Join-Path $Env:LOCALAPPDATA 'Fufucode'
$r = New-Rec -phase 'user-data-preserved'; $r.pass = (Test-Path $userData); $r.exists = $r.pass; Record -rec $r
if (-not $r.pass) { Out-Log "  FAIL user data dir missing"; $ok = $false }
else { Out-Log "  ok user data dir preserved at $userData" }

# --- 8. rollback readiness (old installer still produces a working install) ---
Out-Log "=== phase 7: rollback readiness (reinstall $OldVersion) ==="
$logR = Join-Path $drillDir "install-rollback-$OldVersion.log"
$exitR = Invoke-SilentInstall -Installer $oldSetup -Log $logR
$r = New-Rec -phase "rollback-$OldVersion"; $r.exitCode = $exitR; $r.pass = ($exitR -eq 0); Record -rec $r
if (-not $r.pass) { Out-Log "  FAIL rollback install exit=$exitR"; $ok = $false }
else { Out-Log "  ok rollback install exit=0" }
# clean up the rollback install so the machine is left uninstalled
Uninstall-Silent -UninstallKey $uninstallKey -Log (Join-Path $drillDir 'uninstall-rollback.log') | Out-Null
Start-Sleep -Seconds 2

$r = New-Rec -phase 'overall'; $r.pass = $ok; $r.installDir = $installDir; Record -rec $r
Out-Log "=== drill: $(if ($ok) {'PASS'} else {'FAIL'}) ==="
Out-Log "Report: $report"
exit ($ok ? 0 : 1)
