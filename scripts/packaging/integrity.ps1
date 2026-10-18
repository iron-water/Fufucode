param([ValidateSet('Write','Verify')][string]$Mode='Verify',[Parameter(Mandatory)][string]$PackageRoot,
  # 双渠道打包：全量渠道用 integrity.sha256.json，原生渠道用 integrity-native.sha256.json。
  # 默认值保持不变，既有调用方行为不受影响。
  [string]$ManifestName='integrity.sha256.json')
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $PackageRoot).Path
if([string]::IsNullOrWhiteSpace($ManifestName) -or $ManifestName -match '[\\/:*?"<>|]'){throw "Unsupported manifest name: $ManifestName"}
$manifest=Join-Path $root $ManifestName
function Get-PackageFiles {
  Get-ChildItem -LiteralPath $root -File -Recurse -Force | Where-Object {
    if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Package contains a file link: $($_.FullName)" }
    $_.FullName -ne $manifest
  }
}
if($Mode -eq 'Write') {
  $files=@(Get-PackageFiles | ForEach-Object {
    @{path=$_.FullName.Substring($root.TrimEnd('\').Length + 1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
  } | Sort-Object path)
  @{format=1;files=$files}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $manifest -Encoding utf8
  return
}
if(-not(Test-Path -LiteralPath $manifest)){throw "Missing integrity manifest: $manifest"}
$data=Get-Content -LiteralPath $manifest -Raw|ConvertFrom-Json
if($data.format -ne 1){throw 'Unsupported manifest format'}
$expected=@($data.files)
$missing=@();$changed=@();$known=[collections.generic.hashset[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($item in $expected){
  $rel=[string]$item.path
  if([string]::IsNullOrWhiteSpace($rel) -or $rel -match '(^/|^[A-Za-z]:|(^|/)\.\.(/|$)|(^|/)\.(/|$)|\\|:)'){throw "Unsafe manifest path: $rel"}
  if([string]$item.sha256 -notmatch '^[a-fA-F0-9]{64}$'){throw "Invalid hash in manifest: $rel"}
  if(-not $known.Add($rel)){throw "Duplicate manifest path: $rel"}
  $path=Join-Path $root $rel
  if(-not(Test-Path -LiteralPath $path -PathType Leaf)){$missing+= $rel;continue}
  if((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Package contains a file link: $rel"}
  if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $item.sha256){$changed+= $rel}
}
$extra=@(Get-PackageFiles|ForEach-Object {$_.FullName.Substring($root.TrimEnd('\').Length + 1).Replace('\','/')}|Where-Object {-not $known.Contains($_)})
$result=[pscustomobject]@{ok=($missing.Count -eq 0 -and $changed.Count -eq 0 -and $extra.Count -eq 0);checked=$expected.Count;missing=$missing;changed=$changed;extra=$extra}
$result|ConvertTo-Json -Depth 4
if(-not $result.ok){exit 1}
