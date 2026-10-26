using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class HarImportServiceTests
{
    [Fact]
    public void ImportsXhrAndFetchEntriesAndSkipsStaticResources()
    {
        const string har = """
            {
              "log": {
                "version": "1.2",
                "pages": [ { "id": "page_1", "title": "用户 API" } ],
                "entries": [
                  {
                    "pageref": "page_1",
                    "_resourceType": "xhr",
                    "request": {
                      "method": "POST",
                      "url": "https://api.example.com/api/users?page=1",
                      "queryString": [ { "name": "page", "value": "1" } ],
                      "header": [
                        { "name": "Content-Type", "value": "application/json" },
                        { "name": "Authorization", "value": "Bearer demo" },
                        { "name": "Host", "value": "api.example.com" }
                      ],
                      "postData": {
                        "mimeType": "application/json",
                        "text": "{\"name\":\"test\"}"
                      }
                    },
                    "response": { "content": { "mimeType": "application/json" } }
                  },
                  {
                    "pageref": "page_1",
                    "_resourceType": "xhr",
                    "request": {
                      "method": "POST",
                      "url": "https://api.example.com/api/users?page=1",
                      "queryString": [ { "name": "page", "value": "1" } ],
                      "header": []
                    }
                  },
                  {
                    "pageref": "page_1",
                    "_resourceType": "image",
                    "request": {
                      "method": "GET",
                      "url": "https://api.example.com/logo.png",
                      "header": []
                    },
                    "response": { "content": { "mimeType": "image/png" } }
                  },
                  {
                    "pageref": "page_1",
                    "_resourceType": "xhr",
                    "request": {
                      "method": "GET",
                      "url": "https://api.example.com/api/detail/42",
                      "header": [],
                      "cookies": [ { "name": "sid", "value": "abc" } ]
                    },
                    "response": { "content": { "mimeType": "application/json" } }
                  },
                  {
                    "pageref": "page_1",
                    "_resourceType": "fetch",
                    "request": {
                      "method": "POST",
                      "url": "https://api.example.com/api/upload",
                      "header": [],
                      "postData": {
                        "mimeType": "multipart/form-data; boundary=old",
                        "params": [
                          { "name": "title", "value": "demo" },
                          { "name": "file", "fileName": "demo.txt" }
                        ]
                      }
                    }
                  }
                ]
              }
            }
            """;

        var result = HarImportService.Parse(har, "sample.har");

        Assert.Equal("sample", result.Root.Name);
        Assert.Equal(3, result.RequestCount);
        Assert.Equal(1, result.FolderCount);
        var group = Assert.Single(result.Root.Children);
        Assert.Equal("用户 API", group.Name);

        var create = group.Requests[0];
        Assert.Equal("POST", create.Method);
        Assert.Equal("https://api.example.com/api/users", create.Url);
        Assert.Equal("Json", create.BodyMode);
        Assert.Equal("{\"name\":\"test\"}", create.Body);
        Assert.Equal("page", Assert.Single(create.Params).Key);
        Assert.Contains(create.Headers, header => header.Key == "Authorization");
        Assert.DoesNotContain(create.Headers, header => header.Key == "Host");

        var detail = group.Requests[1];
        Assert.Equal("Cookie", Assert.Single(detail.Headers).Key);
        Assert.Equal("sid=abc", detail.Headers[0].Value);

        var upload = group.Requests[2];
        Assert.Equal("Multipart", upload.BodyMode);
        Assert.Equal("title", Assert.Single(upload.FormFields).Key);
        Assert.Contains(result.Warnings, warning => warning.Contains("静态资源"));
        Assert.Contains(result.Warnings, warning => warning.Contains("重复"));
        Assert.Contains(result.Warnings, warning => warning.Contains("Cookie"));
        Assert.Contains(result.Warnings, warning => warning.Contains("文件字段"));
    }

    [Fact]
    public void CanIncludeStaticResources()
    {
        const string har = """
            {
              "log": {
                "entries": [
                  {
                    "_resourceType": "xhr",
                    "request": { "method": "GET", "url": "https://api.example.com/api" }
                  },
                  {
                    "_resourceType": "image",
                    "request": { "method": "GET", "url": "https://api.example.com/a.png" }
                  }
                ]
              }
            }
            """;

        var result = HarImportService.Parse(har, "sample.har", includeStaticResources: true);

        Assert.Equal(2, result.RequestCount);
        Assert.Equal("api.example.com", Assert.Single(result.Root.Children).Name);
    }

    [Fact]
    public void RejectsInvalidHar()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            HarImportService.Parse("{\"foo\":1}", "bad.har")
        );

        Assert.Contains("HAR", exception.Message);
    }
}
