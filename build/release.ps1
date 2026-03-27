param(
    [string]$Configuration = "Release",
    [string]$PortableDir = "output",
    [string]$DistDir = "dist",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Channel = "win",
    [string]$ReleaseNotesPath = "",
    [switch]$FrameworkDependent,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$projectPath = Join-Path $repoRoot "src\\ME_ACS_SQL_Patcher\\ME_ACS_SQL_Patcher.csproj"
$solutionPath = Join-Path $repoRoot "ME_ACS_SQL_Patcher.sln"
$portablePath = Join-Path $repoRoot $PortableDir
$distPath = Join-Path $repoRoot $DistDir
$installerPath = Join-Path $distPath "installer"
$manifestPath = Join-Path $distPath "release-manifest.json"

[xml]$project = Get-Content $projectPath
$version = @($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1)[0].Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not determine app version from $projectPath"
}

$zipName = "ME_ACS_SQL_Patcher-$version.zip"
$iconPath = Join-Path $repoRoot "src\\ME_ACS_SQL_Patcher\\Assets\\me-acs-patcher.ico"
$generatedReleaseNotesPath = Join-Path $repoRoot "artifacts\\release-notes-$version.md"

if (-not $SkipTests) {
    Write-Host "Running tests before packaging..."
    dotnet test $solutionPath | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Restoring local tools..."
dotnet tool restore | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Building published app folder..."
$packageArgs = @{
    Configuration = $Configuration
    PortableDir = $PortableDir
    DistDir = $DistDir
    ZipName = $zipName
    RuntimeIdentifier = $RuntimeIdentifier
}

if ($FrameworkDependent) {
    $packageArgs.FrameworkDependent = $true
}

& (Join-Path $scriptDir "package.ps1") @packageArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ([string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $generatedReleaseNotesPath) -Force | Out-Null
    @"
# ME_ACS SQL Patcher $version

- Installed-app release with managed LocalAppData runtime storage
- Manual app updates with patch-pack-first SQL content workflow
"@ | Set-Content -Path $generatedReleaseNotesPath -Encoding UTF8
    $ReleaseNotesPath = $generatedReleaseNotesPath
}

if (Test-Path $installerPath) {
    Remove-Item -Recurse -Force $installerPath
}
New-Item -ItemType Directory -Path $installerPath -Force | Out-Null

Write-Host "Packing Velopack installer artifacts..."
dotnet tool run vpk -- pack `
    --packId "ME_ACS_SQL_Patcher" `
    --packVersion $version `
    --packDir $portablePath `
    --mainExe "ME_ACS_SQL_Patcher.exe" `
    --packTitle "ME_ACS SQL Patcher" `
    --packAuthors "Magnet Security" `
    --outputDir $installerPath `
    --runtime $RuntimeIdentifier `
    --channel $Channel `
    --releaseNotes $ReleaseNotesPath `
    --icon $iconPath `
    --delta None `
    --shortcuts "Desktop,StartMenuRoot" | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$manifest = [ordered]@{
    version = $version
    runtime = $RuntimeIdentifier
    channel = $Channel
    zip = $zipName
    installerDirectory = (Resolve-Path $installerPath).Path
    publishedFolder = (Resolve-Path $portablePath).Path
    updateMode = "manual-app-updates"
    sqlContentMode = "patch-pack-first"
}
$manifest | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding UTF8

$combinedZipName = "ME_ACS_SQL_Patcher-$version.zip"
$combinedZipPath = Join-Path $distPath $combinedZipName
$readmeContent = @"
ME ACS SQL Patcher v$version
================================

Two ways to get started - pick one:

OPTION A: Install (Recommended)
  1. Run ME_ACS_SQL_Patcher-win-Setup.exe
  2. A desktop shortcut will be created automatically

OPTION B: Portable (No Install)
  1. Open the portable\ folder
  2. Double-click ME_ACS_SQL_Patcher.exe

Requirements: Windows 10 or 11, 64-bit

------------------------------------------------------------
UPDATING THE APP

  New app version  : Run the new Setup.exe over the old one,
                     or replace the portable\ folder contents.

  New SQL patches  : Open the app, click Import Patches,
                     and select the MagPatchPack-*.zip you received.

Your settings, logs, and patch library are stored in:
  %LOCALAPPDATA%\MagDbPatcher\

These are NOT deleted when you update the app.
------------------------------------------------------------

Support: Magnet Security
"@

Write-Host "Building combined handoff zip..."
$stagingPath = Join-Path $repoRoot "artifacts\\handoff-staging"
if (Test-Path $stagingPath) { Remove-Item -Recurse -Force $stagingPath }
New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null

$readmeContent | Set-Content -Path (Join-Path $stagingPath "README.txt") -Encoding ASCII

$setupExe = Join-Path $installerPath "ME_ACS_SQL_Patcher-win-Setup.exe"
if (Test-Path $setupExe) {
    Copy-Item -Path $setupExe -Destination (Join-Path $stagingPath "ME_ACS_SQL_Patcher-win-Setup.exe")
}

$portableStaging = Join-Path $stagingPath "portable"
New-Item -ItemType Directory -Path $portableStaging -Force | Out-Null
Copy-Item -Path (Join-Path $portablePath "*") -Destination $portableStaging -Recurse -Force

if (Test-Path $combinedZipPath) { Remove-Item -Force $combinedZipPath }
Compress-Archive -Path (Join-Path $stagingPath "*") -DestinationPath $combinedZipPath -Force
Remove-Item -Recurse -Force $stagingPath

Write-Host "Publishing installer artifacts to update feed..."
$updateFeedPath = Join-Path $repoRoot "update-host\\feed"
& (Join-Path $repoRoot "update-host\\Publish-UpdateFeed.ps1") -InstallerSource $installerPath -FeedDirectory $updateFeedPath
if (Test-Path $installerPath) { Remove-Item -Recurse -Force $installerPath }

Write-Host ""
Write-Host "Release ready. Send this one file to support:"
Write-Host "  $combinedZipPath"
Write-Host ""
Write-Host "Contents of the zip:"
Write-Host "  README.txt"
Write-Host "  ME_ACS_SQL_Patcher-win-Setup.exe  <- Option A: install"
Write-Host "  portable\ME_ACS_SQL_Patcher.exe   <- Option B: run directly"
