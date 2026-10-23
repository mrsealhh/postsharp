using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PostSharp.Models;

namespace PostSharp.Services;

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
    string? Error);

public sealed class HttpRequestService
{
    private static readonly HttpClient Client = CreateClient();

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public async Task<HttpResult> SendAsync(
        RequestItem request,
        EnvironmentModel? environment,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var url = BuildUrl(request, environment);
            using var message = new HttpRequestMessage(new HttpMethod(request.Method.ToUpperInvariant()), url);

            var headers = CollectHeaders(request, environment);
            var body = VariableResolver.Resolve(request.Body, environment);
            var hasUserContentType = headers.Any(pair =>
                pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));

            HttpContent? content = null;
            if (!string.Equals(request.BodyMode, "None", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(body))
            {
                content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
                if (!hasUserContentType)
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue(
                        string.Equals(request.BodyMode, "Json", StringComparison.OrdinalIgnoreCase)
                            ? "application/json"
                            : "text/plain")
                    {
                        CharSet = "utf-8"
                    };
                }

                message.Content = content;
            }

            foreach (var (key, value) in headers)
            {
                if (!message.Headers.TryAddWithoutValidation(key, value))
                {
                    message.Content?.Headers.TryAddWithoutValidation(key, value);
                }
            }

            if (!headers.Any(pair => pair.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)))
            {
                message.Headers.TryAddWithoutValidation("Accept", "*/*");
            }

            if (!headers.Any(pair => pair.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)))
            {
                message.Headers.TryAddWithoutValidation("User-Agent", "PostSharp/0.1");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            using var response = await Client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token).ConfigureAwait(false);

            var bytes = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            stopwatch.Stop();

            var text = Decode(bytes, response.Content.Headers.ContentType?.CharSet);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var isJson = TryFormatJson(text, out var pretty);

            var responseHeaders = response.Headers
                .Concat(response.Content.Headers)
                .SelectMany(pair => pair.Value.Select(value => new ResponseHeaderView(pair.Key, value)))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new HttpResult(
                HasResponse: true,
                StatusCode: (int)response.StatusCode,
                ReasonPhrase: response.ReasonPhrase ?? string.Empty,
                Body: isJson ? pretty : text,
                IsJson: isJson,
                MediaType: mediaType,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                SizeBytes: bytes.LongLength,
                Headers: responseHeaders,
                Error: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Failure("请求已取消。");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return Failure("请求超时，60 秒内没有收到响应。");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure($"无法完成请求：{ex.Message}");
        }

        static HttpResult Failure(string message) => new(
            HasResponse: false,
            StatusCode: 0,
            ReasonPhrase: string.Empty,
            Body: string.Empty,
            IsJson: false,
            MediaType: string.Empty,
            ElapsedMs: 0,
            SizeBytes: 0,
            Headers: Array.Empty<ResponseHeaderView>(),
            Error: message);
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static string BuildUrl(RequestItem request, EnvironmentModel? environment)
    {
        var raw = VariableResolver.Resolve(request.Url, environment).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("请输入请求地址。");
        }

        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            raw = "http://" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("请求地址无效，请使用 http:// 或 https:// 开头的完整地址。");
        }

        var query = uri.Query.TrimStart('?');
        var additions = request.Params.ActiveRows
            .Select(row => $"{Uri.EscapeDataString(row.Key.Trim())}={Uri.EscapeDataString(VariableResolver.Resolve(row.Value, environment))}")
            .ToList();

        if (additions.Count == 0)
        {
            return uri.ToString();
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.IsNullOrEmpty(query)
                ? string.Join("&", additions)
                : query + "&" + string.Join("&", additions)
        };

        return builder.Uri.ToString();
    }

    private static List<KeyValuePair<string, string>> CollectHeaders(
        RequestItem request,
        EnvironmentModel? environment)
    {
        return request.Headers.ActiveRows
            .Select(row => new KeyValuePair<string, string>(
                row.Key.Trim(),
                VariableResolver.Resolve(row.Value, environment)))
            .ToList();
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
            catch
            {
                // Unknown charset: fall through to UTF-8.
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool TryFormatJson(string text, out string pretty)
    {
        pretty = text;
        var trimmed = text.TrimStart();
        if (string.IsNullOrEmpty(trimmed) || (trimmed[0] is not ('{' or '[')))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            pretty = JsonSerializer.Serialize(document.RootElement, PrettyJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
