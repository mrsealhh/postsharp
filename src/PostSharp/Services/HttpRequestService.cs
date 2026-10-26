using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using PostSharp.Models;

namespace PostSharp.Services;

public sealed record SseProgress(string Raw, string Thinking, string Content, int EventCount);

public sealed record HttpResult(
    bool HasResponse,
    int StatusCode,
    string ReasonPhrase,
    string Body,
    bool IsJson,
    string MediaType,
    long ElapsedMs,
    long SizeBytes,
    IReadOnlyList<ResponseHeaderView> Headers,
    string? Error,
    bool IsSse = false,
    string SseThinking = "",
    string SseContent = "",
    int SseEventCount = 0,
    string SseRaw = "",
    bool SseCancelled = false
);

public sealed class HttpRequestService
{
    private const int MaxFormatLength = 256 * 1024;
    private const int SseProgressIntervalMs = 150;

    private static readonly HttpClient SharedClient = CreateClient();

    private readonly HttpClient _client;

    public HttpRequestService()
        : this(null) { }

    public HttpRequestService(HttpClient? client)
    {
        _client = client ?? SharedClient;
    }

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public int TimeoutSeconds { get; set; } = 60;

    public bool AutoFormatResponse { get; set; } = true;

    public async Task<HttpResult> SendAsync(
        ComposedRequest composed,
        CancellationToken cancellationToken,
        int timeoutSeconds = 0,
        IProgress<SseProgress>? sseProgress = null
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var effectiveTimeoutSeconds =
            timeoutSeconds > 0
                ? Math.Clamp(timeoutSeconds, 1, 3600)
                : Math.Clamp(TimeoutSeconds, 1, 3600);

        try
        {
            using var message = new HttpRequestMessage(
                new HttpMethod(composed.Method),
                composed.Url
            );

            HttpContent? content = null;
            if (composed.IsMultipart)
            {
                var multipart = new MultipartFormDataContent();
                foreach (
                    var field in composed.FormFields ?? Array.Empty<KeyValuePair<string, string>>()
                )
                {
                    multipart.Add(new StringContent(field.Value, Encoding.UTF8), field.Key);
                }

                if (
                    composed.UploadFiles is { Count: > 0 }
                    || !string.IsNullOrWhiteSpace(composed.UploadFilePath)
                )
                {
                    var uploadFiles = composed.UploadFiles;
                    if (uploadFiles is null || uploadFiles.Count == 0)
                    {
                        uploadFiles = new[]
                        {
                            new ComposedUploadFile(
                                composed.UploadFieldName ?? "file",
                                composed.UploadFilePath ?? string.Empty
                            ),
                        };
                    }

                    foreach (var file in uploadFiles)
                    {
                        var fileStream = File.OpenRead(file.FilePath);
                        var fileContent = new StreamContent(fileStream);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
                            GuessContentType(file.FilePath)
                        );
                        multipart.Add(
                            fileContent,
                            string.IsNullOrWhiteSpace(file.FieldName) ? "file" : file.FieldName,
                            Path.GetFileName(file.FilePath)
                        );
                    }
                }

                content = multipart;
                message.Content = content;
            }
            else if (composed.Body is not null)
            {
                content = new ByteArrayContent(Encoding.UTF8.GetBytes(composed.Body));
                if (composed.ContentType is not null)
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue(composed.ContentType)
                    {
                        CharSet = "utf-8",
                    };
                }

                message.Content = content;
            }

            foreach (var (key, value) in composed.Headers)
            {
                if (
                    composed.IsMultipart
                    && (
                        key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    continue;
                }

                if (!message.Headers.TryAddWithoutValidation(key, value))
                {
                    message.Content?.Headers.TryAddWithoutValidation(key, value);
                }
            }

            if (
                !composed.Headers.Any(pair =>
                    pair.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                message.Headers.TryAddWithoutValidation("Accept", "*/*");
            }

            if (
                !composed.Headers.Any(pair =>
                    pair.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                message.Headers.TryAddWithoutValidation("User-Agent", "PostSharp/0.1");
            }

            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(effectiveTimeoutSeconds)
            );
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token
            );

            using var response = await _client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var responseHeaders = response
                .Headers.Concat(response.Content.Headers)
                .SelectMany(pair =>
                    pair.Value.Select(value => new ResponseHeaderView(pair.Key, value))
                )
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (mediaType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var sse = await ReadSseAsync(response, linked.Token, sseProgress)
                    .ConfigureAwait(false);
                stopwatch.Stop();
                var cancelled = sse.Cancelled;
                var reason = cancelled
                    ? (cancellationToken.IsCancellationRequested ? "已取消" : "超时")
                    : response.ReasonPhrase ?? string.Empty;
                return new HttpResult(
                    HasResponse: true,
                    StatusCode: (int)response.StatusCode,
                    ReasonPhrase: reason,
                    Body: sse.Raw,
                    IsJson: false,
                    MediaType: mediaType,
                    ElapsedMs: stopwatch.ElapsedMilliseconds,
                    SizeBytes: Encoding.UTF8.GetByteCount(sse.Raw),
                    Headers: responseHeaders,
                    Error: null,
                    IsSse: true,
                    SseThinking: sse.Thinking,
                    SseContent: sse.Content,
                    SseEventCount: sse.Count,
                    SseRaw: sse.Raw,
                    SseCancelled: cancelled
                );
            }

            var bytes = await response
                .Content.ReadAsByteArrayAsync(linked.Token)
                .ConfigureAwait(false);
            stopwatch.Stop();

            var text = Decode(bytes, response.Content.Headers.ContentType?.CharSet);
            var (formatted, isJson) = FormatDocument(text, mediaType);

            return new HttpResult(
                HasResponse: true,
                StatusCode: (int)response.StatusCode,
                ReasonPhrase: response.ReasonPhrase ?? string.Empty,
                Body: formatted,
                IsJson: isJson,
                MediaType: mediaType,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                SizeBytes: bytes.LongLength,
                Headers: responseHeaders,
                Error: null
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            Log.Debug("请求被取消 {Method} {Url}", composed.Method, composed.Url);
            return Failure("请求已取消。");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            Log.Warning(
                "请求超时 {Method} {Url} {TimeoutSeconds}",
                composed.Method,
                composed.Url,
                effectiveTimeoutSeconds
            );
            return Failure($"请求超时，{effectiveTimeoutSeconds} 秒内没有收到响应。");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log.Error(ex, "发送请求失败 {Method} {Url}", composed.Method, composed.Url);
            return Failure($"无法完成请求：{ex.Message}");
        }
    }

    public static HttpResult Failure(string message) =>
        new(
            HasResponse: false,
            StatusCode: 0,
            ReasonPhrase: string.Empty,
            Body: string.Empty,
            IsJson: false,
            MediaType: string.Empty,
            ElapsedMs: 0,
            SizeBytes: 0,
            Headers: Array.Empty<ResponseHeaderView>(),
            Error: message
        );

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        };

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static string GuessContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".txt" or ".log" or ".md" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                var encoding = Encoding.GetEncoding(charset.Trim('"', ' '));
                return encoding.GetString(bytes);
            }
            catch (Exception exception)
            {
                // Unknown charset: fall through to UTF-8.
                Log.Warning(exception, "响应字符集无法解码，回退到 UTF-8 {Charset}", charset);
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private (string Body, bool IsJson) FormatDocument(string text, string mediaType)
    {
        if (text.Length > MaxFormatLength)
        {
            return (text, false);
        }

        var trimmed = text.TrimStart();
        if (string.IsNullOrEmpty(trimmed))
        {
            return (text, false);
        }

        if (!AutoFormatResponse)
        {
            var isJson = false;
            if (trimmed[0] is '{' or '[')
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    isJson = true;
                }
                catch (JsonException exception)
                {
                    // Keep raw text.
                    Log.Debug(exception, "响应不是合法 JSON，保留原始文本 {MediaType}", mediaType);
                }
            }

            return (text, isJson);
        }

        if (trimmed[0] is '{' or '[')
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                return (JsonSerializer.Serialize(document.RootElement, PrettyJson), true);
            }
            catch (JsonException exception)
            {
                Log.Debug(exception, "响应 JSON 格式化失败，保留原始文本");
                return (text, false);
            }
        }

        if (
            trimmed[0] == '<'
            && (
                mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            try
            {
                return (XDocument.Parse(text).ToString(), false);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "响应 XML 格式化失败，保留原始文本 {MediaType}", mediaType);
                return (text, false);
            }
        }

        return (text, false);
    }

    private static async Task<SseReadResult> ReadSseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        IProgress<SseProgress>? progress = null
    )
    {
        var raw = new StringBuilder();
        var thinking = new StringBuilder();
        var content = new StringBuilder();
        var dataLines = new List<string>();
        var eventCount = 0;
        var cancelled = false;
        var lastReport = Stopwatch.StartNew();

        void Report()
        {
            progress?.Report(
                new SseProgress(raw.ToString(), thinking.ToString(), content.ToString(), eventCount)
            );
            lastReport.Restart();
        }

        try
        {
            await using var stream = await response
                .Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true
            );

            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                raw.Append(line).Append('\n');

                if (line.Length == 0)
                {
                    if (dataLines.Count > 0)
                    {
                        var payload = string.Join("\n", dataLines);
                        eventCount++;
                        AppendSsePayload(payload, thinking, content);
                        dataLines.Clear();
                        if (lastReport.ElapsedMilliseconds >= SseProgressIntervalMs)
                        {
                            Report();
                        }

                        if (payload.Trim() == "[DONE]")
                        {
                            break;
                        }
                    }

                    continue;
                }

                if (line[0] == ':')
                {
                    continue;
                }

                var separator = line.IndexOf(':');
                var field = separator < 0 ? line : line[..separator];
                var value = separator < 0 ? string.Empty : line[(separator + 1)..];
                if (value.StartsWith(' '))
                {
                    value = value[1..];
                }

                if (field == "data")
                {
                    dataLines.Add(value);
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        if (dataLines.Count > 0)
        {
            eventCount++;
            AppendSsePayload(string.Join("\n", dataLines), thinking, content);
        }

        Report();
        return new SseReadResult(
            raw.ToString(),
            thinking.ToString(),
            content.ToString(),
            eventCount,
            cancelled
        );
    }

    private static void AppendSsePayload(string data, StringBuilder thinking, StringBuilder content)
    {
        if (string.IsNullOrWhiteSpace(data) || data.Trim() == "[DONE]")
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            AppendSseElement(document.RootElement, thinking, content);
        }
        catch (JsonException exception)
        {
            // Some servers send plain text SSE events; keep them in the raw stream only.
            Log.Debug(exception, "SSE 事件不是 JSON，仅保留原始事件流");
        }
    }

    private static void AppendSseElement(
        JsonElement root,
        StringBuilder thinking,
        StringBuilder content
    )
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                AppendSseElement(item, thinking, content);
            }

            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // OpenAI-compatible streams.
        if (
            root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (TryGetObject(choice, "delta", out var delta))
                {
                    AppendThinking(delta, thinking);
                    AppendContent(delta, content);
                }

                if (TryGetObject(choice, "message", out var message))
                {
                    AppendThinking(message, thinking);
                    AppendContent(message, content);
                }

                AppendString(choice, "text", content);
            }

            return;
        }

        // Anthropic-compatible streams.
        if (TryGetObject(root, "delta", out var anthropicDelta))
        {
            var deltaType = GetString(anthropicDelta, "type") ?? string.Empty;
            if (deltaType.Contains("thinking", StringComparison.OrdinalIgnoreCase))
            {
                if (!AppendString(anthropicDelta, "thinking", thinking))
                {
                    AppendString(anthropicDelta, "text", thinking);
                }
            }
            else if (deltaType.Contains("text", StringComparison.OrdinalIgnoreCase))
            {
                AppendString(anthropicDelta, "text", content);
            }
            else
            {
                AppendThinking(anthropicDelta, thinking);
                AppendContent(anthropicDelta, content);
            }

            return;
        }

        if (TryGetObject(root, "content_block", out var contentBlock))
        {
            var blockType = GetString(contentBlock, "type") ?? string.Empty;
            if (blockType.Contains("thinking", StringComparison.OrdinalIgnoreCase))
            {
                AppendString(contentBlock, "thinking", thinking);
                AppendString(contentBlock, "text", thinking);
            }
            else
            {
                AppendString(contentBlock, "text", content);
            }

            return;
        }

        if (TryGetObject(root, "message", out var rootMessage))
        {
            AppendThinking(rootMessage, thinking);
            AppendContent(rootMessage, content);
            return;
        }

        // Gemini/other providers expose candidate content as parts.
        if (
            root.TryGetProperty("candidates", out var candidates)
            && candidates.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (
                    TryGetObject(candidate, "content", out var candidateContent)
                    && candidateContent.TryGetProperty("parts", out var parts)
                    && parts.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        if (
                            part.TryGetProperty("thought", out var thought)
                            && thought.ValueKind == JsonValueKind.True
                        )
                        {
                            AppendString(part, "text", thinking);
                        }
                        else
                        {
                            AppendString(part, "text", content);
                        }
                    }
                }
            }

            return;
        }

        AppendThinking(root, thinking);
        if (!AppendString(root, "content", content))
        {
            AppendString(root, "text", content);
        }

        AppendString(root, "response", content);
    }

    private static void AppendThinking(JsonElement element, StringBuilder target)
    {
        if (
            !AppendString(element, "reasoning_content", target)
            && !AppendString(element, "reasoning", target)
        )
        {
            AppendString(element, "thinking", target);
        }
    }

    private static void AppendContent(JsonElement element, StringBuilder target)
    {
        if (element.TryGetProperty("content", out var content))
        {
            AppendContentValue(content, target);
            return;
        }

        AppendString(element, "text", target);
    }

    private static void AppendContentValue(JsonElement value, StringBuilder target)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                target.Append(value.GetString());
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    AppendContentValue(item, target);
                }

                break;
            case JsonValueKind.Object:
                AppendString(value, "text", target);
                AppendString(value, "content", target);
                break;
        }
    }

    private static bool AppendString(JsonElement element, string property, StringBuilder target)
    {
        if (
            element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }

        var text = value.GetString();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        target.Append(text);
        return true;
    }

    private static bool TryGetObject(JsonElement element, string property, out JsonElement value)
    {
        if (
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out value)
            && value.ValueKind == JsonValueKind.Object
        )
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string property)
    {
        return
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private sealed record SseReadResult(
        string Raw,
        string Thinking,
        string Content,
        int Count,
        bool Cancelled
    );
}
