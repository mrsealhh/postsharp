using System.Text.Json;
using PostSharp.Models;
using PostSharp.Services;

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
    public void HttpsRedirectCanUseManualCallback()
    {
        var profile = new AuthorizationProfile
        {
            DiscoveryUrl = "https://login.example.com/.well-known/openid-configuration",
            ClientId = "desktop-client",
            RedirectUri = "https://app.example.com/signin-oidc",
        };

        Assert.Null(profile.FindConfigurationProblem());
        Assert.False(LoopbackRedirectListener.IsLoopbackRedirect(profile.RedirectUri));
    }

    [Fact]
    public void NewProfileUsesLoopbackRedirectByDefault()
    {
        var profile = new AuthorizationProfile();

        Assert.Equal("http://127.0.0.1:53682/callback", profile.RedirectUri);
    }

    [Fact]
    public void CustomSchemeRedirectCanUseManualCallback()
    {
        var profile = new AuthorizationProfile
        {
            DiscoveryUrl = "https://login.example.com/.well-known/openid-configuration",
            ClientId = "desktop-client",
            RedirectUri = "postsharp://auth/callback",
        };

        Assert.Null(profile.FindConfigurationProblem());
        Assert.False(LoopbackRedirectListener.IsLoopbackRedirect(profile.RedirectUri));
    }

    [Fact]
    public void ClientCredentialsDoesNotNeedRedirectButNeedsSecret()
    {
        var profile = new AuthorizationProfile
        {
            GrantType = OAuthGrantTypes.ClientCredentials,
            DiscoveryUrl = "https://login.example.com/.well-known/openid-configuration",
            ClientId = "service-client",
            ClientSecret = "service-secret",
            RedirectUri = string.Empty,
            Scopes = string.Empty,
        };

        Assert.True(profile.IsClientCredentials);
        Assert.True(profile.CanRefresh);
        Assert.Null(profile.FindConfigurationProblem());

        profile.ClientSecret = string.Empty;
        Assert.Contains("客户端密钥", profile.FindConfigurationProblem(), StringComparison.Ordinal);
    }

    [Fact]
    public void GrantTypeRoundTripsThroughJson()
    {
        var json = JsonSerializer.Serialize(
            new AuthorizationProfile { GrantType = OAuthGrantTypes.ClientCredentials }
        );
        var restored = JsonSerializer.Deserialize<AuthorizationProfile>(json);

        Assert.NotNull(restored);
        Assert.Equal(OAuthGrantTypes.ClientCredentials, restored.GrantType);
        Assert.True(restored.IsClientCredentials);
    }
}
