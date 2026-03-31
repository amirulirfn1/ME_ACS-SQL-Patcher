<#
.SYNOPSIS
    Sets up the ME ACS SQL Patcher update server to auto-start on login.
    Uses Python (already installed). No Administrator required.

.USAGE
    Double-click -> Run with PowerShell
    To remove: Delete shortcut from shell:startup
#>

$Port         = 39000
$ServePy      = Join-Path $PSScriptRoot "serve.py"
$FeedPath     = Join-Path $PSScriptRoot "feed"
$StartupDir   = [Environment]::GetFolderPath("Startup")
$ShortcutPath = Join-Path $StartupDir "MagEtegra Update Server.lnk"

if (-not (Test-Path $ServePy)) {
    Write-Host "ERROR: serve.py not found." -ForegroundColor Red
    pause; exit 1
}

if (-not (Test-Path $FeedPath)) {
    Write-Host "ERROR: Feed folder not found at: $FeedPath" -ForegroundColor Red
    pause; exit 1
}

$pythonCmd = Get-Command python -ErrorAction SilentlyContinue
$python = if ($pythonCmd) { $pythonCmd.Source } else { $null }
if (-not $python) {
    Write-Host "ERROR: Python not found. Install Python from python.org." -ForegroundColor Red
    pause; exit 1
}

# Register startup shortcut
Write-Host "Registering auto-start..."
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath       = "pythonw.exe"
$Shortcut.Arguments        = "`"$ServePy`" $Port"
$Shortcut.WorkingDirectory = $PSScriptRoot
$Shortcut.Description      = "MagEtegra update feed server"
$Shortcut.Save()
Write-Host "  Auto-start registered (runs on every login)."

# Start immediately (pythonw = no console window)
Write-Host "Starting server now..."
Start-Process "pythonw.exe" -ArgumentList "`"$ServePy`" $Port"
Start-Sleep -Seconds 2

# Get local IP
$localIP = (Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.InterfaceAlias -notlike "*Loopback*" -and $_.IPAddress -notlike "169.*" } |
    Select-Object -First 1).IPAddress

Write-Host ""
Write-Host "======================================"
Write-Host "  Done! Server is running."
Write-Host "======================================"
Write-Host ""
Write-Host "  Set this URL in Admin Tools -> App Update Feed"
Write-Host "  on ALL PCs (including this one):"
Write-Host ""
Write-Host "  http://${localIP}:${Port}" -ForegroundColor Green
Write-Host ""
Write-Host "  Auto-starts on every login. No admin needed."
Write-Host "======================================"
pause
