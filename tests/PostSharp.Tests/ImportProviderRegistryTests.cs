using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class ImportProviderRegistryTests
{
    [Fact]
    public void DefaultRegistryContainsEverySupportedKind()
    {
        var registry = ImportProviderRegistry.CreateDefault();

        foreach (
            var kind in Enum.GetValues<ImportFileKind>()
                .Where(kind => kind != ImportFileKind.Unknown)
        )
        {
            var provider = registry.Find(kind);

            Assert.NotNull(provider);
            Assert.False(string.IsNullOrWhiteSpace(provider.FormatName));
        }
    }

    [Fact]
    public void UnknownKindHasNoProvider()
    {
        var registry = ImportProviderRegistry.CreateDefault();

        Assert.Null(registry.Find(ImportFileKind.Unknown));
    }

    [Fact]
    public void PostmanProviderReturnsCollectionPayload()
    {
        const string json = """
            {
              "info": {
                "name": "示例集合",
                "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
              },
              "item": [
                {
                  "name": "健康检查",
                  "request": {
                    "method": "GET",
                    "header": [],
                    "url": { "raw": "https://api.example.com/health" }
                  }
                }
              ]
            }
            """;
        var provider = new PostmanCollectionImportProvider();

        var payload = provider.Parse(
            "fixture.json",
            json,
            new ImportProviderContext(IncludeStaticResources: false)
        );

        var collection = Assert.IsType<CollectionImportPayload>(payload);
        Assert.Equal("示例集合", collection.Result.Root.Name);
        Assert.Equal(1, collection.Result.RequestCount);
    }

    [Fact]
    public void HarProviderPassesStaticResourceOption()
    {
        const string har = """
            {
              "log": {
                "version": "1.2",
                "entries": [
                  {
                    "_resourceType": "xhr",
                    "request": {
                      "method": "GET",
                      "url": "https://api.example.com/api/users",
                      "header": []
                    },
                    "response": { "content": { "mimeType": "application/json" } }
                  },
                  {
                    "_resourceType": "image",
                    "request": {
                      "method": "GET",
                      "url": "https://api.example.com/logo.png",
                      "header": []
                    },
                    "response": { "content": { "mimeType": "image/png" } }
                  }
                ]
              }
            }
            """;
        var provider = new HarImportProvider();

        var xhrOnly = Assert.IsType<CollectionImportPayload>(
            provider.Parse("fixture.har", har, new ImportProviderContext())
        );
        var withStatic = Assert.IsType<CollectionImportPayload>(
            provider.Parse(
                "fixture.har",
                har,
                new ImportProviderContext(IncludeStaticResources: true)
            )
        );

        Assert.Equal(1, xhrOnly.Result.RequestCount);
        Assert.Equal(2, withStatic.Result.RequestCount);
    }
}
