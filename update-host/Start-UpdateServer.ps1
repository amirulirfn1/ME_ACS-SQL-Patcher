param(
    [int]$Port = 80,
    [string]$FeedDirectory = ""
)

$FeedDirectory = if ([string]::IsNullOrWhiteSpace($FeedDirectory)) {
    Join-Path $PSScriptRoot 'feed'
} else {
    $FeedDirectory
}

if (-not (Test-Path $FeedDirectory)) {
    throw "Feed directory not found: $FeedDirectory. Run .\build\release.ps1 first."
}

$localIP = (Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.InterfaceAlias -notlike "*Loopback*" -and $_.IPAddress -notlike "169.*" } |
    Select-Object -First 1).IPAddress

if ([string]::IsNullOrWhiteSpace($localIP)) {
    throw "Could not determine local IP address. Check your network connection."
}

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://+:$Port/")

try {
    $listener.Start()
} catch {
    throw "Could not start HTTP server on port $Port. Try running as Administrator or use a different port: .\Start-UpdateServer.ps1 -Port 9090"
}

Write-Host ""
Write-Host "Update feed server is running."
Write-Host ""
Write-Host "Set App Update Feed in Admin Tools to:"
Write-Host "  http://${localIP}:${Port}"
Write-Host ""
Write-Host "Other PCs on the same network can reach this URL."
Write-Host "Keep this window open while updates are being checked."
Write-Host "Press Ctrl+C to stop the server."
Write-Host ""

$mimeTypes = @{
    '.json' = 'application/json'
    '.nupkg' = 'application/octet-stream'
    '.zip'   = 'application/zip'
    '.exe'   = 'application/octet-stream'
}

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $request  = $context.Request
        $response = $context.Response

        $localPath = $request.Url.LocalPath.TrimStart('/')
        $filePath  = Join-Path $FeedDirectory $localPath

        if ([string]::IsNullOrWhiteSpace($localPath)) {
            $files = Get-ChildItem -Path $FeedDirectory -File |
                ForEach-Object { "<li><a href='$($_.Name)'>$($_.Name)</a> ($([math]::Round($_.Length/1MB,1)) MB)</li>" }
            $html = "<html><body><h2>ME ACS SQL Patcher Update Feed</h2><ul>$($files -join '')</ul></body></html>"
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($html)
            $response.ContentType = 'text/html'
            $response.ContentLength64 = $bytes.Length
            $response.OutputStream.Write($bytes, 0, $bytes.Length)
        } elseif (Test-Path $filePath -PathType Leaf) {
            $ext = [System.IO.Path]::GetExtension($filePath)
            $response.ContentType = if ($mimeTypes.ContainsKey($ext)) { $mimeTypes[$ext] } else { 'application/octet-stream' }
            $bytes = [System.IO.File]::ReadAllBytes($filePath)
            $response.ContentLength64 = $bytes.Length
            $response.OutputStream.Write($bytes, 0, $bytes.Length)
            Write-Host "  Served: $localPath"
        } else {
            $response.StatusCode = 404
        }

        $response.OutputStream.Close()
    }
} finally {
    $listener.Stop()
    Write-Host "Server stopped."
}
