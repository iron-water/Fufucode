param(
 [Parameter(Mandatory)][string]$ApplicationProject,
 [ValidateSet('Headless','Native','All')][string]$Mode='All',
 [string]$EvidenceDirectory
)
$ErrorActionPreference='Stop'
$ApplicationProject=(Resolve-Path -LiteralPath $ApplicationProject).Path
$sourceRepository=[IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($ApplicationProject)).Parent.Parent.FullName
if (-not $EvidenceDirectory) { $EvidenceDirectory=Join-Path $sourceRepository ('tmp/browser-review-'+[Guid]::NewGuid().ToString('N')) }
$EvidenceDirectory=[IO.Path]::GetFullPath($EvidenceDirectory)
$allowedRoot=[IO.Path]::GetFullPath((Join-Path $sourceRepository 'tmp'))+[IO.Path]::DirectorySeparatorChar
if (-not $EvidenceDirectory.StartsWith($allowedRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must stay inside the selected worktree tmp directory.' }
$sourceHead=& git -C $sourceRepository rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Selected application must belong to a Git checkout.' }
[IO.Directory]::CreateDirectory($EvidenceDirectory) | Out-Null
$linkedSource=Join-Path $PSScriptRoot 'browser-review/Program.cs'
$escapedProject=[Security.SecurityElement]::Escape($ApplicationProject)
$escapedSource=[Security.SecurityElement]::Escape($linkedSource)
[xml]$appXml=Get-Content -LiteralPath $ApplicationProject -Raw
$sdkVersion=@($appXml.Project.PropertyGroup.WebView2Version | Where-Object { $_ })[0]
if (-not $sdkVersion) { throw 'Read the actual WebView2 SDK version before running review.' }
$escapedVersion=[Security.SecurityElement]::Escape($sdkVersion)
$project=@"
<Project Sdk="Microsoft.NET.Sdk">
 <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>Fufucode.App.Browser.Tests</AssemblyName><WebView2Version>$escapedVersion</WebView2Version></PropertyGroup>
 <ItemGroup><ProjectReference Include="$escapedProject" /><Compile Include="$escapedSource" Link="Program.cs" /></ItemGroup>
 <Target Name="StripUnusedWebView2ManagedRefs" BeforeTargets="ResolveAssemblyReferences"><ItemGroup>
 <Reference Remove="`$(NuGetPackageRoot)microsoft.web.webview2\`$(WebView2Version)\buildTransitive\..\\lib_manual\net5.0-windows10.0.17763.0\Microsoft.Web.WebView2.Wpf.dll" />
 <Reference Remove="`$(NuGetPackageRoot)microsoft.web.webview2\`$(WebView2Version)\buildTransitive\..\\lib_manual\netcoreapp3.0\Microsoft.Web.WebView2.WinForms.dll" />
 </ItemGroup></Target>
</Project>
"@
$projectPath=Join-Path $EvidenceDirectory 'Review.csproj'
[IO.File]::WriteAllText($projectPath,$project,[Text.UTF8Encoding]::new($false))
& dotnet build $projectPath -v quiet > (Join-Path $EvidenceDirectory 'build.log') 2>&1
$buildExit=$LASTEXITCODE
Get-Content -LiteralPath (Join-Path $EvidenceDirectory 'build.log') -Tail 8
if ($buildExit -ne 0) { exit $buildExit }
$binary=Join-Path $EvidenceDirectory 'bin/Debug/net10.0/Fufucode.App.Browser.Tests.dll'
$failed=$false
foreach ($testMode in @('Headless','Native')) {
 if ($Mode -ne 'All' -and $Mode -ne $testMode) { continue }
 $arguments=@($binary); if ($testMode -eq 'Native') { $arguments+='--native' }
 $log=Join-Path $EvidenceDirectory ($testMode.ToLowerInvariant()+'.log')
 & dotnet @arguments > $log 2>&1
 $testExit=$LASTEXITCODE
 Get-Content -LiteralPath $log
 if ($testExit -ne 0) { $failed=$true }
}
$finalHead=& git -C $sourceRepository rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $finalHead -ne $sourceHead) { throw 'Source HEAD changed while reviewing; do not reuse the result.' }
Write-Output "Reviewed HEAD: $sourceHead Evidence: $EvidenceDirectory"
if ($failed) { exit 1 }
