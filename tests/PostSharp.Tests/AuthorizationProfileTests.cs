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

    [Fact]
    public void SystemBrowserRequiresLoopbackHttpRedirect()
    {
        var profile = new AuthorizationProfile
        {
            BrowserMode = AuthorizationProfile.BrowserModeSystem,
            DiscoveryUrl = "https://login.example.com/.well-known/openid-configuration",
            ClientId = "desktop-client",
            RedirectUri = "https://app.example.com/signin-oidc",
        };

        var problem = profile.FindConfigurationProblem();

        Assert.NotNull(problem);
        Assert.Contains("127.0.0.1", problem);
    }

    [Fact]
    public void NewProfileUsesLoopbackRedirectByDefault()
    {
        var profile = new AuthorizationProfile();

        Assert.Equal("http://127.0.0.1:53682/callback", profile.RedirectUri);
    }
}
