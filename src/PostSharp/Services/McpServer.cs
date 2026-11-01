using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// A minimal MCP (Model Context Protocol) server over Streamable HTTP. It speaks JSON-RPC 2.0 on
/// POST /mcp and exposes the workspace as tools, so an AI client can list and edit requests,
/// send them, change variables and run a request sequence.
/// </summary>
public sealed partial class McpServer
{
    private const string ServerVersion = "0.5.0";

    private readonly McpWorkspaceAdapter _workspace;

    private string? _clientName;

    public McpServer(McpWorkspaceAdapter workspace) => _workspace = workspace;

    /// <summary>Immutable policy snapshot; replaced atomically when settings change.</summary>
    public McpAccessPolicy Policy { get; set; } = McpAccessPolicy.Local;

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

        headers.TryGetValue("Origin", out var origin);
        headers.TryGetValue("Host", out var host);
        if (!IsLoopbackHost(host) || !IsAllowedOrigin(origin))
        {
            // A web page in the person's browser can reach 127.0.0.1 too (directly or through DNS rebinding);
            // only local tools and local pages may drive the workspace.
            Log.Warning("拒绝非本机来源的 MCP 请求 {Origin} {Host}", origin, host);
            return new RawHttpResponse(
                403,
                "Forbidden",
                "application/json",
                Error(
                        null,
                        -32001,
                        "拒绝访问：MCP 只接受本机客户端，网页不能调用。请在 Cursor、Claude Code 等客户端中配置该地址。"
                    )
                    .ToJsonString(McpJson.SerializerOptions)
            );
        }

        var cors = CorsHeaders(origin);
        if (method == "OPTIONS")
        {
            return new RawHttpResponse(204, "No Content", "application/json", string.Empty, cors);
        }

        var policy = Policy;
        if (policy.RequireToken && !HasValidToken(headers, policy.Token))
        {
            Log.Warning("MCP 请求缺少有效 token");
            var unauthorizedHeaders = new Dictionary<string, string>(
                cors,
                StringComparer.OrdinalIgnoreCase
            )
            {
                ["WWW-Authenticate"] = "Bearer realm=\"PostSharp\"",
            };
            return new RawHttpResponse(
                401,
                "Unauthorized",
                "application/json",
                Error(
                        null,
                        -32001,
                        "MCP 需要本机 token。请在客户端配置 Authorization: Bearer <token>。"
                    )
                    .ToJsonString(McpJson.SerializerOptions),
                unauthorizedHeaders
            );
        }

        if (method != "POST")
        {
            return new RawHttpResponse(
                405,
                "Method Not Allowed",
                "application/json",
                new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32000,
                        ["message"] = "MCP Streamable HTTP 端点只接受 POST。",
                    },
                }.ToJsonString(McpJson.SerializerOptions),
                new Dictionary<string, string>(cors, StringComparer.OrdinalIgnoreCase)
                {
                    ["Allow"] = "POST, OPTIONS",
                }
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
                Error(null, -32700, "JSON 解析失败。").ToJsonString(McpJson.SerializerOptions),
                cors
            );
        }

        if (policy.ReadOnly && TryFindBlockedTool(root, out var blockedTool))
        {
            return new RawHttpResponse(
                403,
                "Forbidden",
                "application/json",
                Error(
                        null,
                        -32003,
                        $"只读模式不允许调用 {blockedTool}；请在设置中关闭「只读模式」或改用查看类工具。"
                    )
                    .ToJsonString(McpJson.SerializerOptions),
                cors
            );
        }

        JsonNode? response;
        if (root is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var item in batch)
            {
                var one = await HandleMessageAsync(item, policy, cancellationToken)
                    .ConfigureAwait(false);
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
            response = await HandleMessageAsync(root, policy, cancellationToken)
                .ConfigureAwait(false);
        }

        if (response is null)
        {
            return new RawHttpResponse(202, "Accepted", "application/json", string.Empty, cors);
        }

        return new RawHttpResponse(
            200,
            "OK",
            "application/json",
            response.ToJsonString(McpJson.SerializerOptions),
            new Dictionary<string, string>(cors, StringComparer.OrdinalIgnoreCase)
            {
                ["Mcp-Session-Id"] = "postsharp-local",
                ["Cache-Control"] = "no-store",
            }
        );
    }

    private static bool HasValidToken(IReadOnlyDictionary<string, string> headers, string expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var authorization = HeaderValue(headers, "Authorization");
        var provided =
            authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                ? authorization[7..].Trim()
                : null;
        provided ??= HeaderValue(headers, "X-PostSharp-Token");
        return provided is not null && McpTokenService.FixedEquals(expected, provided);
    }

    private static string? HeaderValue(IReadOnlyDictionary<string, string> headers, string name)
    {
        foreach (var pair in headers)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static bool TryFindBlockedTool(JsonNode? root, out string? blockedTool)
    {
        blockedTool = null;
        if (root is JsonArray batch)
        {
            foreach (var item in batch)
            {
                if (TryFindBlockedTool(item, out blockedTool))
                {
                    return true;
                }
            }

            return false;
        }

        if (root is not JsonObject message)
        {
            return false;
        }

        var method = message["method"]?.GetValue<string>();
        if (!string.Equals(method, "tools/call", StringComparison.Ordinal))
        {
            return false;
        }

        var tool = message["params"]?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(tool) || McpToolAccess.IsReadOnly(tool.Trim()))
        {
            return false;
        }

        blockedTool = tool.Trim();
        return true;
    }

    private const string ReportsResourcePrefix = "postsharp://reports/";

    private static readonly string[] ReportExtensions =
    [
        ".md",
        ".html",
        ".json",
        ".xml",
        ".csv",
        ".txt",
    ];

    private static JsonArray BuildResourceList()
    {
        var resources = new JsonArray();
        if (!Directory.Exists(AppPaths.ReportDirectory))
        {
            return resources;
        }

        foreach (
            var path in Directory
                .EnumerateFiles(AppPaths.ReportDirectory)
                .Where(path =>
                    ReportExtensions.Contains(
                        Path.GetExtension(path),
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(50)
        )
        {
            var name = Path.GetFileName(path);
            resources.Add(
                (JsonNode)
                    new JsonObject
                    {
                        ["uri"] = ReportsResourcePrefix + Uri.EscapeDataString(name),
                        ["name"] = name,
                        ["description"] =
                            $"PostSharp 报告 · {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm:ss}",
                        ["mimeType"] = MimeTypeFor(name),
                    }
            );
        }

        return resources;
    }

    private static JsonObject ReadResource(JsonNode? id, JsonNode? parameters)
    {
        var uri = parameters?["uri"]?.GetValue<string>();
        if (
            string.IsNullOrWhiteSpace(uri)
            || !uri.StartsWith(ReportsResourcePrefix, StringComparison.OrdinalIgnoreCase)
        )
        {
            return Error(id, -32602, "只支持 postsharp://reports/{文件名} 资源。");
        }

        var name = Uri.UnescapeDataString(uri[ReportsResourcePrefix.Length..]);
        if (
            name.Length == 0
            || name.Contains('/')
            || name.Contains('\\')
            || name.Contains("..", StringComparison.Ordinal)
        )
        {
            return Error(id, -32602, "资源文件名无效。");
        }

        var path = Path.Combine(AppPaths.ReportDirectory, name);
        if (!File.Exists(path))
        {
            return Error(id, -32002, $"资源不存在：{name}");
        }

        const int maxCharacters = 1_000_000;
        var text = File.ReadAllText(path);
        var truncated = text.Length > maxCharacters;
        if (truncated)
        {
            text = text[..maxCharacters];
        }

        return Result(
            id,
            new JsonObject
            {
                ["contents"] = new JsonArray(
                    new JsonObject
                    {
                        ["uri"] = uri,
                        ["mimeType"] = MimeTypeFor(name),
                        ["text"] = text,
                    }
                ),
            }
        );
    }

    private static string MimeTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".html" => "text/html",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".md" => "text/markdown",
            _ => "text/plain",
        };

    private static JsonArray BuildToolsForPolicy(McpAccessPolicy policy)
    {
        var tools = BuildTools();
        return new JsonArray(
            tools
                .Where(tool =>
                    tool is JsonObject item
                    && item["name"]?.GetValue<string>() is { } name
                    && policy.Allows(name)
                )
                .Select(tool => tool?.DeepClone())
                .ToArray()
        );
    }

    /// <summary>HTTP/1.0 tools may omit Host; browsers always send it, and DNS rebinding shows up as a foreign name.</summary>
    private static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        return Uri.TryCreate("http://" + host.Trim(), UriKind.Absolute, out var uri)
            && IsLoopbackName(uri.Host);
    }

    /// <summary>Desktop MCP clients send no Origin; a browser always does, and only local pages are allowed.</summary>
    private static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && IsLoopbackName(uri.Host);
    }

    private static bool IsLoopbackName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (
            IPAddress.TryParse(host.Trim('[', ']'), out var address)
            && IPAddress.IsLoopback(address)
        );

    /// <summary>CORS answers only the local page that asked; there is no wildcard.</summary>
    private static Dictionary<string, string> CorsHeaders(string? origin)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(origin))
        {
            return headers;
        }

        headers["Access-Control-Allow-Origin"] = origin;
        headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
        headers["Access-Control-Allow-Headers"] =
            "Content-Type, Accept, Authorization, X-PostSharp-Token, Mcp-Session-Id, Mcp-Protocol-Version, Last-Event-ID";
        headers["Access-Control-Expose-Headers"] = "Mcp-Session-Id, WWW-Authenticate";
        headers["Vary"] = "Origin";
        return headers;
    }

    private async Task<JsonNode?> HandleMessageAsync(
        JsonNode? node,
        McpAccessPolicy policy,
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
                                ["resources"] = new JsonObject
                                {
                                    ["listChanged"] = false,
                                    ["subscribe"] = false,
                                },
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
                                + "自动化优先用 create_suite / create_suite_from_collections 创建套件，再用 update_suite 反复调整。steps 定义请求与断言，"
                                + "可选 workflow.nodes / workflow.edges 定义任意 DAG、分支条件和 All/Any 汇合；省略 workflow 时按步骤顺序生成线性/并行组工作流。"
                                + "支持 expectedStatus、bodyContains、delayMs（默认 200ms，0-60000）和 mappings。mappings 用 sourceStepIndex（从 0 开始，只能引用更早步骤）"
                                + "或 sourceStepId 提取上一步 JSON 响应字段（支持 data.token、items[0].id），通过 targetKind=Body/Header/Query/Path 和 targetPath 注入下一步请求，"
                                + "template 支持 Bearer {value} 这类格式。步骤还支持 retryCount、retryDelayMs、condition、parallelGroup 和 assertions；用 run_suite 运行并读取每步断言，"
                                + "用 list_suites / get_suite 检查规则，用 validate_suite 获取结构化预检错误，用 list_suite_runs / get_suite_run 查看最近 20 次运行历史，用 rerun_failed_suite / continue_suite_from_step 重跑失败或从节点继续。"
                                + "长套件用 start_suite_run 立即拿到 jobId，再用 get_suite_run_job 轮询节点/连线进度，必要时 cancel_suite_run。"
                                + "顺序发送、导入和存储同步分别用 start_sequence_run / start_import / start_storage_sync，统一 get_*_job 轮询、cancel_* 取消，并支持 idempotencyKey。"
                                + "读取、发送、修改、管理四类权限由设置控制；越权工具不会出现在 tools/list，调用会返回 -32003。"
                                + "意图优先：verify_endpoint 一次发送并给出状态/正文/断言结论；generate_suite 从集合或请求生成套件（dryRun 只预览）；diff_runs 对比两次运行；explain_failure 定位失败步骤和断言；export_report 输出 Markdown/HTML/JUnit/JSON 报告到数据目录 reports。"
                                + "send_request、run_sequence、run_suite、rerun_failed_suite 和 continue_suite_from_step 都会写入审计记录；list_audit / get_audit 可查所有 UI 和 AI 触发记录。"
                                + "环境和历史：list_environments / create_environment / rename_environment / delete_environment / activate_environment / set_variable / delete_variable；"
                                + "list_history / get_history / delete_history / clear_history / list_history_trash / restore_history / purge_history / empty_history_trash。"
                                + "审计与集合回收站：delete_audit / clear_audit / list_audit_trash / restore_audit / purge_audit / empty_audit_trash；"
                                + "list_collection_trash / restore_collection_trash / purge_collection_trash / empty_collection_trash。"
                                + "系统配置和存储：get_settings / update_settings；get_storage_config / upsert_storage_profile / delete_storage_profile / set_storage_binding / test_storage_profile / sync_storage / switch_storage。"
                                + "导入导出：import_content 支持 Postman/OpenAPI/HAR/Insomnia/Hoppscotch/cURL/PowerShell，export_workspace 导出 Postman Collection。"
                                + "更新：get_settings/update_settings 可读取或设置 autoCheckUpdates，check_updates 立即检查 GitHub Releases（仅已发布安装包可下载）。",
                        }
                    );

                case "ping":
                    return Result(id, new JsonObject());

                case "resources/list":
                    return Result(id, new JsonObject { ["resources"] = BuildResourceList() });

                case "resources/read":
                    return ReadResource(id, message["params"]);

                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = BuildToolsForPolicy(policy) });

                case "tools/call":
                    return await CallToolAsync(id, message, policy, cancellationToken)
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
        McpAccessPolicy policy,
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

        if (policy.ReadOnly && !McpToolAccess.IsReadOnly(name.Trim()))
        {
            return Error(
                id,
                -32003,
                $"只读模式不允许调用 {name.Trim()}。请在设置中关闭「只读模式」后重试。"
            );
        }

        if (!policy.Allows(name.Trim()))
        {
            return Error(
                id,
                -32003,
                $"当前 MCP 权限不允许调用 {name.Trim()}。请在设置中开启发送、修改或管理权限后重试。"
            );
        }

        var requestedTool = name.Trim();
        if (
            McpDestructiveTools.IsDestructive(requestedTool)
            && OptionalBool(arguments, "dryRun") == true
        )
        {
            try
            {
                var preview = await PreviewDestructiveAsync(requestedTool, arguments)
                    .ConfigureAwait(false);
                return ToolResult(id, preview, isError: false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MCP 破坏性操作 dry-run 失败 {Tool}", requestedTool);
                return ToolResult(id, new JsonObject { ["error"] = ex.Message }, isError: true);
            }
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
                        OptionalInt(arguments, "position"),
                        ReadAuth(arguments)
                    )
                    .ConfigureAwait(false),
                "update_collection" => await _workspace
                    .UpdateCollectionAsync(
                        RequireGuid(arguments, "id"),
                        OptionalString(arguments, "name"),
                        OptionalGuid(arguments, "parentId"),
                        arguments.TryGetPropertyValue("parentId", out _),
                        OptionalBool(arguments, "favorite"),
                        OptionalInt(arguments, "position"),
                        ReadAuth(arguments)
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
                "delete_collection" => Payload(
                    "deleted",
                    await _workspace
                        .DeleteCollectionAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false)
                ),
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
                "delete_request" => Payload(
                    "deleted",
                    await _workspace
                        .DeleteRequestAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false)
                ),
                "duplicate_request" => await _workspace
                    .DuplicateRequestAsync(RequireGuid(arguments, "id"))
                    .ConfigureAwait(false),
                "export_workspace" => new JsonObject
                {
                    ["format"] = "Postman Collection v2.1",
                    ["content"] = await _workspace.ExportWorkspaceAsync().ConfigureAwait(false),
                },
                "soap_describe" => await _workspace
                    .DescribeSoapAsync(
                        OptionalString(arguments, "wsdlUrl"),
                        OptionalString(arguments, "wsdl"),
                        OptionalBool(arguments, "includeSample") ?? false,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
                "grpc_describe" => await _workspace
                    .DescribeGrpcAsync(
                        RequireString(arguments, "url"),
                        OptionalBool(arguments, "useTls") ?? true,
                        OptionalString(arguments, "service"),
                        OptionalString(arguments, "method"),
                        cancellationToken
                    )
                    .ConfigureAwait(false),
                "import_content" => await _workspace
                    .ImportContentAsync(
                        OptionalString(arguments, "source"),
                        RequireString(arguments, "content"),
                        OptionalString(arguments, "fileName"),
                        OptionalBool(arguments, "includeStaticResources") ?? false,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
                "start_import" => await _workspace
                    .StartImportJobAsync(
                        OptionalString(arguments, "source"),
                        RequireString(arguments, "content"),
                        OptionalString(arguments, "fileName"),
                        OptionalBool(arguments, "includeStaticResources") ?? false,
                        OptionalString(arguments, "idempotencyKey")
                    )
                    .ConfigureAwait(false),
                "get_import_job" => await _workspace
                    .GetJobAsync(RequireGuid(arguments, "jobId"))
                    .ConfigureAwait(false),
                "cancel_import" => Payload(
                    "cancelled",
                    await _workspace
                        .CancelJobAsync(RequireGuid(arguments, "jobId"))
                        .ConfigureAwait(false)
                ),
                "get_settings" => await _workspace.GetSettingsAsync().ConfigureAwait(false),
                "update_settings" => await _workspace
                    .UpdateSettingsAsync(
                        new McpSettingsDraft(
                            OptionalString(arguments, "theme"),
                            OptionalString(arguments, "renderingMode"),
                            OptionalDouble(arguments, "cornerScale"),
                            OptionalString(arguments, "uiFont"),
                            OptionalString(arguments, "codeFont"),
                            OptionalString(arguments, "codeFontSize"),
                            OptionalInt(arguments, "requestTimeoutSeconds"),
                            OptionalString(arguments, "defaultBodyMode"),
                            OptionalBool(arguments, "autoFormatResponse"),
                            OptionalInt(arguments, "largeResponseThresholdKb"),
                            OptionalInt(arguments, "auditLimit"),
                            OptionalString(arguments, "logDirectory"),
                            OptionalString(arguments, "logLevel"),
                            OptionalString(arguments, "sseThinkingPath"),
                            OptionalString(arguments, "sseContentPath"),
                            OptionalBool(arguments, "autoCheckUpdates"),
                            OptionalBool(arguments, "keepMcpResident"),
                            OptionalString(arguments, "mcpServiceMode")
                        )
                    )
                    .ConfigureAwait(false),
                "check_updates" => await _workspace
                    .CheckForUpdatesAsync(cancellationToken)
                    .ConfigureAwait(false),
                "get_storage_config" => await _workspace
                    .GetStorageConfigAsync()
                    .ConfigureAwait(false),
                "upsert_storage_profile" => await _workspace
                    .UpsertStorageProfileAsync(
                        OptionalString(arguments, "id"),
                        RequireString(arguments, "name"),
                        RequireString(arguments, "provider"),
                        OptionalString(arguments, "connectionString")
                    )
                    .ConfigureAwait(false),
                "delete_storage_profile" => Payload(
                    "deleted",
                    await _workspace
                        .DeleteStorageProfileAsync(RequireString(arguments, "id"))
                        .ConfigureAwait(false)
                ),
                "set_storage_binding" => await _workspace
                    .SetStorageBindingAsync(
                        RequireString(arguments, "category"),
                        RequireString(arguments, "profileId")
                    )
                    .ConfigureAwait(false),
                "test_storage_profile" => await _workspace
                    .TestStorageAsync(RequireString(arguments, "profileId"))
                    .ConfigureAwait(false),
                "sync_storage" => await _workspace
                    .SyncStorageAsync(
                        RequireString(arguments, "sourceProfileId"),
                        RequireString(arguments, "targetProfileId"),
                        ReadStringArray(arguments, "categories"),
                        cancellationToken
                    )
                    .ConfigureAwait(false),
                "start_storage_sync" => await _workspace
                    .StartStorageSyncJobAsync(
                        RequireString(arguments, "sourceProfileId"),
                        RequireString(arguments, "targetProfileId"),
                        ReadStringArray(arguments, "categories"),
                        OptionalString(arguments, "idempotencyKey")
                    )
                    .ConfigureAwait(false),
                "get_storage_sync_job" => await _workspace
                    .GetJobAsync(RequireGuid(arguments, "jobId"))
                    .ConfigureAwait(false),
                "cancel_storage_sync" => Payload(
                    "cancelled",
                    await _workspace
                        .CancelJobAsync(RequireGuid(arguments, "jobId"))
                        .ConfigureAwait(false)
                ),
                "switch_storage" => await _workspace
                    .SwitchStorageAsync(
                        RequireString(arguments, "targetProfileId"),
                        ReadStringArray(arguments, "categories"),
                        cancellationToken
                    )
                    .ConfigureAwait(false),
                "send_request" => await SendToolAsync(arguments, policy, cancellationToken)
                    .ConfigureAwait(false),
                "run_sequence" => await RunSequenceToolAsync(arguments, policy, cancellationToken)
                    .ConfigureAwait(false),
                "start_sequence_run" => await _workspace
                    .StartSequenceJobAsync(
                        ReadGuidArray(arguments, "requestIds"),
                        OptionalBool(arguments, "stopOnError") ?? true,
                        new McpCallContext(
                            "start_sequence_run",
                            _clientName,
                            policy.PermissionLabel
                        ),
                        OptionalString(arguments, "idempotencyKey")
                    )
                    .ConfigureAwait(false),
                "get_sequence_run_job" => await _workspace
                    .GetJobAsync(RequireGuid(arguments, "jobId"))
                    .ConfigureAwait(false),
                "cancel_sequence_run" => Payload(
                    "cancelled",
                    await _workspace
                        .CancelJobAsync(RequireGuid(arguments, "jobId"))
                        .ConfigureAwait(false)
                ),
                "get_curl" => new JsonObject
                {
                    ["id"] = RequireGuid(arguments, "id"),
                    ["curl"] = await _workspace
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
                "delete_audit" => Payload(
                    "deleted",
                    await _workspace
                        .DeleteAuditAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false)
                ),
                "create_suite" => await _workspace
                    .CreateSuiteAsync(
                        OptionalString(arguments, "name") ?? "MCP 测试套件",
                        ReadSuiteSteps(arguments),
                        OptionalBool(arguments, "stopOnError") ?? true,
                        ReadOptionalSuiteWorkflow(arguments)
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
                        OptionalBool(arguments, "runInParallel"),
                        OptionalInt(arguments, "scheduleMinutes"),
                        ReadOptionalSuiteSteps(arguments),
                        ReadOptionalSuiteWorkflow(arguments)
                    )
                    .ConfigureAwait(false),
                "get_suite" => await _workspace
                    .GetSuiteAsync(RequireGuid(arguments, "id"))
                    .ConfigureAwait(false),
                "validate_suite" => await _workspace
                    .ValidateSuiteAsync(RequireGuid(arguments, "id"))
                    .ConfigureAwait(false),
                "list_suites" => await _workspace.ListSuitesAsync().ConfigureAwait(false),
                "run_suite" => await _workspace
                    .RunSuiteAsync(
                        RequireGuid(arguments, "id"),
                        new McpCallContext("run_suite", _clientName, policy.PermissionLabel),
                        cancellationToken
                    )
                    .ConfigureAwait(false),
                "start_suite_run" => await _workspace
                    .StartSuiteRunAsync(
                        RequireGuid(arguments, "id"),
                        new McpCallContext("start_suite_run", _clientName, policy.PermissionLabel),
                        OptionalString(arguments, "idempotencyKey")
                    )
                    .ConfigureAwait(false),
                "get_suite_run_job" => await _workspace
                    .GetSuiteRunJobAsync(RequireGuid(arguments, "jobId"))
                    .ConfigureAwait(false),
                "cancel_suite_run" => Payload(
                    "cancelled",
                    await _workspace
                        .CancelSuiteRunJobAsync(RequireGuid(arguments, "jobId"))
                        .ConfigureAwait(false)
                ),
                "list_suite_runs" => await _workspace
                    .ListSuiteRunsAsync(RequireGuid(arguments, "suiteId"))
                    .ConfigureAwait(false),
                "get_suite_run" => await _workspace
                    .GetSuiteRunAsync(
                        RequireGuid(arguments, "suiteId"),
                        RequireGuid(arguments, "runId")
                    )
                    .ConfigureAwait(false),
                "rerun_failed_suite" => await _workspace
                    .RerunFailedSuiteAsync(
                        RequireGuid(arguments, "suiteId"),
                        RequireGuid(arguments, "runId"),
                        new McpCallContext(
                            "rerun_failed_suite",
                            _clientName,
                            policy.PermissionLabel
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false),
                "continue_suite_from_step" => await _workspace
                    .ContinueSuiteFromStepAsync(
                        RequireGuid(arguments, "suiteId"),
                        RequireGuid(arguments, "runId"),
                        RequireGuid(arguments, "stepId"),
                        new McpCallContext(
                            "continue_suite_from_step",
                            _clientName,
                            policy.PermissionLabel
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false),
                "delete_suite" => Payload(
                    "deleted",
                    await _workspace
                        .DeleteSuiteAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false)
                ),
                "verify_endpoint" => await VerifyEndpointToolAsync(
                        arguments,
                        policy,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
                "generate_suite" => await GenerateSuiteToolAsync(arguments).ConfigureAwait(false),
                "export_report" => await ExportReportToolAsync(arguments).ConfigureAwait(false),
                "diff_runs" => await DiffRunsToolAsync(arguments).ConfigureAwait(false),
                "diff_openapi" => await DiffOpenApiToolAsync(arguments, cancellationToken)
                    .ConfigureAwait(false),
                "export_run_diff" => await ExportRunDiffToolAsync(arguments, cancellationToken)
                    .ConfigureAwait(false),
                "export_openapi_diff" => await ExportOpenApiDiffToolAsync(
                        arguments,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
                "explain_failure" => await ExplainFailureToolAsync(arguments).ConfigureAwait(false),
                "list_history" => await _workspace
                    .ListHistoryAsync(OptionalInt(arguments, "limit") ?? 20)
                    .ConfigureAwait(false),
                "get_history" => await _workspace
                    .GetHistoryAsync(RequireDate(arguments, "timestamp"))
                    .ConfigureAwait(false),
                "delete_history" => Payload(
                    "deleted",
                    await _workspace
                        .DeleteHistoryAsync(RequireDate(arguments, "timestamp"))
                        .ConfigureAwait(false)
                ),
                "list_history_trash" => await _workspace
                    .ListHistoryTrashAsync(OptionalInt(arguments, "limit") ?? 20)
                    .ConfigureAwait(false),
                "clear_history" => Payload(
                    "moved",
                    await _workspace.ClearHistoryAsync().ConfigureAwait(false)
                ),
                "restore_history" => Payload(
                    "restored",
                    await _workspace
                        .RestoreHistoryAsync(RequireDate(arguments, "timestamp"))
                        .ConfigureAwait(false)
                ),
                "purge_history" => Payload(
                    "deleted",
                    await _workspace
                        .PurgeHistoryAsync(RequireDate(arguments, "timestamp"))
                        .ConfigureAwait(false)
                ),
                "empty_history_trash" => Payload(
                    "deleted",
                    await _workspace.EmptyHistoryTrashAsync().ConfigureAwait(false)
                ),
                "clear_audit" => Payload(
                    "moved",
                    await _workspace.ClearAuditAsync().ConfigureAwait(false)
                ),
                "list_audit_trash" => await _workspace
                    .ListAuditTrashAsync(OptionalInt(arguments, "limit") ?? 50)
                    .ConfigureAwait(false),
                "restore_audit" => Payload(
                    "restored",
                    await _workspace
                        .RestoreAuditAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false)
                ),
                "purge_audit" => Payload(
                    "deleted",
                    await _workspace
                        .PurgeAuditAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false)
                ),
                "empty_audit_trash" => Payload(
                    "deleted",
                    await _workspace.EmptyAuditTrashAsync().ConfigureAwait(false)
                ),
                "list_collection_trash" => await _workspace
                    .ListCollectionTrashAsync()
                    .ConfigureAwait(false),
                "restore_collection_trash" => Payload(
                    "restored",
                    await _workspace
                        .RestoreCollectionTrashAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false)
                ),
                "purge_collection_trash" => Payload(
                    "deleted",
                    await _workspace
                        .PurgeCollectionTrashAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false)
                ),
                "empty_collection_trash" => Payload(
                    "deleted",
                    await _workspace.EmptyCollectionTrashAsync().ConfigureAwait(false)
                ),
                "list_environments" => await _workspace
                    .ListEnvironmentsAsync()
                    .ConfigureAwait(false),
                "create_environment" => await _workspace
                    .CreateEnvironmentAsync(OptionalString(arguments, "name") ?? "MCP 环境")
                    .ConfigureAwait(false),
                "rename_environment" => await _workspace
                    .RenameEnvironmentAsync(
                        RequireGuid(arguments, "id"),
                        RequireString(arguments, "name")
                    )
                    .ConfigureAwait(false),
                "delete_environment" => Payload(
                    "deleted",
                    await _workspace
                        .DeleteEnvironmentAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false)
                ),
                "activate_environment" => await _workspace
                    .ActivateEnvironmentAsync(RequireGuid(arguments, "id"))
                    .ConfigureAwait(false),
                "delete_variable" => Payload(
                    "deleted",
                    await _workspace
                        .DeleteVariableAsync(
                            OptionalGuid(arguments, "environmentId"),
                            RequireString(arguments, "key")
                        )
                        .ConfigureAwait(false)
                ),
                "set_variable" => await _workspace
                    .SetVariableAsync(
                        OptionalGuid(arguments, "environmentId"),
                        RequireString(arguments, "key"),
                        OptionalString(arguments, "value"),
                        OptionalBool(arguments, "enabled"),
                        OptionalBool(arguments, "secret")
                    )
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException($"未知工具：{name}"),
            };

            if (payload is null)
            {
                return ToolResult(
                    id,
                    new JsonObject { ["error"] = "没有找到对象。" },
                    isError: true
                );
            }

            return ToolResult(id, payload, isError: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MCP 工具调用失败 {Tool}", name);
            return ToolResult(id, new JsonObject { ["error"] = ex.Message }, isError: true);
        }
    }

    private async Task<JsonObject> SendToolAsync(
        JsonObject arguments,
        McpAccessPolicy policy,
        CancellationToken cancellationToken
    )
    {
        var result = await _workspace
            .SendAsync(
                RequireGuid(arguments, "id"),
                OptionalGuid(arguments, "environmentId"),
                new McpCallContext("send_request", _clientName, policy.PermissionLabel),
                cancellationToken
            )
            .ConfigureAwait(false);

        var payload = McpJson.ToObject(result);
        payload["sseThinking"] = result.SseThinking;
        payload["sseContent"] = result.SseContent;
        payload["body"] = Truncate(result.Body, McpSendResult.BodyLimit);
        payload["bodyTruncated"] = result.BodyTruncated;
        return payload;
    }

    private async Task<object> RunSequenceToolAsync(
        JsonObject arguments,
        McpAccessPolicy policy,
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
                new McpCallContext("run_sequence", _clientName, policy.PermissionLabel),
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
                    ReadMappings(step),
                    OptionalInt(step, "retryCount"),
                    OptionalInt(step, "retryDelayMs"),
                    OptionalString(step, "condition"),
                    OptionalInt(step, "parallelGroup"),
                    ReadAssertions(step)
                )
            );
        }

        return steps;
    }

    private static IReadOnlyList<McpSuiteAssertionInput>? ReadAssertions(JsonObject step)
    {
        if (step["assertions"] is not JsonArray array)
        {
            return null;
        }

        return array
            .OfType<JsonObject>()
            .Select(assertion => new McpSuiteAssertionInput(
                OptionalString(assertion, "kind") ?? "JSONPath",
                RequireString(assertion, "path"),
                RequireString(assertion, "expected")
            ))
            .ToList();
    }

    private static IReadOnlyList<McpSuiteStepInput>? ReadOptionalSuiteSteps(JsonObject arguments)
    {
        return arguments.TryGetPropertyValue("steps", out _) ? ReadSuiteSteps(arguments) : null;
    }

    private static SuiteWorkflowSpec? ReadOptionalSuiteWorkflow(JsonObject arguments)
    {
        if (arguments["workflow"] is not JsonObject workflow)
        {
            return null;
        }

        if (workflow["nodes"] is not JsonArray nodes || nodes.Count == 0)
        {
            throw new InvalidOperationException("workflow.nodes 不能为空。");
        }

        var nodeSpecs = nodes
            .OfType<JsonObject>()
            .Select(node => new SuiteWorkflowNodeSpec(
                OptionalGuid(node, "stepId"),
                OptionalInt(node, "stepIndex"),
                OptionalString(node, "id"),
                OptionalDouble(node, "x"),
                OptionalDouble(node, "y"),
                OptionalString(node, "joinMode")
            ))
            .ToList();
        var edgeSpecs = workflow["edges"] is JsonArray edges
            ? edges.OfType<JsonObject>().Select(ReadSuiteWorkflowEdge).ToList()
            : new List<SuiteWorkflowEdgeSpec>();
        return new SuiteWorkflowSpec(nodeSpecs, edgeSpecs, OptionalInt(workflow, "maxParallelism"));
    }

    private static SuiteWorkflowEdgeSpec ReadSuiteWorkflowEdge(JsonObject edge)
    {
        return new SuiteWorkflowEdgeSpec(
            RequireString(edge, "sourceId"),
            RequireString(edge, "targetId"),
            OptionalString(edge, "condition"),
            ReadSuiteCondition(edge)
        );
    }

    private static SuiteConditionSpec? ReadSuiteCondition(JsonObject edge)
    {
        if (edge["rules"] is not JsonObject rules)
        {
            return null;
        }

        var ruleSpecs = rules["rules"] is JsonArray ruleArray
            ? ruleArray
                .OfType<JsonObject>()
                .Select(rule => new SuiteConditionRuleSpec(
                    OptionalString(rule, "source"),
                    OptionalString(rule, "path"),
                    OptionalString(rule, "operator"),
                    OptionalString(rule, "expected")
                ))
                .ToList()
            : new List<SuiteConditionRuleSpec>();
        return new SuiteConditionSpec(OptionalString(rules, "mode"), ruleSpecs);
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

    private static JsonObject Payload(string name, object? value) =>
        new() { [name] = PayloadValue(value) };

    private static JsonNode? PayloadValue(object? value) =>
        value switch
        {
            null => null,
            bool flag => JsonValue.Create(flag),
            int number => JsonValue.Create(number),
            string text => JsonValue.Create(text),
            _ => JsonNode.Parse(McpJson.Serialize(value)),
        };

    private static JsonNode ToolResult(JsonNode? id, object? payload, bool isError)
    {
        var text = McpJson.Serialize(payload);
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
                        ["auth"] = AuthSchema("集合级授权；请求可以继承"),
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
                        ["auth"] = AuthSchema("集合级授权；省略时保持原配置"),
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
                "删除集合及其所有子集合和请求；内容移入集合回收站，用户可在界面中恢复",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("集合 id") }, "id")
            ),
            Tool(
                "create_request",
                "在集合中新建请求，可设置查询参数、请求头、JSON/Text/Multipart 请求体、上传文件、WebSocket / Socket.IO / TCP 协议字段和排序",
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
                        ["timeoutSeconds"] = IntegerSchema(
                            "请求超时秒数，0 表示使用全局默认",
                            0,
                            3600
                        ),
                        ["protocol"] = EnumSchema(
                            "协议，默认 HTTP",
                            "HTTP",
                            "WebSocket",
                            "Socket.IO",
                            "TCP",
                            "SOAP",
                            "gRPC"
                        ),
                        ["messageBody"] = StringSchema(
                            "WebSocket / Socket.IO / TCP 消息内容；SOAP 为 Body 片段或完整 Envelope；gRPC 为请求 JSON"
                        ),
                        ["messageFormat"] = EnumSchema("消息格式，默认 Text", "Text", "Hex"),
                        ["waitMilliseconds"] = IntegerSchema(
                            "发送后收集回复的等待窗口毫秒，默认 3000",
                            100,
                            60000
                        ),
                        ["socketIoEvent"] = StringSchema("Socket.IO 事件名，默认 message"),
                        ["socketIoNamespace"] = StringSchema("Socket.IO 命名空间，默认 /"),
                        ["socketIoPath"] = StringSchema("Socket.IO 路径，默认 /socket.io"),
                        ["tcpTerminator"] = EnumSchema(
                            "TCP 文本消息结束符，默认 None",
                            "None",
                            "LF",
                            "CRLF"
                        ),
                        ["tcpUseTls"] = BooleanSchema("TCP 使用 TLS"),
                        ["soapVersion"] = EnumSchema("SOAP 版本，默认 1.1", "1.1", "1.2"),
                        ["soapAction"] = StringSchema("SOAPAction（SOAP 1.1）"),
                        ["grpcService"] = StringSchema("gRPC 服务全名，例如 helloworld.Greeter"),
                        ["grpcMethod"] = StringSchema("gRPC 一元方法名，例如 SayHello"),
                        ["grpcUseTls"] = BooleanSchema(
                            "gRPC URL 没有 scheme 时是否使用 TLS，默认 true"
                        ),
                        ["grpcDeadlineSeconds"] = IntegerSchema(
                            "gRPC deadline 秒数，0 使用请求超时",
                            0,
                            300
                        ),
                        ["auth"] = AuthSchema("请求级授权；敏感值只在写入时使用"),
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
                        ["timeoutSeconds"] = IntegerSchema(
                            "请求超时秒数，0 表示使用全局默认",
                            0,
                            3600
                        ),
                        ["protocol"] = EnumSchema(
                            "协议，省略时保持原值",
                            "HTTP",
                            "WebSocket",
                            "Socket.IO",
                            "TCP",
                            "SOAP",
                            "gRPC"
                        ),
                        ["messageBody"] = StringSchema(
                            "WebSocket / Socket.IO / TCP 消息内容；SOAP Body；gRPC 请求 JSON"
                        ),
                        ["messageFormat"] = EnumSchema("消息格式", "Text", "Hex"),
                        ["waitMilliseconds"] = IntegerSchema(
                            "发送后收集回复的等待窗口毫秒",
                            100,
                            60000
                        ),
                        ["socketIoEvent"] = StringSchema("Socket.IO 事件名"),
                        ["socketIoNamespace"] = StringSchema("Socket.IO 命名空间"),
                        ["socketIoPath"] = StringSchema("Socket.IO 路径"),
                        ["tcpTerminator"] = EnumSchema("TCP 文本消息结束符", "None", "LF", "CRLF"),
                        ["tcpUseTls"] = BooleanSchema("TCP 使用 TLS"),
                        ["soapVersion"] = EnumSchema("SOAP 版本", "1.1", "1.2"),
                        ["soapAction"] = StringSchema("SOAPAction"),
                        ["grpcService"] = StringSchema("gRPC 服务全名"),
                        ["grpcMethod"] = StringSchema("gRPC 一元方法名"),
                        ["grpcUseTls"] = BooleanSchema("gRPC 是否使用 TLS"),
                        ["grpcDeadlineSeconds"] = IntegerSchema("gRPC deadline 秒数", 0, 300),
                        ["auth"] = AuthSchema("请求级授权；省略时保持原配置"),
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
                "删除请求；请求移入集合回收站，用户可在界面中恢复",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("请求 id") }, "id")
            ),
            Tool(
                "send_request",
                "发送请求并返回状态、耗时、响应头和响应体；支持 HTTP / WebSocket / Socket.IO / TCP / SOAP / gRPC 一元调用",
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
                "soap_describe",
                "读取 WSDL，列出 SOAP 服务、端口、operation、SOAPAction 和请求元素；可选返回示例 Envelope",
                ObjectSchema(
                    new JsonObject
                    {
                        ["wsdlUrl"] = StringSchema("WSDL 地址；与 wsdl 二选一"),
                        ["wsdl"] = StringSchema("WSDL XML 内容；与 wsdlUrl 二选一"),
                        ["includeSample"] = BooleanSchema("是否生成示例 Envelope，默认 false"),
                    }
                )
            ),
            Tool(
                "grpc_describe",
                "通过 gRPC 服务端反射列出服务/方法，或返回指定方法的请求/响应字段和示例 JSON",
                ObjectSchema(
                    new JsonObject
                    {
                        ["url"] = StringSchema("gRPC 地址，例如 https://host:443 或 host:50051"),
                        ["useTls"] = BooleanSchema("地址没有 scheme 时是否使用 TLS，默认 true"),
                        ["service"] = StringSchema("服务全名；省略时只列出全部服务"),
                        ["method"] = StringSchema("方法名；提供时返回字段 schema 与示例请求"),
                    },
                    "url"
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
                "start_sequence_run",
                "在后台按顺序发送多个请求并立即返回 jobId；用 get_sequence_run_job 轮询，用 cancel_sequence_run 取消",
                ObjectSchema(
                    new JsonObject
                    {
                        ["requestIds"] = GuidArraySchema("按执行顺序排列的请求 id"),
                        ["stopOnError"] = BooleanSchema("遇到网络错误或 4xx/5xx 时停止，默认 true"),
                        ["idempotencyKey"] = StringSchema(
                            "幂等键；相同 key 重复调用返回同一个 jobId"
                        ),
                    },
                    "requestIds"
                )
            ),
            Tool(
                "get_sequence_run_job",
                "读取顺序发送任务的进度、每步结果和终态",
                ObjectSchema(new JsonObject { ["jobId"] = StringSchema("后台任务 id") }, "jobId")
            ),
            Tool(
                "cancel_sequence_run",
                "取消一个仍在运行的顺序发送任务",
                ObjectSchema(new JsonObject { ["jobId"] = StringSchema("后台任务 id") }, "jobId")
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
            Tool(
                "list_environments",
                "列出环境与变量；机密变量（secret=true）的值显示为 ******",
                ObjectSchema(new JsonObject())
            ),
            Tool(
                "set_variable",
                "新增或更新环境变量",
                ObjectSchema(
                    new JsonObject
                    {
                        ["environmentId"] = StringSchema("环境 id，省略时使用当前环境"),
                        ["key"] = StringSchema("变量名"),
                        ["value"] = StringSchema("变量值；更新已有变量时省略则保持原值"),
                        ["enabled"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "是否启用，默认 true",
                        },
                        ["secret"] = BooleanSchema(
                            "设为 true 把变量标记为机密，值在 list_environments、get_curl、send_request 和审计中显示为 ******；省略则不改。机密只能在界面中取消"
                        ),
                    },
                    "key"
                )
            ),
            Tool(
                "get_curl",
                "生成当前请求可直接执行的 cURL（环境变量已解析；机密变量的值以 ****** 代替）",
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
                "delete_audit",
                "把单条审计记录移入审计回收站；可用 restore_audit 恢复",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("审计记录 id") }, "id")
            ),
            Tool(
                "create_suite",
                "创建自动化套件；steps 定义请求与断言，可选 workflow 定义任意 DAG 节点、连线和分支条件",
                ObjectSchema(
                    new JsonObject
                    {
                        ["name"] = StringSchema("套件名称"),
                        ["stopOnError"] = BooleanSchema("断言或网络失败后停止，默认 true"),
                        ["steps"] = SuiteStepsSchema(),
                        ["workflow"] = SuiteWorkflowSchema(
                            "任意 DAG；省略时按步骤顺序生成线性/并行组工作流"
                        ),
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
                "读取单个套件的完整定义（步骤、workflow 节点/连线、断言、映射、运行策略）",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("套件 id") }, "id")
            ),
            Tool(
                "validate_suite",
                "运行前预检套件，返回结构化错误/提醒（节点 id、错误码、修复建议）",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("套件 id") }, "id")
            ),
            Tool(
                "update_suite",
                "修改套件名称/策略；传入 steps 或 workflow 可整体替换步骤和 DAG，可用于增删、重排或分支编排",
                ObjectSchema(
                    new JsonObject
                    {
                        ["id"] = StringSchema("套件 id"),
                        ["name"] = StringSchema("新名称"),
                        ["stopOnError"] = BooleanSchema("失败后停止"),
                        ["runInParallel"] = BooleanSchema("并行执行"),
                        ["scheduleMinutes"] = IntegerSchema(
                            "定时运行间隔分钟，0 表示不启用",
                            0,
                            1440
                        ),
                        ["steps"] = SuiteStepsSchema("新的完整步骤数组；省略则保留现有步骤"),
                        ["workflow"] = SuiteWorkflowSchema(
                            "新的 DAG；省略时，传 steps 会重建线性工作流"
                        ),
                    },
                    "id"
                )
            ),
            Tool(
                "run_suite",
                "运行自动化套件并返回每个步骤的断言、延迟、映射执行结果和运行 id",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("套件 id") }, "id")
            ),
            Tool(
                "start_suite_run",
                "在后台启动套件运行并立即返回 jobId；用 get_suite_run_job 轮询进度，用 cancel_suite_run 取消",
                ObjectSchema(
                    new JsonObject
                    {
                        ["id"] = StringSchema("套件 id"),
                        ["idempotencyKey"] = StringSchema(
                            "幂等键；相同 key 重复调用会返回同一个 jobId，不会重复执行"
                        ),
                    },
                    "id"
                )
            ),
            Tool(
                "get_suite_run_job",
                "读取后台套件任务的状态、当前节点/连线进度和完成后的运行 id",
                ObjectSchema(new JsonObject { ["jobId"] = StringSchema("后台任务 id") }, "jobId")
            ),
            Tool(
                "cancel_suite_run",
                "取消一个仍在运行或取消中的后台套件任务",
                ObjectSchema(new JsonObject { ["jobId"] = StringSchema("后台任务 id") }, "jobId")
            ),
            Tool(
                "list_suite_runs",
                "列出套件最近的运行历史（最多 20 次）",
                ObjectSchema(new JsonObject { ["suiteId"] = StringSchema("套件 id") }, "suiteId")
            ),
            Tool(
                "get_suite_run",
                "读取一次套件运行的完整结果，包括步骤、断言、节点和连线状态",
                ObjectSchema(
                    new JsonObject
                    {
                        ["suiteId"] = StringSchema("套件 id"),
                        ["runId"] = StringSchema("运行 id"),
                    },
                    "suiteId",
                    "runId"
                )
            ),
            Tool(
                "rerun_failed_suite",
                "重跑一次失败运行中的失败步骤及其依赖，返回新的运行结果",
                ObjectSchema(
                    new JsonObject
                    {
                        ["suiteId"] = StringSchema("套件 id"),
                        ["runId"] = StringSchema("历史运行 id"),
                    },
                    "suiteId",
                    "runId"
                )
            ),
            Tool(
                "continue_suite_from_step",
                "从历史运行中的某个步骤继续执行下游步骤，并自动补跑上游依赖",
                ObjectSchema(
                    new JsonObject
                    {
                        ["suiteId"] = StringSchema("套件 id"),
                        ["runId"] = StringSchema("历史运行 id"),
                        ["stepId"] = StringSchema("步骤 id"),
                    },
                    "suiteId",
                    "runId",
                    "stepId"
                )
            ),
            Tool(
                "delete_suite",
                "删除自动化测试套件",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("套件 id") }, "id")
            ),
            Tool(
                "verify_endpoint",
                "一次调用验证一个端点：发送请求并按状态码、响应体和断言给出通过/失败结论；返回检查项、耗时、掩码后的响应预览和失败原因",
                VerifyEndpointSchema()
            ),
            Tool(
                "generate_suite",
                "从一个或多个集合/请求生成可运行套件：按最近一次成功发送固定期望状态并返回推断依据；dryRun 只预览草稿和预检结果",
                GenerateSuiteSchema()
            ),
            Tool(
                "export_report",
                "把一次运行导出为 Markdown/HTML/JUnit/JSON 报告，写入数据目录 reports，返回文件路径、内容和截断标记",
                ExportReportSchema()
            ),
            Tool(
                "diff_runs",
                "对比同一套件两次运行：新增失败、修复、状态码变化、耗时回归/改善、增删步骤和节点状态变化",
                DiffRunsSchema()
            ),
            Tool(
                "diff_openapi",
                "对比两份 OpenAPI/Swagger 文档，标出新增/删除操作、参数、请求体和响应结构变化，并标记破坏性变更",
                ObjectSchema(
                    new JsonObject
                    {
                        ["baseUrl"] = StringSchema("基准文档的 http/https 地址"),
                        ["baseContent"] = StringSchema("基准文档的 OpenAPI/Swagger 内容"),
                        ["targetUrl"] = StringSchema("目标文档的 http/https 地址"),
                        ["targetContent"] = StringSchema("目标文档的 OpenAPI/Swagger 内容"),
                        ["baseLabel"] = StringSchema("基准文档名称，用于错误提示"),
                        ["targetLabel"] = StringSchema("目标文档名称，用于错误提示"),
                    }
                )
            ),
            Tool(
                "export_run_diff",
                "把两次运行的差异写成 Markdown/HTML/JSON 报告，写入数据目录 reports，返回文件路径和内容",
                ObjectSchema(
                    new JsonObject
                    {
                        ["suiteId"] = StringSchema("套件 id"),
                        ["baseRunId"] = StringSchema("基准运行 id"),
                        ["targetRunId"] = StringSchema("目标运行 id；省略时使用最近一次运行"),
                        ["elapsedRegressionPercent"] = IntegerSchema(
                            "耗时回归阈值百分比，默认 20",
                            0,
                            1000
                        ),
                        ["format"] = EnumSchema(
                            "报告格式，默认 markdown",
                            "markdown",
                            "html",
                            "json"
                        ),
                        ["contentLimit"] = IntegerSchema(
                            "返回内容的最大字符数，默认 60000",
                            1000,
                            500000
                        ),
                    },
                    "suiteId",
                    "baseRunId"
                )
            ),
            Tool(
                "export_openapi_diff",
                "对比两份 OpenAPI/Swagger 文档并把差异写成 Markdown/HTML/JSON 报告，返回文件路径和内容",
                ObjectSchema(
                    new JsonObject
                    {
                        ["baseUrl"] = StringSchema("基准文档的 http/https 地址"),
                        ["baseContent"] = StringSchema("基准文档的 OpenAPI/Swagger 内容"),
                        ["targetUrl"] = StringSchema("目标文档的 http/https 地址"),
                        ["targetContent"] = StringSchema("目标文档的 OpenAPI/Swagger 内容"),
                        ["baseLabel"] = StringSchema("基准文档名称，用于错误提示"),
                        ["targetLabel"] = StringSchema("目标文档名称，用于错误提示"),
                        ["format"] = EnumSchema(
                            "报告格式，默认 markdown",
                            "markdown",
                            "html",
                            "json"
                        ),
                        ["contentLimit"] = IntegerSchema(
                            "返回内容的最大字符数，默认 60000",
                            1000,
                            500000
                        ),
                    }
                )
            ),
            Tool(
                "explain_failure",
                "解释一次运行为什么失败：归类到预检/网络/状态/正文/断言/依赖，给出步骤、证据、修复提示和下一步工具调用",
                ExplainFailureSchema()
            ),
            Tool(
                "duplicate_request",
                "复制一个请求到同一集合的下一位置",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("请求 id") }, "id")
            ),
            Tool(
                "export_workspace",
                "把全部集合和请求导出为 Postman Collection v2.1 JSON",
                ObjectSchema(new JsonObject())
            ),
            Tool(
                "import_content",
                "导入 Postman、OpenAPI、HAR、Insomnia、Hoppscotch、cURL 或 PowerShell 内容，并把结果合并到工作区",
                ObjectSchema(
                    new JsonObject
                    {
                        ["source"] = EnumSchema(
                            "导入格式；省略时根据 fileName 和内容自动识别",
                            "OpenApi",
                            "PostmanCollection",
                            "PostmanEnvironment",
                            "Har",
                            "Insomnia",
                            "Hoppscotch",
                            "Curl",
                            "PowerShell"
                        ),
                        ["content"] = StringSchema("要导入的原始文本"),
                        ["fileName"] = StringSchema("可选文件名，用于识别扩展名"),
                        ["includeStaticResources"] = BooleanSchema(
                            "HAR 导入是否包含静态资源，默认 false"
                        ),
                    },
                    "content"
                )
            ),
            Tool(
                "start_import",
                "在后台导入 Postman/OpenAPI/HAR/Insomnia/Hoppscotch/cURL/PowerShell 内容并返回 jobId",
                ObjectSchema(
                    new JsonObject
                    {
                        ["source"] = EnumSchema(
                            "导入格式；省略时根据 fileName 和内容自动识别",
                            "OpenApi",
                            "PostmanCollection",
                            "PostmanEnvironment",
                            "Har",
                            "Insomnia",
                            "Hoppscotch",
                            "Curl",
                            "PowerShell"
                        ),
                        ["content"] = StringSchema("要导入的原始文本"),
                        ["fileName"] = StringSchema("可选文件名，用于识别扩展名"),
                        ["includeStaticResources"] = BooleanSchema(
                            "HAR 导入是否包含静态资源，默认 false"
                        ),
                        ["idempotencyKey"] = StringSchema(
                            "幂等键；相同 key 重复调用返回同一个 jobId"
                        ),
                    },
                    "content"
                )
            ),
            Tool(
                "get_import_job",
                "读取导入任务的进度、结果和终态",
                ObjectSchema(new JsonObject { ["jobId"] = StringSchema("后台任务 id") }, "jobId")
            ),
            Tool(
                "cancel_import",
                "取消一个仍在运行的导入任务",
                ObjectSchema(new JsonObject { ["jobId"] = StringSchema("后台任务 id") }, "jobId")
            ),
            Tool(
                "get_settings",
                "读取系统配置：主题、字体、超时、响应格式、审计保留、日志和 SSE 路径",
                ObjectSchema(new JsonObject())
            ),
            Tool(
                "update_settings",
                "修改系统配置；只传需要修改的字段，保存到当前系统配置存储",
                ObjectSchema(
                    new JsonObject
                    {
                        ["theme"] = StringSchema("主题：跟随系统/浅色/深色"),
                        ["renderingMode"] = EnumSchema(
                            "渲染模式；修改后重启生效",
                            RenderingModes.Software,
                            RenderingModes.Auto
                        ),
                        ["cornerScale"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["description"] = "全局圆角倍数，0-2",
                            ["minimum"] = 0,
                            ["maximum"] = 2,
                        },
                        ["uiFont"] = StringSchema("界面字体"),
                        ["codeFont"] = StringSchema("代码字体"),
                        ["codeFontSize"] = StringSchema("代码字号"),
                        ["requestTimeoutSeconds"] = IntegerSchema("默认请求超时秒数", 1, 3600),
                        ["defaultBodyMode"] = EnumSchema(
                            "默认请求体格式",
                            "None",
                            "Json",
                            "Text",
                            "Multipart"
                        ),
                        ["autoFormatResponse"] = BooleanSchema("自动格式化 JSON/XML 响应"),
                        ["largeResponseThresholdKb"] = IntegerSchema("大响应折叠阈值 KB", 8, 4096),
                        ["auditLimit"] = IntegerSchema("审计最多保留条数；0 表示不限", 0, 50000),
                        ["logDirectory"] = StringSchema("日志目录"),
                        ["logLevel"] = EnumSchema(
                            "日志级别",
                            "Debug",
                            "Information",
                            "Warning",
                            "Error"
                        ),
                        ["sseThinkingPath"] = StringSchema("SSE 思考节点路径"),
                        ["sseContentPath"] = StringSchema("SSE 内容节点路径"),
                        ["autoCheckUpdates"] = BooleanSchema(
                            "是否每 6 小时后台检查 GitHub Releases"
                        ),
                        ["keepMcpResident"] = BooleanSchema(
                            "旧字段；true 等同页面常驻，false 等同不常驻"
                        ),
                        ["mcpServiceMode"] = EnumSchema(
                            "MCP 常驻模式：Disabled、Page（托盘页面常驻）或 Background（后台独立服务）",
                            McpServiceModes.Disabled,
                            McpServiceModes.Page,
                            McpServiceModes.Background
                        ),
                    }
                )
            ),
            Tool(
                "check_updates",
                "立即检查 GitHub Releases；已发布安装包会下载并校验新版本，下载后由用户在顶栏确认安装",
                ObjectSchema(new JsonObject())
            ),
            Tool(
                "get_storage_config",
                "读取本地 SQLite 路径、存储配置和系统配置/集合与接口/套件三个分类的绑定",
                ObjectSchema(new JsonObject())
            ),
            Tool(
                "upsert_storage_profile",
                "新增或修改远程存储配置；连接串只写不读，读取时隐藏",
                ObjectSchema(
                    new JsonObject
                    {
                        ["id"] = StringSchema("已有配置 id；省略时新增"),
                        ["name"] = StringSchema("配置名称"),
                        ["provider"] = EnumSchema(
                            "数据库类型（远程类型需先在插件目录安装对应插件）",
                            StorageProviders.Remote.Count > 0
                                ? StorageProviders.Remote.ToArray()
                                : StorageProviders.All.ToArray()
                        ),
                        ["connectionString"] = StringSchema(
                            "ADO.NET / MongoDB 连接串；修改已有配置时可省略"
                        ),
                    },
                    "name",
                    "provider"
                )
            ),
            Tool(
                "delete_storage_profile",
                "删除远程存储配置；使用它的分类自动回到本地 SQLite",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("存储配置 id") }, "id")
            ),
            Tool(
                "set_storage_binding",
                "把系统配置、集合与接口记录或套件自动化绑定到指定存储",
                ObjectSchema(
                    new JsonObject
                    {
                        ["category"] = EnumSchema(
                            "数据分类",
                            StorageCategories.Settings,
                            StorageCategories.Workspace,
                            StorageCategories.Suites
                        ),
                        ["profileId"] = StringSchema("存储配置 id"),
                    },
                    "category",
                    "profileId"
                )
            ),
            Tool(
                "test_storage_profile",
                "测试存储连接并确保文档表存在",
                ObjectSchema(
                    new JsonObject { ["profileId"] = StringSchema("存储配置 id") },
                    "profileId"
                )
            ),
            Tool(
                "sync_storage",
                "从源存储单向同步指定分类到目标存储，不切换绑定",
                ObjectSchema(
                    new JsonObject
                    {
                        ["sourceProfileId"] = StringSchema("源存储配置 id"),
                        ["targetProfileId"] = StringSchema("目标存储配置 id"),
                        ["categories"] = StorageCategoriesSchema(
                            "要同步的分类；省略时同步全部三类"
                        ),
                    },
                    "sourceProfileId",
                    "targetProfileId"
                )
            ),
            Tool(
                "start_storage_sync",
                "在后台单向同步存储并立即返回 jobId；用 get_storage_sync_job 轮询",
                ObjectSchema(
                    new JsonObject
                    {
                        ["sourceProfileId"] = StringSchema("源存储配置 id"),
                        ["targetProfileId"] = StringSchema("目标存储配置 id"),
                        ["categories"] = StorageCategoriesSchema(
                            "要同步的分类；省略时同步全部三类"
                        ),
                        ["idempotencyKey"] = StringSchema(
                            "幂等键；相同 key 重复调用返回同一个 jobId"
                        ),
                    },
                    "sourceProfileId",
                    "targetProfileId"
                )
            ),
            Tool(
                "get_storage_sync_job",
                "读取存储同步任务的进度、结果和终态",
                ObjectSchema(new JsonObject { ["jobId"] = StringSchema("后台任务 id") }, "jobId")
            ),
            Tool(
                "cancel_storage_sync",
                "取消一个仍在运行的存储同步任务",
                ObjectSchema(new JsonObject { ["jobId"] = StringSchema("后台任务 id") }, "jobId")
            ),
            Tool(
                "switch_storage",
                "备份本地数据库，复制当前数据到目标存储并切换三个分类的绑定；重启后使用新存储",
                ObjectSchema(
                    new JsonObject
                    {
                        ["targetProfileId"] = StringSchema("目标存储配置 id"),
                        ["categories"] = StorageCategoriesSchema(
                            "要切换的分类；省略时切换全部三类"
                        ),
                    },
                    "targetProfileId"
                )
            ),
            Tool(
                "create_environment",
                "新建环境并设为当前环境",
                ObjectSchema(new JsonObject { ["name"] = StringSchema("环境名称") })
            ),
            Tool(
                "rename_environment",
                "重命名环境",
                ObjectSchema(
                    new JsonObject
                    {
                        ["id"] = StringSchema("环境 id"),
                        ["name"] = StringSchema("新名称"),
                    },
                    "id",
                    "name"
                )
            ),
            Tool(
                "delete_environment",
                "删除环境；至少保留一个环境",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("环境 id") }, "id")
            ),
            Tool(
                "activate_environment",
                "切换当前环境",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("环境 id") }, "id")
            ),
            Tool(
                "delete_variable",
                "删除环境变量",
                ObjectSchema(
                    new JsonObject
                    {
                        ["environmentId"] = StringSchema("环境 id；省略时使用当前环境"),
                        ["key"] = StringSchema("变量名"),
                    },
                    "key"
                )
            ),
            Tool(
                "get_history",
                "按时间戳读取一条发送历史的完整请求快照（URL、参数、请求头和请求体）",
                ObjectSchema(
                    new JsonObject { ["timestamp"] = StringSchema("ISO 8601 时间戳") },
                    "timestamp"
                )
            ),
            Tool(
                "delete_history",
                "把单条发送历史移入历史回收站；可用 restore_history 恢复",
                ObjectSchema(
                    new JsonObject { ["timestamp"] = StringSchema("ISO 8601 时间戳") },
                    "timestamp"
                )
            ),
            Tool(
                "list_history_trash",
                "列出历史回收站",
                ObjectSchema(
                    new JsonObject { ["limit"] = IntegerSchema("返回条数，默认 20", 1, 200) }
                )
            ),
            Tool("clear_history", "把全部发送历史移入历史回收站", ObjectSchema(new JsonObject())),
            Tool(
                "restore_history",
                "按时间戳从历史回收站恢复一条记录",
                ObjectSchema(
                    new JsonObject { ["timestamp"] = StringSchema("ISO 8601 时间戳") },
                    "timestamp"
                )
            ),
            Tool(
                "purge_history",
                "按时间戳永久删除历史回收站记录",
                ObjectSchema(
                    new JsonObject { ["timestamp"] = StringSchema("ISO 8601 时间戳") },
                    "timestamp"
                )
            ),
            Tool("empty_history_trash", "永久清空历史回收站", ObjectSchema(new JsonObject())),
            Tool("clear_audit", "把全部审计记录移入审计回收站", ObjectSchema(new JsonObject())),
            Tool(
                "list_audit_trash",
                "列出审计回收站",
                ObjectSchema(
                    new JsonObject { ["limit"] = IntegerSchema("返回条数，默认 50", 1, 500) }
                )
            ),
            Tool(
                "restore_audit",
                "从审计回收站恢复一条记录",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("审计记录 id") }, "id")
            ),
            Tool(
                "purge_audit",
                "永久删除一条审计回收站记录",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("审计记录 id") }, "id")
            ),
            Tool("empty_audit_trash", "永久清空审计回收站", ObjectSchema(new JsonObject())),
            Tool("list_collection_trash", "列出集合回收站", ObjectSchema(new JsonObject())),
            Tool(
                "restore_collection_trash",
                "恢复集合回收站中的集合或请求",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("回收站 id") }, "id")
            ),
            Tool(
                "purge_collection_trash",
                "永久删除集合回收站中的集合或请求",
                ObjectSchema(new JsonObject { ["id"] = StringSchema("回收站 id") }, "id")
            ),
            Tool("empty_collection_trash", "永久清空集合回收站", ObjectSchema(new JsonObject()))
        );

    private static JsonObject Tool(string name, string description, JsonObject schema)
    {
        if (
            McpDestructiveTools.IsDestructive(name)
            && schema["properties"] is JsonObject properties
            && !properties.ContainsKey("dryRun")
        )
        {
            properties["dryRun"] = BooleanSchema("只返回影响预览，不执行；默认 false");
        }

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };
    }

    private static JsonObject ObjectSchema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };

        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(
                required.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()
            );
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

    private static JsonObject StorageCategoriesSchema(string description)
    {
        return new JsonObject
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    StorageCategories
                        .All.Select(category => (JsonNode?)JsonValue.Create(category))
                        .ToArray()
                ),
            },
        };
    }

    private static JsonObject EnumSchema(string description, params string[] values)
    {
        var array = new JsonArray(
            values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()
        );

        return new JsonObject
        {
            ["type"] = "string",
            ["description"] = description,
            ["enum"] = array,
        };
    }

    private static JsonObject AuthSchema(string description) =>
        new()
        {
            ["type"] = "object",
            ["description"] = description,
            ["properties"] = new JsonObject
            {
                ["authType"] = EnumSchema(
                    "授权类型",
                    "Inherit",
                    "None",
                    "Bearer",
                    "Basic",
                    "ApiKey",
                    "OAuth2"
                ),
                ["target"] = EnumSchema("附加位置", "Header", "Query"),
                ["token"] = StringSchema("Bearer 令牌；传 ****** 表示保持原值"),
                ["username"] = StringSchema("Basic 用户名"),
                ["password"] = StringSchema("Basic 密码；传 ****** 表示保持原值"),
                ["apiKeyName"] = StringSchema("API Key 名称"),
                ["apiKeyValue"] = StringSchema("API Key 值；传 ****** 表示保持原值"),
                ["apiKeyLocation"] = EnumSchema("API Key 位置", "Header", "Query"),
            },
        };

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
                    ["retryCount"] = IntegerSchema("失败重试次数，0-10", 0, 10),
                    ["retryDelayMs"] = IntegerSchema("每次重试前等待毫秒", 0, 60000),
                    ["condition"] = EnumSchema(
                        "执行条件",
                        "Always",
                        "OnPreviousSuccess",
                        "OnPreviousFailure"
                    ),
                    ["parallelGroup"] = IntegerSchema("并行组，0 表示不并行", 0, 1000),
                    ["assertions"] = AssertionsSchema(),
                    ["mappings"] = MappingArraySchema(),
                },
                ["required"] = new JsonArray("requestId"),
            },
        };
    }

    private static JsonObject AssertionsSchema() =>
        new()
        {
            ["type"] = "array",
            ["description"] = "JSONPath 或响应头断言",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["kind"] = EnumSchema("断言类型", "JSONPath", "Header"),
                    ["path"] = StringSchema("JSONPath 或响应头名"),
                    ["expected"] = StringSchema("期望值"),
                },
                ["required"] = new JsonArray("path", "expected"),
            },
        };

    private static JsonObject SuiteWorkflowSchema(string description) =>
        new()
        {
            ["type"] = "object",
            ["description"] = description,
            ["properties"] = new JsonObject
            {
                ["nodes"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "必须覆盖全部步骤；stepId 或 stepIndex 指向步骤",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["id"] = StringSchema("节点 id；省略时使用步骤 id"),
                            ["stepId"] = StringSchema("步骤 id；与 stepIndex 二选一"),
                            ["stepIndex"] = IntegerSchema("步骤的 0 基索引；与 stepId 二选一", 0),
                            ["x"] = NumberSchema("画布横坐标，默认 80"),
                            ["y"] = NumberSchema("画布纵坐标，默认按步骤顺序排列"),
                            ["joinMode"] = EnumSchema("多上游汇合方式", "All", "Any"),
                        },
                    },
                },
                ["edges"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "节点之间的有向依赖；sourceId/targetId 引用 nodes.id",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sourceId"] = StringSchema("上游节点 id"),
                            ["targetId"] = StringSchema("下游节点 id"),
                            ["condition"] = EnumSchema(
                                "分支条件",
                                "Always",
                                "OnSuccess",
                                "OnFailure",
                                "OnSkipped",
                                "Expression"
                            ),
                            ["rules"] = SuiteConditionSchema(),
                        },
                        ["required"] = new JsonArray("sourceId", "targetId"),
                    },
                },
                ["maxParallelism"] = IntegerSchema("最大并行节点数；0 表示不限制，默认 4", 0, 64),
            },
            ["required"] = new JsonArray("nodes"),
        };

    private static JsonObject SuiteConditionSchema() =>
        new()
        {
            ["type"] = "object",
            ["description"] = "条件表达式；condition=Expression 时至少一条规则",
            ["properties"] = new JsonObject
            {
                ["mode"] = EnumSchema("规则组合方式", "All", "Any"),
                ["rules"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["source"] = EnumSchema(
                                "条件来源",
                                "StatusCode",
                                "BodyJson",
                                "Header",
                                "ElapsedMs",
                                "Result",
                                "Variable"
                            ),
                            ["path"] = StringSchema("JSON 路径、响应头名或环境变量名"),
                            ["operator"] = EnumSchema(
                                "比较操作符",
                                "Equals",
                                "NotEquals",
                                "Contains",
                                "NotContains",
                                "Exists",
                                "NotExists",
                                "GreaterThan",
                                "LessThan",
                                "Matches",
                                "In"
                            ),
                            ["expected"] = StringSchema("期望值"),
                        },
                    },
                },
            },
        };

    private static JsonObject NumberSchema(string description) =>
        new() { ["type"] = "number", ["description"] = description };

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
            OptionalInt(arguments, "position"),
            OptionalInt(arguments, "timeoutSeconds"),
            ReadAuth(arguments),
            OptionalString(arguments, "protocol"),
            OptionalString(arguments, "messageBody"),
            OptionalString(arguments, "messageFormat"),
            OptionalInt(arguments, "waitMilliseconds"),
            OptionalString(arguments, "socketIoEvent"),
            OptionalString(arguments, "socketIoNamespace"),
            OptionalString(arguments, "socketIoPath"),
            OptionalString(arguments, "tcpTerminator"),
            OptionalBool(arguments, "tcpUseTls"),
            OptionalString(arguments, "soapVersion"),
            OptionalString(arguments, "soapAction"),
            OptionalString(arguments, "grpcService"),
            OptionalString(arguments, "grpcMethod"),
            OptionalBool(arguments, "grpcUseTls"),
            OptionalInt(arguments, "grpcDeadlineSeconds")
        );

    private static McpAuthInput? ReadAuth(JsonObject arguments)
    {
        if (arguments["auth"] is not JsonObject auth)
        {
            return null;
        }

        return new McpAuthInput(
            OptionalString(auth, "authType"),
            OptionalString(auth, "target"),
            OptionalString(auth, "token"),
            OptionalString(auth, "username"),
            OptionalString(auth, "password"),
            OptionalString(auth, "apiKeyName"),
            OptionalString(auth, "apiKeyValue"),
            OptionalString(auth, "apiKeyLocation")
        );
    }

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

    private static double? OptionalDouble(JsonObject arguments, string name)
    {
        var node = arguments[name];
        return node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonObject arguments, string name)
    {
        if (arguments[name] is not JsonArray array || array.Count == 0)
        {
            return StorageCategories.All;
        }

        return array
            .Select(node => node?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    private static DateTimeOffset RequireDate(JsonObject arguments, string name)
    {
        var value = RequireString(arguments, name);
        return DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var timestamp
        )
            ? timestamp
            : throw new InvalidOperationException($"{name} 不是有效时间：{value}");
    }

    private static string Truncate(string text, int max)
    {
        return text.Length <= max ? text : text[..max] + "\n…（响应体已截断）";
    }
}
