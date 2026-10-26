using PostSharp.Models;
using PostSharp.Services;
using TKCode.Arc;

namespace PostSharp.ViewModels;

/// <summary>
/// 一次界面发送的会话：取消令牌、可续接 SSE 的组合请求以及该请求自己的上传与响应视图。
/// </summary>
internal sealed class RequestSendSession
{
    public CancellationTokenSource? Cts { get; set; }

    public ComposedRequest? LastComposedRequest { get; set; }

    public SseResumeSnapshot? SseResume { get; set; }

    public bool IsUploading { get; set; }

    public double UploadProgressValue { get; set; }

    public string UploadProgressText { get; set; } = string.Empty;

    public ResponseViewState Response { get; set; } = new();
}

/// <summary>当前显示在响应区的全部状态；切换请求时按这份快照保存与恢复。</summary>
internal sealed class ResponseViewState
{
    public bool HasResponse { get; set; }

    public bool HasResponseError { get; set; }

    public string? ResponseError { get; set; }

    public bool IsJsonResponse { get; set; }

    public bool IsResponseJsonView { get; set; } = true;

    public bool IsLargeResponse { get; set; }

    public bool IsSseResponse { get; set; }

    public string SseThinking { get; set; } = string.Empty;

    public string SseContent { get; set; } = string.Empty;

    public string SseRaw { get; set; } = string.Empty;

    public int SseEventCount { get; set; }

    public string SseLastEventId { get; set; } = string.Empty;

    public bool SseCanResume { get; set; }

    public string SseViewMode { get; set; } = "内容";

    public int ResponseTabIndex { get; set; }

    public string ResponseBody { get; set; } = string.Empty;

    public string FullResponseBody { get; set; } = string.Empty;

    public string ResponseClipboardText { get; set; } = string.Empty;

    public string LargeResponseNotice { get; set; } = string.Empty;

    public string ResponseStatusText { get; set; } = string.Empty;

    public string ResponseTimeText { get; set; } = string.Empty;

    public string ResponseSizeText { get; set; } = string.Empty;

    public string ResponseContentType { get; set; } = string.Empty;

    public BadgeTone ResponseTone { get; set; } = BadgeTone.Neutral;

    public IReadOnlyList<ResponseHeaderView> ResponseHeaders { get; set; } =
        Array.Empty<ResponseHeaderView>();
}

internal sealed record SseResumeSnapshot(string Raw, string Thinking, string Content, int Count);

/// <summary>
/// 每个请求各自保留发送会话与最近一次响应。请求被删除、永久移出工作区或退出应用时调用方负责释放，
/// 避免响应体和取消令牌在字典里无限积累。
/// </summary>
internal sealed class RequestSessionStore
{
    private readonly Dictionary<RequestItem, RequestSendSession> _sessions = new(
        ReferenceEqualityComparer.Instance
    );

    public int Count => _sessions.Count;

    public RequestSendSession Get(RequestItem request)
    {
        if (!_sessions.TryGetValue(request, out var session))
        {
            session = new RequestSendSession();
            _sessions[request] = session;
        }

        return session;
    }

    public RequestSendSession? Find(RequestItem request) =>
        _sessions.TryGetValue(request, out var session) ? session : null;

    public RequestSendSession? Remove(RequestItem request) =>
        _sessions.Remove(request, out var session) ? session : null;

    public bool IsTracked(RequestItem request, RequestSendSession session) =>
        _sessions.TryGetValue(request, out var tracked) && ReferenceEquals(tracked, session);

    public IReadOnlyList<RequestItem> SnapshotRequests() => _sessions.Keys.ToList();

    public void CancelAll()
    {
        foreach (var session in _sessions.Values)
        {
            session.Cts?.Cancel();
        }
    }

    public void Clear() => _sessions.Clear();
}
