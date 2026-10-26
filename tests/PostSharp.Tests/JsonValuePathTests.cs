using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class JsonValuePathTests
{
    [Fact]
    public void EvaluatesNestedObjectAndArrayIndex()
    {
        const string json = """
            {
              "data": {
                "token": "abc",
                "items": [
                  { "id": 42 }
                ]
              }
            }
            """;

        Assert.True(JsonValuePath.TryEvaluate(json, "$.data.token", out var token));
        Assert.Equal("abc", token);
        Assert.True(JsonValuePath.TryEvaluate(json, "data.items[0].id", out var id));
        Assert.Equal("42", id);
    }

    [Fact]
    public void ReturnsFalseForMissingPath()
    {
        Assert.False(JsonValuePath.TryEvaluate("{\"data\":{}}", "$.data.missing", out _));
        Assert.False(JsonValuePath.TryEvaluate("not json", "$.data", out _));
    }

    [Fact]
    public void EvaluatesSuiteAssertions()
    {
        var result = new HttpResult(
            HasResponse: true,
            StatusCode: 200,
            ReasonPhrase: "OK",
            Body: """{"data":{"token":"abc"}}""",
            IsJson: true,
            MediaType: "application/json",
            ElapsedMs: 10,
            SizeBytes: 20,
            Headers: new[] { new ResponseHeaderView("X-Trace", "trace-1") },
            Error: null
        );
        var assertions = new[]
        {
            new TestAssertion
            {
                Kind = "JSONPath",
                Path = "$.data.token",
                Expected = "abc",
            },
            new TestAssertion
            {
                Kind = "Header",
                Path = "X-Trace",
                Expected = "trace-1",
            },
            new TestAssertion
            {
                Kind = "JSONPath",
                Path = "$.data.missing",
                Expected = "x",
            },
        };

        var results = SuiteAssertionEvaluator.Evaluate(assertions, result);

        Assert.Equal(3, results.Count);
        Assert.True(results[0].Passed);
        Assert.True(results[1].Passed);
        Assert.False(results[2].Passed);
        Assert.Contains("路径不存在", results[2].Detail);
    }
}
