param(
    [Parameter(Mandatory = $true)]
    [string]$Version
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
$publish = Join-Path $artifacts "PostSharp-$Version-linux-x64"
$zip = Join-Path $artifacts "PostSharp-linux-x64.zip"
$checksum = "$zip.sha256"

if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}

dotnet publish (Join-Path $root "src\PostSharp\PostSharp.csproj") `
    -c Release `
    -r linux-x64 `
    --self-contained true `
    -p:Version=$Version `
    -o $publish

if ($LASTEXITCODE -ne 0) {
    throw "linux publish failed."
}

Get-ChildItem $publish -Filter *.pdb -Recurse -Force | Remove-Item -Force
Remove-PlatformFolders (Join-Path $publish "plugins") "^(win|osx)"

$runScript = @'
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
chmod +x PostSharp
exec ./PostSharp "$@"
'@
Set-Content -Path (Join-Path $publish "run.sh") -Value $runScript -Encoding ascii -NoNewline

if (Test-Path $zip) {
    Remove-Item $zip -Force
}

$files = Get-ChildItem $publish -Force | Select-Object -ExpandProperty FullName
Compress-Archive -Path $files -DestinationPath $zip -Force

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $checksum -Value "$hash  PostSharp-linux-x64.zip" -Encoding ascii

Write-Host "Package: $zip"
Write-Host "SHA256:  $checksum"
