param(
    [string]$Version = "0.1.4",
    [ValidateSet("win", "beta")][string]$Channel = "win",
    [string]$UpdateFeedUrl = "",
    [string]$SignParams = "",
    [string]$InnoCompiler = "",
    [switch]$KeepUpdateAssets
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
& $dotnet publish (Join-Path $root "src\DocVista.Updater\DocVista.Updater.csproj") -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=false -o $publishDir -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($UpdateFeedUrl)
{
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Join-Path $publishDir "update-feed.txt"), $UpdateFeedUrl, $utf8WithoutBom)
}

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
    "--splashProgressColor", "#80A3C8"
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

if (-not $KeepUpdateAssets)
{
    Remove-Item -LiteralPath $releaseDir -Recurse -Force
    Remove-Item -LiteralPath (Join-Path $root "artifacts\publish") -Recurse -Force
}

Write-Host "Setup: $setupPath"
