using System.Text.Json.Nodes;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// Intent-level MCP tools: one call answers "does this endpoint work", "build a suite", "compare two
/// runs", "why did it fail" or "give me a report", instead of asking the AI to chain atomic tools.
/// </summary>
public sealed partial class McpServer
{
    private async Task<object> VerifyEndpointToolAsync(
        JsonObject arguments,
        McpAccessPolicy policy,
        CancellationToken cancellationToken
    )
    {
        var expectedStatus = OptionalInt(arguments, "expectedStatus");
        if (expectedStatus is { } status && status is < 100 or > 599)
        {
            throw new InvalidOperationException("expectedStatus 必须在 100-599 之间。");
        }

        var assertions = (ReadAssertions(arguments) ?? [])
            .Select(assertion => new TestAssertion
            {
                Kind = assertion.Kind,
                Path = assertion.Path,
                Expected = assertion.Expected,
            })
            .ToList();
        return await _workspace
            .VerifyEndpointAsync(
                RequireGuid(arguments, "requestId"),
                OptionalGuid(arguments, "environmentId"),
                new EndpointExpectation(
                    expectedStatus,
                    OptionalString(arguments, "bodyContains"),
                    assertions
                ),
                new McpCallContext("verify_endpoint", _clientName, policy.PermissionLabel),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<object> GenerateSuiteToolAsync(JsonObject arguments)
    {
        return await _workspace
            .GenerateSuiteAsync(
                OptionalString(arguments, "name") ?? "MCP 生成套件",
                ReadGuidArray(arguments, "collectionIds"),
                ReadGuidArray(arguments, "requestIds"),
                OptionalInt(arguments, "delayMs") ?? 200,
                OptionalBool(arguments, "stopOnError") ?? true,
                OptionalBool(arguments, "assertObservedStatus") ?? true,
                OptionalBool(arguments, "dryRun") ?? false
            )
            .ConfigureAwait(false);
    }

    private async Task<object> ExportReportToolAsync(JsonObject arguments)
    {
        var contentLimit =
            OptionalInt(arguments, "contentLimit") ?? SuiteRunReport.DefaultContentLimit;
        return await _workspace
            .ExportReportAsync(
                RequireGuid(arguments, "suiteId"),
                OptionalGuid(arguments, "runId"),
                OptionalString(arguments, "format") ?? "markdown",
                contentLimit
            )
            .ConfigureAwait(false);
    }

    private async Task<object> DiffRunsToolAsync(JsonObject arguments)
    {
        return await _workspace
            .DiffRunsAsync(
                RequireGuid(arguments, "suiteId"),
                RequireGuid(arguments, "baseRunId"),
                OptionalGuid(arguments, "targetRunId"),
                OptionalInt(arguments, "elapsedRegressionPercent") ?? 20
            )
            .ConfigureAwait(false);
    }

    private async Task<object> DiffOpenApiToolAsync(
        JsonObject arguments,
        CancellationToken cancellationToken
    )
    {
        return await _workspace
            .DiffOpenApiAsync(
                OptionalString(arguments, "baseUrl"),
                OptionalString(arguments, "baseContent"),
                OptionalString(arguments, "targetUrl"),
                OptionalString(arguments, "targetContent"),
                OptionalString(arguments, "baseLabel"),
                OptionalString(arguments, "targetLabel"),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<object> ExplainFailureToolAsync(JsonObject arguments)
    {
        return await _workspace
            .ExplainFailureAsync(
                RequireGuid(arguments, "suiteId"),
                OptionalGuid(arguments, "runId"),
                OptionalGuid(arguments, "stepId")
            )
            .ConfigureAwait(false);
    }

    private static JsonObject VerifyEndpointSchema() =>
        ObjectSchema(
            new JsonObject
            {
                ["requestId"] = StringSchema("要验证的请求 id"),
                ["environmentId"] = StringSchema("环境 id；省略时使用当前环境"),
                ["expectedStatus"] = IntegerSchema(
                    "期望 HTTP 状态码；省略时接受 2xx/3xx",
                    100,
                    599
                ),
                ["bodyContains"] = StringSchema("响应体必须包含的文本"),
                ["assertions"] = AssertionsSchema(),
            },
            "requestId"
        );

    private static JsonObject GenerateSuiteSchema() =>
        ObjectSchema(
            new JsonObject
            {
                ["name"] = StringSchema("套件名称，默认「MCP 生成套件」"),
                ["collectionIds"] = GuidArraySchema("按顺序展开的集合 id（含子集合）"),
                ["requestIds"] = GuidArraySchema(
                    "按顺序加入的请求 id；与 collectionIds 二选一或混用"
                ),
                ["delayMs"] = IntegerSchema("每个步骤后的延迟毫秒，默认 200", 0, 60000),
                ["stopOnError"] = BooleanSchema("失败后停止，默认 true"),
                ["assertObservedStatus"] = BooleanSchema(
                    "用最近一次成功发送的状态码作为期望状态，默认 true"
                ),
                ["dryRun"] = BooleanSchema("只返回草稿和预检结果，不写入工作区，默认 false"),
            }
        );

    private static JsonObject ExportReportSchema() =>
        ObjectSchema(
            new JsonObject
            {
                ["suiteId"] = StringSchema("套件 id"),
                ["runId"] = StringSchema("运行 id；省略时使用最近一次运行"),
                ["format"] = EnumSchema("报告格式", "markdown", "html", "junit", "json"),
                ["contentLimit"] = IntegerSchema(
                    "返回内容上限字符，默认 60000，范围 1000-1000000",
                    1000,
                    1000000
                ),
            },
            "suiteId"
        );

    private static JsonObject DiffRunsSchema() =>
        ObjectSchema(
            new JsonObject
            {
                ["suiteId"] = StringSchema("套件 id"),
                ["baseRunId"] = StringSchema("作为基准的历史运行 id"),
                ["targetRunId"] = StringSchema("对比目标运行 id；省略时使用最近一次其他运行"),
                ["elapsedRegressionPercent"] = IntegerSchema(
                    "耗时回归阈值百分比，默认 20，范围 0-1000",
                    0,
                    1000
                ),
            },
            "suiteId",
            "baseRunId"
        );

    private static JsonObject ExplainFailureSchema() =>
        ObjectSchema(
            new JsonObject
            {
                ["suiteId"] = StringSchema("套件 id"),
                ["runId"] = StringSchema("运行 id；省略时使用最近一次运行"),
                ["stepId"] = StringSchema("只解释指定步骤；省略时解释整个运行"),
            },
            "suiteId"
        );
}
