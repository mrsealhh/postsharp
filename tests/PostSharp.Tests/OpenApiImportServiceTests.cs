using System.Net;
using System.Text;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class OpenApiImportServiceTests
{
    [Fact]
    public void GroupsByApifoxFolderAndAddsSecurityPlaceholder()
    {
        const string json = """
            {
              "openapi": "3.0.3",
              "info": { "title": "Demo", "version": "1.0" },
              "servers": [
                { "url": "https://api.example.com" },
                { "url": "https://backup.example.com" }
              ],
              "security": [ { "bearerAuth": [] } ],
              "components": {
                "securitySchemes": {
                  "bearerAuth": { "type": "http", "scheme": "bearer" }
                }
              },
              "paths": {
                "/items": {
                  "get": {
                    "summary": "List items",
                    "tags": [ "用户", "公开" ],
                    "x-apifox-folder": "业务/用户",
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              },
              "external": {
                "$ref": "https://example.com/shared.json"
              }
            }
            """;

        var document = OpenApiImportService.Parse(json, "openapi.json");

        Assert.Equal("Demo", document.Title);
        var group = Assert.Single(document.Groups);
        Assert.Equal("业务/用户", group.Name);
        var request = Assert.Single(group.Requests);
        Assert.Contains(
            request.Headers,
            header => header.Name == "Authorization" && header.Value == "Bearer {{accessToken}}"
        );
        Assert.Contains(document.Warnings, warning => warning.Contains("服务器"));
        Assert.Contains(document.Warnings, warning => warning.Contains("多个 tag"));
        Assert.Contains(document.Warnings, warning => warning.Contains("外部 $ref"));
        Assert.Contains(document.Warnings, warning => warning.Contains("安全方案"));
    }

    [Fact]
    public void ExposesServersAndRebasesRequests()
    {
        const string json = """
            {
              "openapi": "3.0.3",
              "info": { "title": "Servers", "version": "1.0" },
              "servers": [
                { "url": "https://api.example.com", "description": "生产" },
                { "url": "https://staging.example.com", "description": "预发" }
              ],
              "paths": {
                "/items": {
                  "get": {
                    "summary": "List items",
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var document = OpenApiImportService.Parse(json, "openapi.json");

        Assert.Equal(2, document.Servers.Count);
        Assert.Equal("https://api.example.com", document.BaseUrl);
        Assert.Equal("生产", document.Servers[0].Description);
        var request = Assert.Single(Assert.Single(document.Groups).Requests);
        Assert.Equal("https://api.example.com/items", request.Url);

        var rebased = document.WithServer(1);

        Assert.Equal(1, rebased.SelectedServerIndex);
        Assert.Equal("https://staging.example.com", rebased.BaseUrl);
        Assert.Equal(
            "https://staging.example.com/items",
            Assert.Single(Assert.Single(rebased.Groups).Requests).Url
        );
    }

    [Fact]
    public void SwaggerHostBecomesSingleServer()
    {
        const string json = """
            {
              "swagger": "2.0",
              "info": { "title": "Swagger", "version": "1.0" },
              "host": "api.example.com",
              "basePath": "/v1",
              "schemes": [ "https" ],
              "paths": {
                "/health": {
                  "get": {
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var document = OpenApiImportService.Parse(json, "swagger.json");

        var server = Assert.Single(document.Servers);
        Assert.Equal("https://api.example.com/v1", server.Url);
        Assert.Equal(
            "https://api.example.com/v1/health",
            Assert.Single(Assert.Single(document.Groups).Requests).Url
        );
    }

    [Fact]
    public async Task ResolvesExternalRefRelativeToDocumentUrl()
    {
        const string document = """
            {
              "openapi": "3.0.3",
              "info": { "title": "External", "version": "1.0" },
              "servers": [ { "url": "https://api.example.com" } ],
              "paths": {
                "/items": {
                  "post": {
                    "requestBody": { "$ref": "schemas.json#/CreateRequest" },
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        const string external = """
            {
              "CreateRequest": {
                "required": true,
                "content": {
                  "application/json": {
                    "schema": {
                      "type": "object",
                      "properties": {
                        "name": { "type": "string" }
                      }
                    }
                  }
                }
              }
            }
            """;
        var requested = new List<string>();
        using var client = new HttpClient(
            new StubHttpHandler(request =>
            {
                requested.Add(request.RequestUri?.ToString() ?? string.Empty);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(external, Encoding.UTF8, "application/json"),
                };
            })
        );

        var result = await OpenApiImportService.ParseWithExternalReferencesAsync(
            document,
            "https://api.example.com/openapi.json",
            new Uri("https://api.example.com/openapi.json"),
            client: client
        );

        var request = Assert.Single(Assert.Single(result.Groups).Requests);
        Assert.Contains("\"name\"", request.Body);
        Assert.Equal("https://api.example.com/schemas.json", Assert.Single(requested));
    }

    [Fact]
    public async Task ExternalRefFailureKeepsDocumentImportable()
    {
        const string document = """
            {
              "openapi": "3.0.3",
              "info": { "title": "External", "version": "1.0" },
              "servers": [ { "url": "https://api.example.com" } ],
              "paths": {
                "/items": {
                  "post": {
                    "requestBody": { "$ref": "missing.json#/CreateRequest" },
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        using var client = new HttpClient(
            new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))
        );

        var result = await OpenApiImportService.ParseWithExternalReferencesAsync(
            document,
            "https://api.example.com/openapi.json",
            new Uri("https://api.example.com/openapi.json"),
            client: client
        );

        Assert.Single(Assert.Single(result.Groups).Requests);
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("外部引用下载失败", StringComparison.Ordinal)
        );
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(handler(request));
        }
    }
}
