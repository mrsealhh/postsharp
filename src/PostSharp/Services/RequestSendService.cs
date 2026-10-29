using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Dispatches HTTP requests to <see cref="HttpRequestService" /> and other protocols to their senders.</summary>
public sealed class RequestSendService
{
    private readonly HttpRequestService _http;

    public RequestSendService(HttpRequestService? http = null) => _http = http ?? new();

    public int TimeoutSeconds
    {
        get => _http.TimeoutSeconds;
        set => _http.TimeoutSeconds = value;
    }

    public bool AutoFormatResponse
    {
        get => _http.AutoFormatResponse;
        set => _http.AutoFormatResponse = value;
    }

    public string SseThinkingPath
    {
        get => _http.SseThinkingPath;
        set => _http.SseThinkingPath = value;
    }

    public string SseContentPath
    {
        get => _http.SseContentPath;
        set => _http.SseContentPath = value;
    }

    public async Task<HttpResult> SendAsync(
        ComposedRequest composed,
        string logUrl,
        CancellationToken cancellationToken,
        int timeoutSeconds = 0,
        IProgress<SseProgress>? sseProgress = null,
        IProgress<UploadProgress>? uploadProgress = null,
        string? lastEventId = null
    )
    {
        if (
            composed.ProtocolOptions is { } options
            && !string.Equals(
                RequestProtocol.Normalize(options.Protocol),
                RequestProtocol.Http,
                StringComparison.Ordinal
            )
        )
        {
            return await ProtocolRequestService
                .SendAsync(composed, options, cancellationToken, timeoutSeconds)
                .ConfigureAwait(false);
        }

        return await _http
            .SendAsync(
                composed,
                logUrl,
                cancellationToken,
                timeoutSeconds,
                sseProgress,
                uploadProgress,
                lastEventId
            )
            .ConfigureAwait(false);
    }
}
