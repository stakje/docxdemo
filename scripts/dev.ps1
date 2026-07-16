param([string]$Document)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root ".dotnet\dotnet.exe"
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { "dotnet" }
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

$arguments = @("run", "--project", (Join-Path $root "src\DocVista.App\DocVista.App.csproj"), "--")
if ($Document) { $arguments += (Resolve-Path $Document).Path }
& $dotnet @arguments
