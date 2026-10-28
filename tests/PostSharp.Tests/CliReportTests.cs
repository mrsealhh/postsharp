using PostSharp.Cli;
using Xunit;

namespace PostSharp.Tests;

public class CliReportTests
{
    private const string RunJson = """
        {
          "suiteName": "Smoke & <check>",
          "steps": [
            { "name": "health", "passed": true, "elapsedMs": 12, "statusCode": 200 },
            { "name": "order | fail", "passed": false, "elapsedMs": 34, "statusCode": 500, "error": "boom & <bad>" },
            { "name": "optional", "passed": false, "skipped": true, "elapsedMs": 0, "statusCode": 0 }
          ]
        }
        """;

    [Fact]
    public void MarkdownReportSummarizesStepsAndEscapesTableText()
    {
        var report = SuiteReport.BuildMarkdown(RunJson);

        Assert.Contains("Smoke & <check>", report, StringComparison.Ordinal);
        Assert.Contains("1 通过 / 1 失败 / 1 跳过，共 3 步", report, StringComparison.Ordinal);
        Assert.Contains("order \\| fail", report, StringComparison.Ordinal);
        Assert.Contains("boom & <bad>", report, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlReportEncodesUserContent()
    {
        var report = SuiteReport.BuildHtml(RunJson);

        Assert.Contains("Smoke &amp; &lt;check&gt;", report, StringComparison.Ordinal);
        Assert.Contains("boom &amp; &lt;bad&gt;", report, StringComparison.Ordinal);
        Assert.Contains("class=\"pass\"", report, StringComparison.Ordinal);
        Assert.Contains("class=\"fail\"", report, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyRunProducesReadableReport()
    {
        var report = SuiteReport.BuildMarkdown("""{ "suiteName": "Empty", "steps": [] }""");

        Assert.Contains("没有步骤结果", report, StringComparison.Ordinal);
    }
}
