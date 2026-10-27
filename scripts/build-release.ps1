param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$Version = $Version.Trim().TrimStart("v", "V")
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "PostSharp-$Version"
$zip = Join-Path $artifacts "PostSharp-win-x64.zip"
$checksum = "$zip.sha256"

if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}

# Build the default four plugins (also copied into the app) and the optional OLAP plugin zips.
& (Join-Path $PSScriptRoot "build-storage-plugins.ps1") -Configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "storage plugin build failed."
}

dotnet publish (Join-Path $root "src\PostSharp\PostSharp.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:Version=$Version `
    -o $publish

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

# The application only enables updates when this marker is present.
New-Item -ItemType File -Path (Join-Path $publish ".postsharp-update-enabled") -Force | Out-Null
Get-ChildItem $publish -Filter *.pdb -Recurse -Force | Remove-Item -Force

if (Test-Path $zip) {
    Remove-Item $zip -Force
}

$files = Get-ChildItem $publish -Force | Select-Object -ExpandProperty FullName
Compress-Archive -Path $files -DestinationPath $zip -Force

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $checksum -Value "$hash  PostSharp-win-x64.zip" -Encoding ascii

Write-Host "Package: $zip"
Write-Host "SHA256:  $checksum"
Write-Host "Next: create GitHub Release tag v$Version and upload both files."
