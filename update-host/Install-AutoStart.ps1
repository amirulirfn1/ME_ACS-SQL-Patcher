<#
.SYNOPSIS
    Registers the ME ACS SQL Patcher update server to auto-start on login.
    Uses port 8080 (no Administrator required).

.USAGE
    Double-click or right-click -> Run with PowerShell
    To remove: Delete the shortcut from shell:startup
#>

$TaskName   = "MagEtegra Update Server"
$ScriptPath = Join-Path $PSScriptRoot "Start-UpdateServer.ps1"
$FeedPath   = Join-Path $PSScriptRoot "feed"
$StartupDir = [Environment]::GetFolderPath("Startup")
$ShortcutPath = Join-Path $StartupDir "$TaskName.lnk"

if (-not (Test-Path $ScriptPath)) {
    Write-Error "Start-UpdateServer.ps1 not found at: $ScriptPath"
    pause
    exit 1
}

if (-not (Test-Path $FeedPath)) {
    Write-Error "Feed folder not found at: $FeedPath. Build a release first."
    pause
    exit 1
}

# Create shortcut in Windows startup folder (runs on every login, no admin needed)
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = "powershell.exe"
$Shortcut.Arguments  = "-NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$ScriptPath`" -Port 8080 -FeedDirectory `"$FeedPath`""
$Shortcut.WorkingDirectory = $PSScriptRoot
$Shortcut.Description = "MagEtegra update feed server"
$Shortcut.Save()

Write-Host ""
Write-Host "Auto-start registered in: $ShortcutPath"
Write-Host "Port: 8080"
Write-Host "Feed: $FeedPath"
Write-Host ""

# Start it now too
Write-Host "Starting server now..."
Start-Process "powershell.exe" -ArgumentList "-NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$ScriptPath`" -Port 8080 -FeedDirectory `"$FeedPath`""
Start-Sleep -Seconds 2

Write-Host "Done. Server is running on port 8080."
Write-Host "Update feed URL for other PCs:"

$localIP = (Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.InterfaceAlias -notlike "*Loopback*" -and $_.IPAddress -notlike "169.*" } |
    Select-Object -First 1).IPAddress

Write-Host ""
Write-Host "  http://${localIP}:8080"
Write-Host ""
Write-Host "Set this URL in Admin Tools -> App Update Feed on all PCs."
Write-Host ""
pause
