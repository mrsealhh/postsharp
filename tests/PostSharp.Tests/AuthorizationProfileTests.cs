using System.Text.Json;
using PostSharp.Models;

namespace PostSharp.Tests;

public sealed class AuthorizationProfileTests
{
    [Fact]
    public void ClientSecretIsNotSerializedAsPlaintext()
    {
        var profile = new AuthorizationProfile { ClientSecret = "client-secret-1" };

        var json = JsonSerializer.Serialize(profile);

        Assert.DoesNotContain("\"ClientSecret\":\"client-secret-1\"", json);
        Assert.Contains("ClientSecretCipher", json);
    }

    [Fact]
    public void LegacyPlaintextClientSecretStillLoads()
    {
        var profile = JsonSerializer.Deserialize<AuthorizationProfile>(
            """{"ClientSecret":"legacy-secret"}"""
        );

        Assert.NotNull(profile);
        Assert.Equal("legacy-secret", profile.ClientSecret);
    }
}
