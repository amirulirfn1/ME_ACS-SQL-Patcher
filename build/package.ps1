param(
    [string]$Configuration = "Release",
    [string]$PublishDir = "artifacts\\publish-temp",
    [string]$PortableDir = "output",
    [string]$DistDir = "dist",
    [string]$ZipName = "ME_ACS_SQL_Patcher.zip",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$SatelliteResourceLanguages = "en",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$projectPath = Join-Path $repoRoot "src\\ME_ACS_SQL_Patcher\\ME_ACS_SQL_Patcher.csproj"
$publishPath = Join-Path $repoRoot $PublishDir
$portablePath = Join-Path $repoRoot $PortableDir
$distPath = Join-Path $repoRoot $DistDir
$zipPath = Join-Path $distPath $ZipName
$legacyPublishPath = Join-Path $repoRoot "publish"

$runningApp = Get-Process ME_ACS_SQL_Patcher -ErrorAction SilentlyContinue
if ($runningApp) {
    $processList = ($runningApp | Select-Object -ExpandProperty Id) -join ", "
    throw "ME_ACS_SQL_Patcher is still running (PID: $processList). Close the app, then run .\\build\\package.ps1 again."
}

Write-Host "Cleaning Release build output..."
dotnet clean $projectPath -c $Configuration | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Cleaning Release build output for $RuntimeIdentifier..."
dotnet clean $projectPath -c $Configuration -r $RuntimeIdentifier | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Publishing portable app into a temporary staging folder..."
# Ensure a clean staging output so old binaries are not carried into the package.
if (Test-Path $publishPath) {
    Remove-Item -Recurse -Force $publishPath
}
if (Test-Path $legacyPublishPath) {
    Remove-Item -Recurse -Force $legacyPublishPath
}

$publishArgs = @(
    "publish",
    $projectPath,
    "-c", $Configuration,
    "-o", $publishPath,
    "-r", $RuntimeIdentifier,
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:SatelliteResourceLanguages=$SatelliteResourceLanguages"
)

if ($FrameworkDependent) {
    $publishArgs += "--self-contained"
    $publishArgs += "false"
} else {
    $publishArgs += "--self-contained"
    $publishArgs += "true"
    $publishArgs += "-p:PublishSingleFile=true"
    $publishArgs += "-p:EnableCompressionInSingleFile=true"
    $publishArgs += "-p:PublishTrimmed=false"
}

# Publish only the app project (not the solution), and default to self-contained output.
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Preparing handoff folder at $portablePath..."
if (Test-Path $portablePath) {
    Remove-Item -Recurse -Force $portablePath
}
New-Item -ItemType Directory -Path $portablePath -Force | Out-Null
Copy-Item -Path (Join-Path $publishPath "*") -Destination $portablePath -Recurse -Force

if (Test-Path $distPath) {
    Remove-Item -Recurse -Force $distPath
}
New-Item -ItemType Directory -Path $distPath | Out-Null

if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

# Strip non-runtime debug symbols from distributable output.
# Use the published staging folder for the zip so bundled patches/ content is included.
Get-ChildItem -Path $publishPath -Filter *.pdb -Recurse -File | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "Creating zip at $zipPath..."
Compress-Archive -Path (Join-Path $publishPath "*") -DestinationPath $zipPath -Force -ErrorAction Stop

if (Test-Path $publishPath) {
    Remove-Item -Recurse -Force $publishPath
}

Write-Host "Done. Share this handoff archive:"
Write-Host $zipPath
Write-Host "Open this folder to smoke-test the published app:"
Write-Host $portablePath
