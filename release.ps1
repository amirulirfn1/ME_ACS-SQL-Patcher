param(
    [string]$Configuration     = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Channel           = "win",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$repoRoot     = $PSScriptRoot
$projectPath  = Join-Path $repoRoot "src\ME_ACS_SQL_Patcher\ME_ACS_SQL_Patcher.csproj"
$solutionPath = Join-Path $repoRoot "ME_ACS_SQL_Patcher.sln"
$publishTemp  = Join-Path $repoRoot "artifacts\publish-temp"
$outputDir    = Join-Path $repoRoot "output"
$distDir      = Join-Path $repoRoot "dist"
$installerDir = Join-Path $distDir  "installer"
$feedDir      = Join-Path $repoRoot "feed"
$iconPath     = Join-Path $repoRoot "src\ME_ACS_SQL_Patcher\Assets\me-acs-patcher.ico"

# Read version from csproj
[xml]$proj = Get-Content $projectPath
$version = @($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1)[0].Version
if ([string]::IsNullOrWhiteSpace($version)) { throw "Could not read version from $projectPath" }

Write-Host ""
Write-Host "Building ME_ACS SQL Patcher v$version"
Write-Host "======================================"

# Guard: app must not be running
$running = Get-Process ME_ACS_SQL_Patcher -ErrorAction SilentlyContinue
if ($running) {
    $pids = ($running | Select-Object -ExpandProperty Id) -join ", "
    throw "App is still running (PID: $pids). Close it first."
}

# --- TESTS ---
if (-not $SkipTests) {
    Write-Host "Running tests..."
    dotnet test $solutionPath | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# --- TOOLS ---
Write-Host "Restoring tools..."
dotnet tool restore | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# --- BUILD + PUBLISH ---
Write-Host "Cleaning previous build..."
dotnet clean $projectPath -c $Configuration | Out-Host
dotnet clean $projectPath -c $Configuration -r $RuntimeIdentifier | Out-Host

Write-Host "Publishing..."
if (Test-Path $publishTemp) { Remove-Item -Recurse -Force $publishTemp }
if (Test-Path (Join-Path $repoRoot "publish")) { Remove-Item -Recurse -Force (Join-Path $repoRoot "publish") }

dotnet publish $projectPath `
    -c $Configuration `
    -o $publishTemp `
    -r $RuntimeIdentifier `
    --self-contained true `
    "-p:PublishSingleFile=true" `
    "-p:EnableCompressionInSingleFile=true" `
    "-p:PublishTrimmed=false" `
    "-p:DebugType=None" `
    "-p:DebugSymbols=false" `
    "-p:SatelliteResourceLanguages=en" | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Copy to output/ (persistent portable folder, used by vpk)
Write-Host "Preparing output folder..."
if (Test-Path $outputDir) { Remove-Item -Recurse -Force $outputDir }
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
Copy-Item -Path (Join-Path $publishTemp "*") -Destination $outputDir -Recurse -Force

# --- DIST ---
if (Test-Path $distDir) { Remove-Item -Recurse -Force $distDir }
New-Item -ItemType Directory -Path $distDir | Out-Null

# Strip .pdb from the zip source
Get-ChildItem -Path $publishTemp -Filter *.pdb -Recurse -File | Remove-Item -Force -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $publishTemp

# --- VPK PACK ---
Write-Host "Packing Velopack installer..."
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

$releaseNotesPath = Join-Path $repoRoot "artifacts\release-notes-$version.md"
New-Item -ItemType Directory -Path (Split-Path -Parent $releaseNotesPath) -Force | Out-Null
"# ME_ACS SQL Patcher $version`n- Installed-app release" | Set-Content $releaseNotesPath -Encoding UTF8

dotnet tool run vpk -- pack `
    --packId    "ME_ACS_SQL_Patcher" `
    --packVersion $version `
    --packDir   $outputDir `
    --mainExe   "ME_ACS_SQL_Patcher.exe" `
    --packTitle "ME_ACS SQL Patcher" `
    --packAuthors "Magnet Security" `
    --outputDir $installerDir `
    --runtime   $RuntimeIdentifier `
    --channel   $Channel `
    --releaseNotes $releaseNotesPath `
    --icon      $iconPath `
    --delta     None `
    --shortcuts "Desktop,StartMenuRoot" | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# --- HANDOFF ZIP ---
Write-Host "Building handoff zip..."
$stagingDir = Join-Path $repoRoot "artifacts\handoff-staging"
if (Test-Path $stagingDir) { Remove-Item -Recurse -Force $stagingDir }
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

@"
ME ACS SQL Patcher v$version
================================

OPTION A: Install (Recommended)
  1. Run ME_ACS_SQL_Patcher-win-Setup.exe
  2. A desktop shortcut will be created automatically

OPTION B: Portable (No Install)
  1. Open the portable\ folder
  2. Double-click ME_ACS_SQL_Patcher.exe

Requirements: Windows 10 or 11, 64-bit

------------------------------------------------------------
UPDATING
  New app version  : Run the new Setup.exe over the old one.
  New SQL patches  : Open the app -> Import Patches -> select MagPatchPack-*.zip
Your data lives in: %LOCALAPPDATA%\MagDbPatcher\
------------------------------------------------------------
"@ | Set-Content (Join-Path $stagingDir "README.txt") -Encoding ASCII

$setupExe = Join-Path $installerDir "ME_ACS_SQL_Patcher-win-Setup.exe"
if (Test-Path $setupExe) { Copy-Item $setupExe (Join-Path $stagingDir "ME_ACS_SQL_Patcher-win-Setup.exe") }

$portableStaging = Join-Path $stagingDir "portable"
New-Item -ItemType Directory -Path $portableStaging -Force | Out-Null
Copy-Item -Path (Join-Path $outputDir "*") -Destination $portableStaging -Recurse -Force

$handoffZip = Join-Path $distDir "ME_ACS_SQL_Patcher-$version.zip"
Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $handoffZip -Force
Remove-Item -Recurse -Force $stagingDir

# --- PUBLISH TO FEED ---
Write-Host "Publishing to update feed..."
New-Item -ItemType Directory -Path $feedDir -Force | Out-Null
Get-ChildItem -Path $feedDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne '.gitkeep' } |
    Remove-Item -Force

$installerFiles = Get-ChildItem -Path $installerDir -File
if (-not $installerFiles) { throw "No installer artifacts found in $installerDir." }
foreach ($f in $installerFiles) { Copy-Item $f.FullName $feedDir -Force }
Remove-Item -Recurse -Force $installerDir

$buildDateFile = Join-Path $outputDir "build-date.txt"
$buildDate = if (Test-Path $buildDateFile) {
    (Get-Content $buildDateFile -Raw).Trim()
} else {
    [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
}

[ordered]@{
    version       = $version
    buildDate     = $buildDate
    installerName = "ME_ACS_SQL_Patcher-win-Setup.exe"
} | ConvertTo-Json | Set-Content (Join-Path $feedDir "latest.json") -Encoding UTF8

Write-Host ""
Write-Host "======================================"
Write-Host "  Release complete  (v$version)"
Write-Host "======================================"
Write-Host ""
Write-Host "  Handoff zip : $handoffZip"
Write-Host "  Feed folder : $feedDir"
Write-Host "  Build date  : $buildDate"
Write-Host ""
Write-Host "  Start update server: python serve.py"
Write-Host ""
