using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class OpenApiContractDiffTests
{
    private const string BaseDocument = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Base API", "version": "1.0" },
          "servers": [{ "url": "https://api.example.com" }],
          "paths": {
            "/users": {
              "get": {
                "parameters": [
                  { "name": "limit", "in": "query", "required": false, "schema": { "type": "integer" } }
                ],
                "responses": {
                  "200": {
                    "description": "ok",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": { "id": { "type": "string" } }
                        }
                      }
                    }
                  }
                }
              }
            },
            "/orders": {
              "post": {
                "requestBody": {
                  "required": false,
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "properties": { "note": { "type": "string" } }
                      }
                    }
                  }
                },
                "responses": { "201": { "description": "created" } }
              }
            }
          }
        }
        """;

    private const string TargetDocument = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Target API", "version": "2.0" },
          "servers": [{ "url": "https://api.example.com" }],
          "paths": {
            "/users": {
              "get": {
                "parameters": [
                  { "name": "limit", "in": "query", "required": true, "schema": { "type": "string" } },
                  { "name": "X-Tenant", "in": "header", "required": true, "schema": { "type": "string" } }
                ],
                "responses": {
                  "200": {
                    "description": "ok",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": { "id": { "type": "integer" } }
                        }
                      }
                    }
                  },
                  "500": { "description": "error" }
                }
              }
            },
            "/orders": {
              "post": {
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "properties": {
                          "note": { "type": "string" },
                          "email": { "type": "string" }
                        },
                        "required": ["email"]
                      }
                    }
                  }
                },
                "responses": { "201": { "description": "created" } }
              }
            },
            "/health": {
              "get": { "responses": { "200": { "description": "ok" } } }
            }
          }
        }
        """;

    [Fact]
    public void ReportsBreakingAndNonBreakingChanges()
    {
        var baseDocument = OpenApiImportService.Parse(BaseDocument, "base");
        var targetDocument = OpenApiImportService.Parse(TargetDocument, "target");

        var diff = OpenApiContractDiffService.Compare(baseDocument, targetDocument);

        Assert.Equal("Base API", diff.BaseTitle);
        Assert.Equal("Target API", diff.TargetTitle);
        Assert.Equal(1, diff.AddedOperations);
        Assert.Equal(0, diff.RemovedOperations);
        Assert.Contains(
            diff.Changes,
            change => change.Kind == OpenApiContractDiffService.ParameterRequired && change.Breaking
        );
        Assert.Contains(
            diff.Changes,
            change => change.Kind == OpenApiContractDiffService.ParameterType && change.Breaking
        );
        Assert.Contains(
            diff.Changes,
            change =>
                change.Kind == OpenApiContractDiffService.ParameterAdded
                && change.Location == "header:X-Tenant"
                && change.Breaking
        );
        Assert.Contains(
            diff.Changes,
            change => change.Kind == OpenApiContractDiffService.FieldType && change.Breaking
        );
        Assert.Contains(
            diff.Changes,
            change => change.Kind == OpenApiContractDiffService.BodyRequired && change.Breaking
        );
        Assert.Contains(
            diff.Changes,
            change =>
                change.Kind == OpenApiContractDiffService.FieldAdded
                && change.Location == "body.email"
                && change.Breaking
        );
        Assert.Contains(
            diff.Changes,
            change =>
                change.Kind == OpenApiContractDiffService.ResponseAdded
                && change.Location == "response.500"
                && !change.Breaking
        );
        Assert.Contains(
            diff.Changes,
            change => change.Kind == OpenApiContractDiffService.OperationAdded && !change.Breaking
        );
        Assert.Contains("破坏性变更", diff.Conclusion, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovedOperationIsBreaking()
    {
        var baseDocument = OpenApiImportService.Parse(BaseDocument, "base");
        var targetDocument = OpenApiImportService.Parse(
            BaseDocument.Replace("/orders", "/orders-renamed", StringComparison.Ordinal),
            "target"
        );

        var diff = OpenApiContractDiffService.Compare(baseDocument, targetDocument);

        Assert.Equal(1, diff.RemovedOperations);
        Assert.Contains(
            diff.Changes,
            change =>
                change.Kind == OpenApiContractDiffService.OperationRemoved
                && change.Breaking
                && change.Operation.Contains("/orders", StringComparison.Ordinal)
        );
    }
}
