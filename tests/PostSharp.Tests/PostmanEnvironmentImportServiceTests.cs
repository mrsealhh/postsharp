using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class PostmanEnvironmentImportServiceTests
{
    [Fact]
    public void ImportsEnvironmentValuesAndPreservesDisabledRows()
    {
        const string environment = """
            {
              "name": "测试环境",
              "values": [
                { "key": "baseUrl", "value": "https://api.example.com", "enabled": true },
                { "key": "token", "value": "secret", "type": "secret" },
                { "key": "old", "value": "1", "enabled": false },
                { "key": "baseUrl", "value": "duplicate", "enabled": true }
              ]
            }
            """;

        var result = PostmanEnvironmentImportService.Parse(environment, "test.json");

        Assert.Equal("测试环境", result.Environment.Name);
        Assert.Equal(3, result.Environment.Variables.Count);
        Assert.Equal("baseUrl", result.Environment.Variables[0].Key);
        Assert.Equal("secret", result.Environment.Variables[1].Value);
        Assert.False(result.Environment.Variables[2].IsEnabled);
        Assert.Contains(result.Warnings, warning => warning.Contains("重复"));
    }

    [Fact]
    public void RejectsNonEnvironmentJson()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PostmanEnvironmentImportService.Parse("{\"info\":{}}", "collection.json")
        );

        Assert.Contains("环境", exception.Message);
    }
}
