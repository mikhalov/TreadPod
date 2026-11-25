param(
    [string]$Version = "0.1.0",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$userDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (Test-Path $userDotnet) {
    $dotnet = $userDotnet
    $env:DOTNET_ROOT = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
} else {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$vpk = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"
if (-not (Test-Path $vpk)) {
    $vpk = (Get-Command vpk -ErrorAction Stop).Source
}

$publishDir = Join-Path $root "publish"
$releasesDir = Join-Path $root "releases"
$csproj = Join-Path $root "src\TreadPod.App\TreadPod.App.csproj"
$icon = Join-Path $root "src\TreadPod.App\Assets\treadpod.ico"

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
if (Test-Path (Join-Path $releasesDir "TreadPod-$Version-full.nupkg")) {
    Remove-Item $releasesDir -Recurse -Force
}
New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null

$publishArgs = @(
    "publish", $csproj,
    "-c", "Release",
    "-r", "win-x64",
    "-p:Version=$Version",
    "-o", $publishDir
)
if ($SelfContained) {
    $publishArgs += "--self-contained"
} else {
    $publishArgs += "--no-self-contained"
}

& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$vpkArgs = @(
    "pack",
    "--packId", "TreadPod",
    "--packTitle", "TreadPod",
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--mainExe", "TreadPod.App.exe",
    "--runtime", "win-x64",
    "--outputDir", $releasesDir,
    "--icon", $icon,
    "--shortcuts", "StartMenuRoot"
)
if (-not $SelfContained) {
    $vpkArgs += @("--framework", "net8-x64-desktop")
}

& $vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$setup = Join-Path $releasesDir "TreadPod-win-Setup.exe"
if (Test-Path $setup) {
    $mb = [math]::Round((Get-Item $setup).Length / 1MB, 2)
    Write-Host "`nInstaller ready: $setup ($mb MB)" -ForegroundColor Green
}
