using PostSharp.Services;

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
}
