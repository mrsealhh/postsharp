param(
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$SkipPublish,
    [int]$StartupTimeoutSeconds = 60,
    [string]$PublishDirectory = "artifacts\aot-smoke"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\PostSharp\PostSharp.csproj"
$publishPath = Join-Path $root $PublishDirectory
$executable = Join-Path $publishPath "PostSharp.exe"

if (-not $SkipPublish) {
    if (Test-Path $publishPath) {
        Remove-Item $publishPath -Recurse -Force
    }

    & dotnet publish $project -c Release -r $RuntimeIdentifier -p:PublishAot=true `
        -p:SelfContained=true -p:UseOptimizedPublish=false -o $publishPath
    if ($LASTEXITCODE -ne 0) {
        throw "NativeAOT publish failed."
    }
}

if (-not (Test-Path $executable)) {
    throw "Native executable not found: $executable"
}

$running = Get-Process -Name PostSharp -ErrorAction SilentlyContinue
if ($running) {
    throw "Close the running PostSharp instance first: the single-instance guard would make this smoke drive the wrong process."
}

$dataDirectory = Join-Path $env:TEMP ("postsharp-aot-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
$env:POSTSHARP_DATA_DIR = $dataDirectory

function Start-App {
    $started = Start-Process -FilePath $executable -PassThru
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($started.HasExited) {
            throw "The native process exited during startup with code $($started.ExitCode); see $dataDirectory\logs."
        }

        foreach ($port in 5199..5210) {
            $candidate = "http://127.0.0.1:$port"
            try {
                $probe = Invoke-WebRequest -Uri "$candidate/api/health" -UseBasicParsing -TimeoutSec 2
                if ($probe.StatusCode -eq 200) {
                    if ($started.HasExited) {
                        throw "The started process exited and another instance owns $candidate; close every PostSharp window and retry."
                    }

                    return @{ Process = $started; BaseUrl = $candidate }
                }
            }
            catch {
                # Port not serving yet.
            }
        }

        Start-Sleep -Milliseconds 500
    }

    Stop-Process -Id $started.Id -Force -ErrorAction SilentlyContinue
    throw "The mock/MCP service did not start within $StartupTimeoutSeconds seconds."
}

function Stop-App {
    param([System.Diagnostics.Process]$Process)

    Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    Get-Process -Name PostSharp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1200
}

$script:requestId = 0

function Invoke-Mcp {
    param(
        [string]$Method,
        [hashtable]$Params
    )

    $script:requestId++
    $payload = @{
        jsonrpc = "2.0"
        id      = $script:requestId
        method  = $Method
        params  = $Params
    } | ConvertTo-Json -Depth 20 -Compress

    $response = Invoke-RestMethod -Uri $script:endpoint -Method Post `
        -ContentType "application/json" -Body ([System.Text.Encoding]::UTF8.GetBytes($payload)) `
        -TimeoutSec 60
    if ($response.error) {
        throw "MCP $Method failed: $($response.error.message)"
    }

    return $response.result
}

function Invoke-Tool {
    param(
        [string]$Name,
        [hashtable]$Arguments
    )

    $result = Invoke-Mcp -Method "tools/call" -Params @{ name = $Name; arguments = $Arguments }
    $text = $result.content[0].text
    if ($result.isError) {
        throw "MCP tool $Name failed: $text"
    }

    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    return $text | ConvertFrom-Json
}

$checks = New-Object System.Collections.Generic.List[string]
$failure = $null
$app = $null
try {
    $app = Start-App
    $script:endpoint = "$($app.BaseUrl)/mcp"

    Invoke-Mcp -Method "initialize" -Params @{
        protocolVersion = "2025-06-18"
        capabilities    = @{}
        clientInfo      = @{ name = "postsharp-aot-smoke"; version = "1.0" }
    } | Out-Null

    $tools = Invoke-Mcp -Method "tools/list" -Params @{}
    $checks.Add("tools/list -> $($tools.tools.Count) tools")

    $collections = Invoke-Tool -Name "list_collections" -Arguments @{}
    $checks.Add("list_collections -> $($collections.Count) collections")

    $environments = Invoke-Tool -Name "list_environments" -Arguments @{}
    $checks.Add("list_environments -> $($environments.Count) environments")

    $suites = Invoke-Tool -Name "list_suites" -Arguments @{}
    $checks.Add("list_suites -> $($suites.Count) suites")

    $history = Invoke-Tool -Name "list_history" -Arguments @{ limit = 5 }
    $checks.Add("list_history -> $($history.Count) entries")

    $audits = Invoke-Tool -Name "list_audit" -Arguments @{ limit = 5 }
    $checks.Add("list_audit -> $($audits.Count) entries")

    $settings = Invoke-Tool -Name "get_settings" -Arguments @{}
    $checks.Add("get_settings -> theme $($settings.theme)")

    $storage = Invoke-Tool -Name "get_storage_config" -Arguments @{}
    $checks.Add("get_storage_config -> $($storage.profiles.Count) profiles")

    $trash = Invoke-Tool -Name "list_collection_trash" -Arguments @{}
    $checks.Add("list_collection_trash -> $($trash.Count) items")

    $export = Invoke-Tool -Name "export_workspace" -Arguments @{}
    $checks.Add("export_workspace -> $($export.content.Length) chars")

    $created = Invoke-Tool -Name "create_request" -Arguments @{
        name   = "AOT smoke request"
        method = "GET"
        url    = "$($app.BaseUrl)/api/health"
    }
    $checks.Add("create_request -> $($created.id)")

    $sent = Invoke-Tool -Name "send_request" -Arguments @{ id = $created.id }
    if ($sent.statusCode -ne 200) {
        throw "send_request expected 200 but got $($sent.statusCode)."
    }
    $checks.Add("send_request -> HTTP $($sent.statusCode)")

    $curl = Invoke-Tool -Name "get_curl" -Arguments @{ id = $created.id }
    $checks.Add("get_curl -> $($curl.curl.Length) chars")

    $suite = Invoke-Tool -Name "create_suite" -Arguments @{
        name  = "AOT smoke suite"
        steps = @(@{ requestId = $created.id; expectedStatus = 200 })
    }
    $checks.Add("create_suite -> $($suite.id)")

    $run = Invoke-Tool -Name "run_suite" -Arguments @{ id = $suite.id }
    if ($run.failed -ne 0) {
        throw "run_suite expected 0 failures but got $($run.failed)."
    }
    $checks.Add("run_suite -> passed $($run.passed)/$($run.total)")

    Invoke-Tool -Name "delete_suite" -Arguments @{ id = $suite.id } | Out-Null
    Invoke-Tool -Name "delete_request" -Arguments @{ id = $created.id } | Out-Null
    $checks.Add("delete_suite / delete_request -> cleaned up")

    $originalFontSize = $settings.codeFontSize
    $probeFontSize = "13"
    if ($originalFontSize -eq $probeFontSize) {
        $probeFontSize = "12"
    }

    $updated = Invoke-Tool -Name "update_settings" -Arguments @{ codeFontSize = $probeFontSize }
    if ($updated.codeFontSize -ne $probeFontSize) {
        throw "update_settings did not apply codeFontSize."
    }
    $checks.Add("update_settings -> codeFontSize $probeFontSize")

    Stop-App -Process $app.Process
    $app = Start-App
    $script:endpoint = "$($app.BaseUrl)/mcp"
    Invoke-Mcp -Method "initialize" -Params @{
        protocolVersion = "2025-06-18"
        capabilities    = @{}
        clientInfo      = @{ name = "postsharp-aot-smoke"; version = "1.0" }
    } | Out-Null

    $reloaded = Invoke-Tool -Name "get_settings" -Arguments @{}
    if ($reloaded.codeFontSize -ne $probeFontSize) {
        throw "settings did not survive a restart: codeFontSize is $($reloaded.codeFontSize)."
    }
    $checks.Add("restart -> settings persisted")

    Invoke-Tool -Name "update_settings" -Arguments @{ codeFontSize = $originalFontSize } | Out-Null
}
catch {
    $failure = $_.Exception.Message
}
finally {
    if ($app) {
        Stop-App -Process $app.Process
    }
}

$logDirectory = Join-Path $dataDirectory "logs"
$errors = @()
if (Test-Path $logDirectory) {
    $errors = Get-ChildItem $logDirectory -Filter "*.log" -Recurse |
        Select-String -Pattern '\[ERR\]|Reflection-based serialization|NotSupportedException'
}

$checks | ForEach-Object { Write-Host "  [ok] $_" }

if ($failure) {
    Write-Host "  [failed] $failure" -ForegroundColor Red
}

if ($errors) {
    Write-Host "  [failed] the log contains:" -ForegroundColor Red
    $errors | Select-Object -First 20 | ForEach-Object { Write-Host "    $($_.Line)" }
}

if ($failure -or $errors) {
    Write-Host "Data directory kept for inspection: $dataDirectory" -ForegroundColor Yellow
    exit 1
}

Write-Host "NativeAOT smoke passed: $($checks.Count) checks, no errors in the log." -ForegroundColor Green
Remove-Item $dataDirectory -Recurse -Force -ErrorAction SilentlyContinue
exit 0
