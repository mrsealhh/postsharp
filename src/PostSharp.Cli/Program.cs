using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace PostSharp.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (CliException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            if (
                options.Arguments.Count > 0
                && string.Equals(
                    options.Arguments[0],
                    "mcp-stdio",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return await McpStdioBridge.RunAsync(
                    options.Endpoint,
                    options.Timeout,
                    cancellation.Token
                );
            }

            using var client = new McpClient(options.Endpoint, options.ClientName, options.Timeout);
            await client.InitializeAsync(cancellation.Token);
            return await RunAsync(client, options, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("操作已取消。");
            return 3;
        }
        catch (CliConnectionException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 3;
        }
        catch (CliException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"执行失败：{exception.Message}");
            return 3;
        }
    }

    private static async Task<int> RunAsync(
        McpClient client,
        CliOptions options,
        CancellationToken cancellationToken
    )
    {
        var command = options.Arguments;
        if (command.Count == 0)
        {
            PrintHelp();
            return 0;
        }

        return (command[0], command.Count > 1 ? command[1] : string.Empty) switch
        {
            ("suite", "list") => await ListSuitesAsync(client, options, cancellationToken),
            ("suite", "get") => await GetSuiteAsync(client, options, cancellationToken),
            ("suite", "validate") => await ValidateSuiteAsync(client, options, cancellationToken),
            ("suite", "run") => await RunSuiteAsync(client, options, cancellationToken),
            ("suite", "runs") => await ListSuiteRunsAsync(client, options, cancellationToken),
            ("suite", "rerun-failed") => await RerunFailedAsync(client, options, cancellationToken),
            ("suite", "continue") => await ContinueSuiteAsync(client, options, cancellationToken),
            ("request", "send") => await SendRequestAsync(client, options, cancellationToken),
            ("audit", "list") => await ListAuditAsync(client, options, cancellationToken),
            _ => throw new CliException($"未知命令：{string.Join(' ', command)}。"),
        };
    }

    private static async Task<int> ListSuitesAsync(
        McpClient client,
        CliOptions options,
        CancellationToken cancellationToken
    )
    {
        var result = await client.CallToolAsync("list_suites", new JsonObject(), cancellationToken);
        if (options.Json)
        {
            WriteJson(result.Text);
            return 0;
        }

        using var document = Parse(result.Text);
        foreach (var suite in document.RootElement.EnumerateArray())
        {
            var steps = suite.TryGetProperty("steps", out var stepsNode)
                ? stepsNode.GetArrayLength()
                : 0;
            Console.WriteLine(
                $"{ReadString(suite, "id"), -38} {ReadString(suite, "name"), -32} {steps} 步"
            );
        }

        return 0;
    }

    private static async Task<int> GetSuiteAsync(
        McpClient client,
        CliOptions options,
        CancellationToken cancellationToken
    )
    {
        var suiteId = RequireArgument(options, 2, "suite get <suiteId>");
        var result = await client.CallToolAsync(
            "get_suite",
            new JsonObject { ["id"] = suiteId },
            cancellationToken
        );
        if (options.Json)
        {
            WriteJson(result.Text);
            return 0;
        }

        using var document = Parse(result.Text);
        var root = document.RootElement;
        Console.WriteLine($"套件：{ReadString(root, "name")}");
        Console.WriteLine($"id：{ReadString(root, "id")}");
        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in steps.EnumerateArray())
            {
                Console.WriteLine(
                    $"  {ReadString(step, "method"), -7} {ReadString(step, "requestName"), -24} {ReadString(step, "url")}"
                );
            }
        }

        return 0;
    }

    private static async Task<int> ValidateSuiteAsync(
        McpClient client,
        CliOptions options,
        CancellationToken cancellationToken
    )
    {
        var suiteId = RequireArgument(options, 2, "suite validate <suiteId>");
        var result = await client.CallToolAsync(
            "validate_suite",
            new JsonObject { ["id"] = suiteId },
            cancellationToken
        );
        if (options.Json)
        {
            WriteJson(result.Text);
        }
        else
        {
            using var document = Parse(result.Text);
            var root = document.RootElement;
            Console.WriteLine(ReadString(root, "summary"));
            if (root.TryGetProperty("issues", out var issues))
            {
                foreach (var issue in issues.EnumerateArray())
                {
                    Console.WriteLine(
                        $"  [{ReadString(issue, "severity")}] {ReadString(issue, "code")} · {ReadString(issue, "message")}"
                    );
                }
            }
        }

        using var verdict = Parse(result.Text);
        return
            verdict.RootElement.TryGetProperty("hasErrors", out var hasErrors)
            && hasErrors.GetBoolean()
            ? 1
            : 0;
    }

    private static async Task<int> RunSuiteAsync(
        McpClient client,
        CliOptions options,
        CancellationToken cancellationToken
    )
    {
        var suiteId = RequireArgument(options, 2, "suite run <suiteId>");
        var started = await client.CallToolAsync(
            "start_suite_run",
            new JsonObject { ["id"] = suiteId },
            cancellationToken
        );
        using var startDocument = Parse(started.Text);
        var jobId = ReadString(startDocument.RootElement, "jobId");
        if (jobId.Length == 0)
        {
            throw new CliException("服务端没有返回 jobId，无法查询后台任务。");
        }

        var lastSummary = string.Empty;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = await client.CallToolAsync(
                "get_suite_run_job",
                new JsonObject { ["jobId"] = jobId },
                cancellationToken
            );
            using var document = Parse(job.Text);
            var root = document.RootElement;
            var status = ReadString(root, "status");
            var summary = ReadString(root, "summary");
            if (!string.Equals(summary, lastSummary, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(summary);
                lastSummary = summary;
            }

            if (status is not ("Running" or "Cancelling"))
            {
                if (options.Json)
                {
                    WriteJson(job.Text);
                }
                else
                {
                    Console.WriteLine(summary);
                    if (ReadString(root, "error") is { Length: > 0 } error)
                    {
                        Console.WriteLine($"错误：{error}");
                    }

                    if (
                        root.TryGetProperty("runId", out var runId)
                        && runId.ValueKind == JsonValueKind.String
                    )
                    {
                        Console.WriteLine($"运行 id：{runId.GetString()}");
                    }
                }

                var completedRunId = ReadString(root, "runId");
                if (completedRunId.Length > 0)
                {
                    var runResult = await client.CallToolAsync(
                        "get_suite_run",
                        new JsonObject { ["suiteId"] = suiteId, ["runId"] = completedRunId },
                        cancellationToken
                    );
                    WriteJUnitFromText(runResult.Text, options);
                }

                return status == "Completed" ? 0 : 1;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }

    private static async Task<int> ListSuiteRunsAsync(
        McpClient client,
        CliOptions options,
        CancellationToken cancellationToken
    )
    {
        var suiteId = RequireArgument(options, 2, "suite runs <suiteId>");
        var result = await client.CallToolAsync(
            "list_suite_runs",
            new JsonObject { ["suiteId"] = suiteId },
            cancellationToken
        );
        if (options.Json)
        {
            WriteJson(result.Text);
            return 0;
        }

        using var document = Parse(result.Text);
        foreach (var run in document.RootElement.EnumerateArray())
        {
            Console.WriteLine($"{ReadString(run, "id"), -38} {ReadString(run, "summary")}");
        }

        return 0;
    }

    private static async Task<int> RerunFailedAsync(
        McpClient client,
        CliOptions options,
        CancellationToken cancellationToken
    )
    {
        var suiteId = RequireArgument(options, 2, "suite rerun-failed <suiteId> <runId>");
        var runId = RequireArgument(options, 3, "suite rerun-failed <suiteId> <runId>");
        var result = await client.CallToolAsync(
            "rerun_failed_suite",
            new JsonObject { ["suiteId"] = suiteId, ["runId"] = runId },
            cancellationToken
        );
        WriteJUnitFromText(result.Text, options);
        return PrintRunResult(result.Text, options.Json);
    }

    private static async Task<int> ContinueSuiteAsync(
        McpClient client,
        CliOptions options,
        CancellationToken cancellationToken
    )
    {
        var suiteId = RequireArgument(options, 2, "suite continue <suiteId> <runId> <stepId>");
        var runId = RequireArgument(options, 3, "suite continue <suiteId> <runId> <stepId>");
        var stepId = RequireArgument(options, 4, "suite continue <suiteId> <runId> <stepId>");
        var result = await client.CallToolAsync(
            "continue_suite_from_step",
            new JsonObject
            {
                ["suiteId"] = suiteId,
                ["runId"] = runId,
                ["stepId"] = stepId,
            },
            cancellationToken
        );
        WriteJUnitFromText(result.Text, options);
        return PrintRunResult(result.Text, options.Json);
    }

    private static async Task<int> SendRequestAsync(
        McpClient client,
        CliOptions options,
        CancellationToken cancellationToken
    )
    {
        var requestId = RequireArgument(options, 2, "request send <requestId>");
        var result = await client.CallToolAsync(
            "send_request",
            new JsonObject { ["id"] = requestId },
            cancellationToken
        );
        if (options.Json)
        {
            WriteJson(result.Text);
        }
        else
        {
            using var document = Parse(result.Text);
            var root = document.RootElement;
            Console.WriteLine(
                $"{ReadString(root, "method")} {ReadString(root, "url")} -> {ReadString(root, "statusCode")}"
            );
            var body = ReadString(root, "body");
            if (body.Length > 0)
            {
                Console.WriteLine(body);
            }
        }

        return 0;
    }

    private static async Task<int> ListAuditAsync(
        McpClient client,
        CliOptions options,
        CancellationToken cancellationToken
    )
    {
        var limit = options.GetOption("--limit", "50") ?? "50";
        var source = options.GetOption("--source", null);
        var arguments = new JsonObject
        {
            ["limit"] = int.Parse(limit, CultureInfo.InvariantCulture),
        };
        if (source is not null)
        {
            arguments["source"] = source;
        }

        var result = await client.CallToolAsync("list_audit", arguments, cancellationToken);
        if (options.Json)
        {
            WriteJson(result.Text);
            return 0;
        }

        using var document = Parse(result.Text);
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            Console.WriteLine(
                $"{ReadString(entry, "method"), -7} {ReadString(entry, "statusCode"), -5} {ReadString(entry, "url")}"
            );
        }

        return 0;
    }

    private static void WriteJUnitFromText(string text, CliOptions options)
    {
        if (options.GetOption("--junit", null) is not { Length: > 0 } path)
        {
            return;
        }

        JUnitReport.Write(text, path);
    }

    private static int PrintRunResult(string text, bool json)
    {
        if (json)
        {
            WriteJson(text);
        }
        else
        {
            using var document = Parse(text);
            var root = document.RootElement;
            Console.WriteLine(ReadString(root, "summary"));
        }

        using var verdict = Parse(text);
        var rootElement = verdict.RootElement;
        return rootElement.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.String
            && error.GetString()?.Length > 0
                ? 1
            : rootElement.TryGetProperty("failed", out var failed)
            && failed.ValueKind == JsonValueKind.Number
            && failed.GetInt32() > 0
                ? 1
            : 0;
    }

    private static string RequireArgument(CliOptions options, int index, string usage)
    {
        if (options.Arguments.Count <= index || string.IsNullOrWhiteSpace(options.Arguments[index]))
        {
            throw new CliException($"缺少参数。用法：postsharp-cli {usage}");
        }

        return options.Arguments[index];
    }

    private static JsonDocument Parse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new CliException($"服务端返回的不是 JSON：{exception.Message}", exception);
        }
    }

    private static string ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    private static void WriteJson(string text) => Console.WriteLine(text);

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            PostSharp CLI（要求 PostSharp 正在运行）

            用法：
              postsharp-cli [--endpoint URL] [--json] [--timeout 秒] [--client 名称] <命令>

            命令：
              suite list
              suite get <suiteId>
              suite validate <suiteId>
              suite run <suiteId>
              suite runs <suiteId>
              suite rerun-failed <suiteId> <runId>
              suite continue <suiteId> <runId> <stepId>
              request send <requestId>
              audit list [--source UI|MCP] [--limit 数量]
              suite run/rerun-failed/continue 支持 --junit <文件>
              mcp-stdio（把 stdin/stdout 的 MCP JSON-RPC 转发到 HTTP 端点）
              mcp-stdio

            退出码：
              0 成功；1 套件/请求失败；2 参数或服务端错误；3 连接失败或取消。

            示例：
              postsharp-cli suite list
              postsharp-cli suite run <suiteId>
              postsharp-cli suite validate <suiteId> --json
              postsharp-cli audit list --source MCP --limit 20
            """
        );
    }
}

internal sealed record CliOptions(
    Uri Endpoint,
    bool Json,
    TimeSpan Timeout,
    string ClientName,
    bool ShowHelp,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Options
)
{
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var endpoint = new Uri("http://127.0.0.1:5199/mcp");
        var json = false;
        var timeout = TimeSpan.FromSeconds(30);
        var clientName = "PostSharp.Cli";
        var help = false;
        var arguments = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--help" or "-h":
                    help = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--endpoint":
                    endpoint = ParseEndpoint(ReadValue(args, ref index, argument));
                    break;
                case "--timeout":
                    if (
                        !double.TryParse(
                            ReadValue(args, ref index, argument),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var seconds
                        )
                        || seconds <= 0
                    )
                    {
                        throw new CliException("--timeout 必须是正数秒。");
                    }

                    timeout = TimeSpan.FromSeconds(seconds);
                    break;
                case "--client":
                    clientName = ReadValue(args, ref index, argument);
                    break;
                case "--limit" or "--source" or "--junit":
                    options[argument] = ReadValue(args, ref index, argument);
                    break;
                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliException($"未知选项：{argument}");
                    }

                    arguments.Add(argument);
                    break;
            }
        }

        return new CliOptions(endpoint, json, timeout, clientName, help, arguments, options);
    }

    public string? GetOption(string name, string? fallback) =>
        Options.TryGetValue(name, out var value) ? value : fallback;

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count)
        {
            throw new CliException($"{option} 缺少值。");
        }

        index++;
        return args[index];
    }

    private static Uri ParseEndpoint(string value)
    {
        if (
            !Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw new CliException("--endpoint 必须是 http 或 https 地址。");
        }

        return endpoint;
    }
}

internal sealed class CliException : Exception
{
    public CliException(string message)
        : base(message) { }

    public CliException(string message, Exception innerException)
        : base(message, innerException) { }
}

internal sealed class CliConnectionException : Exception
{
    public CliConnectionException(string message)
        : base(message) { }
}

internal static class McpStdioBridge
{
    public static async Task<int> RunAsync(
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        TrySetUtf8Console();
        using var client = new McpClient(endpoint, "PostSharp.Cli.Stdio", timeout);
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var response = await client
                    .PostRawAsync(line, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(response))
                {
                    await Console.Out.WriteLineAsync(response).ConfigureAwait(false);
                    await Console.Out.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.Error.WriteLine($"MCP stdio 转发失败：{exception.Message}");
                if (TryReadId(line) is { } id)
                {
                    var error = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id.DeepClone(),
                        ["error"] = new JsonObject
                        {
                            ["code"] = -32603,
                            ["message"] = exception.Message,
                        },
                    };
                    await Console.Out.WriteLineAsync(error.ToJsonString()).ConfigureAwait(false);
                    await Console.Out.FlushAsync().ConfigureAwait(false);
                }
            }
        }

        return 0;
    }

    private static JsonNode? TryReadId(string line)
    {
        try
        {
            return JsonNode.Parse(line)?["id"]?.DeepClone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TrySetUtf8Console()
    {
        try
        {
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = encoding;
            Console.OutputEncoding = encoding;
        }
        catch (IOException)
        {
            // Redirected consoles may not expose configurable encodings.
        }
    }
}

internal sealed class McpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _clientName;
    private readonly TimeSpan _timeout;
    private int _nextId;

    public McpClient(Uri endpoint, string clientName, TimeSpan timeout)
    {
        _endpoint = endpoint;
        _clientName = clientName;
        _timeout = timeout;
        _http = new HttpClient { Timeout = timeout };
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream")
        );
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await SendAsync(
            "initialize",
            new JsonObject
            {
                ["protocolVersion"] = "2025-03-26",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = _clientName, ["version"] = "1.0" },
            },
            cancellationToken
        );
    }

    public async Task<ToolResult> CallToolAsync(
        string name,
        JsonObject arguments,
        CancellationToken cancellationToken
    )
    {
        var result = await SendAsync(
            "tools/call",
            new JsonObject { ["name"] = name, ["arguments"] = arguments },
            cancellationToken
        );
        if (
            result["content"] is not JsonArray content
            || content.Count == 0
            || content[0] is not JsonObject first
            || first["text"]?.GetValue<string>() is not { } text
        )
        {
            throw new CliException("服务端返回的工具结果格式不正确。");
        }

        var isError = result["isError"]?.GetValue<bool>() == true;
        if (isError)
        {
            throw new CliException(text);
        }

        return new ToolResult(text);
    }

    public void Dispose() => _http.Dispose();

    public async Task<string?> PostRawAsync(string payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CliConnectionException("连接 PostSharp 超时，请确认应用正在运行。");
        }
        catch (HttpRequestException exception)
        {
            throw new CliConnectionException(
                $"无法连接 PostSharp：{exception.Message}。请先启动应用并确认 MCP 端口。"
            );
        }

        using (response)
        {
            var text = await response
                .Content.ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new CliConnectionException(
                    $"PostSharp 返回 HTTP {(int)response.StatusCode}：{text}"
                );
            }

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }

    private async Task<JsonObject> SendAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken
    )
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CliConnectionException("连接 PostSharp 超时，请确认应用正在运行。");
        }
        catch (HttpRequestException exception)
        {
            throw new CliConnectionException(
                $"无法连接 PostSharp：{exception.Message}。请先启动应用并确认 MCP 端口。"
            );
        }

        using (response)
        {
            var text = await response
                .Content.ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new CliConnectionException(
                    $"PostSharp 返回 HTTP {(int)response.StatusCode}：{text}"
                );
            }

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text);
            }
            catch (JsonException exception)
            {
                throw new CliException("PostSharp 返回了无效的 JSON-RPC 响应。", exception);
            }

            if (root is not JsonObject responseObject)
            {
                throw new CliException("PostSharp 返回了空响应。");
            }

            if (responseObject["error"] is JsonObject error)
            {
                throw new CliException(
                    error["message"]?.GetValue<string>() ?? "PostSharp 返回错误。"
                );
            }

            if (responseObject["result"] is not JsonObject result)
            {
                throw new CliException("PostSharp 响应缺少 result。");
            }

            return result;
        }
    }
}

internal static class JUnitReport
{
    public static void Write(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var suiteName = Text(root, "suiteName");
        if (suiteName.Length == 0)
        {
            suiteName = Text(root, "name");
        }

        var steps =
            root.TryGetProperty("steps", out var stepsNode)
            && stepsNode.ValueKind == JsonValueKind.Array
                ? stepsNode.EnumerateArray().ToList()
                : new List<JsonElement>();
        var failures = 0;
        var skipped = 0;
        var totalMs = 0L;
        var cases = new List<XElement>();
        foreach (var step in steps)
        {
            var name = Text(step, "name");
            if (name.Length == 0)
            {
                name = "step";
            }

            var elapsedMs = Number(step, "elapsedMs");
            totalMs += elapsedMs;
            var testCase = new XElement(
                "testcase",
                new XAttribute("classname", "PostSharp"),
                new XAttribute("name", name),
                new XAttribute(
                    "time",
                    (elapsedMs / 1000d).ToString("0.000", CultureInfo.InvariantCulture)
                )
            );
            if (Bool(step, "skipped"))
            {
                skipped++;
                testCase.Add(new XElement("skipped"));
            }
            else if (!Bool(step, "passed"))
            {
                failures++;
                var error = Text(step, "error");
                var status = Number(step, "statusCode");
                testCase.Add(
                    new XElement(
                        "failure",
                        new XAttribute(
                            "message",
                            error.Length > 0
                                ? error
                                : $"HTTP {status.ToString(CultureInfo.InvariantCulture)}"
                        ),
                        error
                    )
                );
            }

            cases.Add(testCase);
        }

        var suite = new XElement(
            "testsuite",
            new XAttribute("name", suiteName.Length > 0 ? suiteName : "PostSharp Suite"),
            new XAttribute("tests", cases.Count),
            new XAttribute("failures", failures),
            new XAttribute("errors", 0),
            new XAttribute("skipped", skipped),
            new XAttribute(
                "time",
                (totalMs / 1000d).ToString("0.000", CultureInfo.InvariantCulture)
            ),
            cases
        );
        var report = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("testsuites", suite)
        );
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = XmlWriter.Create(
            fullPath,
            new XmlWriterSettings
            {
                Indent = true,
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            }
        );
        report.Save(writer);
        Console.Error.WriteLine($"JUnit 报告：{fullPath}");
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;
}

internal sealed record ToolResult(string Text);
