using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class SseTextFormatterTests
{
    [Fact]
    public void FormatsJsonWithIndentation()
    {
        var result = SseTextFormatter.Apply("""{"message":"hello","count":2}""", compact: false);

        Assert.Equal("JSON", result.Kind);
        Assert.Contains('\n', result.Text);
        Assert.Contains("\"message\": \"hello\"", result.Text);
    }

    [Fact]
    public void CompactsJsonToSingleLine()
    {
        var result = SseTextFormatter.Apply(
            """
            {
              "message": "hello",
              "count": 2
            }
            """,
            compact: true
        );

        Assert.Equal("JSON", result.Kind);
        Assert.DoesNotContain('\n', result.Text);
        Assert.Equal("""{"message":"hello","count":2}""", result.Text);
    }

    [Fact]
    public void FormatsXml()
    {
        var result = SseTextFormatter.Apply("<root><item>one</item></root>", compact: false);

        Assert.Equal("XML", result.Kind);
        Assert.Contains('\n', result.Text);
    }

    [Fact]
    public void CompactsMarkdownByRemovingBlankLinesAndTrailingSpaces()
    {
        var result = SseTextFormatter.Apply("第一段  \n\n\n第二段\n\n- 项目  \n", compact: true);

        Assert.Equal("文本", result.Kind);
        Assert.Equal("第一段\n第二段\n- 项目", result.Text);
    }

    [Fact]
    public void FormattingTextCollapsesExcessBlankLines()
    {
        var result = SseTextFormatter.Apply("第一段\n\n\n\n第二段", compact: false);

        Assert.Equal("文本", result.Kind);
        Assert.Equal("第一段\n\n第二段", result.Text);
    }

    [Fact]
    public void InvalidJsonFallsBackToTextFormatting()
    {
        var result = SseTextFormatter.Apply("{not-json}\n\n\n下一段", compact: false);

        Assert.Equal("文本", result.Kind);
        Assert.Equal("{not-json}\n\n下一段", result.Text);
    }

    [Fact]
    public void EmptyInputIsReturnedUnchanged()
    {
        var result = SseTextFormatter.Apply("  ", compact: true);

        Assert.Equal("文本", result.Kind);
        Assert.Equal("  ", result.Text);
    }
}
