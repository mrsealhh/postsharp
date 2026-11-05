using System.Net;
using System.Text;

namespace PostSharp.Services;

/// <summary>流水线执行器需要的最小请求模型；不依赖桌面端的请求编辑器。</summary>
public sealed record PipelineHttpRequest(
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? Body,
    string? ContentType,
    bool FollowRedirects
);

/// <summary>流水线执行器需要的最小响应模型。</summary>
public sealed record PipelineHttpResponse(
    bool HasResponse,
    int StatusCode,
    string Body,
    string? Error
);

/// <summary>把流水线的 HTTP 发送与执行器解耦：桌面端可复用 <c>HttpRequestService</c>，CLI 用纯 HttpClient。</summary>
public interface IPipelineHttpClient
{
    Task<PipelineHttpResponse> SendAsync(
        PipelineHttpRequest request,
        int timeoutSeconds,
        CancellationToken cancellationToken
    );
}

/// <summary>纯 BCL 的流水线 HTTP 客户端：没有 Avalonia、没有 UI 线程，适合 CLI 与 CI。</summary>
public sealed class HttpPipelineClient : IPipelineHttpClient, IDisposable
{
    private readonly HttpClient _follow;
    private readonly HttpClient _noFollow;

    public HttpPipelineClient()
    {
        _follow = CreateClient(allowAutoRedirect: true);
        _noFollow = CreateClient(allowAutoRedirect: false);
    }

    /// <summary>测试入口：用一个可替换的 handler 发送请求。</summary>
    public HttpPipelineClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _follow = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _noFollow = _follow;
    }

    public Task<PipelineHttpResponse> SendAsync(
        PipelineHttpRequest request,
        int timeoutSeconds,
        CancellationToken cancellationToken
    ) =>
        SendAsync(
            request.FollowRedirects ? _follow : _noFollow,
            request,
            timeoutSeconds,
            cancellationToken
        );

    public void Dispose()
    {
        _follow.Dispose();
        _noFollow.Dispose();
    }

    private static async Task<PipelineHttpResponse> SendAsync(
        HttpClient client,
        PipelineHttpRequest request,
        int timeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)));

        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
            if (!string.IsNullOrEmpty(request.Body))
            {
                message.Content = new StringContent(
                    request.Body,
                    Encoding.UTF8,
                    string.IsNullOrWhiteSpace(request.ContentType)
                        ? "text/plain"
                        : request.ContentType
                );
            }

            foreach (var header in request.Headers)
            {
                if (
                    header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                )
                {
                    continue;
                }

                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await client
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
            var body = await response
                .Content.ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);
            return new PipelineHttpResponse(true, (int)response.StatusCode, body, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PipelineHttpResponse(false, 0, string.Empty, "请求超时。");
        }
        catch (HttpRequestException exception)
        {
            return new PipelineHttpResponse(false, 0, string.Empty, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return new PipelineHttpResponse(false, 0, string.Empty, exception.Message);
        }
    }

    private static HttpClient CreateClient(bool allowAutoRedirect)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            AutomaticDecompression =
                DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli,
        };
        return new HttpClient(handler)
        {
            // 每个步骤自己带超时；HttpClient 的全局超时只会把错误变得难以解释。
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
