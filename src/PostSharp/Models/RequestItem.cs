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

    public RequestCollection? Owner { get; set; }

    public RequestItem Clone()
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
