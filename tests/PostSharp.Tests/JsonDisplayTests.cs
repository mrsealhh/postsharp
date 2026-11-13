using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class JsonDisplayTests
{
    [Fact]
    public void IndentsCompactJson()
    {
        var text = JsonDisplay.Beautify("""{"token":"{{token}}","sql":"select 1"}""");

        Assert.Contains(Environment.NewLine, text);
        Assert.Contains("  \"token\": \"{{token}}\"", text);
    }

    [Fact]
    public void KeepsJsonThatDoesNotParse()
    {
        const string body = """{ "token": {{token}}, "sign": "{{sign}}" }""";

        Assert.Equal(body, JsonDisplay.Beautify(body));
    }

    [Fact]
    public void KeepsNonJsonText()
    {
        Assert.Equal("<GetUser />", JsonDisplay.Beautify("<GetUser />"));
        Assert.Equal("plain text", JsonDisplay.Beautify("plain text"));
    }

    [Fact]
    public void ReturnsEmptyForBlankInput()
    {
        Assert.Equal(string.Empty, JsonDisplay.Beautify(null));
        Assert.Equal(string.Empty, JsonDisplay.Beautify("   "));
    }
}
