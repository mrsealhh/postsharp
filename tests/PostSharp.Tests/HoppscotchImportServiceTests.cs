using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class HoppscotchImportServiceTests
{
    [Fact]
    public void ImportsFoldersParamsBodyAndAuth()
    {
        const string json = """
            {
              "v": 1,
              "name": "Quotes API",
              "folders": [
                {
                  "name": "Quotes",
                  "folders": [],
                  "requests": [
                    {
                      "name": "Get quotes",
                      "method": "GET",
                      "endpoint": "{{base_url}}/quotes",
                      "params": [ { "key": "page", "value": "1", "active": true } ],
                      "headers": [],
                      "body": { "contentType": "", "body": "" },
                      "auth": { "authType": "", "authActive": true }
                    }
                  ]
                }
              ],
              "requests": [
                {
                  "name": "Create quote",
                  "method": "POST",
                  "endpoint": "https://api.example.com/quotes",
                  "params": [],
                  "headers": [ { "key": "Accept", "value": "application/json", "active": true } ],
                  "body": {
                    "contentType": "application/json",
                    "body": "{\"quote\":\"demo\"}"
                  },
                  "auth": {
                    "authType": "bearer",
                    "token": "{{token}}",
                    "authActive": true
                  }
                }
              ]
            }
            """;

        var result = HoppscotchImportService.Parse(json, "hoppscotch.json");

        Assert.Equal("Quotes API", result.Root.Name);
        Assert.Equal(2, result.RequestCount);
        Assert.Equal(1, result.FolderCount);
        var folder = Assert.Single(result.Root.Children);
        Assert.Equal("page", Assert.Single(Assert.Single(folder.Requests).Params).Key);

        var create = Assert.Single(result.Root.Requests);
        Assert.Equal("Json", create.BodyMode);
        Assert.Equal("{\"quote\":\"demo\"}", create.Body);
        Assert.Contains(
            create.Headers,
            header => header.Key == "Accept" && header.Value == "application/json"
        );
        Assert.Contains(
            create.Headers,
            header => header.Key == "Authorization" && header.Value == "Bearer {{token}}"
        );
    }

    [Fact]
    public void RejectsEmptyOrUnrecognizedJson()
    {
        Assert.Throws<InvalidOperationException>(() =>
            HoppscotchImportService.Parse("[]", "empty.json")
        );
        Assert.Throws<InvalidOperationException>(() =>
            HoppscotchImportService.Parse("{\"foo\":1}", "bad.json")
        );
    }
}
