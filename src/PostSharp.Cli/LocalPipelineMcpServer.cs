using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Cli;

/// <summary>
/// 本地 MCP 服务：stdio + JSON-RPC，直接执行流水线，不需要 PostSharp 桌面进程。
/// 适合 SSH 终端、CI 以及只支持 stdio 的 AI 客户端。
/// </summary>
internal static class LocalPipelineMcpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        TrySetUtf8Console();
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            if (line is null)
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? message;
            try
            {
                message = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                await WriteErrorAsync(null, -32700, "JSON 解析失败。").ConfigureAwait(false);
                continue;
            }

            if (message is null)
            {
                await WriteErrorAsync(null, -32600, "请求必须是 JSON 对象。").ConfigureAwait(false);
                continue;
            }

            var id = message["id"]?.DeepClone();
            var method = message["method"]?.GetValue<string>() ?? string.Empty;
            if (method.StartsWith("notifications/", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                switch (method)
                {
                    case "initialize":
                        await WriteResultAsync(id, Initialize(message["params"]))
                            .ConfigureAwait(false);
                        break;
                    case "ping":
                        await WriteResultAsync(id, new JsonObject()).ConfigureAwait(false);
                        break;
                    case "tools/list":
                        await WriteResultAsync(id, ListTools()).ConfigureAwait(false);
                        break;
                    case "tools/call":
                        await WriteResultAsync(
                                id,
                                await CallToolAsync(message["params"], cancellationToken)
                                    .ConfigureAwait(false)
                            )
                            .ConfigureAwait(false);
                        break;
                    default:
                        await WriteErrorAsync(id, -32601, $"不支持的方法：{method}.")
                            .ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (Exception exception)
            {
                await WriteErrorAsync(id, -32603, exception.Message).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static JsonObject Initialize(JsonNode? parameters) =>
        new()
        {
            ["protocolVersion"] =
                parameters?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject { ["name"] = "PostSharp CLI", ["version"] = Version() },
            ["instructions"] =
                "本地流水线工具：validate_pipeline 只校验；run_pipeline 直接发送请求并返回结果；visualize_pipeline 返回节点/边与 Mermaid/DOT。",
        };

    private static JsonObject ListTools() =>
        new()
        {
            ["tools"] = new JsonArray(
                Tool(
                    "validate_pipeline",
                    "校验 YAML/JSON 流水线，不发送请求。",
                    DocumentSchema(includeRunOptions: false)
                ),
                Tool(
                    "run_pipeline",
                    "直接执行 YAML/JSON 流水线；支持 vars、baseUrl、数据文件和报告。",
                    DocumentSchema(includeRunOptions: true)
                ),
                Tool(
                    "visualize_pipeline",
                    "返回流水线节点、边和 Mermaid/DOT 文本，不执行请求。",
                    DocumentSchema(includeRunOptions: false)
                )
            ),
        };

    private static async Task<JsonObject> CallToolAsync(
        JsonNode? parameters,
        CancellationToken cancellationToken
    )
    {
        var name = parameters?["name"]?.GetValue<string>();
        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolError("tools/call 缺少 name。");
        }

        try
        {
            return name switch
            {
                "validate_pipeline" => ToolResult(ValidateTool(arguments)),
                "visualize_pipeline" => ToolResult(VisualizeTool(arguments)),
                "run_pipeline" => ToolResult(
                    await RunToolAsync(arguments, cancellationToken).ConfigureAwait(false)
                ),
                _ => ToolError($"未知工具：{name}。"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToolError(exception.Message);
        }
    }

    private static JsonObject ValidateTool(JsonObject arguments)
    {
        var parsed = Parse(arguments);
        return Serialize(
            new McpPipelineValidation(
                parsed.Pipeline.Name,
                !parsed.HasErrors,
                CountSteps(parsed.Pipeline.Steps),
                parsed.Pipeline.Data.Count,
                parsed.Issues
            )
        );
    }

    private static JsonObject VisualizeTool(JsonObject arguments)
    {
        var parsed = Parse(arguments);
        return Serialize(PipelineVisualization.Build(parsed.Pipeline, parsed.Issues));
    }

    private static async Task<JsonObject> RunToolAsync(
        JsonObject arguments,
        CancellationToken cancellationToken
    )
    {
        var parsed = Parse(arguments);
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
            BaseUrl = arguments["baseUrl"]?.GetValue<string>(),
            Variables = ReadVariables(arguments),
            DryRun = arguments["dryRun"]?.GetValue<bool>() ?? false,
        };
        using var http = new HttpPipelineClient();
        var runner = new PipelineRunner(http);
        var result = await runner
            .RunAsync(parsed.Pipeline, options, cancellationToken)
            .ConfigureAwait(false);

        var node = Serialize(result);
        var format = arguments["format"]?.GetValue<string>();
        if (
            !string.IsNullOrWhiteSpace(format)
            && !format.Equals("none", StringComparison.OrdinalIgnoreCase)
        )
        {
            var report = PipelineReport.Write(result, format);
            node["reportFileName"] = report.FileName;
            node["reportPath"] = report.Path;
        }

        return node;
    }

    private static PipelineParseResult Parse(JsonObject arguments)
    {
        var document = arguments["document"]?.GetValue<string>();
        var path = arguments["path"]?.GetValue<string>();
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
                throw new InvalidOperationException($"流水线文件不存在：{sourcePath}");
            }

            content = File.ReadAllText(sourcePath);
        }
        else
        {
            throw new InvalidOperationException("需要 document 或 path 参数。");
        }

        return PipelineParser.Parse(
            content,
            sourcePath,
            arguments["baseDirectory"]?.GetValue<string>(),
            arguments["dataContent"]?.GetValue<string>(),
            arguments["dataFormat"]?.GetValue<string>()
        );
    }

    private static IReadOnlyDictionary<string, string> ReadVariables(JsonObject arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments["vars"] is not JsonObject variables)
        {
            return result;
        }

        foreach (var entry in variables)
        {
            if (entry.Value is not null)
            {
                result[entry.Key] =
                    entry.Value is JsonValue value && value.TryGetValue<string>(out var text)
                        ? text
                        : entry.Value.ToJsonString();
            }
        }

        return result;
    }

    private static int CountSteps(IReadOnlyList<PipelineNode> steps) =>
        steps.Sum(step => step.Kind == PipelineNodeKind.Request ? 1 : CountSteps(step.Children));

    private static JsonObject Tool(string name, string description, JsonObject schema) =>
        new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };

    private static JsonObject DocumentSchema(bool includeRunOptions)
    {
        var properties = new JsonObject
        {
            ["document"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "YAML 或 JSON 流水线文本；与 path 二选一",
            },
            ["path"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "本机可读的流水线文件路径",
            },
            ["dataContent"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "内联数据文件内容（CSV/JSON）",
            },
            ["dataFormat"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("csv", "json"),
                ["description"] = "数据格式",
            },
            ["baseDirectory"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "解析 data 文件与相对路径的基准目录",
            },
        };
        if (includeRunOptions)
        {
            properties["baseUrl"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "覆盖流水线里的 baseUrl",
            };
            properties["vars"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "覆盖 vars 的键值对",
                ["additionalProperties"] = true,
            };
            properties["dryRun"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "只解析和展示计划，不发送请求",
            };
            properties["format"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("none", "markdown", "junit", "json"),
                ["description"] = "同时写出报告到 reports 目录",
            };
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject ToolResult(JsonObject payload) =>
        new()
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString(JsonOptions) }
            ),
            ["isError"] = false,
        };

    private static JsonObject ToolError(string message) =>
        new()
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
            ["isError"] = true,
        };

    private static JsonObject Serialize<T>(T value) =>
        JsonSerializer.SerializeToNode(value, JsonOptions) as JsonObject
        ?? new JsonObject { ["value"] = value?.ToString() };

    private static async Task WriteResultAsync(JsonNode? id, JsonObject result)
    {
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["result"] = result };
        if (id is not null)
        {
            response["id"] = id;
        }

        await Console.Out.WriteLineAsync(response.ToJsonString(JsonOptions)).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(JsonNode? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        if (id is not null)
        {
            response["id"] = id;
        }

        await Console.Out.WriteLineAsync(response.ToJsonString(JsonOptions)).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }

    private static string Version() =>
        typeof(LocalPipelineMcpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static void TrySetUtf8Console()
    {
        try
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 某些托管终端不允许改编码；保持默认即可。
        }
    }
}
