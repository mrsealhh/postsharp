param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$Version = $Version.Trim().TrimStart("v", "V")
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "PostSharp.Cli-$Version-$RuntimeIdentifier"
$zip = Join-Path $artifacts "PostSharp.Cli-$RuntimeIdentifier.zip"
$checksum = "$zip.sha256"

if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }
dotnet publish (Join-Path $root "src\PostSharp.Cli\PostSharp.Cli.csproj") `
    -c Release `
    -r $RuntimeIdentifier `
    --self-contained $selfContainedValue `
    -p:Version=$Version `
    -o $publish

if ($LASTEXITCODE -ne 0) {
    throw "CLI publish failed."
}

Get-ChildItem $publish -Filter *.pdb -Recurse -Force | Remove-Item -Force

$runScript = @'
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
chmod +x postsharp-cli
exec ./postsharp-cli "$@"
'@
Set-Content -Path (Join-Path $publish "run.sh") -Value $runScript -Encoding ascii -NoNewline

if (Test-Path $zip) {
    Remove-Item $zip -Force
}

$files = Get-ChildItem $publish -Force | Select-Object -ExpandProperty FullName
Compress-Archive -Path $files -DestinationPath $zip -Force

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $checksum -Value "$hash  PostSharp.Cli-$RuntimeIdentifier.zip" -Encoding ascii

Write-Host "Package: $zip"
Write-Host "SHA256:  $checksum"
