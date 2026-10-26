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
    public void MasksShortAndQuotedSecretsWhereTheyAreUsed()
    {
        var environment = new EnvironmentModel();
        environment.Variables.AddRow("pin", "ab'cd", isSecret: true);
        environment.Variables.AddRow("host", "api.example.com");
        var request = new RequestItem { Method = "GET", Url = "https://{{host}}/items" };
        request.Headers.AddRow("X-Pin", "{{pin}}");
        request.Params.AddRow("pin", "{{pin}}");
        var secrets = SecretMasker.CollectSecrets([environment]);

        var sent = RequestComposer.Compose(request, environment);
        var curl = RequestComposer.BuildCurl(
            SecretMasker.MaskComposed(request, environment, sent, secrets)
        );

        Assert.Empty(secrets);
        Assert.DoesNotContain("ab'", curl);
        Assert.DoesNotContain("ab%27cd", curl);
        Assert.Contains("X-Pin: ******", curl);
        Assert.Contains("pin=******", curl);
        Assert.Contains("api.example.com", curl);
    }

    [Fact]
    public void MasksOnlyTheHeadOfLargeText()
    {
        var text = "secret-token-1" + new string('x', 10_000) + "secret-token-1";

        var head = SecretMasker.MaskHead(text, 100, ["secret-token-1"]);

        Assert.StartsWith("******", head);
        Assert.True(head.Length < 200);
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
