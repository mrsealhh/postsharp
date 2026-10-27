using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class OpenApiEnvironmentServiceTests
{
    [Fact]
    public void UsesSourceAuthorityWhenDocumentHasNoServers()
    {
        var document = CreateDocument(
            "http://192.168.102.234:31391/swagger/V1/swagger.json",
            Array.Empty<OpenApiImportServer>()
        );

        var target = Assert.Single(OpenApiEnvironmentService.Resolve(document));

        Assert.Equal("http://192.168.102.234:31391", target.BaseUrl);
        Assert.Equal("192.168.102.234:31391", target.Name);
    }

    [Fact]
    public void CreatesTargetsForAbsoluteAndRelativeServers()
    {
        var document = CreateDocument(
            "https://gateway.example.com/swagger.json",
            new[]
            {
                new OpenApiImportServer(0, "https://api.example.com/v1", "生产"),
                new OpenApiImportServer(1, "/v2", "预发"),
                new OpenApiImportServer(2, "https://api.example.com/v1", "重复"),
            }
        );

        var targets = OpenApiEnvironmentService.Resolve(document);

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, target => target.BaseUrl == "https://api.example.com/v1");
        Assert.Contains(targets, target => target.BaseUrl == "https://gateway.example.com/v2");
    }

    [Fact]
    public void SkipsNonHttpSource()
    {
        var document = CreateDocument(@"C:\specs\openapi.json", Array.Empty<OpenApiImportServer>());

        Assert.Empty(OpenApiEnvironmentService.Resolve(document));
    }

    private static OpenApiImportDocument CreateDocument(
        string source,
        IReadOnlyList<OpenApiImportServer> servers
    ) =>
        new()
        {
            Title = "Demo",
            Version = "1",
            Source = source,
            Servers = servers,
            BaseUrl = servers.FirstOrDefault()?.Url ?? "{{baseUrl}}",
            Groups = Array.Empty<OpenApiImportGroup>(),
        };
}
