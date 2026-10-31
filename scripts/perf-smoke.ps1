param(
    [string]$Executable = "",
    [int]$StartupTimeoutSeconds = 60,
    [int]$IdleSeconds = 10,
    [switch]$Json
)

$ErrorActionPreference = "Stop"

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
$failure = $null
$coldStartMs = 0
$startupMemoryMb = 0
$idleMemoryMb = 0
$url = ""

function Test-Port {
    param([int]$Port)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync("127.0.0.1", $Port)
        return $task.Wait(150) -and $client.Connected
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

try {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $Executable -PassThru
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) {
            throw "Process exited during startup with code $($process.ExitCode); see $dataDirectory\logs."
        }

        foreach ($port in 5199..5210) {
            if (-not (Test-Port $port)) {
                continue
            }

            $candidate = "http://127.0.0.1:$port"
            try {
                $probe = Invoke-WebRequest -Uri "$candidate/api/health" -UseBasicParsing -TimeoutSec 1
                if ($probe.StatusCode -eq 200) {
                    $url = $candidate
                    $ready = $true
                    break
                }
            }
            catch {
                # Port is open but the health endpoint is not serving yet.
            }
        }

        if ($ready) {
            break
        }

        Start-Sleep -Milliseconds 200
    }

    if (-not $ready) {
        throw "The service did not become ready within $StartupTimeoutSeconds seconds."
    }

    $coldStartMs = $timer.ElapsedMilliseconds
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
    }

    Start-Sleep -Milliseconds 500
}

$result = [pscustomobject]@{
    Executable     = $Executable
    Url            = $url
    ColdStartMs    = $coldStartMs
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
if ($idleMemoryMb -gt 150) {
    Write-Host "Idle memory missed the 150MB target." -ForegroundColor Yellow
}
exit 0
