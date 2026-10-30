using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class SuiteRunReportTests
{
    private static TestRunResult Run()
    {
        var run = new TestRunResult
        {
            SuiteName = "Smoke & <check>",
            Timestamp = new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.FromHours(8)),
            Total = 3,
            Passed = 1,
            Failed = 1,
            Skipped = 1,
            Steps =
            {
                new TestStepResult
                {
                    StepId = Guid.NewGuid(),
                    Name = "health",
                    Method = "GET",
                    Passed = true,
                    StatusCode = 200,
                    ElapsedMs = 12,
                },
                new TestStepResult
                {
                    StepId = Guid.NewGuid(),
                    Name = "order | fail",
                    Method = "POST",
                    Passed = false,
                    StatusCode = 500,
                    ElapsedMs = 34,
                    Error = "boom & <bad>",
                    Assertions =
                    {
                        new TestAssertionResult
                        {
                            Label = "JSONPath data.ok = true",
                            Passed = false,
                            Detail = "实际 false",
                        },
                    },
                },
                new TestStepResult
                {
                    StepId = Guid.NewGuid(),
                    Name = "optional",
                    Method = "DELETE",
                    Passed = false,
                    Skipped = true,
                    StatusCode = 0,
                },
            },
        };
        return run;
    }

    [Fact]
    public void MarkdownReportListsStepsAndFailedAssertions()
    {
        var report = SuiteRunReport.BuildMarkdown(Run());

        Assert.Contains("Smoke & <check>", report, StringComparison.Ordinal);
        Assert.Contains("1 通过 / 1 失败 / 1 跳过，共 3 步", report, StringComparison.Ordinal);
        Assert.Contains("order \\| fail", report, StringComparison.Ordinal);
        Assert.Contains("## 失败断言", report, StringComparison.Ordinal);
        Assert.Contains("JSONPath data.ok = true", report, StringComparison.Ordinal);
        Assert.Contains("boom & <bad>", report, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlReportEncodesUserContent()
    {
        var report = SuiteRunReport.BuildHtml(Run());

        Assert.Contains("Smoke &amp; &lt;check&gt;", report, StringComparison.Ordinal);
        Assert.Contains("boom &amp; &lt;bad&gt;", report, StringComparison.Ordinal);
        Assert.Contains("class=\"pass\"", report, StringComparison.Ordinal);
        Assert.Contains("class=\"fail\"", report, StringComparison.Ordinal);
        Assert.Contains("class=\"skip\"", report, StringComparison.Ordinal);
    }

    [Fact]
    public void JunitReportMarksFailuresAndSkips()
    {
        var report = SuiteRunReport.BuildJunit(Run());

        Assert.Contains("<testsuite", report, StringComparison.Ordinal);
        Assert.Contains("name=\"Smoke &amp; &lt;check&gt;\"", report, StringComparison.Ordinal);
        Assert.Contains("<failure", report, StringComparison.Ordinal);
        Assert.Contains("<skipped", report, StringComparison.Ordinal);
        Assert.Contains("JSONPath data.ok = true", report, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonReportUsesMcpMetadata()
    {
        var report = SuiteRunReport.Build(Run(), "json");

        Assert.Contains("\"suiteName\": \"Smoke & <check>\"", report, StringComparison.Ordinal);
        Assert.Contains("\"steps\"", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportWritesASafeFileNameAndTruncatesLongContent()
    {
        var run = Run();
        run.SuiteName = "报告/回归\\套件";
        for (var index = 0; index < 60; index++)
        {
            run.Steps.Add(
                new TestStepResult
                {
                    StepId = Guid.NewGuid(),
                    Name = $"step {index}",
                    Passed = true,
                    StatusCode = 200,
                }
            );
        }

        var export = SuiteRunReport.Export(run, "md", 1_000);

        Assert.Equal("markdown", export.Format);
        Assert.DoesNotContain("/", export.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", export.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".md", export.FileName, StringComparison.Ordinal);
        Assert.True(export.Truncated);
        Assert.Contains("报告已截断", export.Content, StringComparison.Ordinal);
        Assert.True(export.Bytes > export.Content.Length);
    }

    [Fact]
    public void UnknownFormatIsRejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SuiteRunReport.Build(Run(), "pdf")
        );

        Assert.Contains("markdown", exception.Message, StringComparison.Ordinal);
    }
}
