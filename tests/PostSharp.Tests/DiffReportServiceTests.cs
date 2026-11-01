using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class DiffReportServiceTests
{
    [Fact]
    public void RunDiffMarkdownListsChangesAndWritesFile()
    {
        var diff = CreateRunDiff();

        var export = DiffReportService.ExportRunDiff(diff, "markdown");

        Assert.Equal("markdown", export.Format);
        Assert.EndsWith(".md", export.FileName, StringComparison.Ordinal);
        Assert.Contains("运行对比", export.Content, StringComparison.Ordinal);
        Assert.Contains("登录", export.Content, StringComparison.Ordinal);
        Assert.Contains("新增失败", export.Content, StringComparison.Ordinal);
        Assert.True(File.Exists(export.Path));
        Delete(export.Path);
    }

    [Fact]
    public void RunDiffHtmlEscapesUserContent()
    {
        var diff = CreateRunDiff();

        var export = DiffReportService.ExportRunDiff(diff, "html");

        Assert.StartsWith("<!doctype html>", export.Content, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", export.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", export.Content, StringComparison.Ordinal);
        Delete(export.Path);
    }

    [Fact]
    public void ContractDiffMarkdownShowsBreakingChanges()
    {
        var diff = new OpenApiContractDiff(
            "Base",
            "1.0",
            "Target",
            "2.0",
            1,
            1,
            2,
            1,
            "发现 2 处破坏性变更。",
            [
                new OpenApiContractChange(
                    OpenApiContractDiffService.OperationRemoved,
                    "GET /users",
                    string.Empty,
                    "users",
                    string.Empty,
                    true,
                    "目标文档中删除了该操作"
                ),
                new OpenApiContractChange(
                    OpenApiContractDiffService.FieldAdded,
                    "GET /users",
                    "body.email",
                    string.Empty,
                    "string · 必填",
                    true,
                    "请求体新增必填字段"
                ),
            ]
        );

        var export = DiffReportService.ExportOpenApiDiff(diff, "markdown");

        Assert.Contains("契约对比", export.Content, StringComparison.Ordinal);
        Assert.Contains("GET /users", export.Content, StringComparison.Ordinal);
        Assert.Contains("body.email", export.Content, StringComparison.Ordinal);
        Assert.Contains("| 是 |", export.Content, StringComparison.Ordinal);
        Delete(export.Path);
    }

    [Fact]
    public void ContentLimitTruncatesResponseButKeepsFile()
    {
        var diff = CreateRunDiff();

        var export = DiffReportService.ExportRunDiff(diff, "markdown", contentLimit: 1000);

        Assert.True(export.Truncated);
        Assert.Equal(1000, export.Content.Length);
        Assert.True(new FileInfo(export.Path!).Length > 1000);
        Delete(export.Path);
    }

    private static SuiteRunDiff CreateRunDiff() =>
        new(
            "登录套件",
            Guid.NewGuid(),
            DateTimeOffset.Now.AddMinutes(-5),
            Guid.NewGuid(),
            DateTimeOffset.Now,
            1,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            "新增失败 1 步。",
            [
                new SuiteRunStepDiff(
                    Guid.NewGuid(),
                    "登录",
                    SuiteRunChange.NewFailure,
                    true,
                    false,
                    200,
                    500,
                    100,
                    120,
                    20,
                    new string('x', 2000) + "<script>alert(1)</script>"
                ),
            ],
            []
        );

    private static void Delete(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
