using System.Text.Json;
using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class PostmanExportTests
{
    [Fact]
    public void CollectionContextExportUsesPostmanV21Shape()
    {
        var collection = new RequestCollection { Name = "Demo" };
        collection.Requests.Add(
            new RequestItem
            {
                Name = "Health",
                Method = "GET",
                Url = "https://example.com/health",
                Owner = collection,
            }
        );

        using var document = JsonDocument.Parse(PostmanExportService.ExportCollection(collection));
        var root = document.RootElement;

        Assert.Equal("Demo", root.GetProperty("info").GetProperty("name").GetString());
        Assert.Contains("v2.1.0", root.GetProperty("info").GetProperty("schema").GetString());
        var item = root.GetProperty("item")[0];
        Assert.Equal("Health", item.GetProperty("name").GetString());
        Assert.Equal(
            "https://example.com/health",
            item.GetProperty("request").GetProperty("url").GetProperty("raw").GetString()
        );
    }
}
