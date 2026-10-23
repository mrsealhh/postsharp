using System.Text.Json.Serialization;
using TKCode.Arc;

namespace PostSharp.Models;

/// <summary>One request that was executed, whether from the UI or from an AI agent through MCP.</summary>
public sealed class AuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    /// <summary>UI or MCP.</summary>
    public string Source { get; set; } = "UI";

    public string? Tool { get; set; }

    public string? Client { get; set; }

    public Guid? RequestId { get; set; }

    public string RequestName { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    public string? Environment { get; set; }

    public int StatusCode { get; set; }

    public string StatusText { get; set; } = string.Empty;

    public long ElapsedMs { get; set; }

    public long SizeBytes { get; set; }

    public bool IsSuccess { get; set; }

    public string? Error { get; set; }

    public string? Curl { get; set; }

    public string RequestHeaders { get; set; } = string.Empty;

    public string RequestBodyPreview { get; set; } = string.Empty;

    public string ResponseHeaders { get; set; } = string.Empty;

    public string ResponseBodyPreview { get; set; } = string.Empty;

    public string ResponseContentType { get; set; } = string.Empty;

    public bool IsSse { get; set; }

    public int SseEventCount { get; set; }

    public string AiThinking { get; set; } = string.Empty;

    public string AiContent { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasAiOutput => IsSse && (AiContent.Length > 0 || AiThinking.Length > 0);

    [JsonIgnore]
    public string AiSummary => $"SSE · {SseEventCount} 个事件 · 内容 {AiContent.Length} 字 · 思考 {AiThinking.Length} 字";

    [JsonIgnore]
    public string TimeText => Timestamp.LocalDateTime.ToString("MM-dd HH:mm:ss");

    [JsonIgnore]
    public string SourceLabel => Source == "MCP" ? "AI · MCP" : "UI";

    [JsonIgnore]
    public string StatusLabel => StatusCode > 0
        ? (string.IsNullOrWhiteSpace(StatusText) ? StatusCode.ToString() : $"{StatusCode} {StatusText}")
        : "失败";

    [JsonIgnore]
    public string StatusShortLabel => StatusCode > 0 ? StatusCode.ToString() : "失败";

    [JsonIgnore]
    public string RequestLine => $"{Method}  {Url}";

    [JsonIgnore]
    public string TimeTooltip => Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string DurationText => $"{ElapsedMs} ms";

    [JsonIgnore]
    public string SizeText => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024d:0.#} KB",
        _ => $"{SizeBytes / (1024d * 1024d):0.##} MB"
    };

    [JsonIgnore]
    public string ClientLabel => string.IsNullOrWhiteSpace(Client) ? "—" : Client;

    [JsonIgnore]
    public string ToolLabel => string.IsNullOrWhiteSpace(Tool) ? "—" : Tool;

    [JsonIgnore]
    public BadgeTone SourceTone => Source == "MCP" ? BadgeTone.Info : BadgeTone.Neutral;

    [JsonIgnore]
    public BadgeTone StatusTone => StatusCode switch
    {
        >= 200 and < 300 => BadgeTone.Success,
        >= 300 and < 400 => BadgeTone.Info,
        >= 400 and < 500 => BadgeTone.Warning,
        _ => BadgeTone.Danger
    };
}
