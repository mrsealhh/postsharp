using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class InsomniaImportServiceTests
{
    [Fact]
    public void ImportsGroupsRequestsEnvironmentAndAuth()
    {
        const string json = """
            {
              "_type": "export",
              "__export_format": 4,
              "resources": [
                {
                  "_id": "wrk_1",
                  "_type": "workspace",
                  "name": "Demo API"
                },
                {
                  "_id": "fld_1",
                  "parentId": "wrk_1",
                  "_type": "request_group",
                  "name": "Users"
                },
                {
                  "_id": "req_1",
                  "parentId": "fld_1",
                  "_type": "request",
                  "name": "List",
                  "method": "GET",
                  "url": "{{baseUrl}}/users",
                  "parameters": [ { "name": "page", "value": "1" } ],
                  "headers": [],
                  "authentication": {}
                },
                {
                  "_id": "req_2",
                  "parentId": "wrk_1",
                  "_type": "request",
                  "name": "Create",
                  "method": "POST",
                  "url": "https://api.example.com/users",
                  "body": {
                    "mimeType": "application/json",
                    "text": "{\"name\":\"demo\"}"
                  },
                  "authentication": {
                    "type": "bearer",
                    "token": "{{token}}"
                  }
                },
                {
                  "_id": "env_1",
                  "_type": "environment",
                  "name": "Base Environment",
                  "data": {
                    "baseUrl": "https://api.example.com",
                    "token": "demo-token"
                  }
                }
              ]
            }
            """;

        var result = InsomniaImportService.Parse(json, "insomnia.json");

        Assert.Equal("Demo API", result.Root.Name);
        Assert.Equal(2, result.RequestCount);
        Assert.Equal(1, result.FolderCount);
        var folder = Assert.Single(result.Root.Children);
        Assert.Equal("Users", folder.Name);
        var list = Assert.Single(folder.Requests);
        Assert.Equal("page", Assert.Single(list.Params).Key);

        var create = Assert.Single(result.Root.Requests);
        Assert.Equal("Json", create.BodyMode);
        Assert.Equal("{\"name\":\"demo\"}", create.Body);
        Assert.Contains(
            create.Headers,
            header => header.Key == "Authorization" && header.Value == "Bearer {{token}}"
        );

        Assert.NotNull(result.Environment);
        Assert.Equal(2, result.Environment.Variables.Count);
        Assert.Contains(
            result.Environment.Variables,
            variable => variable.Key == "baseUrl" && variable.Value == "https://api.example.com"
        );
    }

    [Fact]
    public void RejectsV5OrUnrelatedJson()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            InsomniaImportService.Parse("{\"type\":\"collection.insomnia.rest/5.0\"}", "x.json")
        );

        Assert.Contains("v4", exception.Message);
    }
}
