using System.Text.Json;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("V2.0", 2, 0, 0)]
    [InlineData("v0.3.0.4", 0, 3, 0)]
    public void ParsesReleaseTags(string tag, int major, int minor, int build)
    {
        var version = UpdateService.ParseReleaseVersion(tag);

        Assert.NotNull(version);
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("release")]
    public void RejectsInvalidReleaseTags(string? tag)
    {
        Assert.Null(UpdateService.ParseReleaseVersion(tag));
    }

    [Theory]
    [InlineData(false, "PostSharp-win-x64.zip")]
    [InlineData(true, "PostSharp-aot-win-x64.zip")]
    public void LayoutSelectsItsOwnPackageAsset(bool isAot, string expected)
    {
        Assert.Equal(expected, UpdateService.PackageAssetNameForLayout(isAot));
    }

    [Theory]
    [InlineData("PostSharp-win-x64.zip")]
    [InlineData("PostSharp-aot-win-x64.zip")]
    public void ReleaseAssetsResolveTheMatchingChecksum(string assetName)
    {
        using var document = JsonDocument.Parse(
            """
            [
              { "name": "PostSharp-aot-win-x64.zip", "browser_download_url": "https://example.com/aot.zip" },
              { "name": "PostSharp-aot-win-x64.zip.sha256", "browser_download_url": "https://example.com/aot.sha256" },
              { "name": "PostSharp-linux-x64.zip", "browser_download_url": "https://example.com/linux.zip" },
              { "name": "PostSharp-linux-x64.zip.sha256", "browser_download_url": "https://example.com/linux.sha256" },
              { "name": "PostSharp-osx-arm64.zip", "browser_download_url": "https://example.com/osx.zip" },
              { "name": "PostSharp-osx-arm64.zip.sha256", "browser_download_url": "https://example.com/osx.sha256" },
              { "name": "PostSharp-win-x64.zip", "browser_download_url": "https://example.com/win.zip" },
              { "name": "PostSharp-win-x64.zip.sha256", "browser_download_url": "https://example.com/win.sha256" }
            ]
            """
        );

        var selection = UpdateService.SelectPackageAssets(document.RootElement, assetName);

        var expectedPrefix = assetName.StartsWith("PostSharp-aot", StringComparison.Ordinal)
            ? "https://example.com/aot"
            : "https://example.com/win";
        Assert.Equal($"{expectedPrefix}.zip", selection.AssetUrl);
        Assert.Equal($"{expectedPrefix}.sha256", selection.ChecksumUrl);
    }
}
