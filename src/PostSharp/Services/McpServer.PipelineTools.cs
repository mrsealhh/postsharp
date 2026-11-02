using System.Text.Json.Nodes;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// 流水线工具：把 YAML/JSON 流水线直接跑起来，不需要先存成套件。
/// <c>validate_pipeline</c> 只做静态校验，<c>run_pipeline</c> 真正发请求并可写报告。
/// </summary>
public sealed partial class McpServer
{
    private static readonly HashSet<string> PipelineReportFormats = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "none",
        "markdown",
        "md",
        "junit",
        "json",
    };

    private static object ValidatePipelineTool(JsonObject arguments)
    {
        var parsed = ParsePipelineDocument(arguments);
        return new McpPipelineValidation(
            parsed.Pipeline.Name,
            !parsed.HasErrors,
            CountSteps(parsed.Pipeline.Steps),
            parsed.Pipeline.Data.Count,
            parsed.Issues
        );
    }

    private static object VisualizePipelineTool(JsonObject arguments)
    {
        var parsed = ParsePipelineDocument(arguments);
        return PipelineVisualization.Build(parsed.Pipeline, parsed.Issues);
    }

    private async Task<object> RunPipelineToolAsync(
        JsonObject arguments,
        string permission,
        CancellationToken cancellationToken
    )
    {
        var parsed = ParsePipelineDocument(arguments);
        var errors = parsed.Issues.Where(issue => issue.IsError).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "流水线校验失败："
                    + string.Join(
                        "；",
                        errors.Take(8).Select(issue => $"{issue.Path} {issue.Message}")
                    )
            );
        }

        var options = new PipelineRunOptions
        {
            BaseUrl = OptionalString(arguments, "baseUrl"),
            Variables = ReadPipelineVariables(arguments),
            DryRun = OptionalBool(arguments, "dryRun") ?? false,
        };
        var runner = new PipelineRunner();
        var result = await runner
            .RunAsync(parsed.Pipeline, options, cancellationToken)
            .ConfigureAwait(false);
        Log.Information(
            "流水线运行完成 {Name} passed={Passed} cases={Cases} elapsedMs={ElapsedMs}",
            result.Name,
            result.Passed,
            result.Cases.Count,
            result.ElapsedMs
        );
        if (!options.DryRun)
        {
            await _workspace
                .RecordPipelineAuditAsync(result, _clientName, permission)
                .ConfigureAwait(false);
        }

        var format = (OptionalString(arguments, "format") ?? "none").Trim().ToLowerInvariant();
        if (!PipelineReportFormats.Contains(format))
        {
            throw new InvalidOperationException("format 只支持 none / markdown / junit / json。");
        }

        if (format == "none")
        {
            return result;
        }

        var report = PipelineReport.Write(result, format == "md" ? "markdown" : format);
        return new PipelineRunResult
        {
            Name = result.Name,
            Passed = result.Passed,
            ElapsedMs = result.ElapsedMs,
            Cases = result.Cases,
            Notes = result.Notes,
            ReportFileName = report.FileName,
            ReportPath = report.Path,
        };
    }

    private static PipelineParseResult ParsePipelineDocument(JsonObject arguments)
    {
        var document = OptionalString(arguments, "document");
        var path = OptionalString(arguments, "path");
        string content;
        string? sourcePath = null;
        if (!string.IsNullOrWhiteSpace(document))
        {
            content = document;
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            sourcePath = Path.GetFullPath(path);
            if (!File.Exists(sourcePath))
            {
                throw new InvalidOperationException($"流水线文件不存在：{sourcePath}。");
            }

            content = File.ReadAllText(sourcePath);
        }
        else
        {
            throw new InvalidOperationException("需要 document（YAML/JSON 文本）或 path 参数。");
        }

        return PipelineParser.Parse(
            content,
            sourcePath,
            OptionalString(arguments, "baseDirectory"),
            OptionalString(arguments, "dataContent"),
            OptionalString(arguments, "dataFormat")
        );
    }

    private static IReadOnlyDictionary<string, string> ReadPipelineVariables(JsonObject arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments["vars"] is not JsonObject variables)
        {
            return result;
        }

        foreach (var entry in variables)
        {
            if (entry.Value is JsonValue value && value.TryGetValue<string>(out var text))
            {
                result[entry.Key] = text;
            }
            else if (entry.Value is not null)
            {
                result[entry.Key] = entry.Value.ToJsonString();
            }
        }

        return result;
    }

    private static int CountSteps(IReadOnlyList<PipelineNode> steps) =>
        steps.Sum(step => step.Kind == PipelineNodeKind.Request ? 1 : CountSteps(step.Children));

    private static JsonObject ValidatePipelineSchema() =>
        ObjectSchema(
            new JsonObject
            {
                ["document"] = StringSchema("YAML 或 JSON 流水线文本；与 path 二选一"),
                ["path"] = StringSchema("服务端可读的流水线文件路径"),
                ["dataContent"] = StringSchema("内联数据文件内容（CSV/JSON），覆盖流水线里的 data"),
                ["dataFormat"] = EnumSchema("数据格式", "csv", "json"),
            }
        );

    private static JsonObject VisualizePipelineSchema() =>
        ObjectSchema(
            new JsonObject
            {
                ["document"] = StringSchema("YAML 或 JSON 流水线文本；与 path 二选一"),
                ["path"] = StringSchema("服务端可读的流水线文件路径"),
            }
        );

    private static JsonObject RunPipelineSchema() =>
        ObjectSchema(
            new JsonObject
            {
                ["document"] = StringSchema("YAML 或 JSON 流水线文本；与 path 二选一"),
                ["path"] = StringSchema("服务端可读的流水线文件路径"),
                ["baseUrl"] = StringSchema("覆盖流水线里的 baseUrl"),
                ["vars"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "覆盖 vars 的键值对，例如 {\"tenant\":\"demo\"}",
                    ["additionalProperties"] = true,
                },
                ["dataContent"] = StringSchema("内联数据文件内容（CSV/JSON），覆盖流水线里的 data"),
                ["dataFormat"] = EnumSchema("数据格式", "csv", "json"),
                ["dryRun"] = BooleanSchema("只解析和展示计划，不发送请求，默认 false"),
                ["format"] = EnumSchema(
                    "同时写出报告到 reports 目录",
                    "none",
                    "markdown",
                    "junit",
                    "json"
                ),
            }
        );
}
