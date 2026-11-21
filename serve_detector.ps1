param([switch]$NoBrowser)

$ErrorActionPreference = 'Stop'
$serverRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $instanceId = [System.BitConverter]::ToString(
        $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($serverRoot.ToUpperInvariant()))
    ).Replace('-', '').ToLowerInvariant()
}
finally {
    $sha.Dispose()
}

function Find-RunningPort {
    $network = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties()
    $listeningPorts = $network.GetActiveTcpListeners().Port
    foreach ($candidatePort in 8765..8775) {
        if ($candidatePort -notin $listeningPorts) { continue }
        $request = [System.Net.WebRequest]::CreateHttp(
            "http://127.0.0.1:$candidatePort/.well-known/maple-monitor")
        $request.Proxy = $null
        $request.Timeout = 300
        $request.ReadWriteTimeout = 300
        try {
            $response = $request.GetResponse()
            try {
                if ($response.Headers['X-Maple-Monitor-Instance'] -eq $instanceId) {
                    return $candidatePort
                }
            }
            finally {
                $response.Dispose()
            }
        }
        catch [System.Net.WebException] {
            if ($_.Exception.Response) { $_.Exception.Response.Dispose() }
        }
    }
}

$mutex = [System.Threading.Mutex]::new($false, "Local\MapleMonitorLaunch-$instanceId")
$hasLock = $false
$server = $null
try {
    try {
        $hasLock = $mutex.WaitOne(15000)
    }
    catch [System.Threading.AbandonedMutexException] {
        $hasLock = $true
    }
    if (-not $hasLock) { throw 'Another launcher is starting. Please try again.' }

    $port = Find-RunningPort
    if (-not $port) {
        Add-Type -Path (Join-Path $serverRoot 'LocalStaticServer.cs') -ReferencedAssemblies 'System.dll','System.Web.Extensions.dll'
        $server = [MapleMonitor.LocalStaticServer]::new($serverRoot, $instanceId)
        $port = $server.Start(8765, 8775)
    }
}
finally {
    if ($hasLock) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}

$url = "http://127.0.0.1:$port/index.html"
try {
    if ($server) {
        Write-Host "Captcha detector is running at $url"
        Write-Host 'Keep this window open. Press Ctrl+C or close it to stop.'
    }
    else {
        Write-Host "Using the running Captcha detector at $url"
    }
    if (-not $NoBrowser) { Start-Process $url }
    if ($server) {
        while ($true) { Start-Sleep -Seconds 1 }
    }
}
finally {
    if ($server) { $server.Dispose() }
}
