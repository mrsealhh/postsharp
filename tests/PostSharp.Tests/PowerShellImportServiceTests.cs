using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class PowerShellImportServiceTests
{
    [Fact]
    public void ParsesInvokeRestMethodJsonCommand()
    {
        const string command = """
            Invoke-RestMethod -Uri 'https://api.example.com/api/orders' -Method POST `
              -Headers @{ 'Accept' = 'application/json'; 'X-Trace' = 'trace-1' } `
              -ContentType 'application/json' `
              -Body '{"name":"demo"}' -TimeoutSec 30
            """;

        var result = PowerShellImportService.Parse(command);

        Assert.Equal("POST", result.Request.Method);
        Assert.Equal("https://api.example.com/api/orders", result.Request.Url);
        Assert.Equal("Json", result.Request.BodyMode);
        Assert.Equal("""{"name":"demo"}""", result.Request.Body);
        Assert.Equal(30, result.Request.TimeoutSeconds);
        Assert.Contains(
            result.Request.Headers,
            header => header.Key == "X-Trace" && header.Value == "trace-1"
        );
    }

    [Fact]
    public void ParsesPowerShellFormFiles()
    {
        const string command = """
            Invoke-RestMethod -Uri 'https://api.example.com/upload' -Method POST -Form @{
              'note' = 'demo';
              'file' = Get-Item 'C:\temp\a.txt'
            }
            """;

        var result = PowerShellImportService.Parse(command);

        Assert.Equal("Multipart", result.Request.BodyMode);
        Assert.Equal("file", result.Request.UploadFieldName);
        Assert.Equal("C:\\temp\\a.txt", result.Request.UploadFilePath);
        Assert.Contains(result.Request.FormFields, field => field.Key == "note");
    }

    [Fact]
    public void BuildsPowerShellExport()
    {
        var request = new RequestItem
        {
            Name = "demo",
            Method = "POST",
            Url = "https://api.example.com/api/orders",
            BodyMode = "Json",
            Body = """{"name":"demo"}""",
        };
        request.Headers.AddRow("Accept", "application/json");

        var script = RequestComposer.BuildPowerShell(request, null);

        Assert.Contains("Invoke-RestMethod", script);
        Assert.Contains("-Method POST", script);
        Assert.Contains("$headers = @{", script);
        Assert.Contains("-Body @'", script);
        Assert.Contains(""""{"name":"demo"}"""", script);
    }
}
