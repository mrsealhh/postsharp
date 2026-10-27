using System.Net;
using System.Text;
using PostSharp.Models;
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

    [Fact]
    public void ParsesPathParametersAndResponseDocuments()
    {
        const string json = """
            {
              "openapi": "3.0.3",
              "info": { "title": "Pets", "version": "1.0" },
              "servers": [ { "url": "https://api.example.com/v1" } ],
              "paths": {
                "/pets/{petId}": {
                  "parameters": [
                    {
                      "name": "petId",
                      "in": "path",
                      "required": true,
                      "description": "宠物 ID",
                      "schema": { "type": "integer", "format": "int64", "example": 42 }
                    }
                  ],
                  "get": {
                    "summary": "获取宠物",
                    "tags": [ "宠物" ],
                    "parameters": [
                      { "name": "verbose", "in": "query", "schema": { "type": "boolean" } }
                    ],
                    "responses": {
                      "200": {
                        "description": "成功",
                        "headers": {
                          "X-Rate-Limit": { "description": "剩余额度", "schema": { "type": "integer" } }
                        },
                        "content": {
                          "application/json": {
                            "schema": {
                              "type": "object",
                              "required": [ "id" ],
                              "properties": {
                                "id": { "type": "integer", "example": 42 },
                                "name": { "type": "string", "example": "Tom" }
                              }
                            }
                          }
                        }
                      },
                      "404": { "description": "未找到" }
                    }
                  }
                }
              }
            }
            """;

        var request = Assert.Single(
            Assert.Single(OpenApiImportService.Parse(json, "openapi.json").Groups).Requests
        );

        Assert.Equal("https://api.example.com/v1/pets/42", request.Url);
        Assert.Equal("GET /pets/{petId}", request.Key);
        Assert.Contains(
            request.Doc.Parameters,
            parameter => parameter.Location == "path" && parameter.Required
        );
        Assert.Equal(2, request.Doc.Responses.Count);
        var ok = request.Doc.Responses[0];
        Assert.Equal("200", ok.StatusCode);
        Assert.Contains("X-Rate-Limit", ok.HeadersText, StringComparison.Ordinal);
        Assert.Contains(ok.Fields, field => field.Name == "id" && field.Required);
        Assert.Contains("\"name\": \"Tom\"", ok.Samples[0].Sample, StringComparison.Ordinal);

        var markdown = OpenApiMarkdownRenderer.Render(request.Doc);
        Assert.Contains("## 请求参数", markdown, StringComparison.Ordinal);
        Assert.Contains("## 响应", markdown, StringComparison.Ordinal);
        Assert.Contains("```json", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportsFormAndMultipartBodiesAsEditableFields()
    {
        const string json = """
            {
              "openapi": "3.0.3",
              "info": { "title": "Forms", "version": "1.0" },
              "servers": [ { "url": "https://api.example.com" } ],
              "paths": {
                "/urlencoded": {
                  "post": {
                    "requestBody": {
                      "content": {
                        "application/x-www-form-urlencoded": {
                          "schema": {
                            "type": "object",
                            "properties": {
                              "name": { "type": "string", "example": "Tom" },
                              "age": { "type": "integer", "example": 3 }
                            }
                          }
                        }
                      }
                    },
                    "responses": { "200": { "description": "ok" } }
                  }
                },
                "/multipart": {
                  "post": {
                    "requestBody": {
                      "content": {
                        "multipart/form-data": {
                          "schema": {
                            "type": "object",
                            "properties": {
                              "file": { "type": "string", "format": "binary" },
                              "note": { "type": "string", "example": "hello" }
                            }
                          }
                        }
                      }
                    },
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var requests = Assert
            .Single(OpenApiImportService.Parse(json, "openapi.json").Groups)
            .Requests;

        var urlencoded = requests.Single(request => request.Path == "/urlencoded");
        Assert.Equal("Text", urlencoded.BodyMode);
        Assert.Equal("name=Tom&age=3", urlencoded.Body);
        Assert.Contains(
            urlencoded.Headers,
            header =>
                header.Name == "Content-Type" && header.Value == "application/x-www-form-urlencoded"
        );

        var multipart = requests.Single(request => request.Path == "/multipart");
        Assert.Equal("Multipart", multipart.BodyMode);
        Assert.Contains(
            multipart.FormFields,
            field => field.Name == "note" && field.Value == "hello"
        );
        Assert.DoesNotContain(multipart.FormFields, field => field.Name == "file");
    }

    [Fact]
    public void SyncMergesOperationsAndKeepsUserEdits()
    {
        const string firstJson = """
            {
              "openapi": "3.0.3",
              "info": { "title": "Sync", "version": "1.0" },
              "servers": [ { "url": "https://api.example.com" } ],
              "paths": {
                "/pets": {
                  "get": {
                    "summary": "List pets",
                    "tags": [ "宠物" ],
                    "parameters": [
                      { "name": "limit", "in": "query", "schema": { "type": "integer" } }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                },
                "/old": {
                  "get": {
                    "summary": "Old",
                    "tags": [ "宠物" ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        const string secondJson = """
            {
              "openapi": "3.0.3",
              "info": { "title": "Sync", "version": "1.1" },
              "servers": [ { "url": "https://api.example.com" } ],
              "paths": {
                "/pets": {
                  "get": {
                    "summary": "List all pets",
                    "tags": [ "宠物" ],
                    "parameters": [
                      { "name": "limit", "in": "query", "schema": { "type": "integer" } },
                      { "name": "cursor", "in": "query", "schema": { "type": "string" } }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                },
                "/new": {
                  "post": {
                    "summary": "New",
                    "tags": [ "宠物" ],
                    "responses": { "201": { "description": "created" } }
                  }
                }
              }
            }
            """;

        var root = new RequestCollection { Name = "Sync" };
        var group = new RequestCollection { Name = "宠物", Parent = root };
        root.Children.Add(group);
        foreach (
            var imported in OpenApiImportService
                .Parse(firstJson, "openapi.json")
                .Groups.SelectMany(group => group.Requests)
        )
        {
            var request = OpenApiRequestFactory.Create(imported);
            request.Owner = group;
            group.Requests.Add(request);
        }

        var existing = group.Requests.Single(request => request.OpenApiKey == "GET /pets");
        existing.Name = "我的宠物";
        existing.Params.First(row => row.Key == "limit").Value = "25";
        var trashed = new List<string>();
        var registered = new List<RequestCollection>();

        var result = OpenApiSyncService.Apply(
            root,
            OpenApiImportService.Parse(secondJson, "openapi.json"),
            registered.Add,
            request =>
            {
                trashed.Add(request.Name);
                group.Requests.Remove(request);
            }
        );

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Removed);
        Assert.Contains("Old", trashed);
        Assert.Equal("我的宠物", existing.Name);
        Assert.Equal("25", existing.Params.First(row => row.Key == "limit").Value);
        Assert.Contains(existing.Params, row => row.Key == "cursor");
        Assert.Equal("List all pets", existing.OpenApiDoc?.Summary);
        Assert.Contains(group.Requests, request => request.OpenApiKey == "POST /new");
    }

    [Fact]
    public async Task FetchSendsCacheValidatorsAndHandlesNotModified()
    {
        using var client = new HttpClient(
            new StubHttpHandler(request =>
            {
                Assert.Contains(request.Headers.IfNoneMatch, tag => tag.Tag == "\"v1\"");
                Assert.NotNull(request.Headers.IfModifiedSince);
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            })
        );

        var result = await OpenApiImportService.FetchAsync(
            "https://api.example.com/openapi.json",
            "\"v1\"",
            "Wed, 21 Oct 2026 07:28:00 GMT",
            client: client
        );

        Assert.True(result.NotModified);
        Assert.Null(result.Document);
        Assert.Equal("\"v1\"", result.ETag);
    }

    [Fact]
    public void SwaggerParameterTypeAndEnumBecomeQueryExamples()
    {
        const string json = """
            {
              "swagger": "2.0",
              "info": { "title": "Swagger", "version": "1.0" },
              "host": "api.example.com",
              "schemes": [ "https" ],
              "paths": {
                "/items": {
                  "get": {
                    "parameters": [
                      {
                        "name": "status",
                        "in": "query",
                        "type": "string",
                        "enum": [ "active", "closed" ],
                        "description": "状态"
                      }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var request = Assert.Single(
            Assert.Single(OpenApiImportService.Parse(json, "swagger.json").Groups).Requests
        );

        Assert.Contains(request.Query, field => field.Name == "status" && field.Value == "active");
        Assert.Contains(
            request.Doc.Parameters,
            parameter =>
                parameter.Name == "status"
                && parameter.Type == "string"
                && parameter.EnumText.Contains("active", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void ImportsDeepSchemaWithoutDefaultJsonDepthLimit()
    {
        var schema = """{"type":"string"}""";
        for (var index = 0; index < 80; index++)
        {
            schema = """{"type":"object","properties":{"child":""" + schema + "}}";
        }

        var json = $$"""
            {
              "openapi": "3.0.3",
              "info": { "title": "Deep", "version": "1.0" },
              "paths": {
                "/deep": {
                  "get": {
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": {
                          "application/json": {
                            "schema": {{schema}}
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var document = OpenApiImportService.Parse(json, "deep-openapi.json");

        Assert.Single(Assert.Single(document.Groups).Requests);
    }

    [Fact]
    public void RecursiveSchemaDoesNotRecurseForever()
    {
        const string json = """
            {
              "openapi": "3.0.3",
              "info": { "title": "Recursive", "version": "1.0" },
              "components": {
                "schemas": {
                  "Node": {
                    "type": "object",
                    "properties": {
                      "value": { "type": "string" },
                      "child": { "$ref": "#/components/schemas/Node" }
                    }
                  }
                }
              },
              "paths": {
                "/tree": {
                  "get": {
                    "responses": {
                      "200": {
                        "description": "ok",
                        "content": {
                          "application/json": {
                            "schema": { "$ref": "#/components/schemas/Node" }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var document = OpenApiImportService.Parse(json, "recursive-openapi.json");

        Assert.Single(Assert.Single(document.Groups).Requests);
    }

    [Fact]
    public void ImportsMoreThanFiveHundredOperations()
    {
        var paths = new StringBuilder();
        for (var index = 0; index < 600; index++)
        {
            if (index > 0)
            {
                paths.Append(',');
            }

            paths.Append(
                $$"""
                "/operation-{{index}}": {
                  "get": {
                    "responses": { "200": { "description": "ok" } }
                  }
                }
                """
            );
        }

        var json = $$"""
            {
              "openapi": "3.0.3",
              "info": { "title": "Large", "version": "1.0" },
              "paths": {
                {{paths}}
              }
            }
            """;

        var document = OpenApiImportService.Parse(json, "large-openapi.json");

        Assert.Equal(600, document.Groups.Sum(group => group.Requests.Count));
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
