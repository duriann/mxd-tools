param([switch]$NoBrowser)

$ErrorActionPreference = "Stop"
$serverRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$listener = $null
$port = $null

foreach ($candidatePort in 8765..8775) {
    try {
        $candidate = [System.Net.Sockets.TcpListener]::new(
            [System.Net.IPAddress]::Loopback,
            $candidatePort
        )
        $candidate.Start()
        $listener = $candidate
        $port = $candidatePort
        break
    }
    catch {
        if ($candidate) {
            $candidate.Stop()
        }
    }
}

if (-not $listener) {
    Write-Error "No free local port was found in 8765-8775."
    exit 1
}

$url = "http://127.0.0.1:$port/index.html"
Write-Host "Captcha detector is running at $url"
Write-Host "Keep this window open. Press Ctrl+C or close it to stop."
if (-not $NoBrowser) {
    Start-Process $url
}

$rootPrefix = [System.IO.Path]::GetFullPath($serverRoot).TrimEnd('\') + '\'

try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        # Browsers can preconnect without sending a request. Bound the read
        # so an idle connection cannot block this single-client server.
        $client.ReceiveTimeout = 3000
        $client.SendTimeout = 15000
        try {
            $stream = $client.GetStream()
            $reader = [System.IO.StreamReader]::new(
                $stream,
                [System.Text.Encoding]::ASCII,
                $false,
                1024,
                $true
            )
            $requestLine = $reader.ReadLine()
            while ($true) {
                $line = $reader.ReadLine()
                if ([string]::IsNullOrEmpty($line)) {
                    break
                }
            }

            $status = "200 OK"
            $contentType = "application/octet-stream"
            $body = [byte[]]::new(0)
            $parts = if ($requestLine) { $requestLine.Split(' ') } else { @() }

            if ($parts.Count -lt 2 -or $parts[0] -ne "GET") {
                $status = "400 Bad Request"
                $body = [System.Text.Encoding]::UTF8.GetBytes("Bad request")
                $contentType = "text/plain; charset=utf-8"
            }
            else {
                $requestPath = $parts[1].Split('?')[0]
                $relativePath = [System.Uri]::UnescapeDataString($requestPath).TrimStart('/')
                if (-not $relativePath) {
                    $relativePath = "index.html"
                }
                $relativePath = $relativePath.Replace(
                    '/',
                    [System.IO.Path]::DirectorySeparatorChar
                )
                $fullPath = [System.IO.Path]::GetFullPath(
                    [System.IO.Path]::Combine($serverRoot, $relativePath)
                )

                if (
                    -not $fullPath.StartsWith(
                        $rootPrefix,
                        [System.StringComparison]::OrdinalIgnoreCase
                    ) -or
                    -not [System.IO.File]::Exists($fullPath)
                ) {
                    $status = "404 Not Found"
                    $body = [System.Text.Encoding]::UTF8.GetBytes("Not found")
                    $contentType = "text/plain; charset=utf-8"
                }
                else {
                    $body = [System.IO.File]::ReadAllBytes($fullPath)
                    $contentType = switch (
                        [System.IO.Path]::GetExtension($fullPath).ToLowerInvariant()
                    ) {
                        ".html" { "text/html; charset=utf-8" }
                        ".js" { "application/javascript; charset=utf-8" }
                        ".css" { "text/css; charset=utf-8" }
                        ".png" { "image/png" }
                        default { "application/octet-stream" }
                    }
                }
            }

            $header = (
                "HTTP/1.1 $status`r`n" +
                "Content-Type: $contentType`r`n" +
                "Content-Length: $($body.Length)`r`n" +
                "Cache-Control: no-store`r`n" +
                "Connection: close`r`n`r`n"
            )
            $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($header)
            $stream.Write($headerBytes, 0, $headerBytes.Length)
            if ($body.Length) {
                $stream.Write($body, 0, $body.Length)
            }
            $stream.Flush()
        }
        catch {
            Write-Warning $_.Exception.Message
        }
        finally {
            $client.Close()
        }
    }
}
finally {
    $listener.Stop()
}
