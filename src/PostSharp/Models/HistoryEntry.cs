using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;

namespace PostSharp.Models;

public sealed partial class HistoryEntry : ObservableObject
{
    /// <summary>Request body snapshots are capped so 40 large requests cannot pin hundreds of megabytes.</summary>
    public const int BodyLimit = 256 * 1024;

    public static string LimitBody(string body) =>
        body.Length <= BodyLimit ? body : body[..BodyLimit] + "\n…（内容已截断）";

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string Name { get; init; } = string.Empty;

    public string Protocol { get; init; } = RequestProtocol.Http;

    public string Method { get; init; } = "GET";

    public string Url { get; init; } = string.Empty;

    public int TimeoutSeconds { get; init; }

    public bool FollowRedirects { get; init; } = true;

    public string BodyMode { get; init; } = "None";

    public string Body { get; init; } = string.Empty;

    public string GraphQlQuery { get; init; } = string.Empty;

    public string GraphQlVariables { get; init; } = "{}";

    public string GraphQlOperationName { get; init; } = string.Empty;

    public string MessageBody { get; init; } = string.Empty;

    public string MessageFormat { get; init; } = "Text";

    public int WaitMilliseconds { get; init; } = 3000;

    public string SocketIoEvent { get; init; } = "message";

    public string SocketIoNamespace { get; init; } = "/";

    public string SocketIoPath { get; init; } = "/socket.io";

    public string TcpTerminator { get; init; } = "None";

    public bool TcpUseTls { get; init; }

    public string SoapVersion { get; init; } = "1.1";

    public string SoapAction { get; init; } = string.Empty;

    public string GrpcService { get; init; } = string.Empty;

    public string GrpcMethod { get; init; } = string.Empty;

    public bool GrpcUseTls { get; init; } = true;

    public int GrpcDeadlineSeconds { get; init; }

    public List<RowSnapshot> Params { get; init; } = new();

    public List<RowSnapshot> Headers { get; init; } = new();

    public int StatusCode { get; init; }

    public long DurationMs { get; init; }

    public IRelayCommand? OpenCommand { get; set; }

    public IRelayCommand? DeleteCommand { get; set; }

    public IRelayCommand? RestoreCommand { get; set; }

    public IRelayCommand? PurgeCommand { get; set; }

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isSelected;

    public string DisplayTime => Timestamp.LocalDateTime.ToString("HH:mm:ss");

    public string TimestampText => Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    public string StatusLabel => StatusCode > 0 ? StatusCode.ToString() : "失败";

    public BadgeTone StatusTone =>
        StatusCode switch
        {
            >= 100 and < 300 => BadgeTone.Success,
            >= 300 and < 400 => BadgeTone.Info,
            >= 400 and < 500 => BadgeTone.Warning,
            _ => BadgeTone.Danger,
        };

    public object StatusIcon =>
        new ArcGlyph
        {
            Data = StatusCode switch
            {
                >= 100 and < 300 => ArcIcons.CircleCheck,
                >= 300 and < 400 => ArcIcons.Info,
                >= 400 and < 500 => ArcIcons.TriangleAlert,
                _ => ArcIcons.CircleX,
            },
            Size = 12,
        };

    public BadgeTone MethodTone => MethodTones.Of(MethodDisplay);

    public string MethodDisplay => string.IsNullOrWhiteSpace(Method) ? "GET" : Method;

    public RequestItem ToRequest()
    {
        var protocol = RequestProtocol.Normalize(Protocol);
        var method =
            protocol == RequestProtocol.Soap
            && string.Equals(Method, "GET", StringComparison.OrdinalIgnoreCase)
                ? "POST"
                : Method;
        var request = new RequestItem
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "历史请求" : $"{Name}（历史）",
            Protocol = protocol,
            Method = method,
            Url = Url,
            TimeoutSeconds = TimeoutSeconds,
            FollowRedirects = FollowRedirects,
            BodyMode = BodyMode,
            Body = Body,
            GraphQlQuery = GraphQlQuery,
            GraphQlVariables = GraphQlVariables,
            GraphQlOperationName = GraphQlOperationName,
            MessageBody = MessageBody,
            MessageFormat = MessageFormat,
            WaitMilliseconds = WaitMilliseconds,
            SocketIoEvent = SocketIoEvent,
            SocketIoNamespace = SocketIoNamespace,
            SocketIoPath = SocketIoPath,
            TcpTerminator = TcpTerminator,
            TcpUseTls = TcpUseTls,
            SoapVersion = SoapVersion,
            SoapAction = SoapAction,
            GrpcService = GrpcService,
            GrpcMethod = GrpcMethod,
            GrpcUseTls = GrpcUseTls,
            GrpcDeadlineSeconds = GrpcDeadlineSeconds,
        };

        foreach (var row in Params)
        {
            request.Params.AddRow(row.Key, row.Value, row.IsEnabled);
        }

        foreach (var row in Headers)
        {
            request.Headers.AddRow(row.Key, row.Value, row.IsEnabled);
        }

        request.Params.EnsureTrailingRow();
        request.Headers.EnsureTrailingRow();
        return request;
    }
}

public sealed record RowSnapshot(string Key, string Value, bool IsEnabled);

public sealed record ResponseHeaderView(string Key, string Value);
