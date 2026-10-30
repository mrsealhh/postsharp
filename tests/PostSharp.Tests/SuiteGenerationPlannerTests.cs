using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class SuiteGenerationPlannerTests
{
    private static RequestItem Request(string name, string method, string url) =>
        new()
        {
            Name = name,
            Method = method,
            Url = url,
        };

    private static HistoryEntry History(
        string method,
        string url,
        int statusCode,
        DateTimeOffset timestamp
    ) =>
        new()
        {
            Method = method,
            Url = url,
            StatusCode = statusCode,
            Timestamp = timestamp,
        };

    [Fact]
    public void PinsTheLatestSuccessfulStatus()
    {
        var request = Request("创建订单", "POST", "https://example.com/orders");
        var history = new List<HistoryEntry>
        {
            History(
                "POST",
                "https://example.com/orders",
                500,
                new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero)
            ),
            History(
                "POST",
                "https://example.com/orders",
                201,
                new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero)
            ),
        };

        var draft = SuiteGenerationPlanner.Plan([request], history, assertObservedStatus: true);

        var step = Assert.Single(draft);
        Assert.Equal(201, step.ExpectedStatus);
        Assert.Contains("已固定为期望状态", step.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotPinAnErrorStatus()
    {
        var request = Request("创建订单", "POST", "https://example.com/orders");
        var history = new List<HistoryEntry>
        {
            History("POST", "https://example.com/orders", 500, DateTimeOffset.Now),
        };

        var draft = SuiteGenerationPlanner.Plan([request], history, assertObservedStatus: true);

        Assert.Null(draft[0].ExpectedStatus);
        Assert.Contains("失败状态不作为期望值", draft[0].Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutHistoryOnlyNetworkSuccessIsChecked()
    {
        var draft = SuiteGenerationPlanner.Plan(
            [Request("健康检查", "GET", "https://example.com/health")],
            [],
            assertObservedStatus: true
        );

        Assert.Null(draft[0].ExpectedStatus);
        Assert.Contains("没有发送历史", draft[0].Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void DisablingObservedStatusKeepsTheEvidence()
    {
        var request = Request("健康检查", "GET", "https://example.com/health");
        var history = new List<HistoryEntry>
        {
            History("GET", "https://example.com/health", 204, DateTimeOffset.Now),
        };

        var draft = SuiteGenerationPlanner.Plan([request], history, assertObservedStatus: false);

        Assert.Null(draft[0].ExpectedStatus);
        Assert.Contains("返回 204", draft[0].Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoresHistoryFromAnotherMethodOrUrl()
    {
        var request = Request("健康检查", "GET", "https://example.com/health");
        var history = new List<HistoryEntry>
        {
            History("POST", "https://example.com/health", 201, DateTimeOffset.Now),
            History("GET", "https://example.com/other", 200, DateTimeOffset.Now),
        };

        var draft = SuiteGenerationPlanner.Plan([request], history, assertObservedStatus: true);

        Assert.Null(draft[0].ExpectedStatus);
    }
}
