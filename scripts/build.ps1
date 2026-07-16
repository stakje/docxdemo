param([ValidateSet("Debug", "Release")][string]$Configuration = "Debug")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root ".dotnet\dotnet.exe"
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { "dotnet" }
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

& $dotnet restore (Join-Path $root "DocVista.sln")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet build (Join-Path $root "DocVista.sln") -c $Configuration --no-restore -m:1
exit $LASTEXITCODE
