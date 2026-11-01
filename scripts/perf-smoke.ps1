param(
    [string]$Executable = "",
    [int]$StartupTimeoutSeconds = 60,
    [int]$IdleSeconds = 10,
    [switch]$Json
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = Join-Path $root "src/PostSharp/bin/Release/net10.0/win-x64/PostSharp.exe"
}

if (-not (Test-Path $Executable)) {
    throw "Executable not found: $Executable. Build or pass -Executable."
}

$running = Get-Process -Name PostSharp -ErrorAction SilentlyContinue
if ($running) {
    throw "Close the running PostSharp instance first: the single-instance guard would make this smoke drive the wrong process."
}

$dataDirectory = Join-Path ([IO.Path]::GetTempPath()) ("postsharp-perf-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
$env:POSTSHARP_DATA_DIR = $dataDirectory

$process = $null
$http = $null
$failure = $null
$coldStartMs = 0
$startupMemoryMb = 0
$idleMemoryMb = 0
$url = ""

try {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $Executable -PassThru
    $http = New-Object System.Net.Http.HttpClient
    $http.Timeout = [TimeSpan]::FromMilliseconds(500)
    $baseUrl = "http://127.0.0.1:5199"
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) {
            throw "Process exited during startup with code $($process.ExitCode); see $dataDirectory\logs."
        }

        try {
            $probe = $http.GetAsync("$baseUrl/api/health").GetAwaiter().GetResult()
            if ($probe.IsSuccessStatusCode) {
                $url = $baseUrl
                $ready = $true
                break
            }
        }
        catch {
            # The listener is not ready yet.
        }

        Start-Sleep -Milliseconds 50
    }

    if (-not $ready) {
        throw "The service did not become ready within $StartupTimeoutSeconds seconds."
    }

    $coldStartMs = $timer.ElapsedMilliseconds
    $mcpReadyMs = 0
    $payload =
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"perf-smoke","version":"1.0"}}}'
    $mcpDeadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $mcpDeadline) {
        $content = New-Object System.Net.Http.StringContent(
            $payload,
            [Text.Encoding]::UTF8,
            "application/json"
        )
        try {
            $response = $http.PostAsync("$url/mcp", $content).GetAwaiter().GetResult()
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ($text -match '"result"') {
                $mcpReadyMs = $timer.ElapsedMilliseconds
                break
            }
        }
        catch {
            # MCP is not attached to the mock server yet.
        }
        finally {
            $content.Dispose()
        }

        Start-Sleep -Milliseconds 50
    }

    $process.Refresh()
    $startupMemoryMb = [Math]::Round($process.PrivateMemorySize64 / 1MB, 1)
    Start-Sleep -Seconds $IdleSeconds
    $process.Refresh()
    $idleMemoryMb = [Math]::Round($process.PrivateMemorySize64 / 1MB, 1)
}
catch {
    $failure = $_.Exception.Message
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $exitDeadline = (Get-Date).AddSeconds(5)
        while (-not $process.HasExited -and (Get-Date) -lt $exitDeadline) {
            Start-Sleep -Milliseconds 100
            $process.Refresh()
        }
    }

    if ($http) {
        $http.Dispose()
    }

    Start-Sleep -Milliseconds 500
}

$result = [pscustomobject]@{
    Executable     = $Executable
    Url            = $url
    ColdStartMs    = $coldStartMs
    McpReadyMs     = $mcpReadyMs
    StartupMemoryMb = $startupMemoryMb
    IdleMemoryMb   = $idleMemoryMb
    IdleSeconds    = $IdleSeconds
    Failure        = $failure
}

if ($Json) {
    $result | ConvertTo-Json -Compress
}
else {
    $result | Format-List
}

if ($failure) {
    Write-Host "Data directory kept for inspection: $dataDirectory" -ForegroundColor Yellow
    exit 1
}

Remove-Item $dataDirectory -Recurse -Force -ErrorAction SilentlyContinue
if ($coldStartMs -gt 1500) {
    Write-Host "Cold start missed the 1.5s target." -ForegroundColor Yellow
}
if ($mcpReadyMs -le 0) {
    Write-Host "MCP did not answer initialize within 15s." -ForegroundColor Yellow
}
elseif ($mcpReadyMs -gt 1500) {
    Write-Host "MCP ready missed the 1.5s target." -ForegroundColor Yellow
}
if ($idleMemoryMb -gt 150) {
    Write-Host "Idle memory missed the 150MB target." -ForegroundColor Yellow
}
exit 0
