using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PostSharp.Models;

public partial class RequestCollection : ObservableObject
{
    [ObservableProperty]
    private string _name = "新建集合";

    public Guid Id { get; init; } = Guid.NewGuid();

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

    /// <summary>None, Json or Text.</summary>
    [ObservableProperty]
    private string _bodyMode = "None";

    [ObservableProperty]
    private string _body = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();

    public EditableRowCollection Params { get; } = new();

    public EditableRowCollection Headers { get; } = new();

    public RequestCollection? Owner { get; set; }

    public RequestItem Clone()
    {
        var clone = new RequestItem
        {
            Name = Name,
            Method = Method,
            Url = Url,
            BodyMode = BodyMode,
            Body = Body
        };

        foreach (var row in Params.Where(r => !string.IsNullOrWhiteSpace(r.Key) || !string.IsNullOrWhiteSpace(r.Value)))
        {
            clone.Params.AddRow(row.Key, row.Value, row.IsEnabled);
        }

        foreach (var row in Headers.Where(r => !string.IsNullOrWhiteSpace(r.Key) || !string.IsNullOrWhiteSpace(r.Value)))
        {
            clone.Headers.AddRow(row.Key, row.Value, row.IsEnabled);
        }

        clone.Params.EnsureTrailingRow();
        clone.Headers.EnsureTrailingRow();
        return clone;
    }
}
