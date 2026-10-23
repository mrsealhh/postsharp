using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PostSharp.Services;

/// <summary>
/// A tiny local HTTP API used by the sample requests (and by anyone who wants to try PostSharp offline).
/// It uses a raw TCP listener so it needs no URL ACL, no admin rights and no extra package.
/// </summary>
public sealed class MockApiServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly MockUser[] Users =
    {
        new(1, "林小满", "xiaoman.lin@example.com", "admin", true),
        new(2, "陈一鸣", "yiming.chen@example.com", "editor", true),
        new(3, "周宁", "ning.zhou@example.com", "viewer", true),
        new(4, "许知远", "zhiyuan.xu@example.com", "editor", false),
        new(5, "顾清和", "qinghe.gu@example.com", "viewer", true)
    };

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public int Port { get; private set; }

    public bool IsRunning { get; private set; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>
    /// Optional HTTP handler that gets the first look at every request. It returns null to let the mock
    /// routes handle the request, which is how the MCP endpoint shares this listener with the mock API.
    /// </summary>
    public Func<string, string, IReadOnlyDictionary<string, string>, string, CancellationToken, Task<RawHttpResponse?>>
        ? CustomHandler { get; set; }

    public void Start(int preferredPort = 5199)
    {
        if (IsRunning)
        {
            return;
        }

        for (var port = preferredPort; port < preferredPort + 12; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (SocketException)
            {
                // Port busy: try the next one.
            }
        }

        if (_listener is null)
        {
            IsRunning = false;
            return;
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                var reader = new ConnectionReader(stream);

                var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    await WriteResponseAsync(stream, 400, "Bad Request", new { error = "invalid_request_line" }, cancellationToken);
                    return;
                }

                var method = parts[0].ToUpperInvariant();
                var target = parts[1];

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line))
                    {
                        break;
                    }

                    var separator = line.IndexOf(':');
                    if (separator > 0)
                    {
                        headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                    }
                }

                var contentLength = 0;
                if (headers.TryGetValue("Content-Length", out var lengthText))
                {
                    int.TryParse(lengthText, out contentLength);
                }

                var body = string.Empty;
                if (contentLength > 0)
                {
                    var bodyBytes = await reader.ReadExactAsync(contentLength, cancellationToken).ConfigureAwait(false);
                    var charset = headers.TryGetValue("Content-Type", out var contentType)
                        && contentType.Contains("charset=", StringComparison.OrdinalIgnoreCase)
                        ? contentType[(contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase) + 8)..].Trim('"', ' ', ';')
                        : "utf-8";

                    try
                    {
                        body = Encoding.GetEncoding(charset).GetString(bodyBytes);
                    }
                    catch
                    {
                        body = Encoding.UTF8.GetString(bodyBytes);
                    }
                }

                var uri = new Uri("http://127.0.0.1" + target);
                var query = ParseQuery(uri.Query);
                var path = uri.AbsolutePath;

                if (CustomHandler is not null)
                {
                    var custom = await CustomHandler(method, target, headers, body, cancellationToken)
                        .ConfigureAwait(false);
                    if (custom is not null)
                    {
                        await WriteRawResponseAsync(stream, custom, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                if (method == "OPTIONS")
                {
                    await WriteResponseAsync(stream, 204, "No Content", null, cancellationToken);
                    return;
                }

                switch (path)
                {
                    case "/api/health":
                        await WriteResponseAsync(stream, 200, "OK", new
                        {
                            status = "ok",
                            service = "postsharp-mock",
                            version = "0.1.0",
                            time = DateTimeOffset.Now
                        }, cancellationToken);
                        return;

                    case "/api/users" when method == "GET":
                        await WriteResponseAsync(stream, 200, "OK", new
                        {
                            total = Users.Length,
                            users = Users
                        }, cancellationToken);
                        return;

                    case "/api/error":
                        await WriteResponseAsync(stream, 500, "Internal Server Error", new
                        {
                            code = 500,
                            message = "这是一条模拟的错误响应。",
                            hint = "用状态徽标和响应头验证错误排查流程。"
                        }, cancellationToken);
                        return;

                    case "/api/slow":
                        var delay = query.TryGetValue("ms", out var msText) && int.TryParse(msText, out var ms)
                            ? Math.Clamp(ms, 0, 5000)
                            : 1200;
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        await WriteResponseAsync(stream, 200, "OK", new
                        {
                            message = "慢响应完成。",
                            delayMs = delay
                        }, cancellationToken);
                        return;

                    case "/api/large":
                        var sizeKb = query.TryGetValue("kb", out var kbText) && int.TryParse(kbText, out var kb)
                            ? Math.Clamp(kb, 1, 4096)
                            : 200;
                        await WriteResponseAsync(stream, 200, "OK", new
                        {
                            message = "大响应测试。",
                            sizeKb,
                            data = new string('A', sizeKb * 1024),
                            items = Enumerable.Range(1, 50)
                                .Select(index => new { id = index, name = $"item-{index}" })
                                .ToArray()
                        }, cancellationToken);
                        return;

                    case "/api/ai/stream":
                        await WriteSseAsync(stream, cancellationToken).ConfigureAwait(false);
                        return;

                    case "/api/upload" when method == "POST":
                        await WriteResponseAsync(stream, 200, "OK", new
                        {
                            receivedBytes = contentLength,
                            fileName = MultipartFileName(body) ?? "unknown",
                            contentType = headers.GetValueOrDefault("Content-Type") ?? string.Empty,
                            message = "文件已接收（Mock 服务不会落盘）。"
                        }, cancellationToken);
                        return;

                    case "/api/orders" when method == "POST":
                        await WriteResponseAsync(stream, 201, "Created", new
                        {
                            id = "ord_2026_1042",
                            status = "created",
                            received = TryParseJson(body),
                            createdAt = DateTimeOffset.Now
                        }, cancellationToken);
                        return;

                    case "/api/echo":
                        await WriteResponseAsync(stream, 200, "OK", new
                        {
                            method,
                            path,
                            query,
                            headers,
                            body = TryParseJson(body),
                            receivedAt = DateTimeOffset.Now
                        }, cancellationToken);
                        return;
                }

                if (path.StartsWith("/api/users/", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(path["/api/users/".Length..], out var userId))
                {
                    var user = Users.FirstOrDefault(candidate => candidate.id == userId);
                    if (user is null)
                    {
                        await WriteResponseAsync(stream, 404, "Not Found", new
                        {
                            code = 404,
                            message = $"没有 id 为 {userId} 的用户。"
                        }, cancellationToken);
                        return;
                    }

                    await WriteResponseAsync(stream, 200, "OK", user, cancellationToken);
                    return;
                }

                await WriteResponseAsync(stream, 200, "OK", new
                {
                    message = "PostSharp mock API",
                    method,
                    path,
                    query,
                    body = TryParseJson(body),
                    endpoints = new[]
                    {
                        "GET /api/health",
                        "GET /api/users",
                        "GET /api/users/{id}",
                        "POST /api/orders",
                        "POST /api/echo",
                        "GET /api/error",
                        "GET /api/slow?ms=1200"
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Server stopping.
            }
            catch
            {
                // A mock request must never take the app down.
            }
        }
    }

    private static object? TryParseJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body);
        }
        catch
        {
            return body;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0)
            {
                values[Uri.UnescapeDataString(pair)] = string.Empty;
            }
            else
            {
                values[Uri.UnescapeDataString(pair[..separator])] =
                    Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            }
        }

        return values;
    }

    private static string? MultipartFileName(string body)
    {
        const string marker = "filename=";
        var start = body.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        if (start < body.Length && body[start] == '"')
        {
            start++;
            var quoteEnd = body.IndexOf('"', start);
            return quoteEnd > start ? body[start..quoteEnd] : null;
        }

        var end = body.IndexOfAny(new[] { '\r', '\n', ';' }, start);
        return end > start ? body[start..end].Trim() : body[start..].Trim();
    }

    private static async Task WriteSseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {        var head = "HTTP/1.1 200 OK\r\n"
                   + "Content-Type: text/event-stream; charset=utf-8\r\n"
                   + "Cache-Control: no-cache\r\n"
                   + "Access-Control-Allow-Origin: *\r\n"
                   + "Connection: close\r\n"
                   + "\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var thinkingChunks = new[]
        {
            "先拆解问题：",
            "确认接口、审计和聚合链路，",
            "再给出完整回答。"
        };

        var contentChunks = new[]
        {
            "# PostSharp SSE 示例\n\n",
            "这是一条流式响应。\n\n",
            "- thinking 和 content 会被自动聚合\n",
            "- 原始事件仍然保留在响应体中\n\n",
            "你可以直接复制内容，或在审计中回看完整输出。"
        };

        foreach (var chunk in thinkingChunks)
        {
            var payload = JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { reasoning_content = chunk } } }
            }, SseJsonOptions);
            await WriteSseEventAsync(stream, payload, cancellationToken).ConfigureAwait(false);
            await Task.Delay(70, cancellationToken).ConfigureAwait(false);
        }

        foreach (var chunk in contentChunks)
        {
            var payload = JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = chunk } } }
            }, SseJsonOptions);
            await WriteSseEventAsync(stream, payload, cancellationToken).ConfigureAwait(false);
            await Task.Delay(70, cancellationToken).ConfigureAwait(false);
        }

        await WriteSseEventAsync(stream, "{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}", cancellationToken)
            .ConfigureAwait(false);
        await WriteSseEventAsync(stream, "[DONE]", cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSseEventAsync(
        NetworkStream stream,
        string data,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        object? payload,
        CancellationToken cancellationToken)
    {
        var bodyBytes = payload is null
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));

        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n")
            .Append("Access-Control-Allow-Origin: *\r\n")
            .Append("Access-Control-Allow-Headers: *\r\n")
            .Append("Connection: close\r\n")
            .Append("\r\n")
            .ToString();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteRawResponseAsync(
        NetworkStream stream,
        RawHttpResponse response,
        CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(response.Body ?? string.Empty);
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(response.ReasonPhrase).Append("\r\n")
            .Append("Content-Type: ").Append(response.ContentType).Append("\r\n")
            .Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n")
            .Append("Access-Control-Allow-Origin: *\r\n")
            .Append("Access-Control-Allow-Headers: *\r\n")
            .Append("Access-Control-Expose-Headers: Mcp-Session-Id\r\n")
            .Append("Connection: close\r\n");

        if (response.Headers is not null)
        {
            foreach (var (key, value) in response.Headers)
            {
                head.Append(key).Append(": ").Append(value).Append("\r\n");
            }
        }

        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _acceptLoop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore shutdown races.
        }
        finally
        {
            _cts?.Dispose();
            IsRunning = false;
        }
    }

    private sealed class ConnectionReader
    {
        private readonly NetworkStream _stream;
        private readonly byte[] _buffer = new byte[8192];
        private int _start;
        private int _end;

        public ConnectionReader(NetworkStream stream) => _stream = stream;

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                for (var index = _start; index < _end; index++)
                {
                    if (_buffer[index] != (byte)'\n')
                    {
                        continue;
                    }

                    var length = index - _start;
                    if (length > 0 && _buffer[index - 1] == (byte)'\r')
                    {
                        length--;
                    }

                    var line = Encoding.UTF8.GetString(_buffer, _start, length);
                    _start = index + 1;
                    return line;
                }

                if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }
            }
        }

        public async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
        {
            var result = new byte[count];
            var written = 0;

            while (written < count)
            {
                if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                var available = Math.Min(_end - _start, count - written);
                Buffer.BlockCopy(_buffer, _start, result, written, available);
                _start += available;
                written += available;
            }

            return written == count ? result : result[..written];
        }

        private async Task<bool> FillAsync(CancellationToken cancellationToken)
        {
            if (_start > 0)
            {
                var remaining = _end - _start;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, remaining);
                }

                _start = 0;
                _end = remaining;
            }

            if (_end == _buffer.Length)
            {
                return false;
            }

            var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            _end += read;
            return true;
        }
    }

    private sealed record MockUser(int id, string name, string email, string role, bool active);
}

public sealed record RawHttpResponse(
    int StatusCode,
    string ReasonPhrase,
    string ContentType,
    string Body,
    IReadOnlyDictionary<string, string>? Headers = null);
