using System.Collections.ObjectModel;
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

    /// <summary>集合级授权；子集合与请求默认继承它。</summary>
    public RequestAuth Auth { get; } = new();
}

public partial class RequestItem : ObservableObject
{
    [ObservableProperty]
    private string _name = "新建请求";

    [ObservableProperty]
    private string _method = "GET";

    [ObservableProperty]
    private string _url = string.Empty;

    /// <summary>0 means "use the global timeout from settings"; a positive value overrides it for this request.</summary>
    [ObservableProperty]
    private int _timeoutSeconds;

    /// <summary>None, Json or Text.</summary>
    [ObservableProperty]
    private string _bodyMode = "None";

    [ObservableProperty]
    private string _body = string.Empty;

    [ObservableProperty]
    private string _uploadFilePath = string.Empty;

    [ObservableProperty]
    private string _uploadFieldName = "file";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isFavorite;

    public Guid Id { get; set; } = Guid.NewGuid();

    public EditableRowCollection Params { get; } = new();

    public EditableRowCollection Headers { get; } = new();

    public EditableRowCollection FormFields { get; } = new();

    /// <summary>请求级授权，默认继承所属集合。</summary>
    public RequestAuth Auth { get; set; } = new();

    public RequestCollection? Owner { get; set; }

    /// <summary>
    /// 复制请求。<paramref name="copyAuthorization" /> 为 false 时与原请求共用同一份授权配置，
    /// 供 MCP、套件这类「只读快照」使用；变成新请求时则复制一份，避免两处互相影响。
    /// </summary>
    public RequestItem Clone(bool copyAuthorization = true)
    {
        var clone = new RequestItem
        {
            Name = Name,
            Method = Method,
            Url = Url,
            TimeoutSeconds = TimeoutSeconds,
            BodyMode = BodyMode,
            Body = Body,
            UploadFilePath = UploadFilePath,
            UploadFieldName = UploadFieldName,
            Owner = Owner,
            Auth = copyAuthorization ? Auth.Clone() : Auth,
        };

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
