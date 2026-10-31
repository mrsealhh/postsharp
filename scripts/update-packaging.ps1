param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$WinZip = "artifacts/PostSharp-win-x64.zip",
    [string]$OsxZip = "artifacts/PostSharp-osx-arm64.zip"
)

$ErrorActionPreference = "Stop"

function Get-Sha256 {
    param([string]$Path)
    if (-not (Test-Path $Path)) {
        throw "Package not found: $Path"
    }

    return (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-TextFile {
    param(
        [string]$Path,
        [string]$Content
    )
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $normalized = $Content.Replace("`r`n", "`n")
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $normalized, $encoding)
}

$Version = $Version.Trim().TrimStart("v", "V")
$root = Split-Path -Parent $PSScriptRoot
$winZip = Join-Path $root $WinZip
$osxZip = Join-Path $root $OsxZip
$winHash = Get-Sha256 $winZip
$osxHash = Get-Sha256 $osxZip
$repository = "https://github.com/mrsealhh/postsharp"

$winget = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.singleton.1.10.0.schema.json
PackageIdentifier: Mrsealhh.PostSharp
PackageVersion: $Version
PackageLocale: en-US
Publisher: mrsealhh
PublisherUrl: $repository
PublisherSupportUrl: $repository/issues
PackageName: PostSharp
PackageUrl: $repository
License: MIT
LicenseUrl: $repository/blob/master/LICENSE
ShortDescription: Local API debugging workbench with MCP automation.
Description: PostSharp combines a Postman-style HTTP client with MCP tools, audit trails and regression suites, so an AI client and a person can drive the same local workspace.
Moniker: postsharp
Tags:
  - api
  - http
  - mcp
  - testing
  - automation
ManifestType: singleton
ManifestVersion: 1.10.0
Installers:
  - Architecture: x64
    InstallerType: zip
    NestedInstallerType: portable
    NestedInstallerFiles:
      - RelativeFilePath: PostSharp.exe
        PortableCommandAlias: postsharp
    InstallerUrl: $repository/releases/download/v$Version/PostSharp-win-x64.zip
    InstallerSha256: $($winHash.ToUpperInvariant())
"@

$scoop = @"
{
  "version": "$Version",
  "description": "Local API debugging workbench with MCP automation.",
  "homepage": "$repository",
  "license": "MIT",
  "url": "$repository/releases/download/v$Version/PostSharp-win-x64.zip",
  "hash": "$winHash",
  "bin": "PostSharp.exe",
  "shortcuts": [
    ["PostSharp.exe", "PostSharp"]
  ],
  "checkver": {
    "github": "$repository"
  },
  "autoupdate": {
    "url": "$repository/releases/download/v`$version/PostSharp-win-x64.zip",
    "hash": {
      "url": "`$baseurl/PostSharp-win-x64.zip.sha256"
    }
  }
}
"@

$homebrew = @"
cask "postsharp" do
  version "$Version"
  sha256 "$osxHash"

  url "$repository/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "$repository"

  depends_on arch: :arm64

  binary "PostSharp"
end
"@

Write-TextFile (Join-Path $root "packaging/winget/PostSharp.yaml") $winget
Write-TextFile (Join-Path $root "packaging/scoop/postsharp.json") $scoop
Write-TextFile (Join-Path $root "packaging/homebrew/postsharp.rb") $homebrew

Write-Host "Packaging manifests updated for $Version"
Write-Host "Windows SHA256: $winHash"
Write-Host "macOS   SHA256: $osxHash"
