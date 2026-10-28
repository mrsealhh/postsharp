param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$RuntimeIdentifier = "osx-arm64"
)

$ErrorActionPreference = "Stop"

function Remove-PlatformFolders {
    param(
        [string]$Root,
        [string]$Pattern
    )

    if (-not (Test-Path $Root)) {
        return
    }

    Get-ChildItem $Root -Directory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $Pattern } |
        Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

$Version = $Version.Trim().TrimStart("v", "V")
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "PostSharp-$Version-$RuntimeIdentifier"
$zip = Join-Path $artifacts "PostSharp-$RuntimeIdentifier.zip"
$checksum = "$zip.sha256"

if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}

dotnet publish (Join-Path $root "src\PostSharp\PostSharp.csproj") `
    -c Release `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:Version=$Version `
    -o $publish

if ($LASTEXITCODE -ne 0) {
    throw "macOS publish failed."
}

$cliPublish = Join-Path $publish "cli"
dotnet publish (Join-Path $root "src\PostSharp.Cli\PostSharp.Cli.csproj") `
    -c Release `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:Version=$Version `
    -o $cliPublish

if ($LASTEXITCODE -ne 0) {
    throw "CLI publish failed."
}

$cliRunScript = @'
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
chmod +x postsharp-cli
exec ./postsharp-cli "$@"
'@
Set-Content -Path (Join-Path $cliPublish "run.sh") -Value $cliRunScript -Encoding ascii -NoNewline

Get-ChildItem $publish -Filter *.pdb -Recurse -Force | Remove-Item -Force
Remove-PlatformFolders (Join-Path $publish "plugins") "^(win|linux)"

$runScript = @'
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
chmod +x PostSharp
exec ./PostSharp "$@"
'@
Set-Content -Path (Join-Path $publish "run.command") -Value $runScript -Encoding ascii -NoNewline

if (Test-Path $zip) {
    Remove-Item $zip -Force
}

$files = Get-ChildItem $publish -Force | Select-Object -ExpandProperty FullName
Compress-Archive -Path $files -DestinationPath $zip -Force

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $checksum -Value "$hash  PostSharp-$RuntimeIdentifier.zip" -Encoding ascii

Write-Host "Package: $zip"
Write-Host "SHA256:  $checksum"
