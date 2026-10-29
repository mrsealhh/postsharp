using System.Text.Json;
using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class GraphQlRequestTests
{
    [Fact]
    public void ComposesGraphQlPostBodyWithVariablesAndOperationName()
    {
        var request = new RequestItem
        {
            Method = "GET",
            Url = "https://example.com/graphql",
            BodyMode = "GraphQL",
            GraphQlQuery = "query User($id: ID!) { user(id: $id) { name } }",
            GraphQlVariables = """{ "id": "42" }""",
            GraphQlOperationName = "User",
        };

        var composed = RequestComposer.Compose(request, null);

        Assert.Equal("POST", composed.Method);
        Assert.Equal("application/json", composed.ContentType);
        using var document = JsonDocument.Parse(composed.Body!);
        var root = document.RootElement;
        Assert.Equal(
            "query User($id: ID!) { user(id: $id) { name } }",
            root.GetProperty("query").GetString()
        );
        Assert.Equal("42", root.GetProperty("variables").GetProperty("id").GetString());
        Assert.Equal("User", root.GetProperty("operationName").GetString());
    }

    [Fact]
    public void PostmanExportAndImportKeepGraphQlFields()
    {
        var collection = new RequestCollection { Name = "GraphQL" };
        collection.Requests.Add(
            new RequestItem
            {
                Name = "User",
                Method = "POST",
                Url = "https://example.com/graphql",
                BodyMode = "GraphQL",
                GraphQlQuery = "query { ping }",
                GraphQlVariables = """{ "id": 7 }""",
                GraphQlOperationName = "Ping",
                Owner = collection,
            }
        );

        var exported = PostmanExportService.ExportCollection(collection);
        var imported = PostmanImportService.Parse(exported, "test").Root.Requests.Single();

        Assert.Equal("GraphQL", imported.BodyMode);
        Assert.Equal("query { ping }", imported.GraphQlQuery);
        Assert.Contains("\"id\": 7", imported.GraphQlVariables, StringComparison.Ordinal);
        Assert.Equal("Ping", imported.GraphQlOperationName);
    }

    [Fact]
    public void InvalidGraphQlVariablesAreRejectedBeforeSend()
    {
        var request = new RequestItem
        {
            Url = "https://example.com/graphql",
            BodyMode = "GraphQL",
            GraphQlQuery = "query { ping }",
            GraphQlVariables = "[]",
        };

        Assert.Throws<InvalidOperationException>(() => RequestComposer.Compose(request, null));
    }
}
