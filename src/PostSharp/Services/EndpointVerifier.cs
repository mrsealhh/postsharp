using System.Globalization;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>What a caller expects from one endpoint call; every check becomes a structured result.</summary>
public sealed record EndpointExpectation(
    int? ExpectedStatus,
    string? BodyContains,
    IReadOnlyList<TestAssertion> Assertions
)
{
    public static EndpointExpectation Default { get; } = new(null, null, []);
}

/// <summary>One expected-versus-actual check produced by <see cref="EndpointVerifier"/>.</summary>
public sealed record EndpointCheck(
    string Kind,
    string Expected,
    string Actual,
    bool Passed,
    string Detail
);

/// <summary>A single intent-level answer to "is this endpoint still working?".</summary>
public sealed record EndpointVerification(
    Guid RequestId,
    string Name,
    string Method,
    string Url,
    bool Passed,
    bool HasResponse,
    int StatusCode,
    string StatusText,
    long ElapsedMs,
    string Conclusion,
    IReadOnlyList<EndpointCheck> Checks,
    string ResponsePreview,
    string? Error
);

/// <summary>
/// Turns a sent response into a pass/fail conclusion with one check per expectation, so an AI client
/// needs a single tool call instead of combining send_request with its own comparisons.
/// </summary>
public static class EndpointVerifier
{
    public const int PreviewLimit = 2000;

    public static EndpointVerification Verify(
        Guid requestId,
        string name,
        string method,
        string url,
        HttpResult result,
        EndpointExpectation expectation
    )
    {
        var checks = new List<EndpointCheck>();
        var actualStatus =
            result.HasResponse ? result.StatusCode.ToString(CultureInfo.InvariantCulture)
            : string.IsNullOrWhiteSpace(result.ReasonPhrase) ? "无响应"
            : result.ReasonPhrase;
        var statusPassed = expectation.ExpectedStatus is { } expected
            ? result.HasResponse && result.StatusCode == expected
            : result.HasResponse && result.StatusCode is >= 200 and < 400;
        var statusExpected = expectation.ExpectedStatus is { } pinned
            ? pinned.ToString(CultureInfo.InvariantCulture)
            : "2xx/3xx";
        checks.Add(
            new EndpointCheck(
                "Status",
                statusExpected,
                actualStatus,
                statusPassed,
                statusPassed
                    ? $"状态 {actualStatus} 符合预期"
                    : $"期望状态 {statusExpected}，实际 {actualStatus}"
            )
        );

        if (!string.IsNullOrWhiteSpace(expectation.BodyContains))
        {
            var bodyPassed =
                result.HasResponse
                && result.Body.Contains(
                    expectation.BodyContains,
                    StringComparison.OrdinalIgnoreCase
                );
            checks.Add(
                new EndpointCheck(
                    "Body",
                    expectation.BodyContains,
                    bodyPassed ? "已包含" : "未包含",
                    bodyPassed,
                    bodyPassed
                        ? $"响应体包含 \"{expectation.BodyContains}\""
                        : $"响应体不包含 \"{expectation.BodyContains}\""
                )
            );
        }

        foreach (var assertion in SuiteAssertionEvaluator.Evaluate(expectation.Assertions, result))
        {
            checks.Add(
                new EndpointCheck(
                    "Assertion",
                    assertion.Label,
                    assertion.Detail,
                    assertion.Passed,
                    assertion.Passed
                        ? $"{assertion.Label} 通过"
                        : $"{assertion.Label} 失败：{assertion.Detail}"
                )
            );
        }

        var passed = checks.All(check => check.Passed);
        var error = result.Error;
        var conclusion = passed
            ? $"通过：状态 {actualStatus}，耗时 {result.ElapsedMs} ms，{checks.Count} 项检查全部通过。"
            : ConclusionText(checks, error);
        var preview = SecretMasker.MaskPreview(result.Body, PreviewLimit, []);

        return new EndpointVerification(
            requestId,
            name,
            method,
            url,
            passed,
            result.HasResponse,
            result.StatusCode,
            result.ReasonPhrase,
            result.ElapsedMs,
            conclusion,
            checks,
            preview,
            error
        );
    }

    private static string ConclusionText(IReadOnlyList<EndpointCheck> checks, string? error)
    {
        var failed = checks.Where(check => !check.Passed).ToList();
        if (failed.Count == 0)
        {
            return error is null ? "失败：没有收到可判定的响应。" : $"失败：{error}";
        }

        var details = string.Join("；", failed.Select(check => check.Detail));
        return error is null ? $"失败：{details}。" : $"失败：{error}；{details}。";
    }
}
