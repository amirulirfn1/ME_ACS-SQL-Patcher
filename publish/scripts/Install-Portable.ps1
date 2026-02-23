param(
    [string]$SourceDir = "",
    [string]$InstallRoot = "",
    [switch]$NoDesktopShortcut,
    [switch]$NoStartMenuShortcut,
    [switch]$NoLaunch
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $SourceDir = Join-Path $repoRoot "publish"
}

if (-not (Test-Path $SourceDir)) {
    throw "SourceDir not found: $SourceDir"
}

$sourceExe = Join-Path $SourceDir "ME_ACS_SQL_Patcher.exe"
if (-not (Test-Path $sourceExe)) {
    throw "ME_ACS_SQL_Patcher.exe not found in source directory: $SourceDir"
}

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path $env:LOCALAPPDATA "MagDbPatcher"
}

$appInstallDir = Join-Path $InstallRoot "app"
$targetExe = Join-Path $appInstallDir "ME_ACS_SQL_Patcher.exe"

Write-Host "Installing ME_ACS SQL Patcher..."
Write-Host "Source: $SourceDir"
Write-Host "Target: $appInstallDir"

if (Test-Path $appInstallDir) {
    Remove-Item -Path $appInstallDir -Recurse -Force
}

New-Item -ItemType Directory -Path $appInstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $SourceDir "*") -Destination $appInstallDir -Recurse -Force

$wsh = New-Object -ComObject WScript.Shell

if (-not $NoDesktopShortcut) {
    $desktopShortcutPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "ME_ACS SQL Patcher.lnk"
    $desktopShortcut = $wsh.CreateShortcut($desktopShortcutPath)
    $desktopShortcut.TargetPath = $targetExe
    $desktopShortcut.WorkingDirectory = $appInstallDir
    $desktopShortcut.IconLocation = "$targetExe,0"
    $desktopShortcut.Save()
    Write-Host "Desktop shortcut created: $desktopShortcutPath"
}

if (-not $NoStartMenuShortcut) {
    $startMenuDir = Join-Path ([Environment]::GetFolderPath("Programs")) "ME_ACS SQL Patcher"
    New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null
    $startMenuShortcutPath = Join-Path $startMenuDir "ME_ACS SQL Patcher.lnk"
    $startMenuShortcut = $wsh.CreateShortcut($startMenuShortcutPath)
    $startMenuShortcut.TargetPath = $targetExe
    $startMenuShortcut.WorkingDirectory = $appInstallDir
    $startMenuShortcut.IconLocation = "$targetExe,0"
    $startMenuShortcut.Save()
    Write-Host "Start menu shortcut created: $startMenuShortcutPath"
}

Write-Host "Install complete."
Write-Host "User settings and imported patches are preserved under: $InstallRoot"

if (-not $NoLaunch) {
    Start-Process -FilePath $targetExe -WorkingDirectory $appInstallDir
}
