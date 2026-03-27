param(
    [string]$SourceDir = "",
    [string]$InstallRoot = "",
    [switch]$NoDesktopShortcut,
    [switch]$NoStartMenuShortcut,
    [switch]$NoLaunch
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildRoot = Split-Path -Parent $scriptDir
$repoRoot = Split-Path -Parent $buildRoot

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $SourceDir = Join-Path $repoRoot "output"
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

$userDataDir = $InstallRoot
$appInstallDir = Join-Path $InstallRoot "app"
$targetExe = Join-Path $appInstallDir "ME_ACS_SQL_Patcher.exe"

Write-Host "Installing ME_ACS SQL Patcher..."
Write-Host "Source: $SourceDir"
Write-Host "Target: $appInstallDir"

function Copy-TreeIfMissing {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [switch]$OnlyIfDestinationEmpty
    )

    if (-not (Test-Path $Source)) {
        return
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    if ($OnlyIfDestinationEmpty -and (Get-ChildItem -Force $Destination | Select-Object -First 1)) {
        return
    }

    Get-ChildItem -Path $Source -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($Source.Length).TrimStart('\', '/')
        $destinationFile = Join-Path $Destination $relative
        $destinationDir = Split-Path -Parent $destinationFile
        if (-not (Test-Path $destinationDir)) {
            New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
        }

        if (-not (Test-Path $destinationFile)) {
            Copy-Item -Path $_.FullName -Destination $destinationFile
        }
    }
}

function Copy-FileIfMissing {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path $Source) -or (Test-Path $Destination)) {
        return
    }

    $destinationDir = Split-Path -Parent $Destination
    if (-not (Test-Path $destinationDir)) {
        New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
    }

    Copy-Item -Path $Source -Destination $Destination
}

if (Test-Path $appInstallDir) {
    Remove-Item -Path $appInstallDir -Recurse -Force
}

New-Item -ItemType Directory -Path $appInstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $SourceDir "*") -Destination $appInstallDir -Recurse -Force

Copy-FileIfMissing -Source (Join-Path $SourceDir "settings.json") -Destination (Join-Path $userDataDir "settings.json")
Copy-TreeIfMissing -Source (Join-Path $SourceDir "logs") -Destination (Join-Path $userDataDir "logs")
Copy-TreeIfMissing -Source (Join-Path $SourceDir "backups") -Destination (Join-Path $userDataDir "backups")
Copy-TreeIfMissing -Source (Join-Path $SourceDir "patches") -Destination (Join-Path $userDataDir "patches") -OnlyIfDestinationEmpty

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
Write-Host "User settings, logs, backups, and the active patch library live under: $userDataDir"
Write-Host "Legacy portable data from the source folder was copied forward when missing."

if (-not $NoLaunch) {
    Start-Process -FilePath $targetExe -WorkingDirectory $appInstallDir
}
