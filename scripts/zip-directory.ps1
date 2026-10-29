Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Compress-Archive writes Windows separators on some hosts, and Set-Content keeps CRLF in shell
# scripts. Linux/macOS need forward slashes, LF line endings and Unix execute bits in the ZIP.
function New-PortableZip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    $source = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\', '/')
    $temporary = "$DestinationPath.tmp"
    if (Test-Path $temporary) {
        Remove-Item $temporary -Force
    }

    $archive = [IO.Compression.ZipFile]::Open(
        $temporary,
        [IO.Compression.ZipArchiveMode]::Create
    )
    try {
        Get-ChildItem -LiteralPath $source -Recurse -File -Force | ForEach-Object {
            $relative = $_.FullName.Substring($source.Length).TrimStart('\', '/').Replace('\', '/')
            $entry = $archive.CreateEntry(
                $relative,
                [IO.Compression.CompressionLevel]::Optimal
            )
            $entry.LastWriteTime = [DateTimeOffset]$_.LastWriteTime

            $isExecutable =
                $_.Name -in @('PostSharp', 'postsharp-cli') -or
                $_.Extension -in @('.sh', '.command')
            if ($isExecutable) {
                # 0100755 in the high 16 bits; unzip restores the executable bit.
                $entry.ExternalAttributes = -2114920448
            }

            $input = [IO.File]::OpenRead($_.FullName)
            try {
                $output = $entry.Open()
                try {
                    $input.CopyTo($output)
                }
                finally {
                    $output.Dispose()
                }
            }
            finally {
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    if (Test-Path $DestinationPath) {
        Remove-Item $DestinationPath -Force
    }

    Move-Item -LiteralPath $temporary -Destination $DestinationPath -Force
}

function Set-PortableUnixScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $unix = $Content.Replace("`r`n", "`n").Replace("`r", "`n")
    [IO.File]::WriteAllText($Path, $unix, [Text.Encoding]::ASCII)
}
