using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class EndpointVerifierTests
{
    private static HttpResult Response(int statusCode, string body = "", string reason = "OK") =>
        new(
            HasResponse: true,
            StatusCode: statusCode,
            ReasonPhrase: reason,
            Body: body,
            IsJson: true,
            MediaType: "application/json",
            ElapsedMs: 12,
            SizeBytes: body.Length,
            Headers: [],
            Error: null
        );

    private static TestAssertion Assertion(string path, string expected) =>
        new()
        {
            Kind = "JSONPath",
            Path = path,
            Expected = expected,
        };

    [Fact]
    public void DefaultExpectationAcceptsTwoHundreds()
    {
        var verification = EndpointVerifier.Verify(
            Guid.NewGuid(),
            "健康检查",
            "GET",
            "https://example.com/health",
            Response(204),
            EndpointExpectation.Default
        );

        Assert.True(verification.Passed);
        Assert.Contains("通过", verification.Conclusion, StringComparison.Ordinal);
        Assert.Single(verification.Checks);
    }

    [Fact]
    public void ErrorStatusFailsWithExpectedAndActual()
    {
        var verification = EndpointVerifier.Verify(
            Guid.NewGuid(),
            "创建订单",
            "POST",
            "https://example.com/orders",
            Response(500, "{\"error\":true}", "Internal Server Error"),
            new EndpointExpectation(201, null, [])
        );

        Assert.False(verification.Passed);
        Assert.Equal("201", verification.Checks[0].Expected);
        Assert.Equal("500", verification.Checks[0].Actual);
        Assert.Contains(
            "期望状态 201，实际 500",
            verification.Conclusion,
            StringComparison.Ordinal
        );
        Assert.Equal("Internal Server Error", verification.StatusText);
    }

    [Fact]
    public void BodyAndAssertionsBecomeIndividualChecks()
    {
        var verification = EndpointVerifier.Verify(
            Guid.NewGuid(),
            "查询订单",
            "GET",
            "https://example.com/orders/1",
            Response(200, "{\"paid\":true}"),
            new EndpointExpectation(
                200,
                "paid",
                [Assertion("paid", "true"), Assertion("refunded", "false")]
            )
        );

        Assert.False(verification.Passed);
        Assert.Equal(4, verification.Checks.Count);
        Assert.Contains(verification.Checks, check => check.Kind == "Body" && check.Passed);
        Assert.Contains(verification.Checks, check => check.Kind == "Assertion" && check.Passed);
        Assert.Contains(
            verification.Checks,
            check =>
                check.Kind == "Assertion"
                && !check.Passed
                && check.Detail.Contains("路径不存在", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void MissingResponseReportsTheError()
    {
        var result = new HttpResult(
            HasResponse: false,
            StatusCode: 0,
            ReasonPhrase: string.Empty,
            Body: string.Empty,
            IsJson: false,
            MediaType: string.Empty,
            ElapsedMs: 3,
            SizeBytes: 0,
            Headers: [],
            Error: "连接被拒绝"
        );

        var verification = EndpointVerifier.Verify(
            Guid.NewGuid(),
            "健康检查",
            "GET",
            "https://example.com/health",
            result,
            EndpointExpectation.Default
        );

        Assert.False(verification.Passed);
        Assert.False(verification.HasResponse);
        Assert.Equal("无响应", verification.Checks[0].Actual);
        Assert.Contains("连接被拒绝", verification.Conclusion, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsePreviewIsCapped()
    {
        var body = new string('a', EndpointVerifier.PreviewLimit + 500);

        var verification = EndpointVerifier.Verify(
            Guid.NewGuid(),
            "大响应",
            "GET",
            "https://example.com/large",
            Response(200, body),
            EndpointExpectation.Default
        );

        Assert.EndsWith("（已截断）", verification.ResponsePreview, StringComparison.Ordinal);
        Assert.True(verification.ResponsePreview.Length < body.Length);
    }
}
