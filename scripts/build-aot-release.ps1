param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$RuntimeIdentifier = "",
    [switch]$KeepPlugins
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "zip-directory.ps1")

# Windows PowerShell 5.1 leaves $IsWindows undefined; PowerShell 7 sets it on every platform.
$isWindowsHost = $true
if ($null -ne $IsWindows) {
    $isWindowsHost = $IsWindows
}

$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    if ($isWindowsHost) {
        $RuntimeIdentifier = if ($architecture -eq "Arm64") { "win-arm64" } else { "win-x64" }
    }
    elseif ($IsMacOS) {
        $RuntimeIdentifier = if ($architecture -eq "Arm64") { "osx-arm64" } else { "osx-x64" }
    }
    else {
        $RuntimeIdentifier = if ($architecture -eq "Arm64") { "linux-arm64" } else { "linux-x64" }
    }
}

$Version = $Version.Trim().TrimStart("v", "V")
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "PostSharp-$Version-aot-$RuntimeIdentifier"
$zipName = "PostSharp-aot-$RuntimeIdentifier.zip"
$zip = Join-Path $artifacts $zipName
$checksum = "$zip.sha256"

if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}

$publishArgs = @(
    "publish",
    (Join-Path $root "src\PostSharp\PostSharp.csproj"),
    "-c", "Release",
    "-r", $RuntimeIdentifier,
    "-p:PublishAot=true",
    "-p:SelfContained=true",
    "-p:UseOptimizedPublish=false",
    "-p:Version=$Version",
    "-o", $publish
)

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsDevCmd = $null
if (Test-Path $vswhere) {
    $installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($installation) {
        $candidate = Join-Path $installation "Common7\Tools\VsDevCmd.bat"
        if (Test-Path $candidate) {
            $vsDevCmd = $candidate
        }
    }
}

if ($vsDevCmd -and $isWindowsHost -and $RuntimeIdentifier -like "win*") {
    # NativeAOT needs the MSVC linker, which is not on PATH in a plain shell.
    $batchPath = Join-Path $env:TEMP "postsharp-aot-publish-$PID.cmd"
    $lines = @(
        "@echo off",
        "call `"$vsDevCmd`" -arch=x64",
        "dotnet $($publishArgs -join ' ')"
    )
    Set-Content -Path $batchPath -Value $lines -Encoding ASCII
    & cmd.exe /c "`"$batchPath`""
    $exitCode = $LASTEXITCODE
    Remove-Item $batchPath -Force -ErrorAction SilentlyContinue
    if ($exitCode -ne 0) {
        throw "NativeAOT publish failed."
    }
}
else {
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "NativeAOT publish failed."
    }
}

Get-ChildItem $publish -Filter *.pdb -Recurse -Force | Remove-Item -Force

# In-process storage plugins need AssemblyLoadContext, so a native build falls back to local SQLite.
# Drop them unless the caller explicitly wants to ship the same folder layout.
if (-not $KeepPlugins) {
    $plugins = Join-Path $publish "plugins"
    if (Test-Path $plugins) {
        Remove-Item $plugins -Recurse -Force
    }
}

$runSh = @'
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
chmod +x PostSharp
exec ./PostSharp "$@"
'@
if ($RuntimeIdentifier -like "linux*") {
    Set-PortableUnixScript -Path (Join-Path $publish "run.sh") -Content $runSh
}

$runCommand = @'
#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
chmod +x PostSharp
exec ./PostSharp "$@"
'@
if ($RuntimeIdentifier -like "osx*") {
    Set-PortableUnixScript -Path (Join-Path $publish "run.command") -Content $runCommand
}

if (Test-Path $zip) {
    Remove-Item $zip -Force
}

New-PortableZip -SourceDirectory $publish -DestinationPath $zip

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $checksum -Value "$hash  $zipName" -Encoding ascii

Write-Host "Package: $zip"
Write-Host "SHA256:  $checksum"
Write-Host "Smoke:   pwsh -File scripts/aot-smoke.ps1 -SkipPublish -PublishDirectory `"$publish`""
