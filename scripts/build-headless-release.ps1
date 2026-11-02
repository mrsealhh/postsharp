param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$RuntimeIdentifier = "linux-x64",
    [string]$AppDirectory = "",
    [string]$CliDirectory = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "zip-directory.ps1")

$Version = $Version.Trim().TrimStart("v", "V")
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$platform = if ($RuntimeIdentifier -like "linux*") { "linux" } elseif ($RuntimeIdentifier -like "osx*") { "macos" } else { "windows" }

if ([string]::IsNullOrWhiteSpace($AppDirectory)) {
    $AppDirectory = Join-Path $artifacts "PostSharp-$Version-$RuntimeIdentifier"
}

if ([string]::IsNullOrWhiteSpace($CliDirectory)) {
    $CliDirectory = Join-Path $artifacts "PostSharp.Cli-$Version-$RuntimeIdentifier"
}

if (-not (Test-Path (Join-Path $AppDirectory "PostSharp"))) {
    throw "缺少应用发布目录：$AppDirectory。请先运行 build-$platform-release.ps1。"
}

if (-not (Test-Path (Join-Path $CliDirectory "postsharp-cli"))) {
    & (Join-Path $PSScriptRoot "build-cli-release.ps1") `
        -Version $Version `
        -RuntimeIdentifier $RuntimeIdentifier `
        -SelfContained
    if ($LASTEXITCODE -ne 0) {
        throw "CLI publish failed."
    }
}

$bundle = Join-Path $artifacts "PostSharp-headless-$Version-$RuntimeIdentifier"
if (Test-Path $bundle) {
    Remove-Item $bundle -Recurse -Force
}

New-Item -ItemType Directory -Path $bundle -Force | Out-Null
Get-ChildItem $AppDirectory -Force | Copy-Item -Destination $bundle -Recurse -Force

$cliTarget = Join-Path $bundle "cli"
New-Item -ItemType Directory -Path $cliTarget -Force | Out-Null
Get-ChildItem $CliDirectory -Force | Copy-Item -Destination $cliTarget -Recurse -Force

$readme = @'
# PostSharp headless bundle

- `PostSharp`：无界面 MCP 服务（自包含 .NET 运行时）。
- `cli/postsharp-cli`：命令行客户端，直接运行流水线/套件。

启动服务：

```bash
xvfb-run -a ./PostSharp --headless &
```

等待 http://127.0.0.1:5199/api/health 返回 200 后，运行测试：

```bash
./cli/postsharp-cli pipeline run flow.yaml --format junit --copy-report test-results/postsharp.xml
```

桌面 Linux 环境可直接 `./PostSharp --headless`，无需 Xvfb。
'@
[IO.File]::WriteAllText(
    (Join-Path $bundle "README.md"),
    $readme.Replace("`r`n", "`n").Replace("`r", "`n"),
    (New-Object Text.UTF8Encoding($false))
)

$runScript = @'
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

if ! curl -fsS http://127.0.0.1:5199/api/health >/dev/null 2>&1; then
    if command -v xvfb-run >/dev/null 2>&1; then
        xvfb-run -a ./PostSharp --headless > postsharp.log 2>&1 &
    else
        ./PostSharp --headless > postsharp.log 2>&1 &
    fi

    for _ in $(seq 1 60); do
        if curl -fsS http://127.0.0.1:5199/api/health >/dev/null 2>&1; then
            break
        fi
        sleep 1
    done
fi

exec ./cli/postsharp-cli "$@"
'@
Set-PortableUnixScript -Path (Join-Path $bundle "postsharp-cli.sh") -Content $runScript

$zip = Join-Path $artifacts "PostSharp-headless-$RuntimeIdentifier.zip"
if (Test-Path $zip) {
    Remove-Item $zip -Force
}

New-PortableZip -SourceDirectory $bundle -DestinationPath $zip

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = "$zip.sha256"
Set-Content -Path $checksum -Value "$hash  $(Split-Path -Leaf $zip)" -Encoding ascii

Write-Host "Package: $zip"
Write-Host "SHA256:  $checksum"
