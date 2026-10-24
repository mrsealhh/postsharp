using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PostSharp.Services;

/// <summary>
/// A minimal MCP (Model Context Protocol) server over Streamable HTTP. It speaks JSON-RPC 2.0 on
/// POST /mcp and exposes the workspace as tools, so an AI client can list and edit requests,
/// send them, change variables and run a request sequence.
/// </summary>
public sealed class McpServer
{
    private const string ServerVersion = "0.2.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly McpWorkspaceAdapter _workspace;

    private string? _clientName;

    public McpServer(McpWorkspaceAdapter workspace) => _workspace = workspace;

    public async Task<RawHttpResponse?> HandleHttpAsync(
        string method,
        string target,
        IReadOnlyDictionary<string, string> headers,
        string body,
        CancellationToken cancellationToken
    )
    {
        var path = target.Split('?')[0].TrimEnd('/');
        if (!path.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (method == "OPTIONS")
        {
            return new RawHttpResponse(204, "No Content", "application/json", string.Empty);
        }

        if (method != "POST")
        {
            return new RawHttpResponse(
                405,
                "Method Not Allowed",
                "application/json",
                JsonSerializer.Serialize(
                    new
                    {
                        jsonrpc = "2.0",
                        error = new
                        {
                            code = -32000,
                            message = "MCP Streamable HTTP 端点只接受 POST。",
                        },
                    },
                    JsonOptions
                ),
                new Dictionary<string, string> { ["Allow"] = "POST, OPTIONS" }
            );
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException exception)
        {
            Log.Warning(exception, "MCP 请求 JSON 解析失败");
            return new RawHttpResponse(
                200,
                "OK",
                "application/json",
                JsonSerializer.Serialize(Error(null, -32700, "JSON 解析失败。"), JsonOptions)
            );
        }

        JsonNode? response;
        if (root is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var item in batch)
            {
                var one = await HandleMessageAsync(item, cancellationToken).ConfigureAwait(false);
                if (one is not null)
                {
                    responses.Add(one);
                }
            }

            // A JSON-RPC batch always answers with an array, even when only one call was made.
            response = responses.Count == 0 ? null : responses;
        }
        else
        {
            response = await HandleMessageAsync(root, cancellationToken).ConfigureAwait(false);
        }

        if (response is null)
        {
            return new RawHttpResponse(202, "Accepted", "application/json", string.Empty);
        }

        return new RawHttpResponse(
            200,
            "OK",
            "application/json",
            response.ToJsonString(JsonOptions),
            new Dictionary<string, string>
            {
                ["Mcp-Session-Id"] = "postsharp-local",
                ["Cache-Control"] = "no-store",
            }
        );
    }

    private async Task<JsonNode?> HandleMessageAsync(
        JsonNode? node,
        CancellationToken cancellationToken
    )
    {
        if (node is not JsonObject message)
        {
            return Error(null, -32600, "无效的 JSON-RPC 请求。");
        }

        var id = message["id"];
        var isNotification = id is null;
        var method = message["method"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(method))
        {
            return isNotification ? null : Error(id, -32600, "缺少 method。");
        }

        try
        {
            switch (method)
            {
                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;

                case "initialize":
                    _clientName =
                        message["params"]?["clientInfo"]?["name"]?.GetValue<string>()
                        ?? _clientName;
                    return Result(
                        id,
                        new JsonObject
                        {
                            ["protocolVersion"] =
                                message["params"]?["protocolVersion"]?.GetValue<string>()
                                ?? "2025-06-18",
                            ["capabilities"] = new JsonObject
                            {
                                ["tools"] = new JsonObject { ["listChanged"] = false },
                            },
                            ["serverInfo"] = new JsonObject
                            {
                                ["name"] = "PostSharp",
                                ["version"] = ServerVersion,
                            },
                            ["instructions"] =
                                "PostSharp 是本地接口调试和 AI 自动化工作台。先调用 list_collections 获取集合树、parentId、depth、order 和请求顺序；"
                                + "集合最多 5 级，可用 create_collection / update_collection / move_collection / delete_collection 自由整理目录。"
                                + "用 get_request 查看接口，用 create_request / update_request / move_request / delete_request 管理请求，用 send_request 发请求，"
                                + "用 get_curl 获取可直接执行的 cURL。"
                                + "自动化优先用 create_suite / create_suite_from_collections 创建套件，再用 update_suite 反复调整。每个步骤按数组顺序执行，"
                                + "支持 expectedStatus、bodyContains、delayMs（默认 200ms，0-60000）和 mappings。mappings 用 sourceStepIndex（从 0 开始，只能引用更早步骤）"
                                + "或 sourceStepId 提取上一步 JSON 响应字段（支持 data.token、items[0].id），通过 targetKind=Body/Header/Query/Path 和 targetPath 注入下一步请求，"
                                + "template 支持 Bearer {value} 这类格式。用 run_suite 运行并读取每步断言，用 list_suites / get_suite 检查规则。"
                                + "send_request、run_sequence 和 run_suite 都会写入审计记录；list_audit / get_audit 可查所有 UI 和 AI 触发记录。",
                        }
                    );

                case "ping":
                    return Result(id, new JsonObject());

                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = BuildTools() });

                case "tools/call":
                    return await CallToolAsync(id, message, cancellationToken)
                        .ConfigureAwait(false);

                default:
                    return isNotification ? null : Error(id, -32601, $"不支持的方法：{method}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MCP 方法处理失败 {Method}", method);
            return isNotification ? null : Error(id, -32603, ex.Message);
        }
    }

    private async Task<JsonNode?> CallToolAsync(
        JsonNode? id,
        JsonObject message,
        CancellationToken cancellationToken
    )
    {
        var parameters = message["params"] as JsonObject;
        var name = parameters?["name"]?.GetValue<string>();
        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();

        if (string.IsNullOrWhiteSpace(name))
        {
            return Error(id, -32602, "tools/call 缺少工具名。");
        }

        try
        {
            object? payload = name switch
            {
                "list_collections" => await _workspace.ListCollectionsAsync().ConfigureAwait(false),
                "get_request" => await _workspace
                    .GetRequestAsync(RequireGuid(arguments, "id"))
                    .ConfigureAwait(false),
                "create_collection" => await _workspace
                    .CreateCollectionAsync(
                        OptionalString(arguments, "name") ?? "MCP 集合",
                        OptionalGuid(arguments, "parentId"),
                        OptionalBool(arguments, "favorite") ?? false,
                        OptionalInt(arguments, "position")
                    )
                    .ConfigureAwait(false),
                "update_collection" => await _workspace
                    .UpdateCollectionAsync(
                        RequireGuid(arguments, "id"),
                        OptionalString(arguments, "name"),
                        OptionalGuid(arguments, "parentId"),
                        arguments.TryGetPropertyValue("parentId", out _),
                        OptionalBool(arguments, "favorite"),
                        OptionalInt(arguments, "position")
                    )
                    .ConfigureAwait(false),
                "move_collection" => await _workspace
                    .UpdateCollectionAsync(
                        RequireGuid(arguments, "id"),
                        null,
                        OptionalGuid(arguments, "parentId"),
                        true,
                        null,
                        OptionalInt(arguments, "position")
                    )
                    .ConfigureAwait(false),
                "delete_collection" => new
                {
                    deleted = await _workspace
                        .DeleteCollectionAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false),
                },
                "create_request" => await _workspace
                    .CreateRequestAsync(ReadDraft(arguments))
                    .ConfigureAwait(false),
                "update_request" => await _workspace
                    .UpdateRequestAsync(RequireGuid(arguments, "id"), ReadDraft(arguments))
                    .ConfigureAwait(false),
                "move_request" => await _workspace
                    .MoveRequestAsync(
                        RequireGuid(arguments, "id"),
                        RequireGuid(arguments, "collectionId"),
                        OptionalInt(arguments, "position")
                    )
                    .ConfigureAwait(false),
                "delete_request" => new
                {
                    deleted = await _workspace
                        .DeleteRequestAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false),
                },
                "send_request" => await SendToolAsync(arguments, cancellationToken)
                    .ConfigureAwait(false),
                "run_sequence" => await RunSequenceToolAsync(arguments, cancellationToken)
                    .ConfigureAwait(false),
                "get_curl" => new
                {
                    id = RequireGuid(arguments, "id"),
                    curl = await _workspace
                        .GetCurlAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false),
                },
                "list_audit" => await _workspace
                    .ListAuditAsync(
                        OptionalInt(arguments, "limit") ?? 50,
                        OptionalString(arguments, "source")
                    )
                    .ConfigureAwait(false),
                "get_audit" => await _workspace
                    .GetAuditAsync(RequireGuid(arguments, "id"))
                    .ConfigureAwait(false),
                "create_suite" => await _workspace
                    .CreateSuiteAsync(
                        OptionalString(arguments, "name") ?? "MCP 测试套件",
                        ReadSuiteSteps(arguments),
                        OptionalBool(arguments, "stopOnError") ?? true
                    )
                    .ConfigureAwait(false),
                "create_suite_from_collections" => await _workspace
                    .CreateSuiteFromCollectionsAsync(
                        OptionalString(arguments, "name") ?? "MCP 集合套件",
                        ReadGuidArray(arguments, "collectionIds"),
                        OptionalInt(arguments, "delayMs") ?? 200,
                        OptionalBool(arguments, "stopOnError") ?? true
                    )
                    .ConfigureAwait(false),
                "update_suite" => await _workspace
                    .UpdateSuiteAsync(
                        RequireGuid(arguments, "id"),
                        OptionalString(arguments, "name"),
                        OptionalBool(arguments, "stopOnError"),
                        ReadOptionalSuiteSteps(arguments)
                    )
                    .ConfigureAwait(false),
                "get_suite" => await _workspace
                    .GetSuiteAsync(RequireGuid(arguments, "id"))
                    .ConfigureAwait(false),
                "list_suites" => await _workspace.ListSuitesAsync().ConfigureAwait(false),
                "run_suite" => await _workspace
                    .RunSuiteAsync(
                        RequireGuid(arguments, "id"),
                        new McpCallContext("run_suite", _clientName),
                        cancellationToken
                    )
                    .ConfigureAwait(false),
                "delete_suite" => new
                {
                    deleted = await _workspace
                        .DeleteSuiteAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false),
                },
                "list_history" => await _workspace
                    .ListHistoryAsync(OptionalInt(arguments, "limit") ?? 20)
                    .ConfigureAwait(false),
                "list_environments" => await _workspace
                    .ListEnvironmentsAsync()
                    .ConfigureAwait(false),
                "set_variable" => await _workspace
                    .SetVariableAsync(
                        OptionalGuid(arguments, "environmentId"),
                        RequireString(arguments, "key"),
                        OptionalString(arguments, "value") ?? string.Empty,
                        OptionalBool(arguments, "enabled")
                    )
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException($"未知工具：{name}"),
            };

            if (payload is null)
            {
                return ToolResult(id, new { error = "没有找到对象。" }, isError: true);
            }

            return ToolResult(id, payload, isError: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MCP 工具调用失败 {Tool}", name);
            return ToolResult(id, new { error = ex.Message }, isError: true);
        }
    }

    private async Task<object> SendToolAsync(
        JsonObject arguments,
        CancellationToken cancellationToken
    )
    {
        var result = await _workspace
            .SendAsync(
                RequireGuid(arguments, "id"),
                OptionalGuid(arguments, "environmentId"),
                new McpCallContext("send_request", _clientName),
                cancellationToken
            )
            .ConfigureAwait(false);

        return new
        {
            result.RequestId,
            result.Name,
            result.Method,
            result.Url,
            result.StatusCode,
            result.StatusText,
            result.Error,
            result.ElapsedMs,
            result.SizeBytes,
            result.IsJson,
            result.ContentType,
            result.IsSse,
            result.SseEventCount,
            sseThinking = result.SseThinking,
            sseContent = result.SseContent,
            body = Truncate(result.Body, 30000),
            bodyTruncated = result.Body.Length > 30000,
            headers = result.Headers,
        };
    }

    private async Task<object> RunSequenceToolAsync(
        JsonObject arguments,
        CancellationToken cancellationToken
    )
    {
        var ids = ReadGuidArray(arguments, "requestIds");
        if (ids.Count == 0)
        {
            throw new InvalidOperationException("requestIds 不能为空。");
        }

        return await _workspace
            .RunSequenceAsync(
                ids,
                OptionalBool(arguments, "stopOnError") ?? true,
                new McpCallContext("run_sequence", _clientName),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<McpSuiteStepInput> ReadSuiteSteps(JsonObject arguments)
    {
        if (arguments["steps"] is not JsonArray array || array.Count == 0)
        {
            throw new InvalidOperationException("steps 不能为空。");
        }

        var steps = new List<McpSuiteStepInput>();
        foreach (var node in array)
        {
            if (node is not JsonObject step)
            {
                continue;
            }

            steps.Add(
                new McpSuiteStepInput(
                    RequireGuid(step, "requestId"),
                    OptionalInt(step, "expectedStatus"),
                    OptionalString(step, "bodyContains"),
                    OptionalInt(step, "delayMs"),
                    ReadMappings(step)
                )
            );
        }

        return steps;
    }

    private static IReadOnlyList<McpSuiteStepInput>? ReadOptionalSuiteSteps(JsonObject arguments)
    {
        return arguments.TryGetPropertyValue("steps", out _) ? ReadSuiteSteps(arguments) : null;
    }

    private static IReadOnlyList<McpSuiteMappingInput>? ReadMappings(JsonObject step)
    {
        if (step["mappings"] is not JsonArray array)
        {
            return null;
        }

        var mappings = new List<McpSuiteMappingInput>();
        foreach (var node in array)
        {
            if (node is not JsonObject mapping)
            {
                continue;
            }

            mappings.Add(
                new McpSuiteMappingInput(
                    OptionalGuid(mapping, "sourceStepId"),
                    OptionalInt(mapping, "sourceStepIndex"),
                    OptionalString(mapping, "sourcePath") ?? string.Empty,
                    OptionalString(mapping, "targetKind") ?? "Body",
                    RequireString(mapping, "targetPath"),
                    OptionalString(mapping, "template") ?? "{value}"
                )
            );
        }

        return mappings;
    }

    private static JsonNode ToolResult(JsonNode? id, object payload, bool isError)
    {
        var text = JsonSerializer.Serialize(payload, JsonOptions);
        return Result(
            id,
            new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = isError,
            }
        );
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };

    private static JsonArray BuildTools() =>
        new(
            Tool("list_collections", "列出所有集合与请求", ObjectSchema(new JsonObject())),
            Tool(
                "get_request",
                "读取一个请求的完整定义",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("请求 id") }, "id")
            ),
            Tool(
                "create_collection",
                "新建集合；parentId 省略时创建根集合，最多 5 级",
                ObjectSchema(
                    new JsonObject
                    {
                        ["name"] = StringSchema("集合名称"),
                        ["parentId"] = StringSchema("父集合 id；省略表示根集合"),
                        ["favorite"] = BooleanSchema("是否收藏，默认 false"),
                        ["position"] = IntegerSchema(
                            "在兄弟集合中的顺序，从 0 开始；省略时追加",
                            0
                        ),
                    }
                )
            ),
            Tool(
                "update_collection",
                "修改集合名称、收藏、父级或同级顺序；parentId 传 null 可移动到根目录",
                ObjectSchema(
                    new JsonObject
                    {
                        ["id"] = StringSchema("集合 id"),
                        ["name"] = StringSchema("新名称"),
                        ["parentId"] = NullableStringSchema(
                            "新父集合 id；传 null 或空字符串移动到根目录；省略则不改父级"
                        ),
                        ["favorite"] = BooleanSchema("是否收藏"),
                        ["position"] = IntegerSchema("在目标父级中的顺序，从 0 开始", 0),
                    },
                    "id"
                )
            ),
            Tool(
                "move_collection",
                "移动集合到指定父级和顺序；parentId 省略或为 null 时移动到根目录",
                ObjectSchema(
                    new JsonObject
                    {
                        ["id"] = StringSchema("集合 id"),
                        ["parentId"] = NullableStringSchema("目标父集合 id；省略/ null 表示根目录"),
                        ["position"] = IntegerSchema("目标顺序，从 0 开始", 0),
                    },
                    "id"
                )
            ),
            Tool(
                "delete_collection",
                "删除集合及其所有子集合和请求，不可恢复",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("集合 id") }, "id")
            ),
            Tool(
                "create_request",
                "在集合中新建请求，可设置查询参数、请求头、JSON/Text/Multipart 请求体、上传文件和排序",
                ObjectSchema(
                    new JsonObject
                    {
                        ["collectionId"] = StringSchema("目标集合 id；省略时使用第一个集合"),
                        ["name"] = StringSchema("请求名称"),
                        ["method"] = EnumSchema(
                            "HTTP 方法",
                            "GET",
                            "POST",
                            "PUT",
                            "PATCH",
                            "DELETE",
                            "HEAD",
                            "OPTIONS"
                        ),
                        ["url"] = StringSchema("请求地址，支持 {{变量}}"),
                        ["bodyMode"] = EnumSchema(
                            "请求体格式",
                            "None",
                            "Json",
                            "Text",
                            "Multipart"
                        ),
                        ["body"] = StringSchema("请求体"),
                        ["params"] = RowsSchema("查询参数"),
                        ["headers"] = RowsSchema("请求头"),
                        ["formFields"] = RowsSchema("multipart 表单字段"),
                        ["uploadFilePath"] = StringSchema("multipart 上传文件的绝对路径"),
                        ["uploadFieldName"] = StringSchema("上传文件字段名，默认 file"),
                        ["favorite"] = BooleanSchema("是否收藏"),
                        ["position"] = IntegerSchema("在集合中的顺序，从 0 开始；省略时追加", 0),
                    },
                    "url"
                )
            ),
            Tool(
                "update_request",
                "修改请求的任意字段；params/headers/formFields 传入时整体替换，可通过 collectionId/position 移动排序",
                ObjectSchema(
                    new JsonObject
                    {
                        ["id"] = StringSchema("请求 id"),
                        ["collectionId"] = StringSchema("移动到另一个集合（可选）"),
                        ["position"] = IntegerSchema("在目标集合中的顺序，从 0 开始", 0),
                        ["name"] = StringSchema("请求名称"),
                        ["method"] = EnumSchema(
                            "HTTP 方法",
                            "GET",
                            "POST",
                            "PUT",
                            "PATCH",
                            "DELETE",
                            "HEAD",
                            "OPTIONS"
                        ),
                        ["url"] = StringSchema("请求地址"),
                        ["bodyMode"] = EnumSchema(
                            "请求体格式",
                            "None",
                            "Json",
                            "Text",
                            "Multipart"
                        ),
                        ["body"] = StringSchema("请求体"),
                        ["params"] = RowsSchema("查询参数"),
                        ["headers"] = RowsSchema("请求头"),
                        ["formFields"] = RowsSchema("multipart 表单字段"),
                        ["uploadFilePath"] = StringSchema("multipart 上传文件的绝对路径"),
                        ["uploadFieldName"] = StringSchema("上传文件字段名"),
                        ["favorite"] = BooleanSchema("是否收藏"),
                    },
                    "id"
                )
            ),
            Tool(
                "move_request",
                "把请求移动到另一个集合，并设置它在集合中的顺序",
                ObjectSchema(
                    new JsonObject
                    {
                        ["id"] = StringSchema("请求 id"),
                        ["collectionId"] = StringSchema("目标集合 id"),
                        ["position"] = IntegerSchema("目标顺序，从 0 开始；省略时追加", 0),
                    },
                    "id",
                    "collectionId"
                )
            ),
            Tool(
                "delete_request",
                "删除请求",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("请求 id") }, "id")
            ),
            Tool(
                "send_request",
                "发送请求并返回状态、耗时、响应头和响应体",
                ObjectSchema(
                    new JsonObject
                    {
                        ["id"] = StringSchema("请求 id"),
                        ["environmentId"] = StringSchema("环境 id，省略时使用当前环境"),
                    },
                    "id"
                )
            ),
            Tool(
                "run_sequence",
                "自动化：按顺序发送多个请求，可设置遇到失败时停止",
                ObjectSchema(
                    new JsonObject
                    {
                        ["requestIds"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["description"] = "按执行顺序排列的请求 id",
                        },
                        ["stopOnError"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "遇到网络错误或 4xx/5xx 时停止，默认 true",
                        },
                    },
                    "requestIds"
                )
            ),
            Tool(
                "list_history",
                "列出最近的发送历史",
                ObjectSchema(
                    new JsonObject
                    {
                        ["limit"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "返回条数，默认 20",
                            ["minimum"] = 1,
                            ["maximum"] = 200,
                        },
                    }
                )
            ),
            Tool("list_environments", "列出环境与变量", ObjectSchema(new JsonObject())),
            Tool(
                "set_variable",
                "新增或更新环境变量",
                ObjectSchema(
                    new JsonObject
                    {
                        ["environmentId"] = StringSchema("环境 id，省略时使用当前环境"),
                        ["key"] = StringSchema("变量名"),
                        ["value"] = StringSchema("变量值"),
                        ["enabled"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "是否启用，默认 true",
                        },
                    },
                    "key"
                )
            ),
            Tool(
                "get_curl",
                "生成当前请求可直接执行的 cURL（环境变量已解析）",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("请求 id") }, "id")
            ),
            Tool(
                "list_audit",
                "查看 UI 和 AI/MCP 触发的请求审计记录",
                ObjectSchema(
                    new JsonObject
                    {
                        ["limit"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "返回条数，默认 50",
                            ["minimum"] = 1,
                            ["maximum"] = 300,
                        },
                        ["source"] = EnumSchema("来源筛选", "UI", "MCP"),
                    }
                )
            ),
            Tool(
                "get_audit",
                "读取单条审计记录（含请求/响应预览和 cURL）",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("审计记录 id") }, "id")
            ),
            Tool(
                "create_suite",
                "创建自动化套件；步骤按数组顺序执行，支持断言、步骤间延迟和上一步响应字段映射",
                ObjectSchema(
                    new JsonObject
                    {
                        ["name"] = StringSchema("套件名称"),
                        ["stopOnError"] = BooleanSchema("断言或网络失败后停止，默认 true"),
                        ["steps"] = SuiteStepsSchema(),
                    },
                    "steps"
                )
            ),
            Tool(
                "create_suite_from_collections",
                "按集合/请求的当前顺序快速生成套件（集合按 collectionIds 顺序，集合内按请求顺序）",
                ObjectSchema(
                    new JsonObject
                    {
                        ["name"] = StringSchema("套件名称"),
                        ["collectionIds"] = GuidArraySchema("按顺序排列的集合 id"),
                        ["delayMs"] = IntegerSchema(
                            "每个步骤后的默认延迟毫秒，默认 200，范围 0-60000",
                            0,
                            60000
                        ),
                        ["stopOnError"] = BooleanSchema("失败后停止，默认 true"),
                    },
                    "collectionIds"
                )
            ),
            Tool(
                "list_suites",
                "列出自动化套件、步骤顺序、延迟、断言和输入映射",
                ObjectSchema(new JsonObject())
            ),
            Tool(
                "get_suite",
                "读取单个套件的完整定义（步骤 id、延迟、断言、映射）",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("套件 id") }, "id")
            ),
            Tool(
                "update_suite",
                "修改套件名称/失败策略；传入 steps 时整体替换步骤和顺序，可用于增删步骤或重排工作流",
                ObjectSchema(
                    new JsonObject
                    {
                        ["id"] = StringSchema("套件 id"),
                        ["name"] = StringSchema("新名称"),
                        ["stopOnError"] = BooleanSchema("失败后停止"),
                        ["steps"] = SuiteStepsSchema("新的完整步骤数组；省略则只改名称/策略"),
                    },
                    "id"
                )
            ),
            Tool(
                "run_suite",
                "运行自动化套件并返回每个步骤的断言、延迟、映射执行结果",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("套件 id") }, "id")
            ),
            Tool(
                "delete_suite",
                "删除自动化测试套件",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("套件 id") }, "id")
            )
        );

    private static JsonObject Tool(string name, string description, JsonObject schema) =>
        new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };

    private static JsonObject ObjectSchema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };

        if (required.Length > 0)
        {
            var requiredNodes = new JsonArray();
            foreach (var name in required)
            {
                requiredNodes.Add(name);
            }

            schema["required"] = requiredNodes;
        }

        return schema;
    }

    private static JsonObject StringSchema(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    private static JsonObject NullableStringSchema(string description) =>
        new() { ["type"] = new JsonArray("string", "null"), ["description"] = description };

    private static JsonObject BooleanSchema(string description) =>
        new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject IntegerSchema(
        string description,
        int? minimum = null,
        int? maximum = null
    )
    {
        var schema = new JsonObject { ["type"] = "integer", ["description"] = description };

        if (minimum is { } min)
        {
            schema["minimum"] = min;
        }

        if (maximum is { } max)
        {
            schema["maximum"] = max;
        }

        return schema;
    }

    private static JsonObject GuidArraySchema(string description) =>
        new()
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new JsonObject { ["type"] = "string" },
        };

    private static JsonObject EnumSchema(string description, params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return new JsonObject
        {
            ["type"] = "string",
            ["description"] = description,
            ["enum"] = array,
        };
    }

    private static JsonObject RowsSchema(string description) =>
        new()
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["key"] = StringSchema("键"),
                    ["value"] = StringSchema("值"),
                    ["enabled"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "是否启用，默认 true",
                    },
                },
            },
        };

    private static JsonObject SuiteStepsSchema(
        string description = "按执行顺序排列的测试步骤；mappings 只能引用更早的步骤"
    )
    {
        return new JsonObject
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["requestId"] = StringSchema("请求 id"),
                    ["expectedStatus"] = IntegerSchema(
                        "期望 HTTP 状态码；省略时接受 2xx/3xx",
                        100,
                        599
                    ),
                    ["bodyContains"] = StringSchema("响应体必须包含的文本"),
                    ["delayMs"] = IntegerSchema(
                        "本步骤完成后、下一步骤开始前的延迟毫秒，默认 200，范围 0-60000",
                        0,
                        60000
                    ),
                    ["mappings"] = MappingArraySchema(),
                },
                ["required"] = new JsonArray("requestId"),
            },
        };
    }

    private static JsonObject MappingArraySchema() =>
        new()
        {
            ["type"] = "array",
            ["description"] = "从更早步骤的 JSON 响应提取字段并写入本步骤请求",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["sourceStepIndex"] = IntegerSchema(
                        "来源步骤的 0 基索引，必须小于当前步骤索引",
                        0
                    ),
                    ["sourceStepId"] = StringSchema(
                        "来源步骤 id（也可用 get_suite 返回值）；与 sourceStepIndex 二选一"
                    ),
                    ["sourcePath"] = StringSchema(
                        "JSON 路径，如 status、data.token、items[0].id；空字符串表示整个响应体"
                    ),
                    ["targetKind"] = EnumSchema("写入位置", "Body", "Header", "Query", "Path"),
                    ["targetPath"] = StringSchema(
                        "Body JSON 路径、Header 名、Query 参数名或 URL 占位符名"
                    ),
                    ["template"] = StringSchema(
                        "值模板，{value} 会替换为提取值，默认 {value}，例如 Bearer {value}"
                    ),
                },
                ["required"] = new JsonArray("targetPath"),
            },
        };

    private static McpRequestDraft ReadDraft(JsonObject arguments) =>
        new(
            OptionalGuid(arguments, "collectionId"),
            OptionalString(arguments, "name"),
            OptionalString(arguments, "method"),
            OptionalString(arguments, "url"),
            OptionalString(arguments, "bodyMode"),
            OptionalString(arguments, "body"),
            OptionalRows(arguments, "params"),
            OptionalRows(arguments, "headers"),
            OptionalRows(arguments, "formFields"),
            OptionalString(arguments, "uploadFilePath"),
            OptionalString(arguments, "uploadFieldName"),
            OptionalBool(arguments, "favorite"),
            OptionalInt(arguments, "position")
        );

    private static IReadOnlyList<McpRowInfo>? OptionalRows(JsonObject arguments, string name)
    {
        if (arguments[name] is not JsonArray array)
        {
            return null;
        }

        var rows = new List<McpRowInfo>();
        foreach (var node in array)
        {
            if (node is not JsonObject row)
            {
                continue;
            }

            rows.Add(
                new McpRowInfo(
                    OptionalString(row, "key") ?? string.Empty,
                    OptionalString(row, "value") ?? string.Empty,
                    OptionalBool(row, "enabled") ?? true
                )
            );
        }

        return rows;
    }

    private static IReadOnlyList<Guid> ReadGuidArray(JsonObject arguments, string name)
    {
        if (arguments[name] is not JsonArray array)
        {
            return Array.Empty<Guid>();
        }

        var result = new List<Guid>();
        foreach (var node in array)
        {
            var text = node?.GetValue<string>();
            if (!Guid.TryParse(text, out var id))
            {
                throw new InvalidOperationException($"{name} 包含无效的 id：{text}");
            }

            result.Add(id);
        }

        return result;
    }

    private static string RequireString(JsonObject arguments, string name) =>
        OptionalString(arguments, name)
        ?? throw new InvalidOperationException($"缺少参数 {name}。");

    private static Guid RequireGuid(JsonObject arguments, string name)
    {
        var value = RequireString(arguments, name);
        return Guid.TryParse(value, out var id)
            ? id
            : throw new InvalidOperationException($"{name} 不是有效的 id。");
    }

    private static string? OptionalString(JsonObject arguments, string name)
    {
        var node = arguments[name];
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static Guid? OptionalGuid(JsonObject arguments, string name)
    {
        var node = arguments[name];
        if (node is null)
        {
            return null;
        }

        if (
            node is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text)
        )
        {
            return null;
        }

        return Guid.TryParse(text, out var id)
            ? id
            : throw new InvalidOperationException($"{name} 不是有效的 id。");
    }

    private static bool? OptionalBool(JsonObject arguments, string name)
    {
        var node = arguments[name];
        return node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
    }

    private static int? OptionalInt(JsonObject arguments, string name)
    {
        var node = arguments[name];
        return node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
    }

    private static string Truncate(string text, int max)
    {
        return text.Length <= max ? text : text[..max] + "\n…（响应体已截断）";
    }
}
