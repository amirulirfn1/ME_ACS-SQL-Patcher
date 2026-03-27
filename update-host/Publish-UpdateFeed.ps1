param(
    [string]$InstallerSource,
    [string]$FeedDirectory
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$InstallerSource = if ([string]::IsNullOrWhiteSpace($InstallerSource)) {
    Join-Path $repoRoot 'dist\installer'
} else {
    $InstallerSource
}

$FeedDirectory = if ([string]::IsNullOrWhiteSpace($FeedDirectory)) {
    Join-Path $PSScriptRoot 'feed'
} else {
    $FeedDirectory
}

if (-not (Test-Path $InstallerSource)) {
    throw "Installer source not found: $InstallerSource"
}

New-Item -ItemType Directory -Path $FeedDirectory -Force | Out-Null

$feedFiles = Get-ChildItem -Path $FeedDirectory -File -ErrorAction SilentlyContinue
foreach ($file in $feedFiles) {
    if ($file.Name -ne '.gitkeep') {
        Remove-Item -Path $file.FullName -Force
    }
}

$installerFiles = Get-ChildItem -Path $InstallerSource -File
if (-not $installerFiles) {
    throw "No installer artifacts were found in $InstallerSource. Run .\build\release.ps1 first."
}

foreach ($file in $installerFiles) {
    Copy-Item -Path $file.FullName -Destination $FeedDirectory -Force
}

Write-Host "Copied $($installerFiles.Count) file(s) into $FeedDirectory."
Write-Host "Next step: share that folder and point the installed app's App Update Feed setting to the resulting UNC path."
