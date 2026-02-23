param(
    [string]$Configuration = "Release",
    [string]$PublishDir = "publish",
    [string]$DistDir = "dist",
    [string]$ZipName = "ME_ACS_SQL_Patcher.zip",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$SatelliteResourceLanguages = "en",
    [switch]$FrameworkDependent
)

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishPath = Join-Path $root $PublishDir
$distPath = Join-Path $root $DistDir
$zipPath = Join-Path $distPath $ZipName

Write-Host "Publishing to $publishPath..."
# Ensure a clean publish output so old binaries are not carried into the package.
if (Test-Path $publishPath) {
    Remove-Item -Recurse -Force $publishPath
}

$publishArgs = @(
    "publish",
    (Join-Path $root "ME_ACS_SQL_Patcher.csproj"),
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

if (Test-Path $distPath) {
    Remove-Item -Recurse -Force $distPath
}
New-Item -ItemType Directory -Path $distPath | Out-Null

if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

# Strip non-runtime debug symbols from distributable output.
Get-ChildItem -Path $publishPath -Filter *.pdb -Recurse -File | Remove-Item -Force -ErrorAction SilentlyContinue

$installScriptSource = Join-Path $root "scripts\Install-Portable.ps1"
if (Test-Path $installScriptSource) {
    $publishScriptsDir = Join-Path $publishPath "scripts"
    New-Item -ItemType Directory -Path $publishScriptsDir -Force | Out-Null
    Copy-Item -Path $installScriptSource -Destination (Join-Path $publishScriptsDir "Install-Portable.ps1") -Force
}

Write-Host "Creating zip at $zipPath..."
Compress-Archive -Path (Join-Path $publishPath "*") -DestinationPath $zipPath -Force

Write-Host "Done. Share this file:"
Write-Host $zipPath
