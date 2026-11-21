param([Parameter(Mandatory)][string]$From, [Parameter(Mandatory)][string]$To, [switch]$Symbols)
# Only CLI-private files; never overwrite the desktop's shared runtime or assemblies.
$names = @('Fufucode.Cli.exe','Fufucode.Cli.dll','Fufucode.Cli.deps.json','Fufucode.Cli.runtimeconfig.json','Fufucode.Automation.Legacy.dll')
if ($Symbols) { $names += @('Fufucode.Cli.pdb','Fufucode.Automation.Legacy.pdb') | Where-Object { Test-Path -LiteralPath (Join-Path $From $_) } }
foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $From $name) -Destination (Join-Path $To $name) -Force -ErrorAction Stop }
