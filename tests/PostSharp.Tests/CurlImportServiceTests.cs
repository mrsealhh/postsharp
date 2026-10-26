using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class CurlImportServiceTests
{
    [Fact]
    public void ParsesPostmanStyleCurlWithHeadersAndJsonBody()
    {
        const string command = """
            curl --location 'https://api.example.com/items?x=1' \
            --header 'Content-Type: application/json' \
            --header 'Authorization: Bearer demo' \
            --data-raw '{"name":"测试"}'
            """;

        var result = CurlImportService.Parse(command);
        var request = result.Request;

        Assert.Equal("items", request.Name);
        Assert.Equal("POST", request.Method);
        Assert.Equal("https://api.example.com/items?x=1", request.Url);
        Assert.Equal("Json", request.BodyMode);
        Assert.Equal("{\"name\":\"测试\"}", request.Body);
        Assert.Equal(2, request.Headers.Count);
        Assert.Contains(
            request.Headers,
            header => header.Key == "Authorization" && header.Value == "Bearer demo"
        );
    }

    [Fact]
    public void ParsesMultipartBasicAuthAndTimeout()
    {
        const string command =
            "curl -X POST 'https://api.example.com/upload' -F 'title=hello' "
            + "-F 'file=@C:\\tmp\\demo.txt;type=text/plain' "
            + "--form-string 'literal=@not-a-file' -u 'user:pass' --max-time 5";

        var result = CurlImportService.Parse(command);
        var request = result.Request;

        Assert.Equal("POST", request.Method);
        Assert.Equal("Multipart", request.BodyMode);
        Assert.Equal("file", request.UploadFieldName);
        Assert.Equal("C:\\tmp\\demo.txt", request.UploadFilePath);
        Assert.Equal(2, request.FormFields.Count);
        Assert.Contains(
            request.FormFields,
            field => field.Key == "literal" && field.Value == "@not-a-file"
        );
        Assert.Equal(5, request.TimeoutSeconds);
        Assert.Contains(
            request.Headers,
            header => header.Key == "Authorization" && header.Value == "Basic dXNlcjpwYXNz"
        );
    }

    [Fact]
    public void UsesFormContentTypeForBareData()
    {
        const string command = "curl 'https://api.example.com/form' -d 'a=1&b=2'";

        var result = CurlImportService.Parse(command);

        Assert.Equal("Text", result.Request.BodyMode);
        Assert.Equal("a=1&b=2", result.Request.Body);
        Assert.Contains(
            result.Request.Headers,
            header =>
                header.Key == "Content-Type" && header.Value == "application/x-www-form-urlencoded"
        );
    }

    [Fact]
    public void MovesGetDataIntoQueryParameters()
    {
        const string command = "curl -G 'https://api.example.com/search' -d 'q=hello&page=2'";

        var result = CurlImportService.Parse(command);
        var request = result.Request;

        Assert.Equal("GET", request.Method);
        Assert.Equal("None", request.BodyMode);
        Assert.Equal(2, request.Params.Count);
        Assert.Equal("q", request.Params[0].Key);
        Assert.Equal("page", request.Params[1].Key);
    }

    [Fact]
    public void RejectsCommandsWithoutCurlOrUrl()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CurlImportService.Parse("wget https://api.example.com")
        );
        Assert.Throws<InvalidOperationException>(() =>
            CurlImportService.Parse("curl -X POST -H 'Accept: application/json'")
        );
    }
}
