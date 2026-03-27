param(
    [string]$FeedDirectory,
    [string]$ShareName = 'ME_ACS_SQL_Patcher_Updates'
)

$FeedDirectory = if ([string]::IsNullOrWhiteSpace($FeedDirectory)) {
    Join-Path $PSScriptRoot 'feed'
} else {
    $FeedDirectory
}

if (-not (Test-Path $FeedDirectory)) {
    throw "Feed directory not found: $FeedDirectory"
}

Write-Host "Current feed folder: $FeedDirectory"

if (Get-Command Get-SmbShare -ErrorAction SilentlyContinue) {
    $existing = Get-SmbShare -Name $ShareName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "Existing share found: $($existing.Name) -> $($existing.Path)"
        Write-Host "Use UNC path: \\$env:COMPUTERNAME\$ShareName"
    } else {
        Write-Host "Share '$ShareName' is not configured yet."
    }
} else {
    Write-Host "Get-SmbShare is not available on this machine. Use the net share command below."
}

$escapedPath = $FeedDirectory.Replace('"', '""')
Write-Host "Recommended command:"
Write-Host "  net share $ShareName=""$escapedPath"" /GRANT:Everyone,READ"
Write-Host "After sharing, set App Update Feed in the installed app to \\$env:COMPUTERNAME\$ShareName"
