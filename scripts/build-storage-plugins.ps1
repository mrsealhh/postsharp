param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root "artifacts/plugins"
if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

New-Item -ItemType Directory -Path $output | Out-Null

$plugins = @(
    "PostSharp.Storage.PostgreSql",
    "PostSharp.Storage.MySql",
    "PostSharp.Storage.SqlServer",
    "PostSharp.Storage.MongoDb",
    "PostSharp.Storage.ClickHouse",
    "PostSharp.Storage.Doris",
    "PostSharp.Storage.StarRocks"
)

foreach ($plugin in $plugins) {
    $project = Join-Path $root "src/Plugins/$plugin/$plugin.csproj"
    $target = Join-Path $output $plugin
    dotnet publish $project -c $Configuration -o $target --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "发布存储插件失败：$plugin"
    }

    Compress-Archive -Path (Join-Path $target "*") -DestinationPath (Join-Path $output "$plugin.zip") -Force
    Write-Host "已生成 $(Join-Path $output "$plugin.zip")"
}
