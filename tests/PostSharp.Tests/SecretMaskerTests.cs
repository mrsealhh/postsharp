using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class SecretMaskerTests
{
    [Fact]
    public void CollectsOnlyEnabledLongEnoughSecrets()
    {
        var environment = new EnvironmentModel();
        environment.Variables.AddRow("token", "secret-token-1", isSecret: true);
        environment.Variables.AddRow("pin", "123", isSecret: true);
        environment.Variables.AddRow("baseUrl", "http://127.0.0.1:5199");

        var secrets = SecretMasker.CollectSecrets([environment]);

        Assert.Contains("secret-token-1", secrets);
        Assert.DoesNotContain("123", secrets);
        Assert.DoesNotContain("http://127.0.0.1:5199", secrets);
    }

    [Theory]
    [InlineData("Authorization", "Bearer abc.def", "Bearer ******")]
    [InlineData("authorization", "Basic dXNlcjpwYXNz", "Basic ******")]
    [InlineData("X-Api-Key", "k-123456", "******")]
    [InlineData("Accept", "application/json", "application/json")]
    public void MasksCredentialHeaders(string name, string value, string expected)
    {
        Assert.Equal(expected, SecretMasker.MaskHeaderValue(name, value, []));
    }

    [Fact]
    public void MasksStoredCurlAndSecretValues()
    {
        var request = new ComposedRequest(
            "POST",
            "https://api.example.com/items?key=secret-token-1",
            [new("Authorization", "Bearer secret-token-1"), new("X-Trace", "trace-1")],
            "{\"token\":\"secret-token-1\"}",
            "application/json",
            "dev"
        );
        var secrets = new[] { "secret-token-1" };

        var curl = RequestComposer.BuildCurl(SecretMasker.MaskRequest(request, secrets));
        var legacy = SecretMasker.MaskCurl(RequestComposer.BuildCurl(request), secrets);

        foreach (var text in new[] { curl, legacy })
        {
            Assert.DoesNotContain("secret-token-1", text);
            Assert.Contains("Authorization: Bearer ******", text);
            Assert.Contains("X-Trace: trace-1", text);
        }
    }

    [Fact]
    public void ScrubsExistingAuditEntries()
    {
        var entry = new AuditEntry
        {
            Url = "https://api.example.com/?key=secret-token-1",
            RequestHeaders = "Authorization: Bearer abc\nAccept: */*",
            ResponseHeaders = "Set-Cookie: session=xyz",
            ResponseBodyPreview = "{\"echo\":\"secret-token-1\"}",
        };

        var changed = SecretMasker.MaskAuditEntry(entry, ["secret-token-1"]);

        Assert.True(changed);
        Assert.DoesNotContain("secret-token-1", entry.Url);
        Assert.Equal("Authorization: Bearer ******\nAccept: */*", entry.RequestHeaders);
        Assert.Equal("Set-Cookie: ******", entry.ResponseHeaders);
        Assert.DoesNotContain("secret-token-1", entry.ResponseBodyPreview);
        Assert.False(SecretMasker.MaskAuditEntry(entry, ["secret-token-1"]));
    }
}
