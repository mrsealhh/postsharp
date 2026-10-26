using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Evaluates suite assertions against a completed HTTP response.</summary>
public static class SuiteAssertionEvaluator
{
    public static IReadOnlyList<TestAssertionResult> Evaluate(
        IReadOnlyList<TestAssertion> assertions,
        HttpResult result
    )
    {
        var results = new List<TestAssertionResult>();
        foreach (var assertion in assertions)
        {
            if (assertion.Kind == "Header")
            {
                var header = result.Headers.FirstOrDefault(candidate =>
                    candidate.Key.Equals(assertion.Path, StringComparison.OrdinalIgnoreCase)
                );
                var passed =
                    header is not null
                    && string.Equals(
                        header.Value,
                        assertion.Expected,
                        StringComparison.OrdinalIgnoreCase
                    );
                results.Add(
                    new TestAssertionResult
                    {
                        Label = assertion.Label,
                        Passed = passed,
                        Detail = header is null
                            ? $"缺少响应头 {assertion.Path}"
                            : $"实际 {header.Value}",
                    }
                );
                continue;
            }

            if (JsonValuePath.TryEvaluate(result.Body, assertion.Path, out var actual))
            {
                results.Add(
                    new TestAssertionResult
                    {
                        Label = assertion.Label,
                        Passed = string.Equals(
                            actual,
                            assertion.Expected,
                            StringComparison.OrdinalIgnoreCase
                        ),
                        Detail = $"实际 {actual}",
                    }
                );
            }
            else
            {
                results.Add(
                    new TestAssertionResult
                    {
                        Label = assertion.Label,
                        Passed = false,
                        Detail = "路径不存在或响应不是 JSON",
                    }
                );
            }
        }

        return results;
    }
}
