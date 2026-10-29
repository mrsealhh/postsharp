using System.Net;
using System.Text;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class OAuthClientCredentialsTests
{
    [Fact]
    public async Task ClientCredentialsTokenRequestUsesTheClientCredentialsGrant()
    {
        var handler = new TokenFlowHandler();
        using var client = new HttpClient(handler);
        var service = new OidcAuthService(client);

        var endpoints = await service.DiscoverAsync(
            "https://login.example.com/.well-known/openid-configuration"
        );
        var tokens = await service.AcquireClientCredentialsAsync(
            endpoints.TokenEndpoint,
            "service-client",
            "service-secret",
            "api.read"
        );

        Assert.Equal("access-1", tokens.AccessToken);
        Assert.Contains(
            "grant_type=client_credentials",
            handler.TokenBody,
            StringComparison.Ordinal
        );
        Assert.Contains("client_id=service-client", handler.TokenBody, StringComparison.Ordinal);
        Assert.Contains(
            "client_secret=service-secret",
            handler.TokenBody,
            StringComparison.Ordinal
        );
        Assert.Contains("scope=api.read", handler.TokenBody, StringComparison.Ordinal);
    }

    private sealed class TokenFlowHandler : HttpMessageHandler
    {
        private int _requests;

        public string TokenBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                return Json(
                    """
                    {
                      "issuer": "https://login.example.com",
                      "authorization_endpoint": "https://login.example.com/authorize",
                      "token_endpoint": "https://login.example.com/token",
                      "scopes_supported": ["api.read"]
                    }
                    """
                );
            }

            TokenBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return Json(
                """
                {
                  "access_token": "access-1",
                  "token_type": "Bearer",
                  "expires_in": 3600
                }
                """
            );
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }
}
