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
}
