param(
    [string]$Version = "0.1.6",
    [ValidateSet("win", "beta")][string]$Channel = "win",
    [string]$SignParams = "",
    [string]$InnoCompiler = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root ".dotnet\dotnet.exe"
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { "dotnet" }
$publishDir = Join-Path $root "artifacts\publish\win-x64"
$releaseDir = Join-Path $root "artifacts\releases"
$installerDir = Join-Path $root "artifacts\installer"
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

if (Test-Path $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
if (Test-Path $installerDir) { Remove-Item -LiteralPath $installerDir -Recurse -Force }
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null
if (Test-Path $releaseDir)
{
    $staleReleaseFiles = @(
        "DocVista-$Version-full.nupkg",
        "DocVista-$Version-delta.nupkg",
        "DocVista-$Channel-Setup.exe",
        "assets.$Channel.json",
        "releases.$Channel.json",
        "RELEASES"
    )
    foreach ($fileName in $staleReleaseFiles)
    {
        $path = Join-Path $releaseDir $fileName
        if (Test-Path $path) { Remove-Item -LiteralPath $path -Force }
    }
}

& $dotnet tool restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet publish (Join-Path $root "src\DocVista.App\DocVista.App.csproj") -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=false -o $publishDir -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$packArguments = @(
    "tool", "run", "vpk", "--", "pack",
    "--packId", "DocVista",
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--outputDir", $releaseDir,
    "--channel", $Channel,
    "--runtime", "win-x64",
    "--mainExe", "DocVista.exe",
    "--icon", (Join-Path $root "assets\DocVista.ico"),
    "--packTitle", "DocVista",
    "--packAuthors", "DocVista",
    "--releaseNotes", (Join-Path $root "CHANGELOG.md"),
    "--noPortable", "true",
    "--shortcuts", "StartMenuRoot",
    "--splashProgressColor", "#2F6FBA"
)
if ($SignParams) { $packArguments += @("--signParams", $SignParams) }
& $dotnet @packArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$velopackSetupPath = Join-Path $releaseDir "DocVista-$Channel-Setup.exe"
if (-not (Test-Path $velopackSetupPath)) { throw "Velopack bootstrapper was not generated: $velopackSetupPath" }

if (-not $InnoCompiler)
{
    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    $candidates = @(
        $command.Source,
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ -and (Test-Path $_) }
    $InnoCompiler = $candidates | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path $InnoCompiler))
{
    throw "Inno Setup 6 compiler was not found. Install Inno Setup or pass -InnoCompiler."
}

& $InnoCompiler "/DMyAppVersion=$Version" "/DMyChannel=$Channel" "/DVelopackSetupPath=$velopackSetupPath" "/DOutputDir=$installerDir" (Join-Path $root "installer\DocVista.iss")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$setupPath = Join-Path $installerDir "DocVista-$Channel-Setup.exe"
if (-not (Test-Path $setupPath)) { throw "Classic Setup was not generated: $setupPath" }

Remove-Item -LiteralPath $releaseDir -Recurse -Force
Remove-Item -LiteralPath (Join-Path $root "artifacts\publish") -Recurse -Force

Write-Host "Setup: $setupPath"
