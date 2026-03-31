#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Registers the ME ACS SQL Patcher update server as a Windows Task Scheduler task.
    Runs automatically at system boot on port 80, no login required.

.USAGE
    Right-click Install-AutoStart.ps1 -> Run as Administrator
    To remove: schtasks /delete /tn "MagEtegra Update Server" /f
#>

$TaskName   = "MagEtegra Update Server"
$ScriptPath = Join-Path $PSScriptRoot "Start-UpdateServer.ps1"
$FeedPath   = Join-Path $PSScriptRoot "feed"
$LogPath    = Join-Path $PSScriptRoot "server.log"

if (-not (Test-Path $ScriptPath)) {
    Write-Error "Start-UpdateServer.ps1 not found at: $ScriptPath"
    exit 1
}

if (-not (Test-Path $FeedPath)) {
    Write-Error "Feed folder not found at: $FeedPath. Build a release first."
    exit 1
}

# Remove existing task if present
$existing = schtasks /query /tn $TaskName 2>$null
if ($existing) {
    Write-Host "Removing existing task..."
    schtasks /delete /tn $TaskName /f | Out-Null
}

$argument = "-NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass " +
            "-File `"$ScriptPath`" -Port 80 -FeedDirectory `"$FeedPath`" " +
            "*>> `"$LogPath`""

$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument $argument

$trigger = New-ScheduledTaskTrigger -AtStartup

$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Hours 0) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable

$principal = New-ScheduledTaskPrincipal `
    -UserId "SYSTEM" `
    -LogonType ServiceAccount `
    -RunLevel Highest

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description "Serves the MagEtegra update feed on port 80 for Velopack auto-updates." | Out-Null

Write-Host ""
Write-Host "Task registered: '$TaskName'"
Write-Host "Runs as: SYSTEM (no login needed, survives reboots)"
Write-Host "Port: 80"
Write-Host "Feed: $FeedPath"
Write-Host "Log: $LogPath"
Write-Host ""
Write-Host "Starting now..."
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 2

$state = (Get-ScheduledTask -TaskName $TaskName).State
Write-Host "Task state: $state"
Write-Host ""
Write-Host "Done. The server will start automatically on every boot."
Write-Host "To stop: schtasks /end /tn `"$TaskName`""
Write-Host "To remove: schtasks /delete /tn `"$TaskName`" /f"
