using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class PostmanImportServiceTests
{
    [Fact]
    public void ParsesNestedFoldersAuthQueryAndCollectionVariables()
    {
        const string json = """
            {
              "info": {
                "name": "示例集合",
                "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
              },
              "variable": [
                { "key": "baseUrl", "value": "https://api.example.com" },
                { "key": "token", "value": "demo-token" }
              ],
              "item": [
                {
                  "name": "用户",
                  "item": [
                    {
                      "name": "列表",
                      "request": {
                        "method": "GET",
                        "header": [
                          { "key": "Accept", "value": "application/json", "disabled": true }
                        ],
                        "url": {
                          "raw": "{{baseUrl}}/api/users?id=1",
                          "query": [
                            { "key": "id", "value": "1" },
                            { "key": "debug", "value": "true", "disabled": true }
                          ]
                        }
                      }
                    }
                  ]
                },
                {
                  "name": "创建",
                  "request": {
                    "method": "POST",
                    "auth": {
                      "type": "bearer",
                      "bearer": [ { "key": "token", "value": "{{token}}" } ]
                    },
                    "header": [],
                    "body": {
                      "mode": "raw",
                      "raw": "{\"name\":\"test\"}",
                      "options": { "raw": { "language": "json" } }
                    },
                    "url": { "raw": "https://api.example.com/api/users" }
                  }
                }
              ]
            }
            """;

        var result = PostmanImportService.Parse(json, "fixture.json");

        Assert.Equal("示例集合", result.Root.Name);
        Assert.Equal(2, result.RequestCount);
        Assert.Equal(1, result.FolderCount);
        var folder = Assert.Single(result.Root.Children);
        var list = Assert.Single(folder.Requests);
        Assert.Equal("{{baseUrl}}/api/users", list.Url);
        Assert.Equal(2, list.Params.Count);
        Assert.False(list.Params[1].IsEnabled);
        Assert.Equal("Accept", Assert.Single(list.Headers).Key);
        Assert.False(list.Headers[0].IsEnabled);

        var create = Assert.Single(result.Root.Requests);
        Assert.Equal("Json", create.BodyMode);
        Assert.Equal("{\"name\":\"test\"}", create.Body);
        Assert.Contains(
            create.Headers,
            header => header.Key == "Authorization" && header.Value == "Bearer {{token}}"
        );

        Assert.NotNull(result.Environment);
        Assert.Equal("示例集合 变量", result.Environment.Name);
        Assert.Equal(2, result.Environment.Variables.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("集合变量"));
    }

    [Fact]
    public void ConvertsUrlEncodedBodyAndFormDataFiles()
    {
        const string json = """
            {
              "info": {
                "name": "表单集合",
                "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
              },
              "item": [
                {
                  "name": "urlencoded",
                  "request": {
                    "method": "POST",
                    "url": "https://api.example.com/form",
                    "body": {
                      "mode": "urlencoded",
                      "urlencoded": [
                        { "key": "name", "value": "hello world" },
                        { "key": "skip", "value": "x", "disabled": true }
                      ]
                    }
                  }
                },
                {
                  "name": "upload",
                  "request": {
                    "method": "POST",
                    "url": "https://api.example.com/upload",
                    "body": {
                      "mode": "formdata",
                      "formdata": [
                        { "key": "title", "value": "demo", "type": "text" },
                        { "key": "file", "type": "file", "src": "/C:/tmp/demo.txt" },
                        { "key": "second", "type": "file", "src": "C:/tmp/second.txt" }
                      ]
                    }
                  }
                }
              ]
            }
            """;

        var result = PostmanImportService.Parse(json, "form.json");
        var encoded = result.Root.Requests[0];
        Assert.Equal("Text", encoded.BodyMode);
        Assert.Equal("name=hello%20world", encoded.Body);
        Assert.Contains(
            encoded.Headers,
            header =>
                header.Key == "Content-Type" && header.Value == "application/x-www-form-urlencoded"
        );

        var upload = result.Root.Requests[1];
        Assert.Equal("Multipart", upload.BodyMode);
        Assert.Equal("file", upload.UploadFieldName);
        Assert.Equal("C:/tmp/demo.txt", upload.UploadFilePath);
        Assert.Equal("title", Assert.Single(upload.FormFields).Key);
        Assert.Contains(result.Warnings, warning => warning.Contains("多个文件"));
    }

    [Fact]
    public void FlattensFoldersBeyondTheWorkspaceDepthLimit()
    {
        const string json = """
            {
              "info": {
                "name": "深层集合",
                "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
              },
              "item": [
                {
                  "name": "一级",
                  "item": [
                    {
                      "name": "二级",
                      "item": [
                        {
                          "name": "三级",
                          "item": [
                            {
                              "name": "四级",
                              "item": [
                                {
                                  "name": "五级",
                                  "item": [
                                    {
                                      "name": "六级",
                                      "item": [
                                        {
                                          "name": "深处请求",
                                          "request": {
                                            "method": "GET",
                                            "url": "https://api.example.com/deep"
                                          }
                                        }
                                      ]
                                    }
                                  ]
                                }
                              ]
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        var result = PostmanImportService.Parse(json, "deep.json");

        Assert.Equal(1, result.RequestCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("层级超过"));
        Assert.Contains(
            FlattenRequests(result.Root),
            request => request.Name.Contains("六级") && request.Name.Contains("深处请求")
        );
    }

    [Fact]
    public void RejectsDocumentsThatAreNotPostmanCollections()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PostmanImportService.Parse("{\"openapi\":\"3.0.0\"}", "openapi.json")
        );

        Assert.Contains("Postman", exception.Message);
    }

    private static IEnumerable<ImportedRequest> FlattenRequests(ImportedCollection collection)
    {
        foreach (var request in collection.Requests)
        {
            yield return request;
        }

        foreach (var child in collection.Children)
        {
            foreach (var request in FlattenRequests(child))
            {
                yield return request;
            }
        }
    }
}
