using System.Text.Json;
using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class ResidentSettingsTests
{
    [Fact]
    public void McpServiceModeDefaultsToPageAndRoundTrips()
    {
        Assert.Equal(McpServiceModes.Page, new AppSettings().McpServiceMode);
        Assert.True(new AppSettings().KeepMcpResident);

        var json = JsonSerializer.Serialize(
            new AppSettings { McpServiceMode = McpServiceModes.Background }
        );
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(McpServiceModes.Background, restored.McpServiceMode);
        Assert.True(restored.KeepMcpResident);
    }

    [Fact]
    public void McpSecuritySettingsRoundTripAndGenerateToken()
    {
        var settings = new AppSettings();
        Assert.False(settings.McpRequireToken);
        Assert.False(settings.McpReadOnly);
        Assert.Equal(string.Empty, settings.McpTokenCipher);

        var token = settings.GetMcpToken();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal(token, settings.GetMcpToken());
        Assert.False(string.IsNullOrWhiteSpace(settings.McpTokenCipher));

        settings.McpRequireToken = true;
        settings.McpReadOnly = true;
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(restored);
        Assert.True(restored.McpRequireToken);
        Assert.True(restored.McpReadOnly);
        Assert.Equal(settings.McpTokenCipher, restored.McpTokenCipher);
        Assert.Equal(token, restored.GetMcpToken());
        Assert.NotEqual(token, restored.RegenerateMcpToken());
    }

    [Fact]
    public void McpTokensUseFixedTimeComparisonSemantics()
    {
        var token = McpTokenService.Generate();
        Assert.True(token.Length >= 40);
        Assert.True(McpTokenService.FixedEquals(token, token));
        Assert.False(McpTokenService.FixedEquals(token, token + "x"));
        Assert.False(McpTokenService.FixedEquals(token, "other"));
    }
}
