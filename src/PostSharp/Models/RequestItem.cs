using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PostSharp.Models;

public partial class RequestCollection : ObservableObject
{
    [ObservableProperty]
    private string _name = "新建集合";

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isFavorite;

    public Guid Id { get; init; } = Guid.NewGuid();

    public RequestCollection? Parent { get; set; }

    public int Depth
    {
        get
        {
            var depth = 1;
            for (var parent = Parent; parent is not null; parent = parent.Parent)
            {
                depth++;
            }

            return depth;
        }
    }

    public ObservableCollection<RequestCollection> Children { get; } = new();

    public ObservableCollection<RequestItem> Requests { get; } = new();

    /// <summary>Requests in this collection and all of its sub-collections.</summary>
    public int TotalRequestCount => Requests.Count + Children.Sum(child => child.TotalRequestCount);

    /// <summary>集合级授权；子集合与请求默认继承它。</summary>
    public RequestAuth Auth { get; } = new();

    /// <summary>OpenAPI 来源；只有从 URL 导入的根集合才有。</summary>
    public OpenApiSource? OpenApiSource { get; set; }

    [JsonIgnore]
    public bool HasOpenApiSource => OpenApiSource is not null;
}

public partial class RequestItem : ObservableObject
{
    private bool _syncingUploadFields;

    public RequestItem()
    {
        UploadFiles.CollectionChanged += (_, _) =>
        {
            SyncLegacyUploadFields();
            OnPropertyChanged(nameof(HasUploadFile));
        };
    }

    [ObservableProperty]
    private string _name = "新建请求";

    /// <summary>HTTP, WebSocket, SocketIO or TCP.</summary>
    [ObservableProperty]
    private string _protocol = RequestProtocol.Http;

    [ObservableProperty]
    private string _method = "GET";

    [ObservableProperty]
    private string _url = string.Empty;

    /// <summary>0 means "use the global timeout from settings"; a positive value overrides it for this request.</summary>
    [ObservableProperty]
    private int _timeoutSeconds;

    /// <summary>When false, a 3xx response is returned as-is instead of being followed automatically.</summary>
    [ObservableProperty]
    private bool _followRedirects = true;

    /// <summary>None, Json or Text.</summary>
    [ObservableProperty]
    private string _bodyMode = "None";

    [ObservableProperty]
    private string _body = string.Empty;

    [ObservableProperty]
    private string _graphQlQuery = string.Empty;

    [ObservableProperty]
    private string _graphQlVariables = "{}";

    [ObservableProperty]
    private string _graphQlOperationName = string.Empty;

    /// <summary>Payload sent once the WebSocket, Socket.IO or TCP connection is open.</summary>
    [ObservableProperty]
    private string _messageBody = string.Empty;

    /// <summary>Text or Hex; Hex sends the payload as raw bytes (WebSocket binary frame for WebSocket).</summary>
    [ObservableProperty]
    private string _messageFormat = "Text";

    /// <summary>How long to keep collecting frames after the payload is sent, in milliseconds.</summary>
    [ObservableProperty]
    private int _waitMilliseconds = 3000;

    [ObservableProperty]
    private string _socketIoEvent = "message";

    [ObservableProperty]
    private string _socketIoNamespace = "/";

    [ObservableProperty]
    private string _socketIoPath = "/socket.io";

    /// <summary>None, LF or CRLF; appended to text payloads sent over TCP.</summary>
    [ObservableProperty]
    private string _tcpTerminator = "None";

    /// <summary>Wraps the TCP connection in TLS; a tls:// URL implies it as well.</summary>
    [ObservableProperty]
    private bool _tcpUseTls;

    /// <summary>SOAP 1.1 or 1.2.</summary>
    [ObservableProperty]
    private string _soapVersion = "1.1";

    /// <summary>SOAPAction header for SOAP 1.1; SOAP 1.2 puts it in Content-Type.</summary>
    [ObservableProperty]
    private string _soapAction = string.Empty;

    /// <summary>Fully qualified gRPC service name, for example helloworld.Greeter.</summary>
    [ObservableProperty]
    private string _grpcService = string.Empty;

    /// <summary>Unary method name, for example SayHello.</summary>
    [ObservableProperty]
    private string _grpcMethod = string.Empty;

    /// <summary>Use TLS when the gRPC URL has no scheme.</summary>
    [ObservableProperty]
    private bool _grpcUseTls = true;

    /// <summary>0 uses the request timeout.</summary>
    [ObservableProperty]
    private int _grpcDeadlineSeconds;

    [ObservableProperty]
    private string _uploadFilePath = string.Empty;

    [ObservableProperty]
    private string _uploadFieldName = "file";

    public ObservableCollection<UploadFileItem> UploadFiles { get; } = new();

    public bool HasUploadFile => UploadFiles.Count > 0;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isSending;

    public Guid Id { get; set; } = Guid.NewGuid();

    public EditableRowCollection Params { get; } = new();

    public EditableRowCollection Headers { get; } = new();

    public EditableRowCollection FormFields { get; } = new();

    /// <summary>请求级授权，默认继承所属集合。</summary>
    public RequestAuth Auth { get; set; } = new();

    public RequestCollection? Owner { get; set; }

    /// <summary>OpenAPI 操作标识（METHOD + 路径模板），用于重新同步时匹配。</summary>
    public string OpenApiKey { get; set; } = string.Empty;

    /// <summary>导入时的接口文档快照，不参与发送。</summary>
    public OpenApiOperationDoc? OpenApiDoc { get; set; }

    [JsonIgnore]
    public bool HasOpenApiDoc => OpenApiDoc is not null;

    partial void OnProtocolChanged(string value)
    {
        if (
            string.Equals(
                RequestProtocol.Normalize(value),
                RequestProtocol.Soap,
                StringComparison.Ordinal
            ) && string.Equals(Method, "GET", StringComparison.OrdinalIgnoreCase)
        )
        {
            Method = "POST";
        }
    }

    partial void OnUploadFilePathChanged(string value)
    {
        if (_syncingUploadFields || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (
            UploadFiles.Any(file =>
                string.Equals(file.FilePath, value, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return;
        }

        UploadFiles.Add(
            new UploadFileItem
            {
                FilePath = value,
                FieldName = string.IsNullOrWhiteSpace(UploadFieldName) ? "file" : UploadFieldName,
            }
        );
    }

    partial void OnUploadFieldNameChanged(string value)
    {
        if (_syncingUploadFields || UploadFiles.FirstOrDefault() is not { } first)
        {
            return;
        }

        first.FieldName = string.IsNullOrWhiteSpace(value) ? "file" : value;
    }

    private void SyncLegacyUploadFields()
    {
        var first = UploadFiles.FirstOrDefault();
        _syncingUploadFields = true;
        UploadFilePath = first?.FilePath ?? string.Empty;
        UploadFieldName = string.IsNullOrWhiteSpace(first?.FieldName) ? "file" : first.FieldName;
        _syncingUploadFields = false;
    }

    /// <summary>
    /// 复制请求。<paramref name="copyAuthorization" /> 为 false 时与原请求共用同一份授权配置，
    /// 供 MCP、套件这类「只读快照」使用；变成新请求时则复制一份，避免两处互相影响。
    /// </summary>
    public RequestItem Clone(bool copyAuthorization = true)
    {
        var clone = new RequestItem
        {
            Name = Name,
            Protocol = Protocol,
            Method = Method,
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
            Owner = Owner,
            Auth = copyAuthorization ? Auth.Clone() : Auth,
        };

        foreach (var file in UploadFiles)
        {
            clone.UploadFiles.Add(
                new UploadFileItem
                {
                    FilePath = file.FilePath,
                    FieldName = file.FieldName,
                    SizeBytes = file.SizeBytes,
                }
            );
        }

        foreach (
            var row in Params.Where(r =>
                !string.IsNullOrWhiteSpace(r.Key) || !string.IsNullOrWhiteSpace(r.Value)
            )
        )
        {
            clone.Params.AddRow(row.Key, row.Value, row.IsEnabled);
        }

        foreach (
            var row in Headers.Where(r =>
                !string.IsNullOrWhiteSpace(r.Key) || !string.IsNullOrWhiteSpace(r.Value)
            )
        )
        {
            clone.Headers.AddRow(row.Key, row.Value, row.IsEnabled);
        }

        foreach (
            var row in FormFields.Where(r =>
                !string.IsNullOrWhiteSpace(r.Key) || !string.IsNullOrWhiteSpace(r.Value)
            )
        )
        {
            clone.FormFields.AddRow(row.Key, row.Value, row.IsEnabled);
        }

        clone.Params.EnsureTrailingRow();
        clone.Headers.EnsureTrailingRow();
        clone.FormFields.EnsureTrailingRow();
        return clone;
    }
}
