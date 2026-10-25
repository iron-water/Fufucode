param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [string[]]$AssetsFiles,
    [string]$PiNodeModules,
    [string]$RuntimeIdentifier = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $AssetsFiles) {
    $AssetsFiles = @('src/Fufucode.App/obj/project.assets.json', 'src/Fufucode.Cli/obj/project.assets.json') |
        ForEach-Object { Join-Path $repo $_ }
}
if (-not $PiNodeModules) { $PiNodeModules = Join-Path $repo 'src/Fufucode.Plugins.Pi/runtime/node_modules' }
$destinationPath = [IO.Path]::GetFullPath($Destination)
$packages = @{}
$excluded = [Collections.Generic.List[string]]::new()
$report = [Collections.Generic.List[object]]::new()
function Is-Notice([string]$Path) {
    return [IO.Path]::GetFileName($Path) -match '^(LICENSE|LICENCE|COPYING|NOTICE|THIRD[-_ ]PARTY(?:[-_ ]NOTICES)?)(?:[._-].*)?$'
}
function Inside-Path([string]$Root, [string]$Relative) {
    $base = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $path.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw 'Dependency file escapes its package directory.' }
    return $path
}
function Assert-ReadOnlySource([string]$Root) {
    $sourceRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    if ($destinationPath.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $destinationPath.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Destination cannot be inside a read-only dependency source.'
    }
}
Assert-ReadOnlySource $PiNodeModules
foreach ($assetsPath in $AssetsFiles) {
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    foreach ($folder in $assets.packageFolders.PSObject.Properties) { Assert-ReadOnlySource $folder.Name }
    $runtimeKeys = @{}
    $specific = @($assets.targets.PSObject.Properties | Where-Object { $_.Name.EndsWith('/' + $RuntimeIdentifier) })
    $targets = if ($specific.Count) { $specific } else { @($assets.targets.PSObject.Properties | Where-Object { $_.Name -notmatch '/' }) }
    foreach ($target in $targets) {
        foreach ($dependency in $target.Value.PSObject.Properties) {
            $entry = $dependency.Value
            $hasRuntime = $null -ne $entry.runtime -and @($entry.runtime.PSObject.Properties | Where-Object { $_.Name -notmatch '(^|/)_\._$' }).Count -gt 0
            $hasNative = $null -ne $entry.native -and @($entry.native.PSObject.Properties | Where-Object { $_.Name -notmatch '(^|/)_\._$' }).Count -gt 0
            $hasRuntimeTarget = $null -ne $entry.runtimeTargets -and @($entry.runtimeTargets.PSObject.Properties | Where-Object {
                $_.Name -notmatch '/_\._$' -and ($_.Value.rid -eq $RuntimeIdentifier -or $_.Value.rid -eq 'win')
            }).Count -gt 0
            if ($hasRuntime -or $hasNative -or $hasRuntimeTarget) { $runtimeKeys[$dependency.Name] = $true }
        }
    }
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        if (-not $runtimeKeys.ContainsKey($library.Name) -or $library.Name -match '^Avalonia\.BuildServices/') {
            if (-not $excluded.Contains($library.Name)) { $excluded.Add($library.Name) }
            continue
        }
        if ($packages.ContainsKey($library.Name)) { continue }
        $packagePath = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties) {
            $candidate = Inside-Path $folder.Name $library.Value.path
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packagePath = $candidate; break }
        }
        $packages[$library.Name] = @{ library = $library.Value; path = $packagePath }
    }
}
New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null
foreach ($key in @($packages.Keys | Sort-Object)) {
    $package = $packages[$key]
    $copied = [Collections.Generic.List[object]]::new()
    foreach ($relative in $package.library.files) {
        if (-not (Is-Notice $relative) -or -not $package.path) { continue }
        $source = Inside-Path $package.path $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $targetRelative = Join-Path ('nuget/' + $key) $relative
        $target = Inside-Path $destinationPath $targetRelative
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
        $copied.Add([pscustomobject]@{path=$targetRelative.Replace('\','/');sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash})
    }
    $report.Add([pscustomobject]@{ecosystem='nuget';package=$key;texts=@($copied.ToArray());missingText=($copied.Count -eq 0);cacheMissing=(-not $package.path)})
}
if (Test-Path -LiteralPath $PiNodeModules -PathType Container) {
    foreach ($file in Get-ChildItem -LiteralPath $PiNodeModules -Recurse -File -Filter package.json) {
        # Only package roots are candidates, not embedded example/project manifests.
        $parent = $file.Directory.Parent
        if ($parent.Name -ne 'node_modules' -and -not ($parent.Name.StartsWith('@') -and $parent.Parent.Name -eq 'node_modules')) { continue }
        $metadata = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $texts = @(Get-ChildItem -LiteralPath $file.DirectoryName -File | Where-Object { Is-Notice $_.Name } | ForEach-Object { $_.Name })
        $relative = $file.DirectoryName.Substring([IO.Path]::GetFullPath($PiNodeModules).TrimEnd('\','/').Length).TrimStart('\','/').Replace('\','/')
        $report.Add([pscustomobject]@{ecosystem='npm';package=($metadata.name + '/' + $metadata.version);path=$relative;license=$metadata.license;texts=$texts;missingText=($texts.Count -eq 0);alreadyBundled=$true})
    }
}
$summary = [pscustomobject]@{
    runtimeIdentifier=$RuntimeIdentifier
    nugetPackages=$packages.Count
    copiedTexts=@($report | Where-Object {$_.ecosystem -eq 'nuget'} | ForEach-Object {$_.texts}).Count
    npmPackages=@($report | Where-Object {$_.ecosystem -eq 'npm'}).Count
    missingTextPackages=@($report | Where-Object {$_.missingText}).Count
    excludedBuildOrOtherRuntime=@($excluded.ToArray() | Sort-Object)
    packages=@($report.ToArray())
}
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $destinationPath 'report.json') -Encoding UTF8
Write-Output "Collected $($summary.copiedTexts) texts from $($summary.nugetPackages) runtime NuGet packages; inspected $($summary.npmPackages) already-bundled npm packages; $($summary.missingTextPackages) packages lack root/license-listed texts."
